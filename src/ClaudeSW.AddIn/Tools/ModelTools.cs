using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using static ClaudeSW.AddIn.Tools.K;

namespace ClaudeSW.AddIn.Tools
{
    /// <summary>Feature tree, parameters, properties, material, mass properties, configurations.</summary>
    internal static class ModelTools
    {
        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("list_features", "List the feature tree (name, type, suppressed). Housekeeping folders are hidden unless include_folders.",
                S().Bool("include_folders", "").Bool("include_sketches_under_features", "Also list absorbed sketches."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var list = new List<object>();
                    foreach (var f in SwSession.Features(doc))
                    {
                        if (!a.Bool("include_folders") && SwSession.IsHousekeeping(f)) continue;
                        List<string> subs = null;
                        if (a.Bool("include_sketches_under_features"))
                        {
                            subs = new List<string>();
                            var sf = f.GetFirstSubFeature() as Feature;
                            while (sf != null) { subs.Add(sf.Name); sf = sf.GetNextSubFeature() as Feature; }
                        }
                        list.Add(new { name = f.Name, type = f.GetTypeName2(), suppressed = f.IsSuppressed(), children = subs });
                    }
                    return ToolOutput.Obj(list, $"Listed {list.Count} features");
                }, describe: a => "List features");

            yield return Tool("delete_feature", "Delete a feature or sketch by name (and its absorbed sketch).",
                S().Str("name", "", true),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    doc.ClearSelection2(true);
                    SwSession.FindFeature(doc, a.RequireStr("name")).Select2(false, 0);
                    if (!doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                        throw new ToolException($"Could not delete '{a.Str("name")}' (other features may depend on it).");
                    return ToolOutput.Text($"Deleted '{a.Str("name")}'.", "Deleted " + a.Str("name"));
                }, modifies: true, destructive: true, confirm: a => $"Delete feature \"{a.Str("name")}\"?", describe: a => "Delete " + a.Str("name"));

            yield return Tool("suppress_feature", "Suppress or unsuppress a feature by name.",
                S().Str("name", "", true).Bool("suppressed", "Default true."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var f = SwSession.FindFeature(doc, a.RequireStr("name"));
                    var sup = a.Bool("suppressed", true);
                    f.SetSuppression2(sup ? (int)swFeatureSuppressionAction_e.swSuppressFeature : (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    return ToolOutput.Text($"{(sup ? "Suppressed" : "Unsuppressed")} '{f.Name}'.", $"{(sup ? "Suppressed" : "Unsuppressed")} {f.Name}");
                }, modifies: true, describe: a => (a.Bool("suppressed", true) ? "Suppress " : "Unsuppress ") + a.Str("name"));

            yield return Tool("rename_feature", "Rename a feature or sketch.",
                S().Str("name", "", true).Str("new_name", "", true),
                (a, c) =>
                {
                    var f = SwSession.FindFeature(s.RequireDoc(DocTypes.Part, DocTypes.Assembly), a.RequireStr("name"));
                    f.Name = a.RequireStr("new_name");
                    if (s.LastSketch == a.Str("name")) s.LastSketch = f.Name;
                    return ToolOutput.Text($"Renamed to '{f.Name}'.", $"Renamed {a.Str("name")} → {f.Name}");
                }, modifies: true, describe: a => $"Rename {a.Str("name")}");

            yield return Tool("list_dimensions", "List feature/sketch dimensions, e.g. D1@Sketch1 = 50 (mm or deg). Optionally only for one feature.",
                S().Str("feature", "Limit to this feature."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var only = a.Str("feature");
                    var outList = new List<object>();
                    foreach (var f in SwSession.Features(doc))
                    {
                        if (only != null && !string.Equals(f.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                        var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                        while (dd != null)
                        {
                            var dim = dd.GetDimension2(0);
                            var ang = dd.GetType() == (int)swDimensionType_e.swAngularDimension || dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                            outList.Add(new { name = dim.FullName, value = Math.Round(ang ? dim.SystemValue * 180 / Math.PI : dim.SystemValue * 1000, 6), unit = ang ? "deg" : "mm" });
                            dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                        }
                    }
                    return ToolOutput.Obj(outList, $"Listed {outList.Count} dimensions");
                }, describe: a => "List dimensions");

            yield return Tool("set_dimension", "Change a dimension by full name (e.g. D1@Sketch1 or D1@Boss-Extrude1) and rebuild. mm, or degrees if angular.",
                S().Str("name", "", true).Num("value", "", true).Bool("angular", ""),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var dim = doc.Parameter(a.RequireStr("name")) as Dimension ?? throw new ToolException($"Dimension '{a.Str("name")}' not found. Use list_dimensions.");
                    dim.SystemValue = a.Bool("angular") ? a.Deg("value") : a.Mm("value");
                    var ok = doc.EditRebuild3();
                    var errs = s.RebuildErrors(doc);
                    return ToolOutput.Obj(new { name = a.Str("name"), value = a.Num("value"), rebuilt = ok, rebuild_errors = errs.Count == 0 ? null : errs },
                        $"Set {a.Str("name")} = {F(a.Num("value"))}{(a.Bool("angular") ? "°" : " mm")}");
                }, modifies: true, describe: a => $"{a.Str("name")} = {F(a.Num("value"))}");

            yield return Tool("add_equation", "Add a global variable or equation, e.g. \"Width\" = 50 or \"D1@Sketch1\" = \"Width\" * 2.",
                S().Str("equation", "", true),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var mgr = (EquationMgr)doc.GetEquationMgr();
                    var idx = mgr.Add2(-1, a.RequireStr("equation"), true);
                    if (idx < 0) throw new ToolException("SOLIDWORKS rejected the equation. Quote names: \"D1@Sketch1\" = 20.");
                    doc.EditRebuild3();
                    return ToolOutput.Text($"Equation #{idx} added.", "Added equation");
                }, modifies: true, describe: a => "Equation " + a.Str("equation"));

            yield return Tool("list_equations", "List equations and global variables with their values.",
                S(), (a, c) =>
                {
                    var mgr = (EquationMgr)s.RequireDoc(DocTypes.Part, DocTypes.Assembly).GetEquationMgr();
                    var list = Enumerable.Range(0, mgr.GetCount()).Select(i => new { equation = mgr.get_Equation(i), value = mgr.get_Value(i) }).ToList();
                    return ToolOutput.Obj(list, $"Listed {list.Count} equations");
                }, describe: a => "List equations");

            yield return Tool("set_custom_property", "Set a custom property (empty configuration = file-level).",
                S().Str("name", "", true).Str("value", "", true).Str("configuration", ""),
                (a, c) =>
                {
                    var mgr = s.RequireDoc().Extension.get_CustomPropertyManager(a.Str("configuration", ""));
                    mgr.Add3(a.RequireStr("name"), (int)swCustomInfoType_e.swCustomInfoText, a.Str("value", ""), (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    return ToolOutput.Text($"Set '{a.Str("name")}'.", $"Set property {a.Str("name")}");
                }, modifies: true, describe: a => "Property " + a.Str("name"));

            yield return Tool("get_custom_properties", "Read custom properties (empty configuration = file-level).",
                S().Str("configuration", ""),
                (a, c) =>
                {
                    var mgr = s.RequireDoc().Extension.get_CustomPropertyManager(a.Str("configuration", ""));
                    var names = mgr.GetNames() as object[] ?? new object[0];
                    var d = new Dictionary<string, object>();
                    foreach (string n in names)
                    {
                        string val, resolved; bool wasResolved, linked;
                        mgr.Get6(n, false, out val, out resolved, out wasResolved, out linked);
                        d[n] = new { value = val, resolved };
                    }
                    return ToolOutput.Obj(d, $"Read {d.Count} properties");
                }, describe: a => "Read properties");

            yield return Tool("set_material", "Assign a material to the active part, e.g. 'Plain Carbon Steel', '6061 Alloy', 'ABS'.",
                S().Str("material", "", true).Str("database", "Optional .sldmat path."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var db = a.Str("database") ?? FindMaterialDb(s);
                    var cfg = ((ConfigurationManager)doc.ConfigurationManager).ActiveConfiguration.Name;
                    ((PartDoc)doc).SetMaterialPropertyName2(cfg, db, a.RequireStr("material"));
                    string dbOut;
                    var now = ((PartDoc)doc).GetMaterialPropertyName2(cfg, out dbOut);
                    if (string.IsNullOrEmpty(now)) throw new ToolException($"Material '{a.Str("material")}' not found in {Path.GetFileName(db)}. Check the exact name.");
                    return ToolOutput.Text($"Material set to {now}.", "Material: " + now);
                }, modifies: true, describe: a => "Material " + a.Str("material"));

            yield return Tool("get_model_info", "Mass properties (kg, mm³, mm², mm), bounding box, material, configurations and feature count of the active part/assembly.",
                S(), (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var info = new Dictionary<string, object>
                    {
                        ["document"] = SwSession.Summary(doc),
                        ["configurations"] = doc.GetConfigurationNames(),
                        ["feature_count"] = SwSession.Features(doc).Count(f => !SwSession.IsHousekeeping(f)),
                    };
                    try
                    {
                        var mp = (MassProperty)doc.Extension.CreateMassProperty();
                        var com = (double[])mp.CenterOfMass;
                        info["mass_kg"] = Math.Round(mp.Mass, 6);
                        info["volume_mm3"] = Math.Round(mp.Volume * 1e9, 3);
                        info["surface_area_mm2"] = Math.Round(mp.SurfaceArea * 1e6, 3);
                        info["center_of_mass_mm"] = com.Select(v => Math.Round(v * 1000, 4)).ToArray();
                        info["density_kg_m3"] = Math.Round(mp.Density, 3);
                    }
                    catch { info["mass_properties"] = "unavailable (no solid bodies?)"; }
                    try
                    {
                        double[] box = doc.GetType() == DocTypes.Part ? (double[])((PartDoc)doc).GetPartBox(true) : (double[])((AssemblyDoc)doc).GetBox(0);
                        info["bounding_box_mm"] = new
                        {
                            min = box.Take(3).Select(v => Math.Round(v * 1000, 4)),
                            max = box.Skip(3).Select(v => Math.Round(v * 1000, 4)),
                            size = new[] { box[3] - box[0], box[4] - box[1], box[5] - box[2] }.Select(v => Math.Round(v * 1000, 4)),
                        };
                    }
                    catch { }
                    if (doc.GetType() == DocTypes.Part)
                    {
                        string db;
                        var m = ((PartDoc)doc).GetMaterialPropertyName2("", out db);
                        info["material"] = string.IsNullOrEmpty(m) ? "(not set)" : m;
                    }
                    return ToolOutput.Obj(info, "Read model info");
                }, describe: a => "Mass properties");

            yield return Tool("configurations", "List, add or activate configurations of the active part/assembly.",
                S().Str("action", "", true, new[] { "list", "add", "activate" }).Str("name", "Configuration name for add/activate.").Str("description", ""),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var action = a.RequireStr("action").ToLowerInvariant();
                    if (action == "list")
                        return ToolOutput.Obj(new { active = ((ConfigurationManager)doc.ConfigurationManager).ActiveConfiguration.Name, all = doc.GetConfigurationNames() }, "Listed configurations");
                    var name = a.RequireStr("name");
                    if (action == "add")
                    {
                        if (doc.AddConfiguration3(name, a.Str("description", ""), "", 0) == null) throw new ToolException($"Could not add configuration '{name}' (exists already?).");
                        return ToolOutput.Text($"Added and activated configuration '{name}'.", "Added configuration " + name);
                    }
                    if (!doc.ShowConfiguration2(name)) throw new ToolException($"No configuration named '{name}'.");
                    return ToolOutput.Text($"Activated '{name}'.", "Activated configuration " + name);
                }, modifies: true, describe: a => $"Configurations: {a.Str("action")} {a.Str("name")}");
        }

        private static string FindMaterialDb(SwSession s)
        {
            var exe = s.Sw.GetExecutablePath();
            var dir = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(exe) : exe;
            var lang = Path.Combine(dir, "lang");
            if (Directory.Exists(lang))
            {
                var hit = Directory.GetDirectories(lang).Select(l => Path.Combine(l, "sldmaterials", "solidworks materials.sldmat"))
                    .OrderBy(p => p.IndexOf("english", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).FirstOrDefault(File.Exists);
                if (hit != null) return hit;
            }
            foreach (var folder in (s.Sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsMaterialDatabases) ?? "").Split(';'))
            {
                var p = Path.Combine(folder, "SOLIDWORKS Materials.sldmat");
                if (File.Exists(p)) return p;
            }
            throw new ToolException("Could not find the SOLIDWORKS material database; pass `database`.");
        }
    }
}
