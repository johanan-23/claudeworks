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
    internal static class AssemblyTools
    {
        private static Component2 FindComponent(ModelDoc2 doc, string name)
        {
            var comps = ((AssemblyDoc)doc).GetComponents(false) as object[] ?? new object[0];
            return comps.Cast<Component2>().FirstOrDefault(c => string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase))
                   ?? comps.Cast<Component2>().FirstOrDefault(c => c.Name2.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolException($"No component named '{name}'. Use list_components.");
        }

        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("add_component", "Insert a saved part/sub-assembly file into the active assembly at (x, y, z) mm.",
                S().Str("path", "File path.", true).Num("x", "").Num("y", "").Num("z", "").Str("configuration", ""),
                (a, c) =>
                {
                    var asm = s.RequireDoc(DocTypes.Assembly);
                    var title = asm.GetTitle();
                    var path = Path.GetFullPath(a.RequireStr("path"));
                    if (!File.Exists(path)) throw new ToolException($"File not found: {path}. Components must be saved files.");
                    s.EnsureModelOpen(path); // AddComponent5 needs the model loaded; this can switch the active doc
                    asm = s.Activate(title) ?? throw new ToolException("Could not re-activate the assembly.");
                    var comp = ((AssemblyDoc)asm).AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false,
                        a.Str("configuration", ""), a.Mm("x"), a.Mm("y"), a.Mm("z"));
                    if (comp == null) throw new ToolException("SOLIDWORKS could not add the component.");
                    return ToolOutput.Obj(new { component = comp.Name2 }, "Added " + comp.Name2);
                }, modifies: true, describe: a => "Insert " + Path.GetFileName(a.Str("path", "")));

            yield return Tool("list_components", "List components of the active assembly with file, suppression, fixed state and position (mm).",
                S().Bool("top_level_only", "Default true."),
                (a, c) =>
                {
                    var asm = s.RequireDoc(DocTypes.Assembly);
                    var comps = ((AssemblyDoc)asm).GetComponents(a.Bool("top_level_only", true)) as object[] ?? new object[0];
                    var list = comps.Cast<Component2>().Select(cp =>
                    {
                        double[] pos = null;
                        try { pos = ((double[])((MathTransform)cp.Transform2).ArrayData).Skip(9).Take(3).Select(v => Math.Round(v * 1000, 4)).ToArray(); } catch { }
                        return new { name = cp.Name2, path = cp.GetPathName(), suppressed = cp.IsSuppressed(), @fixed = cp.IsFixed(), position_mm = pos };
                    }).ToList();
                    return ToolOutput.Obj(list, $"Listed {list.Count} components");
                }, describe: a => "List components");

            yield return Tool("add_mate", "Mate two entities in the active assembly. Give each entity as a reference (from find_faces/find_edges with component filter, or the user's selection) in `refs` [a, b]. value: distance mm or angle deg.",
                S().Str("mate_type", "", true, new[] { "coincident", "concentric", "perpendicular", "parallel", "tangent", "distance", "angle", "lock" })
                   .Refs("Exactly two entities.", true).Str("alignment", "", false, new[] { "aligned", "anti_aligned", "closest" })
                   .Num("value", "").Bool("flip", ""),
                (a, c) =>
                {
                    var asm = s.RequireDoc(DocTypes.Assembly);
                    var types = new Dictionary<string, int> { ["coincident"] = 0, ["concentric"] = 1, ["perpendicular"] = 2, ["parallel"] = 3, ["tangent"] = 4, ["distance"] = 5, ["angle"] = 6, ["lock"] = 16 };
                    var aligns = new Dictionary<string, int> { ["aligned"] = 0, ["anti_aligned"] = 1, ["closest"] = 2 };
                    var type = a.RequireStr("mate_type").ToLowerInvariant();
                    var refs = a.Strs("refs");
                    if (refs.Count != 2) throw new ToolException("add_mate needs exactly two references.");
                    asm.ClearSelection2(true);
                    s.SelectRefs(asm, refs, false, 1);
                    int err = 0;
                    var dist = type == "distance" ? a.Mm("value") : 0;
                    var ang = type == "angle" ? a.Deg("value") : 0;
                    var mate = ((AssemblyDoc)asm).AddMate5(types[type], aligns[a.Str("alignment", "closest").ToLowerInvariant()], a.Bool("flip"),
                        dist, dist, dist, 0, 0, ang, ang, ang, false, false, 0, out err);
                    asm.ClearSelection2(true);
                    if (mate == null) throw new ToolException($"Mate failed (status {(swAddMateError_e)err}). Check the entities are compatible for a {type} mate.");
                    asm.EditRebuild3();
                    return ToolOutput.Obj(new { created = type, status = ((swAddMateError_e)err).ToString() }, $"Mated {refs[0]} ↔ {refs[1]} ({type})");
                }, modifies: true, describe: a => $"{a.Str("mate_type")} mate");

            yield return Tool("fix_component", "Fix (or float) a component.",
                S().Str("name", "", true).Bool("fixed", "Default true."),
                (a, c) =>
                {
                    var asm = s.RequireDoc(DocTypes.Assembly);
                    var comp = FindComponent(asm, a.RequireStr("name"));
                    asm.ClearSelection2(true);
                    comp.Select4(false, null, false);
                    if (a.Bool("fixed", true)) ((AssemblyDoc)asm).FixComponent(); else ((AssemblyDoc)asm).UnfixComponent();
                    asm.ClearSelection2(true);
                    return ToolOutput.Text("Done.", (a.Bool("fixed", true) ? "Fixed " : "Floated ") + comp.Name2);
                }, modifies: true, describe: a => (a.Bool("fixed", true) ? "Fix " : "Float ") + a.Str("name"));

            yield return Tool("delete_component", "Delete a component from the active assembly by name (e.g. 'Bracket-1').",
                S().Str("name", "", true),
                (a, c) =>
                {
                    var asm = s.RequireDoc(DocTypes.Assembly);
                    var comp = FindComponent(asm, a.RequireStr("name"));
                    asm.ClearSelection2(true);
                    comp.Select4(false, null, false);
                    if (!asm.Extension.DeleteSelection2(0)) throw new ToolException("Could not delete the component.");
                    return ToolOutput.Text("Deleted.", "Deleted " + a.Str("name"));
                }, modifies: true, destructive: true, confirm: a => $"Delete component \"{a.Str("name")}\" from the assembly?", describe: a => "Delete " + a.Str("name"));

            yield return Tool("check_interference", "Run interference detection on the active assembly and report interfering component pairs and volumes.",
                S().Bool("treat_coincidence_as_interference", ""),
                (a, c) =>
                {
                    var asm = s.RequireDoc(DocTypes.Assembly);
                    var mgr = ((AssemblyDoc)asm).InterferenceDetectionManager;
                    mgr.TreatCoincidenceAsInterference = a.Bool("treat_coincidence_as_interference");
                    mgr.IgnoreHiddenBodies = true;
                    try
                    {
                        var arr = mgr.GetInterferences() as object[] ?? new object[0];
                        var list = arr.Cast<IInterference>().Select(i => new
                        {
                            components = ((object[])i.Components ?? new object[0]).Cast<Component2>().Select(cp => cp.Name2).ToArray(),
                            volume_mm3 = Math.Round(i.Volume * 1e9, 3),
                        }).ToList();
                        return ToolOutput.Obj(new { interference_count = list.Count, interferences = list }, list.Count == 0 ? "No interferences" : $"{list.Count} interference(s)");
                    }
                    finally { mgr.Done(); }
                }, describe: a => "Check interference");
        }
    }

    internal static class DrawingTools
    {
        private static readonly double[] StdScales = { 10, 5, 4, 2, 1, 0.5, 0.4, 0.25, 0.2, 0.1, 0.05, 0.04, 0.02, 0.01 };

        private static string ResolveModelPath(SwSession s, Args a)
        {
            var p = a.Str("model_path");
            if (!string.IsNullOrWhiteSpace(p)) return Path.GetFullPath(p);
            var doc = s.ActiveDoc;
            if (doc != null && doc.GetType() != DocTypes.Drawing)
            {
                var path = doc.GetPathName();
                if (string.IsNullOrEmpty(path)) throw new ToolException("The active model has never been saved; drawings need a saved model file. Ask the user where to save it, then save_document with that path.");
                return path;
            }
            throw new ToolException("Pass model_path (a saved .sldprt/.sldasm).");
        }

        private static View FindView(DrawingDoc drw, string name)
        {
            var sheetViews = ((ModelDoc2)drw).Extension != null ? (object[])drw.GetViews() : null;
            foreach (object[] sheet in sheetViews ?? new object[0])
                foreach (View v in sheet.Skip(1))
                    if (string.Equals(v.GetName2(), name, StringComparison.OrdinalIgnoreCase)) return v;
            throw new ToolException($"No drawing view named '{name}'. Use list_drawing_views.");
        }

        private static double[] SheetSize(DrawingDoc drw)
        {
            var sheet = (Sheet)drw.GetCurrentSheet();
            var props = (double[])sheet.GetProperties2(); // [paper, template, scale1, scale2, firstAngle, width, height, ...]
            return new[] { props[5], props[6] };
        }

        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("create_drawing", "Create a new drawing of a saved part/assembly with a standard 3-view layout (front, top, right, projected and aligned) plus an isometric, auto-scaled to the sheet.",
                S().Str("model_path", "Saved model file; default: the active model.").Str("paper", "", false, PaperSizes.Keys.Select(k => k.ToUpperInvariant()))
                   .Bool("isometric", "Add an isometric view (default true).").Str("template", "Optional .drwdot path."),
                (a, c) =>
                {
                    var model = ResolveModelPath(s, a);
                    s.EnsureModelOpen(model);
                    var modelDoc = (ModelDoc2)s.Sw.GetOpenDocumentByName(model);
                    double[] box = null;
                    try { box = modelDoc.GetType() == DocTypes.Part ? (double[])((PartDoc)modelDoc).GetPartBox(true) : (double[])((AssemblyDoc)modelDoc).GetBox(0); } catch { }
                    var tpl = a.Str("template") ?? s.DefaultTemplate("drawing");
                    var drwDoc = Check(s.Sw.NewDocument(tpl, PaperSizes[a.Str("paper", "A3")], 0, 0) as ModelDoc2, "the drawing");
                    var title = drwDoc.GetTitle();
                    var drw = (DrawingDoc)drwDoc;
                    var size = SheetSize(drw);
                    double w = size[0], h = size[1];
                    double sx = 0.1, sy = 0.1, sz = 0.1;
                    if (box != null) { sx = box[3] - box[0]; sy = box[4] - box[1]; sz = box[5] - box[2]; }
                    var fit = Math.Min(0.55 * w / Math.Max(1e-6, sx + sz), 0.55 * h / Math.Max(1e-6, sy + sz));
                    var scale = StdScales.FirstOrDefault(x => x <= fit);
                    if (scale <= 0) scale = 0.01;
                    // third-angle layout: front bottom-left, top above, right to the right
                    double fx = 0.08 * w + scale * sx / 2, fy = 0.12 * h + scale * sy / 2;
                    var front = drw.CreateDrawViewFromModelView3(model, "*Front", fx, fy, 0) ?? throw new ToolException("Could not place the front view.");
                    front.ScaleDecimal = scale;
                    var placed = new List<string> { front.GetName2() };
                    drwDoc.ClearSelection2(true);
                    drwDoc.Extension.SelectByID2(front.GetName2(), "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
                    var top = drw.CreateUnfoldedViewAt3(fx, fy + scale * (sy / 2 + sz / 2) + 0.02, 0, false);
                    if (top != null) placed.Add(top.GetName2());
                    drwDoc.ClearSelection2(true);
                    drwDoc.Extension.SelectByID2(front.GetName2(), "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
                    var right = drw.CreateUnfoldedViewAt3(fx + scale * (sx / 2 + sz / 2) + 0.02, fy, 0, false);
                    if (right != null) placed.Add(right.GetName2());
                    if (a.Bool("isometric", true))
                    {
                        var iso = drw.CreateDrawViewFromModelView3(model, "*Isometric", Math.Min(0.8 * w, fx + scale * (sx + sz) + 0.06), Math.Min(0.75 * h, fy + scale * (sy + sz) / 1.4 + 0.03), 0);
                        if (iso != null) { iso.ScaleDecimal = scale; placed.Add(iso.GetName2()); }
                    }
                    drwDoc.ClearSelection2(true);
                    drwDoc = s.Activate(title) ?? drwDoc; // placing views can switch focus to the model
                    drwDoc.ViewZoomtofit2();
                    return ToolOutput.Obj(new { drawing = title, views = placed, scale = scale >= 1 ? $"{scale}:1" : $"1:{Math.Round(1 / scale, 2)}", sheet_mm = new[] { Math.Round(w * 1000), Math.Round(h * 1000) } },
                        $"Created drawing {title} with {placed.Count} views");
                }, modifies: true, describe: a => "Create drawing");

            yield return Tool("add_drawing_view", "Add a named model view to the active drawing at sheet position (x, y) mm.",
                S().Str("view", "", true, SwSession.NamedViews.Keys).Num("x", "mm", true).Num("y", "mm", true).Str("model_path", "Default: the model already shown on the sheet / active model.").Num("scale", "e.g. 0.5 for 1:2."),
                (a, c) =>
                {
                    var drwDoc = s.RequireDoc(DocTypes.Drawing);
                    var title = drwDoc.GetTitle();
                    var model = a.Str("model_path");
                    if (string.IsNullOrWhiteSpace(model))
                    {
                        var views = (object[])((DrawingDoc)drwDoc).GetViews();
                        var first = views?.Cast<object[]>().SelectMany(sh => sh.Skip(1)).Cast<View>().FirstOrDefault(v => !string.IsNullOrEmpty(v.GetReferencedModelName()));
                        model = first?.GetReferencedModelName() ?? throw new ToolException("Pass model_path.");
                    }
                    s.EnsureModelOpen(model);
                    drwDoc = s.Activate(title);
                    var v = ((DrawingDoc)drwDoc).CreateDrawViewFromModelView3(model, SwSession.NamedViews[a.Str("view").ToLowerInvariant()].name, a.Mm("x"), a.Mm("y"), 0)
                            ?? throw new ToolException("Could not place the view.");
                    if (a.Has("scale")) v.ScaleDecimal = a.Num("scale");
                    return ToolOutput.Obj(new { view = v.GetName2() }, $"Added {a.Str("view")} view {v.GetName2()}");
                }, modifies: true, describe: a => $"Add {a.Str("view")} view");

            yield return Tool("add_section_view", "Add a section view of an existing drawing view, cut by a horizontal or vertical line through the view (offset from its centre, sheet mm), placed at (x, y) sheet mm.",
                S().Str("view", "Parent view name.", true).Str("orientation", "", true, new[] { "horizontal", "vertical" }).Num("offset", "Offset of the cut line from the view centre, mm on the sheet.")
                   .Num("x", "mm", true).Num("y", "mm", true).Str("label", "Default A."),
                (a, c) =>
                {
                    var drwDoc = s.RequireDoc(DocTypes.Drawing);
                    var drw = (DrawingDoc)drwDoc;
                    var view = FindView(drw, a.RequireStr("view"));
                    var o = (double[])view.GetOutline(); // xmin, ymin, xmax, ymax (sheet metres)
                    double cx = (o[0] + o[2]) / 2, cy = (o[1] + o[3]) / 2, m = 0.005, off = a.Mm("offset");
                    drw.ActivateView(view.GetName2());
                    var sm = (SketchManager)drwDoc.SketchManager;
                    var line = a.Str("orientation") == "horizontal"
                        ? sm.CreateLine(o[0] - m, cy + off, 0, o[2] + m, cy + off, 0)
                        : sm.CreateLine(cx + off, o[1] - m, 0, cx + off, o[3] + m, 0);
                    if (line == null) throw new ToolException("Could not draw the section line.");
                    drwDoc.ClearSelection2(true);
                    line.Select4(false, null);
                    var sv = drw.CreateSectionViewAt5(a.Mm("x"), a.Mm("y"), 0, a.Str("label", "A"), 0, null, 0) ?? throw new ToolException("Could not create the section view.");
                    return ToolOutput.Obj(new { view = sv.GetName2() }, $"Added section {a.Str("label", "A")}-{a.Str("label", "A")}");
                }, modifies: true, describe: a => $"Section view of {a.Str("view")}");

            yield return Tool("add_detail_view", "Add a detail view: a circle (centre cx, cy and radius in sheet mm) on a parent view, magnified by `scale`, placed at (x, y) sheet mm.",
                S().Str("view", "Parent view name.", true).Num("cx", "", true).Num("cy", "", true).Num("radius", "", true, 0)
                   .Num("x", "", true).Num("y", "", true).Num("scale", "Detail scale relative to sheet, e.g. 2.").Str("label", "Default B."),
                (a, c) =>
                {
                    var drwDoc = s.RequireDoc(DocTypes.Drawing);
                    var drw = (DrawingDoc)drwDoc;
                    var view = FindView(drw, a.RequireStr("view"));
                    drw.ActivateView(view.GetName2());
                    var circle = ((SketchManager)drwDoc.SketchManager).CreateCircleByRadius(a.Mm("cx"), a.Mm("cy"), 0, a.Mm("radius")) ?? throw new ToolException("Could not draw the detail circle.");
                    drwDoc.ClearSelection2(true);
                    circle.Select4(false, null);
                    var sc = a.Num("scale", 2);
                    var dv = drw.CreateDetailViewAt4(a.Mm("x"), a.Mm("y"), 0, (int)swDetViewStyle_e.swDetViewSTANDARD, sc >= 1 ? sc : 1, sc >= 1 ? 1 : 1 / sc,
                        a.Str("label", "B"), (int)swDetCircleShowType_e.swDetCircleCIRCLE, true, false, false, 5) as View ?? throw new ToolException("Could not create the detail view.");
                    return ToolOutput.Obj(new { view = dv.GetName2() }, $"Added detail {a.Str("label", "B")}");
                }, modifies: true, describe: a => $"Detail view of {a.Str("view")}");

            yield return Tool("add_auxiliary_view", "Add an auxiliary view perpendicular to an edge picked in a drawing view at sheet point (edge_x, edge_y) mm, placed at (x, y) sheet mm.",
                S().Num("edge_x", "", true).Num("edge_y", "", true).Num("x", "", true).Num("y", "", true).Str("label", "Default C."),
                (a, c) =>
                {
                    var drwDoc = s.RequireDoc(DocTypes.Drawing);
                    drwDoc.ClearSelection2(true);
                    if (!drwDoc.Extension.SelectByID2("", "EDGE", a.Mm("edge_x"), a.Mm("edge_y"), 0, false, 0, null, 0))
                        throw new ToolException("No edge at that sheet point.");
                    var v = ((DrawingDoc)drwDoc).CreateAuxiliaryViewAt2(a.Mm("x"), a.Mm("y"), 0, false, a.Str("label", "C"), true, false) as View
                            ?? throw new ToolException("Could not create the auxiliary view (pick a straight edge).");
                    return ToolOutput.Obj(new { view = v.GetName2() }, "Added auxiliary view " + v.GetName2());
                }, modifies: true, describe: a => "Auxiliary view");

            yield return Tool("insert_model_dimensions", "Import the model's dimensions into the active drawing views.",
                S().Bool("all_views", "Default true."),
                (a, c) =>
                {
                    var drw = (DrawingDoc)s.RequireDoc(DocTypes.Drawing);
                    drw.InsertModelAnnotations3((int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                        (int)(swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing | swInsertAnnotation_e.swInsertHoleWizardProfileDimensions),
                        a.Bool("all_views", true), true, false, false);
                    return ToolOutput.Text("Model dimensions inserted.", "Inserted model dimensions");
                }, modifies: true, describe: a => "Insert model dimensions");

            yield return Tool("add_note", "Place a text note on the active drawing sheet at (x, y) mm.",
                S().Str("text", "", true).Num("x", "", true).Num("y", "", true),
                (a, c) =>
                {
                    var drwDoc = s.RequireDoc(DocTypes.Drawing);
                    drwDoc.ClearSelection2(true);
                    var note = drwDoc.InsertNote(a.RequireStr("text")) as Note ?? throw new ToolException("Could not add the note.");
                    ((Annotation)note.GetAnnotation()).SetPosition2(a.Mm("x"), a.Mm("y"), 0);
                    return ToolOutput.Text("Note added.", "Added note");
                }, modifies: true, describe: a => "Note");

            yield return Tool("list_drawing_views", "List sheets and views of the active drawing with scale and referenced model.",
                S(), (a, c) =>
                {
                    var drw = (DrawingDoc)s.RequireDoc(DocTypes.Drawing);
                    var outList = new List<object>();
                    foreach (object[] sheet in (object[])drw.GetViews() ?? new object[0])
                    {
                        var sh = (View)sheet[0];
                        foreach (View v in sheet.Skip(1))
                            outList.Add(new { sheet = sh.GetName2(), view = v.GetName2(), scale = Math.Round(v.ScaleDecimal, 4), model = v.GetReferencedModelName(), orientation = v.GetOrientationName() });
                    }
                    return ToolOutput.Obj(outList, $"Listed {outList.Count} views");
                }, describe: a => "List drawing views");

            yield return Tool("add_bom_table", "Insert a bill of materials for an assembly drawing view, anchored at (x, y) sheet mm (top-left).",
                S().Str("view", "View name of an assembly.", true).Num("x", "", true).Num("y", "", true).Str("type", "", false, new[] { "top_level", "parts_only", "indented" }),
                (a, c) =>
                {
                    var drw = (DrawingDoc)s.RequireDoc(DocTypes.Drawing);
                    var view = FindView(drw, a.RequireStr("view"));
                    var exe = s.Sw.GetExecutablePath();
                    var dir = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(exe) : exe;
                    var tpl = Directory.Exists(Path.Combine(dir, "lang"))
                        ? Directory.GetFiles(Path.Combine(dir, "lang"), "bom-standard.sldbomtbt", SearchOption.AllDirectories).OrderBy(p => p.IndexOf("english", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).FirstOrDefault()
                        : null;
                    var type = a.Str("type", "top_level") == "parts_only" ? swBomType_e.swBomType_PartsOnly : a.Str("type") == "indented" ? swBomType_e.swBomType_Indented : swBomType_e.swBomType_TopLevelOnly;
                    var bom = view.InsertBomTable4(true, a.Mm("x"), a.Mm("y"), (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft, (int)type, "", tpl ?? "", false,
                        (int)swNumberingType_e.swNumberingType_Detailed, false);
                    if (bom == null) throw new ToolException("Could not insert the BOM (the view must show an assembly).");
                    return ToolOutput.Text("BOM inserted.", "Inserted BOM");
                }, modifies: true, describe: a => "BOM table");
        }
    }
}
