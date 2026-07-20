<#
  Generates assets/app.ico for WpadManager.exe using only in-box .NET (System.Drawing).
  Glyph: a routing decision - one client node branching into a direct path (white)
  and a proxy path (amber) - on a rounded blue tile.
  Run:  powershell -ExecutionPolicy Bypass -File build/make-icon.ps1
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$out    = Join-Path $assets "app.ico"

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $gp.AddArc($x,       $y,       $d, $d, 180, 90)
    $gp.AddArc($x+$w-$d, $y,       $d, $d, 270, 90)
    $gp.AddArc($x+$w-$d, $y+$h-$d, $d, $d,   0, 90)
    $gp.AddArc($x,       $y+$h-$d, $d, $d,  90, 90)
    $gp.CloseFigure()
    return $gp
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    $path = New-RoundedPath (8*$s) (8*$s) (240*$s) (240*$s) (52*$s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0,0),
        (New-Object System.Drawing.Point $size,$size),
        [System.Drawing.Color]::FromArgb(255, 58, 111, 176),
        [System.Drawing.Color]::FromArgb(255, 20,  55,  91))
    $g.FillPath($brush, $path)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (15*$s)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen,  72*$s, 128*$s, 126*$s, 128*$s)
    $g.DrawLine($pen, 126*$s, 128*$s, 186*$s,  80*$s)
    $g.DrawLine($pen, 126*$s, 128*$s, 186*$s, 176*$s)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 240, 163, 48))
    $g.FillEllipse($white,  (72-24)*$s, (128-24)*$s, 48*$s, 48*$s)
    $g.FillEllipse($white, (186-19)*$s,  (80-19)*$s, 38*$s, 38*$s)
    $g.FillEllipse($amber, (186-19)*$s, (176-19)*$s, 38*$s, 38*$s)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $data = $ms.ToArray()
    $ms.Dispose(); $g.Dispose(); $bmp.Dispose()
    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $white.Dispose(); $amber.Dispose()
    return ,$data
}

$sizes = @(16,24,32,48,64,128,256)
$pngs  = New-Object System.Collections.ArrayList
foreach ($sz in $sizes) { [void]$pngs.Add([byte[]](New-IconPng $sz)) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $len = $pngs[$i].Length
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0);    $bw.Write([Byte]0)
    $bw.Write([UInt16]1);  $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
for ($i = 0; $i -lt $sizes.Count; $i++) { $bw.Write($pngs[$i], 0, $pngs[$i].Length) }
$bw.Close(); $fs.Close()

Write-Host "Icon written: $out ($((Get-Item $out).Length) bytes, $($sizes.Count) sizes)" -ForegroundColor Green
