using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services.Audio;

namespace HandPegApp;

// ----- The master meter and fader (Pass 5.2B, the Studio Audio Suite) -----
// What the Sequence's mix measures as the sound engine plays it (after its master gain, before the monitor volume: what an export gets),
// and the Sequence's master fader. Everything is drawn in one OnRender from frozen brushes made once and from text laid out once per
// character (a monospaced face, so a readout is its characters side by side): a redraw allocates nothing. Nothing polls while the
// player is paused: AudioMeterFeed reads the engine's meter about thirty times a second while it plays, and a couple of seconds after,
// so the bars fall and the holds run out; then the meter rests until the next play.

/// <summary>
/// The one reader of the sound engine's meter for every <see cref="AudioMeterControl"/> on screen (Pass 5.2B). The engine's meter is read
/// and reset in one go (WasapiAudioEngine.Meter), so two meters reading it each would see half the peaks: the feed reads it once a tick
/// and hands the reading to each meter shown. Its timer runs only while the player plays (and <see cref="TrailingTicks"/> after), never
/// on CompositionTarget.Rendering, and never while no meter is shown.
/// </summary>
public sealed class AudioMeterFeed
{
    // About thirty readings a second; and after the player stops, two seconds more (the bars fall, the peak holds run out).
    private const double TickMs = 33;
    private const int TrailingTicks = 60;

    private readonly Func<AudioMeterReading?> _read;
    private readonly List<AudioMeterControl> _meters = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    private bool _playing;
    private int _trailing;
    private long _lastTick;

    /// <param name="read">The engine's meter now (read and reset), or null while there is no engine.</param>
    public AudioMeterFeed(Func<AudioMeterReading?> read)
    {
        _read = read;
        _timer.Tick += Tick;
    }

    /// <summary>Whether the player is playing: the readings start at once, and stop a couple of seconds after it pauses.</summary>
    public bool Playing
    {
        get => _playing;
        set
        {
            if (value == _playing)
                return;

            _playing = value;
            _trailing = TrailingTicks;
            Wake();
        }
    }

    internal void Attach(AudioMeterControl meter)
    {
        if (!_meters.Contains(meter))
            _meters.Add(meter);
        Wake();
    }

    internal void Detach(AudioMeterControl meter)
    {
        _meters.Remove(meter);
        if (_meters.Count == 0)
            _timer.Stop();
    }

    private void Wake()
    {
        if (_timer.IsEnabled || _meters.Count == 0 || (!_playing && _trailing <= 0))
            return;

        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Clamp((now - _lastTick) / (double)Stopwatch.Frequency, 0, 0.25);
        _lastTick = now;

        // Read once for every meter (the read resets the engine's peaks).
        var reading = _read() ?? default;
        if (!_playing && --_trailing <= 0)
        {
            _timer.Stop();
            foreach (var meter in _meters)
                meter.Rest();
            return;
        }

        foreach (var meter in _meters)
            meter.Take(reading, seconds);
    }
}

/// <summary>
/// The Sequence's master meter (Pass 5.2B): the stereo peak bars with the RMS inside each, a peak-hold tick on each (held 1.5 s), a clip
/// light (lit by a sample or true peak at 0 dBFS, until it is clicked), a loudness bar (momentary LUFS, its short-term level as a tick)
/// with the usual targets of -14 and -16 LUFS marked, the momentary, short-term and integrated LUFS and the true-peak hold in numbers;
/// and the master fader (-40 to +12 dB): dragged (Shift: finely), the wheel 0.5 dB a notch (Shift: 0.1), a double-click back to 0 dB.
/// Upright or across (<see cref="Orientation"/>). Its readings come from an <see cref="AudioMeterFeed"/>; <see cref="GainDb"/> is bound
/// to the Sequence's master gain, a drag or a burst of wheel notches told as one gesture (<see cref="GainChangeStarted"/> and
/// <see cref="GainChangeEnded"/>: the host makes it one step of Undo).
/// </summary>
public sealed class AudioMeterControl : FrameworkElement
{
    /// <summary>The fader's range, dB.</summary>
    public const double GainLeast = -40, GainMost = 12;

    // The bars' scale (dBFS, and LUFS on the loudness bar): linear in dB from the floor to a little over full scale.
    private const double FloorDb = -60, CeilingDb = 6;

    // How fast the bars and the holds fall (dB a second), how long a peak is held, and the loudness targets marked.
    private const double FallPerSecond = 24, HoldSeconds = 1.5;
    private const double StreamingTarget = -14, BroadcastTarget = -16;

    // How far apart a burst of wheel notches may come and still be one gesture, and how often a drag writes the gain (as the Effects
    // pane's sliders: each write is an edit of the Sequence).
    private static readonly TimeSpan WheelGesture = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan DragWrite = TimeSpan.FromMilliseconds(33);

    // The marks of the scale, in dB.
    private static readonly double[] ScaleMarks = [6, 0, -6, -12, -18, -24, -36, -48, -60];

    // ----- Made once -----

    private static readonly Brush Green = Solid(255, 0x4C, 0xC0, 0x5A), Yellow = Solid(255, 0xE6, 0xC8, 0x3C), Orange = Solid(255, 0xF0, 0x8A, 0x30), Red = Solid(255, 0xE5, 0x3E, 0x3E);
    private static readonly Brush GreenDim = Solid(120, 0x4C, 0xC0, 0x5A), YellowDim = Solid(120, 0xE6, 0xC8, 0x3C), OrangeDim = Solid(120, 0xF0, 0x8A, 0x30), RedDim = Solid(130, 0xE5, 0x3E, 0x3E);
    private static readonly Brush ClipOff = Solid(70, 0xE5, 0x3E, 0x3E), Loudness = Solid(220, 0x4F, 0xC3, 0xF7), ShortTermTick = Solid(255, 0xFF, 0xFF, 0xFF);
    private static readonly Brush StreamingTick = Solid(255, 0xFF, 0xC8, 0x40), BroadcastTick = Solid(255, 0xB0, 0x8C, 0xFF);
    private static readonly Brush FaderTrack = Solid(70, 0x80, 0x80, 0x80), Notch = Solid(160, 0xC0, 0xC0, 0xC0), FaderThumb = Solid(255, 0xE8, 0xE8, 0xE8);
    private static readonly Pen ThumbEdge = FrozenPen(Solid(255, 0x30, 0x30, 0x30), 1);
    private static readonly Typeface Mono = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private const double TextSize = 10;

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    // ----- Its properties -----

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(AudioMeterControl),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GainDbProperty = DependencyProperty.Register(nameof(GainDb), typeof(double), typeof(AudioMeterControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Upright (the level rising up the control) or across (rising to the right).</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>The master fader, dB (-40 to +12): bound to the Sequence's master gain.</summary>
    public double GainDb
    {
        get => (double)GetValue(GainDbProperty);
        set => SetValue(GainDbProperty, value);
    }

    /// <summary>A gesture on the fader began (a drag, the first of a burst of wheel notches): what it sets until it ends is one step.</summary>
    public event Action? GainChangeStarted;

    /// <summary>The gesture ended (the drag let go or taken away, the wheel at rest).</summary>
    public event Action? GainChangeEnded;

    private AudioMeterFeed? _feed;
    private bool _attached;

    /// <summary>Where its readings come from (shared by every meter shown); null for none (the bars rest).</summary>
    public AudioMeterFeed? Feed
    {
        get => _feed;
        set
        {
            if (ReferenceEquals(value, _feed))
                return;

            if (_attached)
                _feed?.Detach(this);
            _attached = false;
            _feed = value;
            FollowShown();
        }
    }

    // ----- What it shows: the readings with their ballistics -----

    private double _peakLeft = FloorDb, _peakRight = FloorDb, _rmsLeft = FloorDb, _rmsRight = FloorDb;
    private double _holdLeft = FloorDb, _holdRight = FloorDb;
    private long _holdLeftUntil, _holdRightUntil;
    private bool _clipped;
    private float _momentary = float.NegativeInfinity, _shortTerm = float.NegativeInfinity, _integrated = float.NegativeInfinity, _truePeakHold = float.NegativeInfinity;

    public AudioMeterControl()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        ToolTip = "Master: the Sequence's mix as it plays (after its master gain, as an export gets it). The bars are each side's peak with its RMS inside, "
                  + "the tick the peak held for 1.5 s; the red light, a sample or true peak at full scale (click it to clear it). The blue bar is the momentary "
                  + "loudness (LUFS), its short-term level the white tick; the gold and violet marks are -14 and -16 LUFS, the usual streaming and broadcast "
                  + "targets. M, S and I: momentary, short-term and integrated LUFS; TP: the highest true peak of the last 3 s. The fader at the side is the "
                  + "master gain: drag it (Shift: finely), the wheel moves it 0.5 dB (Shift: 0.1), a double-click puts it back to 0 dB.";
        AutomationProperties.SetName(this, "Master Meter and Fader");
        _wheelRest.Tick += (_, _) => EndWheel();
        _dragFlush.Tick += (_, _) => FlushDrag();
        Loaded += (_, _) =>
        {
            _loaded = true;
            Services.ThemeManager.Applied += ThemeApplied;
            ReadLook();
            FollowShown();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            Services.ThemeManager.Applied -= ThemeApplied;
            EndWheel();
            FollowShown();
        };
        IsVisibleChanged += (_, _) => FollowShown();
    }

    private bool _loaded;

    // Takes readings only while it is on screen (a meter in a tab not shown costs nothing).
    private void FollowShown()
    {
        var wanted = _feed is not null && _loaded && IsVisible;
        if (wanted == _attached)
            return;

        _attached = wanted;
        if (wanted)
            _feed!.Attach(this);
        else
            _feed?.Detach(this);
    }

    /// <summary>A reading from the feed, <paramref name="seconds"/> after the last: the bars rise to it at once and fall at 24 dB a second.</summary>
    internal void Take(in AudioMeterReading reading, double seconds)
    {
        var fall = FallPerSecond * seconds;
        var now = Stopwatch.GetTimestamp();
        var fresh = reading.Frames > 0;
        Follow(ref _peakLeft, fresh ? Db(reading.PeakLeft) : FloorDb, fall);
        Follow(ref _peakRight, fresh ? Db(reading.PeakRight) : FloorDb, fall);
        Follow(ref _rmsLeft, fresh ? Db(reading.RmsLeft) : FloorDb, fall);
        Follow(ref _rmsRight, fresh ? Db(reading.RmsRight) : FloorDb, fall);
        Hold(ref _holdLeft, ref _holdLeftUntil, _peakLeft, now, fall);
        Hold(ref _holdRight, ref _holdRightUntil, _peakRight, now, fall);
        if (fresh)
        {
            _clipped |= reading.Clipped || reading.PeakLeft >= 1 || reading.PeakRight >= 1;
            (_momentary, _shortTerm, _integrated, _truePeakHold) = (reading.MomentaryLufs, reading.ShortTermLufs, reading.IntegratedLufs, reading.TruePeakHoldDb);
        }

        InvalidateVisual();
    }

    /// <summary>The player stopped a while ago: the bars and holds at rest (the numbers stay, the last the Sequence measured).</summary>
    internal void Rest()
    {
        _peakLeft = _peakRight = _rmsLeft = _rmsRight = _holdLeft = _holdRight = FloorDb;
        InvalidateVisual();
    }

    private static double Db(float factor) => factor <= 1e-9f ? FloorDb : Math.Max(20 * Math.Log10(factor), FloorDb);

    private static void Follow(ref double shown, double now, double fall) => shown = Math.Max(now, Math.Max(shown - fall, FloorDb));

    private static void Hold(ref double hold, ref long until, double peak, long now, double fall)
    {
        if (peak >= hold)
        {
            hold = peak;
            until = now + (long)(HoldSeconds * Stopwatch.Frequency);
        }
        else if (now > until)
        {
            hold = Math.Max(hold - fall, peak);
        }
    }

    // ----- The theme's look -----

    private Brush _inset = Solid(255, 0x1E, 0x1E, 0x1E), _text = Brushes.Gainsboro, _muted = Brushes.Gray;

    private void ThemeApplied()
    {
        ReadLook();
        InvalidateVisual();
    }

    private void ReadLook()
    {
        _inset = Look("InsetBrush", _inset);
        _text = Look("TextBrush", _text);
        _muted = Look("MutedTextBrush", _muted);
        Array.Clear(_glyphs);
    }

    private Brush Look(string key, Brush fallback) => TryFindResource(key) is Brush brush ? (Brush)brush.GetCurrentValueAsFrozen() : fallback;

    // ----- The words: each character laid out once (per DPI and theme), a readout drawn as its characters side by side -----

    private readonly FormattedText?[] _glyphs = new FormattedText?[128];
    private double _glyphDpi = -1;
    private double _advance = 6, _lineHeight = 12;

    private void PrepareGlyphs(double pixelsPerDip)
    {
        if (pixelsPerDip == _glyphDpi)
            return;

        Array.Clear(_glyphs);
        _glyphDpi = pixelsPerDip;
        var zero = Glyph('0', pixelsPerDip);
        (_advance, _lineHeight) = zero is null ? (6.0, 12.0) : (zero.WidthIncludingTrailingWhitespace, zero.Height);
    }

    private FormattedText? Glyph(char c, double pixelsPerDip)
    {
        if (c >= _glyphs.Length || c == ' ')
            return null;

        return _glyphs[c] ??= new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, TextSize, char.IsAsciiLetter(c) ? _muted : _text, pixelsPerDip);
    }

    /// <summary>Draws characters from <paramref name="x"/> (or ending there, <paramref name="rightAligned"/>); the x after them.</summary>
    private double DrawChars(DrawingContext drawing, ReadOnlySpan<char> text, double x, double y, double pixelsPerDip, bool rightAligned = false)
    {
        if (rightAligned)
            x -= text.Length * _advance;
        foreach (var c in text)
        {
            if (Glyph(c, pixelsPerDip) is { } glyph)
                drawing.DrawText(glyph, new Point(x, y));
            x += _advance;
        }

        return x;
    }

    // A value as its characters, into the room given: "--" for silence (or nothing measured yet).
    private static ReadOnlySpan<char> Number(Span<char> room, double value, bool signed)
    {
        if (!double.IsFinite(value) || value < -99.9)
        {
            room[0] = room[1] = '-';
            return room[..2];
        }

        return value.TryFormat(room, out var written, signed ? "+0.0;-0.0;0.0" : "0.0", CultureInfo.InvariantCulture) ? room[..written] : room[..0];
    }

    // ----- Laying it out -----
    // The level runs along the control (up it, or to the right); across it lie the scale, the two bars, the loudness bar and the fader.
    // Everything is placed by arithmetic as it is drawn, and found again so as it is pressed.

    private bool Upright => Orientation == Orientation.Vertical;

    private const double Pad = 3, ScaleRoom = 24, ScaleRoomAcross = 12, ClipRoom = 8, Gap = 1, LoudnessGap = 4, LoudnessWidth = 7, FaderGap = 6, FaderWidth = 16;

    // The level's span along the control (top or left, bottom or right), and where each part lies across it.
    private double _levelFrom, _levelTo;
    private double _leftFrom, _leftTo, _rightFrom, _rightTo, _loudFrom, _loudTo, _faderFrom, _faderTo, _scaleAt;
    private Rect _clipBox, _readouts;

    private void Lay(Size size)
    {
        if (Upright)
        {
            // Across (x): the scale's numbers, the two bars, the loudness bar, the fader. Along (y): the clip light over the bars, the
            // readouts under everything.
            // In a pane wider than it needs, it stands in the middle of it.
            var bar = Math.Clamp((size.Width - 2 * Pad - ScaleRoom - Gap - LoudnessGap - LoudnessWidth - FaderGap - FaderWidth) / 2, 4, 12);
            var content = 2 * Pad + ScaleRoom + 2 * bar + Gap + LoudnessGap + LoudnessWidth + FaderGap + FaderWidth;
            var left = Math.Max((size.Width - content) / 2, 0);
            _scaleAt = left + Pad + ScaleRoom - 3;
            (_leftFrom, _leftTo) = (left + Pad + ScaleRoom, left + Pad + ScaleRoom + bar);
            (_rightFrom, _rightTo) = (_leftTo + Gap, _leftTo + Gap + bar);
            (_loudFrom, _loudTo) = (_rightTo + LoudnessGap, _rightTo + LoudnessGap + LoudnessWidth);
            (_faderFrom, _faderTo) = (_loudTo + FaderGap, _loudTo + FaderGap + FaderWidth);
            var readoutLines = size.Height > 240 ? 5 : 4;
            var readoutHeight = readoutLines * _lineHeight + 4;
            var readoutWidth = Math.Min(Math.Max(content, 10 * _advance + 2 * Pad), size.Width);
            _clipBox = new Rect(_leftFrom, Pad, _rightTo - _leftFrom, ClipRoom - 2);
            (_levelFrom, _levelTo) = (Pad + ClipRoom + 4, Math.Max(size.Height - Pad - readoutHeight - 4, Pad + ClipRoom + 24));
            _readouts = new Rect(Math.Max((size.Width - readoutWidth) / 2, 0) + Pad, _levelTo + 6, Math.Max(readoutWidth - 2 * Pad, 0), readoutHeight);
        }
        else
        {
            // Along (x): the bars, the clip light at their end, the readouts. Across (y): the two bars, the loudness bar, the scale's
            // numbers, the fader.
            var readoutWidth = 24 * _advance + 4;
            var bar = Math.Clamp((size.Height - 2 * Pad - Gap - LoudnessGap - LoudnessWidth - ScaleRoomAcross - FaderGap - FaderWidth) / 2, 4, 10);
            (_leftFrom, _leftTo) = (Pad, Pad + bar);
            (_rightFrom, _rightTo) = (_leftTo + Gap, _leftTo + Gap + bar);
            (_loudFrom, _loudTo) = (_rightTo + LoudnessGap, _rightTo + LoudnessGap + LoudnessWidth);
            _scaleAt = _loudTo + 1;
            (_faderFrom, _faderTo) = (_scaleAt + ScaleRoomAcross + FaderGap - 4, _scaleAt + ScaleRoomAcross + FaderGap - 4 + FaderWidth);
            (_levelFrom, _levelTo) = (Pad + 2 * _advance, Math.Max(size.Width - Pad - readoutWidth - ClipRoom - 8, Pad + 40));
            _clipBox = new Rect(_levelTo + 4, _leftFrom, ClipRoom - 2, _rightTo - _leftFrom);
            _readouts = new Rect(_clipBox.Right + 8, Pad, Math.Max(size.Width - _clipBox.Right - 8 - Pad, 0), Math.Max(size.Height - 2 * Pad, 0));
        }
    }

    // A fraction of the scale (0 at the floor) as a place along the control.
    private double Along(double fraction) => Upright ? _levelTo - fraction * (_levelTo - _levelFrom) : _levelFrom + fraction * (_levelTo - _levelFrom);

    private static double LevelFraction(double db) => Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);

    private static double GainFraction(double db) => Math.Clamp((db - GainLeast) / (GainMost - GainLeast), 0, 1);

    // The stretch between two fractions of the scale, across from one place to another.
    private Rect Stretch(double from, double to, double fractionFrom, double fractionTo)
    {
        var (a, b) = (Along(fractionFrom), Along(fractionTo));
        return Upright
            ? new Rect(from, Math.Min(a, b), Math.Max(to - from, 0), Math.Abs(b - a))
            : new Rect(Math.Min(a, b), from, Math.Abs(b - a), Math.Max(to - from, 0));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var wanted = Upright ? new Size(100, 220) : new Size(420, 60);
        return new Size(Math.Min(wanted.Width, availableSize.Width), Math.Min(wanted.Height, availableSize.Height));
    }

    // ----- Drawing -----

    protected override void OnRender(DrawingContext drawing)
    {
        var size = RenderSize;
        if (size.Width < 8 || size.Height < 8)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        PrepareGlyphs(dpi);
        Lay(size);

        // The whole of it takes the pointer (the fader's wheel, the clip light).
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        // The scale's numbers, beside (or under) the bars.
        Span<char> room = stackalloc char[16];
        foreach (var mark in ScaleMarks)
        {
            var at = Along(LevelFraction(mark));
            var text = Number(room, mark, signed: false);
            text = text.EndsWith(".0") ? text[..^2] : text;
            if (Upright)
                DrawChars(drawing, text, _scaleAt, at - _lineHeight / 2, dpi, rightAligned: true);
            else
                DrawChars(drawing, text, at - text.Length * _advance / 2, _scaleAt, dpi);
        }

        // The two sides: each a track, its peak (dim) and its RMS inside it (full), coloured by zone, and its held peak's tick.
        DrawSide(drawing, _leftFrom, _leftTo, _peakLeft, _rmsLeft, _holdLeft);
        DrawSide(drawing, _rightFrom, _rightTo, _peakRight, _rmsRight, _holdRight);
        drawing.DrawRectangle(_clipped ? Red : ClipOff, null, _clipBox);

        // The loudness bar: momentary LUFS, the short-term a tick, the two targets marked across it.
        drawing.DrawRectangle(_inset, null, Stretch(_loudFrom, _loudTo, 0, 1));
        if (double.IsFinite(_momentary))
            drawing.DrawRectangle(Loudness, null, Stretch(_loudFrom, _loudTo, 0, LevelFraction(_momentary)));
        if (double.IsFinite(_shortTerm))
            drawing.DrawRectangle(ShortTermTick, null, Tick(_loudFrom, _loudTo, LevelFraction(_shortTerm), 2));
        drawing.DrawRectangle(StreamingTick, null, Tick(_loudFrom - 2, _loudTo + 2, LevelFraction(StreamingTarget), 1));
        drawing.DrawRectangle(BroadcastTick, null, Tick(_loudFrom - 2, _loudTo + 2, LevelFraction(BroadcastTarget), 1));

        // The fader: its track, a notch at 0 dB, its thumb.
        var middle = (_faderFrom + _faderTo) / 2;
        drawing.DrawRectangle(FaderTrack, null, Stretch(middle - 1.5, middle + 1.5, 0, 1));
        drawing.DrawRectangle(Notch, null, Tick(_faderFrom + 2, _faderTo - 2, GainFraction(0), 1));
        var thumb = Tick(_faderFrom, _faderTo, GainFraction(ShownGain), 8);
        drawing.DrawRoundedRectangle(FaderThumb, ThumbEdge, thumb, 2, 2);

        DrawReadouts(drawing, room, dpi);
    }

    // A side's bar.
    private void DrawSide(DrawingContext drawing, double from, double to, double peak, double rms, double hold)
    {
        drawing.DrawRectangle(_inset, null, Stretch(from, to, 0, 1));
        var inset = Math.Max((to - from) / 4, 1);
        DrawZones(drawing, from, to, peak, dim: true);
        DrawZones(drawing, from + inset, to - inset, rms, dim: false);
        if (hold > FloorDb + 0.5)
            drawing.DrawRectangle(ZoneOf(hold, dim: false), null, Tick(from, to, LevelFraction(hold), 2));
    }

    // A level drawn up to where it is, in the zones it passes: green to -18 dB, yellow to -6, orange to 0, red over it.
    private void DrawZones(DrawingContext drawing, double from, double to, double db, bool dim)
    {
        if (db <= FloorDb + 0.01 || to <= from)
            return;

        var level = LevelFraction(db);
        double start = 0;
        foreach (var (edge, brush) in Zones(dim))
        {
            var end = Math.Min(level, LevelFraction(edge));
            if (end > start)
                drawing.DrawRectangle(brush, null, Stretch(from, to, start, end));
            start = Math.Max(start, end);
            if (start >= level)
                break;
        }
    }

    private static (double Edge, Brush Brush)[] Zones(bool dim) => dim ? DimZones : FullZones;

    private static readonly (double Edge, Brush Brush)[] FullZones = [(-18, Green), (-6, Yellow), (0, Orange), (CeilingDb, Red)];
    private static readonly (double Edge, Brush Brush)[] DimZones = [(-18, GreenDim), (-6, YellowDim), (0, OrangeDim), (CeilingDb, RedDim)];

    private static Brush ZoneOf(double db, bool dim) => db < -18 ? (dim ? GreenDim : Green) : db < -6 ? (dim ? YellowDim : Yellow) : db < 0 ? (dim ? OrangeDim : Orange) : dim ? RedDim : Red;

    // A thin mark across from one place to another, at a fraction of the scale, so many pixels thick along it.
    private Rect Tick(double from, double to, double fraction, double thickness)
    {
        var at = Along(fraction);
        return Upright ? new Rect(from, at - thickness / 2, Math.Max(to - from, 0), thickness) : new Rect(at - thickness / 2, from, thickness, Math.Max(to - from, 0));
    }

    // The numbers: M, S (and I where there is room), TP, the fader's gain.
    private void DrawReadouts(DrawingContext drawing, Span<char> room, double dpi)
    {
        var (x, y) = (_readouts.Left, _readouts.Top);
        if (Upright)
        {
            var valueRight = _readouts.Right - 2;
            Line(drawing, "M", Number(room, _momentary, signed: false), x, valueRight, y, dpi);
            Line(drawing, "S", Number(room, _shortTerm, signed: false), x, valueRight, y += _lineHeight, dpi);
            if (_readouts.Height >= 5 * _lineHeight)
                Line(drawing, "I", Number(room, _integrated, signed: false), x, valueRight, y += _lineHeight, dpi);
            Line(drawing, "TP", Number(room, _truePeakHold, signed: false), x, valueRight, y += _lineHeight, dpi);
            Line(drawing, "dB", Number(room, ShownGain, signed: true), x, valueRight, y + _lineHeight, dpi);
            return;
        }

        // Across: three columns of two lines (M and S, I and TP, the gain).
        var column = 8 * _advance;
        Line(drawing, "M", Number(room, _momentary, signed: false), x, x + column - _advance, y, dpi);
        Line(drawing, "S", Number(room, _shortTerm, signed: false), x, x + column - _advance, y + _lineHeight, dpi);
        Line(drawing, "I", Number(room, _integrated, signed: false), x + column, x + 2 * column - _advance, y, dpi);
        Line(drawing, "TP", Number(room, _truePeakHold, signed: false), x + column, x + 2 * column - _advance, y + _lineHeight, dpi);
        Line(drawing, "dB", Number(room, ShownGain, signed: true), x + 2 * column, x + 3 * column - _advance, y, dpi);
    }

    private void Line(DrawingContext drawing, string label, ReadOnlySpan<char> value, double left, double valueRight, double y, double dpi)
    {
        DrawChars(drawing, label, left, y, dpi);
        DrawChars(drawing, value, valueRight, y, dpi, rightAligned: true);
    }

    // ----- The fader and the clip light under the pointer -----

    // A drag of the fader: where it began along the control, the gain then, and the gain it has come to (shown at once, written at most
    // thirty times a second); whether a burst of wheel notches is under way.
    private (double From, double Gain)? _drag;
    private double _dragGain;
    private double? _pendingGain;
    private bool _wheeling;
    private readonly DispatcherTimer _wheelRest = new() { Interval = WheelGesture };
    private readonly DispatcherTimer _dragFlush = new() { Interval = DragWrite };

    private double ShownGain => _drag is not null ? _dragGain : GainDb;

    private double AlongOf(Point point) => Upright ? point.Y : point.X;

    private double AcrossOf(Point point) => Upright ? point.X : point.Y;

    private bool OnFader(Point point) =>
        AcrossOf(point) >= _faderFrom - 4 && AcrossOf(point) <= _faderTo + 4
        && AlongOf(point) >= Math.Min(_levelFrom, _levelTo) - 8 && AlongOf(point) <= Math.Max(_levelFrom, _levelTo) + 8;

    // The gain at a place along the fader.
    private double GainAt(double along)
    {
        var length = Math.Max(Math.Abs(_levelTo - _levelFrom), 1);
        var fraction = Upright ? (_levelTo - along) / length : (along - _levelFrom) / length;
        return GainLeast + Math.Clamp(fraction, 0, 1) * (GainMost - GainLeast);
    }

    private static double Rounded(double db) => Math.Round(Math.Clamp(db, GainLeast, GainMost), 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        Lay(RenderSize);
        if (Rect.Inflate(_clipBox, 3, 3).Contains(point))
        {
            // The clip light cleared, and with it the holds.
            _clipped = false;
            (_holdLeft, _holdRight, _truePeakHold) = (_peakLeft, _peakRight, float.NegativeInfinity);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!OnFader(point))
            return;

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            // Back to 0 dB: one step of its own.
            EndDrag();
            GainDb = 0;
            return;
        }

        if (!CaptureMouse())
            return;

        // On the thumb, it is taken where it is; elsewhere on the track it jumps to the pointer first.
        var thumb = Tick(_faderFrom, _faderTo, GainFraction(GainDb), 8);
        var start = Rect.Inflate(thumb, 2, 2).Contains(point) ? GainDb : Rounded(GainAt(AlongOf(point)));
        GainChangeStarted?.Invoke();
        _drag = (AlongOf(point), start);
        _dragGain = start;
        WriteDragged(start);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } drag || !IsMouseCaptured)
            return;

        // Along the control by so many pixels: the gain by as much of the fader's range (a tenth of it with Shift).
        var length = Math.Max(Math.Abs(_levelTo - _levelFrom), 1);
        var moved = AlongOf(e.GetPosition(this)) - drag.From;
        var perPixel = (GainMost - GainLeast) / length * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1);
        var gain = Rounded(drag.Gain + (Upright ? -moved : moved) * perPixel);
        if (gain == _dragGain)
            return;

        _dragGain = gain;
        WriteDragged(gain);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is null)
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

    // A dragged gain written now, or (written less than a thirtieth of a second ago) when that time is up.
    private void WriteDragged(double gain)
    {
        if (_dragFlush.IsEnabled)
        {
            _pendingGain = gain;
            return;
        }

        GainDb = gain;
        _dragFlush.Start();
    }

    private void FlushDrag()
    {
        _dragFlush.Stop();
        if (_pendingGain is { } pending)
        {
            _pendingGain = null;
            GainDb = pending;
            _dragFlush.Start();
        }
    }

    // The drag let go (or taken away): its last gain written, the gesture told as ended.
    private void EndDrag()
    {
        if (_drag is null)
            return;

        _drag = null;
        _dragFlush.Stop();
        if (_pendingGain is { } pending)
            GainDb = pending;
        _pendingGain = null;
        if (_dragGain != GainDb)
            GainDb = _dragGain;
        GainChangeEnded?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Over the fader only: a wheel turned over the rest of it scrolls whatever it lies in, as ever.
        Lay(RenderSize);
        var point = e.GetPosition(this);
        if (_drag is not null || e.Delta == 0 || AcrossOf(point) < _faderFrom - 10 || AcrossOf(point) > _faderTo + 10)
            return;

        e.Handled = true;
        if (!_wheeling)
        {
            _wheeling = true;
            GainChangeStarted?.Invoke();
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.5;
        GainDb = Rounded(GainDb + Math.Sign(e.Delta) * step * Math.Max(Math.Abs(e.Delta) / Mouse.MouseWheelDeltaForOneLine, 1));
        _wheelRest.Stop();
        _wheelRest.Start();
    }

    // The wheel at rest a moment: its burst of notches is one gesture.
    private void EndWheel()
    {
        _wheelRest.Stop();
        if (!_wheeling)
            return;

        _wheeling = false;
        GainChangeEnded?.Invoke();
    }
}
