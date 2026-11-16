using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Models;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Views;

/// <summary>
/// Subtitle Studio timeline: cues as blocks on a time strip sliding under a fixed centre playhead. All drawing is in OnRender from the
/// cue list (no element per cue), so feature-length tracks scroll and zoom as cheaply as short ones.
///
/// Time maps to pixels via a scale (px/s, Ctrl+wheel or pinch) and a translation (strip drag or wheel, i.e. scrubbing: the playhead is
/// fixed and the time under it moves). Drag a cue's middle to move it, an end to retime that end.
///
/// Strip flicks coast via <see cref="MomentumScroller"/>, each step raised as a scrub. Slow drags, a 40 ms rest before release and cue
/// drags never coast; any press, zoom, hiding, or an external position change (playback, typed time) stops it. The frame hook exists
/// only while coasting.
///
/// Wheel scrolls in time like the Timeline's Shift+wheel: plain/Shift wheel (down = later) and horizontal swipe/tilt
/// (<see cref="ISidewaysWheel"/>, right = later), fast streams coast. Ctrl+wheel (precision-touchpad pinch arrives as this) zooms about
/// the centre playhead, so a pinch never seeks.
/// </summary>
public sealed class SubtitleTimeline : FrameworkElement, ISidewaysWheel
{
    /// <summary>Strip DIPs per wheel delta unit (120 per notch), matching the Timeline's Shift+wheel (PanWheelAt).</summary>
    internal const double WheelDipsPerDelta = 1.0;

    /// <summary>Zoom multiplier per Ctrl+wheel notch.</summary>
    private const double ZoomStep = 1.2;

    private const double SmallestZoom = 4, LargestZoom = 1200, EdgePixels = 7, RulerHeight = 18, ShortestCue = 0.1;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush CueBrush = Frozen(Color.FromRgb(0x2E, 0x7D, 0x6B));
    private static readonly Brush SelectedCueBrush = Frozen(Color.FromRgb(0x3F, 0xB5, 0x97));
    private static readonly Brush ImageCueBrush = Frozen(Color.FromRgb(0x6A, 0x5A, 0x9E));
    private static readonly Brush RulerBrush = Frozen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BackBrush = Frozen(Color.FromRgb(0x18, 0x18, 0x18));
    private static readonly Brush OverCueBrush = Frozen(Color.FromRgb(0x4C, 0xE0, 0x8A));
    private static readonly Pen TickPen = FrozenPen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen EdgePen = FrozenPen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 1.5);
    private static readonly Pen SelectedPen = FrozenPen(Colors.White, 1.5);

    // Time to pixels: x = seconds * zoom + pan.
    private readonly ScaleTransform _zoom = new(90, 1);
    private readonly TranslateTransform _pan = new();

    private readonly ConditionalWeakTable<SubtitleCue, Label> _labels = [];
    private double _position;
    private Drag? _drag;

    // Momentum scroller (created on first press), frame-hook flag, and re-entrancy flag while the coast sets the position.
    private MomentumScroller? _momentum;
    private bool _coastHooked, _coastSetting;

    // The strip (ruler, cues, words) renders into its own DrawingVisual three screens wide around the playhead and slides via a transform;
    // playback redraws only when the view passes the drawn range. Content changes redraw via InvalidateVisual (OnRender). The fixed
    // playhead is a separate visual redrawn only on colour change.
    private readonly DrawingVisual _strip = new(), _head = new();
    private readonly TranslateTransform _stripShift = new();
    private (double From, double To) _drawn = (double.NaN, double.NaN);
    private bool? _headOver;
    private double _headWidth;

    // FormattedText cache for ruler labels (a few dozen strings, reused as the strip slides).
    private readonly Dictionary<string, FormattedText> _rulerLabels = [];
    private double _labelsDpi;

    private static readonly Brush VideoBrush = Frozen(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly Pen HeadPen = FrozenPen(Colors.OrangeRed, 2), HeadOverPen = FrozenPen(((SolidColorBrush)OverCueBrush).Color, 2);
    private static readonly double[] RulerSteps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

    private sealed record Label(string Text, FormattedText Formatted);

    private enum Grip { Pan, Move, Start, End, Word }

    private sealed record Drag(Grip Grip, double PointerX, double Position, SubtitleCue? Cue, double Start, double End, int Word = -1)
    {
        public bool Moved { get; set; }
    }

    public SubtitleTimeline()
    {
        ClipToBounds = true;
        Focusable = false;
        _strip.Transform = _stripShift;
        AddVisualChild(_strip);
        AddVisualChild(_head);
        Unloaded += (_, _) => StopCoast();
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
                StopCoast();
        };
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _strip : _head;

    /// <summary>Cue list, drawn live (not copied).</summary>
    public IList<SubtitleCue>? Cues { get; set; }

    /// <summary>Track offset in seconds added to every cue.</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>Strip length in seconds.</summary>
    public double DurationSeconds { get; set; } = 60;

    /// <summary>Bitmap cues: drawn in a distinct colour without text.</summary>
    public bool CuesAreImages { get; set; }

    public SubtitleCue? Selected { get; private set; }

    /// <summary>Last clicked word index in the selected cue (Delete target); -1 for none.</summary>
    public int ActiveWord { get; private set; } = -1;

    private static readonly Brush ActiveWordBrush = Frozen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
    private static readonly Pen WordPen = FrozenPen(Color.FromArgb(0xE0, 0xFF, 0xE0, 0x66), 1.5);

    /// <summary>Time under the playhead. An external change stops a coast; setting the same value (a host echoing a scrub) does
    /// not.</summary>
    public double PositionSeconds
    {
        get => _position;
        set
        {
            var position = Math.Clamp(double.IsFinite(value) ? value : 0, 0, Math.Max(DurationSeconds, 0));
            if (!_coastSetting && position != _position)
                StopCoast();
            _position = position;
            Slide();
        }
    }

    /// <summary>True while coasting after a flick; a host following the player may skip position updates meanwhile.</summary>
    public bool IsCoasting => _momentum?.IsCoasting == true;

    /// <summary>Raised on strip scrub with the new time under the playhead.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>Raised once when a cue drag first moves, before the cue changes (so the host can snapshot it for Undo).</summary>
    public event Action<SubtitleCue>? CueGrabbed;

    /// <summary>Raised after each cue change during a drag.</summary>
    public event Action<SubtitleCue>? CueDragged;

    /// <summary>Raised when a cue drag ends.</summary>
    public event Action<SubtitleCue>? CueReleased;

    public event Action<SubtitleCue?>? SelectionChanged;

    public void Select(SubtitleCue? cue)
    {
        if (ReferenceEquals(Selected, cue))
            return;

        (Selected, ActiveWord) = (cue, -1);
        InvalidateVisual();
        SelectionChanged?.Invoke(cue);
    }

    private double ToX(double seconds) => seconds * _zoom.ScaleX + _pan.X;

    private double CuesTop => RulerHeight + 4;

    private double CuesHeight => Math.Max(ActualHeight - CuesTop - 4, 8);

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(BackBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // Content changed (cue, selection, zoom, size): invalidate the drawn range and redraw.
        _drawn = (double.NaN, double.NaN);
        Slide();
    }

    /// <summary>
    /// Centres the current time under the playhead by translating the strip, redrawing only when the visible range exceeds the drawn
    /// range. The playhead colour reflects whether a cue is active there.
    /// </summary>
    private void Slide()
    {
        var width = ActualWidth;
        if (width <= 0)
            return;

        // Fixed centre playhead: pan so the current time sits under it.
        _pan.X = width / 2 - _position * _zoom.ScaleX;
        var (firstSecond, lastSecond) = (-_pan.X / _zoom.ScaleX, (width - _pan.X) / _zoom.ScaleX);
        if (double.IsNaN(_drawn.From) || firstSecond < _drawn.From || lastSecond > _drawn.To)
            DrawStrip(width, firstSecond, lastSecond);
        _stripShift.X = _pan.X + _drawn.From * _zoom.ScaleX;

        var over = false;
        if (Cues is { } cues)
        {
            foreach (var cue in cues)
            {
                if (_position >= cue.Start + OffsetSeconds && _position < cue.End + OffsetSeconds)
                {
                    over = true;
                    break;
                }
            }
        }

        DrawHead(width, over);
    }

    /// <summary>Draws the fixed playhead; redrawn only when its colour or the width changes.</summary>
    private void DrawHead(double width, bool over)
    {
        if (_headOver == over && _headWidth == width)
            return;

        (_headOver, _headWidth) = (over, width);
        using var drawing = _head.RenderOpen();
        var middle = Math.Round(width / 2) + 0.5;
        drawing.DrawLine(over ? HeadOverPen : HeadPen, new Point(middle, 0), new Point(middle, ActualHeight));
    }

    /// <summary>
    /// Renders the strip from one screen before to one screen after the visible range, with local origin at its start
    /// (x = (seconds - from) * zoom); <see cref="Slide"/> translates it into place.
    /// </summary>
    private void DrawStrip(double width, double firstSecond, double lastSecond)
    {
        var span = lastSecond - firstSecond;
        var (from, to) = (firstSecond - span, lastSecond + span);
        _drawn = (from, to);

        var zoom = _zoom.ScaleX;
        var height = ActualHeight;
        double X(double seconds) => (seconds - from) * zoom;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _labelsDpi || _rulerLabels.Count > 512)
        {
            _labelsDpi = dpi;
            _rulerLabels.Clear();
        }

        using var drawing = _strip.RenderOpen();

        // Lighter band over the video's duration; outside it stays dark.
        var (left, right) = (Math.Max(X(0), 0), Math.Min(X(DurationSeconds), X(to)));
        if (right > left)
            drawing.DrawRectangle(VideoBrush, null, new Rect(left, RulerHeight, right - left, height - RulerHeight));

        // Ruler ticks at the smallest round step leaving >= 72 px per label.
        var step = RulerSteps.FirstOrDefault(s => s * zoom >= 72, 7200);
        for (var tick = Math.Max(Math.Floor(from / step) * step, 0); tick <= Math.Min(to, DurationSeconds); tick += step)
        {
            var x = Math.Round(X(tick)) + 0.5;
            drawing.DrawLine(TickPen, new Point(x, 0), new Point(x, height));
            var time = TimeSpan.FromSeconds(tick);
            var text = step < 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}")
                : time.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
            if (!_rulerLabels.TryGetValue(text, out var label))
                _rulerLabels[text] = label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, RulerBrush, dpi);
            drawing.DrawText(label, new Point(x + 3, 2));
        }

        if (Cues is not { } cues)
            return;

        var (top, tall) = (CuesTop, CuesHeight);
        foreach (var cue in cues)
        {
            var (start, end) = (cue.Start + OffsetSeconds, cue.End + OffsetSeconds);

            // Off-strip cues are culled with one comparison.
            if (end < from || start > to)
                continue;

            var rect = new Rect(X(start), top, Math.Max((end - start) * zoom, 2), tall);
            var selected = ReferenceEquals(cue, Selected);
            drawing.DrawRoundedRectangle(selected ? SelectedCueBrush : CuesAreImages ? ImageCueBrush : CueBrush, selected ? SelectedPen : null, rect, 3, 3);

            if (rect.Width > 12)
            {
                // Edge grips for retiming.
                drawing.DrawLine(EdgePen, new Point(rect.Left + 3, rect.Top + 5), new Point(rect.Left + 3, rect.Bottom - 5));
                drawing.DrawLine(EdgePen, new Point(rect.Right - 3, rect.Top + 5), new Point(rect.Right - 3, rect.Bottom - 5));
            }

            if (selected && !CuesAreImages && cue.GetWords() is { Length: > 1 } parts)
            {
                DrawWords(drawing, cue, parts, rect, X(to), dpi);
            }
            else if (rect.Width > 30)
            {
                var words = CuesAreImages ? $"#{cue.Index}" : cue.Text.ReplaceLineEndings(" ");
                if (!_labels.TryGetValue(cue, out var label) || label.Text != words)
                {
                    label = new Label(words, new FormattedText(words, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11.5, Brushes.White, dpi)
                    {
                        MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis,
                    });
                    _labels.AddOrUpdate(cue, label);
                }

                // Constrain text to the on-strip part of the block; set limits only on change, since setting re-lays out the text.
                var textLeft = Math.Max(rect.Left, 0) + 8;
                var (maxWidth, maxHeight) = (Math.Max(rect.Right - textLeft - 8, 4), Math.Max(tall - 4, 12));
                if (label.Formatted.MaxTextWidth != maxWidth)
                    label.Formatted.MaxTextWidth = maxWidth;
                if (label.Formatted.MaxTextHeight != maxHeight)
                    label.Formatted.MaxTextHeight = maxHeight;
                drawing.PushClip(new RectangleGeometry(rect));
                drawing.DrawText(label.Formatted, new Point(textLeft, rect.Top + Math.Max((tall - label.Formatted.Height) / 2, 2)));
                drawing.Pop();
            }
        }
    }

    /// <summary>
    /// Draws the selected cue's words in their time slices: draggable dividers at word boundaries, and each word where it fits.
    /// </summary>
    private void DrawWords(DrawingContext drawing, SubtitleCue cue, string[] words, Rect rect, double stripRight, double dpi)
    {
        var breaks = cue.GetBreaks();
        drawing.PushClip(new RectangleGeometry(rect));
        for (var i = 0; i < words.Length; i++)
        {
            var (from, to) = (rect.Left + rect.Width * (i == 0 ? 0 : breaks[i - 1]), rect.Left + rect.Width * (i < breaks.Length ? breaks[i] : 1));
            if (i == ActiveWord)
                drawing.DrawRectangle(ActiveWordBrush, null, new Rect(from, rect.Top + 1, Math.Max(to - from, 1), rect.Height - 2));
            if (i > 0)
                drawing.DrawLine(WordPen, new Point(Math.Round(from) + 0.5, rect.Top + 2), new Point(Math.Round(from) + 0.5, rect.Bottom - 2));
            if (to - from < 14 || to < 0 || from > stripRight)
                continue;

            var word = new FormattedText(words[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11.5, Brushes.White, dpi)
            {
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(to - from - 8, 4),
            };
            drawing.DrawText(word, new Point(from + Math.Max((to - from - word.Width) / 2, 4), rect.Top + Math.Max((rect.Height - word.Height) / 2, 2)));
        }

        drawing.Pop();
    }

    /// <summary>Cue hit-test returning the grip: an end, the middle, or (selected cue only) a word boundary.</summary>
    private (SubtitleCue Cue, Grip Grip, int Word)? Hit(Point point)
    {
        if (Cues is not { } cues || point.Y < CuesTop || point.Y > CuesTop + CuesHeight)
            return null;

        // Word boundaries of the selected cue first (drawn on top).
        if (Selected is { } chosen && !CuesAreImages && cues.Contains(chosen))
        {
            var (start, width) = (ToX(chosen.Start + OffsetSeconds), chosen.Duration * _zoom.ScaleX);
            var breaks = chosen.GetBreaks();
            for (var i = 0; i < breaks.Length; i++)
            {
                if (Math.Abs(point.X - (start + width * breaks[i])) <= 4)
                    return (chosen, Grip.Word, i);
            }
        }

        // Selected cue first so it wins where cues overlap.
        foreach (var cue in Selected is { } selected && cues.Contains(selected) ? cues.Prepend(selected) : cues)
        {
            var (left, right) = (ToX(cue.Start + OffsetSeconds), ToX(cue.End + OffsetSeconds));
            if (point.X < left - 2 || point.X > Math.Max(right, left + 2) + 2)
                continue;

            var edge = Math.Min(EdgePixels, (right - left) / 3);
            return (cue, point.X <= left + edge ? Grip.Start : point.X >= right - edge ? Grip.End : Grip.Move, -1);
        }

        return null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        WheelAt(e.Delta, Keyboard.Modifiers, MomentumScroller.Now(), e.GetPosition(this).X);
    }

    /// <summary>
    /// Test seam (called by OnMouseWheel): Ctrl zooms about the playhead and stops coasting (precision-touchpad pinch arrives as
    /// Ctrl+wheel; <paramref name="pointerX"/> is unused); otherwise (none or Shift) scrolls in time with momentum, wheel down = later.
    /// </summary>
    internal void WheelAt(int delta, ModifierKeys modifiers, double nowMs, double pointerX)
    {
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            StopCoast();
            ZoomAt(Math.Pow(ZoomStep, delta / 120.0));
            return;
        }

        // Wheel up (positive) shows earlier time: the strip slides right, as a drag would, and any coast continues that way.
        PanWheel(delta * WheelDipsPerDelta, nowMs);
    }

    /// <summary>Horizontal swipe/tilt (WM_MOUSEHWHEEL via the window hook); positive = right = later.</summary>
    public void SidewaysWheel(double delta, double nowMs) => PanWheel(-delta * WheelDipsPerDelta, nowMs);

    // Wheel pan in drag-sense DIPs (right = earlier): applies the momentum step and raises Scrubbed; fast streams coast (StepCoast
    // subtracts the same way, preserving direction).
    private void PanWheel(double dips, double nowMs)
    {
        if (_drag is not null || !(_zoom.ScaleX > 0))
            return;

        _momentum ??= new();
        var step = _momentum.Wheel(dips, nowMs);
        if (step != 0)
        {
            var before = _position;
            _coastSetting = true;
            try
            {
                PositionSeconds = _position - step / _zoom.ScaleX;
            }
            finally
            {
                _coastSetting = false;
            }

            if (_position == before)
                _momentum.Stop();
            else
                Scrubbed?.Invoke(_position);
        }

        if (_momentum.IsCoasting)
            HookCoast();
    }

    // Zooms about the centre playhead so a pinch never seeks (the Media Browser cutter zooms about the pointer instead). Ignored during
    // drags, where a dragged cue would detach from the pointer.
    private void ZoomAt(double factor)
    {
        if (_drag is not null)
            return;
        var zoom = Math.Max(SmallestZoom, Math.Min(_zoom.ScaleX * factor, LargestZoom));
        if (zoom == _zoom.ScaleX)
            return;
        _zoom.ScaleX = zoom;
        InvalidateVisual();
    }

    /// <summary>Test seam: pixels per second.</summary>
    internal double PixelsPerSecond => _zoom.ScaleX;

    /// <summary>Test seam: strip x to cue-clock seconds.</summary>
    internal double SecondsAt(double x) => (x - _pan.X) / _zoom.ScaleX;

    // Any press (any button) stops a coast before other handling.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        StopCoast();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Begin(e.GetPosition(this), MomentumScroller.Now(), CaptureMouse());
        e.Handled = true;
    }

    /// <summary>Test seam: left press at <paramref name="point"/> at <paramref name="nowMs"/>, treated as captured.</summary>
    internal void PressAt(Point point, double nowMs) => Begin(point, nowMs, captured: true);

    /// <summary>Test seam: pointer move to <paramref name="point"/> at <paramref name="nowMs"/> during a press.</summary>
    internal void MoveTo(Point point, double nowMs) => Follow(point, nowMs);

    /// <summary>Test seam: left release at <paramref name="nowMs"/>; a strip flick starts a coast.</summary>
    internal void ReleaseAt(double nowMs) => End(nowMs);

    // Press: on a cue, select it and start its drag; elsewhere start a scrub pan. Stops any coast and starts velocity sampling for pans.
    private void Begin(Point point, double nowMs, bool captured)
    {
        StopCoast();
        var hit = Hit(point);
        if (hit is { } found)
            Press(found.Cue, found.Grip, point);

        if (!captured)
            return;

        _drag = new Drag(hit?.Grip ?? Grip.Pan, point.X, _position, hit?.Cue, hit?.Cue.Start ?? 0, hit?.Cue.End ?? 0, hit?.Word ?? -1);
        _momentum ??= new MomentumScroller();
        _momentum.Press(nowMs);
        if (_drag.Grip == Grip.Pan)
            _momentum.Sample(point.X, nowMs);
    }

    // Selects the cue; a press on a word of the already-selected cue makes it the active word.
    private void Press(SubtitleCue cue, Grip grip, Point point)
    {
        var again = ReferenceEquals(cue, Selected);
        Select(cue);
        if (grip != Grip.Move || CuesAreImages || cue.Duration <= 0)
            return;

        var word = cue.WordAt((point.X - ToX(cue.Start + OffsetSeconds)) / (cue.Duration * _zoom.ScaleX));
        ActiveWord = again || ActiveWord < 0 ? word : -1;
        InvalidateVisual();
        ActiveWordChanged?.Invoke();
    }

    /// <summary>Raised when the active word of the selected cue changes.</summary>
    public event Action? ActiveWordChanged;

    // Right-click selects the context-menu target like a left press.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (Hit(point) is { } found)
            Press(found.Cue, found.Grip == Grip.Word ? Grip.Move : found.Grip, point);
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) => Follow(e.GetPosition(this), MomentumScroller.Now());

    private void Follow(Point point, double nowMs)
    {
        if (_drag is not { } drag)
        {
            Cursor = Hit(point)?.Grip switch
            {
                Grip.Start or Grip.End or Grip.Word => Cursors.SizeWE,
                Grip.Move => Cursors.SizeAll,
                _ => Cursors.Hand,
            };
            return;
        }

        var seconds = (point.X - drag.PointerX) / _zoom.ScaleX;
        if (!drag.Moved && Math.Abs(point.X - drag.PointerX) > 1.5)
        {
            drag.Moved = true;
            if (drag is { Cue: { } grabbed, Grip: not Grip.Pan })
                CueGrabbed?.Invoke(grabbed);
        }

        if (!drag.Moved)
            return;

        if (drag.Cue is not { } cue || drag.Grip == Grip.Pan)
        {
            // Dragging right moves earlier time under the playhead.
            _momentum?.Sample(point.X, nowMs);
            PositionSeconds = drag.Position - seconds;
            Scrubbed?.Invoke(_position);
            return;
        }

        switch (drag.Grip)
        {
            // Word boundary follows the pointer; the cue's ends stay fixed.
            case Grip.Word:
                if (cue.Duration > 0)
                    cue.SetBreak(drag.Word, (point.X - ToX(cue.Start + OffsetSeconds)) / (cue.Duration * _zoom.ScaleX));
                break;
            case Grip.Start:
                cue.Start = Math.Round(Math.Clamp(drag.Start + seconds, -OffsetSeconds, cue.End - ShortestCue), 3);
                break;
            case Grip.End:
                cue.End = Math.Round(Math.Max(drag.End + seconds, cue.Start + ShortestCue), 3);
                break;
            default:
                var start = Math.Round(Math.Max(drag.Start + seconds, -OffsetSeconds), 3);
                (cue.Start, cue.End) = (start, Math.Round(start + drag.End - drag.Start, 3));
                break;
        }

        InvalidateVisual();
        CueDragged?.Invoke(cue);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => End(MomentumScroller.Now());

    // Release: ends a cue drag; a click on empty strip clears the selection; a strip flick (>= 250 DIP/s, moving within the last 40 ms)
    // coasts. Cue drags never coast.
    private void End(double nowMs)
    {
        if (_drag is not { } drag)
            return;

        _drag = null;
        ReleaseMouseCapture();
        if (drag is { Moved: true, Cue: { } cue } && drag.Grip != Grip.Pan)
            CueReleased?.Invoke(cue);
        else if (drag is { Moved: false, Cue: null })
            Select(null);

        var flick = drag is { Moved: true } && (drag.Cue is null || drag.Grip == Grip.Pan) ? _momentum?.Release(nowMs) ?? 0 : 0;
        if (flick != 0)
            HookCoast();
        else
            _momentum?.Stop();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is { Moved: true, Cue: { } cue } drag && drag.Grip != Grip.Pan)
            CueReleased?.Invoke(cue);

        // Capture lost mid-drag (End releases capture only after clearing _drag): no coast.
        if (_drag is not null)
            StopCoast();
        _drag = null;
    }

    /// <summary>
    /// Test seam (called per frame): advances the coast at <paramref name="nowMs"/> (rightward = earlier) and raises Scrubbed; hitting
    /// either strip end stops it. Returns whether the position moved.
    /// </summary>
    internal bool StepCoast(double nowMs)
    {
        var moved = false;
        if (_momentum is { IsCoasting: true } momentum)
        {
            var pixels = momentum.Tick(nowMs);
            if (pixels != 0 && _zoom.ScaleX > 0)
            {
                var before = _position;
                _coastSetting = true;
                try
                {
                    PositionSeconds = _position - pixels / _zoom.ScaleX;
                }
                finally
                {
                    _coastSetting = false;
                }

                if (_position == before)
                {
                    momentum.Stop();
                }
                else
                {
                    moved = true;
                    Scrubbed?.Invoke(_position);
                }
            }
        }

        if (_momentum?.IsCoasting != true)
            UnhookCoast();
        return moved;
    }

    private void StopCoast()
    {
        _momentum?.Stop();
        UnhookCoast();
    }

    // CompositionTarget.Rendering is hooked only while coasting and visible; tests call StepCoast directly.
    private void HookCoast()
    {
        if (_coastHooked || !IsVisible)
            return;

        _coastHooked = true;
        CompositionTarget.Rendering += OnCoastFrame;
    }

    private void UnhookCoast()
    {
        if (!_coastHooked)
            return;

        _coastHooked = false;
        CompositionTarget.Rendering -= OnCoastFrame;
    }

    private void OnCoastFrame(object? sender, EventArgs e) => StepCoast(MomentumScroller.Now());

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
