using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Model;
using ClaudeSW.Core.Storage;

namespace ClaudeSW.AddIn.Host
{
    /// <summary>
    /// Serves the add-in's SOLIDWORKS tools to local MCP clients (Claude Code, Claude Desktop) through
    /// ClaudeSW.Mcp.exe, which speaks MCP over stdio and forwards here. The pipe only accepts
    /// connections from the current Windows user. One JSON request per line, one JSON reply per line:
    ///   {"op":"hello"} | {"op":"list"} | {"op":"call","name":"...","input":{...},"client":"Claude Code"}
    /// Each modifying call is its own SOLIDWORKS undo step.
    /// </summary>
    internal sealed class ToolPipeServer : IDisposable
    {
        public const string PipeName = "ClaudeForSolidWorks.Tools";
        public const string ContextTool = "get_solidworks_context";
        public const string UndoTool = "undo_last_change";

        private readonly AppController _app;
        private readonly TaskPaneHost _host;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _oneAtATime = new SemaphoreSlim(1, 1);

        public ToolPipeServer(AppController app, TaskPaneHost host) { _app = app; _host = host; }

        public static string ToolCachePath => Path.Combine(AppPaths.Root, "mcp-tools.json");

        public void Start()
        {
            try { WriteToolCache(); } catch (Exception e) { Log.Warn("Tool cache: " + e.Message); }
            for (var i = 0; i < 4; i++) Task.Run(LoopAsync); // up to 4 concurrent clients
            Log.Info("Tool pipe listening on \\\\.\\pipe\\" + PipeName);
        }

        /// <summary>The bridge serves this list when SOLIDWORKS isn't running, so clients still see the tools.</summary>
        private void WriteToolCache()
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(ToolCachePath, JsonSerializer.Serialize(new { tools = ListTools(), instructions = Instructions }));
        }

        private const string Instructions =
            "These tools drive the user's running SOLIDWORKS through the ClaudeWorks add-in. " +
            "Units: millimetres and degrees. Start with get_solidworks_context to see the active document, feature tree and the user's selection (references S1, S2...). " +
            "Use find_faces / find_edges to get references (F#, E#) instead of guessing coordinates, then pass them in `refs`. " +
            "Typical part: new_document -> begin_sketch -> add_* -> end_sketch -> extrude -> capture_view to check. " +
            "Each modifying call is one SOLIDWORKS undo step; undo_last_change reverts the latest one. Never save or overwrite files unless the user asked.";

        private List<object> ListTools()
        {
            var specs = _app.Tools.GetTools();
            var list = specs.Select(t => (object)new
            {
                name = t.Name,
                description = t.Description,
                schema = JsonDocument.Parse(t.InputSchemaJson).RootElement,
                destructive = t.Destructive,
                modifies = t.Modifies,
            }).ToList();
            list.Insert(0, new
            {
                name = ContextTool,
                description = "Current SOLIDWORKS state: active document, feature tree, rebuild errors and the user's selection as references (S1, S2...) usable in other tools' `refs`. Call this first and whenever the user says 'this' or 'the selected...'.",
                schema = JsonDocument.Parse(SchemaBuilder.Empty).RootElement,
                destructive = false,
                modifies = false,
            });
            list.Add(new
            {
                name = UndoTool,
                description = "Undo the most recent change made through these tools (one tool call = one SOLIDWORKS undo step).",
                schema = JsonDocument.Parse(SchemaBuilder.Empty).RootElement,
                destructive = false,
                modifies = true,
            });
            return list;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var sec = new PipeSecurity();
                    sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16, sec))
                    {
                        await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                        var reader = new StreamReader(server, new UTF8Encoding(false));
                        var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            string reply;
                            try { reply = await HandleAsync(line).ConfigureAwait(false); }
                            catch (Exception e) { reply = JsonSerializer.Serialize(new { ok = false, error = e.Message }); }
                            await writer.WriteLineAsync(reply).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    if (_cts.IsCancellationRequested) return;
                    Log.Warn("Tool pipe: " + e.Message);
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }

        private async Task<string> HandleAsync(string line)
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var r = doc.RootElement;
                var op = r.GetProperty("op").GetString();
                if (op == "hello")
                    return JsonSerializer.Serialize(new { ok = true, version = _app.Version, enabled = _app.CurrentSettings.AllowExternalClients });
                if (!_app.CurrentSettings.AllowExternalClients)
                    return Error("External clients are turned off. In SOLIDWORKS open the Claude pane > Settings and turn on \"Allow Claude Code / Claude Desktop\".");
                if (op == "list")
                    return JsonSerializer.Serialize(new { ok = true, tools = ListTools(), instructions = Instructions });
                if (op != "call") return Error("unknown op " + op);

                var name = r.GetProperty("name").GetString();
                var client = r.TryGetProperty("client", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString() : "MCP client";
                var input = r.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp.Clone() : JsonDocument.Parse("{}").RootElement;
                if (_app.Busy) return Error("The Claude pane in SOLIDWORKS is in the middle of its own reply. Wait for it to finish (or press Stop there), then retry.");

                if (!await _oneAtATime.WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false))
                    return Error("Another tool call is still running in SOLIDWORKS.");
                try
                {
                    ToolOutput output;
                    if (name == ContextTool)
                    {
                        var text = await _host.RunOnUiThread(() => _app.Session.BuildContext(false, out _), SwToolExecutorTimeout).ConfigureAwait(false);
                        output = ToolOutput.Text(text, "Read SOLIDWORKS context");
                    }
                    else if (name == UndoTool)
                    {
                        output = ToolOutput.Text(await _app.Tools.UndoLastAsync().ConfigureAwait(false), "Undo");
                    }
                    else
                    {
                        var spec = _app.Tools.GetTools().FirstOrDefault(t => t.Name == name);
                        if (spec == null) return Error($"Unknown tool '{name}'.");
                        var errors = SchemaValidator.Validate(spec.InputSchemaJson, input);
                        if (errors.Count > 0) output = ToolOutput.Error("Invalid arguments: " + string.Join("; ", errors));
                        else
                        {
                            if (spec.Modifies) await _app.Tools.BeginTurnAsync($"{client}: {name}").ConfigureAwait(false);
                            try { output = await _app.Tools.ExecuteAsync(spec, new Args(input), new ToolContext { ToolUseId = "mcp" }).ConfigureAwait(false); }
                            finally { if (spec.Modifies) await _app.Tools.EndTurnAsync().ConfigureAwait(false); }
                        }
                    }
                    try { _app.ExternalActivity(client, name, output.Summary ?? name, output.IsError); } catch { }
                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        is_error = output.IsError,
                        summary = output.Summary,
                        content = output.Content.Select(b => b.Type == Block.ImageType
                            ? (object)new { type = "image", media_type = b.MediaType, base64 = b.Base64 }
                            : new { type = "text", text = b.Text }).ToList(),
                    });
                }
                finally { _oneAtATime.Release(); }
            }
        }

        private static readonly TimeSpan SwToolExecutorTimeout = TimeSpan.FromSeconds(60);
        private static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message });

        public void Dispose() => _cts.Cancel();
    }
}
