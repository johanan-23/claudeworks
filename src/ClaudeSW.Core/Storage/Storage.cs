using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Model;

namespace ClaudeSW.Core.Storage
{
    public static class AppPaths
    {
        /// <summary>Overridable for tests.</summary>
        public static string Root { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeForSolidWorks");

        public static string Conversations => Path.Combine(Root, "conversations");
        public static string Secrets => Path.Combine(Root, "secrets");
        public static string Logs => Path.Combine(Root, "logs");
        public static string Settings => Path.Combine(Root, "settings.json");
        public static string Temp => Path.Combine(Path.GetTempPath(), "ClaudeForSolidWorks");
    }

    /// <summary>Tiny rolling diagnostics log. Redacts anything that looks like an API key.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static readonly Regex KeyPattern = new Regex(@"sk-ant-[A-Za-z0-9_\-]{6,}", RegexOptions.Compiled);
        public static string FilePath => Path.Combine(AppPaths.Logs, "addin.log");

        public static string Redact(string s) => s == null ? null : KeyPattern.Replace(s, "sk-ant-***");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception e = null) =>
            Write("ERROR", e == null ? message : message + ": " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2_000_000)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message)}\r\n", Encoding.UTF8);
                }
            }
            catch { /* logging must never throw */ }
        }
    }

    public sealed class Settings
    {
        [JsonPropertyName("model")] public string Model { get; set; } = KnownModels.Default;
        [JsonPropertyName("extended_thinking")] public bool ExtendedThinking { get; set; }
        /// <summary>"system" (follow SOLIDWORKS), "light" or "dark".</summary>
        [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
        [JsonPropertyName("auto_context")] public bool AutoContext { get; set; } = true;
        /// <summary>Expose the guarded generic API escape hatch tool.</summary>
        [JsonPropertyName("allow_advanced_api")] public bool AllowAdvancedApi { get; set; }
        [JsonPropertyName("confirm_destructive")] public bool ConfirmDestructive { get; set; } = true;
        /// <summary>Let local MCP clients (Claude Code / Claude Desktop) use the SOLIDWORKS tools via ClaudeSW.Mcp.exe.</summary>
        [JsonPropertyName("allow_external_clients")] public bool AllowExternalClients { get; set; } = true;
        [JsonPropertyName("custom_instructions")] public string CustomInstructions { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 32000;
    }

    public sealed class SettingsStore
    {
        private readonly string _path;
        public SettingsStore(string path = null) { _path = path ?? AppPaths.Settings; }

        public Settings Load()
        {
            try
            {
                if (File.Exists(_path)) return Json.Deserialize<Settings>(File.ReadAllText(_path)) ?? new Settings();
            }
            catch (Exception e) { Log.Warn("Settings unreadable, using defaults: " + e.Message); }
            return new Settings();
        }

        public void Save(Settings s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            AtomicWrite(_path, Json.Serialize(s));
        }

        internal static void AtomicWrite(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    /// <summary>One JSON file per conversation under %APPDATA%\ClaudeForSolidWorks\conversations.</summary>
    public sealed class ConversationStore
    {
        private readonly string _dir;
        public ConversationStore(string directory = null) { _dir = directory ?? AppPaths.Conversations; }

        private string PathFor(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Bad conversation id");
            return Path.Combine(_dir, id + ".json");
        }

        public void Save(Conversation c)
        {
            Directory.CreateDirectory(_dir);
            SettingsStore.AtomicWrite(PathFor(c.Id), Json.Serialize(c));
        }

        public Conversation Load(string id)
        {
            var p = PathFor(id);
            return File.Exists(p) ? Json.Deserialize<Conversation>(File.ReadAllText(p)) : null;
        }

        public bool Delete(string id)
        {
            var p = PathFor(id);
            if (!File.Exists(p)) return false;
            File.Delete(p);
            return true;
        }

        public void Rename(string id, string title)
        {
            var c = Load(id) ?? throw new InvalidOperationException("Conversation not found");
            c.Title = string.IsNullOrWhiteSpace(title) ? c.Title : title.Trim();
            Save(c);
        }

        public List<ConversationSummary> List(string search = null)
        {
            var result = new List<ConversationSummary>();
            if (!Directory.Exists(_dir)) return result;
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var c = Json.Deserialize<Conversation>(File.ReadAllText(f));
                    if (c == null) continue;
                    if (!string.IsNullOrWhiteSpace(search) && !Matches(c, search)) continue;
                    result.Add(new ConversationSummary { Id = c.Id, Title = c.Title, Updated = c.Updated, Preview = Preview(c) });
                }
                catch (Exception e) { Log.Warn($"Skipping unreadable conversation {Path.GetFileName(f)}: {e.Message}"); }
            }
            return result.OrderByDescending(s => s.Updated).ToList();
        }

        private static bool Matches(Conversation c, string q)
        {
            if (c.Title?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return c.Messages.SelectMany(m => m.Content)
                .Any(b => b.Type == Block.TextType && b.UiHidden != true && b.Text?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Preview(Conversation c)
        {
            var last = c.Messages.LastOrDefault(m => m.Role == "assistant" && m.Content.Any(b => b.Type == Block.TextType && !string.IsNullOrWhiteSpace(b.Text)));
            var text = last?.Content.Last(b => b.Type == Block.TextType && !string.IsNullOrWhiteSpace(b.Text)).Text ?? "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 90 ? text.Substring(0, 89) + "…" : text;
        }
    }
}
