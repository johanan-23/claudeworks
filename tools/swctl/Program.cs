using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

// usage: swctl load <dll|{guid}> | swctl unload <dll|{guid}> | swctl revision
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var sw = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            switch (args.Length > 0 ? args[0] : "")
            {
                case "load":
                    var r = sw.LoadAddIn(args[1]);
                    Console.WriteLine("LoadAddIn -> " + r + " (0 ok, 2 already loaded)");
                    return r == 0 || r == 2 ? 0 : 1;
                case "unload":
                    Console.WriteLine("UnloadAddIn -> " + sw.UnloadAddIn(args[1]));
                    return 0;
                case "revision":
                    Console.WriteLine(sw.RevisionNumber());
                    return 0;
                default:
                    Console.WriteLine("usage: swctl load|unload <dll or {guid}> | revision");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
