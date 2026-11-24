using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;
using Xunit;

namespace ClaudeSW.Core.Tests
{
    /// <summary>Fake HTTP handler: records requests and replies with canned SSE / JSON.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage req, string body)> Requests { get; } = new List<(HttpRequestMessage, string)>();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; }
        public string ContentType { get; set; } = "text/event-stream";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            Requests.Add((request, (string)body));
            var resp = new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody ?? "", Encoding.UTF8, ContentType) };
            resp.Headers.Add("request-id", "req_test");
            return resp;
        }
    }

    public class AnthropicClientTests
    {
        private static string Sse(params object[] events) =>
            string.Concat(events.Select(e =>
            {
                var json = JsonSerializer.Serialize(e);
                var type = JsonDocument.Parse(json).RootElement.GetProperty("type").GetString();
                return $"event: {type}\ndata: {json}\n\n";
            }));

        private static readonly AuthCredentials Creds = new AuthCredentials { ApiKey = "sk-ant-api03-FAKEFAKEFAKEFAKEFAKEFAKE-test" };

        private static string StreamWithToolCall() => Sse(
            new { type = "message_start", message = new { id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5", content = new object[0], stop_reason = (string)null, stop_sequence = (string)null, usage = new { input_tokens = 50, output_tokens = 1, cache_read_input_tokens = 1000, cache_creation_input_tokens = 0 } } },
            new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } },
            new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "Plan: extrude." } },
            new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "sig123" } },
            new { type = "content_block_stop", index = 0 },
            new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "Making " } },
            new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "the plate." } },
            new { type = "content_block_stop", index = 1 },
            new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "toolu_1", name = "extrude", input = new { } } },
            new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "{\"depth\":" } },
            new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = " 20}" } },
            new { type = "content_block_stop", index = 2 },
            new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string)null }, usage = new { output_tokens = 42 } },
            new { type = "message_stop" });

        private sealed class Sink : IStreamSink
        {
            public StringBuilder Text = new StringBuilder();
            public StringBuilder Thinking = new StringBuilder();
            public string Model;
            public void OnMessageStart(string model) => Model = model;
            public void OnBlockStart(int index, Block block) { }
            public void OnTextDelta(int index, string text) => Text.Append(text);
            public void OnThinkingDelta(int index, string text) => Thinking.Append(text);
            public void OnToolInputDelta(int index, string partialJson) { }
            public void OnBlockStop(int index) { }
        }

        private static ModelRequest Request(params ChatMessage[] messages) => new ModelRequest
        {
            Model = "claude-opus-5-5",
            SystemPrompt = "sys",
            Messages = messages.ToList(),
            Tools = new List<ToolSpec> { new ToolSpec { Name = "extrude", Description = "Extrude", InputSchemaJson = new SchemaBuilder().Num("depth", "mm", true).Build() } },
            ExtendedThinking = true,
        };

        [Fact]
        public async Task ParsesStreamIntoBlocksAndUsage()
        {
            var h = new FakeHandler { ResponseBody = StreamWithToolCall() };
            var client = new AnthropicModelClient(new HttpClient(h));
            var sink = new Sink();
            var r = await client.StreamAsync(Request(ChatMessage.User(Block.OfText("plate"))), Creds, sink, CancellationToken.None);

            Assert.Equal("tool_use", r.StopReason);
            Assert.Equal("claude-opus-5-5", sink.Model);
            Assert.Equal("Making the plate.", sink.Text.ToString());
            Assert.Equal("Plan: extrude.", sink.Thinking.ToString());
            Assert.Equal(new[] { "thinking", "text", "tool_use" }, r.Content.Select(b => b.Type));
            Assert.Equal("sig123", r.Content[0].Signature);
            Assert.Equal("toolu_1", r.Content[2].Id);
            Assert.Equal(20, JsonDocument.Parse(r.Content[2].InputJson).RootElement.GetProperty("depth").GetInt32());
            Assert.Equal(42, r.Usage.OutputTokens);
            Assert.Equal(1000, r.Usage.CacheReadInputTokens);
        }

        [Fact]
        public async Task RequestBodyHasCachingThinkingToolsAndFallback()
        {
            var h = new FakeHandler { ResponseBody = StreamWithToolCall() };
            var client = new AnthropicModelClient(new HttpClient(h));
            var history = new[]
            {
                ChatMessage.User(Block.OfText("plate"), Block.OfImage("image/png", "iVBORw0KGgo=")),
                ChatMessage.Assistant(new Block { Type = "thinking", Thinking = "t", Signature = "s" }, Block.OfToolUse("toolu_9", "extrude", "{\"depth\":5}")),
                new ChatMessage { Role = "user", Content = { new Block { Type = "tool_result", ToolUseId = "toolu_9", Content = new List<Block> { Block.OfText("ok"), Block.OfImage("image/png", "AAAA") }, UiSummary = "ui only" } } },
            };
            await client.StreamAsync(Request(history), Creds, null, CancellationToken.None);

            var (req, body) = h.Requests.Single();
            Assert.EndsWith("/v1/messages", req.RequestUri.AbsolutePath.TrimEnd('/'));
            Assert.Contains(AnthropicModelClient.FallbackBeta, string.Join(",", req.Headers.GetValues("anthropic-beta")));
            // The SDK sends the API key as "Authorization: Bearer <key>" (documented in ClientOptions.ApiKey),
            // and it must be OUR key even when the environment carries other Anthropic credentials.
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(Creds.ApiKey, req.Headers.Authorization?.Parameter);
            Assert.Equal("api.anthropic.com", req.RequestUri.Host);

            var root = JsonDocument.Parse(body).RootElement;
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal("default", root.GetProperty("fallbacks").GetString());
            Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal("ephemeral", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("summarized", root.GetProperty("thinking").GetProperty("display").GetString());
            Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
            var tool = root.GetProperty("tools")[0];
            Assert.Equal("extrude", tool.GetProperty("name").GetString());
            Assert.True(tool.GetProperty("eager_input_streaming").GetBoolean());
            Assert.Equal("depth", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());

            var msgs = root.GetProperty("messages");
            Assert.Equal(3, msgs.GetArrayLength());
            Assert.Equal("image", msgs[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("sig".Length > 0 ? "s" : "", msgs[1].GetProperty("content")[0].GetProperty("signature").GetString());
            Assert.Equal(5, msgs[1].GetProperty("content")[1].GetProperty("input").GetProperty("depth").GetInt32());
            var tr = msgs[2].GetProperty("content")[0];
            Assert.Equal("tool_result", tr.GetProperty("type").GetString());
            Assert.Equal("image", tr.GetProperty("content")[1].GetProperty("type").GetString());
            Assert.DoesNotContain("ui only", body); // UI-only metadata never goes to the API
        }

        [Fact]
        public async Task HaikuGetsNoFallbackBeta()
        {
            var h = new FakeHandler { ResponseBody = StreamWithToolCall() };
            var client = new AnthropicModelClient(new HttpClient(h));
            var req = Request(ChatMessage.User(Block.OfText("hi")));
            req.Model = "claude-haiku-5-5";
            req.ExtendedThinking = false;
            await client.StreamAsync(req, Creds, null, CancellationToken.None);
            var (msg, body) = h.Requests.Single();
            Assert.False(msg.Headers.Contains("anthropic-beta") && msg.Headers.GetValues("anthropic-beta").Any(v => v.Contains("fallback")));
            var root = JsonDocument.Parse(body).RootElement;
            Assert.False(root.TryGetProperty("fallbacks", out _));
            Assert.Equal("omitted", root.GetProperty("thinking").GetProperty("display").GetString());
            Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        }

        [Fact]
        public async Task MapsUnauthorizedToAuthError()
        {
            var h = new FakeHandler
            {
                Status = HttpStatusCode.Unauthorized,
                ContentType = "application/json",
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}",
            };
            var client = new AnthropicModelClient(new HttpClient(h));
            var e = await Assert.ThrowsAsync<ModelApiException>(() =>
                client.StreamAsync(Request(ChatMessage.User(Block.OfText("hi"))), Creds, null, CancellationToken.None));
            Assert.Equal(ModelErrorKind.Auth, e.Kind);
            var v = await client.ValidateAsync(Creds, CancellationToken.None);
            Assert.False(v.Ok);
        }

        [Fact]
        public async Task ListsModels()
        {
            var h = new FakeHandler
            {
                ContentType = "application/json",
                ResponseBody = "{\"data\":[{\"type\":\"model\",\"id\":\"claude-opus-5-5\",\"display_name\":\"Claude Opus 5.5\",\"created_at\":\"2026-10-01T00:00:00Z\"}],\"has_more\":false,\"first_id\":\"claude-opus-5-5\",\"last_id\":\"claude-opus-5-5\"}",
            };
            var models = await new AnthropicModelClient(new HttpClient(h)).ListModelsAsync(Creds, CancellationToken.None);
            Assert.Equal("Claude Opus 5.5", models.Single().DisplayName);
        }
    }
}
