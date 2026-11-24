# ClaudeWorks

A SOLIDWORKS add-in that puts a Claude chat assistant in the Task Pane. Claude sees the active
document, the feature tree and your selection, and builds or edits parts, assemblies and drawings
through ~65 SOLIDWORKS API tools. Tool calls show up as a compact action rail, screenshots appear
inline, and each reply is grouped into one undo step.

> **Unofficial project.** Not affiliated with or endorsed by Dassault Systemes or Anthropic. SOLIDWORKS and Claude are trademarks of their owners. See [NOTICE.md](NOTICE.md).

Two ways to use it:

- **In the Task Pane** with your own Anthropic API key (prepaid Console credits).
- **From Claude Code or Claude Desktop** on your Claude Pro/Max plan, no API key: the add-in serves the
  same tools over MCP through `ClaudeSW.Mcp.exe`. The installer connects both apps (checkboxes); the pane
  shows what they do ("Claude Code: Extruded Sketch1 20 mm"), and each change is one SOLIDWORKS undo step.

## Install (no technical knowledge needed)

1. Download `ClaudeWorks-Setup.exe` from the [latest release](https://github.com/johanan-23/claudeworks/releases/latest) and run it. Windows SmartScreen may warn because the installer is unsigned: click "More info" > "Run anyway".
2. Leave **Connect Claude Code** and **Connect Claude Desktop** ticked. The installer adds ClaudeWorks to those apps by itself; there is nothing to configure in Claude.
3. Start SOLIDWORKS. Open Claude Code or Claude Desktop (restart it if it was already open) and ask for something, e.g. "make a 100x60x20 plate with a 10 mm hole in SOLIDWORKS".
4. Optional: the chat pane inside SOLIDWORKS needs an Anthropic API key. Without one, use Claude Code / Claude Desktop on your normal Claude plan.

## Build

```powershell
.\build.ps1                 # web UI -> add-in (Release) -> 47 unit tests -> installer
.\build.ps1 -SkipInstaller  # build + test only
```

Output: `installer\output\ClaudeWorks-Setup.exe`. Requirements are in [docs/INSTALL.md](docs/INSTALL.md).

## Layout

| Path | What |
|---|---|
| `src/ClaudeSW.Core` | net48 library: chat model, agent loop, Anthropic SDK client (+ isolated AppDomain host), auth, storage |
| `src/ClaudeSW.AddIn` | `ISwAddin`, Task Pane + WebView2 host, UI-thread dispatcher, SOLIDWORKS tools, dev pipe |
| `src/ClaudeSW.Mcp` | MCP stdio server (JSON-RPC) bridging Claude Code / Claude Desktop to the add-in's tool pipe |
| `src/ClaudeSW.Web` | React + TypeScript + Tailwind UI, built to one HTML file embedded in the DLL |
| `tests/ClaudeSW.Core.Tests` | xUnit tests (agent loop, tool arguments/schema, auth with fakes, SDK client with a fake HTTP stream, storage) |
| `installer` | Inno Setup script; WebView2 bootstrapper is downloaded into `redist` by the build |
| `scripts` | dev registration/reload, dev-pipe client, screenshots, tool smoke test, offline acceptance demo |
| `docs` | [ARCHITECTURE](docs/ARCHITECTURE.md), [INSTALL](docs/INSTALL.md), [MANUAL_TEST](docs/MANUAL_TEST.md) |

## Authentication

Only Anthropic API keys (Console, prepaid credits). Anthropic's Agent SDK docs state that, unless
previously approved, third-party developers may not offer claude.ai login or subscription rate
limits, so there is no "Sign in with Claude". The `IAuthProvider` interface is ready for another
provider if that changes. Keys are stored with Windows DPAPI (current user) and only sent to
api.anthropic.com.

## Not included (not available through the API)

Voice, memory, Projects and Artifacts are claude.ai product features, so the pane doesn't imitate them.

## Skills and Claude Code plugin

`skills/` holds Agent Skills (overview, sketching, part modeling, assemblies, drawings, troubleshooting, export/standards) that teach an agent to use the tools well. Install in Claude Code with
`/plugin marketplace add johanan-23/claudeworks` then `/plugin install claudeworks@claudeworks`, or zip the folder for claude.ai upload. `scripts/validate-skills.ps1` fails if a skill names a tool that does not exist. The skills have not yet been benchmarked against real SOLIDWORKS sessions.

## Naming note

The product is **ClaudeWorks**. Internal identifiers (`ClaudeSW.*` namespaces, the `ClaudeForSolidWorks` pipe, ProgId and AppData folder) keep their original names so upgrades do not break.

## Docs

[PRIVACY](PRIVACY.md) · [SECURITY](SECURITY.md) · [CONTRIBUTING](CONTRIBUTING.md) · [CHANGELOG](CHANGELOG.md) · installer is unsigned, so Windows SmartScreen will warn; verify the published SHA-256.

## Licence

MIT, see [LICENSE](LICENSE). SOLIDWORKS interop DLLs are not included; building the add-in needs a licensed SOLIDWORKS.
