using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using SolidWorks.Interop.sldworks;
using static ClaudeSW.AddIn.Tools.K;

namespace ClaudeSW.AddIn.Tools
{
    internal static class SketchTools
    {
        private static SketchManager Active(SwSession s, out ModelDoc2 doc)
        {
            doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
            var sm = (SketchManager)doc.SketchManager;
            if (sm.ActiveSketch == null) throw new ToolException("No sketch is open. Call begin_sketch first.");
            return sm;
        }

        /// <summary>Run with SOLIDWORKS' "modify dimension on create" popup disabled (it would block the API).</summary>
        internal static T WithoutDimPopup<T>(SwSession s, Func<T> f)
        {
            const int swInputDimValOnCreate = 10;
            var was = s.Sw.GetUserPreferenceToggle(swInputDimValOnCreate);
            if (was) s.Sw.SetUserPreferenceToggle(swInputDimValOnCreate, false);
            try { return f(); }
            finally { if (was) s.Sw.SetUserPreferenceToggle(swInputDimValOnCreate, true); }
        }

        /// <summary>Sketch (x, y) in metres -> model space, for placing dimension text.</summary>
        private static double[] ToModel(SwSession s, Sketch sketch, double x, double y)
        {
            var mu = (MathUtility)s.Sw.GetMathUtility();
            var pt = (MathPoint)mu.CreatePoint(new[] { x, y, 0.0 });
            var xf = ((MathTransform)sketch.ModelToSketchTransform).Inverse() as MathTransform;
            var r = (double[])((MathPoint)pt.MultiplyTransform(xf)).ArrayData;
            return r;
        }

        /// <summary>Add driving dimensions to freshly created geometry so drawings and edits have real dimensions.</summary>
        private static void AutoDimension(SwSession s, ModelDoc2 doc, IEnumerable<SketchSegment> segs, double offset)
        {
            var sketch = (Sketch)((SketchManager)doc.SketchManager).ActiveSketch;
            WithoutDimPopup(s, () =>
            {
                foreach (var seg in segs)
                {
                    try
                    {
                        double tx, ty;
                        if (seg is SketchLine line)
                        {
                            var a = (SketchPoint)line.GetStartPoint2();
                            var b = (SketchPoint)line.GetEndPoint2();
                            var horizontal = Math.Abs(a.Y - b.Y) < 1e-9;
                            tx = (a.X + b.X) / 2 + (horizontal ? 0 : offset * Math.Sign(a.X + b.X == 0 ? 1 : a.X + b.X));
                            ty = (a.Y + b.Y) / 2 + (horizontal ? offset * Math.Sign(a.Y + b.Y == 0 ? 1 : a.Y + b.Y) : 0);
                        }
                        else if (seg is SketchArc arc)
                        {
                            var c = (SketchPoint)arc.GetCenterPoint2();
                            var r = arc.GetRadius();
                            tx = c.X + r * 0.8 + offset * 0.5; ty = c.Y + r * 0.8 + offset * 0.5;
                        }
                        else continue;
                        doc.ClearSelection2(true);
                        seg.Select4(false, null);
                        var p = ToModel(s, sketch, tx, ty);
                        doc.AddDimension2(p[0], p[1], p[2]);
                    }
                    catch { /* dimensions are a nicety; never fail the geometry */ }
                }
                doc.ClearSelection2(true);
                return true;
            });
        }

        private static string ShortLabel(string id, string label)
        {
            var parts = (label ?? "").Split(',').Select(p => p.Trim()).ToList();
            var normal = parts.FirstOrDefault(p => p.StartsWith("normal "));
            return parts.Count > 0 ? $"{parts[0]} {id}{(normal != null ? " (" + normal + ")" : "")}" : id;
        }

        private static ToolOutput Segments(SwSession s, ModelDoc2 doc, object created, string what, string summary)
        {
            if (created == null) throw new ToolException($"SOLIDWORKS could not create the {what}. Check the coordinates (mm, sketch X/Y).");
            var items = created is object[] arr ? arr : new[] { created };
            var ids = items.Where(o => o is SketchSegment).Select(o => s.Register(doc, o, "sketch segment", "K", what)).ToList();
            return ToolOutput.Obj(new { created = what, segment_refs = ids }, summary);
        }

        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("begin_sketch", "Open a new sketch on a plane or planar face: plane = Front/Top/Right or a plane name, or pass `refs` with one planar face/plane reference. Sketch X/Y are in the sketch plane (mm).",
                S().Str("plane", "Front, Top, Right, or a reference plane name. Ignored when refs is given.").Refs("A planar face or plane to sketch on."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var sm = (SketchManager)doc.SketchManager;
                    if (sm.ActiveSketch != null) throw new ToolException("A sketch is already open. Call end_sketch first.");
                    var refs = a.Strs("refs");
                    string on;
                    if (refs.Count > 0)
                    {
                        doc.ClearSelection2(true);
                        s.SelectRefs(doc, refs.Take(1), false, 0);
                        on = s.IsRefId(refs[0]) ? ShortLabel(refs[0], s.GetRef(refs[0]).Label) : refs[0];
                    }
                    else
                    {
                        var plane = a.Str("plane", "Front");
                        var f = SwSession.PlaneAlias(doc, plane) ?? throw new ToolException($"Plane '{plane}' not found. Use Front, Top, Right, a plane name, or refs.");
                        doc.ClearSelection2(true);
                        f.Select2(false, 0);
                        on = f.Name;
                    }
                    sm.InsertSketch(true);
                    if (sm.ActiveSketch == null) throw new ToolException("Could not start a sketch there. Is it a plane or a planar face?");
                    s.ClearRefs("K");
                    return ToolOutput.Text($"Sketch open on {on}. Add geometry with add_* tools (mm, sketch X/Y), then end_sketch.", "Started sketch on " + on);
                }, modifies: true, describe: a => "Sketch on " + (a.Strs("refs").FirstOrDefault() ?? a.Str("plane", "Front")));

            yield return Tool("end_sketch", "Close the open sketch and return its name (feature tools use the last closed sketch by default).",
                S(), (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    sm.InsertSketch(true);
                    var f = s.LastFeatureOfType(doc, "ProfileFeature");
                    s.LastSketch = f?.Name;
                    return ToolOutput.Obj(new { sketch = s.LastSketch }, "Closed " + (s.LastSketch ?? "sketch"));
                }, modifies: true, describe: a => "Close sketch");

            yield return Tool("add_line", "Add a line (or construction centerline) to the open sketch. Coordinates in mm.",
                S().Num("x1", "", true).Num("y1", "", true).Num("x2", "", true).Num("y2", "", true).Bool("construction", "Centerline (e.g. a revolve axis)."),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    sm.AddToDB = true;
                    object seg = a.Bool("construction")
                        ? sm.CreateCenterLine(a.Mm("x1"), a.Mm("y1"), 0, a.Mm("x2"), a.Mm("y2"), 0)
                        : sm.CreateLine(a.Mm("x1"), a.Mm("y1"), 0, a.Mm("x2"), a.Mm("y2"), 0);
                    sm.AddToDB = false;
                    return Segments(s, doc, seg, a.Bool("construction") ? "centerline" : "line", "Added line");
                }, modifies: true, describe: a => $"Line ({F(a.Num("x1"))},{F(a.Num("y1"))})→({F(a.Num("x2"))},{F(a.Num("y2"))})");

            yield return Tool("add_circle", "Add a circle by centre and radius (mm).",
                S().Num("cx", "", true).Num("cy", "", true).Num("radius", "", true, 0).Bool("dimension", "Add a diameter dimension (default true)."),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    sm.AddToDB = true;
                    var seg = sm.CreateCircleByRadius(a.Mm("cx"), a.Mm("cy"), 0, a.Mm("radius"));
                    sm.AddToDB = false;
                    var result = Segments(s, doc, seg, "circle", $"Added Ø{F(a.Num("radius") * 2)} mm circle");
                    if (a.Bool("dimension", true)) AutoDimension(s, doc, new[] { seg }, Math.Max(0.005, a.Mm("radius") * 0.5));
                    return result;
                }, modifies: true, describe: a => $"Circle Ø{F(a.Num("radius") * 2)} mm");

            yield return Tool("add_rectangle", "Add a rectangle. Corner mode: two opposite corners. centered=true: (x1,y1) is the centre and (x2,y2) a corner.",
                S().Num("x1", "", true).Num("y1", "", true).Num("x2", "", true).Num("y2", "", true).Bool("centered", "Centre-point rectangle.").Bool("dimension", "Add width/height dimensions (default true)."),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    sm.AddToDB = true;
                    var r = a.Bool("centered")
                        ? sm.CreateCenterRectangle(a.Mm("x1"), a.Mm("y1"), 0, a.Mm("x2"), a.Mm("y2"), 0)
                        : sm.CreateCornerRectangle(a.Mm("x1"), a.Mm("y1"), 0, a.Mm("x2"), a.Mm("y2"), 0);
                    sm.AddToDB = false;
                    if (a.Bool("dimension", true) && r is object[] parts)
                    {
                        // One horizontal + one vertical edge (skip construction diagonals of centre rectangles).
                        var lines = parts.OfType<SketchLine>().Where(l => !((SketchSegment)l).ConstructionGeometry).ToList();
                        bool Horizontal(SketchLine l) => Math.Abs(((SketchPoint)l.GetStartPoint2()).Y - ((SketchPoint)l.GetEndPoint2()).Y) < 1e-9;
                        var pick = new[] { lines.FirstOrDefault(Horizontal), lines.FirstOrDefault(l => !Horizontal(l)) }.Where(l => l != null).Cast<SketchSegment>();
                        AutoDimension(s, doc, pick, 0.008);
                    }
                    var w = a.Bool("centered") ? 2 * Math.Abs(a.Num("x2") - a.Num("x1")) : Math.Abs(a.Num("x2") - a.Num("x1"));
                    var h = a.Bool("centered") ? 2 * Math.Abs(a.Num("y2") - a.Num("y1")) : Math.Abs(a.Num("y2") - a.Num("y1"));
                    return Segments(s, doc, r, "rectangle", $"Added {F(w)} × {F(h)} mm rectangle");
                }, modifies: true, describe: a => "Rectangle");

            yield return Tool("add_arc", "Add an arc by centre, start and end points (mm).",
                S().Num("cx", "", true).Num("cy", "", true).Num("x1", "", true).Num("y1", "", true).Num("x2", "", true).Num("y2", "", true).Bool("counter_clockwise", "Direction (default true)."),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    sm.AddToDB = true;
                    var seg = sm.CreateArc(a.Mm("cx"), a.Mm("cy"), 0, a.Mm("x1"), a.Mm("y1"), 0, a.Mm("x2"), a.Mm("y2"), 0, (short)(a.Bool("counter_clockwise", true) ? 1 : -1));
                    sm.AddToDB = false;
                    return Segments(s, doc, seg, "arc", "Added arc");
                }, modifies: true, describe: a => "Arc");

            yield return Tool("add_polygon", "Add a regular polygon centred at (cx, cy). radius is to the vertices when inscribed=true.",
                S().Num("cx", "", true).Num("cy", "", true).Num("radius", "", true, 0).Int("sides", "3-40 (default 6).", false, 3, 40)
                   .Num("rotation_deg", "Rotation of the first vertex.").Bool("inscribed", "Default true."),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    var ang = a.Deg("rotation_deg");
                    double cx = a.Mm("cx"), cy = a.Mm("cy"), r = a.Mm("radius");
                    sm.AddToDB = true;
                    var res = sm.CreatePolygon(cx, cy, 0, cx + r * Math.Cos(ang), cy + r * Math.Sin(ang), 0, a.Int("sides", 6), a.Bool("inscribed", true));
                    sm.AddToDB = false;
                    return Segments(s, doc, res, "polygon", $"Added {a.Int("sides", 6)}-sided polygon");
                }, modifies: true, describe: a => $"{a.Int("sides", 6)}-gon");

            yield return Tool("add_slot", "Add a straight slot between two centre points with the given width (mm).",
                S().Num("x1", "", true).Num("y1", "", true).Num("x2", "", true).Num("y2", "", true).Num("width", "", true, 0),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    sm.AddToDB = true;
                    var slot = sm.CreateSketchSlot(0 /*swSketchSlotCreationType_line*/, 0 /*center-center length*/, a.Mm("width"),
                        a.Mm("x1"), a.Mm("y1"), 0, a.Mm("x2"), a.Mm("y2"), 0, 0, 0, 0, 1, false);
                    sm.AddToDB = false;
                    if (slot == null) throw new ToolException("SOLIDWORKS could not create the slot.");
                    return ToolOutput.Text("Slot added.", $"Added {F(a.Num("width"))} mm slot");
                }, modifies: true, describe: a => "Slot");

            yield return Tool("add_sketch_point", "Add a sketch point (e.g. to position holes).",
                S().Num("x", "", true).Num("y", "", true),
                (a, c) =>
                {
                    var sm = Active(s, out var doc);
                    var p = sm.CreatePoint(a.Mm("x"), a.Mm("y"), 0) ?? throw new ToolException("Could not create the point.");
                    var id = s.Register(doc, p, "sketch point", "K", "point");
                    return ToolOutput.Obj(new { point_ref = id }, "Added sketch point");
                }, modifies: true, describe: a => $"Point ({F(a.Num("x"))},{F(a.Num("y"))})");

            yield return Tool("add_sketch_relation", "Apply a geometric relation to sketch entities given by `refs` (segment refs returned by add_* tools, e.g. \"K2\").",
                S().Str("relation", "", true, new[] { "horizontal", "vertical", "coincident", "tangent", "parallel", "perpendicular", "equal", "concentric", "fixed", "midpoint", "collinear", "symmetric" })
                   .Refs("Sketch segments/points.", true),
                (a, c) =>
                {
                    Active(s, out var doc);
                    var ids = new Dictionary<string, string>
                    {
                        ["horizontal"] = "sgHORIZONTAL2D", ["vertical"] = "sgVERTICAL2D", ["coincident"] = "sgCOINCIDENT", ["tangent"] = "sgTANGENT",
                        ["parallel"] = "sgPARALLEL", ["perpendicular"] = "sgPERPENDICULAR", ["equal"] = "sgSAMELENGTH", ["concentric"] = "sgCONCENTRIC",
                        ["fixed"] = "sgFIXED", ["midpoint"] = "sgATMIDDLE", ["collinear"] = "sgCOLINEAR", ["symmetric"] = "sgSYMMETRIC",
                    };
                    var rel = a.RequireStr("relation").ToLowerInvariant();
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, a.Strs("refs"), false, 0);
                    doc.SketchAddConstraints(ids[rel]);
                    doc.ClearSelection2(true);
                    return ToolOutput.Text($"Added {rel} relation.", $"Added {rel} relation");
                }, modifies: true, describe: a => a.Str("relation") + " relation");

            yield return Tool("add_sketch_dimension", "Dimension sketch entities (`refs`, 1-2 segments/points) and set the value (mm, or degrees for angular). The text is placed at (text_x, text_y) mm.",
                S().Refs("Entities to dimension.", true).Num("value", "", true).Num("text_x", "").Num("text_y", "").Bool("angular", "Angle between two lines."),
                (a, c) =>
                {
                    Active(s, out var doc);
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, a.Strs("refs"), false, 0);
                    var dd = WithoutDimPopup(s, () => doc.AddDimension2(a.Mm("text_x"), a.Mm("text_y"), 0)) as DisplayDimension
                             ?? throw new ToolException("Could not add the dimension (select 1-2 suitable sketch entities).");
                    var dim = dd.GetDimension2(0);
                    dim.SystemValue = a.Bool("angular") ? a.Deg("value") : a.Mm("value");
                    doc.EditRebuild3();
                    doc.ClearSelection2(true);
                    return ToolOutput.Obj(new { dimension = dim.FullName, value = a.Num("value") }, $"Dimensioned {F(a.Num("value"))}{(a.Bool("angular") ? "°" : " mm")}");
                }, modifies: true, describe: a => $"Dimension {F(a.Num("value"))}");
        }
    }
}
