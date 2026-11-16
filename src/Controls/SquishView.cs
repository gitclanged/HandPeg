using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Squisher processing preview: a still video frame with animated macroblock artefacts while compressing, all drawn in OnRender.
/// Each block is a grid-aligned square filled with the colour at its centre (mimicking a bit-starved encoder); block placement
/// and size change several times per second.
/// </summary>
public sealed class SquishView : FrameworkElement
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly Random _random = new();
    private ImageSource? _thumbnail;
    private bool _crunching;
    private int _frame;

    private static readonly Pen BlockEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1));

    public SquishView()
    {
        ClipToBounds = true;
        _tick.Tick += (_, _) =>
        {
            _frame++;
            InvalidateVisual();
        };
        Unloaded += (_, _) => _tick.Stop();
    }

    /// <summary>Still frame; null draws an empty dark square.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            InvalidateVisual();
        }
    }

    /// <summary>True to animate macroblock artefacts; false shows the clean frame.</summary>
    public bool IsCrunching
    {
        get => _crunching;
        set
        {
            _crunching = value;
            if (value)
                _tick.Start();
            else
                _tick.Stop();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawRoundedRectangle(Brushes.Black, null, bounds, 6, 6);
        if (_thumbnail is not { Width: > 0, Height: > 0 } picture || bounds.Width < 8 || bounds.Height < 8)
            return;

        // Letterbox the frame centred in the square.
        var scale = Math.Min(bounds.Width / picture.Width, bounds.Height / picture.Height);
        var shown = new Rect((bounds.Width - picture.Width * scale) / 2, (bounds.Height - picture.Height * scale) / 2, picture.Width * scale, picture.Height * scale);
        drawing.DrawImage(picture, shown);
        if (!_crunching)
            return;

        // Grid-aligned blocks in three sizes (16/32/64), each filled with the frame colour at its centre.
        const double cell = 16;
        var count = 28 + _random.Next(36) + (_frame % 9 == 0 ? 40 : 0);
        for (var i = 0; i < count; i++)
        {
            var size = cell * (1 << _random.Next(3));
            var x = Math.Floor(_random.NextDouble() * shown.Width / cell) * cell;
            var y = Math.Floor(_random.NextDouble() * shown.Height / cell) * cell;
            var block = Rect.Intersect(new Rect(shown.Left + x, shown.Top + y, size, size), shown);
            if (block.IsEmpty || block.Width < 2 || block.Height < 2)
                continue;

            // ImageBrush sampling a tiny viewbox at the block centre, stretched across the block.
            var point = new Rect((block.Left + block.Width / 2 - shown.Left) / shown.Width, (block.Top + block.Height / 2 - shown.Top) / shown.Height, 0.004, 0.004);
            var flat = new ImageBrush(picture) { Viewbox = point, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
            drawing.DrawRectangle(flat, BlockEdge, block);
        }

        // One or two horizontally displaced bands, a few block rows deep: severe bit starvation.
        for (var band = 0; band < 1 + _frame % 2; band++)
        {
            var top = shown.Top + Math.Floor(_random.NextDouble() * shown.Height / cell) * cell;
            var strip = Rect.Intersect(new Rect(shown.Left, top, shown.Width, cell * (1 + _random.Next(2))), shown);
            if (strip.IsEmpty)
                continue;

            var from = new Rect(0, (strip.Top - shown.Top) / shown.Height, 1, 0.01);
            drawing.DrawRectangle(new ImageBrush(picture) { Viewbox = from, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill }, null, strip);
        }
    }

}
