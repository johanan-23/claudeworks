using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;

namespace ClaudeSW.Core.Agent
{
    /// <summary>Everything the UI needs to follow a turn. Called from a background thread.</summary>
    public interface IAgentObserver : IStreamSink
    {
        void OnRequestStart(int iteration);
        /// <summary>A finished message was appended to the conversation (assistant or tool results).</summary>
        void OnMessageAppended(ChatMessage message);
        void OnToolStart(Block toolUse, string description);
        void OnToolEnd(string toolUseId, ToolOutput output);
        /// <summary>Ask the user to approve a destructive tool call.</summary>
        Task<bool> ConfirmAsync(string toolUseId, string toolName, string question, CancellationToken ct);
        void OnUsage(Usage last, Usage total);
    }

    public sealed class AgentOptions
    {
        public string Model { get; set; } = KnownModels.Default;
        public string SystemPrompt { get; set; }
        public bool ExtendedThinking { get; set; }
        public int MaxTokens { get; set; } = 32000;
        public int MaxIterations { get; set; } = 40;
    }

    public enum TurnOutcome { Completed, Cancelled, Refused, Failed, IterationLimit, MaxTokens }

    public sealed class TurnResult
    {
        public TurnOutcome Outcome { get; set; }
        public string Message { get; set; }
        public ModelApiException Error { get; set; }
    }

    /// <summary>
    /// The manual tool-use loop: stream a response, run requested tools (validated, confirmed when
    /// destructive), send results back, repeat until the model ends its turn.
    /// </summary>
    public sealed class AgentLoop
    {
        private readonly IModelClient _client;
        private readonly IToolExecutor _tools;
        private readonly Func<AuthCredentials> _credentials;

        public AgentLoop(IModelClient client, IToolExecutor tools, Func<AuthCredentials> credentials)
        {
            _client = client;
            _tools = tools;
            _credentials = credentials;
        }

        public async Task<TurnResult> RunAsync(Conversation conv, AgentOptions opts, IAgentObserver obs, CancellationToken ct)
        {
            var tools = _tools.GetTools();
            var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
            for (var iteration = 0; iteration < opts.MaxIterations; iteration++)
            {
                if (ct.IsCancellationRequested) return Cancelled(conv, null, obs);
                obs?.OnRequestStart(iteration);
                var partial = new PartialRecorder(obs);
                ModelResponse resp;
                try
                {
                    resp = await _client.StreamAsync(new ModelRequest
                    {
                        Model = opts.Model,
                        SystemPrompt = opts.SystemPrompt,
                        Tools = tools,
                        Messages = conv.Messages.ToList(),
                        ExtendedThinking = opts.ExtendedThinking,
                        MaxTokens = opts.MaxTokens,
                    }, _credentials(), partial, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Cancelled(conv, partial, obs);
                }
                catch (ModelApiException e)
                {
                    // Keep what was already streamed so the user doesn't lose it.
                    AppendPartialText(conv, partial, "error", obs);
                    return new TurnResult { Outcome = TurnOutcome.Failed, Message = e.Message, Error = e };
                }

                conv.LastUsage = resp.Usage;
                conv.TotalUsage.Add(resp.Usage);
                obs?.OnUsage(resp.Usage, conv.TotalUsage);

                var content = ApplyFallbackEchoRules(resp.Content);
                var assistant = new ChatMessage
                {
                    Role = "assistant",
                    Content = content,
                    Model = resp.Model,
                    Usage = resp.Usage,
                    StopReason = resp.StopReason,
                };
                var toolUses = content.Where(b => b.Type == Block.ToolUseType).ToList();
                if (content.Count == 0)
                {
                    assistant.Content.Add(Block.OfText(resp.StopReason == "refusal" ? "" : "(no response)"));
                }
                conv.Messages.Add(assistant);
                conv.Updated = DateTimeOffset.UtcNow;
                obs?.OnMessageAppended(assistant);

                if (resp.StopReason == "refusal")
                    return new TurnResult
                    {
                        Outcome = TurnOutcome.Refused,
                        Message = "Claude declined this request" + (string.IsNullOrEmpty(resp.RefusalExplanation) ? "." : ": " + resp.RefusalExplanation),
                    };

                if (toolUses.Count == 0)
                {
                    if (resp.StopReason == "max_tokens")
                        return new TurnResult { Outcome = TurnOutcome.MaxTokens, Message = "The response hit the length limit. Ask Claude to continue." };
                    return new TurnResult { Outcome = TurnOutcome.Completed };
                }

                // Execute every requested tool and return all results in ONE user message.
                var results = new List<Block>();
                var truncated = resp.StopReason == "max_tokens";
                foreach (var tu in toolUses)
                {
                    if (ct.IsCancellationRequested)
                    {
                        results.Add(ErrorResult(tu.Id, "Cancelled by the user before this tool ran."));
                        continue;
                    }
                    results.Add(await RunToolAsync(tu, byName, truncated, obs, ct).ConfigureAwait(false));
                }
                var resultMsg = new ChatMessage { Role = "user", Content = results };
                conv.Messages.Add(resultMsg);
                obs?.OnMessageAppended(resultMsg);
                if (ct.IsCancellationRequested) return new TurnResult { Outcome = TurnOutcome.Cancelled, Message = "Stopped." };
            }
            return new TurnResult { Outcome = TurnOutcome.IterationLimit, Message = $"Stopped after {opts.MaxIterations} steps. Say \"continue\" to keep going." };
        }

        private async Task<Block> RunToolAsync(Block tu, Dictionary<string, ToolSpec> byName, bool truncated, IAgentObserver obs, CancellationToken ct)
        {
            if (!byName.TryGetValue(tu.Name ?? "", out var spec))
                return Report(tu, ToolOutput.Error($"Unknown tool '{tu.Name}'."), obs, describe: tu.Name);

            Args args;
            JsonElement root;
            try
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(tu.InputJson) ? "{}" : tu.InputJson))
                    root = doc.RootElement.Clone();
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("input is not an object");
                args = new Args(root);
            }
            catch (JsonException)
            {
                var why = truncated ? "the response was cut off while writing the tool input" : "the tool input was not valid JSON";
                return Report(tu, ToolOutput.Error($"INVALID_JSON: {why}. Call the tool again with complete arguments."), obs, describe: spec.Name);
            }

            var errors = SchemaValidator.Validate(spec.InputSchemaJson, root);
            if (errors.Count > 0)
                return Report(tu, ToolOutput.Error("Invalid arguments: " + string.Join("; ", errors)), obs, describe: spec.Name);

            string description = null;
            try { description = spec.Describe?.Invoke(args); } catch { /* describing must never fail a call */ }
            obs?.OnToolStart(tu, description ?? spec.Name);

            string question = null;
            try { question = spec.ConfirmMessage != null ? spec.ConfirmMessage(args) : (spec.Destructive ? $"Allow Claude to run {spec.Name}?" : null); }
            catch (ToolException e) { return Report(tu, ToolOutput.Error(e.Message), obs, started: true); }
            if (question != null)
            {
                bool ok;
                try { ok = obs != null && await obs.ConfirmAsync(tu.Id, spec.Name, question, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { ok = false; }
                if (!ok) return Report(tu, ToolOutput.Error("The user declined this action. Do not retry it unless they ask."), obs, started: true);
            }

            ToolOutput output;
            try
            {
                output = await _tools.ExecuteAsync(spec, args, new ToolContext { ToolUseId = tu.Id, Cancellation = ct }).ConfigureAwait(false)
                         ?? ToolOutput.Text("Done.");
            }
            catch (ToolException e) { output = ToolOutput.Error(e.Message); }
            catch (Exception e) { output = ToolOutput.Error($"{spec.Name} failed: {e.Message}"); }
            return Report(tu, output, obs, started: true);
        }

        private static Block Report(Block tu, ToolOutput output, IAgentObserver obs, bool started = false, string describe = null)
        {
            if (!started) obs?.OnToolStart(tu, describe ?? tu.Name);
            obs?.OnToolEnd(tu.Id, output);
            return new Block
            {
                Type = Block.ToolResultType,
                ToolUseId = tu.Id,
                IsError = output.IsError ? true : (bool?)null,
                Content = output.Content,
                UiSummary = output.Summary,
            };
        }

        private static Block ErrorResult(string id, string message) => new Block
        {
            Type = Block.ToolResultType,
            ToolUseId = id,
            IsError = true,
            Content = new List<Block> { Block.OfText(message) },
            UiSummary = message,
        };

        /// <summary>
        /// After a mid-output server-side fallback, thinking/redacted_thinking/tool_use blocks that
        /// precede the final fallback block must not be echoed back (and such tool calls never run).
        /// The fallback block itself stays exactly where it appeared.
        /// </summary>
        internal static List<Block> ApplyFallbackEchoRules(List<Block> content)
        {
            var last = content.FindLastIndex(b => b.Type == Block.FallbackType);
            if (last < 0) return content;
            var result = new List<Block>();
            for (var i = 0; i < content.Count; i++)
            {
                var b = content[i];
                if (i < last && (b.Type == Block.ThinkingType || b.Type == Block.RedactedThinkingType || b.Type == Block.ToolUseType))
                    continue;
                result.Add(b);
            }
            return result;
        }

        private static TurnResult Cancelled(Conversation conv, PartialRecorder partial, IAgentObserver obs)
        {
            AppendPartialText(conv, partial, "cancelled", obs);
            return new TurnResult { Outcome = TurnOutcome.Cancelled, Message = "Stopped." };
        }

        private static void AppendPartialText(Conversation conv, PartialRecorder partial, string stopReason, IAgentObserver obs)
        {
            if (partial == null) return;
            var text = partial.Text();
            if (string.IsNullOrWhiteSpace(text)) return;
            var msg = new ChatMessage { Role = "assistant", Content = { Block.OfText(text) }, StopReason = stopReason };
            conv.Messages.Add(msg);
            obs?.OnMessageAppended(msg);
        }

        /// <summary>Forwards stream events and remembers streamed text (for stop/error recovery).</summary>
        private sealed class PartialRecorder : IStreamSink
        {
            private readonly IStreamSink _inner;
            private readonly SortedDictionary<int, StringBuilder> _text = new SortedDictionary<int, StringBuilder>();
            public PartialRecorder(IStreamSink inner) { _inner = inner; }
            public string Text() => string.Join("\n\n", _text.Values.Select(s => s.ToString()).Where(s => s.Length > 0));
            public void OnMessageStart(string model) => _inner?.OnMessageStart(model);
            public void OnBlockStart(int index, Block block)
            {
                if (block.Type == Block.TextType) _text[index] = new StringBuilder(block.Text ?? "");
                _inner?.OnBlockStart(index, block);
            }
            public void OnTextDelta(int index, string text)
            {
                if (_text.TryGetValue(index, out var sb)) sb.Append(text);
                _inner?.OnTextDelta(index, text);
            }
            public void OnThinkingDelta(int index, string text) => _inner?.OnThinkingDelta(index, text);
            public void OnToolInputDelta(int index, string partialJson) => _inner?.OnToolInputDelta(index, partialJson);
            public void OnBlockStop(int index) => _inner?.OnBlockStop(index);
        }

        /// <summary>Default title: the first line of the first human message, trimmed.</summary>
        public static string TitleFrom(Conversation conv)
        {
            var first = conv.Messages.FirstOrDefault(m => m.IsHumanTurn);
            var text = first?.Content.FirstOrDefault(b => b.Type == Block.TextType && b.UiHidden != true)?.Text;
            if (string.IsNullOrWhiteSpace(text)) return "New chat";
            text = text.Trim().Split('\n')[0].Trim();
            return text.Length <= 48 ? text : text.Substring(0, 47).TrimEnd() + "…";
        }
    }
}
