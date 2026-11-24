using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Auth;
using ClaudeSW.Core.Model;
using ClaudeSW.Core.Storage;

namespace ClaudeSW.AddIn.Host
{
    /// <summary>
    /// Mediates between the web UI (JSON messages), the agent loop (background thread) and
    /// SOLIDWORKS (UI thread). Message handlers run on the SOLIDWORKS UI thread.
    /// </summary>
    public sealed class AppController
    {
        private readonly SwSession _sw;
        private readonly TaskPaneHost _host;
        private readonly ConversationStore _store = new ConversationStore();
        private readonly SettingsStore _settingsStore = new SettingsStore();
        private readonly IAuthProvider _auth;
        private readonly IModelClient _client;
        private readonly SwToolExecutor _tools;
        private readonly AgentLoop _loop;
        private readonly bool _demo;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _confirms = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();

        private Settings _settings;
        private Conversation _conv = new Conversation();
        private CancellationTokenSource _cts;
        private bool _busy;
        private IReadOnlyList<ModelInfoLite> _models = KnownModels.Fallback;
        private string _lastStatusJson;
        private string _lastThemeJson;

        public string Version { get; } = typeof(AppController).Assembly.GetName().Version.ToString(3);
        public bool Busy => _busy;
        public Conversation Current => _conv;
        public SwToolExecutor Tools => _tools;
        public Settings CurrentSettings => _settings;
        public SwSession Session => _sw;

        /// <summary>Show what an external client (Claude Code / Desktop) just did, in the pane.</summary>
        public void ExternalActivity(string client, string tool, string summary, bool isError) =>
            Post("external", new { client, tool, summary, isError });

        public AppController(SwSession sw, TaskPaneHost host)
        {
            _sw = sw;
            _host = host;
            _settings = _settingsStore.Load();
            var anthropic = CreateModelClient();
            _auth = new ApiKeyAuthProvider(new DpapiSecretStore(AppPaths.Secrets), (ICredentialValidator)anthropic);
            var demoScript = Environment.GetEnvironmentVariable("CLAUDESW_DEMO_SCRIPT");
            _client = anthropic;
            if (!string.IsNullOrEmpty(demoScript) && File.Exists(demoScript))
            {
                try
                {
                    var outDir = System.Environment.GetEnvironmentVariable("CLAUDESW_DEMO_OUT") ?? Path.Combine(Path.GetTempPath(), "ClaudeForSolidWorks-demo");
                    Directory.CreateDirectory(outDir);
                    _client = ScriptedModelClient.FromJson(File.ReadAllText(demoScript).Replace("%DEMO_OUT%", outDir.Replace('\\', '/')));
                    _demo = true;
                    Log.Info("Demo mode: replaying " + demoScript);
                }
                catch (Exception e) { Log.Error("Demo script unreadable; using the real API", e); }
            }
            _tools = new SwToolExecutor(sw, host, () => _settings.AllowAdvancedApi);
            _loop = new AgentLoop(_client, _tools, () => _demo ? new AuthCredentials { ApiKey = "demo" } : _auth.GetCredentials());
            _host.MessageReceived += json => Handle(json);
        }

        private bool SignedIn => _demo || _auth.IsSignedIn;

        /// <summary>
        /// The SDK runs in a private AppDomain so other add-ins' copies of shared .NET libraries
        /// can't break it. Falls back to in-domain if the private domain can't be created.
        /// </summary>
        private static IModelClient CreateModelClient()
        {
            var dir = AssemblyResolver.Dir;
            var config = Path.Combine(dir, "ClaudeSW.AddIn.dll.config");
            try
            {
                var c = new IsolatedModelClient(dir, File.Exists(config) ? config : null);
                Log.Info("Model client: isolated AppDomain" + (File.Exists(config) ? "" : " (no binding redirect file!)"));
                return c;
            }
            catch (Exception e)
            {
                Log.Error("Isolated model client unavailable; using in-domain client", e);
                AnthropicModelClient.Diagnostics = msg => Log.Warn(msg);
                return new AnthropicModelClient();
            }
        }

        // ------------------------------------------------------------------ outbound

        public void Post(string type, object payload = null)
        {
            var dict = new Dictionary<string, object> { ["type"] = type };
            if (payload != null)
            {
                using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Json.Options)))
                    foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            }
            _host.Post(JsonSerializer.Serialize(dict, Json.Options));
        }

        private void Toast(string message, string kind = "info") => Post("toast", new { message, kind });

        private object SettingsDto() => new
        {
            model = _settings.Model,
            extendedThinking = _settings.ExtendedThinking,
            theme = _settings.Theme,
            autoContext = _settings.AutoContext,
            allowAdvancedApi = _settings.AllowAdvancedApi,
            confirmDestructive = _settings.ConfirmDestructive,
            allowExternalClients = _settings.AllowExternalClients,
            mcpExe = Path.Combine(AssemblyResolver.Dir, "ClaudeSW.Mcp.exe"),
            customInstructions = _settings.CustomInstructions,
        };

        private object AuthDto(string error = null, bool busy = false) => new
        {
            signedIn = SignedIn,
            provider = _demo ? "demo" : _auth.Id,
            hint = _demo ? "Demo mode (scripted, offline)" : _auth.Hint,
            error,
            busy,
        };

        private void PostConversation() => Post("conversation", new { conversation = _conv, busy = _busy });
        private void PostConversations(string search = null) => Post("conversations", new { items = _store.List(search), search });

        // ------------------------------------------------------------------ inbound

        public void Handle(string json)
        {
            JsonElement m;
            try { using (var d = JsonDocument.Parse(json)) m = d.RootElement.Clone(); }
            catch (JsonException) { return; }
            var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
            string S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool B(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            try
            {
                switch (type)
                {
                    case "ready": OnReady(); break;
                    case "auth.signIn": _ = SignInAsync(S("key")); break;
                    case "auth.signOut": _auth.SignOut(); Post("auth", AuthDto()); break;
                    case "chat.send": _ = SendAsync(S("text"), m.TryGetProperty("attachments", out var att) ? att : default, B("useSelection")); break;
                    case "chat.stop": Stop(); break;
                    case "chat.regenerate": _ = RegenerateAsync(); break;
                    case "chat.edit": _ = EditAsync(m.GetProperty("messageIndex").GetInt32(), S("text")); break;
                    case "conv.new": NewConversation(); break;
                    case "conv.open": OpenConversation(S("id")); break;
                    case "conv.rename": _store.Rename(S("id"), S("title")); if (_conv.Id == S("id")) { _conv.Title = S("title"); } PostConversations(); break;
                    case "conv.delete": DeleteConversation(S("id")); break;
                    case "conv.list": PostConversations(S("search")); break;
                    case "settings.set": UpdateSettings(m.GetProperty("settings")); break;
                    case "models.refresh": _ = RefreshModelsAsync(); break;
                    case "confirm.answer":
                        if (_confirms.TryRemove(S("id") ?? "", out var tcs)) tcs.TrySetResult(B("approved"));
                        break;
                    case "undo.last": _ = UndoLastAsync(); break;
                    case "context.get": PushStatus(force: true); break;
                    case "link.open": TaskPaneHost.OpenExternal(S("url")); break;
                    case "log": Log.Info("UI: " + S("message")); break;
                    case "mcp.register": _ = RegisterMcpAsync(S("target")); break;
                    case "mcp.status": PostMcpStatus(); break;
                }
            }
            catch (Exception e)
            {
                Log.Error("Handling " + type, e);
                Post("error", new { kind = "internal", message = "Something went wrong: " + e.Message });
            }
        }

        private void OnReady()
        {
            Post("init", new
            {
                version = Version,
                auth = AuthDto(),
                settings = SettingsDto(),
                models = _models,
                conversations = _store.List(),
                conversation = _conv,
                demo = _demo,
                theme = ThemeDto(),
            });
            PushStatus(force: true);
            PostMcpStatus();
            if (SignedIn && !_demo) _ = RefreshModelsAsync();
        }

        private object ThemeDto()
        {
            var t = _sw.DetectTheme();
            return new { mode = t?.mode, background = t?.background };
        }

        /// <summary>Called periodically on the UI thread: pushes doc/selection status and theme when changed.</summary>
        public void PushStatus(bool force = false)
        {
            try
            {
                var json = JsonSerializer.Serialize(_sw.QuickStatus());
                if (force || json != _lastStatusJson)
                {
                    _lastStatusJson = json;
                    Post("context", JsonDocument.Parse(json).RootElement);
                }
                var theme = JsonSerializer.Serialize(ThemeDto());
                if (force || theme != _lastThemeJson)
                {
                    _lastThemeJson = theme;
                    Post("theme", JsonDocument.Parse(theme).RootElement);
                }
            }
            catch (Exception e) { Log.Warn("Status push failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ auth & models

        private async Task SignInAsync(string key)
        {
            Post("auth", AuthDto(busy: true));
            var r = await _auth.SignInAsync(key, CancellationToken.None);
            Post("auth", AuthDto(error: r.Ok ? null : r.Error));
            if (r.Ok) await RefreshModelsAsync();
        }

        private async Task RefreshModelsAsync()
        {
            if (_demo || !_auth.IsSignedIn) { Post("models", new { items = _models }); return; }
            try
            {
                var list = await _client.ListModelsAsync(_auth.GetCredentials(), CancellationToken.None);
                // Current models first in a sensible order, then anything else the account has.
                var order = KnownModels.Fallback.Select(f => f.Id).ToList();
                _models = list.OrderBy(x => order.IndexOf(x.Id) < 0 ? 100 : order.IndexOf(x.Id)).ToList();
                if (_models.Count == 0) _models = KnownModels.Fallback;
            }
            catch (ModelApiException e)
            {
                Log.Warn("Model list failed: " + e.Message);
                if (e.Kind == ModelErrorKind.Auth) Post("auth", AuthDto(error: e.Message));
            }
            Post("models", new { items = _models });
        }

        private const string McpServerName = "solidworks-addin";

        /// <summary>Is the bridge registered with Claude Code (user scope) / Claude Desktop, and are they installed?</summary>
        private static object McpStatus()
        {
            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var roaming = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);

            bool HasServer(string file, string section)
            {
                try
                {
                    if (!File.Exists(file)) return false;
                    using (var d = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true }))
                        return d.RootElement.TryGetProperty(section, out var servers) && servers.ValueKind == JsonValueKind.Object &&
                               servers.TryGetProperty(McpServerName, out _);
                }
                catch { return false; }
            }

            var claudeJson = Path.Combine(home, ".claude.json");
            var codeInstalled = File.Exists(claudeJson) || File.Exists(Path.Combine(home, ".local", "bin", "claude.exe"));
            var desktopFiles = new List<string> { Path.Combine(roaming, "Claude", "claude_desktop_config.json") };
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
                desktopFiles.AddRange(Directory.GetDirectories(packages, "Claude_*").Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json")));
            var desktopInstalled = desktopFiles.Any(f => Directory.Exists(Path.GetDirectoryName(f)));
            return new
            {
                code = HasServer(claudeJson, "mcpServers"),
                desktop = desktopFiles.Any(f => HasServer(f, "mcpServers")),
                codeInstalled,
                desktopInstalled,
            };
        }

        private void PostMcpStatus()
        {
            try { Post("mcp.status", McpStatus()); } catch (Exception e) { Log.Warn("MCP status: " + e.Message); }
        }

        /// <summary>Runs ClaudeSW.Mcp.exe --register-claude-code / --register-claude-desktop for the user.</summary>
        private async Task RegisterMcpAsync(string target)
        {
            var exe = Path.Combine(AssemblyResolver.Dir, "ClaudeSW.Mcp.exe");
            if (!File.Exists(exe)) { Toast("ClaudeSW.Mcp.exe is missing from the install folder. Reinstall ClaudeWorks.", "error"); return; }
            var arg = target == "desktop" ? "--register-claude-desktop" : "--register-claude-code";
            var result = await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, arg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var o = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
                    p.WaitForExit(60000);
                    return (p.ExitCode, o);
                }
            });
            Log.Info($"MCP register {target}: {result.Item1} {result.Item2}");
            PostMcpStatus();
            var who = target == "desktop" ? "Claude Desktop can now control SOLIDWORKS. Restart Claude Desktop, then chat there." : "Claude Code can now control SOLIDWORKS. Start a new Claude Code session and chat there.";
            Toast(result.Item1 == 0 ? who : "Couldn't set it up automatically: " + result.Item2, result.Item1 == 0 ? "info" : "error");
        }

        // ------------------------------------------------------------------ conversations

        private void NewConversation()
        {
            if (_busy) { Toast("Wait for Claude to finish, or press Stop."); return; }
            _conv = new Conversation { Model = _settings.Model };
            PostConversation();
        }

        private void OpenConversation(string id)
        {
            if (_busy) { Toast("Wait for Claude to finish, or press Stop."); return; }
            var c = _store.Load(id);
            if (c == null) { Toast("That chat no longer exists."); PostConversations(); return; }
            _conv = c;
            PostConversation();
        }

        private void DeleteConversation(string id)
        {
            if (_busy && _conv.Id == id) { Toast("Stop the current reply before deleting this chat."); return; }
            _store.Delete(id);
            if (_conv.Id == id) { _conv = new Conversation { Model = _settings.Model }; PostConversation(); }
            PostConversations();
        }

        private void UpdateSettings(JsonElement s)
        {
            if (s.TryGetProperty("model", out var v) && v.ValueKind == JsonValueKind.String) _settings.Model = v.GetString();
            if (s.TryGetProperty("extendedThinking", out v)) _settings.ExtendedThinking = v.ValueKind == JsonValueKind.True;
            if (s.TryGetProperty("theme", out v) && v.ValueKind == JsonValueKind.String) _settings.Theme = v.GetString();
            if (s.TryGetProperty("autoContext", out v)) _settings.AutoContext = v.ValueKind == JsonValueKind.True;
            if (s.TryGetProperty("allowAdvancedApi", out v)) _settings.AllowAdvancedApi = v.ValueKind == JsonValueKind.True;
            if (s.TryGetProperty("confirmDestructive", out v)) _settings.ConfirmDestructive = v.ValueKind == JsonValueKind.True;
            if (s.TryGetProperty("allowExternalClients", out v)) _settings.AllowExternalClients = v.ValueKind == JsonValueKind.True;
            if (s.TryGetProperty("customInstructions", out v) && v.ValueKind == JsonValueKind.String) _settings.CustomInstructions = v.GetString();
            _settingsStore.Save(_settings);
            Post("settings", new { settings = SettingsDto() });
        }

        // ------------------------------------------------------------------ chat

        private Block ContextBlock(bool useSelection)
        {
            if (!_settings.AutoContext && !useSelection) return null;
            try
            {
                var text = _sw.BuildContext(useSelection, out _);
                return new Block { Type = Block.TextType, Text = text, UiHidden = true };
            }
            catch (Exception e)
            {
                Log.Warn("Context failed: " + e.Message);
                return null;
            }
        }

        private static IEnumerable<Block> Attachments(JsonElement att)
        {
            if (att.ValueKind != JsonValueKind.Array) yield break;
            foreach (var a in att.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : "file";
                var mt = a.TryGetProperty("mediaType", out var m) ? m.GetString() : "";
                var b64 = a.TryGetProperty("base64", out var b) ? b.GetString() : null;
                if (string.IsNullOrEmpty(b64)) continue;
                if (mt.StartsWith("image/")) yield return Block.OfImage(mt, b64, name);
                else if (mt == "application/pdf") yield return Block.OfDocument(b64, name);
                else
                {
                    string text;
                    try { text = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { continue; }
                    yield return new Block { Type = Block.TextType, Text = $"<attached_file name=\"{name}\">\n{text}\n</attached_file>", FileName = name };
                }
            }
        }

        private async Task SendAsync(string text, JsonElement attachments, bool useSelection)
        {
            if (_busy) { Toast("Claude is still working. Press Stop to interrupt."); return; }
            if (!SignedIn) { Post("auth", AuthDto(error: "Sign in to start chatting.")); return; }
            var blocks = new List<Block>();
            var ctx = ContextBlock(useSelection);
            if (ctx != null) blocks.Add(ctx);
            if (!string.IsNullOrWhiteSpace(text)) blocks.Add(Block.OfText(text.Trim()));
            blocks.AddRange(Attachments(attachments));
            if (!blocks.Any(b => b.UiHidden != true)) return;
            if (_conv.Model == null) _conv.Model = _settings.Model;
            _conv.Messages.Add(new ChatMessage { Role = "user", Content = blocks });
            Post("message", new { index = _conv.Messages.Count - 1, message = _conv.Messages.Last() });
            await RunTurnAsync(text);
        }

        private int LastHumanIndex() => _conv.Messages.FindLastIndex(x => x.IsHumanTurn);

        private void RefreshContext(ChatMessage human)
        {
            var hidden = human.Content.FindIndex(b => b.UiHidden == true);
            var ctx = ContextBlock(false);
            if (hidden >= 0 && ctx != null) human.Content[hidden] = ctx;
        }

        private async Task RegenerateAsync()
        {
            if (_busy) return;
            var i = LastHumanIndex();
            if (i < 0) return;
            _conv.Messages.RemoveRange(i + 1, _conv.Messages.Count - i - 1);
            RefreshContext(_conv.Messages[i]);
            PostConversation();
            await RunTurnAsync("regenerate");
        }

        private async Task EditAsync(int index, string text)
        {
            if (_busy || index < 0 || index >= _conv.Messages.Count || !_conv.Messages[index].IsHumanTurn || string.IsNullOrWhiteSpace(text)) return;
            var msg = _conv.Messages[index];
            _conv.Messages.RemoveRange(index + 1, _conv.Messages.Count - index - 1);
            var visible = msg.Content.FindIndex(b => b.Type == Block.TextType && b.UiHidden != true && b.FileName == null);
            if (visible >= 0) msg.Content[visible] = Block.OfText(text.Trim());
            else msg.Content.Add(Block.OfText(text.Trim()));
            RefreshContext(msg);
            PostConversation();
            await RunTurnAsync(text);
        }

        private void Stop()
        {
            _cts?.Cancel();
            foreach (var kv in _confirms) kv.Value.TrySetResult(false);
            _confirms.Clear();
        }

        private async Task RunTurnAsync(string label)
        {
            _busy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Post("turn.start", null);
            var opts = new AgentOptions
            {
                Model = _demo ? "demo" : _settings.Model,
                ExtendedThinking = _settings.ExtendedThinking,
                SystemPrompt = SystemPrompt.Build(_settings.CustomInstructions),
                MaxTokens = _settings.MaxTokens,
            };
            _conv.Model = opts.Model;
            var observer = new Observer(this, _settings.ConfirmDestructive);
            TurnResult result;
            var grouped = false;
            try
            {
                await _tools.BeginTurnAsync(label);
                result = await Task.Run(() => _loop.RunAsync(_conv, opts, observer, ct));
            }
            catch (Exception e)
            {
                Log.Error("Turn failed", e);
                result = new TurnResult { Outcome = TurnOutcome.Failed, Message = "Unexpected error: " + e.Message };
            }
            finally
            {
                try { grouped = await _tools.EndTurnAsync(); } catch (Exception e) { Log.Warn("Undo grouping end failed: " + e.Message); }
                _busy = false;
            }

            if (_conv.Title == "New chat") _conv.Title = AgentLoop.TitleFrom(_conv);
            _conv.Updated = DateTimeOffset.UtcNow;
            try { _store.Save(_conv); } catch (Exception e) { Log.Error("Saving conversation failed", e); }
            Post("turn.end", new
            {
                outcome = result.Outcome.ToString().ToLowerInvariant(),
                message = result.Message,
                errorKind = result.Error?.Kind.ToString().ToLowerInvariant(),
                canUndo = grouped,
                usage = _conv.LastUsage,
                totalUsage = _conv.TotalUsage,
                title = _conv.Title,
            });
            PostConversations();
            PushStatus(force: true);
            if (result.Error?.Kind == ModelErrorKind.Auth) Post("auth", AuthDto(error: result.Error.Message));
        }

        private async Task UndoLastAsync()
        {
            if (_busy) { Toast("Wait for Claude to finish first."); return; }
            try { Toast(await _tools.UndoLastAsync()); }
            catch (Exception e) { Toast("Undo failed: " + e.Message, "error"); }
            Post("undo.state", new { canUndo = _tools.Undo.CanUndo });
        }

        // ------------------------------------------------------------------ agent events -> UI

        private sealed class Observer : IAgentObserver
        {
            private readonly AppController _c;
            private readonly bool _confirm;
            public Observer(AppController c, bool confirm) { _c = c; _confirm = confirm; }

            public void OnRequestStart(int iteration) => _c.Post("stream.request", new { iteration });
            public void OnMessageStart(string model) => _c.Post("stream.start", new { model });
            public void OnBlockStart(int index, Block block) => _c.Post("stream.block", new { index, block });
            public void OnTextDelta(int index, string text) => _c.Post("stream.text", new { index, text });
            public void OnThinkingDelta(int index, string text) => _c.Post("stream.thinking", new { index, text });
            public void OnToolInputDelta(int index, string partialJson) { }
            public void OnBlockStop(int index) { }

            public void OnMessageAppended(ChatMessage message) =>
                _c.Post("message", new { index = _c._conv.Messages.IndexOf(message), message });

            public void OnToolStart(Block toolUse, string description) =>
                _c.Post("tool.start", new { id = toolUse.Id, name = toolUse.Name, description });

            public void OnToolEnd(string toolUseId, ToolOutput output) => _c.Post("tool.end", new
            {
                id = toolUseId,
                isError = output.IsError,
                summary = output.Summary,
            });

            public async Task<bool> ConfirmAsync(string toolUseId, string toolName, string question, CancellationToken ct)
            {
                if (!_confirm) return true;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _c._confirms[toolUseId] = tcs;
                _c.Post("confirm", new { id = toolUseId, tool = toolName, question });
                using (ct.Register(() => tcs.TrySetResult(false)))
                {
                    var ok = await tcs.Task.ConfigureAwait(false);
                    _c._confirms.TryRemove(toolUseId, out _);
                    _c.Post("confirm.done", new { id = toolUseId, approved = ok });
                    return ok;
                }
            }

            public void OnUsage(Usage last, Usage total) => _c.Post("usage", new { last, total });
        }
    }
}
