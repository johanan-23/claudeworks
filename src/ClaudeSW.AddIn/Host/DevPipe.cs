using System;
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
    /// Developer/test automation channel, enabled only with CLAUDESW_DEV=1. A named pipe restricted
    /// to the current Windows user accepts one JSON command per line and replies with one JSON line.
    /// Used by scripts/devpipe.ps1 and the manual test script to drive the pane inside SOLIDWORKS.
    /// Commands: ui (inject a UI message), eval (run JS in the pane), screenshot (pane PNG),
    /// tool (run a tool directly), state (conversation summary).
    /// </summary>
    internal sealed class DevPipe : IDisposable
    {
        public const string PipeName = "ClaudeForSolidWorks.Dev";
        private readonly AppController _app;
        private readonly TaskPaneHost _host;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public DevPipe(AppController app, TaskPaneHost host) { _app = app; _host = host; }

        public void Start() => Task.Run(LoopAsync);

        private async Task LoopAsync()
        {
            Log.Info("Dev pipe listening on \\\\.\\pipe\\" + PipeName);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var sec = new PipeSecurity();
                    sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, sec))
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
                catch (Exception e) { Log.Warn("Dev pipe: " + e.Message); await Task.Delay(500).ConfigureAwait(false); }
            }
        }

        private async Task<string> HandleAsync(string line)
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var r = doc.RootElement;
                var cmd = r.GetProperty("cmd").GetString();
                switch (cmd)
                {
                    case "ui":
                        var msg = r.GetProperty("message").GetRawText();
                        await _host.RunOnUiThread(() => { _app.Handle(msg); return true; }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        return Ok(null);
                    case "eval":
                        return Ok(await _host.EvalAsync(r.GetProperty("js").GetString()).ConfigureAwait(false));
                    case "screenshot":
                        var path = r.GetProperty("path").GetString();
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var fs = File.Create(path)) await _host.CapturePngAsync(fs).ConfigureAwait(false);
                        return Ok(path);
                    case "assemblies":
                        var filter = r.TryGetProperty("filter", out var fl) ? fl.GetString() : "";
                        var list = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.FullName.IndexOf(filter ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(a => { string loc; try { loc = a.IsDynamic ? "(dynamic)" : a.Location; } catch { loc = "?"; } return a.FullName.Split(',')[0] + " " + a.GetName().Version + " @ " + loc; })
                            .OrderBy(x => x).ToArray();
                        return Ok(list);
                    case "devtools":
                        _host.OpenDevTools();
                        return Ok(null);
                    case "tool":
                        var spec = _app.Tools.Find(r.GetProperty("name").GetString()) ?? throw new InvalidOperationException("unknown tool");
                        var args = new Args(r.TryGetProperty("input", out var inp) ? inp.Clone() : default);
                        var output = await _app.Tools.ExecuteAsync(spec, args, new ToolContext { ToolUseId = "dev" }).ConfigureAwait(false);
                        var imgPath = r.TryGetProperty("image_path", out var ip) ? ip.GetString() : null;
                        var img = output.Content.FirstOrDefault(b => b.Type == Block.ImageType);
                        if (img != null && imgPath != null) File.WriteAllBytes(imgPath, Convert.FromBase64String(img.Base64));
                        return JsonSerializer.Serialize(new
                        {
                            ok = !output.IsError,
                            summary = output.Summary,
                            text = string.Join("\n", output.Content.Where(b => b.Type == Block.TextType).Select(b => b.Text)),
                            image = img != null ? imgPath ?? "(image)" : null,
                        });
                    case "state":
                        var c = _app.Current;
                        var lastText = c.Messages.LastOrDefault(m => m.Role == "assistant")?.Content.LastOrDefault(b => b.Type == Block.TextType)?.Text;
                        return JsonSerializer.Serialize(new
                        {
                            ok = true,
                            busy = _app.Busy,
                            title = c.Title,
                            messages = c.Messages.Count,
                            tools = c.Messages.SelectMany(m => m.Content).Where(b => b.Type == Block.ToolUseType).Select(b => b.Name).ToArray(),
                            errors = c.Messages.SelectMany(m => m.Content).Where(b => b.Type == Block.ToolResultType && b.IsError == true).Select(b => b.UiSummary).ToArray(),
                            last_text = lastText,
                        });
                    default:
                        throw new InvalidOperationException("unknown cmd " + cmd);
                }
            }
        }

        private static string Ok(object result) => JsonSerializer.Serialize(new { ok = true, result });

        public void Dispose() => _cts.Cancel();
    }
}
