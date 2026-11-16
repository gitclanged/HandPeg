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

    /// <summary>Media seconds per sequence second: clip speed (and track tempo), pitch preserved (WSOLA).</summary>
    public double Rate { get; init; } = 1;

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
    public double End => Math.Min(SequenceEnd, double.IsPositiveInfinity(MediaEnd) ? double.PositiveInfinity : SequenceStart + (MediaEnd - MediaStart) / Rate);

    /// <summary>Media time heard at a sequence time.</summary>
    public double MediaAt(double sequenceSeconds) => MediaStart + (sequenceSeconds - SequenceStart) * Rate;

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
