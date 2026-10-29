using System.Globalization;
using System.Text.Json.Serialization;

namespace HandPegApp.Models.Timeline;

/// <summary>
/// A frame rate as an exact fraction: 30000/1001 is NTSC's 29.97, 60/1 is 60. Times on a <see cref="TimelineDocument"/>
/// are seconds, and while it quantizes they lie on this rate's frames: a frame's time is one exact double
/// (<see cref="Quantize"/>), so two clips that touch at a frame hold bit-identical edges.
/// </summary>
/// <remarks>A rate that is not positive (a default value, a file without one) counts as 60/1. Written to projects as its two numbers.</remarks>
public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public static readonly FrameRate Ntsc23976 = new(24000, 1001);
    public static readonly FrameRate Fps24 = new(24, 1);
    public static readonly FrameRate Fps25 = new(25, 1);
    public static readonly FrameRate Ntsc2997 = new(30000, 1001);
    public static readonly FrameRate Fps30 = new(30, 1);
    public static readonly FrameRate Fps50 = new(50, 1);
    public static readonly FrameRate Ntsc5994 = new(60000, 1001);
    public static readonly FrameRate Fps60 = new(60, 1);
    public static readonly FrameRate Ntsc11988 = new(120000, 1001);
    public static readonly FrameRate Fps120 = new(120, 1);

    /// <summary>The rates <see cref="FromFps"/> knows by name, the NTSC ones as their exact fractions.</summary>
    public static IReadOnlyList<FrameRate> Standard { get; } =
        [Ntsc23976, Fps24, Fps25, Ntsc2997, Fps30, Fps50, Ntsc5994, Fps60, Ntsc11988, Fps120];

    /// <summary>How near a rate has to be to a standard one to be taken as it: 0.5% (29.92, the average of a variable-rate 29.97 recording, is 29.97).</summary>
    private const double Nearness = 0.005;

    /// <summary>The slowest and fastest rates a project can have, in frames per second.</summary>
    public const double Slowest = 1, Fastest = 1000;

    [JsonIgnore]
    public bool IsValid => Numerator > 0 && Denominator > 0;

    // The fraction the arithmetic uses: this one, or 60/1 for one that is not positive.
    private int N => IsValid ? Numerator : 60;
    private int D => IsValid ? Denominator : 1;

    [JsonIgnore]
    public double Fps => N / (double)D;

    /// <summary>Seconds per frame.</summary>
    [JsonIgnore]
    public double FrameDuration => D / (double)N;

    /// <summary>
    /// The rate meant by a number of frames per second: the standard rate nearest it, within 0.5% (23.976, 29.97, 59.94
    /// and 119.88 are the NTSC fractions); otherwise a whole number from 1 to 1000 over 1 (48 is 48/1). Anything else is 60/1.
    /// </summary>
    public static FrameRate FromFps(double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0)
            return Fps60;

        var nearest = Standard.MinBy(rate => Math.Abs(rate.Fps - fps));
        if (Math.Abs(nearest.Fps - fps) <= nearest.Fps * Nearness)
            return nearest;

        var whole = Math.Round(fps);
        return Math.Abs(fps - whole) < 0.01 && whole is >= Slowest and <= Fastest ? new FrameRate((int)whole, 1) : Fps60;
    }

    /// <summary>
    /// A source's own rate, kept as it is, for a canvas that ticks with the source's frames (4.3B): an NTSC rate within a
    /// hundredth of a frame a second is its exact fraction (29.97 is 30000/1001), any other is the number itself to a
    /// thousandth, reduced (47.952 is 5994/125, 12.5 is 25/2, 30 is 30/1), never snapped to a standard one, as
    /// <see cref="FromFps"/> snaps a project's rate. 30/1 for a rate that is not one (0, NaN), within 1 to 1000.
    /// </summary>
    public static FrameRate OfSource(double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0)
            return Fps30;

        foreach (var ntsc in (FrameRate[])[Ntsc23976, Ntsc2997, Ntsc5994, Ntsc11988])
        {
            if (Math.Abs(ntsc.Fps - fps) < 0.01)
                return ntsc;
        }

        var thousandths = (int)Math.Round(Math.Clamp(fps, Slowest, Fastest) * 1000);
        var common = (int)System.Numerics.BigInteger.GreatestCommonDivisor(thousandths, 1000);
        return new FrameRate(thousandths / common, 1000 / common);
    }

    /// <summary>
    /// Reads "30000/1001" or "60/1" (reduced: "60000/2000" is 30/1), or "29.97" / "60" when <see cref="FromFps"/> knows the
    /// number (invariant culture). False for anything else, or a rate outside 1 to 1000 frames per second.
    /// </summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = Fps60;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || !(fps is >= Slowest and <= Fastest))
                return false;
            var known = FromFps(fps);
            if (Math.Abs(known.Fps - fps) > known.Fps * Nearness)
                return false;
            rate = known;
            return true;
        }

        if (!int.TryParse(text.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0 || denominator <= 0 || !(numerator / (double)denominator is >= Slowest and <= Fastest))
            return false;

        var common = (int)System.Numerics.BigInteger.GreatestCommonDivisor(numerator, denominator);
        rate = new FrameRate(numerator / common, denominator / common);
        return true;
    }

    /// <summary>The frame nearest a moment (halves away from zero); 0 for one before the start or not a number.</summary>
    public long FrameFromSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return 0;

        // Past what a long holds (some 10^11 years at 1000 fps) it stays at the last frame there is.
        var frame = Math.Round(seconds * N / D, MidpointRounding.AwayFromZero);
        return frame >= 9e18 ? (long)9e18 : (long)frame;
    }

    public double SecondsFromFrame(long frame) => frame * (double)D / N;

    /// <summary>
    /// The moment of the frame nearest <paramref name="seconds"/>, rounded to the microsecond: the one double every edit
    /// that lands on that frame gets, whatever arithmetic led there. Unchanged while <paramref name="enabled"/> is false.
    /// </summary>
    public double Quantize(double seconds, bool enabled = true) =>
        enabled ? Math.Round(SecondsFromFrame(FrameFromSeconds(seconds)), 6, MidpointRounding.AwayFromZero) : seconds;

    /// <summary>Whether a moment is exactly a frame's quantized time (<see cref="Quantize"/> leaves it as it is).</summary>
    public bool IsOnFrame(double seconds) => Quantize(seconds) == seconds;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{N}/{D}");
}
