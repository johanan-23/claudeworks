using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;
using ClaudeSW.Core.Storage;

namespace ClaudeSW.Core.Agent
{
    /// <summary>
    /// Runs the Anthropic SDK in a private AppDomain (still inside SOLIDWORKS). Other add-ins load
    /// their own copies of System.Threading.Tasks.Extensions / System.Memory etc. and hook
    /// AssemblyResolve, which can hand the SDK mismatched types ("Method not found ... ValueTask").
    /// The private domain probes only our folder and uses our binding redirects, so it is immune.
    /// Requests/responses cross the boundary as JSON; streaming events via a MarshalByRef sink.
    /// </summary>
    public sealed class IsolatedModelClient : IModelClient, ICredentialValidator, IDisposable
    {
        private readonly AppDomain _domain;
        private readonly RemoteModelHost _host;

        public IsolatedModelClient(string baseDirectory, string configFile)
        {
            var setup = new AppDomainSetup
            {
                ApplicationBase = baseDirectory,
                ConfigurationFile = configFile,
                ApplicationName = "ClaudeForSolidWorks.Net",
                DisallowBindingRedirects = false,
            };
            _domain = AppDomain.CreateDomain("ClaudeForSolidWorks.Net", null, setup);
            _host = (RemoteModelHost)_domain.CreateInstanceFromAndUnwrap(typeof(RemoteModelHost).Assembly.Location, typeof(RemoteModelHost).FullName);
            _host.Initialize(AppPaths.Root);
        }

        public Task<ModelResponse> StreamAsync(ModelRequest request, AuthCredentials credentials, IStreamSink sink, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            var json = RemoteModelHost.SerializeRequest(request);
            var remoteSink = new RemoteSink(sink);
            return Task.Run(() =>
            {
                using (ct.Register(() => { try { _host.Cancel(id); } catch { } }))
                {
                    var reply = _host.Stream(json, credentials?.ApiKey, remoteSink, id);
                    return RemoteModelHost.ParseReply<ModelResponse>(reply, ct);
                }
            });
        }

        public Task<IReadOnlyList<ModelInfoLite>> ListModelsAsync(AuthCredentials credentials, CancellationToken ct) =>
            Task.Run(() => (IReadOnlyList<ModelInfoLite>)RemoteModelHost.ParseReply<List<ModelInfoLite>>(_host.ListModels(credentials?.ApiKey), ct));

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
            catch (ModelApiException e) { return AuthResult.Fail(e.Message); }
        }

        public void Dispose()
        {
            try { AppDomain.Unload(_domain); } catch { /* in-flight calls during shutdown */ }
        }
    }

    /// <summary>Forwards stream events from the private domain back to the caller's sink.</summary>
    public sealed class RemoteSink : MarshalByRefObject
    {
        private readonly IStreamSink _inner;
        public RemoteSink(IStreamSink inner) { _inner = inner; }
        public override object InitializeLifetimeService() => null;
        public void MessageStart(string model) => _inner?.OnMessageStart(model);
        public void BlockStart(int index, string blockJson) => _inner?.OnBlockStart(index, Json.Deserialize<Block>(blockJson));
        public void TextDelta(int index, string text) => _inner?.OnTextDelta(index, text);
        public void ThinkingDelta(int index, string text) => _inner?.OnThinkingDelta(index, text);
        public void ToolInputDelta(int index, string partial) => _inner?.OnToolInputDelta(index, partial);
        public void BlockStop(int index) => _inner?.OnBlockStop(index);
    }

    /// <summary>Lives in the private domain and owns the real <see cref="AnthropicModelClient"/>.</summary>
    public sealed class RemoteModelHost : MarshalByRefObject
    {
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new ConcurrentDictionary<string, CancellationTokenSource>();
        private AnthropicModelClient _client;

        public override object InitializeLifetimeService() => null;

        public void Initialize(string appRoot)
        {
            AppPaths.Root = appRoot;
            AnthropicModelClient.Diagnostics = m => Log.Warn("[net] " + m);
            _client = new AnthropicModelClient();
        }

        public void Cancel(string id)
        {
            if (_running.TryGetValue(id, out var cts)) cts.Cancel();
        }

        public string Stream(string requestJson, string apiKey, RemoteSink sink, string id)
        {
            var cts = new CancellationTokenSource();
            _running[id] = cts;
            try
            {
                var req = DeserializeRequest(requestJson);
                var resp = _client.StreamAsync(req, new AuthCredentials { ApiKey = apiKey }, new SinkAdapter(sink), cts.Token).GetAwaiter().GetResult();
                return Json.Serialize(new Reply<ModelResponse> { Value = resp });
            }
            catch (OperationCanceledException) { return Json.Serialize(new Reply<ModelResponse> { Cancelled = true }); }
            catch (ModelApiException e) { return Json.Serialize(new Reply<ModelResponse> { ErrorKind = e.Kind, Error = e.Message }); }
            catch (Exception e)
            {
                Log.Error("Isolated stream failed", e);
                return Json.Serialize(new Reply<ModelResponse> { ErrorKind = ModelErrorKind.Unknown, Error = "Unexpected error: " + e.Message });
            }
            finally { _running.TryRemove(id, out _); }
        }

        public string ListModels(string apiKey)
        {
            try
            {
                var list = _client.ListModelsAsync(new AuthCredentials { ApiKey = apiKey }, CancellationToken.None).GetAwaiter().GetResult();
                return Json.Serialize(new Reply<List<ModelInfoLite>> { Value = list.ToList() });
            }
            catch (ModelApiException e) { return Json.Serialize(new Reply<List<ModelInfoLite>> { ErrorKind = e.Kind, Error = e.Message }); }
            catch (Exception e) { return Json.Serialize(new Reply<List<ModelInfoLite>> { ErrorKind = ModelErrorKind.Unknown, Error = e.Message }); }
        }

        // ---------------------------------------------------------------- wire format (also unit tested)

        public sealed class ToolDto
        {
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("description")] public string Description { get; set; }
            [JsonPropertyName("schema")] public string Schema { get; set; }
        }

        public sealed class RequestDto
        {
            [JsonPropertyName("model")] public string Model { get; set; }
            [JsonPropertyName("system")] public string System { get; set; }
            [JsonPropertyName("tools")] public List<ToolDto> Tools { get; set; }
            [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; }
            [JsonPropertyName("thinking")] public bool Thinking { get; set; }
            [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        }

        public sealed class Reply<T>
        {
            [JsonPropertyName("value")] public T Value { get; set; }
            [JsonPropertyName("error")] public string Error { get; set; }
            [JsonPropertyName("error_kind")] public ModelErrorKind ErrorKind { get; set; }
            [JsonPropertyName("cancelled")] public bool Cancelled { get; set; }
        }

        public static string SerializeRequest(ModelRequest r) => Json.Serialize(new RequestDto
        {
            Model = r.Model,
            System = r.SystemPrompt,
            Tools = r.Tools.Select(t => new ToolDto { Name = t.Name, Description = t.Description, Schema = t.InputSchemaJson }).ToList(),
            Messages = r.Messages.ToList(),
            Thinking = r.ExtendedThinking,
            MaxTokens = r.MaxTokens,
        });

        public static ModelRequest DeserializeRequest(string json)
        {
            var d = Json.Deserialize<RequestDto>(json);
            return new ModelRequest
            {
                Model = d.Model,
                SystemPrompt = d.System,
                Tools = (d.Tools ?? new List<ToolDto>()).Select(t => new ToolSpec { Name = t.Name, Description = t.Description, InputSchemaJson = t.Schema }).ToList(),
                Messages = d.Messages ?? new List<ChatMessage>(),
                ExtendedThinking = d.Thinking,
                MaxTokens = d.MaxTokens,
            };
        }

        public static T ParseReply<T>(string json, CancellationToken ct)
        {
            var r = Json.Deserialize<Reply<T>>(json);
            if (r.Cancelled) throw new OperationCanceledException(ct);
            if (r.Error != null) throw new ModelApiException(r.ErrorKind, r.Error);
            return r.Value;
        }

        private sealed class SinkAdapter : IStreamSink
        {
            private readonly RemoteSink _s;
            public SinkAdapter(RemoteSink s) { _s = s; }
            public void OnMessageStart(string model) => _s?.MessageStart(model);
            public void OnBlockStart(int index, Block block) => _s?.BlockStart(index, Json.Serialize(block));
            public void OnTextDelta(int index, string text) => _s?.TextDelta(index, text);
            public void OnThinkingDelta(int index, string text) => _s?.ThinkingDelta(index, text);
            public void OnToolInputDelta(int index, string partialJson) => _s?.ToolInputDelta(index, partialJson);
            public void OnBlockStop(int index) => _s?.BlockStop(index);
        }
    }
}
