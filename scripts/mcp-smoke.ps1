<#
.SYNOPSIS
  Talks MCP (stdio JSON-RPC) to ClaudeSW.Mcp.exe exactly like Claude Code does, and builds the
  acceptance plate through it. SOLIDWORKS with the add-in must be running.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\ClaudeSW.AddIn\bin\Debug\net48\ClaudeSW.Mcp.exe"),
    [string]$OutDir = (Join-Path $env:TEMP "claude-sw-mcp")
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$psi = New-Object System.Diagnostics.ProcessStartInfo (Resolve-Path $Exe).Path
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$p = [Diagnostics.Process]::Start($psi)
$script:id = 0
function Rpc([string]$method, $params) {
    $script:id++
    $msg = @{ jsonrpc = "2.0"; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $p.StandardInput.WriteLine($msg); $p.StandardInput.Flush()
    $line = $p.StandardOutput.ReadLine()
    return $line | ConvertFrom-Json
}
function Call([string]$name, [hashtable]$a = @{}, [string]$saveImage) {
    $r = Rpc "tools/call" @{ name = $name; arguments = $a }
    $text = ($r.result.content | Where-Object type -eq "text" | ForEach-Object text) -join " "
    $img = $r.result.content | Where-Object type -eq "image" | Select-Object -First 1
    if ($img -and $saveImage) { [IO.File]::WriteAllBytes((Join-Path $OutDir $saveImage), [Convert]::FromBase64String($img.data)) }
    $flag = if ($r.result.isError) { "FAIL" } else { "ok  " }
    $short = if ($text.Length -gt 110) { $text.Substring(0, 110) + "..." } else { $text }
    Write-Host ("{0} {1,-24} {2}{3}" -f $flag, $name, $short, $(if ($img) { " [image]" } else { "" }))
    return $text
}

$init = Rpc "initialize" @{ protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "mcp-smoke"; version = "1" } }
Write-Host "server: $($init.result.serverInfo.name) $($init.result.serverInfo.version), protocol $($init.result.protocolVersion)"
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$tools = (Rpc "tools/list" @{}).result.tools
Write-Host "tools: $($tools.Count) (first: $($tools[0].name), last: $($tools[-1].name))"

Call "get_solidworks_context" | Out-Null
Call "new_document" @{ doc_type = "part" } | Out-Null
Call "begin_sketch" @{ plane = "Front" } | Out-Null
Call "add_rectangle" @{ x1 = 0; y1 = 0; x2 = 50; y2 = 30; centered = $true } | Out-Null
Call "end_sketch" | Out-Null
Call "extrude" @{ depth = 20 } | Out-Null
$f = Call "find_faces" @{ normal = "+Z" }
$top = ($f | ConvertFrom-Json).faces[0].ref
Call "begin_sketch" @{ refs = @($top) } | Out-Null
Call "add_circle" @{ cx = 0; cy = 0; radius = 5 } | Out-Null
Call "end_sketch" | Out-Null
Call "extrude" @{ cut = $true; direction = "through_all" } | Out-Null
$e = Call "find_edges" @{ kind = "line"; direction = "Z" }
$refs = @(($e | ConvertFrom-Json).edges | ForEach-Object { $_.ref })
Call "fillet" @{ radius = 3; refs = $refs } | Out-Null
Call "get_model_info" | Out-Null
Call "capture_view" @{ view = "isometric" } "mcp-plate.png" | Out-Null
Call "undo_last_change" | Out-Null
Call "list_features" | Out-Null
$p.StandardInput.Close(); $p.WaitForExit(5000) | Out-Null
Write-Host "Done. Image: $OutDir\mcp-plate.png"
