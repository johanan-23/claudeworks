using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Storage;
using SolidWorks.Interop.sldworks;

namespace ClaudeSW.AddIn.Host
{
    /// <summary>Owns the Task Pane tab, the hosted UI and the controller for one SOLIDWORKS session.</summary>
    internal sealed class PaneSession : IDisposable
    {
        private readonly ISldWorks _sw;
        private TaskpaneView _view;
        private TaskPaneHost _host;
        private AppController _app;
        private DevPipe _pipe;
        private ToolPipeServer _tools;
        private Timer _timer;

        public PaneSession(ISldWorks sw, int cookie) { _sw = sw; }

        public void Start()
        {
            Log.Info($"ClaudeWorks {typeof(PaneSession).Assembly.GetName().Version} starting in SOLIDWORKS {_sw.RevisionNumber()}");
            var iconDir = Path.Combine(AssemblyResolver.Dir, "icons");
            var icons = new[] { 20, 32, 40, 64, 96, 128 }.Select(s => Path.Combine(iconDir, $"pane_{s}.png")).ToArray();
            _view = _sw.CreateTaskpaneView3(icons.All(File.Exists) ? icons : null, "Claude") as TaskpaneView
                    ?? throw new InvalidOperationException("SOLIDWORKS refused to create the Task Pane view.");

            var dev = string.Equals(System.Environment.GetEnvironmentVariable("CLAUDESW_DEV"), "1", StringComparison.Ordinal);
            _host = new TaskPaneHost { DevMode = dev };
            if (!_view.DisplayWindowFromHandlex64(_host.Handle.ToInt64()))
                Log.Warn("DisplayWindowFromHandlex64 returned false");
            _host.FollowParent();

            // First run (no settings yet) or dev mode: open the pane so the user lands on the sign-in screen.
            if (dev || !File.Exists(AppPaths.Settings))
            {
                try { _view.ShowView(); } catch (Exception e) { Log.Warn("ShowView failed: " + e.Message); }
            }

            var session = new SwSession(_sw);
            _app = new AppController(session, _host);
            _host.WebReady += () => Log.Info("Pane UI loaded");
            _ = _host.InitializeAsync();

            // Light polling keeps the selection chip and theme in sync without per-document event plumbing.
            _timer = new Timer { Interval = 1200 };
            _timer.Tick += (s, e) => { if (!_app.Busy && _host.Visible) _app.PushStatus(); };
            _timer.Start();

            // Local MCP bridge endpoint (Claude Code / Claude Desktop via ClaudeSW.Mcp.exe).
            _tools = new ToolPipeServer(_app, _host);
            _tools.Start();

            if (dev)
            {
                _pipe = new DevPipe(_app, _host);
                _pipe.Start();
            }
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _pipe?.Dispose();
            _tools?.Dispose();
            try { _host?.Dispose(); } catch { }
            try { _view?.DeleteView(); } catch { }
            if (_view != null) Marshal.ReleaseComObject(_view);
            _view = null;
            Log.Info("ClaudeWorks unloaded");
        }
    }
}
