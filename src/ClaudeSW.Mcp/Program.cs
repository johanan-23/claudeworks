using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;

namespace ClaudeSW.Mcp
{
    /// <summary>
    /// MCP server (stdio, JSON-RPC 2.0, one message per line) exposing the ClaudeWorks
    /// add-in's tools to Claude Code / Claude Desktop, so they can drive SOLIDWORKS on the user's own
    /// Claude plan without an API key. stdout carries protocol only; diagnostics go to stderr.
    ///
    ///   ClaudeSW.Mcp.exe                         run as an MCP server (what clients launch)
    ///   ClaudeSW.Mcp.exe --register-claude-code  add it to Claude Code (user scope)
    ///   ClaudeSW.Mcp.exe --register-claude-desktop  add it to Claude Desktop's config
    ///   ClaudeSW.Mcp.exe --unregister            remove both registrations
    ///   ClaudeSW.Mcp.exe --check                 test the connection to SOLIDWORKS
    /// </summary>
    internal static class Program
    {
        public const string ServerName = "solidworks-addin";
        private const string PipeName = "ClaudeForSolidWorks.Tools";
        private const string LaunchTool = "launch_solidworks";
        private static string _client = "MCP client";
        private static PipeLink _link = new PipeLink();

        private static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--register-claude-code": return Registration.ClaudeCode(true);
                    case "--register-claude-desktop": return Registration.ClaudeDesktop(true);
                    case "--unregister": Registration.ClaudeCode(false); Registration.ClaudeDesktop(false); return 0;
                    case "--check": return Check();
                    case "--help": case "-h": case "/?":
                        Console.WriteLine("ClaudeSW.Mcp.exe [--register-claude-code | --register-claude-desktop | --unregister | --check]");
                        return 0;
                }
            }
            Serve();
            return 0;
        }

        private static int Check()
        {
            var hello = _link.Request(new JsonObject { ["op"] = "hello" }, connectMs: 2000);
            if (hello == null) { Console.WriteLine("SOLIDWORKS with the Claude add-in is not running (or the pipe is unavailable)."); return 1; }
            Console.WriteLine("Connected: " + hello.ToJsonString());
            var list = _link.Request(new JsonObject { ["op"] = "list" }, connectMs: 2000);
            Console.WriteLine("Tools: " + (list?["tools"] as JsonArray)?.Count);
            return 0;
        }

        // ------------------------------------------------------------------ MCP loop

        private static void Serve()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode msg;
                try { msg = JsonNode.Parse(line); }
                catch (Exception e) { Write(stdout, ErrorReply(null, -32700, "Parse error: " + e.Message)); continue; }
                var id = msg?["id"]?.DeepClone();
                var method = (string)msg?["method"];
                if (method == null) continue; // a response to something we never send
                try
                {
                    var result = Dispatch(method, msg["params"] as JsonObject);
                    if (id == null) continue; // notification
                    Write(stdout, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() });
                }
                catch (MethodNotFound)
                {
                    if (id != null) Write(stdout, ErrorReply(id, -32601, "Method not found: " + method));
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    if (id != null) Write(stdout, ErrorReply(id, -32603, e.Message));
                }
            }
        }

        private sealed class MethodNotFound : Exception { }

        private static readonly object WriteGate = new object();
        private static void Write(StreamWriter w, JsonNode n) { lock (WriteGate) w.WriteLine(n.ToJsonString()); }

        private static JsonObject ErrorReply(JsonNode id, int code, string message) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

        private static JsonNode Dispatch(string method, JsonObject p)
        {
            switch (method)
            {
                case "initialize":
                    _client = (string)p?["clientInfo"]?["name"] ?? _client;
                    var requested = (string)p?["protocolVersion"];
                    var supported = new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
                    var tools = FetchTools(out var instructions);
                    return new JsonObject
                    {
                        ["protocolVersion"] = supported.Contains(requested) ? requested : "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "ClaudeWorks", ["version"] = typeof(Program).Assembly.GetName().Version.ToString(3) },
                        ["instructions"] = instructions,
                    };
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "ping":
                    return new JsonObject();
                case "tools/list":
                    return new JsonObject { ["tools"] = FetchTools(out _) };
                case "tools/call":
                    return CallTool((string)p?["name"], p?["arguments"] as JsonObject ?? new JsonObject());
                case "resources/list": return new JsonObject { ["resources"] = new JsonArray() };
                case "prompts/list": return new JsonObject { ["prompts"] = new JsonArray() };
                default:
                    throw new MethodNotFound();
            }
        }

        private static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeForSolidWorks", "mcp-tools.json");

        /// <summary>Live list from SOLIDWORKS, else the list the add-in cached on its last start.</summary>
        private static JsonArray FetchTools(out string instructions)
        {
            JsonObject source = _link.Request(new JsonObject { ["op"] = "list" }, connectMs: 1500) as JsonObject;
            if (source == null || source["ok"]?.GetValue<bool>() != true)
            {
                try { source = File.Exists(CachePath) ? JsonNode.Parse(File.ReadAllText(CachePath)) as JsonObject : null; }
                catch { source = null; }
            }
            instructions = (string)source?["instructions"] ??
                "Tools for the user's SOLIDWORKS via the ClaudeWorks add-in. If SOLIDWORKS isn't running, call launch_solidworks first.";
            var result = new JsonArray();
            foreach (var t in (source?["tools"] as JsonArray) ?? new JsonArray())
            {
                var destructive = t["destructive"]?.GetValue<bool>() == true;
                var modifies = t["modifies"]?.GetValue<bool>() == true;
                result.Add(new JsonObject
                {
                    ["name"] = (string)t["name"],
                    ["description"] = (string)t["description"],
                    ["inputSchema"] = t["schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                    ["annotations"] = new JsonObject
                    {
                        ["readOnlyHint"] = !modifies,
                        ["destructiveHint"] = destructive,
                        ["openWorldHint"] = false,
                    },
                });
            }
            result.Add(new JsonObject
            {
                ["name"] = LaunchTool,
                ["description"] = "Start SOLIDWORKS (if it isn't running) and wait until the Claude add-in is ready. Use when other tools report that SOLIDWORKS isn't running.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["openWorldHint"] = false },
            });
            return result;
        }

        private static JsonObject ToolResult(string text, bool isError) => new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError,
        };

        private static JsonObject CallTool(string name, JsonObject arguments)
        {
            if (name == LaunchTool) return Launch();
            var reply = _link.Request(new JsonObject { ["op"] = "call", ["name"] = name, ["input"] = arguments.DeepClone(), ["client"] = _client }, connectMs: 3000);
            if (reply == null)
                return ToolResult("SOLIDWORKS isn't running, or the ClaudeWorks add-in isn't loaded. Call launch_solidworks, or ask the user to start SOLIDWORKS.", true);
            if (reply["ok"]?.GetValue<bool>() != true)
                return ToolResult((string)reply["error"] ?? "The add-in reported an error.", true);
            var content = new JsonArray();
            foreach (var c in (reply["content"] as JsonArray) ?? new JsonArray())
            {
                if ((string)c["type"] == "image")
                    content.Add(new JsonObject { ["type"] = "image", ["data"] = (string)c["base64"], ["mimeType"] = (string)c["media_type"] ?? "image/png" });
                else
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = (string)c["text"] ?? "" });
            }
            if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = (string)reply["summary"] ?? "Done." });
            return new JsonObject { ["content"] = content, ["isError"] = reply["is_error"]?.GetValue<bool>() == true };
        }

        private static JsonObject Launch()
        {
            if (_link.Request(new JsonObject { ["op"] = "hello" }, connectMs: 1000) != null)
                return ToolResult("SOLIDWORKS is running and the Claude add-in is ready.", false);
            if (Process.GetProcessesByName("SLDWORKS").Length == 0)
            {
                var exe = FindSolidWorks();
                if (exe == null) return ToolResult("Could not find SOLIDWORKS on this PC. Ask the user to start it.", true);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            var until = DateTime.UtcNow.AddMinutes(4);
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(3000);
                if (_link.Request(new JsonObject { ["op"] = "hello" }, connectMs: 1000) != null)
                    return ToolResult("SOLIDWORKS started and the Claude add-in is ready.", false);
            }
            return ToolResult("SOLIDWORKS is starting but the Claude add-in did not answer yet. Check that it is enabled in Tools > Add-ins, then retry.", true);
        }

        private static string FindSolidWorks()
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var root = hklm.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (root == null) return null;
                foreach (var name in root.GetSubKeyNames().Where(n => n.StartsWith("SOLIDWORKS 20", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n))
                {
                    using (var setup = root.OpenSubKey(name + @"\Setup"))
                    {
                        var dir = setup?.GetValue("SolidWorks Folder") as string;
                        if (dir != null && File.Exists(Path.Combine(dir, "SLDWORKS.exe"))) return Path.Combine(dir, "SLDWORKS.exe");
                    }
                }
            }
            return null;
        }
    }

    /// <summary>Line-based JSON request/reply over the add-in's named pipe, reconnecting as needed.</summary>
    internal sealed class PipeLink
    {
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly object _gate = new object();

        public JsonNode Request(JsonObject request, int connectMs)
        {
            lock (_gate)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (_pipe == null || !_pipe.IsConnected)
                        {
                            Close();
                            var p = new NamedPipeClientStream(".", "ClaudeForSolidWorks.Tools", PipeDirection.InOut);
                            p.Connect(connectMs);
                            _pipe = p;
                            _reader = new StreamReader(p, new UTF8Encoding(false));
                            _writer = new StreamWriter(p, new UTF8Encoding(false)) { AutoFlush = true };
                        }
                        _writer.WriteLine(request.ToJsonString());
                        var line = _reader.ReadLine();
                        if (line == null) throw new IOException("pipe closed");
                        return JsonNode.Parse(line);
                    }
                    catch (TimeoutException) { Close(); return null; }
                    catch (Exception e) { Console.Error.WriteLine("pipe: " + e.Message); Close(); }
                }
                return null;
            }
        }

        private void Close()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null; _reader = null; _writer = null;
        }
    }

    /// <summary>Adds/removes this server in Claude Code (via its CLI) and Claude Desktop (config file).</summary>
    internal static class Registration
    {
        private static string Self => Process.GetCurrentProcess().MainModule.FileName;

        public static int ClaudeCode(bool add)
        {
            var claude = FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? FindOnPath("claude");
            if (claude == null) { Console.Error.WriteLine("Claude Code CLI (claude) not found on PATH; add it manually: claude mcp add --scope user " + Program.ServerName + " -- \"" + Self + "\""); return 1; }
            Run(claude, "mcp remove --scope user " + Program.ServerName);
            if (!add) return 0;
            var code = Run(claude, "mcp add --scope user " + Program.ServerName + " -- \"" + Self + "\"");
            Console.WriteLine(code == 0 ? "Added to Claude Code (user scope) as '" + Program.ServerName + "'." : "claude mcp add failed (exit " + code + ").");
            return code;
        }

        public static int ClaudeDesktop(bool add)
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new List<string> { Path.Combine(roaming, "Claude", "claude_desktop_config.json") };
            // Microsoft Store / MSIX install keeps a virtualised copy of %APPDATA%.
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(packages))
                foreach (var d in Directory.GetDirectories(packages, "Claude_*"))
                    candidates.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
            var touched = 0;
            foreach (var file in candidates)
            {
                var dir = Path.GetDirectoryName(file);
                if (!Directory.Exists(dir)) continue; // that flavour of Claude Desktop isn't installed
                JsonObject root;
                try { root = File.Exists(file) ? (JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject()) : new JsonObject(); }
                catch (Exception e) { Console.Error.WriteLine($"Skipping unreadable {file}: {e.Message}"); continue; }
                var servers = root["mcpServers"] as JsonObject;
                if (servers == null) { servers = new JsonObject(); root["mcpServers"] = servers; }
                servers.Remove(Program.ServerName);
                if (add) servers[Program.ServerName] = new JsonObject { ["command"] = Self, ["args"] = new JsonArray() };
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);
                File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine((add ? "Added to " : "Removed from ") + file);
                touched++;
            }
            if (touched == 0) Console.WriteLine("Claude Desktop config folder not found (is Claude Desktop installed?).");
            return 0;
        }

        private static int Run(string exe, string args)
        {
            var useCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(useCmd ? "cmd.exe" : exe, useCmd ? "/c \"\"" + exe + "\" " + args + "\"" : args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(o)) Console.WriteLine(o.Trim());
                return p.ExitCode;
            }
        }

        private static string FindOnPath(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var f = Path.Combine(dir.Trim(), name);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name);
            return File.Exists(local) ? local : null;
        }
    }
}
