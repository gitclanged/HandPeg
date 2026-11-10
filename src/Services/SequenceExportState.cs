using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

/// <summary>
/// What an output audio track is made as (5.2-AuditB): its stream place among the Sub-Lanes
/// (<see cref="TimelineTrackModel.StreamOrdinal"/>), title, codec, bitrate, copied as it is or left out. Output N carries every
/// sound on a Sub-Lane of stream place N, whichever track that lies under (ExportSoundPlan.For).
/// </summary>
/// <param name="Passthrough">Copied from its file as it is, while it can be (one sound played plainly from a file the whole Sequence is one clip of); encoded as a mix otherwise.</param>
/// <param name="Dropped">Left out of the output: its stream place's sounds are not in the export.</param>
public sealed record AudioOutputChoice(int Ordinal, string Title, string Codec, string Bitrate, bool Passthrough, bool Dropped);

/// <summary>
/// Everything an export, the preview and the sound engine are built from, gathered at one moment: the sequence
/// (<see cref="Timeline"/>: its tracks of clips, its frame, its rate), the sequence-wide picture settings, the sound's output
/// tracks, and the encode settings that end up in the command. The view model compiles one of these and hands it to the GPU
/// export, the GPU preview and the sound engine (TimelineLowering, GpuScenePlan, AudioProgram, EncoderCommand), which read
/// nothing else: not the window, not the settings, not the disk. No file is the "main" one (5.2-AuditB): every clip reads its own
/// file at its own offset, and a sound is heard from its own clip.
///
/// It is a snapshot, not a view. Its timeline is one made for it, which nothing else holds or edits, so what is
/// built from it cannot change under the builder or be changed by it.
///
/// The spine of a sequence is its canvas: a black picture the timeline's width by its height, as long as the
/// timeline (<see cref="TimelineDocument.TotalDuration"/>), ticking at its frame rate. Every clip on every track is
/// laid on that; none of them is what the others are measured against (see Services/Gpu/TimelineLowering.cs). The output
/// is that canvas, second for second: what was cut out is not on the timeline at all (4.3C made 4.2.0's cut list
/// splits), so a moment of the sequence is the same moment of the output.
/// </summary>
public sealed record SequenceExportState
{
    // ----- The sequence: its tracks of clips, its frame, its clock -----

    /// <summary>
    /// The sequence: its tracks of clips (bottom first), its frame (<see cref="TimelineDocument.Width"/> by
    /// <see cref="TimelineDocument.Height"/>), its rate, and the Sequence Output's effect stack. The builder reads every clip
    /// from it, and lowers it once for all the graphs built from it (cached by the document, which is why it must be one
    /// made for this state: a copy, never the view model's own while it is being edited).
    /// </summary>
    public required TimelineDocument Timeline { get; init; }

    /// <summary>Frames a second of the Sequence (30 when not known): Live Preview plays a fast one at half its rate.</summary>
    public required double SourceFrameRate { get; init; }

    /// <summary>The frame rate to convert to, already written for FFmpeg, or null to keep the Sequence's.</summary>
    public string? TargetFramerate { get; init; }

    /// <summary>What the Width and Height boxes hold, as typed.</summary>
    public string OutputWidth { get; init; } = "";

    public string OutputHeight { get; init; } = "";

    /// <summary>The size Resolution &amp; Cropping asks for, or null when it leaves the size to the Sequence's natural one.</summary>
    public (int Width, int Height)? RequestedSize { get; init; }

    /// <summary>Frame size of the output, or null when it is not known.</summary>
    public (int Width, int Height)? OutputSize { get; init; }

    /// <summary>The value for setsar, or null to leave the pixel shape alone.</summary>
    public string? SampleAspectRatio { get; init; }

    // ----- The canvas: what is laid on it besides the clips -----

    /// <summary>
    /// The file whose own subtitle streams and chapters the Subtitles and Chapters settings name (the file last opened); "" none.
    /// Never read by the picture or the sound (5.2-AuditB: no file is the "main" one): its soft subtitles and chapters are carried
    /// over only while the whole Sequence is one clip of it (SequenceSpans.WholeFiles), their times being its own; its burned
    /// subtitles are placed through its clips (SequenceSpans.SpansOf).
    /// </summary>
    public string StreamSourcePath { get; init; } = "";

    /// <summary>
    /// The blur fill's look (5.2-AuditB: of every clip whose look asks for one, LayerState.BlurFill): its box blur's radius in
    /// pixels of the frame (0 sharp to 64, a fraction its own on the GPU), its passes (1 to 4), its darkening (-0.5 to 0), its
    /// zoom about the middle (1 just covers the frame), and its own Look cards (keys and ranges in seconds of the sequence), after
    /// the blur and the darkening.
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
    /// 5.4B: an HDR project exported as HDR (10-bit HEVC or AV1 under Match Project Settings): the transfer tagged and kept,
    /// "smpte2084" (PQ) or "arib-std-b67" (HLG); null for SDR (BT.709, tone-mapped when the project is HDR).
    /// </summary>
    public string? HdrTransfer { get; init; }
    public string LutPath { get; init; } = "";

    /// <summary>The colour sliders' grade of the whole picture.</summary>
    public SequenceGrade Grade { get; init; }

    public bool FadeIn { get; init; }
    public bool FadeOut { get; init; }

    // ----- Chapters, subtitles, the list a copied picture is cut by -----

    /// <summary>
    /// The chapters of <see cref="StreamSourcePath"/> are carried over: only while the whole Sequence is one clip of that file as it
    /// is (SequenceSpans.WholeFiles), their times being the file's.
    /// </summary>
    public bool ChapterMarkers { get; init; }

    /// <summary>
    /// A chapter begins at each cut (4.2.0's Chapters at Cut Points, now that cuts are splits): read from
    /// <see cref="ChaptersFilePath"/>, which the view model writes, unless the whole Sequence is one clip of
    /// <see cref="StreamSourcePath"/> as it is.
    /// </summary>
    public bool ChaptersAtCuts { get; init; }

    /// <summary>Chapters begin at the markers: switched on, and there are markers.</summary>
    public bool ChaptersAtMarkers { get; init; }

    /// <summary>
    /// Where the concat list of a Copy export is (see <see cref="EncoderCommand.BuildCutsList"/>): the clips of the file a Copy
    /// export copies (SequenceSpans.CopyCutOf) as stretches of it, which a picture that is only copied can be cut by. Written by
    /// the view model when the command reads it (<see cref="ExportCommand.UsesCutsFile"/>).
    /// </summary>
    public string CutsFilePath { get; init; } = "";

    /// <summary>The chapters, as an FFmpeg metadata file, when the command reads them (<see cref="ExportCommand.UsesChaptersFile"/>).</summary>
    public string ChaptersFilePath { get; init; } = "";

    /// <summary>
    /// The subtitle streams of <see cref="StreamSourcePath"/> to carry over as they are, by their number ("s:N"): only while the
    /// whole Sequence is one clip of that file as it is (SequenceSpans.WholeFiles), their times being the file's.
    /// </summary>
    public IReadOnlyList<int> SoftSubtitleStreams { get; init; } = [];

    /// <summary>The subtitle streams of <see cref="StreamSourcePath"/> to draw into the picture, where its clips put their moments.</summary>
    public IReadOnlyList<int> BurnedSubtitleStreams { get; init; } = [];

    // ----- Sound -----
    // Every heard clip's sound is its own (TimelineLowering's LayerAudio): its file and stream at its offset, its track's gain,
    // filters and ducking (TimelineTrackModel). What is here is how the output tracks are made and the sequence-wide settings.

    /// <summary>
    /// The output audio tracks' choices, by stream place (<see cref="AudioOutputChoice.Ordinal"/>): output N carries every sound
    /// on a Sub-Lane of stream place N; the sounds of no stream place and the voiceover go to the first (ExportSoundPlan.For). A
    /// place with no choice is encoded as a mix is (<see cref="MixedAudioCodec"/>, <see cref="AudioBitrate"/>).
    /// </summary>
    public IReadOnlyList<AudioOutputChoice> AudioOutputs { get; init; } = [];

    public bool MergeAudioTracks { get; init; }
    public bool NormalizeAudio { get; init; }

    /// <summary>The loudness Normalize aims at, in LUFS (5.4B): -16, the default (ExportSoundPlan.TargetLufs), or -14.</summary>
    public double LoudnessTargetLufs { get; init; } = Audio.ExportSoundPlan.TargetLufs;

    /// <summary>How far a sound set to Auto-Duck is turned down while a voice speaks, in decibels.</summary>
    public double DuckAmountDb { get; init; } = -15;

    /// <summary>
    /// The Sequence's master output fader in decibels (5.2B): its document's own (<see cref="TimelineDocument.MasterGainDb"/>, which the
    /// timeline this state is made from carries), so every state made from the document has it. On every bus after the strips are summed,
    /// before the Sequence's fades (AudioProgram.MasterGain); 0 changes nothing.
    /// </summary>
    public double MasterGainDb => Timeline.MasterGainDb;

    /// <summary>The codec of a mixed track.</summary>
    public string MixedAudioCodec { get; init; } = "aac";

    public string AudioBitrate { get; init; } = "160k";

    /// <summary>The sample rate every encoded audio stream is written at, in Hz; 0 keeps each stream's own (Auto).</summary>
    public int AudioSampleRate { get; init; }

    /// <summary>
    /// The channels of every encoded audio stream (<see cref="AudioChannelChoice"/>): Stereo makes anything wider
    /// two-channel (a stereo or mono stream is left as it is), Mono makes one channel, Source keeps the source's.
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

    /// <summary>How the command names FFmpeg: a bare "ffmpeg", or the quoted path of the one chosen in the settings.</summary>
    public string FfmpegExecutable { get; init; } = "ffmpeg";

    // (HardwareDecodeArguments, the dead "-hwaccel auto" no command read, went with the Video tab's switch, 5.4B.)

    /// <summary>The video encoder is Copy: the picture is passed through, and no filter can touch it.</summary>
    public bool CopyVideoEncoder { get; init; }

    /// <summary>The output is a GIF or a WebP, going by the format chosen.</summary>
    public bool IsAnimatedOutput { get; init; }

    /// <summary>The video encoder and its settings, as command-line arguments.</summary>
    public string VideoCodecArguments { get; init; } = "";

    /// <summary>
    /// The pixel format a hardware encoder is handed (yuv420p, or p010le for 10-bit), or null for a software
    /// encoder, which takes what the graph gives it. The export's picture is finished in it by the graph itself.
    /// </summary>
    public string? EncoderPixelFormat { get; init; }

    /// <summary>The container chosen, for an output whose name has no extension.</summary>
    public string Container { get; init; } = "mp4";

    public bool WebOptimized { get; init; }

    /// <summary>The file the output is written to.</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>
    /// The files named in this state that were there when it was compiled (masks, LUTs, the caption file). The
    /// builder leaves out a mask or a LUT whose file is missing, and asks this instead of the disk.
    /// </summary>
    public IReadOnlySet<string> ExistingFiles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mask pictures that are read by their transparency, not their brightness (see <see cref="MaskStore.UsesAlpha"/>).</summary>
    public IReadOnlySet<string> AlphaMasks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the picture of each video layer's file starts, in seconds (see <see cref="MediaInfo.VideoStartSeconds"/>),
    /// for the files that have been inspected. A layer whose file is listed here is read from where it is wanted
    /// (seek_point) and cut on its own time stamps; one that is not is read from its first frame, as before.
    /// </summary>
    public IReadOnlyDictionary<string, double> MediaStarts { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The I-frames of the video layers' files whose I-frames have been listed (see <see cref="SeekIndex"/>): a live
    /// graph opens such a file exactly at the I-frame before the moment it wants, not a lead before that moment.
    /// </summary>
    public IReadOnlyDictionary<string, SeekIndex> SeekIndexes { get; init; } = new Dictionary<string, SeekIndex>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The proxies ready for the video layers' files (see ProxyCache), by the file they stand for: of the files whose clips
    /// asked for one (Optimize Long-GOP Clip for Scrubbing). Read by the preview only (the GPU scene plan of a state that
    /// <see cref="IsPreview"/>, and live graphs); an export always reads the original. A proxy starts at its first frame
    /// (time 0), whatever the original's start.
    /// </summary>
    public IReadOnlyDictionary<string, string> PreviewPaths { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The state the GPU preview draws (MainViewModel.LivePreviewState, 5.0-Verify): only its scene plan reads a video layer's
    /// proxy (<see cref="PreviewPaths"/>) in place of its file. False for every export and filmstrip.
    /// </summary>
    public bool IsPreview { get; init; }

    /// <summary>
    /// The subtitle track's pictures (a PGS or VobSub track of <see cref="StreamSourcePath"/>, decoded for the preview:
    /// Gpu.PictureSubtitles.cs; an export's burned subtitles, BurnedSubtitles), which the GPU scene draws over the picture where
    /// that file's clips put their moments (5.0-Verify); null for none.
    /// </summary>
    public Gpu.PictureSubtitleTrack? PictureSubtitles { get; init; }
}
