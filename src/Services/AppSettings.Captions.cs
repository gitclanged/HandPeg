namespace HandPegApp.Services;

// Remove Dead Air from the captions (4.4): its settings, kept beside the dead-air ones in AppSettings.cs; and what Auto-Captions run
// on (5.2-Arm64AndNpu: the NPU, the graphics card or the processor).
public sealed partial class AppSettings
{
    public const string DeadAirFromAudio = "audio", DeadAirFromCaptions = "captions";

    /// <summary>What Remove Dead Air can go by: silence in the sound (by its loudness), or the gaps between captioned words.</summary>
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

    // ----- What Auto-Captions run on (5.2-Arm64AndNpu) -----

    public const string SpeechOnAuto = "auto", SpeechOnNpu = "npu", SpeechOnGpu = "gpu", SpeechOnCpu = "cpu";

    /// <summary>The choices of <see cref="CaptionAccelerator"/>, in the order the lists show them.</summary>
    public static IReadOnlyList<string> CaptionAccelerators { get; } = [SpeechOnAuto, SpeechOnNpu, SpeechOnGpu, SpeechOnCpu];

    /// <summary>
    /// What Auto-Captions (whisper.cpp, and its voice activity detection, which runs inside it on the same back end) run on:
    /// "auto" (the NPU when a speech build for it is installed, which none is yet; else the graphics card when the whisper.cpp
    /// build in use has a back end for it that has not failed here; else the processor), "npu", "gpu" or "cpu". A choice the
    /// installed build cannot honour runs on the next one down, and says so in the log (CaptionGenerator.ChooseSpeechAccelerator).
    /// </summary>
    public string CaptionAccelerator { get; set; } = SpeechOnAuto;

    /// <summary>
    /// The whisper.cpp build, and the graphics cards and drivers, with which whisper.exe fell over on the graphics card (it
    /// crashed rather than said why: b5454's Adreno OpenCL back end at model load with driver 31.0.170.0, measured): the run
    /// was made again on the processor, and Auto keeps to the processor while the build and the drivers are those. Empty
    /// when it never did. Written by CaptionGenerator, never shown.
    /// </summary>
    public string WhisperGpuFailedOn { get; set; } = "";
}
