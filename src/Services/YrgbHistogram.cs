namespace HandPegApp.Services;

/// <summary>
/// The channels a histogram overlay shows (Color Studio curves backdrop and the Video Scopes Histogram): YRGB (red, green and blue area
/// fills with Y/R/G/B traces), RGB (no luma) or Luma alone. Append-only; persisted by label in <see cref="AppSettings.ColorHistogramMode"/>.
/// </summary>
public enum HistogramMode { Yrgb, Rgb, Luma }

/// <summary>
/// A frame's YRGB histogram (Pass 5.6.4B): counts per level of red, green, blue and BT.709 luma (<see cref="VideoScopes.Histogram"/>'s
/// rows), plus what the overlay draws from them: the peak its heights scale to (pure black and white excluded so letterboxes and blown skies
/// do not flatten the rest) and each channel's 0 % shadow-crush and 100 % highlight-clip shares behind the edge indicators. Immutable and
/// WPF-free: built off the UI thread, drawn by HistogramPainter.
/// </summary>
public sealed class YrgbHistogram
{
    /// <summary>Channel rows, in <see cref="VideoScopes.Histogram"/>'s order.</summary>
    public const int Red = 0, Green = 1, Blue = 2, Luma = 3;

    /// <summary>Levels per channel.</summary>
    public const int Levels = VideoScopes.Levels;

    /// <summary>Share of a channel's pixels at level 0 (or 255) that lights its shadow-crush (or highlight-clip) indicator: 0.5 %.</summary>
    public const double EdgeShare = 0.005;

    /// <summary>The modes in pill order (YRGB | RGB | Luma).</summary>
    public static readonly HistogramMode[] Modes = [HistogramMode.Yrgb, HistogramMode.Rgb, HistogramMode.Luma];

    private static readonly int[] YrgbChannels = [Red, Green, Blue, Luma], RgbChannels = [Red, Green, Blue], LumaChannels = [Luma];

    private readonly int[,] _counts;

    /// <summary>Wraps counts[channel, level] (4 x 256: red, green, blue, luma), copied so the histogram never changes under a reader.</summary>
    public YrgbHistogram(int[,] counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.GetLength(0) != 4 || counts.GetLength(1) != Levels)
            throw new ArgumentException("A YRGB histogram has 4 rows of 256 levels.", nameof(counts));
        _counts = (int[,])counts.Clone();
        var pixels = 0L;
        for (var level = 0; level < Levels; level++)
            pixels += Math.Max(_counts[Luma, level], 0);
        Pixels = (int)Math.Min(pixels, int.MaxValue);
    }

    /// <summary>The histogram of a BGRA8 frame (alpha ignored).</summary>
    public static YrgbHistogram Of(ReadOnlySpan<byte> bgra, int width, int height) => new(VideoScopes.Histogram(bgra, width, height));

    /// <summary>A grey picture's histogram from luma counts alone (every channel equal to the luma): the legacy 256-bin input.</summary>
    public static YrgbHistogram FromLuma(int[] luma)
    {
        ArgumentNullException.ThrowIfNull(luma);
        var counts = new int[4, Levels];
        for (var level = 0; level < Math.Min(luma.Length, Levels); level++)
        {
            for (var channel = 0; channel < 4; channel++)
                counts[channel, level] = luma[level];
        }

        return new YrgbHistogram(counts);
    }

    /// <summary>Pixels counted (the luma row's total).</summary>
    public int Pixels { get; }

    /// <summary>Pixels of <paramref name="channel"/> at <paramref name="level"/>.</summary>
    public int Count(int channel, int level) => _counts[channel, level];

    /// <summary>A copy of one channel's 256 counts.</summary>
    public int[] Row(int channel)
    {
        var row = new int[Levels];
        for (var level = 0; level < Levels; level++)
            row[level] = _counts[channel, level];
        return row;
    }

    /// <summary>Share (0..1) of the channel's pixels at level 0.</summary>
    public double CrushShare(int channel) => Pixels > 0 ? _counts[channel, 0] / (double)Pixels : 0;

    /// <summary>Share (0..1) of the channel's pixels at level 255.</summary>
    public double ClipShare(int channel) => Pixels > 0 ? _counts[channel, Levels - 1] / (double)Pixels : 0;

    /// <summary>True when at least <see cref="EdgeShare"/> of the channel sits at 0 %: its shadow-crush indicator lights.</summary>
    public bool Crushed(int channel) => Pixels > 0 && CrushShare(channel) >= EdgeShare;

    /// <summary>True when at least <see cref="EdgeShare"/> of the channel sits at 100 %: its highlight-clip indicator lights.</summary>
    public bool Clipped(int channel) => Pixels > 0 && ClipShare(channel) >= EdgeShare;

    /// <summary>
    /// The tallest count among the mode's channels over levels 1..254 (at least 1), which every shown channel's heights scale to, so the
    /// channels of one overlay compare directly.
    /// </summary>
    public int Peak(HistogramMode mode)
    {
        var peak = 1;
        foreach (var channel in ChannelsOf(mode))
        {
            for (var level = 1; level < Levels - 1; level++)
                peak = Math.Max(peak, _counts[channel, level]);
        }

        return peak;
    }

    /// <summary>A bin's height as a share of the graph (0..1): square-root scaled so sparse shadow bins stay visible, capped at 1.</summary>
    public double Height(int channel, int level, int peak) => Math.Min(Math.Sqrt(Math.Max(_counts[channel, level], 0) / (double)Math.Max(peak, 1)), 1);

    /// <summary>The channels a mode draws, back to front (YRGB: R, G, B, then the luma trace on top).</summary>
    public static IReadOnlyList<int> ChannelsOf(HistogramMode mode) => mode switch
    {
        HistogramMode.Rgb => RgbChannels,
        HistogramMode.Luma => LumaChannels,
        _ => YrgbChannels,
    };

    /// <summary>The pill label of a mode ("YRGB", "RGB", "Luma"): also its persisted name.</summary>
    public static string LabelOf(HistogramMode mode) => mode switch
    {
        HistogramMode.Rgb => "RGB",
        HistogramMode.Luma => "Luma",
        _ => "YRGB",
    };

    /// <summary>A persisted label (or enum name), case-insensitive, back to its mode; anything else is YRGB.</summary>
    public static HistogramMode Parse(string? name)
    {
        foreach (var mode in Modes)
        {
            if (string.Equals(name?.Trim(), LabelOf(mode), StringComparison.OrdinalIgnoreCase) || string.Equals(name?.Trim(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
                return mode;
        }

        return HistogramMode.Yrgb;
    }

    /// <summary>
    /// A pill click in one open pane (Color Studio or Video Scopes), already saved to AppSettings by it: the other panes adopt the mode
    /// without saving it again. Subscribers hold this static event only while loaded (UI thread only).
    /// </summary>
    public static event Action<HistogramMode, object?>? ModeChanged;

    /// <summary>Tells the other open panes a pill was clicked in <paramref name="origin"/> (which ignores its own notification).</summary>
    public static void Announce(HistogramMode mode, object? origin) => ModeChanged?.Invoke(mode, origin);
}
