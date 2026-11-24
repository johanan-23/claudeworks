# Install

## Users

1. Close SOLIDWORKS and run `ClaudeWorks-Setup.exe` (asks for admin rights once).
   It checks .NET Framework 4.8, installs the WebView2 runtime if missing, registers the add-in for
   all installed SOLIDWORKS versions and turns on "load at startup" for you.
2. Start SOLIDWORKS. The Claude tab opens in the Task Pane on first run.
3. Paste an API key from console.anthropic.com (the account needs API credits).

Silent install: `ClaudeWorks-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES [/CLOSESOLIDWORKS]`.
Known gap: `/S` is accepted but currently falls back to the normal wizard (setup can't re-launch itself
silently on this Windows build); use `/VERYSILENT` for unattended installs.
Add `/TASKS=""` to skip connecting Claude Code / Claude Desktop.
Upgrades install over the previous version. A diagnostics summary is written to
`C:\Program Files\ClaudeWorks\install-diagnostics.txt`, the full log to `%TEMP%\Setup Log *.txt`,
and the add-in log to `%APPDATA%\ClaudeForSolidWorks\logs`.

Uninstall from Windows Settings > Apps. It removes the files and registry keys and (after asking,
or always when silent unless `/KEEPDATA`) your chats, settings and saved key.

## Using it from Claude Code / Claude Desktop (no API key)

The installer's "Connect Claude Code" / "Connect Claude Desktop" options (checked by default) register
`C:\Program Files\ClaudeWorks\ClaudeSW.Mcp.exe` as the MCP server `solidworks-addin`
(Claude Code: user scope; Claude Desktop: `claude_desktop_config.json`, including the Microsoft Store copy).
You can also do it from the pane (sign-in screen or Settings) or by hand:

```powershell
claude mcp add --scope user solidworks-addin -- "C:\Program Files\ClaudeWorks\ClaudeSW.Mcp.exe"
```

Start SOLIDWORKS (or let Claude call `launch_solidworks`), then in a new Claude Code session ask e.g.
"make a 100x60x20 plate with a 10 mm centre hole in SOLIDWORKS". The bridge talks to the add-in over the
per-user pipe `\\.\pipe\ClaudeForSolidWorks.Tools`; turn it off in the pane's Settings
("Allow Claude Code and Claude Desktop"). `ClaudeSW.Mcp.exe --check` tests the connection.

## Building

- .NET SDK 8 or 9 (builds the net48 projects through reference packages)
- Node.js 18+
- Inno Setup 6 (`winget install JRSoftware.InnoSetup`)
- SOLIDWORKS installed (for `SolidWorks.Interop.*.dll`), or copy the three interop DLLs into `lib\`
  and pass `-SwInstallDir`.

Then `.\build.ps1`.

## Developer loop (no admin rights)

`scripts\dev-reload.ps1` closes SOLIDWORKS, rebuilds, registers the build per-user and loads it with
`ISldWorks.LoadAddIn`, with the dev pipe enabled (`CLAUDESW_DEV=1`). SOLIDWORKS only auto-loads add-ins
registered under HKLM, so per-user registration needs that explicit load. `-Demo scripts\demo\acceptance.json`
replays a scripted, offline conversation against the real tools. `scripts\register-dev.ps1 -Unregister`
removes the per-user registration (the installer also removes it).
