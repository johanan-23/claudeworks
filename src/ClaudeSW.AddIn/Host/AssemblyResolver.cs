using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClaudeSW.AddIn.Host
{
    /// <summary>
    /// SOLIDWORKS loads add-ins into its own AppDomain where the probing base is the SOLIDWORKS
    /// folder, and add-ins get no app.config binding redirects. This resolver loads our private
    /// dependencies (Anthropic SDK, System.Text.Json, WebView2, ...) from the add-in folder, and
    /// accepts any version we ship when a different one was requested (acts as a binding redirect).
    /// </summary>
    internal static class AssemblyResolver
    {
        private static bool _installed;
        public static string Dir { get; } = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name);
                // Already loaded (possibly a different version)? Reuse it if it is ours.
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                {
                    try
                    {
                        return string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase) &&
                               !a.IsDynamic && Path.GetDirectoryName(a.Location).Equals(Dir, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });
                if (loaded != null) return loaded;

                var path = Path.Combine(Dir, name.Name + ".dll");
                if (File.Exists(path))
                {
                    SwAddin.Trace($"resolve {args.Name} (for {args.RequestingAssembly?.GetName().Name}) -> ours");
                    return Assembly.LoadFrom(path);
                }
            }
            catch { /* fall through: let the CLR report the original failure */ }
            return null;
        }
    }
}
