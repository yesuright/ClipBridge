# Original ClipBridge geometry. No downloaded art or production drawing dependency.
# Regenerates the multi-resolution ICO with PNG entries using Windows System.Drawing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = Join-Path $root 'apps\windows\src\ClipBridge.Win\Assets'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap ($size * 4), ($size * 4)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(($size * 4 / 32.0), ($size * 4 / 32.0))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(1, 1, 12, 12, 180, 90); $path.AddArc(19, 1, 12, 12, 270, 90)
    $path.AddArc(19, 19, 12, 12, 0, 90); $path.AddArc(1, 19, 12, 12, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#246c85'))
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#f5fbff')), 2.2
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    # Clipboard silhouette, with an open edge bridged by a transfer arrow.
    $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(11,8),[System.Drawing.PointF]::new(8,8),[System.Drawing.PointF]::new(8,25),[System.Drawing.PointF]::new(22,25),[System.Drawing.PointF]::new(22,22)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(19,8),[System.Drawing.PointF]::new(22,8),[System.Drawing.PointF]::new(22,11)))
    $g.DrawRectangle($pen, 12, 6, 6, 4)
    $g.DrawLine($pen, 13, 17, 26, 17)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(23,14),[System.Drawing.PointF]::new(26,17),[System.Drawing.PointF]::new(23,20)))
    $small = New-Object System.Drawing.Bitmap $size, $size
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = 'HighQualityBicubic'; $sg.DrawImage($bitmap, 0, 0, $size, $size)
    $stream = New-Object System.IO.MemoryStream
    $small.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    $stream.Dispose(); $sg.Dispose(); $small.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $target 'ClipBridge.ico'))
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Length)
$offset = 6 + 16 * $sizes.Length
for ($i = 0; $i -lt $sizes.Length; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $file.Dispose()
Write-Host 'Original ClipBridge icon generated: 16, 20, 24, 32, 48, 64, 128, 256 px.'
