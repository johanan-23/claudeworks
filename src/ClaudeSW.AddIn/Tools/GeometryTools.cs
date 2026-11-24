using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSW.AddIn.Sw;
using ClaudeSW.Core.Agent;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using static ClaudeSW.AddIn.Tools.K;

namespace ClaudeSW.AddIn.Tools
{
    /// <summary>Selection and geometry queries that return stable entity references.</summary>
    internal static class GeometryTools
    {
        private sealed class Candidate
        {
            public object Entity;
            public string Component;
            public double[] Center; // metres
            public double Area;
        }

        private static IEnumerable<(Body2 body, string comp)> AllBodies(ModelDoc2 doc, string componentFilter)
        {
            if (doc.GetType() == DocTypes.Part)
            {
                foreach (var b in SwSession.Bodies(doc)) yield return (b, null);
                yield break;
            }
            if (doc.GetType() != DocTypes.Assembly) throw new ToolException("Geometry search works in parts and assemblies.");
            var comps = ((AssemblyDoc)doc).GetComponents(false) as object[] ?? new object[0];
            foreach (Component2 c in comps)
            {
                if (c.IsSuppressed()) continue;
                if (componentFilter != null && c.Name2.IndexOf(componentFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                object info;
                var bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[];
                if (bodies == null) continue;
                foreach (Body2 b in bodies) yield return (b, c.Name2);
            }
        }

        private static double[] BoxCenter(double[] box) => box == null ? null : new[] { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };

        private static double Dist(double[] a, double[] b) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(a[i] - b[i], 2)));

        private static bool DirMatches(double[] v, string axis, bool signed)
        {
            if (v == null || string.IsNullOrEmpty(axis)) return true;
            var ax = axis.Trim().ToUpperInvariant();
            var sign = ax.StartsWith("-") ? -1 : 1;
            var name = ax.TrimStart('+', '-');
            var idx = name == "X" ? 0 : name == "Y" ? 1 : name == "Z" ? 2 : -1;
            if (idx < 0) throw new ToolException("Direction must be X, Y, Z (optionally with + or -).");
            var len = Math.Sqrt(v.Take(3).Sum(x => x * x));
            if (len < 1e-12) return false;
            var comp = v[idx] / len;
            return signed && (ax.StartsWith("+") || ax.StartsWith("-")) ? Math.Abs(comp - sign) < 1e-6 : Math.Abs(Math.Abs(comp) - 1) < 1e-6;
        }

        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("get_selection", "Describe what the user currently has selected in SOLIDWORKS and return references (S1, S2, ...).",
                S(), (a, c) =>
                {
                    var doc = s.RequireDoc();
                    var sel = s.CaptureSelection(doc);
                    return ToolOutput.Obj(new { count = sel.Count, items = sel.Select(x => new { @ref = x.Id, description = x.Description }) },
                        sel.Count == 0 ? "Nothing selected" : $"{sel.Count} item(s) selected");
                }, describe: a => "Read selection");

            yield return Tool("select_entities", "Select entities in SOLIDWORKS (highlights them for the user and prepares commands). Use refs, or names, or as a fallback entity_type + points.",
                S().Refs("What to select.").Points().Bool("append", "Add to the current selection.").Int("mark", "Selection mark for commands that need roles (advanced)."),
                (a, c) =>
                {
                    var doc = s.RequireDoc();
                    var n = s.SelectTargets(doc, a, "what to select", a.Int("mark"), a.Bool("append"));
                    return ToolOutput.Text($"Selected {n} item(s).", $"Selected {n} item(s)");
                }, describe: a => "Select " + string.Join(", ", a.Strs("refs")));

            yield return Tool("clear_selection", "Clear the selection.",
                S(), (a, c) => { s.RequireDoc().ClearSelection2(true); return ToolOutput.Text("Selection cleared.", "Cleared selection"); },
                describe: a => "Clear selection");

            yield return Tool("find_faces", "Find faces of the active part (or of assembly components) by criteria and return references (F1, F2, ...) with descriptions. Use it instead of guessing coordinates.",
                S().Str("surface", "Surface type.", false, new[] { "any", "plane", "cylinder", "cone", "sphere", "torus", "freeform" })
                   .Str("normal", "Planar faces whose outward normal is along +X, -X, +Y, -Y, +Z, -Z (or X/Y/Z for either sign).")
                   .Arr("near", "number", "Sort by distance from this point [x,y,z] mm.", false, 3, 3)
                   .Str("feature", "Only faces created by this feature.").Str("component", "Assemblies: only this component (name contains).")
                   .Num("min_area", "mm².", false, 0).Int("limit", "Max results (default 20).", false, 1, 200),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var surface = a.Str("surface", "any").ToLowerInvariant();
                    var normal = a.Str("normal");
                    var feature = a.Str("feature");
                    var minArea = a.Num("min_area") / 1e6;
                    var cands = new List<Candidate>();
                    foreach (var (body, comp) in AllBodies(doc, a.Str("component")))
                    {
                        foreach (Face2 f in (object[])body.GetFaces() ?? new object[0])
                        {
                            var srf = (Surface)f.GetSurface();
                            var kind = srf.IsPlane() ? "plane" : srf.IsCylinder() ? "cylinder" : srf.IsCone() ? "cone" : srf.IsSphere() ? "sphere" : srf.IsTorus() ? "torus" : "freeform";
                            if (surface != "any" && kind != surface) continue;
                            if (normal != null && (kind != "plane" || !DirMatches((double[])f.Normal, normal, true))) continue;
                            if (f.GetArea() < minArea) continue;
                            if (feature != null && !string.Equals((f.GetFeature() as Feature)?.Name, feature, StringComparison.OrdinalIgnoreCase)) continue;
                            cands.Add(new Candidate { Entity = f, Component = comp, Center = BoxCenter((double[])f.GetBox()), Area = f.GetArea() });
                        }
                    }
                    var near = a.Nums("near", 3);
                    IEnumerable<Candidate> ordered = near != null
                        ? cands.OrderBy(x => Dist(x.Center, near.Select(v => v / 1000).ToArray()))
                        : cands.OrderByDescending(x => x.Area);
                    s.ClearRefs("F");
                    var result = ordered.Take(a.Int("limit", 20)).Select(x =>
                    {
                        var desc = s.DescribeFace((Face2)x.Entity) + (x.Component != null ? " on " + x.Component : "");
                        return new { @ref = s.Register(doc, x.Entity, "face", "F", desc), description = desc };
                    }).ToList();
                    return ToolOutput.Obj(new { found = cands.Count, shown = result.Count, faces = result }, $"Found {cands.Count} face(s)");
                }, describe: a => "Find faces" + (a.Str("normal") != null ? " facing " + a.Str("normal") : "") + (a.Has("surface") ? " (" + a.Str("surface") + ")" : ""));

            yield return Tool("find_edges", "Find edges by criteria and return references (E1, E2, ...): straight edges along X/Y/Z, circular edges by diameter, edges of a face, near a point.",
                S().Str("kind", "Edge kind.", false, new[] { "any", "line", "circle", "other" })
                   .Str("direction", "Straight edges parallel to X, Y or Z.").Num("diameter", "Circular edges of this diameter (mm, ±0.01).", false, 0)
                   .Str("on_face", "Only edges bounding this face ref.").Arr("near", "number", "Sort by distance from [x,y,z] mm.", false, 3, 3)
                   .Str("component", "Assemblies: only this component.").Int("limit", "Max results (default 30).", false, 1, 300),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    var kind = a.Str("kind", "any").ToLowerInvariant();
                    var dir = a.Str("direction");
                    var dia = a.Has("diameter") ? a.Num("diameter") / 1000 : (double?)null;
                    IEnumerable<Edge> edges;
                    if (a.Has("on_face"))
                    {
                        var face = s.Resolve(doc, a.Str("on_face")) as Face2 ?? throw new ToolException("on_face must be a face reference.");
                        edges = ((object[])face.GetEdges() ?? new object[0]).Cast<Edge>();
                    }
                    else edges = AllBodies(doc, a.Str("component")).SelectMany(b => ((object[])b.body.GetEdges() ?? new object[0]).Cast<Edge>());

                    var cands = new List<Candidate>();
                    foreach (var e in edges)
                    {
                        var curve = (Curve)e.GetCurve();
                        var k = curve.IsLine() ? "line" : curve.IsCircle() ? "circle" : "other";
                        if (kind != "any" && k != kind) continue;
                        double[] center;
                        if (k == "line")
                        {
                            var sv = e.GetStartVertex() as Vertex; var ev = e.GetEndVertex() as Vertex;
                            if (sv == null || ev == null) continue;
                            var p0 = (double[])sv.GetPoint(); var p1 = (double[])ev.GetPoint();
                            if (dir != null && !DirMatches(new[] { p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2] }, dir, false)) continue;
                            center = new[] { (p0[0] + p1[0]) / 2, (p0[1] + p1[1]) / 2, (p0[2] + p1[2]) / 2 };
                        }
                        else
                        {
                            if (dir != null) continue;
                            if (k == "circle")
                            {
                                var cp = (double[])curve.CircleParams;
                                if (dia.HasValue && Math.Abs(cp[6] * 2 - dia.Value) > 1e-5) continue;
                                center = new[] { cp[0], cp[1], cp[2] };
                            }
                            else
                            {
                                if (dia.HasValue) continue;
                                var cpar = (double[])e.GetCurveParams2();
                                center = new[] { (cpar[0] + cpar[3]) / 2, (cpar[1] + cpar[4]) / 2, (cpar[2] + cpar[5]) / 2 };
                            }
                        }
                        cands.Add(new Candidate { Entity = e, Center = center });
                    }
                    var near = a.Nums("near", 3);
                    IEnumerable<Candidate> ordered = near != null
                        ? cands.OrderBy(x => Dist(x.Center, near.Select(v => v / 1000).ToArray()))
                        : cands.OrderBy(x => x.Center[2]).ThenBy(x => x.Center[1]).ThenBy(x => x.Center[0]);
                    s.ClearRefs("E");
                    var result = ordered.Take(a.Int("limit", 30)).Select(x =>
                    {
                        var desc = s.DescribeEdge((Edge)x.Entity);
                        return new { @ref = s.Register(doc, x.Entity, "edge", "E", desc), description = desc };
                    }).ToList();
                    return ToolOutput.Obj(new { found = cands.Count, shown = result.Count, edges = result }, $"Found {cands.Count} edge(s)");
                }, describe: a => "Find edges" + (a.Str("direction") != null ? " along " + a.Str("direction") : "") + (a.Has("kind") ? " (" + a.Str("kind") + ")" : ""));

            yield return Tool("measure", "Measure between two references (faces, edges, vertices, planes): minimum distance and, where defined, the angle.",
                S().Refs("Exactly two entities.", true),
                (a, c) =>
                {
                    var doc = s.RequireDoc(DocTypes.Part, DocTypes.Assembly);
                    doc.ClearSelection2(true);
                    s.SelectRefs(doc, a.Strs("refs").Take(2), false, 0);
                    var m = doc.Extension.CreateMeasure() as Measure ?? throw new ToolException("Measure is unavailable.");
                    if (!m.Calculate(null)) throw new ToolException("SOLIDWORKS could not measure those entities.");
                    var res = new Dictionary<string, object>();
                    if (m.Distance >= 0) res["distance_mm"] = Math.Round(m.Distance * 1000, 4);
                    if (m.Angle > 0) res["angle_deg"] = Math.Round(m.Angle * 180 / Math.PI, 4);
                    if (m.Length > 0) res["length_mm"] = Math.Round(m.Length * 1000, 4);
                    doc.ClearSelection2(true);
                    return ToolOutput.Obj(res, res.ContainsKey("distance_mm") ? $"Distance {F((double)res["distance_mm"])} mm" : "Measured");
                }, describe: a => "Measure " + string.Join(" ↔ ", a.Strs("refs")));
        }
    }
}
