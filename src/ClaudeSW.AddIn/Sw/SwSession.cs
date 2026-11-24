using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Storage;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ClaudeSW.AddIn.Sw
{
    public static class DocTypes
    {
        public const int Part = (int)swDocumentTypes_e.swDocPART;
        public const int Assembly = (int)swDocumentTypes_e.swDocASSEMBLY;
        public const int Drawing = (int)swDocumentTypes_e.swDocDRAWING;

        public static string Name(int t) => t == Part ? "part" : t == Assembly ? "assembly" : t == Drawing ? "drawing" : "unknown";

        public static int FromPath(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".sldprt": return Part;
                case ".sldasm": return Assembly;
                case ".slddrw": return Drawing;
                default: throw new ToolException("Path must end in .sldprt, .sldasm or .slddrw.");
            }
        }
    }

    /// <summary>A stable reference to a model entity that survives rebuilds (persistent ID).</summary>
    public sealed class EntityRef
    {
        public string Id;
        public string Kind;
        public string DocTitle;
        public object PersistId;
        public object Live;
        public string Label;
    }

    /// <summary>Shared state and helpers for SOLIDWORKS tools. Everything here runs on the SW UI thread.</summary>
    public sealed class SwSession
    {
        public ISldWorks Sw { get; }
        public string LastSketch { get; set; }
        private readonly Dictionary<string, EntityRef> _refs = new Dictionary<string, EntityRef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>();

        public SwSession(ISldWorks sw) { Sw = sw; }

        // ------------------------------------------------------------------ documents

        public ModelDoc2 ActiveDoc => Sw.ActiveDoc as ModelDoc2;

        public ModelDoc2 RequireDoc(params int[] allowed)
        {
            var doc = ActiveDoc ?? throw new ToolException("No document is open. Use new_document or open_document first.");
            var t = doc.GetType();
            if (allowed.Length > 0 && !allowed.Contains(t))
                throw new ToolException($"This needs an active {string.Join(" or ", allowed.Select(DocTypes.Name))}, but the active document is a {DocTypes.Name(t)} (\"{doc.GetTitle()}\"). Use activate_document or open the right file.");
            return doc;
        }

        public static object Summary(ModelDoc2 doc) => new
        {
            title = doc.GetTitle(),
            path = string.IsNullOrEmpty(doc.GetPathName()) ? null : doc.GetPathName(),
            type = DocTypes.Name(doc.GetType()),
            unsaved_changes = doc.GetSaveFlag(),
        };

        public IEnumerable<ModelDoc2> OpenDocuments()
        {
            var d = Sw.GetFirstDocument() as ModelDoc2;
            while (d != null)
            {
                yield return d;
                d = d.GetNext() as ModelDoc2;
            }
        }

        public ModelDoc2 Activate(string title)
        {
            int errs = 0;
            return Sw.ActivateDoc3(title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errs) as ModelDoc2;
        }

        public string DefaultTemplate(string kind)
        {
            var pref = kind == "part" ? swUserPreferenceStringValue_e.swDefaultTemplatePart
                : kind == "assembly" ? swUserPreferenceStringValue_e.swDefaultTemplateAssembly
                : swUserPreferenceStringValue_e.swDefaultTemplateDrawing;
            var tpl = Sw.GetUserPreferenceStringValue((int)pref);
            if (!string.IsNullOrEmpty(tpl) && File.Exists(tpl)) return tpl;
            var file = kind == "part" ? "Part.PRTDOT" : kind == "assembly" ? "Assembly.ASMDOT" : "Drawing.DRWDOT";
            var root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "SOLIDWORKS");
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root, "SOLIDWORKS *").OrderByDescending(d => d))
                {
                    var p = Path.Combine(dir, "templates", file);
                    if (File.Exists(p)) return p;
                }
            }
            throw new ToolException($"No default {kind} template found. Set one in SOLIDWORKS (Tools > Options > Default Templates) or pass `template` with a full path.");
        }

        public void EnsureModelOpen(string path)
        {
            if (Sw.GetOpenDocumentByName(path) != null) return;
            int errs = 0, warns = 0;
            var doc = Sw.OpenDoc6(path, DocTypes.FromPath(path), (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns);
            if (doc == null) throw new ToolException($"Could not open '{path}' (error {errs}). Check the path; the file must be saved to disk.");
        }

        public static IEnumerable<Feature> Features(ModelDoc2 doc)
        {
            var f = doc.FirstFeature() as Feature;
            while (f != null)
            {
                yield return f;
                f = f.GetNextFeature() as Feature;
            }
        }

        public static Feature FindFeature(ModelDoc2 doc, string name)
        {
            var f = Features(doc).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (f != null) return f;
            // sub-features (sketches under features, etc.)
            foreach (var top in Features(doc))
            {
                var sub = top.GetFirstSubFeature() as Feature;
                while (sub != null)
                {
                    if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase)) return sub;
                    sub = sub.GetNextSubFeature() as Feature;
                }
            }
            throw new ToolException($"No feature named '{name}'. Use list_features to see names.");
        }

        public static readonly HashSet<string> HousekeepingTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "HistoryFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "SurfaceBodyFolder", "SolidBodyFolder",
            "MaterialFolder", "EqnFolder", "InkMarkupFolder", "CommentsFolder", "FavoriteFolder", "SelectionSetFolder",
            "OriginProfileFeature", "MateReferenceGroupFolder", "MateGroup", "ProfileFeatureFolder", "Attribute", "NotesFolder",
            "BlockFolder", "ExplodeFolder", "LiveSectionFolder", "MarkupCommentFolder", "RefAxisFolder", "CutListFolder",
            "SubAtomFolder", "DimXpertManager", "MBD3DViewFolder", "TableFolder", "GeneralTableFeature", "DesignBinderFolder",
            "SelectionSetFolder", "EnvFolder", "AmbientLight", "DirectionLight", "CameraFolder", "Light"
        };

        public static bool IsHousekeeping(Feature f)
        {
            var t = f.GetTypeName2();
            return HousekeepingTypes.Contains(t) || t.EndsWith("Folder", StringComparison.Ordinal);
        }

        public Feature LastFeatureOfType(ModelDoc2 doc, string typeName) =>
            Features(doc).LastOrDefault(f => f.GetTypeName2() == typeName);

        public static string Mm(double metres) => (metres * 1000).ToString("0.###", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ entity references

        public string Register(ModelDoc2 doc, object obj, string kind, string prefix, string label)
        {
            if (obj == null) return null;
            object pid = null;
            try { pid = doc.Extension.GetPersistReference3(obj); } catch { /* some objects have none */ }
            _counters.TryGetValue(prefix, out var n);
            _counters[prefix] = ++n;
            var id = prefix + n;
            _refs[id] = new EntityRef { Id = id, Kind = kind, DocTitle = doc.GetTitle(), PersistId = pid, Live = obj, Label = label };
            return id;
        }

        public EntityRef GetRef(string id) =>
            _refs.TryGetValue((id ?? "").Trim(), out var r) ? r : throw new ToolException($"Unknown reference '{id}'. Use references from the latest context, find_faces or find_edges.");

        public object Resolve(ModelDoc2 doc, string id)
        {
            var r = GetRef(id);
            if (!string.Equals(r.DocTitle, doc.GetTitle(), StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"Reference {r.Id} belongs to \"{r.DocTitle}\", not the active document \"{doc.GetTitle()}\".");
            if (r.PersistId != null)
            {
                int err = 0;
                try
                {
                    var o = doc.Extension.GetObjectByPersistReference3(r.PersistId, out err);
                    if (o != null) return o;
                }
                catch { /* fall back to the live object */ }
                if (err == (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Deleted)
                    throw new ToolException($"{r.Id} ({r.Label}) no longer exists in the model (it was consumed or deleted). Find it again with find_faces/find_edges.");
            }
            return r.Live ?? throw new ToolException($"{r.Id} can no longer be found.");
        }

        public void ClearRefs(string prefix)
        {
            foreach (var k in _refs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length && char.IsDigit(k[prefix.Length])).ToList())
                _refs.Remove(k);
            _counters[prefix] = 0;
        }

        // ------------------------------------------------------------------ selection

        public SelectData NewSelectData(ModelDoc2 doc, int mark)
        {
            var sd = ((SelectionMgr)doc.SelectionManager).CreateSelectData();
            sd.Mark = mark;
            return sd;
        }

        public bool SelectObject(ModelDoc2 doc, object o, bool append, int mark)
        {
            var sd = NewSelectData(doc, mark);
            switch (o)
            {
                case Feature f: return f.Select2(append, mark);
                case Component2 c: return c.Select4(append, sd, false);
                case SketchSegment s: return s.Select4(append, sd);
                case SketchPoint p: return p.Select4(append, sd);
                case Entity e: return e.Select4(append, sd);
                case View v: return doc.Extension.SelectByID2(v.GetName2(), "DRAWINGVIEW", 0, 0, 0, append, mark, null, 0);
                default:
                    if (o is Face2 face) return ((Entity)face).Select4(append, sd);
                    if (o is Edge edge) return ((Entity)edge).Select4(append, sd);
                    return false;
            }
        }

        /// <summary>Select references ("S1", "F3", "E7", or plain feature/component/plane names).</summary>
        public int SelectRefs(ModelDoc2 doc, IEnumerable<string> refs, bool append, int mark)
        {
            var count = 0;
            foreach (var r in refs)
            {
                var first = !append && count == 0;
                if (IsRefId(r))
                {
                    var o = Resolve(doc, r);
                    if (!SelectObject(doc, o, !first, mark)) throw new ToolException($"Could not select {r} ({GetRef(r).Label}).");
                }
                else if (!SelectByName(doc, r, !first, mark))
                    throw new ToolException($"Nothing named '{r}' to select (feature, plane, sketch, component or axis).");
                count++;
            }
            return count;
        }

        public bool IsRefId(string s) => !string.IsNullOrEmpty(s) && _refs.ContainsKey(s.Trim());

        public bool SelectByName(ModelDoc2 doc, string name, bool append, int mark)
        {
            var alias = PlaneAlias(doc, name);
            if (alias != null) return alias.Select2(append, mark);
            var types = doc.GetType() == DocTypes.Assembly
                ? new[] { "COMPONENT", "PLANE", "BODYFEATURE", "SKETCH", "AXIS", "MATE" }
                : doc.GetType() == DocTypes.Drawing ? new[] { "DRAWINGVIEW", "SHEET", "NOTE" }
                : new[] { "PLANE", "BODYFEATURE", "SKETCH", "AXIS", "REFCURVE", "SOLIDBODY" };
            foreach (var t in types)
            {
                var n = t == "COMPONENT" && !name.Contains("@") ? name + "@" + Path.GetFileNameWithoutExtension(doc.GetTitle()) : name;
                if (doc.Extension.SelectByID2(n, t, 0, 0, 0, append, mark, null, 0)) return true;
            }
            try
            {
                var f = FindFeature(doc, name);
                return f.Select2(append, mark);
            }
            catch (ToolException) { return false; }
        }

        /// <summary>Front/Top/Right resolve to the first three reference planes (language independent).</summary>
        public static Feature PlaneAlias(ModelDoc2 doc, string name)
        {
            var key = (name ?? "").Trim().ToLowerInvariant().Replace(" plane", "");
            var idx = key == "front" ? 0 : key == "top" ? 1 : key == "right" ? 2 : -1;
            var planes = Features(doc).Where(f => f.GetTypeName2() == "RefPlane").ToList();
            var exact = planes.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            return idx >= 0 && planes.Count > idx ? planes[idx] : null;
        }

        /// <summary>Select by a model-space point (mm). Falls back to nearest-geometry picking in parts.</summary>
        public bool SelectByPoint(ModelDoc2 doc, string type, double xMm, double yMm, double zMm, bool append, int mark)
        {
            double x = xMm / 1000, y = yMm / 1000, z = zMm / 1000;
            var nudges = new List<double[]> { new double[] { 0, 0, 0 } };
            foreach (var a in Enumerable.Range(0, 3))
                foreach (var d in new[] { 1e-6, -1e-6 })
                    nudges.Add(Enumerable.Range(0, 3).Select(i => i == a ? d : 0.0).ToArray());
            foreach (var n in nudges)
                if (doc.Extension.SelectByID2("", type, x + n[0], y + n[1], z + n[2], append, mark, null, 0)) return true;
            if (doc.GetType() == DocTypes.Part && (type == "FACE" || type == "EDGE" || type == "VERTEX"))
            {
                var best = NearestEntity(doc, type, x, y, z, 1e-5);
                if (best != null) return ((Entity)best).Select4(append, NewSelectData(doc, mark));
            }
            return false;
        }

        public static object NearestEntity(ModelDoc2 doc, string type, double x, double y, double z, double tol)
        {
            object best = null;
            var bestD = tol;
            foreach (Body2 b in Bodies(doc))
            {
                IEnumerable<object> ents = type == "FACE" ? (object[])b.GetFaces() ?? new object[0]
                    : type == "EDGE" ? (object[])b.GetEdges() ?? new object[0]
                    : (object[])b.GetVertices() ?? new object[0];
                foreach (var e in ents)
                {
                    double[] cp = type == "FACE" ? (double[])((Face2)e).GetClosestPointOn(x, y, z)
                        : type == "EDGE" ? (double[])((Edge)e).GetClosestPointOn(x, y, z)
                        : (double[])((Vertex)e).GetPoint();
                    if (cp == null) continue;
                    var d = Math.Sqrt(Math.Pow(cp[0] - x, 2) + Math.Pow(cp[1] - y, 2) + Math.Pow(cp[2] - z, 2));
                    if (d < bestD) { best = e; bestD = d; }
                }
            }
            return best;
        }

        public static IEnumerable<Body2> Bodies(ModelDoc2 doc)
        {
            if (doc.GetType() != DocTypes.Part) yield break;
            var arr = ((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (arr == null) yield break;
            foreach (var b in arr) yield return (Body2)b;
        }

        /// <summary>
        /// Common selection input used by many tools: "refs" (references or names), else
        /// "entity_type" with "points" ([[x,y,z],...] mm), else whatever the user has selected.
        /// </summary>
        public int SelectTargets(ModelDoc2 doc, Args a, string what, int mark = 0, bool append = false, string refsKey = "refs")
        {
            var refs = a.Strs(refsKey);
            if (refs.Count > 0)
            {
                if (!append) doc.ClearSelection2(true);
                return SelectRefs(doc, refs, append, mark);
            }
            if (a.Has("points"))
            {
                var type = (a.Str("entity_type", "FACE") ?? "FACE").ToUpperInvariant();
                if (!append) doc.ClearSelection2(true);
                var i = 0;
                foreach (var p in PointList(a, "points"))
                {
                    if (!SelectByPoint(doc, type, p[0], p[1], p[2], append || i > 0, mark))
                        throw new ToolException($"Could not find a {type} at ({p[0]}, {p[1]}, {p[2]}) mm. Use find_faces/find_edges to get references instead.");
                    i++;
                }
                return i;
            }
            var count = ((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1);
            if (count < 1) throw new ToolException($"Nothing selected. Pass `refs` for {what} (from the context, find_faces or find_edges).");
            if (mark != 0)
            {
                // Re-mark the user's current selection.
                var sm = (SelectionMgr)doc.SelectionManager;
                var objs = Enumerable.Range(1, count).Select(i => sm.GetSelectedObject6(i, -1)).ToList();
                doc.ClearSelection2(true);
                foreach (var o in objs) SelectObject(doc, o, true, mark);
            }
            return count;
        }

        public static List<double[]> PointList(Args a, string key)
        {
            var result = new List<double[]>();
            var v = a.Raw.TryGetProperty(key, out var el) ? el : default;
            if (v.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var p in v.EnumerateArray())
            {
                var arr = p.EnumerateArray().Select(e => e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDouble() : double.Parse(e.GetString(), CultureInfo.InvariantCulture)).ToArray();
                if (arr.Length != 3) throw new ToolException($"Each entry of '{key}' must be [x, y, z] in mm.");
                result.Add(arr);
            }
            return result;
        }

        // ------------------------------------------------------------------ descriptions & context

        public static string Vec(double[] v) => v == null ? "?" : string.Join(", ", v.Take(3).Select(c => Math.Round(c, 4).ToString(CultureInfo.InvariantCulture)));

        public static string AxisName(double[] n)
        {
            if (n == null || n.Length < 3) return null;
            var labels = new[] { "X", "Y", "Z" };
            for (var i = 0; i < 3; i++)
                if (Math.Abs(Math.Abs(n[i]) - 1) < 1e-6) return (n[i] > 0 ? "+" : "-") + labels[i];
            return null;
        }

        public string DescribeFace(Face2 f)
        {
            var sb = new StringBuilder("face");
            try
            {
                var s = (Surface)f.GetSurface();
                if (s.IsPlane())
                {
                    var n = (double[])f.Normal;
                    sb.Append(", planar, normal ").Append(AxisName(n) ?? "(" + Vec(n) + ")");
                }
                else if (s.IsCylinder())
                {
                    var cp = (double[])s.CylinderParams; // origin xyz, axis xyz, radius
                    sb.Append($", cylindrical, diameter {Mm(cp[6] * 2)} mm, axis {AxisName(new[] { cp[3], cp[4], cp[5] }) ?? Vec(new[] { cp[3], cp[4], cp[5] })}");
                }
                else if (s.IsCone()) sb.Append(", conical");
                else if (s.IsSphere()) sb.Append(", spherical");
                else if (s.IsTorus()) sb.Append(", toroidal (e.g. fillet)");
                else sb.Append(", freeform");
                sb.Append($", area {(f.GetArea() * 1e6).ToString("0.##", CultureInfo.InvariantCulture)} mm²");
                var box = (double[])f.GetBox();
                if (box != null) sb.Append($", centre ({Mm((box[0] + box[3]) / 2)}, {Mm((box[1] + box[4]) / 2)}, {Mm((box[2] + box[5]) / 2)}) mm");
                var feat = f.GetFeature() as Feature;
                if (feat != null) sb.Append(", from ").Append(feat.Name);
            }
            catch { }
            return sb.ToString();
        }

        public string DescribeEdge(Edge e)
        {
            var sb = new StringBuilder("edge");
            try
            {
                var c = (Curve)e.GetCurve();
                var sv = e.GetStartVertex() as Vertex;
                var ev = e.GetEndVertex() as Vertex;
                if (c.IsLine() && sv != null && ev != null)
                {
                    var a = (double[])sv.GetPoint();
                    var b = (double[])ev.GetPoint();
                    var d = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                    var len = Math.Sqrt(d.Sum(x => x * x));
                    sb.Append($", straight, length {Mm(len)} mm");
                    var dir = len > 0 ? AxisName(d.Select(x => Math.Abs(x / len)).ToArray()) : null;
                    if (dir != null) sb.Append(", along ").Append(dir.TrimStart('+'));
                    sb.Append($", from ({Mm(a[0])}, {Mm(a[1])}, {Mm(a[2])}) to ({Mm(b[0])}, {Mm(b[1])}, {Mm(b[2])}) mm");
                }
                else if (c.IsCircle())
                {
                    var cp = (double[])c.CircleParams; // centre xyz, axis xyz, radius
                    sb.Append($", {(sv == null ? "full circle" : "arc")}, diameter {Mm(cp[6] * 2)} mm, centre ({Mm(cp[0])}, {Mm(cp[1])}, {Mm(cp[2])}) mm");
                }
                else sb.Append(", curved");
            }
            catch { }
            return sb.ToString();
        }

        public string DescribeSelected(ModelDoc2 doc, object o, int type)
        {
            try
            {
                string comp = null;
                if (o is Entity ent && doc.GetType() == DocTypes.Assembly)
                    comp = (ent.GetComponent() as Component2)?.Name2;
                string d;
                switch (o)
                {
                    case Face2 f: d = DescribeFace(f); break;
                    case Edge e: d = DescribeEdge(e); break;
                    case Vertex v: var p = (double[])v.GetPoint(); d = $"vertex at ({Mm(p[0])}, {Mm(p[1])}, {Mm(p[2])}) mm"; break;
                    case Component2 c: d = $"component {c.Name2} ({Path.GetFileName(c.GetPathName())})"; break;
                    case Feature f2: d = $"{(f2.GetTypeName2() == "RefPlane" ? "plane" : "feature")} {f2.Name} ({f2.GetTypeName2()})"; break;
                    case View vw: d = $"drawing view {vw.GetName2()}"; break;
                    case SketchSegment ss: d = $"sketch segment ({((swSketchSegments_e)ss.GetType()).ToString().Replace("swSketch", "").ToLowerInvariant()})"; break;
                    case SketchPoint sp: d = $"sketch point ({Mm(sp.X)}, {Mm(sp.Y)}) mm"; break;
                    default: d = ((swSelectType_e)type).ToString().Replace("swSel", "").ToLowerInvariant(); break;
                }
                return comp != null ? d + " on component " + comp : d;
            }
            catch { return "selected item"; }
        }

        public sealed class SelectionInfo
        {
            public string Id;
            public string Description;
        }

        /// <summary>Snapshot the user's selection into "S#" references.</summary>
        public List<SelectionInfo> CaptureSelection(ModelDoc2 doc, int max = 25)
        {
            ClearRefs("S");
            var list = new List<SelectionInfo>();
            if (doc == null) return list;
            var sm = (SelectionMgr)doc.SelectionManager;
            var n = sm.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= Math.Min(n, max); i++)
            {
                var o = sm.GetSelectedObject6(i, -1);
                var type = sm.GetSelectedObjectType3(i, -1);
                var desc = DescribeSelected(doc, o, type);
                var id = Register(doc, o, "selection", "S", desc);
                list.Add(new SelectionInfo { Id = id, Description = desc });
            }
            return list;
        }

        public List<string> RebuildErrors(ModelDoc2 doc)
        {
            var result = new List<string>();
            try
            {
                object feats, codes, warns;
                if (doc.Extension.GetWhatsWrong(out feats, out codes, out warns) && feats is object[] fs)
                {
                    var cs = codes as int[];
                    var ws = warns as bool[];
                    for (var i = 0; i < fs.Length; i++)
                    {
                        var f = fs[i] as Feature;
                        var kind = ws != null && i < ws.Length && ws[i] ? "warning" : "error";
                        result.Add($"{f?.Name ?? "?"}: {kind} {(cs != null && i < cs.Length ? ((swFeatureError_e)cs[i]).ToString().Replace("swFeatureError", "") : "")}");
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>The auto-context block prepended to each user turn.</summary>
        public string BuildContext(bool useSelection, out int selectionCount)
        {
            selectionCount = 0;
            var doc = ActiveDoc;
            var sb = new StringBuilder("<solidworks_context>\n");
            if (doc == null)
            {
                sb.Append("No document is open.\n");
                var open = OpenDocuments().Select(d => d.GetTitle()).ToList();
                if (open.Count > 0) sb.Append("Open documents: ").Append(string.Join(", ", open)).Append('\n');
                sb.Append("</solidworks_context>");
                return sb.ToString();
            }
            var type = doc.GetType();
            sb.Append($"Active document: {DocTypes.Name(type)} \"{doc.GetTitle()}\"");
            sb.Append(string.IsNullOrEmpty(doc.GetPathName()) ? " (never saved)" : $" at {doc.GetPathName()}");
            if (doc.GetSaveFlag()) sb.Append(", has unsaved changes");
            sb.Append('\n');
            try
            {
                var cfg = doc.ConfigurationManager?.ActiveConfiguration?.Name;
                if (!string.IsNullOrEmpty(cfg) && type != DocTypes.Drawing) sb.Append("Active configuration: ").Append(cfg).Append('\n');
            }
            catch { }
            if (((SketchManager)doc.SketchManager).ActiveSketch != null)
                sb.Append("A sketch is currently open for editing").Append(LastSketch != null ? "" : "").Append(".\n");

            if (type != DocTypes.Drawing)
            {
                var feats = Features(doc).Where(f => !IsHousekeeping(f)).ToList();
                var shown = feats.Skip(Math.Max(0, feats.Count - 40))
                    .Select(f => f.Name + " (" + f.GetTypeName2() + (f.IsSuppressed() ? ", suppressed" : "") + ")");
                sb.Append($"Feature tree ({feats.Count}{(feats.Count > 40 ? ", last 40 shown" : "")}): ").Append(string.Join(", ", shown)).Append('\n');
                if (type == DocTypes.Assembly)
                {
                    var comps = (((AssemblyDoc)doc).GetComponents(true) as object[]) ?? new object[0];
                    sb.Append($"Top-level components ({comps.Length}): ")
                        .Append(string.Join(", ", comps.Take(30).Cast<Component2>().Select(c => c.Name2))).Append('\n');
                }
            }
            else
            {
                try
                {
                    var sheet = (Sheet)((DrawingDoc)doc).GetCurrentSheet();
                    sb.Append("Current sheet: ").Append(sheet.GetName()).Append('\n');
                }
                catch { }
            }
            var errors = RebuildErrors(doc);
            sb.Append("Rebuild errors: ").Append(errors.Count == 0 ? "none" : string.Join("; ", errors.Take(10))).Append('\n');

            var sel = CaptureSelection(doc);
            selectionCount = sel.Count;
            if (sel.Count == 0) sb.Append("Selection: nothing selected.\n");
            else
            {
                sb.Append($"Selection ({sel.Count}):\n");
                foreach (var s in sel) sb.Append($"  {s.Id} = {s.Description}\n");
                if (useSelection) sb.Append("The user explicitly refers to this selection in their message (\"this\", \"these\", \"the selected ...\").\n");
            }
            var others = OpenDocuments().Where(d => d.GetTitle() != doc.GetTitle()).Select(d => d.GetTitle()).ToList();
            if (others.Count > 0) sb.Append("Other open documents: ").Append(string.Join(", ", others.Take(10))).Append('\n');
            sb.Append("</solidworks_context>");
            return sb.ToString();
        }

        /// <summary>Short status for the composer chip.</summary>
        public object QuickStatus()
        {
            var doc = ActiveDoc;
            if (doc == null) return new { doc = (string)null, docType = (string)null, selection = 0, selectionLabel = (string)null };
            var sm = (SelectionMgr)doc.SelectionManager;
            var n = sm.GetSelectedObjectCount2(-1);
            string label = null;
            if (n == 1)
            {
                var t = (swSelectType_e)sm.GetSelectedObjectType3(1, -1);
                label = t.ToString().Replace("swSel", "").ToLowerInvariant().TrimEnd('s');
                if (label == "verticee" || label == "vertice") label = "vertex";
                if (label == "datumplane") label = "plane";
                if (label == "bodyfeature") label = "feature";
            }
            else if (n > 1) label = n + " items";
            return new { doc = doc.GetTitle(), docType = DocTypes.Name(doc.GetType()), selection = n, selectionLabel = label };
        }

        // ------------------------------------------------------------------ UI state

        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);

        /// <summary>True while SOLIDWORKS shows a modal dialog (its main window is disabled).</summary>
        public bool IsModalDialogOpen()
        {
            try
            {
                var frame = Sw.Frame() as Frame;
                if (frame == null) return false;
                var h = new IntPtr(frame.GetHWndx64());
                return h != IntPtr.Zero && !IsWindowEnabled(h);
            }
            catch { return false; }
        }

        /// <summary>Theme of the SOLIDWORKS UI: ("dark"|"light", background hex) or null if unknown.</summary>
        private bool _themeLogged;

        public (string mode, string background)? DetectTheme()
        {
            (string, string) FromColorRef(int c)
            {
                int r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF; // COLORREF (0x00BBGGRR)
                var lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                return (lum < 128 ? "dark" : "light", $"#{r:x2}{g:x2}{b:x2}");
            }
            try
            {
                object colors;
                Sw.GetInterfaceBrightnessThemeColors(out colors);
                if (!_themeLogged)
                {
                    _themeLogged = true;
                    var vals = colors is Array arr0 ? string.Join(",", arr0.Cast<object>().Select(v => Convert.ToInt64(v).ToString("X"))) : "null";
                    var tree = Sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSystemColorsTreeViewBackground);                    Log.Info($"Theme colors: {colors?.GetType().Name} [{vals}] treeBg={tree:X}");
                }
                var idx = (int)swInterfaceBrightnessColor_e.swIBColor_FeatureMgrBkgnd;
                if (colors is int[] arr && arr.Length > idx) return FromColorRef(arr[idx]);
                if (colors is object[] oarr && oarr.Length > idx) return FromColorRef(Convert.ToInt32(oarr[idx]));
                if (colors is Array any && any.Length > idx) return FromColorRef(Convert.ToInt32(any.GetValue(idx)));
            }
            catch (Exception e) { if (!_themeLogged) { _themeLogged = true; Log.Warn("Theme API failed: " + e.Message); } }
            try
            {
                // Fallback: the FeatureManager tree background colour preference.
                var c = Sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSystemColorsTreeViewBackground);
                return FromColorRef(c);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ capture

        public static readonly Dictionary<string, (string name, int id)> NamedViews = new Dictionary<string, (string, int)>
        {
            ["front"] = ("*Front", 1), ["back"] = ("*Back", 2), ["left"] = ("*Left", 3), ["right"] = ("*Right", 4),
            ["top"] = ("*Top", 5), ["bottom"] = ("*Bottom", 6), ["isometric"] = ("*Isometric", 7),
            ["trimetric"] = ("*Trimetric", 8), ["dimetric"] = ("*Dimetric", 9),
        };

        /// <summary>Screenshot the active document as PNG (base64), at most maxSide pixels on the long side.</summary>
        public string CapturePngBase64(ModelDoc2 doc, int width, int height, int maxSide = 1400)
        {
            Directory.CreateDirectory(AppPaths.Temp);
            var bmp = Path.Combine(AppPaths.Temp, "capture_" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                if (!doc.SaveBMP(bmp, width, height) || !File.Exists(bmp))
                    throw new ToolException("SOLIDWORKS could not capture the view (is the document window visible?).");
                using (var src = Image.FromFile(bmp))
                {
                    var scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
                    var w = Math.Max(1, (int)(src.Width * scale));
                    var h = Math.Max(1, (int)(src.Height * scale));
                    using (var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    using (var g = Graphics.FromImage(dst))
                    using (var ms = new MemoryStream())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, w, h);
                        dst.Save(ms, ImageFormat.Png);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            finally
            {
                try { if (File.Exists(bmp)) File.Delete(bmp); } catch { }
            }
        }
    }
}
