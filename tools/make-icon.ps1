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

# The same artwork the taskbar pill draws (see Ui.DrawDownloadsIcon in src).
# Kept in step deliberately: the icon in Explorer, Alt-Tab and the exe itself
# should be the thing already recognised on the taskbar, not a second mark
# that exists only here. The proportions are the identical fractions of the
# box, so this is a port rather than a lookalike.
function Render-Icon([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # A little margin so the folder is not flush with the icon edge.
    $m = $S * 0.06
    $x = $m; $y = $m
    $w = $S - $m * 2; $h = $S - $m * 2

    $tabColor    = [System.Drawing.ColorTranslator]::FromHtml("#2E8BD6")
    $bodyTop     = [System.Drawing.ColorTranslator]::FromHtml("#7FC4F5")
    $bodyBottom  = [System.Drawing.ColorTranslator]::FromHtml("#3B9EE8")
    $panelTop    = [System.Drawing.ColorTranslator]::FromHtml("#A8D8F8")
    $panelBottom = [System.Drawing.ColorTranslator]::FromHtml("#57AEEF")
    $arrowColor  = [System.Drawing.ColorTranslator]::FromHtml("#17405F")
    $shadowColor = [System.Drawing.Color]::FromArgb(30, 0, 0, 0)
    $hiColor     = [System.Drawing.Color]::FromArgb(90, 255, 255, 255)

    $tabWidth    = 0.42 * $w
    $tabHeight   = 0.20 * $h
    $tabRadius   = 0.06 * $w
    $bodyRadius  = 0.13 * $w
    $panelRadius = 0.11 * $w
    $panelInset  = 0.04 * $w
    $shadowOff   = 0.02 * $h

    $bodyY = $y + 0.14 * $h
    $bodyH = $h - 0.14 * $h

    $p = New-RoundedPath $x ($bodyY + $shadowOff) $w $bodyH $bodyRadius
    $b = New-Object System.Drawing.SolidBrush($shadowColor)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    $p = New-RoundedPath $x $y $tabWidth $tabHeight $tabRadius
    $b = New-Object System.Drawing.SolidBrush($tabColor)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    $p = New-RoundedPath $x $bodyY $w $bodyH $bodyRadius
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF($x, $bodyY)),
        (New-Object System.Drawing.PointF($x, ($bodyY + $bodyH))),
        $bodyTop, $bodyBottom)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    $panelX = $x + $panelInset
    $panelY = $y + 0.30 * $h
    $panelW = $w - $panelInset * 2
    $panelH = 0.67 * $h
    $p = New-RoundedPath $panelX $panelY $panelW $panelH $panelRadius
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF($panelX, $panelY)),
        (New-Object System.Drawing.PointF($panelX, ($panelY + $panelH))),
        $panelTop, $panelBottom)
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()

    if ($S -ge 24) {
        $pen = New-Object System.Drawing.Pen($hiColor, [Math]::Max(1.0, $S / 256.0))
        $g.DrawLine($pen, ($panelX + $panelRadius), $panelY, ($panelX + $panelW - $panelRadius), $panelY)
        $pen.Dispose()
    }

    $cx = $x + $w / 2.0
    $pen = New-Object System.Drawing.Pen($arrowColor, (0.10 * $w))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLine($pen, $cx, ($y + 0.44 * $h), $cx, ($y + 0.74 * $h))
    $spread = 0.17 * $w
    $pts = @(
        (New-Object System.Drawing.PointF(($cx - $spread), ($y + 0.62 * $h))),
        (New-Object System.Drawing.PointF($cx,             ($y + 0.78 * $h))),
        (New-Object System.Drawing.PointF(($cx + $spread), ($y + 0.62 * $h)))
    )
    $g.DrawLines($pen, $pts)
    $pen.Dispose()

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
