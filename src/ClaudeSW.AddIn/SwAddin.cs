using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClaudeSW.AddIn.Host;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

namespace ClaudeSW.AddIn
{
    /// <summary>
    /// SOLIDWORKS add-in entry point. Creates the "Claude" Task Pane tab hosting the chat UI.
    /// Everything heavy lives in <see cref="PaneSession"/>, loaded only after the dependency
    /// resolver is installed so SOLIDWORKS can find our private assemblies.
    /// </summary>
    [ComVisible(true)]
    [Guid(AddinGuid)]
    [ProgId("ClaudeForSolidWorks.AddIn")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class SwAddin : ISwAddin
    {
        public const string AddinGuid = "ABA6C017-84D2-41CD-BD49-E617BE823AB0";
        public const string Title = "ClaudeWorks";
        public const string Description = "Chat with Claude in the Task Pane to inspect, build and edit parts, assemblies and drawings.";

        private object _session;

        /// <summary>Dependency-free startup trace (works even if our own assemblies fail to load).</summary>
        internal static void Trace(string message)
        {
            try
            {
                var dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ClaudeForSolidWorks", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "startup.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n");
            }
            catch { }
        }

        public SwAddin() { Trace("SwAddin constructed"); }

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            Trace("ConnectToSW called");
            AssemblyResolver.Install();
            try
            {
                var ok = Connect((ISldWorks)ThisSW, Cookie);
                Trace("ConnectToSW finished: " + ok);
                return ok;
            }
            catch (Exception e)
            {
                Trace("ConnectToSW failed: " + e);
                TryLog("ConnectToSW failed", e);
                return false;
            }
        }

        // Separate, non-inlined method: the JIT must not touch Core/WebView2 types until the resolver is in place.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool Connect(ISldWorks sw, int cookie)
        {
            // No SetAddinCallbackInfo2: it is only needed for CommandManager callbacks, which we don't use.
            var session = new PaneSession(sw, cookie);
            session.Start();
            _session = session;
            EnsureStartupFlag();
            return true;
        }

        public bool DisconnectFromSW()
        {
            try { (_session as PaneSession)?.Dispose(); }
            catch (Exception e) { TryLog("DisconnectFromSW", e); }
            _session = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TryLog(string message, Exception e)
        {
            try { ClaudeSW.Core.Storage.Log.Error(message, e); }
            catch
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ClaudeForSolidWorks-crash.log"), $"{DateTime.Now:o} {message}: {e}\r\n"); } catch { }
            }
        }

        /// <summary>Keep "load at startup" on for this user (the installer sets it too).</summary>
        private static void EnsureStartupFlag()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\{" + AddinGuid + "}"))
                    if (k.GetValue(null) == null) k.SetValue(null, 1, RegistryValueKind.DWord);
            }
            catch { /* not critical */ }
        }

        // ------------------------------------------------------------------ RegAsm support (developer builds)
        // The installer writes the same keys itself; these run when using `regasm /codebase`.

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\AddIns\{" + AddinGuid + "}"))
                {
                    k.SetValue(null, 1, RegistryValueKind.DWord);
                    k.SetValue("Title", Title);
                    k.SetValue("Description", Description);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\{" + AddinGuid + "}"))
                    k.SetValue(null, 1, RegistryValueKind.DWord);
            }
            catch (Exception e) { Console.WriteLine("Could not write SOLIDWORKS add-in keys: " + e.Message); }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\AddIns\{" + AddinGuid + "}", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\{" + AddinGuid + "}", false); } catch { }
        }
    }
}
