# Regenerates assets\app.ico, the Haptics Everywhere icon: a blue-to-violet rounded square with "((•))" haptic waves.
#   pwsh .\scripts\make-icon.ps1
# 256 px is stored as PNG; smaller sizes are 32-bit DIBs. Below 24 px the waves collapse to one bolder arc so it reads in the tray.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets\app.ico'
$sizes = 256, 64, 48, 40, 32, 28, 24, 20, 16

function New-Frame([int]$s) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [math]::Max(0.5, $s * 0.03)
    $side = $s - 2 * $inset
    $d = $side * 0.44
    $plate = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $plate.AddArc($inset, $inset, $d, $d, 180, 90)
    $plate.AddArc($inset + $side - $d, $inset, $d, $d, 270, 90)
    $plate.AddArc($inset + $side - $d, $inset + $side - $d, $d, $d, 0, 90)
    $plate.AddArc($inset, $inset + $side - $d, $d, $d, 90, 90)
    $plate.CloseFigure()
    $rect = [System.Drawing.RectangleF]::new($inset, $inset, $side, $side)
    $fill = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect,
        [System.Drawing.Color]::FromArgb(0x4F, 0x7B, 0xFF), [System.Drawing.Color]::FromArgb(0x8E, 0x5B, 0xFF), 45.0)
    $g.FillPath($fill, $plate)

    $c = $s / 2.0
    $small = $s -lt 24
    $dot = $s * $(if ($small) { 0.11 } else { 0.09 })
    $g.FillEllipse([System.Drawing.Brushes]::White, $c - $dot, $c - $dot, 2 * $dot, 2 * $dot)

    $waves = @(@(0.235, 255), @(0.36, 190))
    if ($small) { $waves = @(, @(0.29, 255)) }
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [math]::Max(1.4, $s * $(if ($small) { 0.1 } else { 0.075 })))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($wave in $waves) {
        $r = $s * $wave[0]
        $pen.Color = [System.Drawing.Color]::FromArgb($wave[1], 255, 255, 255)
        $box = [System.Drawing.RectangleF]::new($c - $r, $c - $r, 2 * $r, 2 * $r)
        $g.DrawArc($pen, $box, -42, 84)
        $g.DrawArc($pen, $box, 138, 84)
    }

    $pen.Dispose(); $fill.Dispose(); $plate.Dispose(); $g.Dispose()
    return $bmp
}

function Get-Png([System.Drawing.Bitmap]$bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# Classic icon DIB: BITMAPINFOHEADER (double height), bottom-up BGRA rows, then a 1-bpp AND mask set where alpha is 0.
function Get-Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $maskRow = [int][math]::Floor(($s + 31) / 32) * 4
    $ms = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($ms)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4 + $maskRow * $s)); $w.Write([int32]0); $w.Write([int32]0)
    $w.Write([uint32]0); $w.Write([uint32]0)
    $mask = [byte[]]::new($maskRow * $s)
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = $s - 1 - $y
        for ($x = 0; $x -lt $s; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
            if ($p.A -eq 0) { $mask[$row * $maskRow + [math]::Floor($x / 8)] = $mask[$row * $maskRow + [math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
    }
    $w.Write($mask)
    $w.Flush()
    return $ms.ToArray()
}

$images = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    , $(if ($s -ge 256) { Get-Png $bmp } else { Get-Dib $bmp })
    $bmp.Dispose()
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Dispose()
Write-Host "$out  ($((Get-Item $out).Length) bytes, $($sizes.Count) sizes)"
