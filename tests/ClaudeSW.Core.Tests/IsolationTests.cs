using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Model;
using Xunit;

namespace ClaudeSW.Core.Tests
{
    public class IsolationTests
    {
        [Fact]
        public void RequestSurvivesTheDomainBoundary()
        {
            var req = new ModelRequest
            {
                Model = "claude-opus-5-5",
                SystemPrompt = "sys",
                ExtendedThinking = true,
                MaxTokens = 1234,
                Tools = new List<ToolSpec> { new ToolSpec { Name = "extrude", Description = "d", InputSchemaJson = new SchemaBuilder().Num("depth", "mm", true).Build(), Run = (a, c) => null } },
                Messages = new List<ChatMessage>
                {
                    ChatMessage.User(Block.OfText("hi"), Block.OfImage("image/png", "AAAA")),
                    ChatMessage.Assistant(new Block { Type = "thinking", Thinking = "t", Signature = "s" }, Block.OfToolUse("t1", "extrude", "{\"depth\":2}")),
                },
            };
            var back = RemoteModelHost.DeserializeRequest(RemoteModelHost.SerializeRequest(req));
            Assert.Equal("claude-opus-5-5", back.Model);
            Assert.True(back.ExtendedThinking);
            Assert.Equal(1234, back.MaxTokens);
            Assert.Equal(req.Tools[0].InputSchemaJson, back.Tools[0].InputSchemaJson);
            Assert.Null(back.Tools[0].Run); // delegates never cross
            Assert.Equal("s", back.Messages[1].Content[0].Signature);
            Assert.Equal("{\"depth\":2}", back.Messages[1].Content[1].InputJson);
            Assert.Equal("AAAA", back.Messages[0].Content[1].Base64);
        }

        [Fact]
        public void RepliesCarryErrorsAndCancellation()
        {
            var err = Json.Serialize(new RemoteModelHost.Reply<ModelResponse> { ErrorKind = ModelErrorKind.RateLimit, Error = "slow down" });
            var e = Assert.Throws<ModelApiException>(() => RemoteModelHost.ParseReply<ModelResponse>(err, CancellationToken.None));
            Assert.Equal(ModelErrorKind.RateLimit, e.Kind);
            var c = Json.Serialize(new RemoteModelHost.Reply<ModelResponse> { Cancelled = true });
            Assert.ThrowsAny<System.OperationCanceledException>(() => RemoteModelHost.ParseReply<ModelResponse>(c, CancellationToken.None));
            var ok = Json.Serialize(new RemoteModelHost.Reply<ModelResponse> { Value = new ModelResponse { StopReason = "end_turn", Content = { Block.OfText("x") } } });
            Assert.Equal("x", RemoteModelHost.ParseReply<ModelResponse>(ok, CancellationToken.None).Content.Single().Text);
        }
    }
}
