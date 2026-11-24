<#
.SYNOPSIS
  One-command build: web UI -> add-in (Release) -> unit tests -> installer.
.EXAMPLE
  .\build.ps1                 # everything, output: installer\output\ClaudeWorks-Setup.exe
  .\build.ps1 -SkipInstaller  # just build + test
  .\build.ps1 -Sign "signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a `$f"
.NOTES
  Needs: .NET SDK 8+ (builds net48 via reference packs), Node 18+, Inno Setup 6, and the
  SOLIDWORKS interop DLLs (from an installed SOLIDWORKS, or copied into .\lib).
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$Sign,
    [string]$SwInstallDir = "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Tool($name, $fallback) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($f in $fallback) { if (Test-Path $f) { return $f } }
    throw "$name not found. See docs/INSTALL.md (Building)."
}
$dotnet = Tool "dotnet" @("$env:ProgramFiles\dotnet\dotnet.exe")
$npm = Tool "npm.cmd" @("$env:ProgramFiles\nodejs\npm.cmd")
$iscc = if ($SkipInstaller) { $null } else { Tool "ISCC.exe" @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") }

Step "Web UI"
Push-Location "$root\src\ClaudeSW.Web"
if (-not (Test-Path node_modules)) { & $npm ci --no-audit --no-fund; if ($LASTEXITCODE) { throw "npm ci failed" } }
& $npm run build; if ($LASTEXITCODE) { throw "web build failed" }
Pop-Location

Step "Add-in ($Configuration)"
& $dotnet build "$root\src\ClaudeSW.AddIn\ClaudeSW.AddIn.csproj" -c $Configuration -nologo -v q "/p:SwInstallDir=$SwInstallDir"
if ($LASTEXITCODE) { throw "add-in build failed" }
$out = "$root\src\ClaudeSW.AddIn\bin\$Configuration\net48"
foreach ($f in "ClaudeSW.AddIn.dll", "ClaudeSW.AddIn.dll.config", "ClaudeSW.Mcp.exe", "ClaudeSW.Mcp.exe.config", "Anthropic.dll", "runtimes\win-x64\native\WebView2Loader.dll", "icons\pane_20.png") {
    if (-not (Test-Path "$out\$f")) { throw "missing build output: $f" }
}

if (-not $SkipTests) {
    Step "Unit tests"
    & $dotnet test "$root\tests\ClaudeSW.Core.Tests\ClaudeSW.Core.Tests.csproj" -c $Configuration -nologo
    if ($LASTEXITCODE) { throw "tests failed" }
}

if (-not $SkipInstaller) {
    Step "Installer"
    if (-not (Test-Path "$root\installer\redist\MicrosoftEdgeWebview2Setup.exe")) {
        Write-Host "Downloading the WebView2 bootstrapper from Microsoft..."
        Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile "$root\installer\redist\MicrosoftEdgeWebview2Setup.exe" -UseBasicParsing
    }
    $sig = Get-AuthenticodeSignature "$root\installer\redist\MicrosoftEdgeWebview2Setup.exe"
    if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "Microsoft Corporation") { throw "WebView2 bootstrapper signature check failed" }
    $version = ([Reflection.AssemblyName]::GetAssemblyName("$out\ClaudeSW.AddIn.dll")).Version
    $ver = "{0}.{1}.{2}" -f $version.Major, $version.Minor, $version.Build
    $args = @("/Q", "/DAppVersion=$ver", "/DSourceDir=$out")
    if ($Sign) { $args += @("/DSignTool", "/Ssigntool=$Sign") }
    & $iscc @args "$root\installer\ClaudeWorks.iss"
    if ($LASTEXITCODE) { throw "installer build failed" }
    Get-Item "$root\installer\output\ClaudeWorks-Setup.exe" | ForEach-Object { Write-Host ("Installer: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
}
