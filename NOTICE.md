# Notices

ClaudeWorks is an independent, unofficial project. It is **not affiliated with, endorsed by or sponsored by Dassault Systemes or Anthropic**. SOLIDWORKS is a registered trademark of Dassault Systemes SolidWorks Corp.; Claude is a trademark of Anthropic, PBC.

## Proprietary components are not redistributed
The SOLIDWORKS interop assemblies (`SolidWorks.Interop.*.dll`) are **not** in this repository or the installer's source. The add-in references them from your local SOLIDWORKS installation (`SwInstallDir`) at build time, with interop types embedded. You need a licensed SOLIDWORKS to build the add-in.

## Third-party components (see each project for its licence)
- Anthropic .NET SDK (`Anthropic`), MIT
- Microsoft.Web.WebView2, Microsoft software licence (runtime installed by the installer from Microsoft)
- React, Vite, Tailwind CSS and other npm packages in `src/ClaudeSW.Web/package.json`, mostly MIT
- Inno Setup (build tool only), Inno Setup licence
- xUnit (tests), Apache-2.0
