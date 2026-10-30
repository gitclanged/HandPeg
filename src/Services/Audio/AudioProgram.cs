using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

// ----- What is heard: the sound engine's program (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// An AudioProgram is everything audible in a sequence, worked out once from a SequenceExportState with the semantics the
// FFmpeg path writes into its graphs (FilterGraphBuilder.Audio.cs): the main file's audio streams placed as its clips'
// sound is (J and L cuts, slips, gains, fades), every other clip's sound, the voiceover, each strip's processing and the
// ducking. It is immutable: the engine takes a new one whole (WasapiAudioEngine.SetProgram) at the next buffer, and what
// it already decoded for a voice whose timing did not change is kept.
//
// Media time (AudioVoice.MediaStart) is the GPU compositor's: seconds from the file's first video frame (FFmpeg's pts less
// the video's start_time, MediaStarts), or, for a file without pictures, the sound's own time. That is the clock a layer's
// sound is cut on in BuildLayerAudio (origin + into), so a sound and its picture are one moment of the file.

/// <summary>
/// One audio stream of one file. <see cref="VideoStart"/> is where FFmpeg found its picture to start when the file was
/// inspected (SequenceExportState.MediaStarts); NaN when not known, and the decoder then learns it itself (an MP4's boxes,
/// a transport stream's time stamps, else the file's first video sample).
/// </summary>
/// <param name="Stream">Which audio stream: 0 the first ("0:a:N").</param>
public sealed record AudioSource(string Path, int Stream)
{
    public double VideoStart { get; init; } = double.NaN;

    public override string ToString() => $"{System.IO.Path.GetFileName(Path)} a:{Stream}";
}

/// <summary>A fade's length in seconds and its curve (EasingCurves; Linear is FFmpeg's afade, which the export uses).</summary>
public readonly record struct AudioFade(double Seconds, EasingType Easing = EasingType.Linear)
{
    public bool IsOn => Seconds > 0.0005;
}

/// <summary>
/// One stretch of one file's sound on the sequence. From <see cref="SequenceStart"/> it plays the file from
/// <see cref="MediaStart"/> at <see cref="Rate"/> media seconds a sequence second (pitch kept), until
/// <see cref="SequenceEnd"/> or until the file reaches <see cref="MediaEnd"/>, whichever is first; silence wherever the
/// file has nothing (before its start, past its end).
/// </summary>
public sealed class AudioVoice
{
    public required AudioSource Source { get; init; }
    public required double SequenceStart { get; init; }

    /// <summary>Infinity: until the file ends (or the sequence does).</summary>
    public double SequenceEnd { get; init; } = double.PositiveInfinity;

    public required double MediaStart { get; init; }
    public double MediaEnd { get; init; } = double.PositiveInfinity;

    /// <summary>Media seconds a sequence second: the clip's speed (and a track's tempo), pitch kept (WSOLA).</summary>
    public double Rate { get; init; } = 1;

    /// <summary>Its pitch as a factor (a track's Pitch filter: 2^(semitones/12)); the timing stays.</summary>
    public double Pitch { get; init; } = 1;

    /// <summary>Its own gain as a factor: a main clip's (and its track's) gain; a clip's is in its strip's effects, where FFmpeg has it.</summary>
    public float Gain { get; init; } = 1;

    /// <summary>In from <see cref="SequenceStart"/>.</summary>
    public AudioFade FadeIn { get; init; }

    /// <summary>Out, ending at <see cref="FadeOutEnd"/>.</summary>
    public AudioFade FadeOut { get; init; }

    public double FadeOutEnd { get; init; } = double.PositiveInfinity;

    /// <summary>Stretches of the sequence where it is silenced (a track's muted pieces, in sequence seconds), cut hard as FFmpeg's volume=0 does.</summary>
    public IReadOnlyList<(double Start, double End)> Silenced { get; init; } = [];

    /// <summary>The strip it is mixed into (<see cref="AudioProgram.Strips"/>).</summary>
    public required int Strip { get; init; }

    /// <summary>What it is, for the log ("main a:0 clip 3", "clip music.mp3").</summary>
    public string Label { get; init; } = "";

    /// <summary>Where it stops: at its end, or where its file runs out of what it may play.</summary>
    public double End => Math.Min(SequenceEnd, double.IsPositiveInfinity(MediaEnd) ? double.PositiveInfinity : SequenceStart + (MediaEnd - MediaStart) / Rate);

    /// <summary>The media moment heard at a moment of the sequence.</summary>
    public double MediaAt(double sequenceSeconds) => MediaStart + (sequenceSeconds - SequenceStart) * Rate;

    /// <summary>Whether it is heard anywhere in [from, to).</summary>
    public bool Overlaps(double from, double to) => SequenceStart < to && End > from;
}

public enum AudioStripKind
{
    /// <summary>One of the main file's audio streams: its clips' sound summed, ducked, then its track's processing (the export's order).</summary>
    MainTrack,

    /// <summary>A clip's sound: its processing and gain, then its fades, then ducked (BuildLayerAudio's order).</summary>
    Clip,

    /// <summary>The session's voiceover: as a clip.</summary>
    Voiceover,
}

/// <summary>
/// A line of the mixer: the voices mixed into it, the processing it applies (in order), and its part in ducking. Its
/// <see cref="Key"/> stays the same across edits so that its processing keeps its state (no click when a slider moves).
/// </summary>
public sealed class AudioStrip
{
    public required string Key { get; init; }
    public required AudioStripKind Kind { get; init; }

    /// <summary>A voice: what the ducked strips make room for (its sound is the side chain).</summary>
    public bool IsVoice { get; init; }

    /// <summary>Turned down while a voice speaks (Auto-Duck, the track's Duck Against Voice).</summary>
    public bool Ducked { get; init; }

    public IReadOnlyList<AudioEffectSpec> Effects { get; init; } = [];

    /// <summary>Filters of its chain this engine does not play (afftdn...), as FFmpeg names them: the export still has them.</summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    public string Label { get; init; } = "";

    /// <summary>Its voices' fades and gains come after its processing (a clip's), not before (a track's).</summary>
    public bool EnvelopeAfterEffects => Kind != AudioStripKind.MainTrack;
}

/// <summary>
/// The ducking, as FFmpeg's sidechaincompress takes it (FilterGraphBuilder.DuckFilter): every raw parameter, the 1-click
/// amount being only how <see cref="Mix"/> is worked out.
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

    /// <summary>The share of the pressed-down sound: 1 - 10^(amount / 20), which makes the dip the amount asked for.</summary>
    public double Mix { get; init; } = 1 - Math.Pow(10, -15 / 20.0);

    /// <summary>The ducking for SequenceExportState.DuckAmountDb, as the FFmpeg path writes it (the amount held to -40..-1 dB).</summary>
    public static DuckParameters ForAmount(double amountDb) => new() { Mix = 1 - Math.Pow(10, Math.Clamp(amountDb, -40, -1) / 20) };
}

/// <summary>What <see cref="AudioProgram.Build(SequenceExportState, AudioBuildOptions)"/> may be told besides the state.</summary>
public sealed record AudioBuildOptions
{
    public static AudioBuildOptions Default { get; } = new();

    /// <summary>
    /// The main file's audio streams played on their own (their "0:a:N" numbers, MainViewModel.SoloedTracks): one or more.
    /// The rest of the file's streams, every clip's sound and the voiceover are left out, as MainViewModel.Soloed has it.
    /// Null or empty: no solo.
    /// </summary>
    public IReadOnlyCollection<int>? SoloStreams { get; init; }

    /// <summary>
    /// Seconds added to the main file's media times, beyond the shift both this and the GPU scene make (the main clips'
    /// offsets are on the container's clock, media time from the first video frame: GpuScenePlan.ChainShift). 0 normally;
    /// kept for a measured correction (5.0-Verify's A/V check).
    /// </summary>
    public double MainMediaShift { get; init; }

    /// <summary>The ducking's raw parameters; null for the FFmpeg path's own, from the state's amount.</summary>
    public DuckParameters? Duck { get; init; }
}

/// <summary>Everything audible in a sequence, immutable (see the top of this file).</summary>
public sealed class AudioProgram
{
    private static long _serials;

    internal AudioProgram(double sequenceSeconds, AudioVoice[] voices, AudioStrip[] strips, bool fadeIn, bool fadeOut, DuckParameters duck)
    {
        (SequenceSeconds, VoiceArray, StripArray, FadeIn, FadeOut, Duck) = (Math.Max(sequenceSeconds, 0), voices, strips, fadeIn, fadeOut, duck);
        Serial = Interlocked.Increment(ref _serials);
    }

    /// <summary>Nothing to hear: the engine still runs its clock.</summary>
    public static AudioProgram Empty { get; } = new(0, [], [], false, false, new DuckParameters());

    /// <summary>The program of a state, with the FFmpeg path's semantics (AudioProgramBuilder). Cheap: run it on every edit.</summary>
    public static AudioProgram Build(SequenceExportState state) => AudioProgramBuilder.Build(state, AudioBuildOptions.Default);

    public static AudioProgram Build(SequenceExportState state, AudioBuildOptions options) => AudioProgramBuilder.Build(state, options);

    /// <summary>Nothing is heard past it (amix's duration=first: the main track, padded to the sequence's end).</summary>
    public double SequenceSeconds { get; }

    public IReadOnlyList<AudioVoice> Voices => VoiceArray;
    public IReadOnlyList<AudioStrip> Strips => StripArray;

    internal AudioVoice[] VoiceArray { get; }
    internal AudioStrip[] StripArray { get; }

    /// <summary>The sequence's fade in over its first second, and out over its last (SequenceExportState.FadeIn / FadeOut).</summary>
    public bool FadeIn { get; }

    public bool FadeOut { get; }

    public const double FadeSeconds = 1;

    public DuckParameters Duck { get; }

    /// <summary>Unique and growing: which program a log line or a test speaks of.</summary>
    public long Serial { get; }
}
