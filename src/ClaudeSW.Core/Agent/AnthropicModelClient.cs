using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;
using Block = ClaudeSW.Core.Model.Block;
using SdkBlock = Anthropic.Models.Beta.Messages.Block;

namespace ClaudeSW.Core.Agent
{
    /// <summary>
    /// <see cref="IModelClient"/> on the official Anthropic C# SDK (beta Messages endpoint, streaming).
    /// Converts our provider-neutral <see cref="ChatMessage"/>s to SDK params and back.
    /// Enables the server-side refusal fallback ("fallbacks": "default") on models that support it.
    /// </summary>
    public sealed class AnthropicModelClient : IModelClient, ICredentialValidator
    {
        public const string FallbackBeta = "server-side-fallback-2026-07-01";
        public const string BaseUrl = "https://api.anthropic.com";
        private readonly HttpClient _http;

        /// <param name="http">Optional HttpClient (tests inject a fake handler).</param>
        public AnthropicModelClient(HttpClient http = null) { _http = http; EnsureModernTls(); }

        /// <summary>
        /// Host applications (SOLIDWORKS included) may pin ServicePointManager to legacy protocols
        /// for the whole process; api.anthropic.com requires TLS 1.2+. Add 1.2/1.3 without removing
        /// anything the host relies on.
        /// </summary>
        public static void EnsureModernTls()
        {
            try
            {
                var p = System.Net.ServicePointManager.SecurityProtocol;
                if (p != System.Net.SecurityProtocolType.SystemDefault)
                    System.Net.ServicePointManager.SecurityProtocol = p | System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288 /* Tls13 */;
            }
            catch (NotSupportedException)
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
        }

        /// <summary>Optional sink for diagnostic details of failures (never includes secrets).</summary>
        public static Action<string> Diagnostics { get; set; }

        private static string Describe(Exception e)
        {
            var sb = new StringBuilder();
            for (var x = e; x != null; x = x.InnerException)
                sb.Append(x.GetType().Name).Append(": ").Append(x.Message).Append(" | ");
            return sb.ToString();
        }

        private AnthropicClient CreateClient(AuthCredentials creds)
        {
            if (creds == null || string.IsNullOrEmpty(creds.ApiKey))
                throw new ModelApiException(ModelErrorKind.Auth, "You're not signed in. Add your Anthropic API key in Settings.");
            // Everything explicit: never let environment variables (ANTHROPIC_BASE_URL, ANTHROPIC_AUTH_TOKEN)
            // or an on-disk CLI profile on the user's machine override the key they signed in with.
            var options = new Anthropic.Core.ClientOptions
            {
                ApiKey = creds.ApiKey,
                AuthToken = null,
                BaseUrl = BaseUrl,
                MaxRetries = 2,
            };
            if (_http != null) options = options with { HttpClient = _http };
            return new AnthropicClient(options);
        }

        // ------------------------------------------------------------------ request building

        internal static MessageCreateParams BuildParams(ModelRequest req)
        {
            var useFallback = KnownModels.SupportsServerFallback(req.Model);
            var p = new MessageCreateParams
            {
                Model = req.Model,
                MaxTokens = req.MaxTokens,
                System = new List<BetaTextBlockParam>
                {
                    new BetaTextBlockParam { Text = req.SystemPrompt ?? "", CacheControl = new BetaCacheControlEphemeral() },
                },
                Messages = ToParams(req.Messages),
                Tools = req.Tools.Select(ToTool).ToList(),
                // Automatic caching of the conversation prefix (the system breakpoint covers tools + system).
                CacheControl = new BetaCacheControlEphemeral(),
            };
            if (KnownModels.SupportsEffort(req.Model))
            {
                p = p with
                {
                    Thinking = new BetaThinkingConfigAdaptive { Display = req.ExtendedThinking ? Display.Summarized : Display.Omitted },
                    OutputConfig = new BetaOutputConfig { Effort = req.ExtendedThinking ? Effort.High : Effort.Medium },
                };
            }
            if (useFallback)
            {
                p = p with { Betas = new List<Anthropic.Core.ApiEnum<string, Anthropic.Models.Beta.AnthropicBeta>> { FallbackBeta }, Fallbacks = new Default() };
            }
            return p;
        }

        internal static BetaToolUnion ToTool(ToolSpec spec)
        {
            using (var doc = JsonDocument.Parse(spec.InputSchemaJson))
            {
                var root = doc.RootElement;
                var props = new Dictionary<string, JsonElement>();
                if (root.TryGetProperty("properties", out var pe))
                    foreach (var prop in pe.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
                var required = root.TryGetProperty("required", out var re)
                    ? re.EnumerateArray().Select(r => r.GetString()).ToList()
                    : new List<string>();
                return new BetaTool
                {
                    Name = spec.Name,
                    Description = spec.Description,
                    InputSchema = new InputSchema { Properties = props, Required = required },
                    EagerInputStreaming = true,
                };
            }
        }

        internal static List<BetaMessageParam> ToParams(IEnumerable<ChatMessage> messages)
        {
            var list = new List<BetaMessageParam>();
            foreach (var m in messages)
            {
                var blocks = m.Content.Select(ToParam).Where(b => b != null).ToList();
                if (blocks.Count == 0) continue;
                if (m.Role == "assistant") list.Add(new BetaAssistantMessageParam { Content = blocks });
                else list.Add(new BetaUserMessageParam { Content = blocks });
            }
            return list;
        }

        private static BetaImageBlockParam ToImage(Block b) => new BetaImageBlockParam
        {
            Source = new BetaBase64ImageSource { Data = b.Base64, MediaType = ImageMediaType(b.MediaType) },
        };

        internal static MediaType ImageMediaType(string mt)
        {
            switch ((mt ?? "").ToLowerInvariant())
            {
                case "image/jpeg": case "image/jpg": return MediaType.ImageJpeg;
                case "image/gif": return MediaType.ImageGif;
                case "image/webp": return MediaType.ImageWebP;
                default: return MediaType.ImagePng;
            }
        }

        internal static BetaContentBlockParam ToParam(Block b)
        {
            switch (b.Type)
            {
                case Block.TextType:
                    if (string.IsNullOrEmpty(b.Text)) return null; // the API rejects empty text blocks
                    return new BetaTextBlockParam { Text = b.Text };
                case Block.ImageType:
                    return ToImage(b);
                case Block.DocumentType:
                    return new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = b.Base64 }, Title = b.FileName };
                case Block.ThinkingType:
                    return new BetaThinkingBlockParam { Thinking = b.Thinking ?? "", Signature = b.Signature ?? "" };
                case Block.RedactedThinkingType:
                    return new BetaRedactedThinkingBlockParam { Data = b.Data };
                case Block.ToolUseType:
                    return new BetaToolUseBlockParam { ID = b.Id, Name = b.Name, Input = ParseInput(b.InputJson) };
                case Block.ToolResultType:
                    var content = new List<SdkBlock>();
                    foreach (var c in b.Content ?? new List<Block>())
                    {
                        if (c.Type == Block.TextType && !string.IsNullOrEmpty(c.Text)) content.Add(new BetaTextBlockParam { Text = c.Text });
                        else if (c.Type == Block.ImageType) content.Add(ToImage(c));
                    }
                    if (content.Count == 0) content.Add(new BetaTextBlockParam { Text = b.IsError == true ? "Error" : "OK" });
                    return new BetaToolResultBlockParam { ToolUseID = b.ToolUseId, IsError = b.IsError, Content = content };
                case Block.FallbackType:
                    return new BetaFallbackBlockParam
                    {
                        From = new BetaFallbackInfoParam { Model = b.FromModel },
                        To = new BetaFallbackInfoParam { Model = b.ToModel },
                    };
                default:
                    return null;
            }
        }

        internal static Dictionary<string, JsonElement> ParseInput(string json)
        {
            var d = new Dictionary<string, JsonElement>();
            if (string.IsNullOrWhiteSpace(json)) return d;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
            }
            catch (JsonException) { /* truncated input: echo as empty object */ }
            return d;
        }

        private static string Raw<T>(Anthropic.Core.ApiEnum<string, T> e) where T : struct, Enum
        {
            try { return e.Raw(); } catch { return null; }
        }

        // ------------------------------------------------------------------ streaming

        private sealed class Partial
        {
            public Block Block;
            public StringBuilder Text = new StringBuilder();
            public StringBuilder Json = new StringBuilder();
        }

        public async Task<ModelResponse> StreamAsync(ModelRequest request, AuthCredentials credentials, IStreamSink sink, CancellationToken ct)
        {
            var client = CreateClient(credentials);
            var p = BuildParams(request);
            var partials = new SortedDictionary<int, Partial>();
            var resp = new ModelResponse { Model = request.Model };
            try
            {
                await foreach (var ev in client.Beta.Messages.CreateStreaming(p, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (ev.TryPickStart(out var start))
                    {
                        resp.Model = Raw(start.Message.Model) ?? request.Model;
                        var u = start.Message.Usage;
                        if (u != null)
                        {
                            resp.Usage.InputTokens = u.InputTokens;
                            resp.Usage.CacheReadInputTokens = u.CacheReadInputTokens ?? 0;
                            resp.Usage.CacheCreationInputTokens = u.CacheCreationInputTokens ?? 0;
                            resp.Usage.OutputTokens = u.OutputTokens;
                        }
                        sink?.OnMessageStart(resp.Model);
                    }
                    else if (ev.TryPickContentBlockStart(out var cbs))
                    {
                        var idx = (int)cbs.Index;
                        var block = FromStartBlock(cbs.ContentBlock);
                        if (block == null) continue; // unknown/server block types are ignored
                        var part = new Partial { Block = block };
                        if (block.Text != null) part.Text.Append(block.Text);
                        if (block.Thinking != null) part.Text.Append(block.Thinking);
                        partials[idx] = part;
                        sink?.OnBlockStart(idx, block);
                    }
                    else if (ev.TryPickContentBlockDelta(out var cbd))
                    {
                        var idx = (int)cbd.Index;
                        if (!partials.TryGetValue(idx, out var part)) continue;
                        var d = cbd.Delta;
                        if (d.TryPickText(out var td)) { part.Text.Append(td.Text); sink?.OnTextDelta(idx, td.Text); }
                        else if (d.TryPickThinking(out var th)) { part.Text.Append(th.Thinking); sink?.OnThinkingDelta(idx, th.Thinking); }
                        else if (d.TryPickSignature(out var sg)) { part.Block.Signature = sg.Signature; }
                        else if (d.TryPickInputJson(out var ij)) { part.Json.Append(ij.PartialJson); sink?.OnToolInputDelta(idx, ij.PartialJson); }
                    }
                    else if (ev.TryPickContentBlockStop(out var stop))
                    {
                        var idx = (int)stop.Index;
                        if (partials.TryGetValue(idx, out var part)) Finish(part);
                        sink?.OnBlockStop(idx);
                    }
                    else if (ev.TryPickDelta(out var md))
                    {
                        resp.StopReason = md.Delta?.StopReason is { } sr ? Raw(sr) : resp.StopReason;
                        if (md.Delta?.StopDetails != null) resp.RefusalExplanation = md.Delta.StopDetails.Explanation;
                        if (md.Usage != null)
                        {
                            resp.Usage.OutputTokens = md.Usage.OutputTokens;
                            if (md.Usage.InputTokens.HasValue) resp.Usage.InputTokens = md.Usage.InputTokens.Value;
                            if (md.Usage.CacheReadInputTokens.HasValue) resp.Usage.CacheReadInputTokens = md.Usage.CacheReadInputTokens.Value;
                            if (md.Usage.CacheCreationInputTokens.HasValue) resp.Usage.CacheCreationInputTokens = md.Usage.CacheCreationInputTokens.Value;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (!(e is ModelApiException))
            {
                throw Normalize(e);
            }

            foreach (var part in partials.Values) Finish(part);
            resp.Content = partials.Values.Select(x => x.Block).ToList();
            return resp;
        }

        private static void Finish(Partial part)
        {
            var b = part.Block;
            if (b.Type == Block.TextType) b.Text = part.Text.ToString();
            else if (b.Type == Block.ThinkingType) b.Thinking = part.Text.ToString();
            else if (b.Type == Block.ToolUseType)
            {
                var json = part.Json.ToString();
                b.InputJson = string.IsNullOrWhiteSpace(json) ? (b.InputJson ?? "{}") : json;
            }
        }

        private static Block FromStartBlock(ContentBlock cb)
        {
            if (cb.TryPickBetaText(out var t)) return new Block { Type = Block.TextType, Text = t.Text ?? "" };
            if (cb.TryPickBetaThinking(out var th)) return new Block { Type = Block.ThinkingType, Thinking = th.Thinking ?? "", Signature = th.Signature };
            if (cb.TryPickBetaRedactedThinking(out var rt)) return new Block { Type = Block.RedactedThinkingType, Data = rt.Data };
            if (cb.TryPickBetaToolUse(out var tu))
            {
                var input = tu.Input != null && tu.Input.Count > 0 ? JsonSerializer.Serialize(tu.Input) : null;
                return new Block { Type = Block.ToolUseType, Id = tu.ID, Name = tu.Name, InputJson = input };
            }
            if (cb.TryPickBetaFallback(out var fb))
                return new Block { Type = Block.FallbackType, FromModel = Raw(fb.From.Model), ToModel = Raw(fb.To.Model) };
            return null;
        }

        internal static ModelApiException Normalize(Exception e)
        {
            try { Diagnostics?.Invoke("API call failed: " + Describe(e)); } catch { }
            switch (e)
            {
                case AnthropicUnauthorizedException _:
                    return new ModelApiException(ModelErrorKind.Auth, "Your API key was rejected. Check it in Settings.", e);
                case AnthropicForbiddenException f:
                    return new ModelApiException(ModelErrorKind.Auth, "This API key isn't allowed to do that. " + ApiMessage(f), e);
                case AnthropicRateLimitException _:
                    return new ModelApiException(ModelErrorKind.RateLimit, "Rate limited by the Anthropic API. Wait a moment and try again.", e);
                case AnthropicOverloadedException _:
                    return new ModelApiException(ModelErrorKind.Overloaded, "Claude is temporarily overloaded. Try again in a moment.", e);
                case AnthropicContentTooLargeException _:
                    return new ModelApiException(ModelErrorKind.TooLarge, "The request is too large (attachments or a long chat). Start a new chat or remove attachments.", e);
                case AnthropicPaymentRequiredException _:
                    return new ModelApiException(ModelErrorKind.Auth, "Your Anthropic account has no remaining credit. Check billing in the Console.", e);
                case AnthropicBadRequestException b when ApiMessage(b).IndexOf("credit balance", StringComparison.OrdinalIgnoreCase) >= 0:
                    return new ModelApiException(ModelErrorKind.Billing, "Your Anthropic account is out of API credits. Add credits under Plans & Billing in the Console, then retry.", e);
                case AnthropicBadRequestException b:
                    return new ModelApiException(ModelErrorKind.BadRequest, "The API rejected the request: " + ApiMessage(b), e);
                case AnthropicNotFoundException nf:
                    return new ModelApiException(ModelErrorKind.BadRequest, "Not found (is the model name right?): " + ApiMessage(nf), e);
                case Anthropic5xxException _:
                    return new ModelApiException(ModelErrorKind.Server, "Anthropic's API had a server error. Try again.", e);
                case AnthropicIOException _:
                case HttpRequestException _:
                case TaskCanceledException _:
                    return new ModelApiException(ModelErrorKind.Network, "Can't reach the Anthropic API. Check your internet connection.", e);
                case AnthropicSseException sse:
                    var msg = sse.Message ?? "";
                    if (msg.IndexOf("overloaded", StringComparison.OrdinalIgnoreCase) >= 0)
                        return new ModelApiException(ModelErrorKind.Overloaded, "Claude is temporarily overloaded. Try again in a moment.", e);
                    return new ModelApiException(ModelErrorKind.Server, "The response stream failed: " + msg, e);
                case AnthropicServiceException s:
                    return new ModelApiException(ModelErrorKind.Unknown, "API error: " + ApiMessage(s), e);
                default:
                    return new ModelApiException(ModelErrorKind.Unknown, "Unexpected error: " + e.Message, e);
            }
        }

        private static string ApiMessage(AnthropicServiceException e)
        {
            try { return string.IsNullOrEmpty(e.ApiMessage) ? e.Message : e.ApiMessage; } catch { return e.Message; }
        }

        // ------------------------------------------------------------------ models / validation

        public async Task<IReadOnlyList<ModelInfoLite>> ListModelsAsync(AuthCredentials credentials, CancellationToken ct)
        {
            var client = CreateClient(credentials);
            try
            {
                var page = await client.Models.List(cancellationToken: ct).ConfigureAwait(false);
                return page.Items
                    .Where(m => m.ID.StartsWith("claude-", StringComparison.Ordinal))
                    .Select(m => new ModelInfoLite { Id = m.ID, DisplayName = string.IsNullOrEmpty(m.DisplayName) ? m.ID : m.DisplayName })
                    .ToList();
            }
            catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                throw Normalize(e);
            }
        }

        public async Task<AuthResult> ValidateAsync(AuthCredentials credentials, CancellationToken ct)
        {
            try
            {
                await ListModelsAsync(credentials, ct).ConfigureAwait(false);
                return AuthResult.Success();
            }
            catch (ModelApiException e) when (e.Kind == ModelErrorKind.Auth)
            {
                return AuthResult.Fail("That key was rejected by Anthropic. Copy it again from console.anthropic.com.");
            }
            catch (ModelApiException e)
            {
                return AuthResult.Fail(e.Message);
            }
        }
    }
}
