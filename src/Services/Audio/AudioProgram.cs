using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

// ----- Sound engine program -----
//
// An AudioProgram is everything audible in a sequence, built once from a SequenceExportState: each clip's sound from its own file and
// stream at its offset (J/L cuts, slips, gains, fades), each track's clips mixed as one strip, the voiceover, per-strip processing and
// ducking. Exports render the same program (ExportSoundPlan). Immutable: the engine swaps in a new one at the next buffer
// (WasapiAudioEngine.SetProgram), keeping decoded audio for voices whose timing did not change.
//
// Media time (AudioVoice.MediaStart) is the compositor's: seconds from the file's first video frame (FFmpeg pts minus the video's
// start_time, MediaStarts), or the audio's own time for files without video. BuildLayerAudio cuts on the same clock, so sound and
// picture refer to the same instant of the file.

/// <summary>
/// One audio stream of one file. <see cref="VideoStart"/> is the video start FFmpeg reported at probe time
/// (SequenceExportState.MediaStarts); NaN when unknown, in which case the decoder derives it (MP4 boxes, TS timestamps, else the
/// first video sample).
/// </summary>
/// <param name="Stream">Audio stream index, 0-based ("0:a:N").</param>
public sealed record AudioSource(string Path, int Stream)
{
    public double VideoStart { get; init; } = double.NaN;

    /// <summary>
    /// Channel pair read as left/right (surround tracks exported per pair: pair 0 = channels 1-2, pair 1 = 3-4, ...; a pair past the last
    /// channel is silent, a lone last channel plays on both sides); -1 downmixes the whole stream to stereo (playback default).
    /// </summary>
    public int ChannelPair { get; init; } = -1;

    public override string ToString() => ChannelPair < 0 ? $"{System.IO.Path.GetFileName(Path)} a:{Stream}" : $"{System.IO.Path.GetFileName(Path)} a:{Stream} pair {ChannelPair}";
}

/// <summary>Fade length in seconds and curve (EasingCurves; Linear matches FFmpeg's afade).</summary>
public readonly record struct AudioFade(double Seconds, EasingType Easing = EasingType.Linear)
{
    public bool IsOn => Seconds > 0.0005;
}

/// <summary>
/// One span of a file's sound on the sequence: from <see cref="SequenceStart"/> it plays the file from <see cref="MediaStart"/> at
/// <see cref="Rate"/> media seconds per sequence second (pitch preserved) until <see cref="SequenceEnd"/> or <see cref="MediaEnd"/>,
/// whichever comes first; silent wherever the file has no audio.
/// </summary>
public sealed class AudioVoice
{
    public required AudioSource Source { get; init; }
    public required double SequenceStart { get; init; }

    /// <summary>Infinity: until the file (or the sequence) ends.</summary>
    public double SequenceEnd { get; init; } = double.PositiveInfinity;

    public required double MediaStart { get; init; }
    public double MediaEnd { get; init; } = double.PositiveInfinity;

    /// <summary>
    /// Media seconds per sequence second: clip speed (and track tempo), pitch preserved (WSOLA). With a <see cref="Ramp"/>, its mean over
    /// the clip (timing then follows the ramp: <see cref="MediaAt"/>, <see cref="End"/>).
    /// </summary>
    public double Rate { get; init; } = 1;

    /// <summary>The clip's keyframed speed ramp (Layer.SpeedKeys) on the voice's clock; null at a constant <see cref="Rate"/>.</summary>
    public AudioSpeedRamp? Ramp { get; init; }

    /// <summary>The clip's hand-drawn ducking curve (Layer.DuckOverrideKeys) on a ducked sound; null for none.</summary>
    public DuckOverrideCurve? DuckOverride { get; init; }

    /// <summary>Pitch factor (track Pitch filter: 2^(semitones/12)); timing unchanged.</summary>
    public double Pitch { get; init; } = 1;

    /// <summary>When stretched (tempo differs from pitch), uses the phase vocoder instead of WSOLA (the clip's Spectral Audio).</summary>
    public bool Spectral { get; init; }

    /// <summary>
    /// Voice gain factor: the clip gain on a <see cref="AudioStripKind.Track"/> strip (track gain lives in the strip effects); 1 on a
    /// <see cref="AudioStripKind.Clip"/> strip, whose effects carry clip and track gain.
    /// </summary>
    public float Gain { get; init; } = 1;

    /// <summary>Fade in from <see cref="SequenceStart"/>.</summary>
    public AudioFade FadeIn { get; init; }

    /// <summary>Fade out ending at <see cref="FadeOutEnd"/>.</summary>
    public AudioFade FadeOut { get; init; }

    public double FadeOutEnd { get; init; } = double.PositiveInfinity;

    /// <summary>Index of the strip it mixes into (<see cref="AudioProgram.Strips"/>).</summary>
    public required int Strip { get; init; }

    /// <summary>Log label ("clip game.mkv", "voiceover").</summary>
    public string Label { get; init; } = "";

    /// <summary>Sequence time where it stops: its end, or where the file's playable media runs out.</summary>
    public double End => Math.Min(SequenceEnd, double.IsPositiveInfinity(MediaEnd) ? double.PositiveInfinity
        : SequenceStart + (Ramp is { } ramp ? ramp.SoundSeconds(MediaEnd - MediaStart) : (MediaEnd - MediaStart) / Rate));

    /// <summary>Media time heard at a sequence time (through the <see cref="Ramp"/> when keyframed).</summary>
    public double MediaAt(double sequenceSeconds) =>
        MediaStart + (Ramp is { } ramp ? ramp.MediaSeconds(sequenceSeconds - SequenceStart) : (sequenceSeconds - SequenceStart) * Rate);

    /// <summary>True when audible anywhere in [from, to).</summary>
    public bool Overlaps(double from, double to) => SequenceStart < to && End > from;
}

public enum AudioStripKind
{
    /// <summary>
    /// A track's clips: each clip's sound with its gain and fades, summed into one continuous strip, ducked, then the track's gain and
    /// processing (export order; processing state carries across cuts).
    /// </summary>
    Track,

    /// <summary>A clip with its own processing: processing and gain, then fades, then ducking (BuildLayerAudio order).</summary>
    Clip,

    /// <summary>The session's voiceover, processed like a clip.</summary>
    Voiceover,
}

/// <summary>
/// A mixer strip: its voices, its ordered processing chain and its ducking role. <see cref="Key"/> is stable across edits so
/// processing keeps its state (no click when a slider moves).
/// </summary>
public sealed class AudioStrip
{
    public required string Key { get; init; }
    public required AudioStripKind Kind { get; init; }

    /// <summary>Voice strip: the side chain that ducked strips make room for.</summary>
    public bool IsVoice { get; init; }

    /// <summary>Attenuated while a voice is active (Auto-Duck, the track's Duck Against Voice).</summary>
    public bool Ducked { get; init; }

    public IReadOnlyList<AudioEffectSpec> Effects { get; init; } = [];

    public string Label { get; init; } = "";

    /// <summary>
    /// Output bus (multi-track exports render in one pass via AudioMixer.RenderTo with buses); 0 = the main mix. Ducking still listens
    /// to every voice on any bus.
    /// </summary>
    public int Bus { get; init; }

    /// <summary>Voice fades and gains apply after processing (clips), not before (tracks).</summary>
    public bool EnvelopeAfterEffects => Kind != AudioStripKind.Track;
}

/// <summary>
/// Ducking parameters in FFmpeg sidechaincompress terms; the one-control amount only determines <see cref="Mix"/>.
/// </summary>
public sealed record DuckParameters
{
    public double Threshold { get; init; } = 0.02;
    public double Ratio { get; init; } = 20;
    public double AttackMs { get; init; } = 15;
    public double ReleaseMs { get; init; } = 350;
    public double SideChainGain { get; init; } = 16;
    public double Knee { get; init; } = 2.82843;
    public double Makeup { get; init; } = 1;

    /// <summary>Compressed-signal mix: 1 - 10^(amount / 20), which yields a dip of the requested amount.</summary>
    public double Mix { get; init; } = 1 - Math.Pow(10, -15 / 20.0);

    /// <summary>Ducking for SequenceExportState.DuckAmountDb (clamped to -40..-1 dB).</summary>
    public static DuckParameters ForAmount(double amountDb) => new() { Mix = 1 - Math.Pow(10, Math.Clamp(amountDb, -40, -1) / 20) };
}

/// <summary>Options for <see cref="AudioProgram.Build(SequenceExportState, AudioBuildOptions)"/> beyond the state.</summary>
public sealed record AudioBuildOptions
{
    public static AudioBuildOptions Default { get; } = new();

    /// <summary>Raw ducking parameters; null derives them from the state's amount.</summary>
    public DuckParameters? Duck { get; init; }

    /// <summary>
    /// Bus per strip (multi-track export, ExportSoundPlan); null puts everything on bus 0, as in playback. Sub-Lane audio whose stream
    /// slot has no bus in the layout is not rendered (the export copies or omits it).
    /// </summary>
    public AudioBusLayout? Buses { get; init; }

    /// <summary>
    /// True ignores Solo (export). False (editor): while any track is soloed (TimelineTrackModel.IsSoloed), only soloed tracks and their
    /// Sub-Lanes are heard, and the voiceover is muted.
    /// </summary>
    public bool IgnoresSolo { get; init; }
}

/// <summary>Everything audible in a sequence; immutable (see the file header).</summary>
public sealed class AudioProgram
{
    private static long _serials;

    internal AudioProgram(double sequenceSeconds, AudioVoice[] voices, AudioStrip[] strips, bool fadeIn, bool fadeOut, DuckParameters duck)
    {
        (SequenceSeconds, VoiceArray, StripArray, FadeIn, FadeOut, Duck) = (Math.Max(sequenceSeconds, 0), voices, strips, fadeIn, fadeOut, duck);
        Serial = Interlocked.Increment(ref _serials);
    }

    /// <summary>
    /// Maximum effect latency across strips, in samples (AudioEffect.LatencyOf): offline renders start mixing this early so processed
    /// clips get their pre-roll.
    /// </summary>
    internal int MostLatencyAt(int sampleRate)
    {
        var most = 0;
        foreach (var strip in StripArray)
            most = Math.Max(most, AudioEffect.LatencyOf([.. strip.Effects], sampleRate));
        return most;
    }

    /// <summary>Silent program; the engine still runs its clock.</summary>
    public static AudioProgram Empty { get; } = new(0, [], [], false, false, new DuckParameters());

    /// <summary>Builds the program for a state (AudioProgramBuilder). Cheap enough to run on every edit.</summary>
    public static AudioProgram Build(SequenceExportState state) => AudioProgramBuilder.Build(state, AudioBuildOptions.Default);

    public static AudioProgram Build(SequenceExportState state, AudioBuildOptions options) => AudioProgramBuilder.Build(state, options);

    /// <summary>Sequence length; nothing is heard past it.</summary>
    public double SequenceSeconds { get; }

    public IReadOnlyList<AudioVoice> Voices => VoiceArray;
    public IReadOnlyList<AudioStrip> Strips => StripArray;

    internal AudioVoice[] VoiceArray { get; }
    internal AudioStrip[] StripArray { get; }

    /// <summary>Sequence fade in over the first second and out over the last (SequenceExportState.FadeIn / FadeOut).</summary>
    public bool FadeIn { get; }

    public bool FadeOut { get; }

    public const double FadeSeconds = 1;

    public DuckParameters Duck { get; }

    /// <summary>
    /// Sequence master fader factor (SequenceExportState.MasterGainDb), applied on every bus after strip summing and before the sequence
    /// fades (AudioMixer); exactly 1 at 0 dB.
    /// </summary>
    public float MasterGain { get; init; } = 1;

    /// <summary>Monotonic unique id identifying the program in logs and tests.</summary>
    public long Serial { get; }
}

/// <summary>
/// A clip's hand-drawn ducking curve on the sequence (Layer.DuckOverrideKeys: reduction in dB, Layer.DuckOverrideFloorDb..0, eased between
/// keys at seconds from <see cref="Origin"/>, the clip's sequence start). From its first key to its last it replaces the automatic ducking
/// of that clip's sound outright; over <see cref="EdgeSeconds"/> outside them the gain glides back to the side chain's (no click); beyond,
/// the voices duck it as before (AudioMixer). Immutable.
/// </summary>
public sealed class DuckOverrideCurve
{
    /// <summary>Hand-over glide between the automatic and the drawn gain just outside the curve's ends.</summary>
    public const double EdgeSeconds = 0.005;

    private readonly Keyframe[] _keys;

    /// <param name="keys">Layer.DuckOverrideKeys (clip-relative seconds, dB).</param>
    /// <param name="origin">The clip's sequence start (clip-relative 0).</param>
    public DuckOverrideCurve(IEnumerable<Keyframe> keys, double origin)
    {
        _keys = [.. keys.Where(k => double.IsFinite(k.Time) && double.IsFinite(k.Value))
            .Select(k => k with { Value = Math.Clamp(k.Value, Layer.DuckOverrideFloorDb, 0) }).OrderBy(k => k.Time)];
        Origin = double.IsFinite(origin) ? origin : 0;
    }

    /// <summary>Sequence time of clip-relative 0.</summary>
    public double Origin { get; }

    public IReadOnlyList<Keyframe> Keys => _keys;

    /// <summary>Sequence times of the first and last key (the span the curve rules); an empty curve rules nowhere.</summary>
    public double From => _keys.Length > 0 ? Origin + _keys[0].Time : double.PositiveInfinity;

    public double To => _keys.Length > 0 ? Origin + _keys[^1].Time : double.NegativeInfinity;

    /// <summary>True when the curve (or its edge glide) touches [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public bool Overlaps(double from, double to) => From - EdgeSeconds < to && To + EdgeSeconds >= from;

    /// <summary>Drawn reduction (dB) at a sequence time; null outside the first and last key (Layer.DuckOverrideAt on the sequence).</summary>
    public double? DecibelsAt(double sequenceSeconds) =>
        sequenceSeconds >= From && sequenceSeconds <= To ? Math.Clamp(EasingCurves.ValueAt(_keys, sequenceSeconds - Origin), Layer.DuckOverrideFloorDb, 0) : null;

    /// <summary>
    /// Gain at a sequence time given the automatic ducking factor there (<paramref name="automatic"/>, 1 when nothing ducks): the drawn
    /// curve's between its ends, gliding to <paramref name="automatic"/> over <see cref="EdgeSeconds"/> outside them, else the automatic.
    /// </summary>
    public float GainAt(double sequenceSeconds, float automatic)
    {
        var outside = Math.Max(From - sequenceSeconds, sequenceSeconds - To);
        if (!(outside < EdgeSeconds))
            return automatic;
        var held = Math.Clamp(sequenceSeconds, From, To) - Origin;
        var drawn = Math.Pow(10, Math.Clamp(EasingCurves.ValueAt(_keys, held), Layer.DuckOverrideFloorDb, 0) / 20);
        var weight = outside <= 0 ? 1 : 1 - outside / EdgeSeconds;
        return (float)(automatic + (drawn - automatic) * weight);
    }
}

/// <summary>
/// A voice's keyframed speed ramp on its own clock (seconds since its sequence start; the clip's slip only shifts it): media seconds played
/// since the voice's <see cref="AudioVoice.MediaStart"/> are the contract's SpeedRamp.MediaSeconds times the strip's net Pitch/Tempo factor.
/// Tabulated once (at every key and every <see cref="Step"/> between keys, each cell by 3-point Gauss-Legendre at interior points, so held
/// segments stay exact and the table matches the contract at the keys); the held speeds before the first key and after the last extrapolate
/// linearly. Lookups (<see cref="MediaSeconds"/>, its inverse <see cref="SoundSeconds"/>) are allocation-free. Immutable.
/// </summary>
public sealed class AudioSpeedRamp
{
    /// <summary>Table step between keys, seconds (2 ms: well under a sample of drift for any ramp in 0.1x..16x).</summary>
    public const double Step = 0.002;

    private const int MostNodes = 200_000;

    private readonly Keyframe[] _keys;
    private readonly double _constant, _net, _first, _last, _before, _after, _zero;
    private readonly double[] _times, _media;

    /// <param name="keys">Layer.SpeedKeys (clip-relative seconds, speed in x).</param>
    /// <param name="constantSpeed">Layer.Speed (used without keys).</param>
    /// <param name="net">The strip's net Pitch/Tempo rate factor (1 without one).</param>
    public AudioSpeedRamp(IEnumerable<Keyframe> keys, double constantSpeed, double net)
    {
        _keys = [.. SpeedRamp.Normalized(keys)];
        (_constant, _net) = (constantSpeed, double.IsFinite(net) && net > 0 ? net : 1);
        if (_keys.Length == 0)
        {
            (_first, _last, _times, _media, _zero) = (0, 0, [0], [0], 0);
            _before = _after = SpeedRamp.SpeedAt(_keys, _constant, 0) * _net;
            return;
        }

        (_first, _last) = (_keys[0].Time, _keys[^1].Time);
        (_before, _after) = (SpeedRamp.SpeedAt(_keys, _constant, _first - 1) * _net, SpeedRamp.SpeedAt(_keys, _constant, _last + 1) * _net);
        var step = Math.Max(Step, (_last - _first) / MostNodes);
        var times = new List<double> { _first };
        var media = new List<double> { SpeedRamp.MediaSeconds(_keys, _constant, _first) * _net };
        for (var k = 1; k < _keys.Length; k++)
        {
            var (a, b) = (_keys[k - 1].Time, _keys[k].Time);
            var cells = Math.Max((int)Math.Ceiling((b - a) / step - 1e-9), 1);
            for (var c = 1; c <= cells; c++)
            {
                var (from, to) = (times[^1], c == cells ? b : a + (b - a) * c / cells);
                if (to <= from)
                    continue;
                times.Add(to);
                media.Add(c == cells ? SpeedRamp.MediaSeconds(_keys, _constant, b) * _net : media[^1] + Cell(from, to));
            }
        }

        (_times, _media) = (times.ToArray(), media.ToArray());

        // Exactly 0 at the voice's start (a key before clip-relative 0 puts it inside a cell).
        _zero = MediaAtTable(0);
    }

    // Integral of the speed (x net) over one cell inside a key segment: 3-point Gauss-Legendre at interior points only.
    private double Cell(double from, double to)
    {
        var (middle, half) = ((from + to) / 2, (to - from) / 2);
        var offset = half * Math.Sqrt(0.6);
        return half * _net * (5.0 / 9 * SpeedRamp.SpeedAt(_keys, _constant, middle - offset) + 8.0 / 9 * SpeedRamp.SpeedAt(_keys, _constant, middle)
                              + 5.0 / 9 * SpeedRamp.SpeedAt(_keys, _constant, middle + offset));
    }

    /// <summary>The ramp's keys (normalized as Layer.SetSpeedKeys keeps them).</summary>
    public IReadOnlyList<Keyframe> Keys => _keys;

    /// <summary>Media rate (speed x net) at a moment of the voice's clock.</summary>
    public double RateAt(double soundSeconds) => SpeedRamp.SpeedAt(_keys, _constant, soundSeconds) * _net;

    /// <summary>Fastest and slowest media rate anywhere on the ramp (the held ends included).</summary>
    public double MostRate => _keys.Length == 0 ? _before : _keys.Max(k => k.Value) * _net;

    public double LeastRate => _keys.Length == 0 ? _before : _keys.Min(k => k.Value) * _net;

    /// <summary>Media seconds played from the voice's start to a moment of its clock (negative before it).</summary>
    public double MediaSeconds(double soundSeconds) => double.IsFinite(soundSeconds) ? MediaAtTable(soundSeconds) - _zero : 0;

    /// <summary>The moment of the voice's clock at which <paramref name="mediaSeconds"/> have played (inverse of <see cref="MediaSeconds"/>).</summary>
    public double SoundSeconds(double mediaSeconds)
    {
        if (!double.IsFinite(mediaSeconds))
            return mediaSeconds;
        var (times, media) = (_times, _media);
        var wanted = mediaSeconds + _zero;
        if (wanted <= media[0])
            return times[0] - (media[0] - wanted) / _before;
        if (wanted >= media[^1])
            return times[^1] + (wanted - media[^1]) / _after;
        var at = IndexIn(media, wanted);
        var u = (wanted - media[at]) / Math.Max(media[at + 1] - media[at], 1e-15);
        return times[at] + (times[at + 1] - times[at]) * u;
    }

    // The table's media value at a clock moment: the integral from clip-relative 0, the held speeds extrapolated outside the keys.
    private double MediaAtTable(double soundSeconds)
    {
        var (times, media) = (_times, _media);
        if (soundSeconds <= times[0])
            return media[0] - (times[0] - soundSeconds) * _before;
        if (soundSeconds >= times[^1])
            return media[^1] + (soundSeconds - times[^1]) * _after;
        var at = IndexIn(times, soundSeconds);
        return media[at] + (media[at + 1] - media[at]) * (soundSeconds - times[at]) / (times[at + 1] - times[at]);
    }

    // Index of the table cell [values[i], values[i + 1]) holding a value inside the table (binary search; values ascending).
    private static int IndexIn(double[] values, double value)
    {
        var (low, high) = (0, values.Length - 1);
        while (high - low > 1)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] <= value)
                low = middle;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>Identity of the mapping (keys, constant speed, net factor) for keying decoded audio (AudioMixer.VoiceKey).</summary>
    public string Identity()
    {
        var hash = new HashCode();
        foreach (var key in _keys)
        {
            hash.Add(key.Time);
            hash.Add(key.Value);
            hash.Add(key.Easing);
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ramp{_keys.Length}:{hash.ToHashCode():x8}:{_constant:R}:{_net:R}");
    }
}
