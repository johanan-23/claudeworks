# Contributing

Thanks for helping! 

## Build
Requirements: Windows, .NET SDK 8/9, Node 20+, Inno Setup 6 (installer only), and a licensed **SOLIDWORKS** (2022-2026) for the add-in. The proprietary interop DLLs are never committed: the build reads them from your SOLIDWORKS install (`-SwInstallDir`) or from an untracked `lib\` folder.

```powershell
.\build.ps1 -SkipInstaller   # web UI, add-in, unit tests
.\build.ps1                  # + installer
```

Without SOLIDWORKS you can still build and test `ClaudeSW.Core`, the web UI (`npm run build` in `src/ClaudeSW.Web`) and validate skills (`scripts\validate-skills.ps1`).

## Pull requests
- Keep changes focused; add unit tests for Core logic; run `scripts\validate-skills.ps1` if you touch skills or tools.
- Anything touching real SOLIDWORKS behaviour is verified by hand; follow `docs/MANUAL_TEST.md` and say what you ran.
- Tool names mentioned in `skills/` must exist in the server.
- Use semantic versioning; add a line to `CHANGELOG.md`.
