# Architecture

```
SOLIDWORKS process (sldworks.exe)
├─ Default AppDomain
│  ├─ SwAddin (ISwAddin, COM)      → PaneSession: Task Pane tab, timers, dev pipe
│  ├─ TaskPaneHost (WinForms)      → WebView2 ── postMessage JSON ── React UI (embedded HTML)
│  ├─ AppController                → conversations, settings, auth, turns, confirmations
│  ├─ AgentLoop (thread pool)      → tool calls ──► SwToolExecutor ──BeginInvoke──► SW UI thread
│  └─ SwSession + Tools            → typed SOLIDWORKS API (embedded interop types)
│  └─ ToolPipeServer  ◄── \\.\pipe\ClaudeForSolidWorks.Tools (current user) ◄── ClaudeSW.Mcp.exe ◄── MCP stdio ── Claude Code / Desktop
└─ "ClaudeForSolidWorks.Net" AppDomain (private)
   └─ RemoteModelHost → AnthropicModelClient → official Anthropic C# SDK (streaming)
```

## Decisions

- **C# / .NET Framework 4.8 add-in, embedded interop types.** One build loads in SOLIDWORKS 2022–2026;
  no dependency on a specific `SolidWorks.Interop.*.dll` version.
- **WebView2 UI** (React/TS/Tailwind, single-file Vite build, served from resources through
  `WebResourceRequested` on `https://claude-sw.local`). No network needed to render; navigation to
  other origins opens the system browser. The control follows the native Task Pane container's size
  by subclassing its parent window.
- **Official Anthropic C# SDK, in a private AppDomain.** SOLIDWORKS hosts other add-ins that load their
  own `System.Threading.Tasks.Extensions`/`System.Memory` and answer `AssemblyResolve` first (seen in
  practice: the Vector add-in), which broke the SDK ("Method not found … ValueTask"). The SDK runs in
  its own AppDomain whose probing is limited to our folder and which uses generated binding redirects
  (`ClaudeSW.AddIn.dll.config`). Requests/responses cross as JSON; stream events via a MarshalByRef sink.
- **TLS:** SOLIDWORKS pins `ServicePointManager` to SSL3/TLS 1.0; the client adds TLS 1.2/1.3.
- **Threading:** all SOLIDWORKS calls run on its UI thread via `Control.BeginInvoke`. A call that is not
  picked up within 120 s (busy rebuild, modal loop) is abandoned and reported as "SOLIDWORKS is busy".
  Tools refuse to run while SOLIDWORKS's main window is disabled by a modal dialog.
- **Agent loop:** manual tool-use loop; tool inputs validated against their JSON schema before running;
  destructive tools need a confirmation card; all `tool_result`s of a turn go back in one message;
  stop/cancel keeps streamed text; max 40 steps per turn.
- **Requests:** streaming, `cache_control` on the system prompt and automatic caching of the
  conversation prefix, adaptive thinking (summarized display when "Extended thinking" is on, effort
  high vs medium), `eager_input_streaming` on tools, server-side refusal fallback (`fallbacks: "default"`)
  on models that support it. The system prompt is frozen; live SOLIDWORKS state goes in a hidden
  `<solidworks_context>` block in each user turn.
- **Entity references:** the user's selection becomes `S1…`, `find_faces` returns `F1…`, `find_edges`
  `E1…`, sketch geometry `K1…`. They are stored as SOLIDWORKS persistent references, so they survive
  rebuilds. This replaces coordinate picking (still available as a fallback).
- **Undo:** each assistant turn wraps its model changes in `StartRecordingUndoObject` /
  `FinishRecordingUndoObject2` per document; "Undo changes" calls `EditUndo2(1)`.
- **Storage:** `%APPDATA%\ClaudeForSolidWorks` (chats as JSON, settings, DPAPI-encrypted key, logs);
  WebView2 profile in `%LOCALAPPDATA%\ClaudeForSolidWorks`.

## Lessons carried over from the Python prototype

mm/degrees at the tool boundary; `SelectByID2` nudging + nearest-geometry fallback; feature method
argument counts checked against the installed type library (the compiler enforces them now);
template fallbacks under `C:\ProgramData\SOLIDWORKS\…\templates`; re-activate the target document after
`AddComponent5`/drawing views; hide housekeeping folders; modal dialogs block the API.
New ones: suppress the "modify dimension" popup (`swInputDimValOnCreate`) when adding dimensions,
`SetAddinCallbackInfo2` can't marshal an embedded-interop add-in object (not needed without CommandManager).
