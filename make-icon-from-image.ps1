# Build a multi-size PNG-in-ICO icon file from a source image.
# Auto-trims empty margins, square center-crops, then writes 16/32/48/256 icons.
# Usage: make-icon-from-image.ps1 -InputImage <path> -OutputIco <path>
param(
    [Parameter(Mandatory = $true)][string]$InputImage,
    [Parameter(Mandatory = $true)][string]$OutputIco
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$src = New-Object System.Drawing.Bitmap($InputImage)
$w = $src.Width
$h = $src.Height
$hasAlpha = ($src.PixelFormat -band [System.Drawing.Imaging.PixelFormat]::Alpha) -ne 0
Write-Host "source: ${w}x${h} alpha=$hasAlpha"

# ---- exact-ish content bounding box (sample every 3 px for speed) ----
$minX = $w; $minY = $h; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $h; $y += 3) {
    for ($x = 0; $x -lt $w; $x += 3) {
        $c = $src.GetPixel($x, $y)
        $opaque = $true
        if ($hasAlpha) {
            if ($c.A -lt 10) { $opaque = $false }
        } else {
            if ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250) { $opaque = $false }
        }
        if ($opaque) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
if ($maxX -lt 0) { throw "no visible content found in source image" }
Write-Host "content bbox: x[$minX..$maxX] y[$minY..$maxY]"

# ---- square center-crop of the bbox with a little padding ----
$cw = $maxX - $minX + 1
$ch = $maxY - $minY + 1
$side = [Math]::Min($cw, $ch)
$pad = [int]($side * 0.04)
$side = $side + 2 * $pad
$cropX = $minX + [int](($cw - ($side - 2 * $pad)) / 2) - $pad
$cropY = $minY + [int](($ch - ($side - 2 * $pad)) / 2) - $pad
if ($cropX -lt 0) { $cropX = 0 }
if ($cropY -lt 0) { $cropY = 0 }
if (($cropX + $side) -gt $w) { $side = $w - $cropX }
if (($cropY + $side) -gt $h) { $side = $h - $cropY }
$cropRect = New-Object System.Drawing.Rectangle($cropX, $cropY, $side, $side)
$square = $src.Clone($cropRect, $src.PixelFormat)
Write-Host "square crop: ${side}x${side} at ($cropX,$cropY)"

# ---- render each size as PNG in memory ----
$sizes = 16, 32, 48, 256
$entries = New-Object System.Collections.Generic.List[object]
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $destRect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $g.DrawImage($square, $destRect)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add(@{ size = $s; data = $ms.ToArray() })
    $ms.Dispose(); $bmp.Dispose()
}
$square.Dispose(); $src.Dispose()

# ---- write ICO container (PNG-compressed entries) ----
$fs = [System.IO.File]::Create($OutputIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = $e.size
    if ($dim -ge 256) { $dim = 0 }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$e.data.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.data.Length
}
foreach ($e in $entries) {
    $bw.Write($e.data)
}
$bw.Close()
$fs.Close()
Write-Host "icon written: $OutputIco"
