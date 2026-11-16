using System.Globalization;
using System.Windows;
using System.Windows.Media;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Views;

/// <summary>
/// Media Browser source preview: the selected file's frame at the cutter time, letterboxed, with a time line below and the marked
/// sub-clip (In, Out, duration), or a status message when no frame is available (no selection, audio, missing or unreadable file).
/// Owns no decoder: the pane supplies frames (SourceFrames, or a strip frame while a still is pending). During stepping (Space) it shows
/// a notice: a few stills per second, no audio.
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

    /// <summary>Displayed frame (frozen); null shows <see cref="Message"/> instead.</summary>
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

    /// <summary>Status text shown without a frame (no selection, audio, missing or unreadable file).</summary>
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

    /// <summary>Sets the current time and file duration in seconds (shown below the picture).</summary>
    public void ShowMoment(double position, double duration)
    {
        if (position == _position && duration == _duration)
            return;
        (_position, _duration) = (position, duration);
        InvalidateVisual();
    }

    /// <summary>Sets the marked sub-clip (In/Out seconds); <paramref name="marked"/> false means the whole file.</summary>
    public void ShowRange(double inSeconds, double outSeconds, bool marked)
    {
        if ((_in, _out, _hasRange) == (inSeconds, outSeconds, marked))
            return;
        (_in, _out, _hasRange) = (inSeconds, outSeconds, marked);
        InvalidateVisual();
    }

    /// <summary>True while the pane steps through the file (Space); shows a no-audio notice over the picture.</summary>
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

    /// <summary>Millisecond timecode, e.g. "1:02.345" (hours prefixed when non-zero).</summary>
    public static string FormatMoment(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(double.IsFinite(seconds) ? seconds : 0, 0) * 1000);
        var (hours, minutes, secs, millis) = (ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}.{millis:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}.{millis:000}");
    }

    /// <summary>Sub-clip label, e.g. "In 0:12.000 · Out 0:31.500 · 19.5 s".</summary>
    public static string FormatRange(double inSeconds, double outSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"In {FormatMoment(inSeconds)} · Out {FormatMoment(outSeconds)} · {Math.Max(outSeconds - inSeconds, 0):0.0##} s");

    /// <summary>Largest centred rect of the picture's aspect inside the box (letterbox or pillarbox).</summary>
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

        // Bottom line: time / duration on the left, sub-clip range on the right.
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

}
