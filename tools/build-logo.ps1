$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\WallpaperSwitcher\Assets'))
[IO.Directory]::CreateDirectory($assets) | Out-Null

# Code-native vector geometry mirrors Assets/logo.svg. Render at 4x for clean small icons.
function RoundRect([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $width - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $width - $d), ($y + $height - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $height - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function FillShape($graphics, $path, [string]$color) {
    $brush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($color))
    $graphics.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()
}
function Polygon($graphics, [int[]]$xy, [string]$color) {
    $points = for ($i = 0; $i -lt $xy.Length; $i += 2) { [Drawing.PointF]::new($xy[$i], $xy[$i + 1]) }
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon([Drawing.PointF[]]$points)
    FillShape $graphics $p $color
}
$source = New-Object Drawing.Bitmap 1024,1024
$g = [Drawing.Graphics]::FromImage($source)
$g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.ScaleTransform(4,4)
$tile = RoundRect 8 8 240 240 54
$gradient = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Point]::new(8,8)),([Drawing.Point]::new(248,248)),([Drawing.ColorTranslator]::FromHtml('#3265df')),([Drawing.ColorTranslator]::FromHtml('#243d91'))
$g.FillPath($gradient,$tile)
$gradient.Dispose()
$tile.Dispose()
FillShape $g (RoundRect 64 48 148 126 22) '#74dbd4'
FillShape $g (RoundRect 38 66 172 142 28) '#ffffff'
$clip = RoundRect 48 76 152 122 18
$g.SetClip($clip)
FillShape $g (RoundRect 48 76 152 122 18) '#e9f3ff'
$sun = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#ffc968'))
$g.FillEllipse($sun,150,88,32,32)
$sun.Dispose()
Polygon $g @(34,176,103,105,170,181,208,157,208,210,34,210) '#4b82df'
Polygon $g @(36,198,84,157,113,178,157,132,214,187,214,214,36,214) '#25b2aa'
Polygon $g @(36,203,90,180,138,201,201,166,216,214,36,214) '#168e97'
$g.ResetClip()
$clip.Dispose()
$g.Dispose()

function ResizeImage([int]$size) {
    $bitmap = New-Object Drawing.Bitmap $size,$size
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($source,0,0,$size,$size)
    $graphics.Dispose()
    return $bitmap
}
$logo = ResizeImage 512
$logo.Save((Join-Path $assets 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
$preview = ResizeImage 128
$preview.Save((Join-Path $assets 'logo-preview.png'), [Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
$sizes = @(16,20,24,32,40,48,64,128,256)
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = ResizeImage $size
    $memory = [IO.MemoryStream]::new()
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $images.Add($memory.ToArray())
    $bitmap.Dispose()
    $memory.Dispose()
}
$stream = [IO.File]::Create((Join-Path $assets 'wallpaper.ico'))
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $dimension = if ($size -eq 256) { 0 } else { $size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($imageBytes in $images) { $writer.Write($imageBytes) }
$writer.Dispose()
$source.Dispose()
# Verify Windows/.NET can open the finished icon.
$icon = [Drawing.Icon]::new((Join-Path $assets 'wallpaper.ico'),32,32)
Write-Output ("Logo generated; icon opened at {0}x{1}, with {2} sizes." -f $icon.Width,$icon.Height,$sizes.Count)
$icon.Dispose()
