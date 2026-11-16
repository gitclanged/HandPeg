using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

/// <summary>
/// Output audio track choice by Sub-Lane stream ordinal (<see cref="TimelineTrackModel.StreamOrdinal"/>): title, codec, bitrate, copy or
/// drop. Output N mixes every Sub-Lane of ordinal N under any track (ExportSoundPlan.For).
/// </summary>
/// <param name="Passthrough">Stream-copy when possible (single plain sound from a file that is the whole Sequence); otherwise encode the mix.</param>
/// <param name="Dropped">Exclude this ordinal from the export.</param>
public sealed record AudioOutputChoice(int Ordinal, string Title, string Codec, string Bitrate, bool Passthrough, bool Dropped);

/// <summary>
/// Immutable input snapshot for export, preview and audio (TimelineLowering, GpuScenePlan, AudioProgram, EncoderCommand): Sequence
/// (<see cref="Timeline"/>), Sequence-wide picture settings, output audio tracks and encode settings. Consumers read nothing else (no
/// window, settings or disk). Every clip references its own file and offset; there is no primary file.
///
/// The timeline is a private copy, so builders can neither observe nor cause concurrent mutation.
///
/// The canvas (black, timeline size, <see cref="TimelineDocument.TotalDuration"/> long, at the frame rate) is the time base; all clips
/// are laid on it (Services/Gpu/TimelineLowering.cs). Output time equals Sequence time.
/// </summary>
public sealed record SequenceExportState
{
    // ----- The sequence: its tracks of clips, its frame, its clock -----

    /// <summary>
    /// Sequence: tracks bottom-first, frame (<see cref="TimelineDocument.Width"/> x <see cref="TimelineDocument.Height"/>), rate and Sequence
    /// Output effects. Lowered once and cached on the document, so it must be a private copy, never the view model's live document.
    /// </summary>
    public required TimelineDocument Timeline { get; init; }

    /// <summary>Sequence frame rate (30 when unknown).</summary>
    public required double SourceFrameRate { get; init; }

    /// <summary>The frame rate to convert to, already written for FFmpeg, or null to keep the Sequence's.</summary>
    public string? TargetFramerate { get; init; }

    /// <summary>What the Width and Height boxes hold, as typed.</summary>
    public string OutputWidth { get; init; } = "";

    public string OutputHeight { get; init; } = "";

    /// <summary>Output size from Resolution &amp; Cropping, or null for the Sequence's natural size.</summary>
    public (int Width, int Height)? RequestedSize { get; init; }

    /// <summary>Frame size of the output, or null when it is not known.</summary>
    public (int Width, int Height)? OutputSize { get; init; }

    /// <summary>The value for setsar, or null to leave the pixel shape alone.</summary>
    public string? SampleAspectRatio { get; init; }

    // ----- The canvas: what is laid on it besides the clips -----

    /// <summary>
    /// Last-opened file supplying subtitle streams and chapters (""= none); never used for picture or audio. Soft subtitles and chapters
    /// carry over only when the Sequence is that whole file (SequenceSpans.WholeFiles); burned subtitles map through its clips (SequenceSpans.SpansOf).
    /// </summary>
    public string StreamSourcePath { get; init; } = "";

    /// <summary>
    /// Blur-fill settings for clips with LayerState.BlurFill: box radius (0-64 frame px, fractional on GPU), passes (1-4), darkening
    /// (-0.5..0), centre zoom (1 = cover), and Look cards applied after blur and darkening (Sequence-time keys and ranges).
    /// </summary>
    public double BlurRadius { get; init; } = 20;
    public int BlurPasses { get; init; } = 2;
    public double BackgroundDim { get; init; } = -0.15;
    public double BackgroundZoom { get; init; } = 1;
    public IReadOnlyList<EffectState> BackgroundEffects { get; init; } = [];

    /// <summary>The box the auto-captions are drawn in.</summary>
    public required Layer CaptionLayer { get; init; }

    public bool AutoCaptions { get; init; }

    /// <summary>The caption file an export draws.</summary>
    public string CaptionsFilePath { get; init; } = "";

    // ----- Filters on the whole picture -----

    public bool Deinterlace { get; init; }
    public bool Denoise { get; init; }
    public bool TonemapToSdr { get; init; }

    /// <summary>
    /// HDR export transfer (10-bit HEVC/AV1 under Match Project Settings): "smpte2084" (PQ) or "arib-std-b67" (HLG); null for SDR BT.709
    /// (HDR projects are tone-mapped).
    /// </summary>
    public string? HdrTransfer { get; init; }
    public string LutPath { get; init; } = "";

    /// <summary>The colour sliders' grade of the whole picture.</summary>
    public SequenceGrade Grade { get; init; }

    public bool FadeIn { get; init; }
    public bool FadeOut { get; init; }

    // ----- Chapters, subtitles, the list a copied picture is cut by -----

    /// <summary>
    /// Copy <see cref="StreamSourcePath"/> chapters; effective only when the Sequence is that whole file (SequenceSpans.WholeFiles).
    /// </summary>
    public bool ChapterMarkers { get; init; }

    /// <summary>
    /// Chapters at each cut, read from <see cref="ChaptersFilePath"/> (written by the view model), unless the Sequence is the whole
    /// <see cref="StreamSourcePath"/> file.
    /// </summary>
    public bool ChaptersAtCuts { get; init; }

    /// <summary>Chapters begin at the markers: switched on, and there are markers.</summary>
    public bool ChaptersAtMarkers { get; init; }

    /// <summary>
    /// Copy-export concat list path (<see cref="EncoderCommand.BuildCutsList"/>, ranges from SequenceSpans.CopyCutOf); written by the view
    /// model when <see cref="ExportCommand.UsesCutsFile"/>.
    /// </summary>
    public string CutsFilePath { get; init; } = "";

    /// <summary>FFmpeg metadata chapters file, when <see cref="ExportCommand.UsesChaptersFile"/>.</summary>
    public string ChaptersFilePath { get; init; } = "";

    /// <summary>
    /// Soft subtitle streams ("s:N") of <see cref="StreamSourcePath"/> to copy; effective only when the Sequence is that whole file.
    /// </summary>
    public IReadOnlyList<int> SoftSubtitleStreams { get; init; } = [];

    /// <summary>Subtitle streams of <see cref="StreamSourcePath"/> to burn in, mapped through its clips.</summary>
    public IReadOnlyList<int> BurnedSubtitleStreams { get; init; } = [];

    // ----- Audio -----
    // Per-clip audio comes from the clip (TimelineLowering LayerAudio) with track gain, filters and ducking; this section holds output
    // track choices and Sequence-wide settings.

    /// <summary>
    /// Output track choices by ordinal (<see cref="AudioOutputChoice.Ordinal"/>): output N mixes Sub-Lanes of ordinal N; unassigned audio and
    /// voiceover go to the first (ExportSoundPlan.For). Ordinals without a choice use <see cref="MixedAudioCodec"/> and <see cref="AudioBitrate"/>.
    /// </summary>
    public IReadOnlyList<AudioOutputChoice> AudioOutputs { get; init; } = [];

    public bool MergeAudioTracks { get; init; }
    public bool NormalizeAudio { get; init; }

    /// <summary>Normalize target in LUFS: -16 (default, ExportSoundPlan.TargetLufs) or -14.</summary>
    public double LoudnessTargetLufs { get; init; } = Audio.ExportSoundPlan.TargetLufs;

    /// <summary>How far a sound set to Auto-Duck is turned down while a voice speaks, in decibels.</summary>
    public double DuckAmountDb { get; init; } = -15;

    /// <summary>
    /// Master fader (dB) from <see cref="TimelineDocument.MasterGainDb"/>, applied on every bus after summing and before Sequence fades
    /// (AudioProgram.MasterGain); 0 = unity.
    /// </summary>
    public double MasterGainDb => Timeline.MasterGainDb;

    /// <summary>The codec of a mixed track.</summary>
    public string MixedAudioCodec { get; init; } = "aac";

    public string AudioBitrate { get; init; } = "160k";

    /// <summary>The sample rate every encoded audio stream is written at, in Hz; 0 keeps each stream's own (Auto).</summary>
    public int AudioSampleRate { get; init; }

    /// <summary>
    /// Encoded channel layout (<see cref="AudioChannelChoice"/>): Stereo downmixes wider layouts (mono/stereo unchanged), Mono forces one
    /// channel, Source keeps the original.
    /// </summary>
    public string AudioChannels { get; init; } = AudioChannelChoice.Stereo;

    // The session's voiceover (with nothing open to put it on as a clip): a second input, mixed into the first track.
    public bool IncludeVoiceover { get; init; }
    public string VoiceoverPath { get; init; } = "";
    public double VoiceoverDuration { get; init; }
    public double VoiceoverTrimStart { get; init; }
    public double VoiceoverTrimEnd { get; init; }
    public double VoiceoverStartSeconds { get; init; }
    public double VoiceoverGainDb { get; init; }

    // ----- The command around the graph -----

    /// <summary>FFmpeg executable token: bare "ffmpeg" or the quoted custom path.</summary>
    public string FfmpegExecutable { get; init; } = "ffmpeg";

    // (Legacy HardwareDecodeArguments removed: no command used it.)

    /// <summary>The video encoder is Copy: the picture is passed through, and no filter can touch it.</summary>
    public bool CopyVideoEncoder { get; init; }

    /// <summary>The output is a GIF or a WebP, going by the format chosen.</summary>
    public bool IsAnimatedOutput { get; init; }

    /// <summary>The video encoder and its settings, as command-line arguments.</summary>
    public string VideoCodecArguments { get; init; } = "";

    /// <summary>
    /// Pixel format passed to a hardware encoder (yuv420p, or p010le for 10-bit); null for software encoders, which accept the piped format.
    /// </summary>
    public string? EncoderPixelFormat { get; init; }

    /// <summary>The container chosen, for an output whose name has no extension.</summary>
    public string Container { get; init; } = "mp4";

    public bool WebOptimized { get; init; }

    /// <summary>The file the output is written to.</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>
    /// Referenced files (masks, LUTs, caption file) that existed at snapshot time; builders consult this instead of the disk and skip missing ones.
    /// </summary>
    public IReadOnlySet<string> ExistingFiles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Masks sampled by alpha rather than luminance (<see cref="MaskStore.UsesAlpha"/>).</summary>
    public IReadOnlySet<string> AlphaMasks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Video stream start offset (s) per probed file (<see cref="MediaInfo.VideoStartSeconds"/>), mapping media time to container time.
    /// </summary>
    public IReadOnlyDictionary<string, double> MediaStarts { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Known I-frame indexes per file (<see cref="SeekIndex"/>), enabling exact keyframe seeks.
    /// </summary>
    public IReadOnlyDictionary<string, SeekIndex> SeekIndexes { get; init; } = new Dictionary<string, SeekIndex>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ready proxies by original path (ProxyCache; Optimize Long-GOP Clip). Used only when <see cref="IsPreview"/>; exports always read
    /// originals. Proxies start at time 0 regardless of the original's start offset.
    /// </summary>
    public IReadOnlyDictionary<string, string> PreviewPaths { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for the preview state (MainViewModel.LivePreviewState); only then are <see cref="PreviewPaths"/> proxies used. False for exports and filmstrips.
    /// </summary>
    public bool IsPreview { get; init; }

    /// <summary>
    /// Decoded bitmap subtitles (PGS/VobSub of <see cref="StreamSourcePath"/>; preview: Gpu.PictureSubtitles.cs, export: BurnedSubtitles),
    /// drawn over the picture mapped through that file's clips; null for none.
    /// </summary>
    public Gpu.PictureSubtitleTrack? PictureSubtitles { get; init; }
}
