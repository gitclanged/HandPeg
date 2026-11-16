namespace HandPegApp.Services;

// Caption-based Remove Dead Air settings (alongside the audio ones in AppSettings.cs) and the Auto-Captions accelerator (NPU, GPU, CPU).
public sealed partial class AppSettings
{
    public const string DeadAirFromAudio = "audio", DeadAirFromCaptions = "captions";

    /// <summary>Remove Dead Air detection sources: audio loudness or caption word gaps.</summary>
    public static IReadOnlyList<string> DeadAirSources { get; } = [DeadAirFromAudio, DeadAirFromCaptions];

    /// <summary>
    /// Detection mode: "audio" (below <see cref="DeadAirThresholdDb"/>) or "captions" (word gaps; transcribes first if needed).
    /// </summary>
    public string DeadAirSource { get; set; } = DeadAirFromAudio;

    /// <summary>Caption mode: minimum word gap to cut (s, 0.1-3).</summary>
    public double DeadAirCaptionMinGapSeconds { get; set; } = 0.5;

    /// <summary>Caption mode: padding kept before the next word (s, 0-0.5).</summary>
    public double DeadAirCaptionPadBeforeSeconds { get; set; } = 0.08;

    /// <summary>Caption mode: padding kept after the last word (s, 0-0.5).</summary>
    public double DeadAirCaptionPadAfterSeconds { get; set; } = 0.12;

    // ----- Auto-Captions accelerator -----

    public const string SpeechOnAuto = "auto", SpeechOnNpu = "npu", SpeechOnGpu = "gpu", SpeechOnCpu = "cpu";

    /// <summary>The choices of <see cref="CaptionAccelerator"/>, in the order the lists show them.</summary>
    public static IReadOnlyList<string> CaptionAccelerators { get; } = [SpeechOnAuto, SpeechOnNpu, SpeechOnGpu, SpeechOnCpu];

    /// <summary>
    /// whisper.cpp (and its in-process VAD) back end: "auto" (NPU if a speech build exists, else GPU if supported and not previously
    /// failed, else CPU), "npu", "gpu" or "cpu". Unsupported choices fall back down the chain with a log entry
    /// (CaptionGenerator.ChooseSpeechAccelerator).
    /// </summary>
    public string CaptionAccelerator { get; set; } = SpeechOnAuto;

    /// <summary>
    /// Fingerprint (whisper.cpp build plus adapters and drivers) of a GPU back-end crash (e.g. an Adreno OpenCL crash at model load); the
    /// run was retried on CPU and Auto stays on CPU while the fingerprint matches. Empty if never crashed. Internal (CaptionGenerator).
    /// </summary>
    public string WhisperGpuFailedOn { get; set; } = "";
}
