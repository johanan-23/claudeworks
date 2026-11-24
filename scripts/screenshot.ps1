# Captures the SOLIDWORKS main window to a PNG even when other windows cover it (PrintWindow),
# or the whole primary screen with -Screen. Used by the manual test script.
param([string]$Out = (Join-Path $env:TEMP "sw-shot.png"), [switch]$Screen, [int]$MaxWidth = 1600)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W32 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W32]::SetProcessDPIAware() | Out-Null
$bmp = $null
if (-not $Screen) {
    $p = Get-Process sldworks -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) {
        $h = $p.MainWindowHandle
        if ([W32]::IsIconic($h)) { [W32]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 500 }
        $r = New-Object W32+RECT
        [W32]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [W32]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT: includes DirectX/WebView content
        $g.ReleaseHdc($hdc); $g.Dispose()
    }
}
if (-not $bmp) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $g.Dispose()
}
if ($bmp.Width -gt $MaxWidth) {
    $h2 = [int]($bmp.Height * $MaxWidth / $bmp.Width)
    $small = New-Object System.Drawing.Bitmap $bmp, $MaxWidth, $h2
    $bmp.Dispose(); $bmp = $small
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output $Out
