using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Draws a live post-grade YRGB overlay histogram (Pass 5.6.4B) into a graph rect: translucent red, green and blue area fills (luma's own
/// fill only in Luma mode), crisp 1.5 px Y/R/G/B traces, an emphasized channel (the Curves' active Red/Green/Blue), an optional faint
/// pre-grade ghost (while a curve or wheel is dragged) and per-channel 0 % shadow-crush / 100 % highlight-clip edge indicators. Shared
/// by the Color Studio curves backdrop (ColorCurvesControl) and the Video Scopes Histogram (<see cref="HistogramView"/>). Geometry is
/// frozen and cached until the histogram, ghost, mode or size changes; brushes and pens are frozen statics.
/// </summary>
internal sealed class HistogramPainter
{
    /// <summary>Trace width (DIPs), the share of the graph height the tallest bin reaches, and the edge indicators' width.</summary>
    internal const double TraceWidth = 1.5, Headroom = 0.9, EdgeWidth = 3;

    // Channel hues in YrgbHistogram row order: red, green, blue, luma.
    private static readonly Color[] Hues = [Color.FromRgb(0xFF, 0x48, 0x48), Color.FromRgb(0x48, 0xE0, 0x68), Color.FromRgb(0x50, 0x8C, 0xFF), Color.FromRgb(0xE8, 0xE8, 0xE8)];
    private static readonly Brush[] Fills = [.. Hues.Select((c, i) => Brush(i == YrgbHistogram.Luma ? (byte)44 : (byte)60, c))];
    private static readonly Brush[] MutedFills = [.. Hues.Select(c => Brush(22, c))];
    private static readonly Brush[] EdgeBrushes = [.. Hues.Select(c => Brush(255, c))];
    private static readonly Pen[] Traces = [.. Hues.Select(c => Pen(Brush(230, c), TraceWidth))];
    private static readonly Pen[] MutedTraces = [.. Hues.Select(c => Pen(Brush(90, c), 1))];
    private static readonly Pen GhostPen = Pen(Brush(80, Colors.White), 1);

    // Additive R/G/B fills: per bin the three heights are sorted and each span is filled once with the sum of the hues that reach it
    // (bit 0 red, 1 green, 2 blue), so the result is order-independent and equal R, G and B read as neutral grey. Indexed by alpha
    // (normal, emphasized, muted) then channel mask; all frozen statics.
    internal static readonly Color[] SpanHues = [.. Enumerable.Range(0, 8).Select(SumOf)];
    private static readonly Brush[][] SpanFills = [.. new byte[] { 60, 120, 22 }.Select(alpha => SpanHues.Select(c => (Brush)Brush(alpha, c)).ToArray())];

    private YrgbHistogram? _histogram, _ghost;
    private HistogramMode _mode;
    private Size _size, _ghostSize;
    private (StreamGeometry Fill, StreamGeometry Trace)?[]? _shapes;
    private StreamGeometry?[]? _spans;
    private StreamGeometry?[]? _ghostShapes;

    /// <summary>The post-grade histogram drawn (null: nothing).</summary>
    public YrgbHistogram? Histogram
    {
        get => _histogram;
        set
        {
            if (ReferenceEquals(_histogram, value))
                return;
            (_histogram, _shapes) = (value, null);
        }
    }

    /// <summary>The pre-grade input histogram drawn as a faint ghost when asked (null: none).</summary>
    public YrgbHistogram? Ghost
    {
        get => _ghost;
        set
        {
            if (ReferenceEquals(_ghost, value))
                return;
            (_ghost, _ghostShapes) = (value, null);
        }
    }

    /// <summary>The channels shown (YRGB | RGB | Luma).</summary>
    public HistogramMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            (_mode, _shapes, _ghostShapes) = (value, null, null);
        }
    }

    /// <summary>The emphasized channel row (YrgbHistogram.Red/Green/Blue/Luma), the others muted; -1 for none.</summary>
    public int Emphasis { get; set; } = -1;

    /// <summary>Draws into <paramref name="graph"/>: fills, traces, the ghost (when <paramref name="ghost"/>) and the edge indicators.</summary>
    public void Draw(DrawingContext drawing, Rect graph, bool ghost = false)
    {
        if (_histogram is not { } histogram || graph.Width < 2 || graph.Height < 2)
            return;

        var channels = YrgbHistogram.ChannelsOf(_mode);
        if (_shapes is null || _size != graph.Size)
        {
            var peak = histogram.Peak(_mode);
            _shapes = new (StreamGeometry, StreamGeometry)?[4];
            foreach (var channel in channels)
                _shapes[channel] = Shapes(histogram, channel, peak, graph, fill: _mode == HistogramMode.Luma);
            _spans = _mode == HistogramMode.Luma ? null : Spans(histogram, peak, graph);
            _size = graph.Size;
        }

        // Fills (luma's own area, or the additive R/G/B spans), then every trace on top so overlaps stay readable.
        if (_mode == HistogramMode.Luma)
        {
            if (_shapes[YrgbHistogram.Luma] is { } luma)
                drawing.DrawGeometry(Emphasis < 0 || Emphasis == YrgbHistogram.Luma ? Fills[YrgbHistogram.Luma] : MutedFills[YrgbHistogram.Luma], null, luma.Fill);
        }
        else if (_spans is { } spans)
        {
            for (var mask = 1; mask < 8; mask++)
            {
                if (spans[mask] is not { } span)
                    continue;
                var tier = Emphasis < 0 ? 0 : Emphasis < 3 && (mask & (1 << Emphasis)) != 0 ? 1 : 2;
                drawing.DrawGeometry(SpanFills[tier][mask], null, span);
            }
        }

        foreach (var channel in channels)
        {
            if (_shapes[channel] is { } shape)
                drawing.DrawGeometry(null, Emphasis < 0 || Emphasis == channel ? Traces[channel] : MutedTraces[channel], shape.Trace);
        }

        if (ghost && _ghost is { } before)
        {
            if (_ghostShapes is null || _ghostSize != graph.Size)
            {
                var peak = before.Peak(_mode);
                _ghostShapes = new StreamGeometry?[4];
                foreach (var channel in channels)
                    _ghostShapes[channel] = Shapes(before, channel, peak, graph, fill: false).Trace;
                _ghostSize = graph.Size;
            }

            foreach (var channel in channels)
            {
                if (_ghostShapes[channel] is { } trace)
                    drawing.DrawGeometry(null, GhostPen, trace);
            }
        }

        // Edge indicators: one slot per shown channel down each side, lit where that channel crushes (left) or clips (right).
        var slot = Math.Min(graph.Height / 4, 14);
        for (var k = 0; k < channels.Count; k++)
        {
            var channel = channels[k];
            var top = graph.Top + 2 + k * slot;
            if (histogram.Crushed(channel))
                drawing.DrawRectangle(EdgeBrushes[channel], null, new Rect(graph.Left, top, EdgeWidth, Math.Max(slot - 2, 1)));
            if (histogram.Clipped(channel))
                drawing.DrawRectangle(EdgeBrushes[channel], null, new Rect(graph.Right - EdgeWidth, top, EdgeWidth, Math.Max(slot - 2, 1)));
        }
    }

    /// <summary>The additive sum of the R/G/B hues in <paramref name="mask"/> (bit 0 red, 1 green, 2 blue), each component capped at 255.</summary>
    private static Color SumOf(int mask)
    {
        var (r, g, b) = (0, 0, 0);
        for (var channel = 0; channel < 3; channel++)
        {
            if ((mask & (1 << channel)) != 0)
                (r, g, b) = (r + Hues[channel].R, g + Hues[channel].G, b + Hues[channel].B);
        }

        return Color.FromRgb((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255));
    }

    /// <summary>
    /// Per-bin spans of a level's R/G/B heights sorted tallest first: [0, lowest] filled by all three, up to the middle by the two tallest,
    /// up to the top by the tallest alone. Returns the levels' spans grouped by channel mask (pure; heights as 0..1 shares of the graph).
    /// </summary>
    internal static IReadOnlyList<(int Mask, double From, double To)> SpansOf(double red, double green, double blue)
    {
        Span<(double Height, int Bit)> sorted = [(red, 1), (green, 2), (blue, 4)];
        sorted.Sort((a, b) => b.Height.CompareTo(a.Height));
        var (top, middle, low) = (sorted[0], sorted[1], sorted[2]);
        var spans = new List<(int, double, double)>(3);
        if (low.Height > 0)
            spans.Add((7, 0, low.Height));
        if (middle.Height > low.Height)
            spans.Add((top.Bit | middle.Bit, Math.Max(low.Height, 0), middle.Height));
        if (top.Height > middle.Height)
            spans.Add((top.Bit, Math.Max(middle.Height, 0), top.Height));
        return spans;
    }

    // The additive spans as one frozen geometry per channel mask (null where none).
    private static StreamGeometry?[] Spans(YrgbHistogram histogram, int peak, Rect graph)
    {
        var spans = new StreamGeometry?[8];
        var contexts = new StreamGeometryContext?[8];
        var width = graph.Width / YrgbHistogram.Levels;
        var scale = graph.Height * Headroom;
        try
        {
            for (var level = 0; level < YrgbHistogram.Levels; level++)
            {
                var (left, right) = (graph.X + width * level, graph.X + width * (level + 1));
                foreach (var (mask, from, to) in SpansOf(histogram.Height(YrgbHistogram.Red, level, peak), histogram.Height(YrgbHistogram.Green, level, peak), histogram.Height(YrgbHistogram.Blue, level, peak)))
                {
                    var context = contexts[mask] ??= (spans[mask] = new StreamGeometry()).Open();
                    var (bottom, top) = (graph.Bottom - from * scale, graph.Bottom - to * scale);
                    context.BeginFigure(new Point(left, bottom), true, true);
                    context.LineTo(new Point(left, top), false, false);
                    context.LineTo(new Point(right, top), false, false);
                    context.LineTo(new Point(right, bottom), false, false);
                }
            }
        }
        finally
        {
            foreach (var context in contexts)
                context?.Close();
        }

        foreach (var span in spans)
            span?.Freeze();
        return spans;
    }

    /// <summary>The pen a channel's tonal pip and reference spline use (the channel's hue, opaque).</summary>
    internal static Brush HueBrush(int channel) => EdgeBrushes[Math.Clamp(channel, 0, 3)];

    // A channel's closed area (when filled) and its open trace across the graph, one point per level at the bin centre.
    private static (StreamGeometry Fill, StreamGeometry Trace) Shapes(YrgbHistogram histogram, int channel, int peak, Rect graph, bool fill)
    {
        Point At(int level) => new(graph.X + graph.Width * (level + 0.5) / YrgbHistogram.Levels, graph.Bottom - histogram.Height(channel, level, peak) * graph.Height * Headroom);

        var area = new StreamGeometry();
        if (fill)
        {
            using var context = area.Open();
            context.BeginFigure(graph.BottomLeft, true, true);
            for (var level = 0; level < YrgbHistogram.Levels; level++)
                context.LineTo(At(level), false, false);
            context.LineTo(graph.BottomRight, false, false);
        }

        area.Freeze();
        var trace = new StreamGeometry();
        using (var context = trace.Open())
        {
            context.BeginFigure(At(0), false, false);
            for (var level = 1; level < YrgbHistogram.Levels; level++)
                context.LineTo(At(level), true, true);
        }

        trace.Freeze();
        return (area, trace);
    }

    private static SolidColorBrush Brush(byte alpha, Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>
/// The Video Scopes Histogram's surface (Pass 5.6.4B): the inset ground with quarter graticules and the shared <see cref="HistogramPainter"/>
/// overlay, in place of the old bitmap histogram.
/// </summary>
internal sealed class HistogramView : FrameworkElement
{
    private static readonly Pen Graticule = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(48, 0x90, 0x90, 0x90)), 1));
    private static readonly Brush Ground = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)));

    public HistogramView()
    {
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "Histogram");
    }

    /// <summary>The overlay painter (histogram, mode).</summary>
    internal HistogramPainter Painter { get; } = new();

    /// <summary>Sets the histogram drawn and redraws.</summary>
    public void Show(YrgbHistogram? histogram)
    {
        Painter.Histogram = histogram;
        InvalidateVisual();
    }

    /// <summary>Sets the pre-grade ghost drawn while a Color Studio grading gesture runs (null: none) and redraws.</summary>
    public void ShowGhost(YrgbHistogram? ghost)
    {
        if (ReferenceEquals(Painter.Ghost, ghost))
            return;
        Painter.Ghost = ghost;
        InvalidateVisual();
    }

    /// <summary>The channels shown; redraws on change.</summary>
    public HistogramMode Mode
    {
        get => Painter.Mode;
        set
        {
            Painter.Mode = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var graph = new Rect(4, 4, Math.Max(ActualWidth - 8, 2), Math.Max(ActualHeight - 8, 2));
        drawing.DrawRectangle(Ground, null, new Rect(RenderSize));
        for (var i = 1; i < 4; i++)
        {
            drawing.DrawLine(Graticule, new Point(graph.X + graph.Width * i / 4, graph.Top), new Point(graph.X + graph.Width * i / 4, graph.Bottom));
            drawing.DrawLine(Graticule, new Point(graph.Left, graph.Y + graph.Height * i / 4), new Point(graph.Right, graph.Y + graph.Height * i / 4));
        }

        Painter.Draw(drawing, graph, ghost: Painter.Ghost is not null);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// The Color Studio's pre-grade input histogram and whether one of its grading gestures (wheel, Basics, curve, Match Strength) is live
/// (Pass 5.6.4B): the curves and the Video Scopes Histogram draw that ghost while one is. UI thread only; a subscriber unsubscribes when it
/// detaches (Law 2).
/// </summary>
internal static class GradeGhost
{
    /// <summary>The latest pre-grade histogram (null: none yet).</summary>
    public static YrgbHistogram? Pre { get; private set; }

    /// <summary>True while a grading gesture runs.</summary>
    public static bool Active { get; private set; }

    /// <summary>The ghost to draw now: the pre-grade histogram during a gesture, else null.</summary>
    public static YrgbHistogram? Shown => Active ? Pre : null;

    /// <summary>Raised when the pre-grade histogram or the gesture state changes.</summary>
    public static event Action? Changed;

    public static void Set(YrgbHistogram? pre, bool active)
    {
        if (ReferenceEquals(pre, Pre) && active == Active)
            return;
        (Pre, Active) = (pre, active);
        Changed?.Invoke();
    }
}
