<#
.SYNOPSIS
  Repeatable in-SOLIDWORKS test of the whole add-in (UI + agent loop + real tools) with the
  offline scripted model: no API key or network needed.
.DESCRIPTION
  1. Restarts SOLIDWORKS with the add-in in demo mode (scripts/demo/acceptance.json).
  2. Sends "make a 100x60x20 plate..." through the pane, waits, screenshots SOLIDWORKS + pane.
  3. Selects the plate's top face, asks "what is selected?", screenshots.
  4. Asks for a 3-view drawing, screenshots.
  Screenshots land in -OutDir. For the live (API) version follow docs/MANUAL_TEST.md.
#>
param([string]$OutDir = (Join-Path $env:TEMP "claude-sw-acceptance"), [switch]$NoRestart)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-ChildItem $OutDir -Filter *.SLD* -ErrorAction SilentlyContinue | Remove-Item -Force
$env:CLAUDESW_DEMO_OUT = $OutDir
$pipe = Join-Path $PSScriptRoot "devpipe.ps1"
$shot = Join-Path $PSScriptRoot "screenshot.ps1"

function Dev([hashtable]$cmd) { & $pipe ($cmd | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json }
function WaitIdle([int]$timeoutSec = 240) {
    $t = 0
    do { Start-Sleep 2; $t += 2; $s = Dev @{ cmd = "state" } } while ($s.busy -and $t -lt $timeoutSec)
    return $s
}
function Snap([string]$name) {
    & $shot -Out (Join-Path $OutDir "$name-solidworks.png") | Out-Null
    Dev @{ cmd = "screenshot"; path = (Join-Path $OutDir "$name-pane.png") } | Out-Null
    Write-Host "  screenshots: $name-*.png"
}

if (-not $NoRestart) {
    & (Join-Path $PSScriptRoot "dev-reload.ps1") -Demo (Join-Path $PSScriptRoot "demo\acceptance.json") | Out-Null
    Start-Sleep 8
}

Write-Host "1. Build the plate"
Dev @{ cmd = "ui"; message = @{ type = "chat.send"; text = "make a 100x60x20 plate with a 10 mm centre hole and 3 mm corner fillets" } } | Out-Null
$s = WaitIdle
Write-Host ("  tools run: {0}; tool errors: {1}" -f ($s.tools -join ", "), (@($s.errors).Count))
Snap "1-plate"

Write-Host "2. Select the top face and ask what is selected"
Dev @{ cmd = "tool"; name = "find_faces"; input = @{ normal = "+Z" } } | Out-Null
Dev @{ cmd = "tool"; name = "select_entities"; input = @{ refs = @("F1") } } | Out-Null
Start-Sleep 2
Dev @{ cmd = "ui"; message = @{ type = "chat.send"; text = "what is selected?"; useSelection = $true } } | Out-Null
$s = WaitIdle
Snap "2-selection"

Write-Host "3. Create a 3-view drawing"
Dev @{ cmd = "ui"; message = @{ type = "chat.send"; text = "create a 3-view drawing of it" } } | Out-Null
$s = WaitIdle
Write-Host ("  tools run: {0}; tool errors: {1}" -f ($s.tools -join ", "), (@($s.errors).Count))
Snap "3-drawing"
Write-Host "Done. Output: $OutDir"
