# Manual test script (SOLIDWORKS 2026)

## Automated parts (no API key)

| Script | Covers |
|---|---|
| `dotnet test tests\ClaudeSW.Core.Tests` | 47 unit tests |
| `scripts\smoke-tools.ps1` | Real tools in a running SOLIDWORKS via the dev pipe: plate (volume checked against the analytic value), save, 3-view drawing |
| `scripts\mcp-smoke.ps1` | MCP over stdio exactly like Claude Code: initialize, tools/list, the plate via tools/call, image result, undo |
| `scripts\acceptance-demo.ps1` | Whole add-in with the scripted offline model: plate, "what is selected?", drawing; screenshots of SOLIDWORKS and the pane |

## Install / upgrade / uninstall results (2026-11-24, SOLIDWORKS 2026 SP3.2, Windows 11)

| Check | Result |
|---|---|
| `/VERYSILENT` fresh install | exit 0; add-in, CLSID, startup key, Claude Code + both Claude Desktop configs registered |
| SOLIDWORKS normal start | add-in loads by itself from Program Files; MCP bridge connects (70 tools) |
| Upgrade in place with SOLIDWORKS open (`/VERYSILENT /CLOSESOLIDWORKS`) | SOLIDWORKS closed politely, exit 0, one Apps entry, loads after restart |
| Silent uninstall | no Program Files folder, no HKLM/HKCU keys, no CLSID/ProgId, no user data, no Apps entry, MCP registrations removed |
| Silent uninstall with SOLIDWORKS running | failed before the fix (`WizardSilent` called during uninstall); fixed, not re-run |
| `/S` | falls back to the normal wizard (re-launch refused); open |

## Live acceptance (needs an API key with credits)

1. Run the installer on a clean machine/user. Check `install-diagnostics.txt` lists the SOLIDWORKS versions.
2. Start SOLIDWORKS: the Claude tab opens on the sign-in screen.
3. Paste the key. Wrong key → "That key was rejected". Good key → empty state with suggestions.
4. Send: "make a 100x60x20 plate with a 10 mm centre hole and 3 mm corner fillets".
   Expect: action rail rows ticking, a capture shown inline, a summary; model has Boss-Extrude, Cut-Extrude, Fillet;
   get_model_info volume ≈ 118 274.7 mm³.
5. Click "Undo changes" → the turn's features disappear in one step; Ctrl+Y in SOLIDWORKS restores.
6. Select the top face, toggle "Use selection: face", ask "what is selected?" → it describes a planar face, normal +Z, 6000 mm².
7. "create a 3-view drawing" → asks where to save if the part is unsaved, then an A3 drawing with aligned views.
8. Ask Claude to delete a feature → confirmation card; "Don't allow" leaves the model unchanged.
9. Press Stop mid-reply → streaming stops, partial text kept. Pull the network cable → offline error with Retry.
10. Chats panel (Ctrl+K): search, rename, delete; Settings: model, thinking, theme follows SOLIDWORKS light/dark.
11. Resize the Task Pane to ~320 px: no horizontal scrolling.
12. MCP (no API key): open a new Claude Code session, `/mcp` shows `solidworks-addin` connected; ask it to build the
    plate. The pane footer shows each action ("Claude Code: Filleted 4 edge(s) R3 mm").
13. Uninstall → no files under Program Files, no `HKLM\SOFTWARE\SolidWorks\AddIns\{ABA6C017-…}`,
    no CLSID, no `%APPDATA%\ClaudeForSolidWorks` (if you chose to delete data).
