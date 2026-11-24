using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;

namespace ClaudeSW.Core.Agent
{
    /// <summary>
    /// Offline model that replays a fixed list of responses. Used by unit tests and by the
    /// add-in's developer "demo" mode, which drives the real UI and real SOLIDWORKS tools
    /// without an API key. Each response streams its text in small chunks like the real API.
    /// </summary>
    public sealed class ScriptedModelClient : IModelClient
    {
        private readonly Queue<ModelResponse> _responses;
        public List<ModelRequest> Requests { get; } = new List<ModelRequest>();
        public int ChunkSize { get; set; } = 12;
        public int DelayMs { get; set; }

        public ScriptedModelClient(IEnumerable<ModelResponse> responses) { _responses = new Queue<ModelResponse>(responses); }

        public static ModelResponse Text(string text) => new ModelResponse
        {
            Content = { Block.OfText(text) },
            StopReason = "end_turn",
            Usage = new Usage { InputTokens = 100, OutputTokens = 20 },
        };

        public static ModelResponse Tools(string text, params (string name, object input)[] calls)
        {
            var r = new ModelResponse { StopReason = "tool_use", Usage = new Usage { InputTokens = 100, OutputTokens = 20 } };
            if (!string.IsNullOrEmpty(text)) r.Content.Add(Block.OfText(text));
            var i = 0;
            foreach (var (name, input) in calls)
                r.Content.Add(Block.OfToolUse($"toolu_{Guid.NewGuid():N}".Substring(0, 20) + i++, name, JsonSerializer.Serialize(input)));
            return r;
        }

        /// <summary>Script file format: [{"text": "...", "tools": [{"name": "...", "input": {...}}]}, ...]</summary>
        public static ScriptedModelClient FromJson(string json)
        {
            var list = new List<ModelResponse>();
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var step in doc.RootElement.EnumerateArray())
                {
                    var r = new ModelResponse { Usage = new Usage { InputTokens = 1200, OutputTokens = 80 } };
                    if (step.TryGetProperty("text", out var t)) r.Content.Add(Block.OfText(t.GetString()));
                    if (step.TryGetProperty("tools", out var tools))
                    {
                        var i = 0;
                        foreach (var tool in tools.EnumerateArray())
                            r.Content.Add(Block.OfToolUse("toolu_demo_" + list.Count + "_" + i++, tool.GetProperty("name").GetString(),
                                tool.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}"));
                    }
                    r.StopReason = r.Content.Any(b => b.Type == Block.ToolUseType) ? "tool_use" : "end_turn";
                    list.Add(r);
                }
            }
            return new ScriptedModelClient(list) { DelayMs = 15 };
        }

        public async Task<ModelResponse> StreamAsync(ModelRequest request, AuthCredentials credentials, IStreamSink sink, CancellationToken ct)
        {
            Requests.Add(request);
            if (_responses.Count == 0) return Text("(script finished)");
            var r = _responses.Dequeue();
            sink?.OnMessageStart(request.Model);
            for (var i = 0; i < r.Content.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var b = r.Content[i];
                if (b.Type == Block.TextType)
                {
                    sink?.OnBlockStart(i, new Block { Type = Block.TextType, Text = "" });
                    for (var p = 0; p < b.Text.Length; p += ChunkSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        sink?.OnTextDelta(i, b.Text.Substring(p, Math.Min(ChunkSize, b.Text.Length - p)));
                        if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    sink?.OnBlockStart(i, b);
                    if (b.Type == Block.ToolUseType) sink?.OnToolInputDelta(i, b.InputJson);
                }
                sink?.OnBlockStop(i);
            }
            return new ModelResponse
            {
                Content = r.Content.Select(x => x.Clone()).ToList(),
                StopReason = r.StopReason,
                Usage = r.Usage,
                Model = request.Model,
                RefusalExplanation = r.RefusalExplanation,
            };
        }

        public Task<IReadOnlyList<ModelInfoLite>> ListModelsAsync(AuthCredentials credentials, CancellationToken ct) =>
            Task.FromResult(KnownModels.Fallback);
    }
}
