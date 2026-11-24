using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using static ClaudeSW.AddIn.Tools.K;

namespace ClaudeSW.AddIn.Tools
{
    /// <summary>
    /// Guarded escape hatch: call one SOLIDWORKS API member by late binding. Only registered when the
    /// user enables "Allow advanced API calls" in Settings, and every call needs confirmation.
    /// </summary>
    internal static class AdvancedTools
    {
        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("invoke_api", "ADVANCED: call one SOLIDWORKS API method or property by late binding, for things no other tool covers. target: 'sw' (ISldWorks), 'doc' (active ModelDoc2), or a dotted path from them like 'doc.Extension' or 'doc.FeatureManager'. Arguments are JSON values; lengths are metres and angles radians here (raw API units). Each call is shown to the user for approval.",
                S().Str("target", "sw | doc | doc.Extension | doc.FeatureManager | doc.SketchManager | doc.SelectionManager ...", true)
                   .Str("member", "Method or property name, e.g. 'GetTitle' or 'FeatureFillet3'.", true)
                   .Arr("args", "string", "Arguments as JSON literals (numbers, true/false, \"strings\", null).")
                   .Str("set_value", "For setting a property: JSON literal value."),
                (a, c) =>
                {
                    object target = Target(s, a.RequireStr("target"));
                    var member = a.RequireStr("member");
                    var args = a.Strs("args").Select(ParseLiteral).ToArray();
                    object result;
                    var t = target.GetType();
                    if (a.Has("set_value"))
                    {
                        t.InvokeMember(member, BindingFlags.SetProperty, null, target, new[] { ParseLiteral(a.Str("set_value")) });
                        result = "ok";
                    }
                    else
                    {
                        try { result = t.InvokeMember(member, BindingFlags.InvokeMethod, null, target, args); }
                        catch (MissingMethodException) when (args.Length == 0)
                        { result = t.InvokeMember(member, BindingFlags.GetProperty, null, target, null); }
                    }
                    return ToolOutput.Obj(new { result = Describe(result) }, $"Called {a.Str("target")}.{member}");
                },
                modifies: true, destructive: true, advanced: true,
                confirm: a => $"Allow Claude to call the SOLIDWORKS API directly?\n{a.Str("target")}.{a.Str("member")}({string.Join(", ", a.Strs("args"))})",
                describe: a => $"API {a.Str("target")}.{a.Str("member")}");
        }

        private static object Target(SwSession s, string path)
        {
            var parts = path.Split('.');
            object o;
            switch (parts[0].ToLowerInvariant())
            {
                case "sw": o = s.Sw; break;
                case "doc": o = s.ActiveDoc ?? throw new ToolException("No active document."); break;
                default: throw new ToolException("target must start with 'sw' or 'doc'.");
            }
            foreach (var p in parts.Skip(1))
                o = o.GetType().InvokeMember(p, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, o, null)
                    ?? throw new ToolException($"'{p}' returned nothing.");
            return o;
        }

        private static object ParseLiteral(string json)
        {
            if (json == null) return null;
            try
            {
                using (var d = JsonDocument.Parse(json))
                {
                    var e = d.RootElement;
                    switch (e.ValueKind)
                    {
                        case JsonValueKind.Number: return e.TryGetInt32(out var i) ? (object)i : e.GetDouble();
                        case JsonValueKind.True: return true;
                        case JsonValueKind.False: return false;
                        case JsonValueKind.Null: return null;
                        case JsonValueKind.String: return e.GetString();
                        default: return e.GetRawText();
                    }
                }
            }
            catch (JsonException) { return json; } // bare word -> string
        }

        private static object Describe(object r)
        {
            switch (r)
            {
                case null: return null;
                case string _: case bool _: case int _: case double _: case long _: case short _: return r;
                case Array arr: return arr.Cast<object>().Take(100).Select(Describe).ToArray();
                default:
                    if (r.GetType().IsCOMObject) return "<COM object " + Microsoft.VisualBasic.Information.TypeName(r) + ">";
                    return r.ToString();
            }
        }
    }
}
