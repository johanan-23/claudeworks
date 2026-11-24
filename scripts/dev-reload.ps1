<#
.SYNOPSIS
  Developer loop: close SOLIDWORKS, rebuild the add-in, start SOLIDWORKS with the dev pipe enabled
  and load the add-in (per-user COM registration + ISldWorks.LoadAddIn; no admin rights needed).
.PARAMETER Demo
  Path to a scripted demo conversation (offline model) to replay instead of calling the API.
#>
param([string]$Demo, [switch]$NoBuild, [switch]$KeepRunning)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root "src\ClaudeSW.AddIn\bin\Debug\net48"
$sw = "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe"
if ($env:SOLIDWORKS_EXE) { $sw = $env:SOLIDWORKS_EXE }

if (-not $KeepRunning) {
    Get-Process sldworks -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 3
}
if (-not $NoBuild) {
    $dotnet = "dotnet"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
    & $dotnet build (Join-Path $root "src\ClaudeSW.AddIn\ClaudeSW.AddIn.csproj") -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
& (Join-Path $PSScriptRoot "register-dev.ps1") -BuildDir $build -Scope User

$env:CLAUDESW_DEV = "1"
if ($Demo) { $env:CLAUDESW_DEMO_SCRIPT = (Resolve-Path $Demo).Path } else { Remove-Item Env:\CLAUDESW_DEMO_SCRIPT -ErrorAction SilentlyContinue }
if (-not (Get-Process sldworks -ErrorAction SilentlyContinue | Where-Object MainWindowTitle)) {
    Start-Process $sw
    $t = 0
    while ($t -lt 180 -and -not (Get-Process sldworks -ErrorAction SilentlyContinue | Where-Object MainWindowTitle)) { Start-Sleep 3; $t += 3 }
    Start-Sleep 10
}
$swctl = Join-Path $root "tools\swctl\bin\Debug\net48\swctl.exe"
if (-not (Test-Path $swctl)) {
    $dotnet = "dotnet"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
    & $dotnet build (Join-Path $root "tools\swctl\swctl.csproj") -nologo -v q | Out-Null
}
& $swctl load (Join-Path $build "ClaudeSW.AddIn.dll")
