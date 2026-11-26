using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

// ----- Speed & Time card per-clip options -----
// Look properties (not ClipFields), flushed into State with the look, serialized only when non-default (Layer.Serialization.cs), and
// preserved over track looks by TimelineLowering.Merged.
public sealed partial class Layer
{
    /// <summary>Smooth Slow-Mo: slower than 1x, the picture is drawn as optical-flow in-betweens instead of repeated frames.</summary>
    [ObservableProperty] private bool _smoothSlowMo;

    /// <summary>Keep Pitch: a sped-up or slowed-down sound keeps its pitch (on, as it always did); off, it plays like tape.</summary>
    [ObservableProperty] private bool _keepPitch = true;

    /// <summary>Spectral Audio: a stretched sound goes through the phase vocoder instead of WSOLA (smooth slow-motion sound).</summary>
    [ObservableProperty] private bool _spectralAudio;

    /// <summary>Takes the speed options of a saved layer (null: the default), as its look (ApplyLook).</summary>
    private void TakeSpeedOptions(LayerState state)
    {
        SmoothSlowMo = state.SmoothSlowMo == true;
        KeepPitch = state.KeepPitch != false;
        SpectralAudio = state.SpectralAudio == true;
    }

    // ----- Keyframed speed ramp (Pass 5.6.5 contract; Speed & Time card, Q7a) -----
    // Speed in x (SlowestSpeed..FastestSpeed) at clip-relative sequence seconds, eased toward the next key and held outside the first and
    // last like every keyframe; no keys = the constant Speed. A look value (LayerState.KeysSpeed, one of LayerState.MotionChannels), so
    // split, join, head trim and speed change carry it like motion keys. SpeedRamp is the one mapping from sequence time to file time for
    // picture, sound and captions. Changes raise SpeedKeys and HasSpeedRamp (a look change with no undo step of its own: callers
    // checkpoint first, as keyframe edits do).

    private readonly List<Keyframe> _speedKeys = [];

    /// <summary>The speed ramp's keys (value = speed in x), ascending; empty while the clip runs at its constant <see cref="Speed"/>.</summary>
    public IReadOnlyList<Keyframe> SpeedKeys => _speedKeys;

    /// <summary>True while the clip's speed is keyframed.</summary>
    public bool HasSpeedRamp => _speedKeys.Count > 0;

    /// <summary>Speed (x) at a clip-relative moment: the ramp's eased value, or the constant speed without a ramp.</summary>
    public double SpeedAt(double clipSeconds) => SpeedRamp.SpeedAt(_speedKeys, Speed, clipSeconds);

    /// <summary>File seconds played from the clip's start (its <see cref="MediaOffset"/>) to a clip-relative moment.</summary>
    public double MediaSecondsAt(double clipSeconds) => SpeedRamp.MediaSeconds(_speedKeys, Speed, clipSeconds);

    /// <summary>Replaces the ramp (null or empty: none) with <see cref="SpeedRamp.Normalized"/> keys; raises only when they differ.</summary>
    public void SetSpeedKeys(IEnumerable<Keyframe>? keys)
    {
        var normalized = SpeedRamp.Normalized(keys);
        if (normalized.SequenceEqual(_speedKeys))
            return;

        _speedKeys.Clear();
        _speedKeys.AddRange(normalized);
        OnPropertyChanged(nameof(SpeedKeys));
        OnPropertyChanged(nameof(HasSpeedRamp));
    }
}

/// <summary>A clip fitted to its speed ramp (Pass 5.7.1, <see cref="SpeedRamp.Fit"/>).</summary>
/// <param name="Keys">The ramp re-timed to the fitted length (times x <paramref name="Stretch"/>, speeds kept); empty at a constant speed.</param>
/// <param name="Length">Clip seconds that play the whole source span.</param>
/// <param name="Duration">The clip's new length: <paramref name="Length"/>, or the room when shorter (<paramref name="Clamped"/>).</param>
/// <param name="Speed">The base speed: the ramp's average over <paramref name="Length"/> (the constant speed without keys).</param>
/// <param name="Stretch">The keys' re-timing factor (span played after over before).</param>
/// <param name="Clamped">True when <paramref name="Length"/> ran past the room.</param>
public readonly record struct SpeedRampFit(IReadOnlyList<Keyframe> Keys, double Length, double Duration, double Speed, double Stretch, bool Clamped);

/// <summary>
/// Pure time mapping of a keyframed speed ramp (Pass 5.6.5 contract, shared by picture, sound, captions and the timeline). Speed s(t) is
/// <see cref="EasingCurves.ValueAt"/> over the keys (held outside them), held to <see cref="Layer.SlowestSpeed"/>..<see
/// cref="Layer.FastestSpeed"/>; without keys it is the constant speed. File seconds from the clip's start are its integral F(t) (negative
/// before the start): exact for Linear and Hold segments, Simpson's rule over <see cref="Panels"/> panels for eased ones. F is strictly
/// increasing (s is at least SlowestSpeed), so <see cref="ClipSeconds"/> inverts it.
/// </summary>
public static class SpeedRamp
{
    /// <summary>Simpson panels per eased segment (even).</summary>
    public const int Panels = 64;

    /// <summary>Keys as stored: finite time and value only, the value held to the speed range, ascending by time (ties keep their order).</summary>
    public static List<Keyframe> Normalized(IEnumerable<Keyframe>? keys) => keys is null
        ? []
        : [.. keys.Where(k => double.IsFinite(k.Time) && double.IsFinite(k.Value))
            .Select(k => k with { Value = Math.Clamp(k.Value, Layer.SlowestSpeed, Layer.FastestSpeed) }).OrderBy(k => k.Time)];

    /// <summary>Speed (x) at a clip-relative moment: the eased value of the keys, or <paramref name="constantSpeed"/> without keys.</summary>
    public static double SpeedAt(IReadOnlyList<Keyframe> keys, double constantSpeed, double clipSeconds) =>
        Held(keys.Count > 0 ? EasingCurves.ValueAt(keys, clipSeconds) : constantSpeed);

    /// <summary>File seconds played between the clip's start and a clip-relative moment (negative before the start).</summary>
    public static double MediaSeconds(IReadOnlyList<Keyframe> keys, double constantSpeed, double clipSeconds)
    {
        if (!double.IsFinite(clipSeconds))
            return 0;
        if (keys.Count == 0)
            return clipSeconds * Held(constantSpeed);
        return clipSeconds >= 0 ? Integral(keys, 0, clipSeconds) : -Integral(keys, clipSeconds, 0);
    }

    /// <summary>
    /// The clip-relative moment at which <paramref name="mediaSeconds"/> of the file have played (inverse of <see cref="MediaSeconds"/>),
    /// to 1e-9 s by bisection.
    /// </summary>
    public static double ClipSeconds(IReadOnlyList<Keyframe> keys, double constantSpeed, double mediaSeconds)
    {
        if (!double.IsFinite(mediaSeconds) || mediaSeconds == 0)
            return 0;
        if (keys.Count == 0)
            return mediaSeconds / Held(constantSpeed);

        // F(t) lies between t x Slowest and t x Fastest, which brackets the answer.
        var (low, high) = mediaSeconds > 0
            ? (mediaSeconds / Layer.FastestSpeed, mediaSeconds / Layer.SlowestSpeed)
            : (mediaSeconds / Layer.SlowestSpeed, mediaSeconds / Layer.FastestSpeed);
        for (var i = 0; i < 200 && high - low > 1e-9; i++)
        {
            var middle = (low + high) / 2;
            if (MediaSeconds(keys, constantSpeed, middle) < mediaSeconds)
                low = middle;
            else
                high = middle;
        }

        return (low + high) / 2;
    }

    /// <summary>
    /// Fits a clip to its ramp (Pass 5.7.1 auto-duration): the clip keeps playing <paramref name="sourceSpan"/> file seconds, so its keys
    /// (shaped over <paramref name="clipSeconds"/>) are re-timed by the span over what they play there (times only, speeds kept: the curve's
    /// shape over the clip stays) and the length is the exact integral's inverse (<see cref="ClipSeconds"/>). The base speed becomes the
    /// ramp's average (span / length), so the constant-speed bounds (MediaEnd, trims, a later speed change or Clear) agree with the ramp.
    /// Without keys the length is span / constant speed. A length longer than <paramref name="room"/> is clamped to it (the tail trimmed).
    /// </summary>
    public static SpeedRampFit Fit(IReadOnlyList<Keyframe>? keys, double constantSpeed, double clipSeconds, double sourceSpan,
        double room = double.PositiveInfinity)
    {
        var ramp = Normalized(keys);
        var held = Held(constantSpeed);
        if (!(clipSeconds > 0) || !double.IsFinite(clipSeconds) || !(sourceSpan > 0) || !double.IsFinite(sourceSpan))
            return new SpeedRampFit(ramp, clipSeconds, clipSeconds, held, 1, false);

        double length, stretch, speed;
        if (ramp.Count == 0)
        {
            (length, speed) = (sourceSpan / held, held);
            stretch = length / clipSeconds;
        }
        else
        {
            var played = MediaSeconds(ramp, held, clipSeconds);
            stretch = played > 1e-9 ? sourceSpan / played : 1;
            if (Math.Abs(stretch - 1) > 1e-12)
                ramp = [.. ramp.Select(k => k with { Time = Math.Round(k.Time * stretch, 6) })];
            length = ClipSeconds(ramp, held, sourceSpan);
            speed = Held(sourceSpan / length);
        }

        var clamped = double.IsFinite(room) && length > room + 1e-9;
        return new SpeedRampFit(ramp, length, clamped ? Math.Max(room, 0) : length, speed, stretch, clamped);
    }

    private static double Held(double speed) => double.IsFinite(speed) ? Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    // Integral of s over [from, to] (from <= to): held ends outside the keys, each key segment by its easing.
    private static double Integral(IReadOnlyList<Keyframe> keys, double from, double to)
    {
        var (first, last) = (keys[0], keys[^1]);
        var sum = 0.0;
        if (from < first.Time)
            sum += (Math.Min(to, first.Time) - from) * Held(first.Value);
        if (to > last.Time)
            sum += (to - Math.Max(from, last.Time)) * Held(last.Value);

        for (var i = 1; i < keys.Count; i++)
        {
            var (a, b) = (keys[i - 1], keys[i]);
            var (start, end) = (Math.Max(from, a.Time), Math.Min(to, b.Time));
            if (end > start)
                sum += Segment(a, b, start, end);
        }

        return sum;
    }

    // Integral of one key segment's speed over [start, end] inside it.
    private static double Segment(in Keyframe a, in Keyframe b, double start, double end)
    {
        var length = b.Time - a.Time;
        if (length < 1e-6 || a.Easing == EasingType.Hold)
            return (end - start) * Held(a.Value);

        if (a.Easing == EasingType.Linear && !a.IsPart)
            return (end - start) * (At(a, b, length, start) + At(a, b, length, end)) / 2;

        var step = (end - start) / Panels;
        var sum = At(a, b, length, start) + At(a, b, length, end);
        for (var i = 1; i < Panels; i++)
            sum += (i % 2 == 1 ? 4 : 2) * At(a, b, length, start + i * step);
        return sum * step / 3;
    }

    private static double At(in Keyframe a, in Keyframe b, double length, double time) =>
        Held(a.Value + (b.Value - a.Value) * EasingCurves.Evaluate(Math.Clamp((time - a.Time) / length, 0, 1), a));
}
