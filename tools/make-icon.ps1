Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$out = 'D:\Code\ZKeyAudioLink\src\ZKeyAudioLink.App\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.045))
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, $size - 2 * $pad, $size - 2 * $pad)
    $radius = [float]($size * 0.22)
    $path = New-RoundedPath $rect.X $rect.Y $rect.Width $rect.Height $radius
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235),
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    if ($size -ge 24) {
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [float]([Math]::Max(1, $size * 0.035)))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $y = [float]($size * 0.735)
        $points = @(
            [System.Drawing.PointF]::new([float]($size * 0.30), $y),
            [System.Drawing.PointF]::new([float]($size * 0.40), [float]($y - $size * 0.06)),
            [System.Drawing.PointF]::new([float]($size * 0.50), $y),
            [System.Drawing.PointF]::new([float]($size * 0.60), [float]($y + $size * 0.06)),
            [System.Drawing.PointF]::new([float]($size * 0.70), $y)
        )
        $g.DrawLines($pen, [System.Drawing.PointF[]]$points)
        $pen.Dispose()
    }

    $fontSize = [float]($size * 0.52)
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = [System.Drawing.StringFormat]::new()
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = [System.Drawing.RectangleF]::new(0, [float](-$size * 0.05), $size, $size)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $g.DrawString('Z', $font, $white, $textRect, $fmt)

    $white.Dispose(); $font.Dispose(); $fmt.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = [System.Collections.ArrayList]::new()
foreach ($s in $sizes) { [void]$images.Add([byte[]](New-IconPng $s)) }

$fs = [System.IO.File]::Create($out)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $images[$i]
    $dim = 0
    if ($s -lt 256) { $dim = $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $images) { $bw.Write($bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Output "Wrote $out ($((Get-Item $out).Length) bytes)"
