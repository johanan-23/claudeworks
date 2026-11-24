using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;
using ClaudeSW.Core.Storage;
using Xunit;

namespace ClaudeSW.Core.Tests
{
    public class FakeValidator : ICredentialValidator
    {
        public string Accept { get; set; }
        public int Calls { get; private set; }
        public Task<AuthResult> ValidateAsync(AuthCredentials c, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(c.ApiKey == Accept ? AuthResult.Success() : AuthResult.Fail("rejected"));
        }
    }

    public class AuthTests
    {
        private const string GoodKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ-1234";

        [Fact]
        public async Task SignInStoresValidatedKey()
        {
            var store = new MemorySecretStore();
            var p = new ApiKeyAuthProvider(store, new FakeValidator { Accept = GoodKey });
            Assert.False(p.IsSignedIn);
            var r = await p.SignInAsync("  " + GoodKey + " ", CancellationToken.None);
            Assert.True(r.Ok);
            Assert.True(p.IsSignedIn);
            Assert.Equal(GoodKey, p.GetCredentials().ApiKey);
            Assert.Equal(GoodKey, store.Load(ApiKeyAuthProvider.SecretName));
            // a new provider instance picks up the stored key
            Assert.True(new ApiKeyAuthProvider(store, null).IsSignedIn);
        }

        [Fact]
        public async Task RejectedKeyIsNotStored()
        {
            var store = new MemorySecretStore();
            var p = new ApiKeyAuthProvider(store, new FakeValidator { Accept = "other" });
            var r = await p.SignInAsync(GoodKey, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.False(p.IsSignedIn);
            Assert.Null(store.Load(ApiKeyAuthProvider.SecretName));
        }

        [Fact]
        public async Task ShapeErrorsSkipNetwork()
        {
            var v = new FakeValidator { Accept = GoodKey };
            var p = new ApiKeyAuthProvider(new MemorySecretStore(), v);
            Assert.False((await p.SignInAsync("short", CancellationToken.None)).Ok);
            Assert.False((await p.SignInAsync("sk-ant-has spaces in it 12345", CancellationToken.None)).Ok);
            Assert.Equal(0, v.Calls);
        }

        [Fact]
        public async Task SignOutDeletes()
        {
            var store = new MemorySecretStore();
            var p = new ApiKeyAuthProvider(store, null);
            await p.SignInAsync(GoodKey, CancellationToken.None);
            p.SignOut();
            Assert.False(p.IsSignedIn);
            Assert.Null(p.GetCredentials());
            Assert.Null(store.Load(ApiKeyAuthProvider.SecretName));
        }

        [Fact]
        public void HintNeverRevealsKey()
        {
            var hint = ApiKeyAuthProvider.Mask(GoodKey);
            Assert.Equal("sk-ant-…1234", hint);
            Assert.DoesNotContain("ABCDEF", hint);
            Assert.DoesNotContain("ABCD", new AuthCredentials { ApiKey = GoodKey }.ToString());
        }

        [Fact]
        public void DpapiRoundTrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "csw-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var s = new DpapiSecretStore(dir);
                s.Save("k", GoodKey);
                var raw = File.ReadAllBytes(Path.Combine(dir, "k.bin"));
                Assert.DoesNotContain("sk-ant", System.Text.Encoding.UTF8.GetString(raw));
                Assert.Equal(GoodKey, s.Load("k"));
                s.Delete("k");
                Assert.Null(s.Load("k"));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void LogRedactsKeys() =>
            Assert.Equal("key=sk-ant-*** ok", Log.Redact("key=" + GoodKey + " ok"));
    }

    public class StorageTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "csw-conv-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        [Fact]
        public void SaveListSearchRenameDelete()
        {
            var store = new ConversationStore(_dir);
            var a = new Conversation { Title = "Bracket", Updated = DateTimeOffset.UtcNow.AddMinutes(-5) };
            a.Messages.Add(ChatMessage.User(Block.OfText("make a bracket")));
            a.Messages.Add(ChatMessage.Assistant(Block.OfText("Built an L bracket 40 x 40 mm.")));
            var b = new Conversation { Title = "Plate", Updated = DateTimeOffset.UtcNow };
            b.Messages.Add(ChatMessage.User(new Block { Type = Block.TextType, Text = "<solidworks_context>bracket</solidworks_context>", UiHidden = true }, Block.OfText("plate")));
            store.Save(a);
            store.Save(b);

            var all = store.List();
            Assert.Equal(new[] { "Plate", "Bracket" }, all.ConvertAll(s => s.Title));
            Assert.Equal("Built an L bracket 40 x 40 mm.", all[1].Preview);

            // hidden context is not searchable
            var hits = store.List("bracket");
            Assert.Single(hits);
            Assert.Equal(a.Id, hits[0].Id);

            store.Rename(a.Id, "  L bracket ");
            Assert.Equal("L bracket", store.Load(a.Id).Title);
            Assert.True(store.Delete(a.Id));
            Assert.Null(store.Load(a.Id));
            Assert.Single(store.List());
        }

        [Fact]
        public void RejectsPathTraversalIds() =>
            Assert.Throws<ArgumentException>(() => new ConversationStore(_dir).Load("..\\evil"));

        [Fact]
        public void SettingsRoundTripAndDefaults()
        {
            var path = Path.Combine(_dir, "settings.json");
            var ss = new SettingsStore(path);
            Assert.Equal("claude-opus-5-5", ss.Load().Model);
            ss.Save(new Settings { Model = "claude-sonnet-5-5", ExtendedThinking = true });
            var s = ss.Load();
            Assert.Equal("claude-sonnet-5-5", s.Model);
            Assert.True(s.ExtendedThinking);
            File.WriteAllText(path, "{ not json");
            Assert.Equal("claude-opus-5-5", ss.Load().Model);
        }
    }
}
