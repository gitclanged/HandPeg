using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services.Audio;

namespace HandPegApp;

/// <summary>
/// The parametric equalizer's curve (Pass 5.2B): a sound's ten equalizer bands on a log-frequency grid (20 Hz to 20 kHz, -20 to +20 dB),
/// the response its biquads give (AudioChainBuilder.EqualizerResponseDb, the high-pass and the bass/treble shelves taken in while they are
/// on) sampled at 200 points, and a node for each band: dragged across, its frequency; up and down, its gain (-12 to +12 dB); the wheel
/// over it, its width in octaves (0.1 to 4; Shift: finely); double-clicked, the band as it was (its octave centre, one octave, 0 dB).
/// It edits the <see cref="Filters"/> it is given in place (the frequencies and widths made from the octave centres the first time a
/// band moves) and says so (<see cref="BandChanged"/>; a drag or a burst of notches is one gesture, <see cref="EditStarted"/> and
/// <see cref="EditEnded"/>). One OnRender: the grid and the curve are frozen geometry made again only when the size or the filters change.
/// </summary>
public sealed class ParametricEqControl : FrameworkElement
{
    private const int Bands = 10, CurvePoints = 200;
    private const double LeastHz = 20, MostHz = 20000, RangeDb = 20, GainLimit = 12, LeastWidth = 0.1, MostWidth = 4;
    private const double NodeRadius = 5, NodeReach = 10, LeftRoom = 26, BottomRoom = 14, Pad = 4;

    private static readonly TimeSpan WheelGesture = TimeSpan.FromMilliseconds(600);
    private static readonly double[] GridHz = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];
    private static readonly string[] GridHzWords = ["20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "20k"];
    private static readonly double[] GridDb = [20, 10, 0, -10, -20];
    private static readonly string[] GridDbWords = ["+20", "+10", "0", "-10", "-20"];

    private static readonly Brush CurveFill = Solid(40, 0x4F, 0xC3, 0xF7), NodeFill = Solid(255, 0x4F, 0xC3, 0xF7), NodeOff = Solid(150, 0x90, 0x90, 0x90);
    private static readonly Brush HeldFill = Solid(255, 0xFF, 0xC8, 0x40), WidthShade = Solid(36, 0xFF, 0xC8, 0x40);
    private static readonly Pen CurvePen = FrozenPen(Solid(255, 0x4F, 0xC3, 0xF7), 1.6), NodeEdge = FrozenPen(Solid(255, 0x20, 0x20, 0x20), 1);
    private static readonly Pen GridPen = FrozenPen(Solid(50, 0x90, 0x90, 0x90), 1), ZeroPen = FrozenPen(Solid(110, 0x90, 0x90, 0x90), 1);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private TrackAudioFilters? _filters;

    /// <summary>The filters whose equalizer it shows and edits in place; null shows a flat grid.</summary>
    public TrackAudioFilters? Filters
    {
        get => _filters;
        set
        {
            _filters = value;
            Refresh();
        }
    }

    /// <summary>A band was changed here (its index): its gain, frequency or width are the filters' now.</summary>
    public event Action<int>? BandChanged;

    /// <summary>A gesture began (a drag, a burst of wheel notches, a double-click's reset): what it changes until it ends is one step.</summary>
    public event Action? EditStarted;

    /// <summary>The gesture ended.</summary>
    public event Action? EditEnded;

    public ParametricEqControl()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        ToolTip = "The equalizer's curve: drag a band's node across for its frequency, up or down for its gain; the wheel over a node widens or narrows it "
                  + "(in octaves; Shift: finely); a double-click puts the band back (its octave, one octave wide, 0 dB). The curve takes in the high-pass and the "
                  + "bass and treble while they are on, as the sound engine hears them.";
        AutomationProperties.SetName(this, "Parametric Equalizer");
        _wheelRest.Tick += (_, _) => EndWheel();
        Loaded += (_, _) =>
        {
            Services.ThemeManager.Applied += ThemeApplied;
            ReadLook();
        };
        Unloaded += (_, _) =>
        {
            Services.ThemeManager.Applied -= ThemeApplied;
            EndWheel();
        };
    }

    /// <summary>The filters changed elsewhere (a slider moved, Reset All): the curve is made again.</summary>
    public void Refresh()
    {
        _curve = null;
        InvalidateVisual();
    }

    // ----- A band's values, read and written on the filters -----

    private double GainOf(int band) => _filters?.EqualizerGains is { } gains && band < gains.Length ? gains[band] : 0;

    private double FrequencyOf(int band) =>
        _filters?.EqualizerFrequencies is { Length: Bands } frequencies ? frequencies[band] : TrackAudioFilters.EqualizerBands[band];

    private double WidthOf(int band) => _filters?.EqualizerWidths is { Length: Bands } widths ? widths[band] : 1;

    private void SetBand(int band, double? gain = null, double? hz = null, double? width = null)
    {
        if (_filters is not { } filters)
            return;

        if (gain is { } g)
        {
            if (filters.EqualizerGains is not { Length: Bands })
                filters.EqualizerGains = [.. (filters.EqualizerGains ?? []).Concat(new double[Bands]).Take(Bands)];
            filters.EqualizerGains[band] = Math.Round(Math.Clamp(g, -GainLimit, GainLimit), 1);
        }

        if (hz is { } frequency)
        {
            if (filters.EqualizerFrequencies is not { Length: Bands })
                filters.EqualizerFrequencies = [.. TrackAudioFilters.EqualizerBands.Select(b => (double)b)];
            filters.EqualizerFrequencies[band] = Math.Round(Math.Clamp(frequency, LeastHz, MostHz), frequency < 1000 ? 1 : 0);
        }

        if (width is { } octaves)
        {
            if (filters.EqualizerWidths is not { Length: Bands })
                filters.EqualizerWidths = [.. Enumerable.Repeat(1.0, Bands)];
            filters.EqualizerWidths[band] = Math.Round(Math.Clamp(octaves, LeastWidth, MostWidth), 2);
        }

        Refresh();
        BandChanged?.Invoke(band);
    }

    // ----- Where things are -----

    private Rect Plot => new(LeftRoom, Pad, Math.Max(RenderSize.Width - LeftRoom - Pad, 1), Math.Max(RenderSize.Height - Pad - BottomRoom, 1));

    private static double LogOf(double hz) => Math.Log10(Math.Clamp(hz, LeastHz, MostHz));

    private static double XOf(Rect plot, double hz) => plot.Left + (LogOf(hz) - LogOf(LeastHz)) / (LogOf(MostHz) - LogOf(LeastHz)) * plot.Width;

    private static double HzAt(Rect plot, double x) => Math.Pow(10, LogOf(LeastHz) + Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * (LogOf(MostHz) - LogOf(LeastHz)));

    private static double YOf(Rect plot, double db) => plot.Top + (RangeDb - Math.Clamp(db, -RangeDb, RangeDb)) / (2 * RangeDb) * plot.Height;

    private static double DbAt(Rect plot, double y) => RangeDb - (y - plot.Top) / plot.Height * 2 * RangeDb;

    private Point NodeOf(Rect plot, int band) => new(XOf(plot, FrequencyOf(band)), YOf(plot, GainOf(band)));

    // The band whose node is nearest a point, within reach; -1 for none.
    private int BandAt(Point point, double reach = NodeReach)
    {
        var plot = Plot;
        var (best, nearest) = (-1, reach * reach);
        for (var band = 0; band < Bands; band++)
        {
            var node = NodeOf(plot, band);
            var distance = (node - point).LengthSquared;
            if (distance <= nearest)
                (best, nearest) = (band, distance);
        }

        return best;
    }

    // ----- The theme's look, and the words laid out once (per DPI and theme) -----

    private Brush _inset = Solid(255, 0x1E, 0x1E, 0x1E), _muted = Brushes.Gray, _text = Brushes.Gainsboro;
    private readonly FormattedText?[] _hzWords = new FormattedText?[GridHz.Length];
    private readonly FormattedText?[] _dbWords = new FormattedText?[GridDb.Length];
    private readonly FormattedText?[] _bandWords = new FormattedText?[Bands];
    private double _wordsDpi = -1;

    private void ThemeApplied()
    {
        ReadLook();
        InvalidateVisual();
    }

    private void ReadLook()
    {
        _inset = Look("InsetBrush", _inset);
        _muted = Look("MutedTextBrush", _muted);
        _text = Look("TextBrush", _text);
        _wordsDpi = -1;
    }

    private Brush Look(string key, Brush fallback) => TryFindResource(key) is Brush brush ? (Brush)brush.GetCurrentValueAsFrozen() : fallback;

    private void PrepareWords(double pixelsPerDip)
    {
        if (pixelsPerDip == _wordsDpi)
            return;

        _wordsDpi = pixelsPerDip;
        FormattedText Word(string text, Brush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, brush, pixelsPerDip);
        for (var i = 0; i < GridHz.Length; i++)
            _hzWords[i] = Word(GridHzWords[i], _muted);
        for (var i = 0; i < GridDb.Length; i++)
            _dbWords[i] = Word(GridDbWords[i], _muted);
        for (var i = 0; i < Bands; i++)
            _bandWords[i] = Word((i + 1).ToString(CultureInfo.InvariantCulture), _text);
    }

    // ----- Drawing -----

    // The grid's lines and the response curve, made for a size (and the curve for the filters as they were).
    private Geometry? _grid, _curve, _curveArea, _plotClip;
    private Size _gridSize, _curveSize;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        (_grid, _curve) = (null, null);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var size = RenderSize;
        if (size.Width < 40 || size.Height < 30)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        PrepareWords(dpi);
        var plot = Plot;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        drawing.DrawRectangle(_inset, null, plot);

        if (_grid is null || _gridSize != size)
        {
            (_grid, _gridSize) = (MakeGrid(plot), size);
            _plotClip = new RectangleGeometry(plot);
            _plotClip.Freeze();
        }

        drawing.DrawGeometry(null, GridPen, _grid);
        drawing.DrawLine(ZeroPen, new Point(plot.Left, YOf(plot, 0)), new Point(plot.Right, YOf(plot, 0)));

        // The scale's words: dB down the left, Hz along the bottom.
        for (var i = 0; i < GridDb.Length; i++)
        {
            if (_dbWords[i] is { } word)
                drawing.DrawText(word, new Point(plot.Left - word.Width - 3, YOf(plot, GridDb[i]) - word.Height / 2));
        }

        for (var i = 0; i < GridHz.Length; i++)
        {
            if (_hzWords[i] is { } word)
                drawing.DrawText(word, new Point(Math.Max(Math.Min(XOf(plot, GridHz[i]) - word.Width / 2, plot.Right - word.Width), plot.Left - 4), plot.Bottom + 1));
        }

        // The band held (or under the pointer): how wide it is, shaded.
        var held = _held >= 0 ? _held : _hover;
        if (held >= 0)
        {
            var (centre, half) = (FrequencyOf(held), Math.Pow(2, WidthOf(held) / 2));
            var (from, to) = (XOf(plot, centre / half), XOf(plot, centre * half));
            drawing.DrawRectangle(WidthShade, null, new Rect(from, plot.Top, Math.Max(to - from, 1), plot.Height));
        }

        if (_curve is null || _curveSize != size)
        {
            (_curve, _curveArea) = MakeCurve(plot);
            _curveSize = size;
        }

        drawing.PushClip(_plotClip!);
        if (_curveArea is not null)
            drawing.DrawGeometry(CurveFill, null, _curveArea);
        if (_curve is not null)
            drawing.DrawGeometry(null, CurvePen, _curve);
        drawing.Pop();

        // The nodes: dimmed while the equalizer is off, the one held in gold, each with its number.
        var on = _filters?.Equalizer == true;
        for (var band = 0; band < Bands; band++)
        {
            var node = NodeOf(plot, band);
            drawing.DrawEllipse(band == held ? HeldFill : on ? NodeFill : NodeOff, NodeEdge, node, NodeRadius, NodeRadius);
            if (_bandWords[band] is { } number)
                drawing.DrawText(number, new Point(node.X - number.Width / 2, node.Y - NodeRadius - number.Height));
        }
    }

    private static Geometry MakeGrid(Rect plot)
    {
        var grid = new StreamGeometry();
        using (var path = grid.Open())
        {
            foreach (var hz in GridHz)
            {
                var x = Math.Round(XOf(plot, hz)) + 0.5;
                path.BeginFigure(new Point(x, plot.Top), false, false);
                path.LineTo(new Point(x, plot.Bottom), true, false);
            }

            foreach (var db in GridDb)
            {
                var y = Math.Round(YOf(plot, db)) + 0.5;
                path.BeginFigure(new Point(plot.Left, y), false, false);
                path.LineTo(new Point(plot.Right, y), true, false);
            }
        }

        grid.Freeze();
        return grid;
    }

    // The response, 200 points from 20 Hz to 20 kHz (as the chain's biquads have it), as a line and as the area between it and 0 dB.
    private (Geometry? Line, Geometry? Area) MakeCurve(Rect plot)
    {
        if (_filters is not { } filters)
            return (null, null);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var zero = YOf(plot, 0);
        using (var lineDrawing = line.Open())
        using (var areaDrawing = area.Open())
        {
            for (var i = 0; i < CurvePoints; i++)
            {
                var hz = Math.Pow(10, LogOf(LeastHz) + i / (double)(CurvePoints - 1) * (LogOf(MostHz) - LogOf(LeastHz)));
                var db = AudioChainBuilder.EqualizerResponseDb(filters, hz);
                var point = new Point(XOf(plot, hz), YOf(plot, double.IsFinite(db) ? db : -RangeDb));
                if (i == 0)
                {
                    lineDrawing.BeginFigure(point, false, false);
                    areaDrawing.BeginFigure(new Point(point.X, zero), true, true);
                }
                else
                {
                    lineDrawing.LineTo(point, true, true);
                }

                areaDrawing.LineTo(point, false, true);
            }

            areaDrawing.LineTo(new Point(plot.Right, zero), false, true);
        }

        line.Freeze();
        area.Freeze();
        return (line, area);
    }

    // ----- The pointer -----

    // The band being dragged (-1 for none), the one under the pointer, and whether a burst of wheel notches is under way (and on which band).
    private int _held = -1, _hover = -1, _wheelBand = -1;
    private bool _wheeling;
    private readonly DispatcherTimer _wheelRest = new() { Interval = WheelGesture };

    // A drag's press: where it was, and the held node's centre less it (the node keeps that offset from the pointer, so a press off its centre
    // moves nothing); and whether the pointer has gone past the system's drag distance across (the frequency follows from then on) and up or
    // down (the gain does): an axis the pointer has not moved along keeps its value, so a drag straight up never detunes the band.
    private Point _press;
    private Vector _grab;
    private bool _movedAcross, _movedUpDown;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var press = e.GetPosition(this);
        if (_filters is null || BandAt(press) is not (>= 0 and var band))
            return;

        e.Handled = true;
        EndWheel();
        if (e.ClickCount == 2)
        {
            // The band as it was: its octave centre, one octave wide, flat.
            EditStarted?.Invoke();
            SetBand(band, gain: 0, hz: TrackAudioFilters.EqualizerBands[band], width: 1);
            EditEnded?.Invoke();
            return;
        }

        if (!CaptureMouse())
            return;

        _held = band;
        (_press, _grab, _movedAcross, _movedUpDown) = (press, NodeOf(Plot, band) - press, false, false);
        EditStarted?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_held >= 0 && IsMouseCaptured)
        {
            _movedAcross |= Math.Abs(point.X - _press.X) >= SystemParameters.MinimumHorizontalDragDistance;
            _movedUpDown |= Math.Abs(point.Y - _press.Y) >= SystemParameters.MinimumVerticalDragDistance;
            if (_movedAcross || _movedUpDown)
            {
                var (plot, node) = (Plot, point + _grab);
                SetBand(_held, gain: _movedUpDown ? DbAt(plot, node.Y) : null, hz: _movedAcross ? HzAt(plot, node.X) : null);
            }

            return;
        }

        var hover = BandAt(point);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.SizeAll : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0)
            return;

        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_held < 0)
            return;

        e.Handled = true;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_held < 0)
            return;

        _held = -1;
        EditEnded?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var band = _wheeling ? _wheelBand : BandAt(e.GetPosition(this), NodeReach * 1.5);
        if (_filters is null || band < 0 || _held >= 0 || e.Delta == 0)
            return;

        e.Handled = true;
        if (!_wheeling)
        {
            (_wheeling, _wheelBand) = (true, band);
            EditStarted?.Invoke();
        }

        // Up narrows it, down widens it.
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.02 : 0.1;
        SetBand(band, width: WidthOf(band) - Math.Sign(e.Delta) * step);
        _wheelRest.Stop();
        _wheelRest.Start();
    }

    private void EndWheel()
    {
        _wheelRest.Stop();
        if (!_wheeling)
            return;

        (_wheeling, _wheelBand) = (false, -1);
        EditEnded?.Invoke();
    }
}
