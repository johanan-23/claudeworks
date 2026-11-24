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
    internal static class DocumentTools
    {
        public static IEnumerable<ToolSpec> Create(SwSession s)
        {
            yield return Tool("sw_status", "Report the SOLIDWORKS version, the active document and all open documents.",
                S(), (a, c) =>
                {
                    var active = s.ActiveDoc;
                    return ToolOutput.Obj(new
                    {
                        revision = s.Sw.RevisionNumber(),
                        active_document = active == null ? null : SwSession.Summary(active),
                        open_documents = s.OpenDocuments().Select(SwSession.Summary).ToList(),
                    }, "Checked SOLIDWORKS status");
                }, describe: a => "Check SOLIDWORKS status");

            yield return Tool("new_document", "Create a new part, assembly or drawing (uses the default template unless `template` is a .prtdot/.asmdot/.drwdot path). The new document becomes active.",
                S().Str("doc_type", "Document type.", false, new[] { "part", "assembly", "drawing" })
                   .Str("template", "Optional template path.")
                   .Str("paper", "Drawing sheet size.", false, PaperSizes.Keys.Select(k => k.ToUpperInvariant())),
                (a, c) =>
                {
                    var kind = a.Str("doc_type", "part").ToLowerInvariant();
                    var tpl = a.Str("template") ?? s.DefaultTemplate(kind);
                    var paper = kind == "drawing" ? PaperSizes[a.Str("paper", "A3")] : 0;
                    var doc = Check(s.Sw.NewDocument(tpl, paper, 0, 0) as ModelDoc2, "the document");
                    s.LastSketch = null;
                    return ToolOutput.Obj(SwSession.Summary(doc), $"Created new {kind} {doc.GetTitle()}");
                }, modifies: true, describe: a => $"New {a.Str("doc_type", "part")}");

            yield return Tool("open_document", "Open a .sldprt/.sldasm/.slddrw file and make it active.",
                S().Str("path", "Full file path.", true).Bool("read_only", "Open read-only.").Str("configuration", "Configuration to open."),
                (a, c) =>
                {
                    var path = Path.GetFullPath(a.RequireStr("path"));
                    if (!File.Exists(path)) throw new ToolException($"File not found: {path}");
                    int errs = 0, warns = 0;
                    var opts = (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (a.Bool("read_only") ? (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0);
                    var doc = s.Sw.OpenDoc6(path, DocTypes.FromPath(path), opts, a.Str("configuration", ""), ref errs, ref warns);
                    if (doc == null) throw new ToolException($"Could not open '{path}' (error code {errs}).");
                    s.Activate(doc.GetTitle());
                    return ToolOutput.Obj(SwSession.Summary(doc), $"Opened {doc.GetTitle()}");
                }, describe: a => "Open " + Path.GetFileName(a.Str("path", "")));

            yield return Tool("save_document", "Save the active document. With `path`, Save As to that location (required for never-saved documents). Only call this when the user asked to save.",
                S().Str("path", "Optional Save As path (.sldprt/.sldasm/.slddrw)."),
                (a, c) =>
                {
                    var doc = s.RequireDoc();
                    int errs = 0, warns = 0;
                    bool ok;
                    var path = a.Str("path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        path = Path.GetFullPath(path);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errs, ref warns);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(doc.GetPathName())) throw new ToolException("This document has never been saved. Pass `path` (ask the user where to save it).");
                        ok = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errs, ref warns);
                    }
                    if (!ok) throw new ToolException($"Save failed (error code {errs}). The file may be read-only or open elsewhere.");
                    return ToolOutput.Obj(SwSession.Summary(doc), "Saved " + doc.GetTitle());
                },
                destructive: true,
                confirm: a =>
                {
                    var path = a.Str("path");
                    var doc = s.ActiveDoc;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(Path.GetFullPath(path)))
                        return $"Overwrite the existing file {Path.GetFileName(path)}?";
                    if (string.IsNullOrWhiteSpace(path) && doc != null && !string.IsNullOrEmpty(doc.GetPathName()))
                        return $"Save changes to {Path.GetFileName(doc.GetPathName())} (overwrites the file on disk)?";
                    return null; // saving a new file to a new path needs no extra confirmation
                },
                describe: a => "Save " + (a.Str("path") != null ? "as " + Path.GetFileName(a.Str("path")) : "document"));

            yield return Tool("close_document", "Close a document by title (default: the active one). Unsaved changes are discarded.",
                S().Str("title", "Document title as shown in SOLIDWORKS."),
                (a, c) =>
                {
                    var title = a.Str("title") ?? s.RequireDoc().GetTitle();
                    s.Sw.CloseDoc(title);
                    return ToolOutput.Text($"Closed '{title}'.", "Closed " + title);
                },
                destructive: true,
                confirm: a =>
                {
                    var title = a.Str("title") ?? s.ActiveDoc?.GetTitle();
                    var doc = s.OpenDocuments().FirstOrDefault(d => d.GetTitle() == title);
                    return doc != null && doc.GetSaveFlag() ? $"Close \"{title}\" and discard unsaved changes?" : null;
                },
                describe: a => "Close " + (a.Str("title") ?? "document"));

            yield return Tool("activate_document", "Bring an already-open document to the front by title.",
                S().Str("title", "Document title.", true),
                (a, c) =>
                {
                    var doc = s.Activate(a.RequireStr("title")) ?? throw new ToolException($"No open document titled '{a.Str("title")}'. Use sw_status to list them.");
                    return ToolOutput.Obj(SwSession.Summary(doc), "Activated " + doc.GetTitle());
                }, describe: a => "Activate " + a.Str("title"));

            yield return Tool("rebuild", "Force a full rebuild of the active document and report rebuild errors.",
                S(), (a, c) =>
                {
                    var doc = s.RequireDoc();
                    doc.ForceRebuild3(false);
                    var errs = s.RebuildErrors(doc);
                    return ToolOutput.Obj(new { rebuilt = true, rebuild_errors = errs }, errs.Count == 0 ? "Rebuilt, no errors" : $"Rebuilt with {errs.Count} error(s)");
                }, modifies: true, describe: a => "Rebuild");

            yield return Tool("get_rebuild_errors", "List features with rebuild errors or warnings in the active document.",
                S(), (a, c) =>
                {
                    var errs = s.RebuildErrors(s.RequireDoc());
                    return ToolOutput.Obj(new { errors = errs }, errs.Count == 0 ? "No rebuild errors" : $"{errs.Count} rebuild issue(s)");
                }, describe: a => "Check rebuild errors");

            yield return Tool("export_file", "Export the active document; the format comes from the extension: .step .stp .iges .igs .stl .x_t .3mf .pdf .dxf .dwg .png .jpg .sat .obj. Only when the user asked.",
                S().Str("path", "Output file path.", true),
                (a, c) =>
                {
                    var doc = s.RequireDoc();
                    var path = Path.GetFullPath(a.RequireStr("path"));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    int errs = 0, warns = 0;
                    var ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref errs, ref warns);
                    if (!ok || !File.Exists(path)) throw new ToolException($"Export failed (error code {errs}). Check the extension is supported for a {DocTypes.Name(doc.GetType())}.");
                    return ToolOutput.Text($"Exported to {path} ({new FileInfo(path).Length} bytes).", "Exported " + Path.GetFileName(path));
                },
                destructive: true,
                confirm: a => File.Exists(Path.GetFullPath(a.Str("path") ?? "x:\\nonexistent")) ? $"Overwrite {Path.GetFileName(a.Str("path"))}?" : null,
                describe: a => "Export " + Path.GetFileName(a.Str("path", "")));

            yield return Tool("capture_view", "Screenshot the active model or drawing sheet and return it as an image. Use it to check your work after geometry changes.",
                S().Str("view", "Camera orientation for parts/assemblies.", false, new[] { "current", "front", "back", "left", "right", "top", "bottom", "isometric", "trimetric", "dimetric" })
                   .Int("width", "Image width in px (default 1024).", false, 200, 2000)
                   .Int("height", "Image height in px (default 768).", false, 200, 2000)
                   .Bool("zoom_to_fit", "Zoom to fit first (default true)."),
                (a, c) =>
                {
                    var doc = s.RequireDoc();
                    var view = a.Str("view", "isometric").ToLowerInvariant();
                    if (doc.GetType() != DocTypes.Drawing && view != "current")
                    {
                        if (!SwSession.NamedViews.TryGetValue(view, out var nv)) throw new ToolException("Unknown view " + view);
                        doc.ShowNamedView2(nv.name, nv.id);
                    }
                    if (a.Bool("zoom_to_fit", true)) doc.ViewZoomtofit2();
                    var png = s.CapturePngBase64(doc, a.Int("width", 1024), a.Int("height", 768));
                    var label = doc.GetType() == DocTypes.Drawing ? "sheet" : view + " view";
                    return ToolOutput.Text($"Captured the {label} of {doc.GetTitle()}.", $"Captured {label}").WithImage("image/png", png);
                }, describe: a => "Capture " + a.Str("view", "isometric") + " view");
        }
    }
}
