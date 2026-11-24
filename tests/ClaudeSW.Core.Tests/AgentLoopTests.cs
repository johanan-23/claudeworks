using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;
using Xunit;

namespace ClaudeSW.Core.Tests
{
    internal sealed class RecordingObserver : IAgentObserver
    {
        public List<string> Events { get; } = new List<string>();
        public bool Approve { get; set; } = true;
        public Action OnToolStartHook { get; set; }
        public string Streamed = "";
        public void OnMessageStart(string model) => Events.Add("start:" + model);
        public void OnBlockStart(int index, Block block) => Events.Add($"block:{index}:{block.Type}");
        public void OnTextDelta(int index, string text) => Streamed += text;
        public void OnThinkingDelta(int index, string text) { }
        public void OnToolInputDelta(int index, string partialJson) { }
        public void OnBlockStop(int index) { }
        public void OnRequestStart(int iteration) => Events.Add("request:" + iteration);
        public void OnMessageAppended(ChatMessage m) => Events.Add("append:" + m.Role + (m.IsToolResultMessage ? ":results" : ""));
        public void OnToolStart(Block tu, string d) { Events.Add("tool:" + tu.Name + ":" + d); OnToolStartHook?.Invoke(); }
        public void OnToolEnd(string id, ToolOutput o) => Events.Add("toolend:" + (o.IsError ? "error" : "ok"));
        public Task<bool> ConfirmAsync(string id, string name, string q, CancellationToken ct) { Events.Add("confirm:" + name); return Task.FromResult(Approve); }
        public void OnUsage(Usage last, Usage total) { }
    }

    public class AgentLoopTests
    {
        private static readonly AuthCredentials Creds = new AuthCredentials { ApiKey = "x" };

        private static List<ToolSpec> Tools(List<string> calls) => new List<ToolSpec>
        {
            new ToolSpec
            {
                Name = "extrude",
                InputSchemaJson = new SchemaBuilder().Num("depth", "mm", required: true, min: 0).Build(),
                Describe = a => $"Extrude {a.Num("depth")} mm",
                Run = (a, c) => { calls.Add("extrude:" + a.Mm("depth")); return ToolOutput.Text("ok", "Extruded"); },
            },
            new ToolSpec
            {
                Name = "delete_feature",
                Destructive = true,
                InputSchemaJson = new SchemaBuilder().Str("name", "n", required: true).Build(),
                Run = (a, c) => { calls.Add("delete:" + a.Str("name")); return ToolOutput.Text("deleted"); },
            },
            new ToolSpec
            {
                Name = "boom",
                Run = (a, c) => throw new InvalidOperationException("COM exploded"),
            },
            new ToolSpec
            {
                Name = "capture_view",
                Run = (a, c) => ToolOutput.Text("view").WithImage("image/png", "iVBORw0KGgo="),
            },
        };

        private static (AgentLoop loop, ScriptedModelClient model, List<string> calls) Make(params ModelResponse[] responses)
        {
            var calls = new List<string>();
            var model = new ScriptedModelClient(responses);
            return (new AgentLoop(model, new InlineToolExecutor(Tools(calls)), () => Creds), model, calls);
        }

        private static Conversation Conv(string text = "make a plate")
        {
            var c = new Conversation();
            c.Messages.Add(ChatMessage.User(Block.OfText(text)));
            return c;
        }

        [Fact]
        public async Task RunsToolsThenFinishes()
        {
            var (loop, model, calls) = Make(
                ScriptedModelClient.Tools("Extruding.", ("extrude", new { depth = 20 })),
                ScriptedModelClient.Text("Done: 20 mm plate."));
            var conv = Conv();
            var obs = new RecordingObserver();
            var r = await loop.RunAsync(conv, new AgentOptions(), obs, CancellationToken.None);

            Assert.Equal(TurnOutcome.Completed, r.Outcome);
            Assert.Equal(new[] { "extrude:0.02" }, calls);
            Assert.Equal(4, conv.Messages.Count); // user, assistant(tool_use), user(results), assistant
            var results = conv.Messages[2];
            Assert.True(results.IsToolResultMessage);
            Assert.Equal(conv.Messages[1].Content.Single(b => b.Type == Block.ToolUseType).Id, results.Content[0].ToolUseId);
            Assert.Equal("Extruded", results.Content[0].UiSummary);
            Assert.Contains("tool:extrude:Extrude 20 mm", obs.Events);
            Assert.Contains("Done: 20 mm plate.", obs.Streamed);
            // second request contained the tool result
            Assert.Equal(3, model.Requests[1].Messages.Count);
        }

        [Fact]
        public async Task ParallelToolCallsReturnInOneMessage()
        {
            var (loop, _, calls) = Make(
                ScriptedModelClient.Tools(null, ("extrude", new { depth = 1 }), ("extrude", new { depth = 2 })),
                ScriptedModelClient.Text("ok"));
            var conv = Conv();
            await loop.RunAsync(conv, new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Equal(2, conv.Messages[2].Content.Count);
            Assert.Equal(2, calls.Count);
        }

        [Fact]
        public async Task InvalidArgumentsBecomeErrorResults()
        {
            var (loop, _, calls) = Make(
                ScriptedModelClient.Tools(null, ("extrude", new { depth = -5 })),
                ScriptedModelClient.Text("sorry"));
            var conv = Conv();
            await loop.RunAsync(conv, new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Empty(calls);
            var res = conv.Messages[2].Content[0];
            Assert.True(res.IsError);
            Assert.Contains(">= 0", res.Content[0].Text);
        }

        [Fact]
        public async Task TruncatedJsonIsReportedNotRun()
        {
            var bad = new ModelResponse { StopReason = "max_tokens", Content = { Block.OfToolUse("t1", "extrude", "{\"depth\": 2") } };
            var (loop, _, calls) = Make(bad, ScriptedModelClient.Text("retry"));
            var conv = Conv();
            await loop.RunAsync(conv, new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Empty(calls);
            Assert.StartsWith("INVALID_JSON", conv.Messages[2].Content[0].Content[0].Text);
        }

        [Fact]
        public async Task UnknownToolAndExceptionsAreErrors()
        {
            var (loop, _, _) = Make(
                ScriptedModelClient.Tools(null, ("nope", new { }), ("boom", new { })),
                ScriptedModelClient.Text("hmm"));
            var conv = Conv();
            var r = await loop.RunAsync(conv, new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Equal(TurnOutcome.Completed, r.Outcome);
            var res = conv.Messages[2].Content;
            Assert.All(res, b => Assert.True(b.IsError));
            Assert.Contains("Unknown tool", res[0].Content[0].Text);
            Assert.Contains("COM exploded", res[1].Content[0].Text);
        }

        [Fact]
        public async Task DestructiveToolNeedsConfirmation()
        {
            var (loop, _, calls) = Make(
                ScriptedModelClient.Tools(null, ("delete_feature", new { name = "Fillet1" })),
                ScriptedModelClient.Text("ok"));
            var obs = new RecordingObserver { Approve = false };
            var conv = Conv();
            await loop.RunAsync(conv, new AgentOptions(), obs, CancellationToken.None);
            Assert.Contains("confirm:delete_feature", obs.Events);
            Assert.Empty(calls);
            Assert.Contains("declined", conv.Messages[2].Content[0].Content[0].Text);

            var (loop2, _, calls2) = Make(
                ScriptedModelClient.Tools(null, ("delete_feature", new { name = "Fillet1" })),
                ScriptedModelClient.Text("ok"));
            await loop2.RunAsync(Conv(), new AgentOptions(), new RecordingObserver { Approve = true }, CancellationToken.None);
            Assert.Equal(new[] { "delete:Fillet1" }, calls2);
        }

        [Fact]
        public async Task ImagesFlowIntoToolResults()
        {
            var (loop, _, _) = Make(ScriptedModelClient.Tools(null, ("capture_view", new { })), ScriptedModelClient.Text("looks good"));
            var conv = Conv();
            await loop.RunAsync(conv, new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Contains(conv.Messages[2].Content[0].Content, b => b.Type == Block.ImageType && b.MediaType == "image/png");
        }

        [Fact]
        public async Task CancellationDuringToolsStopsTheTurn()
        {
            var cts = new CancellationTokenSource();
            var (loop, _, calls) = Make(
                ScriptedModelClient.Tools(null, ("extrude", new { depth = 1 }), ("extrude", new { depth = 2 })),
                ScriptedModelClient.Text("never"));
            var obs = new RecordingObserver { OnToolStartHook = () => cts.Cancel() };
            var conv = Conv();
            var r = await loop.RunAsync(conv, new AgentOptions(), obs, cts.Token);
            Assert.Equal(TurnOutcome.Cancelled, r.Outcome);
            Assert.Single(calls); // the first tool already started, the second was skipped
            // every tool_use still has a tool_result, so the conversation stays valid
            Assert.Equal(2, conv.Messages[2].Content.Count);
        }

        [Fact]
        public async Task RefusalEndsTurn()
        {
            var refusal = new ModelResponse { StopReason = "refusal", RefusalExplanation = "policy" };
            var (loop, _, _) = Make(refusal);
            var r = await loop.RunAsync(Conv(), new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Equal(TurnOutcome.Refused, r.Outcome);
            Assert.Contains("policy", r.Message);
        }

        [Fact]
        public async Task IterationLimit()
        {
            var many = Enumerable.Range(0, 5).Select(_ => ScriptedModelClient.Tools(null, ("extrude", new { depth = 1 }))).ToArray();
            var (loop, _, calls) = Make(many);
            var r = await loop.RunAsync(Conv(), new AgentOptions { MaxIterations = 3 }, new RecordingObserver(), CancellationToken.None);
            Assert.Equal(TurnOutcome.IterationLimit, r.Outcome);
            Assert.Equal(3, calls.Count);
        }

        [Fact]
        public void FallbackEchoRulesDropPreBoundaryInternals()
        {
            var content = new List<Block>
            {
                new Block { Type = Block.ThinkingType, Thinking = "a", Signature = "s" },
                Block.OfText("partial"),
                Block.OfToolUse("t0", "extrude", "{}"),
                new Block { Type = Block.FallbackType, FromModel = "claude-opus-5-5", ToModel = "claude-opus-5" },
                new Block { Type = Block.ThinkingType, Thinking = "b", Signature = "s2" },
                Block.OfText("final"),
            };
            var r = AgentLoop.ApplyFallbackEchoRules(content);
            Assert.Equal(new[] { "text", "fallback", "thinking", "text" }, r.Select(b => b.Type));
        }

        [Fact]
        public async Task ApiErrorKeepsStreamedText()
        {
            var failing = new FailingClient();
            var loop = new AgentLoop(failing, new InlineToolExecutor(new ToolSpec[0]), () => Creds);
            var conv = Conv();
            var r = await loop.RunAsync(conv, new AgentOptions(), new RecordingObserver(), CancellationToken.None);
            Assert.Equal(TurnOutcome.Failed, r.Outcome);
            Assert.Equal(ModelErrorKind.Overloaded, r.Error.Kind);
            Assert.Equal("Half an ans", conv.Messages.Last().Content[0].Text);
        }

        [Fact]
        public void TitleFromFirstHumanMessage()
        {
            var c = new Conversation();
            c.Messages.Add(ChatMessage.User(new Block { Type = "text", Text = "<ctx/>", UiHidden = true }, Block.OfText("Make a 100x60x20 plate with a 10 mm centre hole and 3 mm corner fillets please")));
            var t = AgentLoop.TitleFrom(c);
            Assert.StartsWith("Make a 100x60x20 plate", t);
            Assert.True(t.Length <= 48);
        }

        private sealed class FailingClient : IModelClient
        {
            public Task<ModelResponse> StreamAsync(ModelRequest r, AuthCredentials c, IStreamSink sink, CancellationToken ct)
            {
                sink.OnBlockStart(0, Block.OfText(""));
                sink.OnTextDelta(0, "Half an ans");
                throw new ModelApiException(ModelErrorKind.Overloaded, "overloaded");
            }
            public Task<IReadOnlyList<ModelInfoLite>> ListModelsAsync(AuthCredentials c, CancellationToken ct) => throw new NotImplementedException();
        }
    }
}
