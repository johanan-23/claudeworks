using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClaudeSW.Core.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeSW.AddIn.Host
{
    /// <summary>
    /// The WinForms control placed in the SOLIDWORKS Task Pane. Hosts a WebView2 that renders the
    /// bundled web UI from embedded resources (no network needed) and relays JSON messages.
    /// Also acts as the UI-thread dispatcher: SOLIDWORKS API calls must run on this thread.
    /// </summary>
    public sealed class TaskPaneHost : UserControl
    {
        public const string Origin = "https://claude-sw.local";
        private readonly WebView2 _web;
        private readonly Label _status;
        private bool _ready;

        public event Action<string> MessageReceived;
        public event Action WebReady;

        public TaskPaneHost()
        {
            BackColor = Color.FromArgb(250, 249, 245);
            Dock = DockStyle.Fill;
            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Starting Claude…",
                ForeColor = Color.FromArgb(110, 106, 96),
                Font = new Font("Segoe UI", 9f),
            };
            _web = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = BackColor };
            Controls.Add(_web);
            Controls.Add(_status);
            // Force handle creation now so BeginInvoke works before the pane is first shown.
            CreateControl();
            var _ = Handle;
        }

        public bool DevMode { get; set; }

        // ------------------------------------------------------------------ sizing inside the native Task Pane window

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private ParentWatcher _watcher;

        /// <summary>
        /// SOLIDWORKS re-parents our window into its own (native) Task Pane container, so WinForms
        /// docking does not apply. Track the container's size and fill it.
        /// </summary>
        public void FollowParent()
        {
            var parent = GetParent(Handle);
            if (parent == IntPtr.Zero) return;
            _watcher?.ReleaseHandle();
            _watcher = new ParentWatcher(this);
            _watcher.AssignHandle(parent);
            FitToParent();
        }

        private void FitToParent()
        {
            var parent = GetParent(Handle);
            if (parent == IntPtr.Zero || !GetClientRect(parent, out var r)) return;
            var w = Math.Max(1, r.Right - r.Left);
            var h = Math.Max(1, r.Bottom - r.Top);
            if (Left != 0 || Top != 0 || Width != w || Height != h) SetBounds(0, 0, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _watcher?.ReleaseHandle(); _watcher = null; }
            base.Dispose(disposing);
        }

        private sealed class ParentWatcher : NativeWindow
        {
            private readonly TaskPaneHost _owner;
            public ParentWatcher(TaskPaneHost owner) { _owner = owner; }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                const int WM_SIZE = 0x0005, WM_WINDOWPOSCHANGED = 0x0047, WM_SHOWWINDOW = 0x0018, WM_DESTROY = 0x0002;
                if (m.Msg == WM_SIZE || m.Msg == WM_WINDOWPOSCHANGED || m.Msg == WM_SHOWWINDOW)
                {
                    try { _owner.BeginInvoke((Action)_owner.FitToParent); } catch { }
                }
                else if (m.Msg == WM_DESTROY) ReleaseHandle();
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                var dir = AssemblyResolver.Dir;
                // SOLIDWORKS ships its own WebView2Loader.dll; make sure ours (matching our SDK) is used.
                var loaderDir = Path.Combine(dir, "runtimes", "win-x64", "native");
                if (File.Exists(Path.Combine(loaderDir, "WebView2Loader.dll"))) CoreWebView2Environment.SetLoaderDllFolderPath(loaderDir);
                else if (File.Exists(Path.Combine(dir, "WebView2Loader.dll"))) CoreWebView2Environment.SetLoaderDllFolderPath(dir);

                var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForSolidWorks", "WebView2");
                Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = DevMode;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = DevMode; // no F5/Ctrl+P etc. in production
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnResource;
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
                core.WebMessageReceived += (s, e) =>
                {
                    string json;
                    try { json = e.WebMessageAsJson; } catch { return; }
                    try { MessageReceived?.Invoke(json); }
                    catch (Exception ex) { Log.Error("Bridge handler failed", ex); }
                };
                core.ProcessFailed += (s, e) => Log.Error("WebView2 process failed: " + e.ProcessFailedKind);
                core.NavigationCompleted += (s, e) =>
                {
                    if (!e.IsSuccess) Log.Warn("Navigation failed: " + e.WebErrorStatus);
                    _status.Visible = false;
                    _web.Visible = true;
                    if (!_ready) { _ready = true; WebReady?.Invoke(); }
                };
                core.Navigate(Origin + "/index.html");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                _status.Text = "Claude needs the Microsoft Edge WebView2 Runtime.\n\nReinstall ClaudeWorks, or install WebView2 from microsoft.com/edge/webview2, then restart SOLIDWORKS.";
                Log.Error("WebView2 runtime not found");
            }
            catch (Exception e)
            {
                _status.Text = "Claude could not start its window.\n" + e.Message + "\n\nSee %APPDATA%\\ClaudeForSolidWorks\\logs.";
                Log.Error("WebView2 init failed", e);
            }
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        }

        public static void OpenExternal(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            {
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception e) { Log.Warn("Could not open link: " + e.Message); }
            }
        }

        private void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            if (path == "") path = "index.html";
            var stream = OpenUiResource(path);
            if (stream == null)
            {
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var mime = path.EndsWith(".html") ? "text/html; charset=utf-8" : path.EndsWith(".js") ? "text/javascript" : path.EndsWith(".css") ? "text/css" : "application/octet-stream";
            var headers = $"Content-Type: {mime}\r\nCache-Control: no-store\r\n" +
                          "Content-Security-Policy: default-src 'self' https://claude-sw.local; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'";
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        }

        private static Stream OpenUiResource(string path)
        {
            var asm = Assembly.GetExecutingAssembly();
            // Developer override: serve the live Vite build output if present next to the DLL.
            var dev = Path.Combine(AssemblyResolver.Dir, "ui", path);
            if (File.Exists(dev)) return new MemoryStream(File.ReadAllBytes(dev));
            return asm.GetManifestResourceStream("ui/" + path) ??
                   (path == "index.html" ? asm.GetManifestResourceStream("ui/fallback.html") : null);
        }

        /// <summary>Send a JSON message to the page. Safe to call from any thread.</summary>
        public void Post(string json)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => Post(json))); } catch (InvalidOperationException) { }
                return;
            }
            try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception e) { Log.Warn("Post failed: " + e.Message); }
        }

        public Task<string> EvalAsync(string js) => OnUi(() => _web.CoreWebView2.ExecuteScriptAsync(js));

        public Task CapturePngAsync(Stream target) => OnUi(() => _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, target));

        public void OpenDevTools() => OnUi(() => { _web.CoreWebView2?.OpenDevToolsWindow(); return Task.CompletedTask; });

        private Task<T> OnUi<T>(Func<Task<T>> f)
        {
            var tcs = new TaskCompletionSource<T>();
            BeginInvoke((Action)(async () =>
            {
                try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); }
            }));
            return tcs.Task;
        }

        private Task OnUi(Func<Task> f) => OnUi(async () => { await f(); return true; });

        // ------------------------------------------------------------------ UI-thread dispatch for SOLIDWORKS calls

        /// <summary>
        /// Run <paramref name="work"/> on the SOLIDWORKS UI thread. If the thread does not pick it up
        /// within <paramref name="timeout"/> (SOLIDWORKS busy rebuilding or a modal loop), the work is
        /// abandoned and a <see cref="TimeoutException"/> is raised; it will not run late.
        /// </summary>
        public Task<T> RunOnUiThread<T>(Func<T> work, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!InvokeRequired)
            {
                try { tcs.SetResult(work()); } catch (Exception e) { tcs.SetException(e); }
                return tcs.Task;
            }
            var state = 0; // 0 = pending, 1 = started, 2 = abandoned
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                    try { tcs.SetResult(work()); } catch (Exception e) { tcs.SetException(e); }
                }));
            }
            catch (Exception e) { tcs.SetException(e); return tcs.Task; }

            Task.Delay(timeout).ContinueWith(_ =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                    tcs.TrySetException(new TimeoutException("SOLIDWORKS is busy and did not respond."));
            });
            return tcs.Task;
        }
    }
}
