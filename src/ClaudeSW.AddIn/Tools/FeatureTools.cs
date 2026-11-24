using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using static ClaudeSW.AddIn.Tools.K;

namespace ClaudeSW.AddIn.Tools
{
    internal static class FeatureTools
    {
        /// <summary>Make sure a profile sketch is selected for a feature command.</summary>
        private static string PrepareSketch(SwSession s, ModelDoc2 doc, string sketchName, int mark = 0)
        {
            var sm = (SketchManager)doc.SketchManager;
            if (!string.IsNullOrEmpty(sketchName))
            {
                if (sm.ActiveSketch != null) sm.InsertSketch(true);
                doc.ClearSelection2(true);
                var f = SwSession.FindFeature(doc, sketchName);
                f.Select2(false, mark);
                return f.Name;
            }
            if (sm.ActiveSketch != null)
            {
                sm.InsertSketch(true); // close it; then select it below
                s.LastSketch = s.LastFeatureOfType(doc, "ProfileFeature")?.Name;
            }
            if (s.LastSketch != null)
            {
                doc.ClearSelection2(true);
                try
                {
                    SwSession.FindFeature(doc, s.LastSketch).Select2(false, mark);
                    return s.LastSketch;
                }
                catch (ToolException) { /* deleted: fall through */ }
            }
            throw new ToolException("No sketch to use. Pass sketch_name, or create one with begin_sketch / end_sketch.");
        }

        private static void Used(SwSession s, string sketch)
        {
            if (sketch == s.LastSketch) s.LastSketch = null;
        }

        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            var directions = new[] { "one_side", "reverse", "both", "mid_plane", "through_all", "through_all_both", "up_to_next" };

            yield return Tool("extrude", "Extrude the last (or named) sketch as a boss, or as a cut (cut=true). depth in mm. 'both' uses depth and depth2; 'mid_plane' is symmetric; 'through_all' ignores depth (holes).",
                S().Num("depth", "Depth in mm.", false, 0).Bool("cut", "Cut instead of boss.").Str("sketch_name", "Sketch to use (default: the last one).")
                   .Str("direction", "End condition.", false, directions).Num("depth2", "Second-direction depth for 'both'.", false, 0)
                   .Num("draft_deg", "Draft angle.").Bool("merge", "Merge with existing bodies (default true)."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var sketch = PrepareSketch(s, doc, a.Str("sketch_name"));
                    var dir = a.Str("direction", "one_side").ToLowerInvariant();
                    var t1 = dir == "through_all" || dir == "through_all_both" ? (int)swEndConditions_e.swEndCondThroughAll
                        : dir == "mid_plane" ? (int)swEndConditions_e.swEndCondMidPlane
                        : dir == "up_to_next" ? (int)swEndConditions_e.swEndCondUpToNext
                        : (int)swEndConditions_e.swEndCondBlind;
                    var both = dir == "both" || dir == "through_all_both";
                    var t2 = dir == "through_all_both" ? (int)swEndConditions_e.swEndCondThroughAll : (int)swEndConditions_e.swEndCondBlind;
                    var d1 = a.Mm("depth", 10);
                    var d2 = dir == "both" ? a.Mm("depth2", a.Num("depth", 10)) : 0;
                    var flip = dir == "reverse";
                    var draft = Math.Abs(a.Num("draft_deg")) > 1e-9;
                    var ang = a.Deg("draft_deg");
                    var fm = (FeatureManager)doc.FeatureManager;
                    Feature feat = a.Bool("cut")
                        ? fm.FeatureCut4(true, flip, both, t1, t2, d1, d2, draft, draft && both, false, false, ang, ang, false, false, false, false,
                            false, true, true, true, true, false, (int)swStartConditions_e.swStartSketchPlane, 0, false, false)
                        : fm.FeatureExtrusion3(true, flip, both, t1, t2, d1, d2, draft, draft && both, false, false, ang, ang, false, false, false, false,
                            a.Bool("merge", true), true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
                    Used(s, sketch);
                    var how = t1 == (int)swEndConditions_e.swEndCondThroughAll ? "through all" : dir == "mid_plane" ? $"{F(a.Num("depth", 10))} mm (mid-plane)" : $"{F(a.Num("depth", 10))} mm";
                    return FeatureResult(s, doc, feat, a.Bool("cut") ? "cut-extrude" : "extrusion", $"{(a.Bool("cut") ? "Cut" : "Extruded")} {sketch} {how} → {{name}}");
                }, modifies: true,
                describe: a => $"{(a.Bool("cut") ? "Cut" : "Extrude")} {(a.Str("direction") == "through_all" ? "through all" : F(a.Num("depth", 10)) + " mm")}");

            yield return Tool("revolve", "Revolve the last (or named) sketch about a centerline in that sketch (add one with add_line construction=true). Boss or cut.",
                S().Num("angle_deg", "Default 360.", false, 0, 360).Bool("cut", "Revolved cut.").Str("sketch_name", "").Bool("reverse", "Reverse direction."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var sketch = PrepareSketch(s, doc, a.Str("sketch_name"));
                    var feat = ((FeatureManager)doc.FeatureManager).FeatureRevolve2(true, true, false, a.Bool("cut"), a.Bool("reverse"), false,
                        (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, a.Deg("angle_deg", 360), 0,
                        false, false, 0, 0, 0, 0, 0, true, true, true);
                    Used(s, sketch);
                    return FeatureResult(s, doc, feat, "revolve (does the sketch contain a centerline axis and a closed profile on one side of it?)", $"Revolved {sketch} {F(a.Num("angle_deg", 360))}° → {{name}}");
                }, modifies: true, describe: a => $"Revolve {F(a.Num("angle_deg", 360))}°");

            yield return Tool("fillet", "Constant-radius fillet on edges or faces (`refs`, e.g. from find_edges). Radius in mm.",
                S().Num("radius", "", true, 0).Refs("Edges/faces to fillet.").Points(),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var n = s.SelectTargets(doc, a, "the edges to fillet");
                    var feat = ((FeatureManager)doc.FeatureManager).FeatureFillet3(
                        (int)(swFeatureFilletOptions_e.swFeatureFilletPropagate | swFeatureFilletOptions_e.swFeatureFilletUniformRadius),
                        a.Mm("radius"), 0, 0, 0, 0, 0, null, null, null, null, null, null, null) as Feature;
                    return FeatureResult(s, doc, feat, "fillet (radius too large for the geometry?)", $"Filleted {n} edge(s) R{F(a.Num("radius"))} mm → {{name}}");
                }, modifies: true, describe: a => $"Fillet R{F(a.Num("radius"))} mm");

            yield return Tool("chamfer", "Chamfer edges (`refs`): distance in mm at angle_deg (45 = equal distance).",
                S().Num("distance", "", true, 0).Num("angle_deg", "Default 45.", false, 0, 90).Refs("Edges to chamfer.").Points(),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var n = s.SelectTargets(doc, a, "the edges to chamfer");
                    var feat = ((FeatureManager)doc.FeatureManager).InsertFeatureChamfer((int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation,
                        (int)swChamferType_e.swChamferAngleDistance, a.Mm("distance"), a.Deg("angle_deg", 45), 0, 0, 0, 0);
                    return FeatureResult(s, doc, feat, "chamfer", $"Chamfered {n} edge(s) {F(a.Num("distance"))} mm → {{name}}");
                }, modifies: true, describe: a => $"Chamfer {F(a.Num("distance"))} mm");

            yield return Tool("shell", "Hollow the body with the given wall thickness (mm), removing the faces in `refs` (open faces).",
                S().Num("thickness", "", true, 0).Bool("outward", "Grow walls outward.").Refs("Faces to remove.").Points(),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    s.SelectTargets(doc, a, "the faces to remove");
                    doc.InsertFeatureShell(a.Mm("thickness"), a.Bool("outward"));
                    var f = s.LastFeatureOfType(doc, "Shell");
                    return FeatureResult(s, doc, f, "shell (wall too thick?)", $"Shelled to {F(a.Num("thickness"))} mm walls → {{name}}");
                }, modifies: true, describe: a => $"Shell {F(a.Num("thickness"))} mm");

            yield return Tool("linear_pattern", "Linear pattern of features along a direction edge/axis/plane. Optionally a second direction.",
                S().Arr("features", "string", "Feature names to repeat.", true).Str("direction", "Reference for direction 1 (edge/axis ref or name).", true)
                   .Int("count", "Instances incl. the original.", true, 2).Num("spacing", "mm", true, 0).Bool("reverse", "")
                   .Str("direction2", "Optional second direction ref.").Int("count2", "", false, 1).Num("spacing2", "mm", false, 0).Bool("reverse2", ""),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, new[] { a.RequireStr("direction") }, false, 1);
                    var hasDir2 = a.Has("direction2");
                    if (hasDir2) s.SelectRefs(doc, new[] { a.Str("direction2") }, true, 2);
                    s.SelectRefs(doc, a.Strs("features"), true, 4);
                    var feat = ((FeatureManager)doc.FeatureManager).FeatureLinearPattern5(a.Int("count"), a.Mm("spacing"),
                        hasDir2 ? a.Int("count2", 2) : 1, hasDir2 ? a.Mm("spacing2") : 0, a.Bool("reverse"), a.Bool("reverse2"),
                        "NULL", "NULL", false, false, false, false, false, false, false, false, false, false, 0, 0, false, false);
                    return FeatureResult(s, doc, feat, "linear pattern", $"Patterned {a.Int("count")}× at {F(a.Num("spacing"))} mm → {{name}}");
                }, modifies: true, describe: a => $"Linear pattern ×{a.Int("count")}");

            yield return Tool("circular_pattern", "Circular pattern of features about an axis, cylindrical face or circular edge.",
                S().Arr("features", "string", "Feature names to repeat.", true).Str("axis", "Axis/edge/cylindrical-face ref or axis name.", true)
                   .Int("count", "Instances incl. the original.", true, 2).Num("angle_deg", "Total angle when equal_spacing (default 360), else spacing angle.")
                   .Bool("equal_spacing", "Default true.").Bool("reverse", ""),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, new[] { a.RequireStr("axis") }, false, 1);
                    s.SelectRefs(doc, a.Strs("features"), true, 4);
                    var feat = ((FeatureManager)doc.FeatureManager).FeatureCircularPattern5(a.Int("count"), a.Deg("angle_deg", 360), a.Bool("reverse"), "NULL",
                        false, a.Bool("equal_spacing", true), false, false, false, false, 1, 0, "NULL", false);
                    return FeatureResult(s, doc, feat, "circular pattern", $"Circular pattern ×{a.Int("count")} → {{name}}");
                }, modifies: true, describe: a => $"Circular pattern ×{a.Int("count")}");

            yield return Tool("mirror", "Mirror features (or the whole body with mirror_body=true) about a plane or planar face.",
                S().Str("plane", "Mirror plane: Front/Top/Right, plane name or face ref.", true).Arr("features", "string", "Features to mirror (ignored with mirror_body).")
                   .Bool("mirror_body", "Mirror the solid body instead of features.").Bool("merge", "Merge mirrored body (default true)."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, new[] { a.RequireStr("plane") }, false, 2);
                    var body = a.Bool("mirror_body");
                    if (body)
                    {
                        var b = SwSession.Bodies(doc).FirstOrDefault() ?? throw new ToolException("The part has no solid body.");
                        b.Select2(true, s.NewSelectData(doc, 1));
                    }
                    else
                    {
                        if (a.Strs("features").Count == 0) throw new ToolException("Pass `features` to mirror, or mirror_body=true.");
                        s.SelectRefs(doc, a.Strs("features"), true, 1);
                    }
                    var feat = ((FeatureManager)doc.FeatureManager).InsertMirrorFeature2(body, false, a.Bool("merge", true), false, 0);
                    return FeatureResult(s, doc, feat, "mirror", $"Mirrored about {a.Str("plane")} → {{name}}");
                }, modifies: true, describe: a => "Mirror about " + a.Str("plane"));

            yield return Tool("sweep", "Sweep a closed profile sketch along a path sketch. Or use circular_diameter (mm) with only a path for a round sweep (tubes, wires). Boss or cut.",
                S().Str("path_sketch", "Path sketch name.", true).Str("profile_sketch", "Profile sketch name (omit when using circular_diameter).")
                   .Num("circular_diameter", "Diameter of a circular profile, mm.", false, 0).Bool("cut", ""),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var sm = (SketchManager)doc.SketchManager;
                    if (sm.ActiveSketch != null) sm.InsertSketch(true);
                    doc.ClearSelection2(true);
                    var circular = a.Has("circular_diameter");
                    if (!circular) SwSession.FindFeature(doc, a.Str("profile_sketch") ?? throw new ToolException("Pass profile_sketch or circular_diameter.")).Select2(false, 1);
                    SwSession.FindFeature(doc, a.RequireStr("path_sketch")).Select2(!circular, 4);
                    var fm = (FeatureManager)doc.FeatureManager;
                    var feat = a.Bool("cut")
                        ? fm.InsertCutSwept5(false, true, 0, false, false, 0, 0, false, 0, 0, 0, 0, true, true, 0, true, true, true, false, circular, a.Mm("circular_diameter"), 0)
                        : fm.InsertProtrusionSwept4(false, true, 0, false, false, 0, 0, false, 0, 0, 0, 0, true, true, true, 0, true, circular, a.Mm("circular_diameter"), 0);
                    return FeatureResult(s, doc, feat, "sweep (profile must be closed and pierce the path start)", "Swept along " + a.Str("path_sketch") + " → {name}");
                }, modifies: true, describe: a => "Sweep along " + a.Str("path_sketch"));

            yield return Tool("loft", "Loft through two or more closed profile sketches (in order).",
                S().Arr("profiles", "string", "Profile sketch names in order.", true, 2),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var sm = (SketchManager)doc.SketchManager;
                    if (sm.ActiveSketch != null) sm.InsertSketch(true);
                    doc.ClearSelection2(true);
                    var i = 0;
                    foreach (var p in a.Strs("profiles")) SwSession.FindFeature(doc, p).Select2(i++ > 0, 1);
                    var feat = ((FeatureManager)doc.FeatureManager).InsertProtrusionBlend2(false, true, false, 1, 0, 0, 1, 1, true, true, false, 0, 0, 0, true, true, true, 0);
                    return FeatureResult(s, doc, feat, "loft", $"Lofted {i} profiles → {{name}}");
                }, modifies: true, describe: a => $"Loft {a.Strs("profiles").Count} profiles");

            yield return Tool("hole_wizard", "Hole Wizard simple drilled hole on a planar face at a point (mm, model space, lying on the face).",
                S().Str("face", "Face ref (planar).", true).Arr("position", "number", "[x, y, z] mm on the face.", true, 3, 3)
                   .Num("diameter", "mm", true, 0).Num("depth", "mm (ignored if through_all).", false, 0).Bool("through_all", "")
                   .Str("standard", "Hole standard.", false, new[] { "iso", "ansi_metric", "ansi_inch" }),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part);
                    var p = a.Nums("position", 3);
                    var face = s.Resolve(doc, a.RequireStr("face")) as Face2 ?? throw new ToolException("`face` must be a face reference.");
                    doc.ClearSelection2(true);
                    // Select the face at the requested position: the hole is placed at the pick point.
                    var cp = (double[])face.GetClosestPointOn(p[0] / 1000, p[1] / 1000, p[2] / 1000);
                    if (!doc.Extension.SelectByID2("", "FACE", cp[0], cp[1], cp[2], false, 0, null, 0) && !s.SelectByPoint(doc, "FACE", p[0], p[1], p[2], false, 0))
                        throw new ToolException("Could not pick the face at that position.");
                    var std = a.Str("standard", "iso");
                    int stdIdx = std == "ansi_inch" ? (int)swWzdHoleStandards_e.swStandardAnsiInch : std == "ansi_metric" ? (int)swWzdHoleStandards_e.swStandardAnsiMetric : (int)swWzdHoleStandards_e.swStandardISO;
                    int fast = std == "ansi_inch" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchAllDrillSizes : std == "ansi_metric" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricDrillSizes : (int)swWzdHoleStandardFastenerTypes_e.swStandardISODrillSizes;
                    var end = a.Bool("through_all") ? (short)swEndConditions_e.swEndCondThroughAll : (short)swEndConditions_e.swEndCondBlind;
                    var size = "Ø" + a.Num("diameter").ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    var feat = ((FeatureManager)doc.FeatureManager).HoleWizard5((int)swWzdGeneralHoleTypes_e.swWzdHole, stdIdx, fast, size, end,
                        a.Mm("diameter"), a.Mm("depth", 10), -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, "", false, true, true, true, true, false);
                    return FeatureResult(s, doc, feat, "hole (is the face planar and the point on it?)", $"Hole Ø{F(a.Num("diameter"))} mm → {{name}}");
                }, modifies: true, describe: a => $"Hole Ø{F(a.Num("diameter"))} mm");

            yield return Tool("reference_plane", "Create a reference plane offset from a plane/planar face, or at an angle about an edge/axis, or mid-way between two faces.",
                S().Str("mode", "", true, new[] { "offset", "angle", "midplane" }).Str("reference", "Plane name or face ref.", true)
                   .Str("second_reference", "Edge/axis (angle) or second face (midplane).").Num("distance", "mm for offset.")
                   .Num("angle_deg", "for angle.").Bool("flip", "Other side."),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var mode = a.RequireStr("mode").ToLowerInvariant();
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, new[] { a.RequireStr("reference") }, false, 0);
                    var flip = a.Bool("flip") ? (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip : 0;
                    object plane;
                    var fm = (FeatureManager)doc.FeatureManager;
                    if (mode == "offset")
                        plane = fm.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance | flip, a.Mm("distance", 10), 0, 0, 0, 0);
                    else
                    {
                        var second = a.Str("second_reference") ?? throw new ToolException("Pass second_reference.");
                        s.SelectRefs(doc, new[] { second }, true, 1);
                        plane = mode == "angle"
                            ? fm.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle | flip, a.Deg("angle_deg", 45), (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0, 0, 0)
                            : fm.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_MidPlane, 0, (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_MidPlane, 0, 0, 0);
                    }
                    return FeatureResult(s, doc, plane as Feature, "reference plane", "Created plane {name}");
                }, modifies: true, describe: a => $"{a.Str("mode")} plane");

            yield return Tool("reference_axis", "Create a reference axis from two planes, a cylindrical face, or two vertices (`refs`).",
                S().Refs("1-2 entities defining the axis.", true),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, a.Strs("refs"), false, 0);
                    if (!doc.InsertAxis2(true)) throw new ToolException("Could not create the axis from those references.");
                    var f = s.LastFeatureOfType(doc, "RefAxis");
                    return FeatureResult(s, doc, f, "axis", "Created axis {name}");
                }, modifies: true, describe: a => "Reference axis");
        }
    }
}
