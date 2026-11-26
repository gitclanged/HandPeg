using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp;

/// <summary>
/// Aspect Sandbox (Pass 5.6.5): the program picture framed as 16:9, 1:1 and 9:16 side by side, so one edit can be judged for a
/// landscape, a square and a vertical delivery at once. Each tile is the frame's largest centred crop of that shape (<see cref="CenterCrop"/>),
/// box-filtered down to the tile's device pixels (<see cref="Downsample"/>, off the UI thread) and frozen. It floats in the player's HUD
/// (bottom right, not hit-testable, never zoomed with the picture) and is fed by the window with the composited frame the GPU compositor
/// reads back (GpuPreviewEngine.CaptureFrameAsync), only while it is shown and the player is paused: during playback it keeps the last
/// frame, dimmed, so the viewport never waits on a read-back (Law 3).
/// </summary>
public sealed class AspectSandboxOverlay : Border
{
    /// <summary>The framings shown, left to right: label and width / height.</summary>
    public static readonly (string Label, double Aspect)[] Framings = [("16:9", 16 / 9.0), ("1:1", 1.0), ("9:16", 9 / 16.0)];

    /// <summary>Tile height in DIPs (every tile is as tall; widths follow the framing).</summary>
    public const double TileHeight = 96;

    private static readonly SolidColorBrush Panel = UiBrushes.Frozen(0xD8, 0x0E, 0x10, 0x14), Edge = UiBrushes.Frozen(0x40, 0xFF, 0xFF, 0xFF),
        Ink = UiBrushes.Frozen(0xE6, 0xFF, 0xFF, 0xFF), LabelFill = UiBrushes.Frozen(0xB0, 0x00, 0x00, 0x00);

    private readonly Image[] _tiles = new Image[Framings.Length];

    // Newest frame request; an older one finishing later is dropped.
    private int _generation;

    public AspectSandboxOverlay()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(10);
        Padding = new Thickness(6);
        CornerRadius = new CornerRadius(6);
        Background = Panel;
        BorderBrush = Edge;
        BorderThickness = new Thickness(1);
        AutomationProperties.SetName(this, "Aspect Sandbox");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < Framings.Length; i++)
        {
            var (label, aspect) = Framings[i];
            _tiles[i] = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_tiles[i], BitmapScalingMode.HighQuality);
            var tile = new Grid
            {
                Width = Math.Round(TileHeight * aspect),
                Height = TileHeight,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                Background = Brushes.Black,
                ClipToBounds = true,
            };
            tile.Children.Add(_tiles[i]);
            tile.Children.Add(new Border
            {
                Background = LabelFill,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = label, FontSize = 10.5, Foreground = Ink },
            });
            AutomationProperties.SetName(tile, $"Aspect Sandbox {label}");
            row.Children.Add(tile);
        }

        Child = row;
    }

    /// <summary>True while playback runs: the tiles keep the last paused frame, dimmed.</summary>
    public bool IsStale
    {
        set => Opacity = value ? 0.55 : 1;
    }

    /// <summary>
    /// Shows a composited frame (top-down BGRA, <paramref name="width"/> x <paramref name="height"/>): crops and scales it to the tiles'
    /// device pixels on the thread pool, then swaps the tiles in at once. A newer call supersedes one still scaling.
    /// </summary>
    public async Task ShowFrameAsync(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            return;

        var generation = ++_generation;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var tileHeight = Math.Max(1, (int)Math.Round(TileHeight * dpi));
        var frames = await Task.Run(() => Framings.Select(f =>
        {
            var crop = CenterCrop(width, height, f.Aspect);
            return Downsample(bgra, width, height, crop, Math.Max(1, (int)Math.Round(tileHeight * f.Aspect)), tileHeight);
        }).ToArray());

        if (generation != _generation)
            return;
        for (var i = 0; i < _tiles.Length; i++)
            _tiles[i].Source = frames[i];
    }

    /// <summary>Drops the tiles' pictures (hidden: nothing kept).</summary>
    public void Clear()
    {
        _generation++;
        foreach (var tile in _tiles)
            tile.Source = null;
    }

    /// <summary>
    /// Pure: the largest rectangle of <paramref name="aspect"/> (width / height) centred in a <paramref name="width"/> x
    /// <paramref name="height"/> frame, in whole pixels (at least 1 x 1).
    /// </summary>
    public static Int32Rect CenterCrop(int width, int height, double aspect)
    {
        if (width <= 0 || height <= 0)
            return Int32Rect.Empty;
        if (!double.IsFinite(aspect) || aspect <= 0)
            aspect = (double)width / height;

        var (cropWidth, cropHeight) = (double)width / height > aspect
            ? ((int)Math.Round(height * aspect), height)
            : (width, (int)Math.Round(width / aspect));
        (cropWidth, cropHeight) = (Math.Clamp(cropWidth, 1, width), Math.Clamp(cropHeight, 1, height));
        return new Int32Rect((width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight);
    }

    /// <summary>
    /// Pure: <paramref name="crop"/> of a top-down BGRA frame scaled to <paramref name="outWidth"/> x <paramref name="outHeight"/> by a
    /// box filter (each output pixel the mean of the source pixels it covers; nearest when enlarging), as a frozen Bgr32 bitmap.
    /// </summary>
    public static BitmapSource Downsample(byte[] bgra, int width, int height, Int32Rect crop, int outWidth, int outHeight)
    {
        var pixels = new byte[outWidth * outHeight * 4];
        var (stepX, stepY) = ((double)crop.Width / outWidth, (double)crop.Height / outHeight);
        for (var y = 0; y < outHeight; y++)
        {
            var top = crop.Y + (int)(y * stepY);
            var bottom = Math.Min(Math.Max(crop.Y + (int)((y + 1) * stepY), top + 1), height);
            for (var x = 0; x < outWidth; x++)
            {
                var left = crop.X + (int)(x * stepX);
                var right = Math.Min(Math.Max(crop.X + (int)((x + 1) * stepX), left + 1), width);
                long b = 0, g = 0, r = 0;
                for (var sy = top; sy < bottom; sy++)
                {
                    var at = (sy * width + left) * 4;
                    for (var sx = left; sx < right; sx++, at += 4)
                        (b, g, r) = (b + bgra[at], g + bgra[at + 1], r + bgra[at + 2]);
                }

                var count = Math.Max((bottom - top) * (right - left), 1);
                var to = (y * outWidth + x) * 4;
                (pixels[to], pixels[to + 1], pixels[to + 2], pixels[to + 3]) = ((byte)(b / count), (byte)(g / count), (byte)(r / count), 255);
            }
        }

        var bitmap = BitmapSource.Create(outWidth, outHeight, 96, 96, PixelFormats.Bgr32, null, pixels, outWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
