# 生成 DeepSeek Harness 启动器图标 (launcher.ico)
# 深蓝渐变圆角方块 + 白色"鲸"字, 多尺寸 PNG-in-ICO (16/32/48/256)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "launcher.ico"
$sizes = 16, 32, 48, 256
$entries = New-Object System.Collections.Generic.List[object]

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    $r = New-Object System.Drawing.Rectangle(1, 1, ($s - 2), ($s - 2))
    $radius = [int]($s * 0.24)
    if ($radius -lt 2) { $radius = 2 }
    $d = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r, [System.Drawing.Color]::FromArgb(255, 10, 26, 56), [System.Drawing.Color]::FromArgb(255, 34, 96, 178), 45)
    $g.FillPath($brush, $path)

    if ($s -ge 32) {
        $fontSize = [double]$s * 0.52
        $font = New-Object System.Drawing.Font("Microsoft YaHei", [single]$fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rf = New-Object System.Drawing.RectangleF($r.X, $r.Y, $r.Width, $r.Height)
        $g.DrawString([string][char]0x9CB8, $font, [System.Drawing.Brushes]::White, $rf, $sf)
        $font.Dispose(); $sf.Dispose()
    }
    $brush.Dispose(); $path.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add(@{ size = $s; data = $ms.ToArray() })
    $ms.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)          # reserved
$bw.Write([uint16]1)          # type: icon
$bw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $w = $e.size
    if ($w -ge 256) { $w = 0 }
    $bw.Write([byte]$w)
    $bw.Write([byte]$w)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)      # planes
    $bw.Write([uint16]32)     # bit count
    $bw.Write([uint32]$e.data.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.data.Length
}
foreach ($e in $entries) {
    $bw.Write($e.data)
}
$bw.Close()
$fs.Close()
Write-Host "icon written: $out"
