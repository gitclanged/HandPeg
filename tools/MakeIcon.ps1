# HandPeg's application icon (5.2.0's re-cut, the user's answer): drawn from vector geometry, never painted by hand, and packed into
# src\handpeg.ico at 16, 24, 32, 48, 64, 128 and 256 px (32-bit, each frame a PNG). HandPegApp.csproj names it as the ApplicationIcon, so
# HandPeg.exe, the taskbar, Alt+Tab and the .hproj / .hpb files Explorer shows (DefaultIcon "<exe>",0) all have it; vpk takes it for Setup.exe.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\MakeIcon.ps1 [-Preview <folder>]
#
# The mark, on a 256-unit square: a rounded square in HandPeg's accent blue, a white peg (the Split's cut) beside a white play triangle.
param([string]$Preview)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'src\handpeg.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-Brush([string]$hex) {
    $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $brush.Freeze()
    return $brush
}

function Get-Frame([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))

    # The square: the accent, lighter at the top; a darker edge so it stands out on a light taskbar.
    $fill = New-Object System.Windows.Media.LinearGradientBrush (
        [System.Windows.Media.ColorConverter]::ConvertFromString('#FF3A8BFF'),
        [System.Windows.Media.ColorConverter]::ConvertFromString('#FF0B57D0'), 90.0)
    $fill.Freeze()
    $edge = New-Object System.Windows.Media.Pen ((New-Brush '#FF083E99'), ([Math]::Max(6.0, 256.0 / $size)))
    $edge.Freeze()
    $dc.DrawRoundedRectangle($fill, $edge, (New-Object System.Windows.Rect 10, 10, 236, 236), 54, 54)

    # The peg and the play triangle, white, their corners rounded by a stroke of their own colour.
    $white = New-Brush '#FFFFFFFF'
    $dc.DrawRoundedRectangle($white, $null, (New-Object System.Windows.Rect 58, 66, 34, 124), 12, 12)
    $round = New-Object System.Windows.Media.Pen ($white, 16)
    $round.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $round.Freeze()
    $triangle = [System.Windows.Media.Geometry]::Parse('M 122,76 L 122,180 L 206,128 Z')
    $triangle.Freeze()
    $dc.DrawGeometry($white, $round, $triangle)

    $dc.Pop()
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap ($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    if ($Preview) {
        [System.IO.File]::WriteAllBytes((Join-Path $Preview "handpeg-$size.png"), $stream.ToArray())
    }
    return ,$stream.ToArray()
}

if ($Preview) { New-Item -ItemType Directory -Force $Preview | Out-Null }
$frames = @()
foreach ($size in $sizes) { $frames += ,(Get-Frame $size) }

# ICONDIR, one ICONDIRENTRY per frame, then the frames (PNG-compressed entries, which Windows reads since Vista).
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frames[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Output ("Wrote {0}: {1} bytes, {2} sizes" -f $target, $out.Length, $sizes.Count)
