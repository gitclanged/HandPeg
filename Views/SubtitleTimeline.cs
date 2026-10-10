using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Models;

namespace HandPegApp.Views;

/// <summary>
/// The Subtitle Editor's timeline: the cues as blocks along a strip of time that slides under a playhead
/// which stays in the middle. Everything is drawn in OnRender from the cues themselves; there is no element
/// per cue, so a film's worth of subtitles scrolls and zooms as readily as ten do.
///
/// Time is mapped to pixels by two transforms: a scale (pixels to the second, changed by the mouse wheel)
/// and a translation (changed by dragging the strip, which is the same as scrubbing: the playhead does not
/// move, the time under it does). A cue is dragged by its middle to move it and by an end to retime that end.
/// </summary>
public sealed class SubtitleTimeline : FrameworkElement
{
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
    }

    /// <summary>The cues that are drawn. Not copied: the timeline draws them as they are whenever it is asked to.</summary>
    public IList<SubtitleCue>? Cues { get; set; }

    /// <summary>Seconds every cue is shown later by: the track's offset.</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>How long the strip is.</summary>
    public double DurationSeconds { get; set; } = 60;

    /// <summary>The cues are pictures: drawn in another colour, with no words.</summary>
    public bool CuesAreImages { get; set; }

    public SubtitleCue? Selected { get; private set; }

    /// <summary>The word of the selected cue that was last clicked: what Delete takes out. -1 for none.</summary>
    public int ActiveWord { get; private set; } = -1;

    private static readonly Brush ActiveWordBrush = Frozen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
    private static readonly Pen WordPen = FrozenPen(Color.FromArgb(0xE0, 0xFF, 0xE0, 0x66), 1.5);

    /// <summary>The moment under the playhead.</summary>
    public double PositionSeconds
    {
        get => _position;
        set
        {
            _position = Math.Clamp(double.IsFinite(value) ? value : 0, 0, Math.Max(DurationSeconds, 0));
            InvalidateVisual();
        }
    }

    /// <summary>The strip was dragged: the moment now under the playhead.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>A cue is being dragged and has just changed.</summary>
    public event Action<SubtitleCue>? CueDragged;

    /// <summary>A drag of a cue ended.</summary>
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
        var (width, height) = (ActualWidth, ActualHeight);
        drawing.DrawRectangle(BackBrush, null, new Rect(0, 0, width, height));
        if (width <= 0)
            return;

        // The playhead stays in the middle: the strip is moved so that the moment it is at lies under it.
        _pan.X = width / 2 - _position * _zoom.ScaleX;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (firstSecond, lastSecond) = (-_pan.X / _zoom.ScaleX, (width - _pan.X) / _zoom.ScaleX);

        // Outside the video there is nothing to subtitle: that part of the strip is left darker.
        var (from, to) = (Math.Max(ToX(0), 0), Math.Min(ToX(DurationSeconds), width));
        if (to > from)
            drawing.DrawRectangle(Frozen(Color.FromRgb(0x22, 0x22, 0x22)), null, new Rect(from, RulerHeight, to - from, height - RulerHeight));

        // The ruler: a tick at every round step of time that leaves room for its label.
        var step = new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 }.FirstOrDefault(s => s * _zoom.ScaleX >= 72, 7200);
        for (var tick = Math.Max(Math.Floor(firstSecond / step) * step, 0); tick <= Math.Min(lastSecond, DurationSeconds); tick += step)
        {
            var x = Math.Round(ToX(tick)) + 0.5;
            drawing.DrawLine(TickPen, new Point(x, 0), new Point(x, height));
            var time = TimeSpan.FromSeconds(tick);
            var text = step < 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}")
                : time.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
            drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, RulerBrush, dpi), new Point(x + 3, 2));
        }

        var over = false;
        if (Cues is { } cues)
        {
            var (top, tall) = (CuesTop, CuesHeight);
            foreach (var cue in cues)
            {
                var (start, end) = (cue.Start + OffsetSeconds, cue.End + OffsetSeconds);
                over |= _position >= start && _position < end;

                // A cue that is not on screen costs a comparison and nothing more.
                if (end < firstSecond || start > lastSecond)
                    continue;

                var rect = new Rect(ToX(start), top, Math.Max((end - start) * _zoom.ScaleX, 2), tall);
                var selected = ReferenceEquals(cue, Selected);
                drawing.DrawRoundedRectangle(selected ? SelectedCueBrush : CuesAreImages ? ImageCueBrush : CueBrush, selected ? SelectedPen : null, rect, 3, 3);

                if (rect.Width > 12)
                {
                    // The ends are what is taken hold of to retime them.
                    drawing.DrawLine(EdgePen, new Point(rect.Left + 3, rect.Top + 5), new Point(rect.Left + 3, rect.Bottom - 5));
                    drawing.DrawLine(EdgePen, new Point(rect.Right - 3, rect.Top + 5), new Point(rect.Right - 3, rect.Bottom - 5));
                }

                if (selected && !CuesAreImages && cue.GetWords() is { Length: > 1 } parts)
                {
                    DrawWords(drawing, cue, parts, rect, dpi);
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

                    // The words keep to the part of the block that is on screen.
                    var left = Math.Max(rect.Left, 0) + 8;
                    label.Formatted.MaxTextWidth = Math.Max(rect.Right - left - 8, 4);
                    label.Formatted.MaxTextHeight = Math.Max(tall - 4, 12);
                    drawing.PushClip(new RectangleGeometry(rect));
                    drawing.DrawText(label.Formatted, new Point(left, rect.Top + Math.Max((tall - label.Formatted.Height) / 2, 2)));
                    drawing.Pop();
                }
            }
        }

        // The playhead: where it is never changes; its colour says whether a subtitle is showing there.
        var middle = Math.Round(width / 2) + 0.5;
        drawing.DrawLine(FrozenPen(over ? ((SolidColorBrush)OverCueBrush).Color : Colors.OrangeRed, 2), new Point(middle, 0), new Point(middle, height));
    }

    /// <summary>
    /// The selected cue's words, each in its own part of the block: a divider where one word ends and the next
    /// begins, which is what is dragged to retime them, and the word itself where there is room for it.
    /// </summary>
    private void DrawWords(DrawingContext drawing, SubtitleCue cue, string[] words, Rect rect, double dpi)
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
            if (to - from < 14 || to < 0 || from > ActualWidth)
                continue;

            var word = new FormattedText(words[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11.5, Brushes.White, dpi)
            {
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(to - from - 8, 4),
            };
            drawing.DrawText(word, new Point(from + Math.Max((to - from - word.Width) / 2, 4), rect.Top + Math.Max((rect.Height - word.Height) / 2, 2)));
        }

        drawing.Pop();
    }

    /// <summary>The cue under a point, and which part of it: an end, the middle, or (in the selected cue) the boundary between two words.</summary>
    private (SubtitleCue Cue, Grip Grip, int Word)? Hit(Point point)
    {
        if (Cues is not { } cues || point.Y < CuesTop || point.Y > CuesTop + CuesHeight)
            return null;

        // The boundaries inside the selected cue come first: they lie on top of it.
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

        // The selected cue first: where two overlap, it is the one that is being worked on.
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
        // About the playhead: the moment under it stays there, and the strip opens out or closes in around it.
        _zoom.ScaleX = Math.Clamp(_zoom.ScaleX * Math.Pow(1.2, e.Delta / 120.0), SmallestZoom, LargestZoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        var hit = Hit(point);
        if (hit is { } found)
            Press(found.Cue, found.Grip, point);

        if (CaptureMouse())
            _drag = new Drag(hit?.Grip ?? Grip.Pan, point.X, _position, hit?.Cue, hit?.Cue.Start ?? 0, hit?.Cue.End ?? 0, hit?.Word ?? -1);
        e.Handled = true;
    }

    // A press on a cue selects it; a press on a word of the cue that is selected makes that word the active one.
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

    /// <summary>Another word of the selected cue was clicked.</summary>
    public event Action? ActiveWordChanged;

    // A right-click chooses what the menu that opens is about, as a left one would.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (Hit(point) is { } found)
            Press(found.Cue, found.Grip == Grip.Word ? Grip.Move : found.Grip, point);
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
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
        if (Math.Abs(point.X - drag.PointerX) > 1.5)
            drag.Moved = true;
        if (!drag.Moved)
            return;

        if (drag.Cue is not { } cue || drag.Grip == Grip.Pan)
        {
            // Dragging the strip to the right brings earlier moments under the playhead.
            PositionSeconds = drag.Position - seconds;
            Scrubbed?.Invoke(_position);
            return;
        }

        switch (drag.Grip)
        {
            // A boundary between two words: it goes where the pointer is, and the cue's own ends stay where they are.
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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is not { } drag)
            return;

        _drag = null;
        ReleaseMouseCapture();
        if (drag is { Moved: true, Cue: { } cue } && drag.Grip != Grip.Pan)
            CueReleased?.Invoke(cue);
        else if (drag is { Moved: false, Cue: null })
            Select(null);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is { Moved: true, Cue: { } cue } drag && drag.Grip != Grip.Pan)
            CueReleased?.Invoke(cue);
        _drag = null;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
