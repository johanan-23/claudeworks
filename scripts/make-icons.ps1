# Generates the Task Pane / installer icons (original artwork: a warm rounded tile with a spark).
# Output: assets/icons/pane_<size>.png and assets/icons/app.ico
param([string]$Out = (Join-Path $PSScriptRoot "..\assets\icons"))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

function New-Icon([int]$size, [bool]$tile) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $c = $size / 2.0
    if ($tile) {
        $r = [Math]::Max(2, $size * 0.22)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $r * 2; $w = $size - 1
        $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
        $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 201, 100, 66))), $path)
        $ink = [System.Drawing.Color]::White
    } else {
        $ink = [System.Drawing.Color]::FromArgb(255, 201, 100, 66)
    }
    # spark: 4 long + 4 short tapered rays
    $brush = New-Object System.Drawing.SolidBrush $ink
    foreach ($i in 0..7) {
        $ang = [Math]::PI / 4 * $i
        $len = if ($i % 2 -eq 0) { $size * 0.36 } else { $size * 0.22 }
        $wid = [Math]::Max(1.2, $size * 0.075)
        $tip = New-Object System.Drawing.PointF ([float]($c + [Math]::Cos($ang) * $len)), ([float]($c + [Math]::Sin($ang) * $len))
        $l = New-Object System.Drawing.PointF ([float]($c + [Math]::Cos($ang + [Math]::PI / 2) * $wid)), ([float]($c + [Math]::Sin($ang + [Math]::PI / 2) * $wid))
        $rr = New-Object System.Drawing.PointF ([float]($c + [Math]::Cos($ang - [Math]::PI / 2) * $wid)), ([float]($c + [Math]::Sin($ang - [Math]::PI / 2) * $wid))
        $g.FillPolygon($brush, [System.Drawing.PointF[]]@($l, $tip, $rr))
    }
    $dot = $size * 0.11
    $g.FillEllipse($brush, [float]($c - $dot), [float]($c - $dot), [float]($dot * 2), [float]($dot * 2))
    $g.Dispose()
    return $bmp
}

foreach ($s in 20, 32, 40, 64, 96, 128) {
    $b = New-Icon $s $true
    $b.Save((Join-Path $Out "pane_$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

# Multi-size .ico (PNG-compressed entries) for the installer and Add/Remove Programs.
$sizes = 16, 32, 48, 256
$pngs = foreach ($s in $sizes) { $b = New-Icon $s $true; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
$fs = [IO.File]::Create((Join-Path $Out "app.ico"))
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Icons written to $Out"
