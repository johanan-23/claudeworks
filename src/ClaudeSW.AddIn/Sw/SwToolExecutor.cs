using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClaudeSW.AddIn.Host;
using ClaudeSW.AddIn.Tools;
using ClaudeSW.Core.Agent;
using ClaudeSW.Core.Storage;
using SolidWorks.Interop.sldworks;

namespace ClaudeSW.AddIn.Sw
{
    /// <summary>
    /// Runs tools on the SOLIDWORKS UI thread (via the Task Pane control), refuses to run while a
    /// modal dialog is open, and groups each assistant turn's model changes into one undo step.
    /// </summary>
    public sealed class SwToolExecutor : IToolExecutor
    {
        private readonly SwSession _s;
        private readonly TaskPaneHost _ui;
        private readonly Func<bool> _allowAdvanced;
        private readonly List<ToolSpec> _all;
        private readonly UndoGrouper _undo;

        public SwToolExecutor(SwSession session, TaskPaneHost ui, Func<bool> allowAdvanced)
        {
            _s = session;
            _ui = ui;
            _allowAdvanced = allowAdvanced;
            _undo = new UndoGrouper();
            _all = DocumentTools.Create(session)
                .Concat(GeometryTools.Create(session))
                .Concat(SketchTools.Create(session))
                .Concat(FeatureTools.Create(session))
                .Concat(ModelTools.Create(session))
                .Concat(AssemblyTools.Create(session))
                .Concat(DrawingTools.Create(session))
                .Concat(AdvancedTools.Create(session))
                .ToList();
        }

        public UndoGrouper Undo => _undo;
        public IReadOnlyList<ToolSpec> AllTools => _all;

        public IReadOnlyList<ToolSpec> GetTools() => _all.Where(t => !t.Advanced || _allowAdvanced()).ToList();

        public ToolSpec Find(string name) => _all.FirstOrDefault(t => t.Name == name);

        public static TimeSpan Timeout = TimeSpan.FromSeconds(120);

        public async Task<ToolOutput> ExecuteAsync(ToolSpec spec, Args args, ToolContext context)
        {
            try
            {
                return await _ui.RunOnUiThread(() => RunOnUi(spec, args, context), Timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return ToolOutput.Error("SOLIDWORKS is busy (a long rebuild, a dialog or a command in progress) and did not respond. Ask the user to finish what SOLIDWORKS is doing, then try again.");
            }
        }

        private ToolOutput RunOnUi(ToolSpec spec, Args args, ToolContext context)
        {
            if (_s.IsModalDialogOpen())
                return ToolOutput.Error("A SOLIDWORKS dialog is open, which blocks the API. Ask the user to close it (for example a rebuild-error or save prompt), then retry.");
            try
            {
                if (spec.Modifies) _undo.Touch(_s.ActiveDoc);
                var output = spec.Run(args, context) ?? ToolOutput.Text("Done.");
                if (spec.Modifies) _undo.Touch(_s.ActiveDoc); // new documents start their own group
                return output;
            }
            catch (ToolException e)
            {
                return ToolOutput.Error(e.Message);
            }
            catch (COMException e)
            {
                Log.Warn($"{spec.Name} COM error 0x{e.ErrorCode:X8}: {e.Message}");
                return ToolOutput.Error($"SOLIDWORKS API error in {spec.Name} (0x{e.ErrorCode:X8}): {e.Message}. Check the inputs or try another approach.");
            }
            catch (InvalidCastException e)
            {
                Log.Warn($"{spec.Name} cast error: {e.Message}");
                return ToolOutput.Error($"{spec.Name}: a reference pointed at the wrong kind of entity ({e.Message}).");
            }
            catch (Exception e)
            {
                Log.Error($"Tool {spec.Name} failed", e);
                return ToolOutput.Error($"{spec.Name} failed: {e.Message}");
            }
        }

        /// <summary>Begin/end the per-turn undo group. Call on the UI thread.</summary>
        public Task BeginTurnAsync(string label) => _ui.RunOnUiThread(() => { _undo.Begin(label); return true; }, Timeout);
        public Task<bool> EndTurnAsync() => _ui.RunOnUiThread(() => _undo.End(), Timeout);
        public Task<string> UndoLastAsync() => _ui.RunOnUiThread(() => _undo.UndoLast(_s), Timeout);
    }

    /// <summary>
    /// Groups everything Claude changes during one turn into a single SOLIDWORKS undo object per
    /// document (ModelDocExtension.StartRecordingUndoObject / FinishRecordingUndoObject2).
    /// </summary>
    public sealed class UndoGrouper
    {
        private readonly Dictionary<string, ModelDoc2> _open = new Dictionary<string, ModelDoc2>();
        private string _label = "Claude";
        private readonly List<string> _lastDocs = new List<string>();
        public bool Enabled { get; set; } = true;
        public bool CanUndo => _lastDocs.Count > 0;

        public void Begin(string label)
        {
            _open.Clear();
            _label = "Claude: " + (string.IsNullOrWhiteSpace(label) ? "changes" : label.Length > 40 ? label.Substring(0, 40) + "…" : label);
        }

        public void Touch(ModelDoc2 doc)
        {
            if (!Enabled || doc == null) return;
            var key = doc.GetTitle();
            if (_open.ContainsKey(key)) return;
            try
            {
                doc.Extension.StartRecordingUndoObject();
                _open[key] = doc;
            }
            catch (Exception e) { Log.Warn("StartRecordingUndoObject failed: " + e.Message); }
        }

        /// <summary>Finish recording. Returns true if anything was grouped.</summary>
        public bool End()
        {
            if (_open.Count == 0) return false;
            _lastDocs.Clear();
            foreach (var kv in _open)
            {
                try
                {
                    if (kv.Value.Extension.FinishRecordingUndoObject2(_label, false)) _lastDocs.Add(kv.Key);
                }
                catch (Exception e) { Log.Warn("FinishRecordingUndoObject2 failed: " + e.Message); }
            }
            _open.Clear();
            return _lastDocs.Count > 0;
        }

        public string UndoLast(SwSession s)
        {
            if (_lastDocs.Count == 0) return "Nothing to undo from Claude's last turn.";
            var done = new List<string>();
            foreach (var title in _lastDocs)
            {
                var doc = s.OpenDocuments().FirstOrDefault(d => d.GetTitle() == title);
                if (doc == null) continue;
                if (s.ActiveDoc?.GetTitle() != title) s.Activate(title);
                doc.EditUndo2(1);
                done.Add(title);
            }
            _lastDocs.Clear();
            return done.Count == 0 ? "The documents from the last turn are closed." : "Undid Claude's last changes in " + string.Join(", ", done) + ".";
        }
    }
}
