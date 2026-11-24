<#
.SYNOPSIS
  Drives the add-in's real SOLIDWORKS tools through the dev pipe (no API key needed) and checks results.
  Builds the acceptance plate (100 x 60 x 20 mm, 10 mm centre hole, 3 mm corner fillets),
  verifies its volume, then makes a 3-view drawing. Images land in -OutDir.
#>
param([string]$OutDir = (Join-Path $env:TEMP "claude-sw-smoke"))
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pipe = Join-Path $PSScriptRoot "devpipe.ps1"
$fail = 0

function Invoke-Tool([string]$name, [hashtable]$inputArgs = @{}, [string]$image) {
    $cmd = @{ cmd = "tool"; name = $name; input = $inputArgs }
    if ($image) { $cmd.image_path = (Join-Path $OutDir $image) }
    $reply = & $pipe ($cmd | ConvertTo-Json -Depth 8 -Compress) | ConvertFrom-Json
    $status = if ($reply.ok) { "ok  " } else { "FAIL"; $script:fail++ }
    Write-Host ("{0} {1,-22} {2}" -f $status, $name, $reply.summary)
    return $reply
}

Invoke-Tool "new_document" @{ doc_type = "part" } | Out-Null
Invoke-Tool "begin_sketch" @{ plane = "Front" } | Out-Null
Invoke-Tool "add_rectangle" @{ x1 = 0; y1 = 0; x2 = 50; y2 = 30; centered = $true } | Out-Null
Invoke-Tool "end_sketch" | Out-Null
Invoke-Tool "extrude" @{ depth = 20 } | Out-Null
$faces = Invoke-Tool "find_faces" @{ normal = "+Z" }
$top = ($faces.text | ConvertFrom-Json).faces[0].ref
Invoke-Tool "begin_sketch" @{ refs = @($top) } | Out-Null
Invoke-Tool "add_circle" @{ cx = 0; cy = 0; radius = 5 } | Out-Null
Invoke-Tool "end_sketch" | Out-Null
Invoke-Tool "extrude" @{ cut = $true; direction = "through_all" } | Out-Null
$edges = Invoke-Tool "find_edges" @{ kind = "line"; direction = "Z" }
$refs = @(($edges.text | ConvertFrom-Json).edges | ForEach-Object { $_.ref })
Write-Host "     corner edges: $($refs -join ', ')"
Invoke-Tool "fillet" @{ radius = 3; refs = $refs } | Out-Null
$info = Invoke-Tool "get_model_info"
$i = $info.text | ConvertFrom-Json
$expected = 100 * 60 * 20 - [Math]::PI * 25 * 20 - 4 * (9 - [Math]::PI * 9 / 4) * 20
Write-Host ("     volume {0} mm3 (expected {1:0.0}); bbox {2}" -f $i.volume_mm3, $expected, ($i.bounding_box_mm.size -join ' x '))
if ([Math]::Abs($i.volume_mm3 - $expected) -gt 1) { Write-Host "FAIL volume mismatch"; $fail++ }
Invoke-Tool "capture_view" @{ view = "isometric" } "plate.png" | Out-Null
Invoke-Tool "list_features" | Out-Null

# drawing (needs a saved model)
$part = Join-Path $OutDir "plate.SLDPRT"
if (Test-Path $part) { Remove-Item $part }
Invoke-Tool "save_document" @{ path = $part } | Out-Null
Invoke-Tool "create_drawing" @{ paper = "A3" } | Out-Null
Invoke-Tool "list_drawing_views" | Out-Null
Invoke-Tool "insert_model_dimensions" | Out-Null
Invoke-Tool "capture_view" @{} "drawing.png" | Out-Null
Write-Host ("{0} failure(s). Images in {1}" -f $fail, $OutDir)
exit $fail
