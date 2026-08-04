# Generates Assets\vantage.ico — Windows 11 style rounded-square gradient tile with a "V" glyph.
# Multi-size ICO with PNG-compressed entries (16..256).
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-rect tile, Win11 corner radius ~22% of size
    $radius = [Math]::Max(2, [int]($size * 0.22))
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 124, 58, 237),   # violet
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235),    # blue
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    # "V" glyph
    $fontSize = [Math]::Max(6, $size * 0.58)
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF(0, ($size * 0.02), $size, $size)
    $g.DrawString("V", $font, [System.Drawing.Brushes]::White, $textRect, $fmt)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $size; Bytes = $ms.ToArray() }
    $g.Dispose(); $bmp.Dispose(); $ms.Dispose()
}

# ICO container: ICONDIR + ICONDIRENTRY[] + png blobs
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { [byte]0 } else { [byte]$p.Size }
    $w.Write($dim); $w.Write($dim)          # width, height (0 = 256)
    $w.Write([byte]0); $w.Write([byte]0)    # palette, reserved
    $w.Write([UInt16]1); $w.Write([UInt16]32)  # planes, bpp
    $w.Write([UInt32]$p.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $w.Write($p.Bytes) }
$w.Flush()

$dest = Join-Path $PSScriptRoot "..\src\Vantage.App\Assets\vantage.ico"
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
Write-Output "Wrote $dest ($($out.Length) bytes, $($pngs.Count) sizes)"
