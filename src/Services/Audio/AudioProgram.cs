using HandPegApp.Models;

namespace HandPegApp.Services.Audio;

// ----- What is heard: the sound engine's program (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// An AudioProgram is everything audible in a sequence, worked out once from a SequenceExportState with the semantics the
// FFmpeg path wrote into its graphs until 5.2-Audit (now the export's sound is this program, rendered: ExportSoundPlan): every
// heard clip's sound from its own file and stream at its own offset (J and L cuts, slips, gains, fades), each track's clips
// processed as one strip (5.2-AuditB: no file is the "main" one), the voiceover, each strip's processing and the ducking. It is
// immutable: the engine takes a new one whole (WasapiAudioEngine.SetProgram) at the next buffer, and what it already decoded
// for a voice whose timing did not change is kept.
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

    /// <summary>
    /// Which pair of the stream's channels is read as its left and right (5.2-Audit, a surround track exported as it is: pair 0
    /// is channels 1 and 2, pair 1 channels 3 and 4...; a pair past the last channel is silent, a lone last channel heard on
    /// both sides); -1, as everything plays, for the whole stream made stereo.
    /// </summary>
    public int ChannelPair { get; init; } = -1;

    public override string ToString() => ChannelPair < 0 ? $"{System.IO.Path.GetFileName(Path)} a:{Stream}" : $"{System.IO.Path.GetFileName(Path)} a:{Stream} pair {ChannelPair}";
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

    /// <summary>
    /// Its own gain as a factor: on a <see cref="AudioStripKind.Track"/> strip its clip's gain (the track's is in the strip's
    /// effects); 1 on a <see cref="AudioStripKind.Clip"/> strip, whose effects carry the clip's gain with its track's.
    /// </summary>
    public float Gain { get; init; } = 1;

    /// <summary>In from <see cref="SequenceStart"/>.</summary>
    public AudioFade FadeIn { get; init; }

    /// <summary>Out, ending at <see cref="FadeOutEnd"/>.</summary>
    public AudioFade FadeOut { get; init; }

    public double FadeOutEnd { get; init; } = double.PositiveInfinity;

    /// <summary>The strip it is mixed into (<see cref="AudioProgram.Strips"/>).</summary>
    public required int Strip { get; init; }

    /// <summary>What it is, for the log ("clip game.mkv", "voiceover").</summary>
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
    /// <summary>
    /// A track's clips (5.2-AuditB, as the main file's streams were before): each clip's sound with its gain and fades, summed into
    /// one continuous strip, ducked, then the track's gain and processing (the export's order: its processing's state runs on across
    /// the cuts).
    /// </summary>
    Track,

    /// <summary>A clip with processing of its own: its processing and gain, then its fades, then ducked (BuildLayerAudio's order).</summary>
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

    public string Label { get; init; } = "";

    /// <summary>
    /// The output it is mixed into (5.2-Audit, an export's several sound tracks rendered in one pass: AudioMixer.RenderTo with
    /// buses): 0, the one everything plays through. Its ducking still hears every voice, whichever bus that is on.
    /// </summary>
    public int Bus { get; init; }

    /// <summary>Its voices' fades and gains come after its processing (a clip's), not before (a track's).</summary>
    public bool EnvelopeAfterEffects => Kind != AudioStripKind.Track;
}

/// <summary>
/// The ducking, as FFmpeg's sidechaincompress takes it (as the export's graph had it before 5.2-Audit): every raw parameter, the 1-click
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

    /// <summary>The ducking's raw parameters; null for the FFmpeg path's own, from the state's amount.</summary>
    public DuckParameters? Duck { get; init; }

    /// <summary>
    /// Which bus each strip is mixed into (5.2-Audit: an export's several sound tracks, ExportSoundPlan); null for everything on
    /// bus 0, as the editor plays. A sound on a Sub-Lane whose stream place the layout has no buses for is not rendered (the
    /// export copies it, or leaves it out).
    /// </summary>
    public AudioBusLayout? Buses { get; init; }

    /// <summary>
    /// Every track is heard whatever is soloed (5.2-AuditB: an export's, ExportSoundPlan; the Solo switches are for listening
    /// while editing, as the Audio tab's solo always was). False, the editor's: while any track of the Sequence is soloed
    /// (TimelineTrackModel.IsSoloed), only the soloed tracks and the Sub-Lanes under them are heard, and the voiceover is not.
    /// </summary>
    public bool IgnoresSolo { get; init; }
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

    /// <summary>
    /// The most any strip's effects bring its sound out late (samples, 5.2C: AudioEffect.LatencyOf): an offline render begins mixing that
    /// long before its first moment, so a processed clip there has its pre-roll too.
    /// </summary>
    internal int MostLatencyAt(int sampleRate)
    {
        var most = 0;
        foreach (var strip in StripArray)
            most = Math.Max(most, AudioEffect.LatencyOf([.. strip.Effects], sampleRate));
        return most;
    }

    /// <summary>Nothing to hear: the engine still runs its clock.</summary>
    public static AudioProgram Empty { get; } = new(0, [], [], false, false, new DuckParameters());

    /// <summary>The program of a state, with the FFmpeg path's semantics (AudioProgramBuilder). Cheap: run it on every edit.</summary>
    public static AudioProgram Build(SequenceExportState state) => AudioProgramBuilder.Build(state, AudioBuildOptions.Default);

    public static AudioProgram Build(SequenceExportState state, AudioBuildOptions options) => AudioProgramBuilder.Build(state, options);

    /// <summary>Nothing is heard past it: the Sequence's length.</summary>
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

    /// <summary>
    /// The Sequence's master output fader as a factor (5.2B: SequenceExportState.MasterGainDb), on every bus after the strips are summed and
    /// before the Sequence's fades (AudioMixer); exactly 1 at 0 dB, which multiplies nothing.
    /// </summary>
    public float MasterGain { get; init; } = 1;

    /// <summary>Unique and growing: which program a log line or a test speaks of.</summary>
    public long Serial { get; }
}
