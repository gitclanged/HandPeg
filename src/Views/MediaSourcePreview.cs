using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HandPegApp.Views;

/// <summary>
/// The Media Browser's preview of the file chosen (5.4C): its frame at the cutter's moment, letterboxed in the box, with the moment under
/// it and the sub-clip marked (In, Out and its length), or what keeps a frame from being shown (no file chosen, a sound, a file gone or
/// unreadable). It owns no decoder: the pane gives it frames (SourceFrames, the strip's while a still is on its way). While the pane steps
/// through the file (Space) it says so: stills at a few a second, no sound.
/// </summary>
public sealed class MediaSourcePreview : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush BackBrush = Frozen(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush TextBrush = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MutedBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0xB0, 0x00, 0x00, 0x00));
    private static readonly Brush RangeBrush = Frozen(Color.FromRgb(0xFF, 0xC8, 0x3C));

    private ImageSource? _frame;
    private string _message = "Choose a file in the list to see it here.";
    private double _position, _duration, _in, _out;
    private bool _hasRange, _stepping;

    public MediaSourcePreview()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>The frame shown (frozen); null for none (the message is shown instead).</summary>
    public ImageSource? Frame
    {
        get => _frame;
        set
        {
            if (ReferenceEquals(_frame, value))
                return;
            _frame = value;
            InvalidateVisual();
        }
    }

    /// <summary>What is said while there is no frame: no file chosen, a sound, a file that is gone or cannot be read.</summary>
    public string Message
    {
        get => _message;
        set
        {
            if (_message == value)
                return;
            _message = value;
            InvalidateVisual();
        }
    }

    /// <summary>The moment shown and the file's length, seconds (the line under the picture).</summary>
    public void ShowMoment(double position, double duration)
    {
        if (position == _position && duration == _duration)
            return;
        (_position, _duration) = (position, duration);
        InvalidateVisual();
    }

    /// <summary>The sub-clip marked (In and Out, seconds); <paramref name="marked"/> false when the whole file is.</summary>
    public void ShowRange(double inSeconds, double outSeconds, bool marked)
    {
        if ((_in, _out, _hasRange) == (inSeconds, outSeconds, marked))
            return;
        (_in, _out, _hasRange) = (inSeconds, outSeconds, marked);
        InvalidateVisual();
    }

    /// <summary>Whether the pane is stepping through the file (Space): said over the picture, as it plays no sound.</summary>
    public bool Stepping
    {
        get => _stepping;
        set
        {
            if (_stepping == value)
                return;
            _stepping = value;
            InvalidateVisual();
        }
    }

    /// <summary>"1:02.345" (hours when there are any): a moment to the millisecond.</summary>
    public static string FormatMoment(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(double.IsFinite(seconds) ? seconds : 0, 0) * 1000);
        var (hours, minutes, secs, millis) = (ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}.{millis:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}.{millis:000}");
    }

    /// <summary>The sub-clip's line: "In 0:12.000 · Out 0:31.500 · 19.5 s".</summary>
    public static string FormatRange(double inSeconds, double outSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"In {FormatMoment(inSeconds)} · Out {FormatMoment(outSeconds)} · {Math.Max(outSeconds - inSeconds, 0):0.0##} s");

    /// <summary>Where a picture of a shape goes in a box: as large as fits, in its middle (letterboxed or pillarboxed).</summary>
    internal static Rect Letterbox(Size box, double pictureWidth, double pictureHeight)
    {
        if (box.Width <= 0 || box.Height <= 0 || pictureWidth <= 0 || pictureHeight <= 0)
            return Rect.Empty;

        var scale = Math.Min(box.Width / pictureWidth, box.Height / pictureHeight);
        var (width, height) = (pictureWidth * scale, pictureHeight * scale);
        return new Rect((box.Width - width) / 2, (box.Height - height) / 2, width, height);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        drawing.DrawRectangle(BackBrush, null, new Rect(0, 0, width, height));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double line = 22;
        var pictureBox = new Size(width, Math.Max(height - line, 1));
        if (_frame is { Width: > 0, Height: > 0 } frame)
        {
            var at = Letterbox(pictureBox, frame.Width, frame.Height);
            drawing.DrawImage(frame, at);
        }
        else
        {
            var said = Text(_message, 12, MutedBrush, dpi);
            said.MaxTextWidth = Math.Max(width - 24, 8);
            said.TextAlignment = TextAlignment.Center;
            drawing.DrawText(said, new Point(12, Math.Max((pictureBox.Height - said.Height) / 2, 4)));
        }

        // The line under the picture: the moment and the length on the left, the sub-clip on the right.
        if (_duration > 0)
        {
            var moment = Text($"{FormatMoment(_position)} / {FormatMoment(_duration)}", 11, TextBrush, dpi);
            drawing.DrawText(moment, new Point(6, height - line + (line - moment.Height) / 2));
            if (_hasRange)
            {
                var range = Text(FormatRange(_in, _out), 11, RangeBrush, dpi);
                var left = Math.Max(width - range.Width - 6, moment.Width + 18);
                if (left + range.Width <= width)
                    drawing.DrawText(range, new Point(left, height - line + (line - range.Height) / 2));
            }
        }

        if (_stepping)
        {
            var note = Text("Preview: stepping through stills, no sound", 11, TextBrush, dpi);
            var box = new Rect(6, 6, note.Width + 12, note.Height + 6);
            drawing.DrawRoundedRectangle(ShadeBrush, null, box, 3, 3);
            drawing.DrawText(note, new Point(12, 9));
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
