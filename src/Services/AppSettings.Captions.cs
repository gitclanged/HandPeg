namespace HandPegApp.Services;

// Remove Dead Air from the captions (4.4): its settings, kept beside the dead-air ones in AppSettings.cs.
public sealed partial class AppSettings
{
    public const string DeadAirFromAudio = "audio", DeadAirFromCaptions = "captions";

    /// <summary>What Remove Dead Air can listen for: silence in the sound (by its loudness), or the gaps between captioned words.</summary>
    public static IReadOnlyList<string> DeadAirSources { get; } = [DeadAirFromAudio, DeadAirFromCaptions];

    /// <summary>
    /// What Remove Dead Air finds the dead air by: "audio", the stretches quieter than <see cref="DeadAirThresholdDb"/>; or
    /// "captions", the gaps between the words the captions heard (transcribed first when there are none yet).
    /// </summary>
    public string DeadAirSource { get; set; } = DeadAirFromAudio;

    /// <summary>Remove Dead Air from the captions: a gap between two words has to last this long, in seconds, to be cut (0.1 to 3).</summary>
    public double DeadAirCaptionMinGapSeconds { get; set; } = 0.5;

    /// <summary>Remove Dead Air from the captions: how much of a gap is kept before the next word, in seconds (0 to 0.5).</summary>
    public double DeadAirCaptionPadBeforeSeconds { get; set; } = 0.08;

    /// <summary>Remove Dead Air from the captions: how much of a gap is kept after the last word, in seconds (0 to 0.5).</summary>
    public double DeadAirCaptionPadAfterSeconds { get; set; } = 0.12;
}
