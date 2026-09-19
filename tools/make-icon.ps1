# Generates assets/shelf.ico from code - no external tools, no image files.
# Mark: a tray/box shape representing a shelf or stack, with accent fill.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root "assets"
if (-not (Test-Path $assets)) { New-Item -ItemType Directory -Path $assets | Out-Null }
$icoPath = Join-Path $assets "shelf.ico"

$Accent  = [System.Drawing.ColorTranslator]::FromHtml("#8AB4F8")
$Card    = [System.Drawing.ColorTranslator]::FromHtml("#232529")
$BgTop   = [System.Drawing.ColorTranslator]::FromHtml("#2A2C2F")
$BgBot   = [System.Drawing.ColorTranslator]::FromHtml("#191A1C")
$Border  = [System.Drawing.ColorTranslator]::FromHtml("#3C4043")

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x,           $y,           $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $p.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $p.CloseFigure()
    return $p
}

function Render-Icon([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $S / 256.0   # design on a 256 grid, scale down

    # --- rounded background ---
    $inset  = 6 * $k
    $radius = [Math]::Max(2.0, 54 * $k)
    $path = New-RoundedPath $inset $inset ($S - $inset * 2) ($S - $inset * 2) $radius
    $rect = New-Object System.Drawing.RectangleF(0, 0, $S, $S)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, $BgTop, $BgBot, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    if ($S -ge 32) {
        $bw = [Math]::Max(1.0, 3 * $k)
        $pen = New-Object System.Drawing.Pen($Border, $bw)
        $g.DrawPath($pen, $path)
        $pen.Dispose()
    }
    $brush.Dispose(); $path.Dispose()

    # --- the tray/shelf mark ---
    # A simplified inbox/tray shape: open-top box with accent fill
    $w = if ($S -le 20) { [Math]::Max(2.0, 24 * $k) } else { 20 * $k }

    # Draw a tray shape - trapezoid-ish box
    $trayPath = New-Object System.Drawing.Drawing2D.GraphicsPath

    # Coordinates for a tray shape (open top box)
    $left   = 56 * $k
    $right  = 200 * $k
    $top    = 70 * $k
    $mid    = 150 * $k
    $bottom = 190 * $k
    $indent = 24 * $k

    # Draw: top-left, top-right, then down and in for the tray sides, bottom
    $trayPath.AddLine($left, $mid, $left, $bottom)
    $trayPath.AddLine($left, $bottom, $right, $bottom)
    $trayPath.AddLine($right, $bottom, $right, $mid)
    $trayPath.AddLine($right, $mid, $right - $indent, $mid)
    $trayPath.AddLine($right - $indent, $mid, $right - $indent, $top + $indent)
    $trayPath.AddLine($right - $indent, $top + $indent, $left + $indent, $top + $indent)
    $trayPath.AddLine($left + $indent, $top + $indent, $left + $indent, $mid)
    $trayPath.AddLine($left + $indent, $mid, $left, $mid)
    $trayPath.CloseFigure()

    $trayBrush = New-Object System.Drawing.SolidBrush($Accent)
    $g.FillPath($trayBrush, $trayPath)
    $trayBrush.Dispose()
    $trayPath.Dispose()

    # Draw a small stack indicator (two lines above the tray)
    $lineY1 = 52 * $k
    $lineY2 = 68 * $k
    $lineLeft = 90 * $k
    $lineRight = 166 * $k
    $lineW = [Math]::Max(2.0, 8 * $k)

    $linePen = New-Object System.Drawing.Pen($Card, $lineW)
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($linePen, $lineLeft, $lineY1, $lineRight, $lineY1)
    $linePen.Dispose()

    $g.Dispose()
    return $bmp
}

# --- encode each size to PNG, then pack an ICO container ---
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$blobs = @()
foreach ($s in $sizes) {
    $bmp = Render-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += ,@($s, $ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)               # reserved
$bw.Write([UInt16]1)               # type: icon
$bw.Write([UInt16]$blobs.Count)

$offset = 6 + (16 * $blobs.Count)
foreach ($b in $blobs) {
    $s = $b[0]; $data = $b[1]
    $dim = 0                              # 0 means 256 in the ICO directory
    if ($s -lt 256) { $dim = $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)             # palette
    $bw.Write([Byte]0)             # reserved
    $bw.Write([UInt16]1)           # colour planes
    $bw.Write([UInt16]32)          # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($b in $blobs) { $bw.Write($b[1]) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

$len = (Get-Item $icoPath).Length
Write-Host "ICO written: $icoPath ($len bytes, $($blobs.Count) sizes: $($sizes -join ', '))"
