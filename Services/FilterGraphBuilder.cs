using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>A kept stretch of the sequence, as the export sees a cut segment: from where to where, in the sequence's time.</summary>
public sealed record ExportSegment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// An audio track of the output as the graph needs it: which stream of the file it is, what is done with
/// it, and the filters its edits and its mixer settings come to, already worked out.
/// </summary>
/// <param name="Index">Position among the file's audio streams, as used in "0:a:N".</param>
/// <param name="IsSilentBase">Not a stream of the file at all: the silence that layer sound is mixed into when the sequence has none of its own.</param>
/// <param name="EditChain">What was done to the track on the timeline (silenced parts, a slip), as filters.</param>
/// <param name="FilterChain">Its gain and processing, as filters; empty when it has none.</param>
public sealed record ExportAudioTrack(
    int Index, string Title, string Codec, string Bitrate, bool IsPassthrough, bool IsMono, bool AutoDuck, bool IsVoice,
    bool IsSilentBase, bool HasProcessing, IReadOnlyList<string> EditChain, IReadOnlyList<string> FilterChain)
{
    public List<string> BuildEditChain() => [.. EditChain];

    public List<string> GetFilterChain() => [.. FilterChain];
}

/// <summary>
/// Everything a filter graph is built from, gathered at one moment: the sequence as it stands (how long it
/// is, the frame it is composed on, the rate its canvas ticks at, what is cropped), the clips on it, the cuts,
/// the sound, and the encode settings that end up in the command. The view model compiles one of these and
/// hands it to <see cref="FilterGraphBuilder"/>, which reads nothing else: not the window, not the settings,
/// not the disk.
///
/// It is a snapshot, not a view. The layers in it are copies made for it, which nothing else holds, so what
/// is built from it cannot change under the builder or be changed by it.
///
/// The spine of a sequence is its canvas: a black picture <see cref="FrameWidth"/> by <see cref="FrameHeight"/>,
/// <see cref="SequenceSeconds"/> long, at <see cref="SourceFrameRate"/>. Every clip, the main video among
/// them, is laid on that; none of them is what the others are measured against.
/// </summary>
public sealed record SequenceExportState
{
    // ----- The sequence: its length, its frame, its clock -----

    /// <summary>Total length of the sequence in seconds: to the end of whichever clip ends last. The canvas is this long.</summary>
    public required double SequenceSeconds { get; init; }

    /// <summary>Length of the output in seconds: the kept segments end to end, or the whole sequence.</summary>
    public required double OutputDuration { get; init; }

    /// <summary>Width and height of the frame the sequence is composed on, in pixels: the canvas's size.</summary>
    public required int FrameWidth { get; init; }

    public required int FrameHeight { get; init; }

    /// <summary>Frames a second of the main video, which is the rate the canvas ticks at (30 when not known).</summary>
    public required double SourceFrameRate { get; init; }

    /// <summary>The frame rate to convert to, already written for FFmpeg, or null to keep the source's.</summary>
    public string? TargetFramerate { get; init; }

    /// <summary>The source's own size in pixels; 0 when not known.</summary>
    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    /// <summary>What the Width and Height boxes hold, as typed: the size asked for when the Layer Engine is off.</summary>
    public string OutputWidth { get; init; } = "";

    public string OutputHeight { get; init; } = "";

    /// <summary>The size Resolution &amp; Cropping asks for, or null when it leaves the size to the source.</summary>
    public (int Width, int Height)? RequestedSize { get; init; }

    /// <summary>Frame size of the output after cropping and scaling, or null when the source's size is not known.</summary>
    public (int Width, int Height)? OutputSize { get; init; }

    /// <summary>The frame is a tall one made from a wide video.</summary>
    public bool UseVerticalResolution { get; init; }

    /// <summary>The value for setsar, or null to leave the pixel shape alone.</summary>
    public string? SampleAspectRatio { get; init; }

    // ----- The crop -----

    public bool HasCrop { get; init; }

    /// <summary>The crop as fractions (0 to 1) of the source's width and height.</summary>
    public (double Left, double Top, double Right, double Bottom) CropFractions { get; init; }

    // ----- The main video on the sequence -----

    /// <summary>The file FFmpeg reads; blank before anything is loaded.</summary>
    public string LocalMediaPath { get; init; } = "";

    public bool HasSource { get; init; }

    /// <summary>The source was inspected and has no picture.</summary>
    public bool SourceHasNoVideo { get; init; }

    /// <summary>The source has sound, or could not be inspected (in which case it is taken to have some).</summary>
    public bool SourceMayHaveAudio { get; init; } = true;

    /// <summary>How long the main video's file is, in seconds; 0 while that is not known.</summary>
    public double MainMediaSeconds { get; init; }

    /// <summary>The main video's clips in the order they come on the sequence.</summary>
    public IReadOnlyList<MainClip> MainClips { get; init; } = [];

    /// <summary>What to add to a time in the main video's file to get the time it is on the sequence, for its first clip.</summary>
    public double MainShift { get; init; }

    /// <summary>The main video has been cut into more than one clip.</summary>
    public bool IsMainSpliced { get; init; }

    /// <summary>The main video is the whole sequence, untouched.</summary>
    public bool IsMainWholeSequence { get; init; } = true;

    /// <summary>
    /// Where the main video sits on the frame, in the frame's pixels: its left, top, width and height. The
    /// zoom and the offsets it is set with are worked out into this rectangle before the builder is asked, in
    /// one place (the view model's GetCenterRect), which the layout pane draws from too; so what is dragged
    /// there and what is encoded cannot differ. The builder scales it down for Live Preview and emits it as numbers.
    /// </summary>
    public (int X, int Y, int Width, int Height) CenterRect { get; init; }

    // ----- The Layer Engine: what is laid on the canvas -----

    /// <summary>Whether the Layer Engine composes the picture. Off, the file's own frames are filtered as they are.</summary>
    public bool FrameEngine { get; init; }

    /// <summary>The pictures in stacking order, bottom first, the main video among them. Copies; loading clips are left out.</summary>
    public IReadOnlyList<Layer> Stack { get; init; } = [];

    /// <summary>The main video as a layer: its look, filters, keyframes and how it is turned. The same copy that is in <see cref="Stack"/>.</summary>
    public required Layer MainLayer { get; init; }

    /// <summary>The blurred background is switched off, leaving the black canvas.</summary>
    public bool BackgroundHidden { get; init; }

    public int BlurRadius { get; init; } = 20;
    public int BlurPasses { get; init; } = 2;
    public double BackgroundDim { get; init; } = -0.15;

    /// <summary>The box the auto-captions are drawn in.</summary>
    public required Layer CaptionLayer { get; init; }

    public bool AutoCaptions { get; init; }

    /// <summary>The caption file an export draws.</summary>
    public string CaptionsFilePath { get; init; } = "";

    // ----- Filters on the whole picture -----

    public bool Deinterlace { get; init; }
    public bool Denoise { get; init; }
    public bool TonemapToSdr { get; init; }
    public string LutPath { get; init; } = "";

    /// <summary>The grading filters for the colour sliders, already written; empty when nothing is graded.</summary>
    public IReadOnlyList<string> ColorFilters { get; init; } = [];

    public bool FadeIn { get; init; }
    public bool FadeOut { get; init; }

    // ----- Cuts, chapters, subtitles -----

    /// <summary>The kept segments in order, overlaps merged and skipped ones left out; empty for the whole sequence.</summary>
    public IReadOnlyList<ExportSegment> Segments { get; init; } = [];

    /// <summary>There are cut segments on the timeline at all, kept or skipped.</summary>
    public bool HasCutSegments { get; init; }

    public bool ChapterMarkers { get; init; }
    public bool ChaptersAtCuts { get; init; }
    public string CutsFilePath { get; init; } = "";
    public string ChaptersFilePath { get; init; } = "";

    /// <summary>The subtitle streams to carry over as they are, by their number ("0:s:N").</summary>
    public IReadOnlyList<int> SoftSubtitleStreams { get; init; } = [];

    /// <summary>The subtitle streams to draw into the picture.</summary>
    public IReadOnlyList<int> BurnedSubtitleStreams { get; init; } = [];

    // ----- Sound -----

    /// <summary>The audio tracks that reach the output, in order.</summary>
    public IReadOnlyList<ExportAudioTrack> AudioTracks { get; init; } = [];

    /// <summary>The clips whose own sound goes into the output: video layers that have any, and sound clips. Copies.</summary>
    public IReadOnlyList<Layer> LayerAudio { get; init; } = [];

    /// <summary>Sound follows its picture on the timeline; unlinked, a video layer's sound may have been slipped.</summary>
    public bool AudioLinked { get; init; } = true;

    public bool MergeAudioTracks { get; init; }
    public bool NormalizeAudio { get; init; }

    /// <summary>The old one-box ducking of the first track under the second. Only set by presets from before ducking was per track.</summary>
    public bool DuckAudio { get; init; }

    /// <summary>How far a sound set to Auto-Duck is turned down while a voice speaks, in decibels.</summary>
    public double DuckAmountDb { get; init; } = -15;

    /// <summary>The codec of a mixed track.</summary>
    public string MixedAudioCodec { get; init; } = "aac";

    public string AudioBitrate { get; init; } = "160k";

    // The session's voiceover (Encoder Mode's): a second input, mixed into the first track.
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

    /// <summary>The options that switch hardware decoding on, with a space after them; empty when it is off.</summary>
    public string HardwareDecodeArguments { get; init; } = "";

    /// <summary>The graphics card can blur the background (OpenCL): it was tested and it works here.</summary>
    public bool OpenClBlur { get; init; }

    /// <summary>The options that give a graph an OpenCL device.</summary>
    public string OpenClDeviceArguments { get; init; } = "";

    /// <summary>The video encoder is Copy: the picture is passed through, and no filter can touch it.</summary>
    public bool CopyVideoEncoder { get; init; }

    /// <summary>The output is a GIF or a WebP, going by the format chosen.</summary>
    public bool IsAnimatedOutput { get; init; }

    /// <summary>The video encoder and its settings, as command-line arguments.</summary>
    public string VideoCodecArguments { get; init; } = "";

    /// <summary>The container chosen, for an output whose name has no extension.</summary>
    public string Container { get; init; } = "mp4";

    public bool WebOptimized { get; init; }

    /// <summary>The file the output is written to.</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>
    /// Preview Subtitles: a caption file timed to the sequence, for Live Preview to draw over the picture in
    /// the captions' own style. Blank while the preview is off.
    /// </summary>
    public string LiveCaptionsPath { get; init; } = "";

    /// <summary>
    /// The files named in this state that were there when it was compiled (masks, LUTs, the caption file). The
    /// builder leaves out a mask or a LUT whose file is missing, and asks this instead of the disk.
    /// </summary>
    public IReadOnlySet<string> ExistingFiles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A finished export command, and which of the lists FFmpeg reads beside it the command refers to.</summary>
/// <param name="UsesCutsFile">The command reads the cuts from <see cref="SequenceExportState.CutsFilePath"/>, which has to be written before it runs.</param>
/// <param name="UsesChaptersFile">The command reads its chapters from <see cref="SequenceExportState.ChaptersFilePath"/>.</param>
public sealed record ExportCommand(string CommandLine, bool UsesCutsFile, bool UsesChaptersFile);

/// <summary>
/// Turns a sequence into FFmpeg: the export's command line, the graph Live Preview runs in the player, and the
/// short encode of Render Preview. Three functions of a <see cref="SequenceExportState"/>, which is all they
/// read; the same state gives the same text, whatever the application is doing meanwhile.
///
/// Every number written into a filter or an option goes through <see cref="Number"/> or one of its kin, which
/// write it the way FFmpeg reads it (a point for the decimals) whatever the language of the machine.
/// </summary>
public static class FilterGraphBuilder
{
    /// <summary>The export: the whole command line, from "ffmpeg" to the output file.</summary>
    public static ExportCommand BuildExportCommand(SequenceExportState state)
    {
        var pass = new Pass(state);
        var command = pass.BuildFfmpegCommand();
        return new ExportCommand(command, pass.UsesCutsFile, pass.UsesChaptersFile);
    }

    /// <summary>
    /// The graph for mpv's lavfi-complex, from its video track to the screen; empty when the export would not
    /// filter the picture at all.
    /// </summary>
    /// <param name="surfaceWidth">Width of the player on screen, in screen pixels: the picture is not built larger than it is shown.</param>
    public static string BuildLiveGraph(SequenceExportState state, double surfaceWidth, double surfaceHeight) =>
        new Pass(state).BuildLiveFilterGraph(surfaceWidth, surfaceHeight);

    /// <summary>Render Preview: a short, small encode of the given stretches of the timeline.</summary>
    /// <param name="ranges">The stretches to show, joined end to end.</param>
    /// <param name="captionsPath">An auto-caption file made for these stretches, or null.</param>
    public static string BuildPreviewCommand(
        SequenceExportState state, string outputPath, IReadOnlyList<(double Start, double End)> ranges, int percent, string? captionsPath) =>
        new Pass(state).BuildPreviewCommand(outputPath, ranges, percent, captionsPath);

    // ----- Keyframes -----
    // A keyframe pins a clip's place, size and turn at once, so a clip that only moves has keyframes on its
    // size and its turn that all say the same thing. Compiled as they stand, those would be worked out afresh
    // for every frame (a scale and a rotation per frame, of a picture that does neither). So the compiler
    // compares first: a property whose keyframes all hold one value is not a motion, and is written as the
    // plain number it is; and within a property that does change, a stretch between two keyframes that hold
    // the same value adds nothing to the expression.

    // Two values closer than this are the same: a fiftieth of a pixel on a 1920 frame, a hundredth of a degree.
    private const double SamePlace = 0.00001;
    private const double SameAngle = 0.01;

    /// <summary>
    /// Takes the keyframes off every property of a layer that they do not change, leaving the property at
    /// the one value they hold. For a copy of a layer that is being prepared for a
    /// <see cref="SequenceExportState"/>: what is then compiled for it is only what moves.
    /// </summary>
    /// <returns>How many properties were folded into plain values.</returns>
    public static int FoldConstantKeys(Layer layer)
    {
        var folded = 0;
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            var keys = layer.GetKeys(property);
            if (keys.Count == 0)
                continue;

            var (low, high) = (keys.Min(k => k.Value), keys.Max(k => k.Value));
            if (high - low >= (property == KeyProperty.Rotation ? SameAngle : SamePlace))
                continue;

            var value = keys[0].Value;
            layer.SetKeys(property, null);
            layer.SetValue(property, value);
            folded++;
        }

        return folded;
    }

    /// <summary>
    /// A stretch's progress (an expression that runs from 0 to 1) bent into an easing curve. All closed-form:
    /// FFmpeg evaluates it per frame like any other arithmetic, and Linear is the progress itself.
    /// </summary>
    public static string FormatEasing(string progressExpr, EasingType type) => type switch
    {
        EasingType.EaseInOut => $"((1-cos({progressExpr}*PI))/2)",
        EasingType.EaseIn => $"(1-cos({progressExpr}*(PI/2)))",
        EasingType.EaseOut => $"sin({progressExpr}*(PI/2))",
        EasingType.Smoothstep => $"({progressExpr}*{progressExpr}*(3-2*{progressExpr}))",
        _ => progressExpr,
    };

    // HDR (PQ or HLG, BT.2020) to SDR BT.709: linearize, map the primaries, compress the highlights with the
    // Hable curve, then go back to 8-bit 4:2:0 for the encoder.
    private const string TonemapFilters =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    // The frame size assumed while the source's is not known.
    private const int DefaultSourceWidth = 1920;
    private const int DefaultSourceHeight = 1080;

    /// <summary>A number as FFmpeg reads it: up to three decimals, with a point, whatever the machine's language.</summary>
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string value) => $"\"{value}\"";

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

    /// <summary>
    /// The stretches of a part of the sequence that the main video is there for, as a condition on t: for
    /// switching on, between them, what comes from it. Clips that follow each other without a gap count as one stretch.
    /// </summary>
    /// <param name="from">Where the part begins: its clock starts there.</param>
    /// <param name="clips">The main video's clips, in the order they come on the sequence.</param>
    public static string BuildMainGate(IReadOnlyList<MainClip> clips, double from, double to)
    {
        var gate = new StringBuilder();
        var (start, end) = (double.NaN, double.NaN);
        void Close()
        {
            if (double.IsNaN(start))
                return;
            if (gate.Length > 0)
                gate.Append('+');
            gate.Append("gte(t,").Append(Number(start - from)).Append(")*lt(t,").Append(Number(end - from)).Append(')');
        }

        foreach (var clip in clips)
        {
            var (a, b) = (Math.Max(clip.Start, from), Math.Min(clip.End, to));
            if (b - a < 0.001)
                continue;

            if (!double.IsNaN(end) && a - end < 0.001)
            {
                end = b;
                continue;
            }

            Close();
            (start, end) = (a, b);
        }

        Close();
        return gate.Length > 0 ? gate.ToString() : "0";
    }

    /// <summary>How a command names a caption file: this is what is looked for to tell that a command draws one.</summary>
    public static string CaptionFilter(string assPath) => $"ass='{EscapeFilterPath(assPath)}'";

    /// <summary>
    /// A file path for use inside a quoted filter option. Even in quotes FFmpeg treats ":" as an option
    /// separator, and a quote ends the string.
    /// </summary>
    private static string EscapeFilterPath(string path) =>
        path.Trim().Trim('"').Replace('\\', '/').Replace(":", @"\:").Replace("'", @"'\\\''");

    /// <summary>
    /// One building of one graph. It holds the state it was given and, while it works, which kind of graph it
    /// is building (the export's, or the smaller one for Live Preview). It is made for a call and dropped after it:
    /// nothing is kept from one call to the next.
    /// </summary>
    private sealed class Pass(SequenceExportState state)
    {
    private SequenceExportState S { get; } = state;

    /// <summary>Set while the export's command is built: it reads the cuts, or the chapters, from a list beside it.</summary>
    public bool UsesCutsFile => _usesCutsFile;

    public bool UsesChaptersFile => _usesChaptersFile;

    private bool _usesCutsFile;
    private bool _usesChaptersFile;

    // The state, under the names the code below reads it by.
    private double SequenceSeconds => S.SequenceSeconds;
    private double SourceFrameRate => S.SourceFrameRate;
    private int FrameWidth => S.FrameWidth;
    private int FrameHeight => S.FrameHeight;
    private int SourceWidth => S.SourceWidth;
    private int SourceHeight => S.SourceHeight;
    private string OutputWidth => S.OutputWidth;
    private string OutputHeight => S.OutputHeight;
    private bool UseVerticalResolution => S.UseVerticalResolution;
    private bool HasCrop => S.HasCrop;
    private string LocalMediaPath => S.LocalMediaPath;
    private bool HasSource => S.HasSource;
    private double MainMediaSeconds => S.MainMediaSeconds;
    private double MainShift => S.MainShift;
    private bool IsMainSpliced => S.IsMainSpliced;
    private bool IsMainWholeSequence => S.IsMainWholeSequence;
    private bool FrameEngine => S.FrameEngine;
    private int BlurRadius => S.BlurRadius;
    private int BlurPasses => S.BlurPasses;
    private double BackgroundDim => S.BackgroundDim;
    private Layer CaptionLayer => S.CaptionLayer;
    private bool AutoCaptions => S.AutoCaptions;
    private string CaptionsFilePath => S.CaptionsFilePath;
    private bool Deinterlace => S.Deinterlace;
    private bool Denoise => S.Denoise;
    private string LutPath => S.LutPath;
    private bool FadeIn => S.FadeIn;
    private bool FadeOut => S.FadeOut;
    private bool ChapterMarkers => S.ChapterMarkers;
    private bool ChaptersAtCuts => S.ChaptersAtCuts;
    private string CutsFilePath => S.CutsFilePath;
    private string ChaptersFilePath => S.ChaptersFilePath;
    private bool AudioLinked => S.AudioLinked;
    private bool MergeAudioTracks => S.MergeAudioTracks;
    private bool NormalizeAudio => S.NormalizeAudio;
    private bool DuckAudio => S.DuckAudio;
    private double DuckAmountDb => S.DuckAmountDb;
    private string MixedAudioCodec => S.MixedAudioCodec;
    private string AudioBitrate => S.AudioBitrate;
    private bool IncludeVoiceover => S.IncludeVoiceover;
    private string VoiceoverPath => S.VoiceoverPath;
    private double VoiceoverDuration => S.VoiceoverDuration;
    private double VoiceoverTrimStart => S.VoiceoverTrimStart;
    private double VoiceoverTrimEnd => S.VoiceoverTrimEnd;
    private double VoiceoverStartSeconds => S.VoiceoverStartSeconds;
    private double VoiceoverGainDb => S.VoiceoverGainDb;
    private bool HardwareDecoding => S.HardwareDecodeArguments.Length > 0;
    private bool IsAnimatedOutput => S.IsAnimatedOutput;
    private string Container => S.Container;
    private bool WebOptimized => S.WebOptimized;

    private IReadOnlyList<MainClip> GetMainClips() => S.MainClips;
    private IReadOnlyList<ExportSegment> GetMergedSegments() => S.Segments;
    private double GetOutputDuration() => S.OutputDuration;
    private List<ExportAudioTrack> GetOutputAudioTracks() => [.. S.AudioTracks];
    private List<Layer> GetLayerAudioSources() => [.. S.LayerAudio];
    private List<Layer> GetStack() => [.. S.Stack];
    private string? GetTargetFramerate() => S.TargetFramerate;
    private string? GetSampleAspectRatio() => S.SampleAspectRatio;
    private (int Width, int Height)? GetRequestedSize() => S.RequestedSize;
    private (int Width, int Height)? GetOutputSize() => S.OutputSize;
    private (double Left, double Top, double Right, double Bottom) GetCropFractions() => S.CropFractions;
    private (int X, int Y, int Width, int Height) GetCenterRect() => S.CenterRect;
    private List<string> BuildColorFilters() => [.. S.ColorFilters];
    private string BuildVideoCodecArguments() => S.VideoCodecArguments;
    private string BuildMainGate(double from, double to) => FilterGraphBuilder.BuildMainGate(S.MainClips, from, to);

    /// <summary>Whether a file named in the state was there when the state was compiled.</summary>
    private bool Exists(string path) => S.ExistingFiles.Contains(path);

        // loudnorm works at 192 kHz internally and would hand that rate to the encoder; bring it back down.
        private const string NormalizeFilters = "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000";

        // Presses the first input down while the second (the side chain) is above the threshold.
        private const string DuckingFilter = "sidechaincompress=threshold=0.05:ratio=8:attack=20:release=400";

        private const double FadeSeconds = 1;

        public string BuildFfmpegCommand()
        {
            var input = string.IsNullOrWhiteSpace(LocalMediaPath) ? "<source>" : LocalMediaPath;
            var output = S.OutputPath;
            var segments = GetMergedSegments();
            var duration = GetOutputDuration();

            // What the file can hold depends on the name it is saved under, which the user may have changed by hand.
            var extension = Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0)
                extension = Container;

            // GIF and WebP are picture formats: always encoded, never with sound.
            var animated = extension is "gif" or "webp";

            var copyVideo = !animated && S.CopyVideoEncoder;
            var tracks = animated ? [] : GetOutputAudioTracks();
            var mixAudio = MergeAudioTracks && tracks.Count > 1;

            // The voiceover from the Voiceover Studio, as one more input. It is mixed into the first audio
            // track, which therefore cannot be a copied one.
            var voiceover = !animated && IncludeVoiceover ? VoiceoverPath : null;
            // So is the sound of video layers, for the same reason.
            var layerAudio = animated || tracks.Count == 0 ? [] : GetLayerAudioSources();
            var copyAnyAudio = !mixAudio && tracks.Where((_, index) => (voiceover is null && layerAudio.Count == 0) || index > 0).Any(t => t.IsPassthrough);

            // Ducking needs the game and microphone tracks decoded, which a mix always does.
            var duckAudio = DuckAudio && tracks.Count >= 2 && (mixAudio || tracks.Take(2).All(t => !t.IsPassthrough));

            // A copied stream cannot pass through filter_complex, so as soon as any stream is copied
            // the cuts are made by the concat demuxer instead, which works on packets.
            var cutWithDemuxer = segments.Count > 0 && (copyVideo || copyAnyAudio);
            var cutWithFilters = segments.Count > 0 && !cutWithDemuxer;

            // A bare "ffmpeg" means the local copy; an ffmpeg.exe chosen in the settings is spelled out.
            var ffmpeg = S.FfmpegExecutable;
            var args = new List<string> { ffmpeg, "-hide_banner", "-y" };

            if (HardwareDecoding && !copyVideo)
                args.Add(S.HardwareDecodeArguments.TrimEnd());

            if (cutWithDemuxer)
            {
                _usesCutsFile = true;
                args.Add($"-f concat -safe 0 -i {Quote(CutsFilePath)}");
            }
            else
            {
                args.Add($"-i {Quote(input)}");
            }

            // One chapter per kept segment, read from a metadata file given as a second input.
            var chaptersFromCuts = ChaptersAtCuts && segments.Count > 0 && !animated;
            if (chaptersFromCuts)
            {
                _usesChaptersFile = true;
                args.Add($"-i {Quote(ChaptersFilePath)}");
            }

            int? voiceoverInput = null;
            if (voiceover is not null)
            {
                // Input 0 is the video; the chapters file, when there is one, is input 1.
                voiceoverInput = chaptersFromCuts ? 2 : 1;
                args.Add($"-i {Quote(voiceover)}");
            }

            var burnFilters = copyVideo || IsMainSpliced ? [] : BuildSubtitleBurnFilters(input);
            var framerate = copyVideo ? null : GetTargetFramerate();

            // Applied once to the finished picture: the fades, and for GIF the palette (which has to come last).
            var onceFilters = copyVideo ? [] : BuildWholeVideoFilters(duration);

            // Auto-captions are timed against the finished output, so they are drawn last of all.
            if (AutoCaptions && !copyVideo)
                onceFilters.Add(BuildCaptionOverlay(CaptionsFilePath));
            var lastFilters = extension == "gif" ? new List<string> { "split[gs0][gs1];[gs0]palettegen[gp];[gs1][gp]paletteuse" } : [];

            // Every label in the graph is unique: v/a per segment, ac per track after the join, then the outputs.
            var graph = new List<string>();
            var maps = new List<string>();
            string? simpleVideoFilters = null;

            // Audio that was edited on the timeline (slipped, silenced in places), that sits on the sequence
            // somewhere other than its start, that is ducked under a voice, or that has layer sound to take in, is
            // made ready first, each track on a chain of its own; what follows then works from that instead of
            // from the input. Not with cuts made by the demuxer, which copies packets: no filter can be put
            // in front of those.
            var preparedAudio = new Dictionary<int, string[]>();
            if (!cutWithDemuxer)
            {

                // Auto-duck. The voices are mixed into one signal, and each ducked sound gets a copy of it to be
                // pressed down by. A copy that nothing listened to would be an error, so they are counted first.
                bool IsDucked(ExportAudioTrack track) => track is { AutoDuck: true, IsVoice: false } && !track.IsSilentBase;
                var ducked = tracks.Count(IsDucked) + layerAudio.Count(l => l is { AutoDuck: true, IsVoice: false });
                var keys = new Queue<string>();
                if (ducked > 0)
                {
                    var voices = new List<string>();
                    foreach (var track in tracks.Where(t => t.IsVoice && !t.IsSilentBase))
                        voices.Add(PlaceMainAudio(graph, $"[0:a:{track.Index}]", track.BuildEditChain(), track.IsMono, $"dvsrc{track.Index}") + "anull");
                    voices.AddRange(layerAudio.Where(l => l.IsVoice).Select(BuildLayerAudio));

                    // The voiceover is timed against the finished video, which is the sequence only while nothing is cut out of it.
                    if (voiceoverInput is { } voiceInput && segments.Count == 0)
                        voices.Add($"[{voiceInput}:a:0]{BuildVoiceoverChain()}");

                    if (voices.Count > 0)
                    {
                        for (var n = 0; n < voices.Count; n++)
                            graph.Add($"{voices[n]},aresample=48000,apad[dvoice{n}]");

                        var key = "[dvoice0]";
                        if (voices.Count > 1)
                        {
                            key = "[dkey]";
                            graph.Add($"{string.Concat(voices.Select((_, n) => $"[dvoice{n}]"))}amix=inputs={voices.Count}:normalize=0[dkey]");
                        }

                        if (ducked == 1)
                        {
                            keys.Enqueue(key);
                        }
                        else
                        {
                            var copies = Enumerable.Range(0, ducked).Select(n => $"[dkey{n}]").ToList();
                            graph.Add($"{key}asplit={ducked}{string.Concat(copies)}");
                            copies.ForEach(keys.Enqueue);
                        }
                    }
                }

                for (var k = 0; k < tracks.Count; k++)
                {
                    var track = tracks[k];
                    var silent = track.IsSilentBase;
                    var edits = silent ? [] : track.BuildEditChain();
                    var mixedIn = k == 0 ? layerAudio : [];
                    var duck = keys.Count > 0 && IsDucked(track);
                    if (edits.Count == 0 && mixedIn.Count == 0 && !duck && !silent && IsMainWholeSequence)
                        continue;

                    var label = $"[0:a:{track.Index}]";
                    if (silent)
                    {
                        // The sequence has no sound of its own: silence as long as it is, for the rest to be mixed into.
                        graph.Add($"anullsrc=r=48000:cl=stereo:d={Number(Math.Max(SequenceSeconds, 0.1))}[asilent]");
                        label = "[asilent]";
                    }
                    else
                    {
                        label = PlaceMainAudio(graph, label, edits, track.IsMono, $"aedit{track.Index}");
                    }

                    if (duck)
                    {
                        graph.Add($"{label}{keys.Dequeue()}{DuckFilter}[aduck{track.Index}]");
                        label = $"[aduck{track.Index}]";
                    }

                    if (mixedIn.Count > 0)
                    {
                        for (var n = 0; n < mixedIn.Count; n++)
                        {
                            if (keys.Count > 0 && mixedIn[n] is { AutoDuck: true, IsVoice: false })
                            {
                                graph.Add($"{BuildLayerAudio(mixedIn[n])}[alraw{n}]");
                                graph.Add($"[alraw{n}]{keys.Dequeue()}{DuckFilter}[alayer{n}]");
                            }
                            else
                            {
                                graph.Add($"{BuildLayerAudio(mixedIn[n])}[alayer{n}]");
                            }
                        }

                        graph.Add($"{label}{string.Concat(mixedIn.Select((_, n) => $"[alayer{n}]"))}amix=inputs={mixedIn.Count + 1}:duration=first:normalize=0[amixed{track.Index}]");
                        label = $"[amixed{track.Index}]";
                    }

                    // A filter's output can be read once; with cuts it is wanted once per segment.
                    var uses = cutWithFilters ? segments.Count : 1;
                    if (uses > 1)
                    {
                        preparedAudio[track.Index] = Enumerable.Range(0, uses).Select(u => $"[aprep{track.Index}_{u}]").ToArray();
                        graph.Add($"{label}asplit={uses}{string.Concat(preparedAudio[track.Index])}");
                    }
                    else
                    {
                        preparedAudio[track.Index] = [label];
                    }
                }
            }

            if (cutWithFilters)
            {
                // Every kept stretch is cut out of the video and each audio track with its timestamps reset, then
                // the pieces are joined with one concat filter so all streams stay locked together.
                var concatInputs = new StringBuilder();
                for (var i = 0; i < segments.Count; i++)
                {
                    var range = $"start={Seconds(segments[i].Start)}:end={Seconds(segments[i].End)}";
                    var suffix = i.ToString(CultureInfo.InvariantCulture);
                    var chain = new List<string>();
                    if (FrameEngine)
                    {
                        // The engine composes this stretch of the sequence on a clock that starts at 0. Burned-in
                        // subtitles are placed by the main video's own time, which the clock is set to while they are drawn.
                        chain.AddRange(BuildVideoFilters(suffix, segments[i].Start.TotalSeconds, segments[i].End.TotalSeconds));
                        if (burnFilters.Count > 0)
                        {
                            chain.Add($"setpts=PTS{Signed(segments[i].Start.TotalSeconds - MainShift)}/TB");
                            chain.AddRange(burnFilters);
                            chain.Add("setpts=PTS-STARTPTS");
                        }
                    }
                    else
                    {
                        // Burned-in subtitles are placed by timestamp, so they must be drawn before the timestamps are reset.
                        var frameFilters = BuildVideoFilters(suffix);
                        chain.Add($"trim={range}");
                        if (burnFilters.Count > 0)
                        {
                            chain.AddRange(frameFilters);
                            chain.AddRange(burnFilters);
                            chain.Add("setpts=PTS-STARTPTS");
                        }
                        else
                        {
                            chain.Add("setpts=PTS-STARTPTS");
                            chain.AddRange(frameFilters);
                        }
                    }

                    if (framerate is not null)
                        chain.Add($"fps=fps={framerate}");

                    graph.Add($"[0:v:0]{string.Join(",", chain)}[v{i}]");
                    concatInputs.Append($"[v{i}]");

                    foreach (var track in tracks)
                    {
                        var from = preparedAudio.TryGetValue(track.Index, out var ready) ? ready[i] : $"[0:a:{track.Index}]";
                        graph.Add($"{from}atrim={range},asetpts=PTS-STARTPTS[a{track.Index}_{i}]");
                        concatInputs.Append($"[a{track.Index}_{i}]");
                    }
                }

                var finishing = onceFilters.Concat(lastFilters).ToList();
                var joinedVideo = finishing.Count > 0 ? "[vcat]" : "[vout]";
                graph.Add($"{concatInputs}concat=n={segments.Count}:v=1:a={tracks.Count}{joinedVideo}{string.Concat(tracks.Select(t => $"[ac{t.Index}]"))}");
                if (finishing.Count > 0)
                    graph.Add($"[vcat]{string.Join(",", finishing)}[vout]");

                maps.Add("-map \"[vout]\"");
            }
            else
            {
                maps.Add("-map 0:v:0");

                if (!copyVideo)
                {
                    var chain = BuildVideoFilters(labelSuffix: "", 0, SequenceSeconds);

                    // Burned-in subtitles are placed by the main video's own time: the clock is set to it while
                    // they are drawn, wherever on the sequence the main video has been put.
                    var shift = MainShift;
                    if (burnFilters.Count > 0 && Math.Abs(shift) > 0.001)
                    {
                        chain.Add($"setpts=PTS{Signed(-shift)}/TB");
                        chain.AddRange(burnFilters);
                        chain.Add($"setpts=PTS{Signed(shift)}/TB");
                    }
                    else
                    {
                        chain.AddRange(burnFilters);
                    }

                    chain.AddRange(onceFilters);
                    if (framerate is not null)
                        chain.Add($"fps=fps={framerate}");
                    chain.AddRange(lastFilters);
                    if (chain.Count > 0)
                        simpleVideoFilters = string.Join(",", chain);
                }
            }

            var audioOptions = BuildAudio(tracks, mixAudio, duckAudio, cutWithFilters, duration, voiceoverInput, preparedAudio, graph, maps);

            // Soft subtitles cannot follow filter-based cuts, so they are carried over for uncut and demuxer-cut encodes only.
            var softSubtitles = cutWithFilters || animated ? [] : S.SoftSubtitleStreams.ToList();
            foreach (var subtitle in softSubtitles)
                maps.Add($"-map 0:s:{subtitle}?");

            if (graph.Count > 0)
                args.Add($"-filter_complex \"{string.Join(";", graph)}\"");
            args.AddRange(maps);
            if (simpleVideoFilters is not null)
                args.Add($"-vf \"{simpleVideoFilters}\"");

            args.Add(extension switch
            {
                "gif" => "-c:v gif -loop 0",
                "webp" => "-c:v libwebp -q:v 75 -loop 0",
                _ => BuildVideoCodecArguments(),
            });
            args.AddRange(audioOptions);
            if (animated)
                args.Add("-an");

            if (softSubtitles.Count > 0)
            {
                var subtitleCodec = extension switch { "mp4" or "mov" or "m4v" => "mov_text", "webm" => "webvtt", _ => "copy" };
                args.Add($"-c:s {subtitleCodec}");
            }

            // Source chapter times no longer line up once sections have been removed.
            args.Add(chaptersFromCuts ? "-map_chapters 1"
                : ChapterMarkers && segments.Count == 0 && !animated ? "-map_chapters 0"
                : "-map_chapters -1");

            if (WebOptimized && extension is "mp4" or "mov" or "m4v")
                args.Add("-movflags +faststart");

            args.Add(Quote(output));
            return WithOpenClDevice(string.Join(" ", args));
        }

        /// <summary>
        /// The filter graph Live Preview runs on the picture, for mpv's lavfi-complex: the same per-frame chain the
        /// export uses (crop, the Frame &amp; Layer Engine, scaling, color, LUT), with burned-in subtitles, from the
        /// video track to the screen. Empty when the export would not filter the picture at all (Copy), which
        /// is then what the preview shows too.
        ///
        /// The cuts are not part of it: the preview stays on the source's own timeline, where Play only cut
        /// segments does the skipping. Fades are placed by the length of the output and are left out, and
        /// auto-captions are drawn only once they have been transcribed and while nothing is cut, which is when
        /// their times are the source's.
        /// </summary>
        /// <param name="surfaceWidth">Width of the player on screen, in screen pixels: the picture is not built larger than it is shown.</param>
        public string BuildLiveFilterGraph(double surfaceWidth, double surfaceHeight)
        {
            if (!HasSource || S.SourceHasNoVideo || (S.CopyVideoEncoder && !IsAnimatedOutput))
                return "";

            // The whole graph is built for a frame the size the player shows, not the size of the export: a 4K
            // layout watched in a 900-pixel player is composed at a quarter of its width and a sixteenth of the
            // work. The scale goes in steps, so that resizing the window a little does not rebuild the graph.
            var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
            var shown = surfaceWidth > 0 && surfaceHeight > 0 ? Math.Min(surfaceWidth / frameWidth, surfaceHeight / frameHeight) : 1;
            _liveScale = LiveScaleSteps.FirstOrDefault(step => step >= shown, 1);

            _buildingLiveGraph = true;
            try
            {
                // The player's clock is the sequence's, so the whole of it is composed as one stretch from 0.
                var chain = BuildVideoFilters(labelSuffix: "", 0, SequenceSeconds);
                var (burn, shift) = (IsMainSpliced ? [] : BuildSubtitleBurnFilters(LocalMediaPath), MainShift);
                if (burn.Count > 0 && Math.Abs(shift) > 0.001)
                {
                    chain.Add($"setpts=PTS{Signed(-shift)}/TB");
                    chain.AddRange(burn);
                    chain.Add($"setpts=PTS{Signed(shift)}/TB");
                }
                else
                {
                    chain.AddRange(burn);
                }

                // Preview Subtitles: a caption file made for the preview, timed to the sequence. Otherwise the one
                // the last encode drew, while nothing is cut (which is when its times are the sequence's).
                if (S.LiveCaptionsPath.Length > 0 && Exists(S.LiveCaptionsPath))
                    chain.Add(BuildCaptionOverlay(S.LiveCaptionsPath));
                else if (AutoCaptions && !S.HasCutSegments && Exists(CaptionsFilePath))
                    chain.Add(BuildCaptionOverlay(CaptionsFilePath));

                return chain.Count == 0 ? "" : $"[vid1]{string.Join(",", chain)}[vo]";
            }
            finally
            {
                (_buildingLiveGraph, _liveScale) = (false, 1);
            }
        }

        // What makes the live graph cheaper than the export's, while showing the same layers in the same places:
        //   - everything is sized for the player (see above), sources being scaled down before anything is done to them;
        //   - the blurred background is blurred at a quarter of that size and scaled back up, which a blur hides;
        //   - a layer's rounded or feathered outline is worked out once and reused, where the export works it
        //     out again for every frame;
        //   - a source faster than 30 frames a second is previewed at half its rate.
        private static readonly double[] LiveScaleSteps = [0.25, 1 / 3.0, 0.5, 0.75, 1];

        private bool _buildingLiveGraph;
        private double _liveScale = 1;

        /// <summary>A size in output pixels as the graph being built needs it: as it is for the export, scaled (and kept even) for Live Preview.</summary>
        private int Sized(int pixels) => _liveScale >= 1 ? pixels : Math.Max((int)Math.Round(pixels * _liveScale / 2) * 2, 2);

        /// <summary>A distance in output pixels, scaled the same way but not rounded to an even number.</summary>
        private int Placed(int pixels) => _liveScale >= 1 ? pixels : (int)Math.Round(pixels * _liveScale);

        // ----- Render preview -----

        /// <summary>
        /// A short, small encode that shows what the current filters do to the picture: the same per-frame
        /// chain, layers and burned-in subtitles as the real command, then scaled down. Speed matters more
        /// than quality here, so it always uses the fastest software settings.
        /// </summary>
        /// <param name="ranges">The stretches of the timeline to show, joined end to end: one, or several cut segments.</param>
        /// <param name="captionsPath">An auto-caption file made for these stretches, or null.</param>
        public string BuildPreviewCommand(string outputPath, IReadOnlyList<(double Start, double End)> ranges, int percent, string? captionsPath)
        {
            var burnFilters = IsMainSpliced ? [] : BuildSubtitleBurnFilters(LocalMediaPath);

            // What is done once, to the finished picture. Fades are left out: they are placed by the length of
            // the whole output, which a few seconds do not have.
            var finishing = BuildWholeVideoFilters(duration: 0);
            if (captionsPath is not null)
                finishing.Add(BuildCaptionOverlay(captionsPath));
            if (GetTargetFramerate() is { } framerate)
                finishing.Add($"fps=fps={framerate}");

            var factor = Number(Math.Clamp(percent, 5, 100) / 100.0);
            finishing.Add($"scale=trunc(iw*{factor}/2)*2:trunc(ih*{factor}/2)*2");
            finishing.Add("format=yuv420p");

            var ffmpeg = S.FfmpegExecutable;
            var hardware = HardwareDecoding ? S.HardwareDecodeArguments : "";
            const string encode = "-c:v libx264 -preset ultrafast -crf 24 -c:a aac -b:a 128k -movflags +faststart";
            var hasSound = S.SourceMayHaveAudio;

            if (!FrameEngine)
            {
                // Without the engine the timeline is the file's own: each stretch is simply a stretch of the file.
                var (startSeconds, durationSeconds) = (ranges[0].Start, ranges[0].End - ranges[0].Start);
                var chain = BuildVideoFilters(labelSuffix: "");
                chain.AddRange(burnFilters);
                chain.AddRange(finishing);

                if (ranges.Count > 1)
                {
                    // Each stretch is opened as an input of its own, already cut to length, and the stretches are
                    // joined before anything is done to the picture: what is filtered is the trimmed timeline.
                    var cutInputs = string.Join(" ", ranges.Select(r => $"{hardware}-ss {Number(r.Start)} -t {Number(r.End - r.Start)} -i {Quote(LocalMediaPath)}"));
                    var joined = $"{string.Concat(ranges.Select((_, i) => $"[{i}:v:0]"))}concat=n={ranges.Count}:v=1:a=0[joined];[joined]{string.Join(",", chain)}[v]";
                    if (hasSound)
                        joined += $";{string.Concat(ranges.Select((_, i) => $"[{i}:a:0]"))}concat=n={ranges.Count}:v=0:a=1[a]";

                    return WithOpenClDevice($"{ffmpeg} -hide_banner -y {cutInputs} -filter_complex \"{joined}\" -map \"[v]\"{(hasSound ? " -map \"[a]\"" : "")} {encode} {Quote(outputPath)}");
                }

                // Seeking before the input is fast. Burned-in subtitles need the original timestamps, though,
                // so with those the seek comes after the input: slower, but the text lands on the right frames.
                var seek = $"-ss {Number(startSeconds)} -t {Number(durationSeconds)}";
                var input = hardware + (burnFilters.Count > 0 ? $"-i {Quote(LocalMediaPath)} {seek}" : $"{seek} -i {Quote(LocalMediaPath)}");
                return WithOpenClDevice($"{ffmpeg} -hide_banner -y {input} -map 0:v:0 -map 0:a:0? -vf \"{string.Join(",", chain)}\" {encode} {Quote(outputPath)}");
            }

            // With the engine every stretch of the sequence is composed on its own, as the export composes a cut
            // segment. The file is opened once per stretch, wound forward to the first frame that stretch shows
            // of it, which is where its frames then count from.
            var shift = MainShift;
            var inputs = new List<string>();
            var graph = new List<string>();
            for (var i = 0; i < ranges.Count; i++)
            {
                var (from, to) = ranges[i];
                var seen = GetSeenMain(from, to);
                var origin = seen.Count > 0 ? Math.Max(seen.Min(p => p.Source), 0) : 0;
                inputs.Add($"{hardware}-ss {Number(origin)} -i {Quote(LocalMediaPath)}");

                List<string> chain;
                _inputOrigin = origin;
                try
                {
                    chain = BuildVideoFilters(i.ToString(CultureInfo.InvariantCulture), from, to);
                }
                finally
                {
                    _inputOrigin = 0;
                }

                if (burnFilters.Count > 0)
                {
                    chain.Add($"setpts=PTS{Signed(from - shift)}/TB");
                    chain.AddRange(burnFilters);
                    chain.Add("setpts=PTS-STARTPTS");
                }

                graph.Add($"[{i}:v:0]{string.Join(",", chain)}[pv{i}]");
                if (!hasSound)
                    continue;

                // The main video's first track for as long as it is there in this stretch, and silence around it.
                if (seen.Count == 0)
                {
                    graph.Add($"anullsrc=r=48000:cl=stereo:d={Number(to - from)}[pa{i}]");
                    continue;
                }

                for (var j = 0; j < seen.Count; j++)
                {
                    var (start, lead) = (seen[j].Source - origin, (int)Math.Round((seen[j].From - from) * 1000));
                    graph.Add($"[{i}:a:0]atrim=start={Number(start)}:end={Number(start + seen[j].To - seen[j].From)},asetpts=PTS-STARTPTS{(lead > 0 ? $",adelay={lead}:all=1" : "")},aformat=channel_layouts=stereo[pa{i}_{j}]");
                }

                var mixed = seen.Count == 1 ? $"[pa{i}_0]" : $"{string.Concat(seen.Select((_, j) => $"[pa{i}_{j}]"))}amix=inputs={seen.Count}:normalize=0:dropout_transition=0,asetpts=N/SR/TB,";
                graph.Add($"{mixed}apad=whole_dur={Number(to - from)}[pa{i}]");
            }

            if (ranges.Count > 1)
            {
                graph.Add($"{string.Concat(ranges.Select((_, i) => hasSound ? $"[pv{i}][pa{i}]" : $"[pv{i}]"))}concat=n={ranges.Count}:v=1:a={(hasSound ? 1 : 0)}[joined]{(hasSound ? "[a]" : "")}");
                graph.Add($"[joined]{string.Join(",", finishing)}[v]");
            }
            else
            {
                graph.Add($"[pv0]{string.Join(",", finishing)}[v]");
            }

            var sound = !hasSound ? "" : ranges.Count > 1 ? " -map \"[a]\"" : " -map \"[pa0]\"";
            return WithOpenClDevice($"{ffmpeg} -hide_banner -y {string.Join(" ", inputs)} -filter_complex \"{string.Join(";", graph)}\" -map \"[v]\"{sound} {encode} {Quote(outputPath)}");
        }

        /// <summary>
        /// Gives a command whose graph blurs on the graphics card (boxblur_opencl) the OpenCL device its frames are
        /// uploaded to. Any other command is returned as it is.
        /// </summary>
        private string WithOpenClDevice(string command)
        {
            const string start = " -hide_banner -y ";
            var at = command.Contains("boxblur_opencl", StringComparison.Ordinal) ? command.IndexOf(start, StringComparison.Ordinal) : -1;
            return at < 0 ? command : command.Insert(at + start.Length, S.OpenClDeviceArguments + " ");
        }

        /// <summary>
        /// The ducking step: its first input is the sound, its second the voices. The compressor is driven hard
        /// (the voices are heard by it far louder than they are), so that while anyone speaks it shuts the sound
        /// almost off; and only a share of the result is that shut-off sound, the rest being the sound untouched.
        /// The share is what makes the dip the number of decibels asked for, however loud the voice happens to be.
        /// </summary>
        private string DuckFilter =>
            $"sidechaincompress=threshold=0.02:ratio=20:attack=15:release=350:level_sc=16:mix={Number(1 - Math.Pow(10, Math.Clamp(DuckAmountDb, -40, -1) / 20))}";

        /// <summary>
        /// What puts the main video's own sound where its picture is on the sequence: cut to the part of the
        /// file that is used, and held back until the main video comes in. Empty while the main video is the
        /// whole sequence.
        /// </summary>
        /// <param name="label">Where the track's sound comes from.</param>
        /// <param name="edits">What was done to the track itself (silenced parts, a slip), applied before it is placed.</param>
        /// <param name="mono">
        /// Whether the track has one channel. One that comes out beginning with silence is made two-channel:
        /// FFmpeg's AAC encoder has been seen to stop dead on exactly that (one channel, digital silence first,
        /// 192k or more), and the same sound on two channels goes through.
        /// </param>
        /// <param name="tag">The label the placed sound gets, when anything had to be done to it.</param>
        /// <returns>The label to carry on from.</returns>
        private string PlaceMainAudio(List<string> graph, string label, List<string> edits, bool mono, string tag)
        {
            var clips = IsMainWholeSequence ? [] : GetMainClips();
            if (clips.Count <= 1)
            {
                var steps = new List<string>(edits);
                if (clips.Count == 1)
                {
                    var clip = clips[0];
                    if (clip.Offset > 0.001 || clip.End - clip.Start < MainMediaSeconds - clip.Offset - 0.02)
                        steps.Add($"atrim={(clip.Offset > 0.001 ? $"start={Number(clip.Offset)}:" : "")}end={Number(clip.Offset + clip.End - clip.Start)},asetpts=PTS-STARTPTS");
                    if (clip.Start > 0.001)
                        steps.Add($"adelay={(int)Math.Round(clip.Start * 1000)}:all=1");
                }

                if (mono && steps.Any(e => e.StartsWith("adelay", StringComparison.Ordinal)))
                    steps.Add("aformat=channel_layouts=stereo");
                if (steps.Count == 0)
                    return label;

                graph.Add($"{label}{string.Join(",", steps)}[{tag}]");
                return $"[{tag}]";
            }

            // Several clips: the track is copied once for each, every copy cut to its clip and held back to where
            // the clip is, and the copies are laid over each other. Sound, unlike pictures, is cheap to read through.
            graph.Add($"{label}{(edits.Count > 0 ? string.Join(",", edits) + "," : "")}asplit={clips.Count}{string.Concat(clips.Select((_, j) => $"[{tag}_c{j}]"))}");
            for (var j = 0; j < clips.Count; j++)
            {
                var clip = clips[j];
                graph.Add($"[{tag}_c{j}]atrim=start={Number(clip.Offset)}:end={Number(clip.Offset + clip.End - clip.Start)},asetpts=PTS-STARTPTS"
                          + (clip.Start > 0.001 ? $",adelay={(int)Math.Round(clip.Start * 1000)}:all=1" : "") + (mono ? ",aformat=channel_layouts=stereo" : "") + $"[{tag}_p{j}]");
            }

            // The mix is given a clock of its own, counted from its samples: what it inherits from clips taken out of
            // order is not one an encoder can follow, and the track came out empty.
            graph.Add($"{string.Concat(clips.Select((_, j) => $"[{tag}_p{j}]"))}amix=inputs={clips.Count}:normalize=0:dropout_transition=0,asetpts=N/SR/TB[{tag}]");
            return $"[{tag}]";
        }

        /// <summary>The voiceover's own steps: cut to its trim handles, moved to where it starts, set to its gain.</summary>
        private string BuildVoiceoverChain()
        {
            var steps = new List<string>();
            if (VoiceoverTrimStart > 0.05 || (VoiceoverDuration > 0 && VoiceoverTrimEnd < VoiceoverDuration - 0.05 && VoiceoverTrimEnd > VoiceoverTrimStart))
                steps.Add($"atrim=start={Number(Math.Max(VoiceoverTrimStart, 0))}:end={Number(VoiceoverTrimEnd)},asetpts=PTS-STARTPTS");
            if (VoiceoverStartSeconds >= 0.01)
                steps.Add($"adelay={(int)Math.Round(VoiceoverStartSeconds * 1000)}:all=1");
            if (Math.Abs(VoiceoverGainDb) >= 0.05)
                steps.Add($"volume={Number(VoiceoverGainDb)}dB");
            return steps.Count > 0 ? string.Join(",", steps) : "anull";
        }

        /// <summary>
        /// Adds the audio side of the command: graph pieces for mixing, ducking, filter-based cuts and the
        /// voiceover, the maps, and (returned) the per-stream encoder options. Tracks the graph does not need
        /// are mapped directly.
        /// </summary>
        /// <param name="voiceoverInput">Which input (-i) the voiceover is, or null when there is none to mix in.</param>
        private List<string> BuildAudio(
            List<ExportAudioTrack> tracks, bool mix, bool duck, bool cutWithFilters, double duration, int? voiceoverInput,
            Dictionary<int, string[]> preparedAudio, List<string> graph, List<string> maps)
        {
            var options = new List<string>();

            // After a filter-based cut each track continues from the concat filter; otherwise from the input.
            // (Or, without cuts, from what was made ready for it: see preparedAudio in BuildFfmpegCommand.)
            string Source(ExportAudioTrack track) =>
                cutWithFilters ? $"[ac{track.Index}]"
                : preparedAudio.TryGetValue(track.Index, out var ready) ? ready[0]
                : $"[0:a:{track.Index}]";

            // A track with a gain or processing of its own (the Audio tab) gets that first, as a step of the
            // graph, and whatever is done with the track afterwards continues from there.
            var prepared = new Dictionary<int, string>();
            string Prepared(ExportAudioTrack track)
            {
                if (!track.HasProcessing || track.GetFilterChain() is not { Count: > 0 } chain)
                    return Source(track);

                if (!prepared.TryGetValue(track.Index, out var label))
                {
                    label = $"[at{track.Index}]";
                    graph.Add($"{Source(track)}{string.Join(",", chain)}{label}");
                    prepared[track.Index] = label;
                }

                return label;
            }

            // Applied to whatever is encoded: loudness first, so the fades are not undone by it.
            var finishing = new List<string>();
            if (NormalizeAudio)
                finishing.Add(NormalizeFilters);
            if (FadeIn)
                finishing.Add($"afade=t=in:st=0:d={Number(FadeSeconds)}");
            if (FadeOut && duration > FadeSeconds)
                finishing.Add($"afade=t=out:st={Number(duration - FadeSeconds)}:d={Number(FadeSeconds)}");
            var finishingChain = string.Join(",", finishing);
            var thenFinishing = finishing.Count > 0 ? "," + finishingChain : "";

            // The voiceover: its own input, cut to its trim handles, moved to where it starts and set to its
            // gain. It was made against the finished timeline, so it is mixed in after the video's own sound
            // has been through the cuts: its times are times in the output, whatever was cut out before them.
            string? voice = null;
            if (voiceoverInput is { } input)
            {
                graph.Add($"[{input}:a:0]{BuildVoiceoverChain()}[voiceover]");
                voice = "[voiceover]";
            }

            // Lays the voiceover over a track. The track decides the length; neither is turned down to make room.
            string WithVoice(string label)
            {
                if (voice is null)
                    return label;

                graph.Add($"{label}{voice}amix=inputs=2:duration=first:normalize=0[a_voiced]");
                return "[a_voiced]";
            }

            // A video without sound of its own: the voiceover is its audio, ending where the picture does.
            if (tracks.Count == 0)
            {
                if (voice is not null)
                {
                    maps.Add($"-map \"{voice}\"");
                    options.Add(BuildAudioEncodeOptions(0, MixedAudioCodec, AudioBitrate));
                    options.Add("-shortest");
                }

                return options;
            }

            if (mix)
            {
                var inputs = tracks.Select(Prepared).ToList();
                if (duck)
                {
                    // The microphone both steers the compressor and is heard, so it is split in two.
                    graph.Add($"{inputs[1]}asplit=2[mic_key][mic_out]");
                    graph.Add($"{inputs[0]}[mic_key]{DuckingFilter}[ducked]");
                    (inputs[0], inputs[1]) = ("[ducked]", "[mic_out]");
                }

                graph.Add($"{string.Concat(inputs)}amix=inputs={inputs.Count}:duration=longest{thenFinishing}[a_mixed]");
                maps.Add($"-map \"{WithVoice("[a_mixed]")}\"");
                options.Add(BuildAudioEncodeOptions(0, MixedAudioCodec, AudioBitrate));
                return options;
            }

            for (var k = 0; k < tracks.Count; k++)
            {
                var track = tracks[k];
                string? label = null;

                // The name from the track list, written as the stream's title. Quotes would end the argument early.
                if (!string.IsNullOrWhiteSpace(track.Title))
                    options.Add($"-metadata:s:a:{k} title=\"{track.Title.Trim().Replace('"', '\'')}\"");

                if (duck && k == 0)
                {
                    graph.Add($"{Prepared(tracks[1])}asplit=2[mic_key][mic_out]");
                    graph.Add($"{Prepared(track)}[mic_key]{DuckingFilter}{thenFinishing}[ducked]");
                    label = "[ducked]";
                }
                else if (duck && k == 1)
                {
                    label = "[mic_out]";
                    if (finishing.Count > 0)
                    {
                        graph.Add($"[mic_out]{finishingChain}[mic_done]");
                        label = "[mic_done]";
                    }
                }
                else if (cutWithFilters || preparedAudio.ContainsKey(track.Index))
                {
                    label = Prepared(track);
                    if (finishing.Count > 0)
                    {
                        graph.Add($"{label}{finishingChain}[an{track.Index}]");
                        label = $"[an{track.Index}]";
                    }
                }

                // The voiceover goes into the first track. That track is then decoded and encoded whatever it
                // was set to: sound cannot be mixed into a stream that is only being copied.
                if (k == 0 && voice is not null)
                {
                    if (label is null)
                    {
                        var own = track.GetFilterChain().Concat(finishing).ToList();
                        label = Source(track);
                        if (own.Count > 0)
                        {
                            graph.Add($"{label}{string.Join(",", own)}[a_first]");
                            label = "[a_first]";
                        }
                    }

                    maps.Add($"-map \"{WithVoice(label)}\"");
                    options.Add(track.IsPassthrough
                        ? BuildAudioEncodeOptions(k, MixedAudioCodec, AudioBitrate)
                        : BuildAudioEncodeOptions(k, track.Codec, track.Bitrate));
                    continue;
                }

                if (label is not null)
                {
                    maps.Add($"-map \"{label}\"");
                    options.Add(BuildAudioEncodeOptions(k, track.Codec, track.Bitrate));
                    continue;
                }

                // Straight from the input. "?" keeps the command valid for a source without such a stream.
                maps.Add($"-map 0:a:{track.Index}?");
                if (track.IsPassthrough)
                {
                    options.Add($"-c:a:{k} copy");
                    continue;
                }

                // Straight from the input, so the track's own processing and the finishing go on as a simple filter.
                options.Add(BuildAudioEncodeOptions(k, track.Codec, track.Bitrate));
                var simple = track.GetFilterChain().Concat(finishing).ToList();
                if (simple.Count > 0)
                    options.Add($"-filter:a:{k} \"{string.Join(",", simple)}\"");
            }

            return options;
        }
        /// <summary>
        /// The sound of a video layer, as a chain that ends where the caller labels it: read from the layer's file
        /// inside the graph, started where the layer's picture starts (or as far from there as it was slipped),
        /// and no longer than the layer is on screen.
        /// </summary>
        private string BuildLayerAudio(Layer layer)
        {
            // The clip's length is in the timeline's seconds; at its speed it plays that many times as much of its file.
            var speed = GetSpeed(layer);
            var chain = new List<string> { $"amovie='{EscapeFilterPath(layer.ImagePath)}'" };
            var trim = layer.MediaOffset > 0.01 ? $"atrim=start={Number(layer.MediaOffset)}" : "";
            if (layer.Duration > 0.001)
                trim = (trim.Length > 0 ? trim + ":" : "atrim=") + $"end={Number(layer.MediaOffset + layer.Duration * speed)}";
            if (trim.Length > 0)
                chain.Add(trim + ",asetpts=PTS-STARTPTS");

            chain.Add("aresample=48000");
            chain.AddRange(BuildTempo(speed));
            // Its own processing and gain, as a track of the video has them.
            chain.AddRange(layer.AudioFilters.BuildChain(Math.Clamp(layer.AudioGainDb, -40, 24)));
            var startsAt = Math.Max(layer.StartTime + (AudioLinked || layer.IsAudio ? 0 : layer.AudioOffset), 0);
            if (startsAt > 0.01)
                chain.Add($"adelay={(int)Math.Round(startsAt * 1000)}:all=1");
            return string.Join(",", chain);
        }

        /// <summary>A clip's speed as the graph uses it: within its limits, and exactly 1 for a clip that has none.</summary>
        private static double GetSpeed(Layer layer) =>
            layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

        /// <summary>Whether a speed is anything other than as recorded.</summary>
        private static bool IsRetimed(double speed) => Math.Abs(speed - 1) >= 0.0005;

        /// <summary>
        /// The filters that play sound at a speed without changing its pitch. One atempo takes the sound from half
        /// speed to double; anything beyond is reached by several in a row, each within that range, whose
        /// speeds multiply out to the one asked for (0.1 is 0.5 x 0.5 x 0.5 x 0.8; 10 is 2 x 2 x 2 x 1.25).
        /// </summary>
        private static List<string> BuildTempo(double speed)
        {
            var steps = new List<string>();
            if (!IsRetimed(speed))
                return steps;

            var left = Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed);
            for (; left > 2.0 + 1e-9; left /= 2.0)
                steps.Add("atempo=2.0");
            for (; left < 0.5 - 1e-9; left /= 0.5)
                steps.Add("atempo=0.5");
            if (Math.Abs(left - 1) >= 0.0005)
                steps.Add($"atempo={left.ToString("0.######", CultureInfo.InvariantCulture)}");
            return steps;
        }

        /// <summary>The filter that plays a picture at a speed: every frame's time divided by it.</summary>
        private static string BuildRetime(double speed) => $"setpts=(1.0/{speed.ToString("0.######", CultureInfo.InvariantCulture)})*PTS";

        /// <param name="outputIndex">Position of the stream among the output's audio streams.</param>
        private static string BuildAudioEncodeOptions(int outputIndex, string codec, string bitrate) =>
            codec.StartsWith("pcm_", StringComparison.Ordinal)
                ? $"-c:a:{outputIndex} {codec}"
                : $"-c:a:{outputIndex} {codec} -b:a:{outputIndex} {bitrate}";

        // ----- Video filters -----

        /// <summary>
        /// The per-frame chain applied to re-encoded video. Order matters: deinterlace whole frames, crop,
        /// clean up what is left, resize, flag the pixel shape, then convert the colours.
        /// </summary>
        /// <param name="labelSuffix">
        /// Makes the labels inside a step unique. With filter-based cuts the chain is written once per segment
        /// into one graph, where two pieces may not share a label.
        /// </param>
        /// <param name="from">
        /// With the Layer Engine: where on the sequence the stretch of picture being composed begins, and
        /// (<paramref name="to"/>) where it ends. Every layer is placed in time by them.
        /// </param>
        private List<string> BuildVideoFilters(string labelSuffix, double from = 0, double to = 0)
        {
            var filters = new List<string>();
            if (Deinterlace)
                filters.Add("yadif");

            // Live Preview: fewer frames from a fast source, and fewer pixels, before anything else is done to them.
            if (_buildingLiveGraph)
            {
                if (SourceFrameRate > 40)
                    filters.Add($"fps={Number(SourceFrameRate / 2)}");
                if (_liveScale < 1)
                    filters.Add($"scale=trunc(iw*{Number(_liveScale)}/2)*2:trunc(ih*{Number(_liveScale)}/2)*2:flags=fast_bilinear");
            }

            // The crop is written as fractions of the input frame, so the same settings cut the same part out
            // of a source of any resolution. Width and height are kept even, as most encoders need.
            string? crop = null;
            if (HasCrop)
            {
                var (left, top, right, bottom) = GetCropFractions();
                crop = $"crop=trunc(iw*{Fraction(1 - left - right)}/2)*2:trunc(ih*{Fraction(1 - top - bottom)}/2)*2:trunc(iw*{Fraction(left)}):trunc(ih*{Fraction(top)})";
            }

            // A main video in several clips has no one time of its own left once it is composed. Its burned-in
            // subtitles are drawn on its own frames first, in the file's time (which a preview's input has been
            // wound forward in). Not for Live Preview, whose frames already come in the sequence's time.
            if (FrameEngine && IsMainSpliced && !_buildingLiveGraph && BuildSubtitleBurnFilters(LocalMediaPath) is { Count: > 0 } early)
            {
                if (_inputOrigin > 0.001)
                    filters.Add($"setpts=PTS{Signed(_inputOrigin)}/TB");
                filters.AddRange(early);
                if (_inputOrigin > 0.001)
                    filters.Add($"setpts=PTS{Signed(-_inputOrigin)}/TB");
            }

            if (FrameEngine)
            {
                filters.Add(BuildFrameEngine(crop, labelSuffix, from, to));
                if (Denoise)
                    filters.Add("hqdn3d");
                filters.Add("setsar=1");
            }
            else
            {
                if (crop is not null)
                    filters.Add(crop);
                if (Denoise)
                    filters.Add("hqdn3d");

                var hasWidth = int.TryParse(OutputWidth, out var width) && width > 0;
                var hasHeight = int.TryParse(OutputHeight, out var height) && height > 0;

                // A vertical size is not the source's shape, so both sides are spelled out; otherwise a side
                // left blank follows the other in the source's shape (-2).
                if (UseVerticalResolution && GetRequestedSize() is { } vertical)
                    filters.Add($"scale={Sized(vertical.Width)}:{Sized(vertical.Height)}");
                else if (hasWidth || hasHeight)
                    filters.Add($"scale={(hasWidth ? Sized(width) : -2)}:{(hasHeight ? Sized(height) : -2)}");

                if (GetSampleAspectRatio() is { } sampleAspectRatio)
                    filters.Add($"setsar={sampleAspectRatio}");
            }

            if (S.TonemapToSdr)
                filters.Add(TonemapFilters);

            if (!string.IsNullOrWhiteSpace(LutPath))
                filters.Add($"lut3d=file='{EscapeFilterPath(LutPath)}'");

            // Colour correction, then sharpening last of all, so that it sharpens the picture as it will be seen.
            filters.AddRange(BuildColorFilters());

            return filters;
        }

        // A preview encode opens the main video's file already wound forward to where it begins; the file's frames
        // then count from there, and this is how far in that is.
        private double _inputOrigin;

        // Frame rates that are not the round numbers they are quoted as.
        private static readonly (double Rate, string Exact)[] BroadcastRates =
            [(23.976, "24000/1001"), (29.97, "30000/1001"), (59.94, "60000/1001"), (119.88, "120000/1001")];

        /// <summary>The rate the sequence's canvas ticks at: the main video's own, so that its frames fall one to a tick.</summary>
        private string CanvasRate
        {
            get
            {
                var rate = SourceFrameRate;
                foreach (var (near, exact) in BroadcastRates)
                {
                    if (Math.Abs(rate - near) < 0.01)
                        return exact;
                }

                return Number(rate);
            }
        }

        /// <summary>A number with its sign always written, to follow PTS in a setpts expression.</summary>
        private static string Signed(double value) => (value < 0 ? "-" : "+") + Number(Math.Abs(value));

        /// <summary>The enable option for a step that is only applied while every one of the given conditions holds; nothing when there are none.</summary>
        private static string Enabled(string? first, string? second = null) =>
            first is null && second is null ? "" : $":enable='{(first is not null && second is not null ? $"{first}*{second}" : first ?? second)}'";

        /// <summary>
        /// Composes one stretch of the sequence. The base is the sequence's own canvas: black, the size of the
        /// project, as long as the stretch, with a clock that belongs to nothing that is laid on it. Every track
        /// is laid on it in stacking order, the main video being one of them: cut to the part of its file this
        /// stretch shows, and set down at the moment it comes in. Before it starts and after it ends the canvas
        /// simply goes on without it. A layer may lie partly or wholly outside the canvas: it is cut off at the edge.
        ///
        /// The main video's frames are split into independent branches, one per thing that shows them (the
        /// blurred background, the main video itself, the pieces cut from it); nothing done to an upper layer can
        /// reach the ones below, because by then they are a finished picture. One shortcut is kept: the blurred
        /// background is not made at all while the main video would hide every pixel of it.
        ///
        /// Live Preview is the exception to the canvas: the player's frames are the clock there, the player having
        /// been given the sequence to play, so the canvas is made from them and what comes from the main video is
        /// switched off outside the main video's stretch instead of being cut to it.
        /// </summary>
        /// <param name="from">Where on the sequence the stretch begins, in seconds: the clock of what is composed starts there.</param>
        /// <param name="to">Where it ends.</param>
        private string BuildFrameEngine(string? centerCrop, string s, double from, double to)
        {
            var live = _buildingLiveGraph;
            var (width, height) = (Sized(FrameWidth), Sized(FrameHeight));
            var full = GetCenterRect();
            var center = (X: Placed(full.X), Y: Placed(full.Y), Width: Sized(full.Width), Height: Sized(full.Height));
            var main = S.MainLayer;

            // How much of this stretch the main video is there for. A file whose length is not known is taken to
            // be all of it, and lends the canvas its clock as it does for Live Preview.
            var seen = GetSeenMain(from, to);
            var known = to - from > 0.001 && MainMediaSeconds > 0;
            var mainSeen = !known || seen.Count > 0;
            var throughout = !known;
            if (known && seen.Count > 0 && seen[0].From - from < 0.001 && to - seen[^1].To < 0.001)
            {
                throughout = true;
                for (var i = 1; i < seen.Count && throughout; i++)
                    throughout = seen[i].From - seen[i - 1].To < 0.001;
            }

            var clockFromInput = live || !known;

            // The stack, bottom first, without the layers that have nothing to show.
            var stack = GetStack().Where(l => l.IsMainVideo ? !l.IsHidden && mainSeen : l.IsUsable && (!l.IsVideo || mainSeen)).ToList();
            var motions = stack.ConvertAll(l => GetMotion(l, width, height, from));

            var covers = center.X <= 0 && center.Y <= 0 && center.X + center.Width >= width && center.Y + center.Height >= height;
            var hidesBackground = stack.Count > 0 && stack[0].IsMainVideo && covers && throughout && IsOpaque(main) && !motions[0].Any;
            var blurred = mainSeen && !hidesBackground && !S.BackgroundHidden;

            // One copy of the main video's frames for each thing that shows them. Layers from files bring their own picture.
            var branches = new List<string>();
            if (clockFromInput)
                branches.Add($"[canvas_in{s}]");
            if (blurred)
                branches.Add($"[bg_in{s}]");
            if (stack.Any(l => l.IsMainVideo))
                branches.Add($"[center_in{s}]");
            for (var n = 0; n < stack.Count; n++)
            {
                if (stack[n].IsVideo)
                    branches.Add($"[ui{n}_in{s}]");
                else if (live && stack[n].IsImage && motions[n].Reshapes)
                    branches.Add($"[ui{n}_tick{s}]");
            }

            var graph = new StringBuilder(1024);

            // The main video, cut to what this stretch shows of it and set down where it comes in. Left as it is
            // when it is the whole sequence and all of it is wanted.
            if (!clockFromInput && seen.Count > 0 && !(IsMainWholeSequence && from < 0.001 && to >= SequenceSeconds - 0.001 && _inputOrigin == 0))
                AppendMainClips(graph, seen, from, s);

            // Nothing of it wanted at all: one frame is read and dropped, and the file is left alone.
            graph.Append(branches.Count switch
            {
                0 => "trim=end_frame=1,nullsink;",
                1 => $"null{branches[0]};",
                _ => $"split={branches.Count}{string.Concat(branches)};",
            });

            // The canvas. For Live Preview four pixels of the player's own frame are blackened and stretched, which
            // keeps its clock: frame for frame, through seeking.
            if (clockFromInput)
                graph.Append($"[canvas_in{s}]crop=2:2:0:0,format=yuv420p,lutyuv=y=16:u=128:v=128,scale={width}:{height}:flags=neighbor");
            else
                graph.Append($"color=c=black:s={width}x{height}:r={CanvasRate}:d={Number(to - from)},format=yuv420p");

            // What comes from the main video is there only while the main video is. Composed on the canvas, its
            // frames run out and the canvas carries on; on the player's clock it is switched off outside its stretch.
            var mainEnding = clockFromInput ? "" : ":eof_action=pass";
            // Between two clips that do not meet there is nothing of it either, whichever clock it is on.
            var mainGate = !throughout && (clockFromInput || seen.Count > 1) ? BuildMainGate(from, to) : null;

            if (blurred)
            {
                graph.Append($"[base{s}];[bg_in{s}]");
                if (live)
                {
                    // Blurred small and scaled back up: the blur hides the difference, and it is a sixteenth of the work.
                    var (smallWidth, smallHeight) = (Math.Max(width / 8 * 2, 16), Math.Max(height / 8 * 2, 16));
                    var smallRadius = Math.Clamp((int)Math.Round(Math.Clamp(BlurRadius, 5, 50) * _liveScale / 4), 1, Math.Max(Math.Min(smallWidth, smallHeight) / 4 - 1, 1));
                    graph.Append($"scale={smallWidth}:{smallHeight}:force_original_aspect_ratio=increase:flags=fast_bilinear,crop={smallWidth}:{smallHeight},"
                                 + $"boxblur={smallRadius}:{Math.Clamp(BlurPasses, 1, 5)},eq=brightness={Number(Math.Clamp(BackgroundDim, -0.5, 0))},"
                                 + $"scale={width}:{height}:flags=bilinear");
                }
                else
                {
                    // The blur radius cannot exceed half the smaller side of the colour planes, which are half size.
                    var radius = Math.Min(Math.Clamp(BlurRadius, 5, 50), Math.Max(Math.Min(width, height) / 4 - 1, 1));
                    // On the graphics card where it can be (OpenCL, which every vendor's driver has): the frame is
                    // uploaded, blurred there and brought back. Otherwise the processor blurs, as it always could.
                    var blur = S.OpenClBlur
                        ? $"format=yuv420p,hwupload,boxblur_opencl={radius}:{Math.Clamp(BlurPasses, 1, 5)},hwdownload,format=yuv420p"
                        : $"boxblur={radius}:{Math.Clamp(BlurPasses, 1, 5)}";
                    graph.Append($"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},"
                                 + $"{blur},eq=brightness={Number(Math.Clamp(BackgroundDim, -0.5, 0))}");
                }

                graph.Append($"[bg{s}];[base{s}][bg{s}]overlay=0:0{mainEnding}{Enabled(mainGate)}");
            }

            // The layers, each on the picture built so far. The running picture is labelled between steps;
            // after the last one it is left open, so the rest of the chain continues from it.
            for (var n = 0; n < stack.Count; n++)
            {
                var (layer, motion) = (stack[n], motions[n]);
                var name = layer.IsMainVideo ? "center" : $"ui{n}";
                var (x, y, layerWidth, layerHeight) = layer.IsMainVideo
                    ? center
                    : layer.GetOutputRect(width, height, SourceWidth, SourceHeight);

                // A layer whose size is animated is built once, at the largest it gets, and scaled from there.
                var (builtWidth, builtHeight) = (layerWidth, layerHeight);
                if (motion.Width is not null)
                {
                    builtWidth = Even(motion.LargestWidth * width);
                    builtHeight = Even((double)layerHeight * builtWidth / layerWidth);
                }

                // The main video. The crop frames this layer only; the background uses the whole picture.
                var mainSource = layer.IsMainVideo ? $"[center_in{s}]{(centerCrop is null ? "" : centerCrop + ",")}scale={builtWidth}:{builtHeight}" : null;
                var tick = live && layer.IsImage && motion.Reshapes ? $"[ui{n}_tick{s}]" : null;
                graph.Append($"[stage{n}{s}];");
                graph.Append(BuildLayer(layer, name, s, builtWidth, builtHeight, from, motion, mainSource, tick));

                // A turned picture is larger than the layer it was; it stays where its middle was.
                var (boxWidth, boxHeight) = GetBox(layer, motion, builtWidth, builtHeight);
                string placeX, placeY;
                if (motion.Any)
                {
                    // The box is that much larger than the layer on each side, and grows and shrinks with it.
                    var size = motion.Width is null ? "" : $"*({motion.Width})/{builtWidth}";
                    var (marginX, marginY) = ((boxWidth - builtWidth) / 2.0, (boxHeight - builtHeight) / 2.0);
                    placeX = (motion.X ?? Number(x)) + (marginX == 0 ? "" : $"-{Number(marginX)}{size}");
                    placeY = (motion.Y ?? Number(y)) + (marginY == 0 ? "" : $"-{Number(marginY)}{size}");
                }
                else
                {
                    (placeX, placeY) = (Number(x - (boxWidth - layerWidth) / 2), Number(y - (boxHeight - layerHeight) / 2));
                }

                string At(int offset) => motion.Any
                    ? $"x='{placeX}{(offset == 0 ? "" : "+" + offset)}':y='{placeY}{(offset == 0 ? "" : "+" + offset)}':eval=frame"
                    : $"{int.Parse(placeX, CultureInfo.InvariantCulture) + offset}:{int.Parse(placeY, CultureInfo.InvariantCulture) + offset}";

                var ending = new StringBuilder();
                if (layer.IsMainVideo || layer.IsVideo)
                    ending.Append(mainEnding);

                // A video from a file runs for as long as it is asked to, and so does a still that was made into
                // a stream to be animated; the picture under it decides when the output ends.
                if (layer.IsVideoFile || (layer.IsImage && motion.Reshapes && !live))
                    ending.Append(":shortest=1");

                // A layer with a time to appear and a time to go is only laid on between the two. The clock the
                // filter sees starts at this stretch of the picture, so the times are counted from there.
                string? timed = null;
                if (!layer.IsMainVideo && layer.HasTiming && (layer.StartTime > 0.001 || layer.Duration > 0.001))
                {
                    var appears = layer.StartTime - from;
                    timed = $"between(t,{Number(appears)},{(layer.Duration > 0.001 ? Number(appears + layer.Duration) : "1e9")})";
                }

                ending.Append(Enabled(layer.IsMainVideo || layer.IsVideo ? mainGate : null, timed));

                if (layer.Shadow)
                {
                    // The shadow is the layer's own outline in black at reduced opacity, laid down first and offset.
                    var offset = Placed(Math.Clamp(layer.ShadowOffset, 0, 200));
                    graph.Append($";[{name}_layer{s}]split[{name}_top{s}][{name}_shadow_in{s}]");
                    graph.Append($";[{name}_shadow_in{s}]format=yuva420p,lutyuv=y=16:u=128:v=128:a=val*{Number(Math.Clamp(layer.ShadowOpacity, 0, 1))},"
                                 + $"boxblur=2:1:0:0:2:1[{name}_shadow{s}]");
                    graph.Append($";[stage{n}{s}][{name}_shadow{s}]overlay={At(offset)}{ending}[{name}_shaded{s}]");
                    graph.Append($";[{name}_shaded{s}][{name}_top{s}]overlay={At(0)}{ending}");
                }
                else
                {
                    graph.Append($";[stage{n}{s}][{name}_layer{s}]overlay={At(0)}{ending}");
                }
            }

            return graph.ToString();
        }

        /// <summary>The parts of the main video's clips that fall inside a stretch of the sequence, in order, each with the moment of the file it begins at.</summary>
        private List<(double From, double To, double Source)> GetSeenMain(double from, double to)
        {
            var seen = new List<(double From, double To, double Source)>();
            foreach (var clip in GetMainClips())
            {
                var (a, b) = (Math.Max(from, clip.Start), Math.Min(to, clip.End));
                if (b - a > 0.001)
                    seen.Add((a, b, clip.Offset + a - clip.Start));
            }

            return seen;
        }

        /// <summary>
        /// Makes the main video's frames a picture in the sequence's time: every clip cut out of the file and set
        /// down at the moment it comes in. One clip is a trim. Several clips that take the file in the order it
        /// runs are one pass over it: the frames of every clip are let through and the rest dropped (select), and
        /// each frame is then moved to where its clip puts it (setpts, by which clip its own time falls in).
        /// Nothing is held in memory for that. Clips that take the file out of order (the end of it placed before
        /// the beginning) cannot be had in one pass: each is cut on a copy of its own and the copies are woven
        /// together by time, which holds frames back for as long as the order is wrong.
        /// </summary>
        private void AppendMainClips(StringBuilder graph, List<(double From, double To, double Source)> seen, double from, string s)
        {
            var origin = _inputOrigin;
            if (seen.Count == 1)
            {
                var (cutFrom, lead) = (seen[0].Source - origin, seen[0].From - from);
                graph.Append("trim=");
                if (cutFrom > 0.001)
                    graph.Append("start=").Append(Number(cutFrom)).Append(':');
                graph.Append("end=").Append(Number(cutFrom + seen[0].To - seen[0].From)).Append(",setpts=PTS-STARTPTS");
                if (lead > 0.001)
                    graph.Append('+').Append(Number(lead)).Append("/TB");
                graph.Append(',');
                return;
            }

            var inOrder = true;
            for (var i = 1; i < seen.Count && inOrder; i++)
                inOrder = seen[i].Source >= seen[i - 1].Source + (seen[i - 1].To - seen[i - 1].From) - 0.001;

            if (inOrder)
            {
                graph.Append("select='");
                for (var i = 0; i < seen.Count; i++)
                {
                    var start = seen[i].Source - origin;
                    graph.Append(i > 0 ? "+" : "").Append("gte(t,").Append(Number(start)).Append(")*lt(t,").Append(Number(start + seen[i].To - seen[i].From)).Append(')');
                }

                // How far each clip's frames are moved: to where the clip is on this stretch, from where they are in the file.
                graph.Append("',setpts='PTS+(");
                var before = 0.0;
                for (var i = 0; i < seen.Count; i++)
                {
                    var shift = seen[i].From - from - (seen[i].Source - origin);
                    if (i == 0)
                        graph.Append(Number(shift));
                    else if (Math.Abs(shift - before) >= 0.0005)
                        graph.Append(Signed(shift - before)).Append("*gte(T,").Append(Number(seen[i].Source - origin)).Append(')');
                    before = shift;
                }

                graph.Append(")/TB',");
                return;
            }

            graph.Append("split=").Append(seen.Count);
            for (var i = 0; i < seen.Count; i++)
                graph.Append($"[mcut{i}{s}]");
            for (var i = 0; i < seen.Count; i++)
            {
                var start = seen[i].Source - origin;
                graph.Append($";[mcut{i}{s}]trim=start={Number(start)}:end={Number(start + seen[i].To - seen[i].From)},setpts=PTS-STARTPTS{Signed(seen[i].From - from)}/TB[mset{i}{s}]");
            }

            graph.Append(';');
            for (var i = 0; i < seen.Count; i++)
                graph.Append($"[mset{i}{s}]");
            graph.Append("interleave=n=").Append(seen.Count).Append(',');
        }

        /// <summary>
        /// The size of the picture a layer is laid on the frame as: the layer itself, the box its corners reach
        /// when it is turned, or, when its turning is animated, the square it can turn all the way round in.
        /// </summary>
        private static (int Width, int Height) GetBox(Layer layer, Motion motion, int width, int height)
        {
            if (motion.Angle is null)
                return GetTurnedSize(layer, width, height);

            var across = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height) / 2) * 2;
            return (across, across);
        }

        /// <summary>Whether a layer hides everything under the rectangle it covers: solid, square-cornered, unturned, with nothing keyed or masked out.</summary>
        private bool IsOpaque(Layer layer) =>
            layer.Opacity >= 100 && !layer.ChromaKey && !(layer.CustomMask && Exists(layer.MaskPath.Trim().Trim('"')))
            && layer.CornerRadius == 0 && !layer.Feather && Math.Abs(layer.Rotation % 360) < 0.05;

        /// <summary>The size of the picture once it has been turned: the box its corners then reach, in even pixels.</summary>
        private static (int Width, int Height) GetTurnedSize(Layer layer, int width, int height)
        {
            if (!layer.HasFilters || Math.Abs(layer.Rotation % 360) < 0.05)
                return (width, height);

            var angle = layer.Rotation * Math.PI / 180;
            var (cos, sin) = (Math.Abs(Math.Cos(angle)), Math.Abs(Math.Sin(angle)));
            return ((int)Math.Ceiling((width * cos + height * sin) / 2) * 2, (int)Math.Ceiling((width * sin + height * cos) / 2) * 2);
        }

        /// <summary>The filters that are a layer's own: mirroring, its LUT, noise, colour, blur and sharpening, in that order.</summary>
        /// <param name="frameWidth">The width of the frame being composed, which a blur given for a 1080-wide frame is scaled to.</param>
        private List<string> BuildLayerFilters(Layer layer, int frameWidth)
        {
            var filters = new List<string>();
            if (!layer.HasFilters)
                return filters;

            if (layer.FlipHorizontal)
                filters.Add("hflip");
            if (layer.FlipVertical)
                filters.Add("vflip");

            var lut = layer.FilterLut.Trim().Trim('"');
            if (lut.Length > 0 && Exists(lut))
                filters.Add($"lut3d=file='{EscapeFilterPath(lut)}'");
            if (layer.FilterDenoise)
                filters.Add("hqdn3d");

            var (contrast, brightness) = (Math.Clamp(layer.FilterContrast, 0, 2), Math.Clamp(layer.FilterBrightness, -1, 1));
            var (saturation, gamma) = (Math.Clamp(layer.FilterSaturation, 0, 3), Math.Clamp(layer.FilterGamma, 0.1, 3));
            if (Math.Abs(contrast - 1) >= 0.005 || Math.Abs(brightness) >= 0.005 || Math.Abs(saturation - 1) >= 0.005 || Math.Abs(gamma - 1) >= 0.005)
                filters.Add($"eq=contrast={Number(contrast)}:brightness={Number(brightness)}:saturation={Number(saturation)}:gamma={Number(gamma)}");
            if (Math.Abs(layer.FilterHue) >= 0.05)
                filters.Add($"hue=h={Number(Math.Clamp(layer.FilterHue, -180, 180))}");
            if (layer.FilterBlur >= 0.05)
                filters.Add($"gblur=sigma={Number(Math.Max(Math.Clamp(layer.FilterBlur, 0, 50) * frameWidth / 1080.0 / 2, 0.2))}");
            if (layer.FilterSharpen >= 0.005)
                filters.Add($"cas=strength={Number(Math.Clamp(layer.FilterSharpen, 0, 1))}");
            return filters;
        }

        /// <summary>
        /// One layer as a finished picture with its transparency, labelled [name_layer]: cut from the main video
        /// (or the main video itself), or read from its image or video file, and scaled. Its own filters are then
        /// applied to it. What shows of it is the product of everything that makes parts of it transparent: its
        /// own transparency (a PNG's, or a color keyed out), a custom mask from a file, and the rounded or
        /// feathered outline. Opacity scales whatever results, and last of all the picture is turned, and, when
        /// its size is animated, scaled frame by frame.
        /// </summary>
        /// <param name="width">The size it is built at: its size on the frame, or the largest it gets when that is animated.</param>
        /// <param name="mainSource">For the main video: the chain that brings its picture, already scaled.</param>
        /// <param name="tick">For a still that changes shape in Live Preview: the branch of the player's frames that gives it a clock.</param>
        private string BuildLayer(Layer layer, string name, string s, int width, int height, double startSeconds, Motion motion, string? mainSource = null, string? tick = null)
        {
            var chain = new StringBuilder(512);
            if (mainSource is not null)
            {
                chain.Append(mainSource);
            }
            else if (layer.IsVideoFile)
            {
                // Read by a source filter inside the graph, so no second -i is needed. It repeats without end,
                // is given an even clock, and is started as far in as the main video is.
                // Its own video begins when the layer appears: by then this stretch of the picture may be some
                // way in (so the start of the layer's video is skipped), or not there yet (so it is held back).
                // At a speed other than 1 the clip covers its file that many times as fast: how far into the file it
                // is, is worked out in the file's own seconds, and its frames are then given the timeline's.
                chain.Append($"movie='{EscapeFilterPath(layer.ImagePath)}':loop=0,setpts=N/FRAME_RATE/TB");
                var speed = GetSpeed(layer);
                var retimed = IsRetimed(speed);
                var into = (startSeconds - layer.StartTime) * speed + layer.MediaOffset;
                if (into > 0.01)
                    chain.Append($",trim=start={Number(into)},setpts=PTS-STARTPTS{(retimed ? "," + BuildRetime(speed) : "")}");
                else if (into < -0.01)
                    chain.Append(retimed ? $",{BuildRetime(speed)}+{Number(-into / speed)}/TB" : $",setpts=PTS+{Number(-into)}/TB");
                else if (retimed)
                    chain.Append(',').Append(BuildRetime(speed));
                chain.Append($",scale={width}:{height}");
            }
            else if (layer.IsImage)
            {
                // A single frame, which the overlay repeats for as long as the video runs.
                chain.Append($"movie='{EscapeFilterPath(layer.ImagePath)}',scale={width}:{height}");
            }
            else
            {
                // Cut out of the source by fractions of the frame, so the same layer works at any source size.
                chain.Append($"[{name}_in{s}]crop=trunc(iw*{Fraction(layer.SourceWidth)}/2)*2:trunc(ih*{Fraction(layer.SourceHeight)}/2)*2:"
                             + $"iw*{Fraction(layer.SourceX)}:ih*{Fraction(layer.SourceY)},scale={width}:{height}");
            }

            var own = BuildLayerFilters(layer, Sized(FrameWidth));
            if (own.Count > 0 && layer.IsImage)
            {
                // The colour filters know nothing of transparency: a picture's own is set aside and put back after them.
                chain.Append($",format=yuva420p,split[{name}_fc{s}][{name}_fa_in{s}];[{name}_fa_in{s}]alphaextract"
                             + $"{string.Concat(own.Where(f => f is "hflip" or "vflip").Select(f => "," + f))}[{name}_fa{s}];"
                             + $"[{name}_fc{s}]{string.Join(",", own)}[{name}_fd{s}];[{name}_fd{s}][{name}_fa{s}]alphamerge");
            }
            else if (own.Count > 0)
            {
                chain.Append(',').Append(string.Join(",", own));
            }

            if (layer.ChromaKey && layer.HasKeying)
            {
                chain.Append($",chromakey=0x{KeyColor(layer.ChromaColor)}:{Number(Math.Clamp(layer.ChromaSimilarity, 0.01, 1))}:{Number(Math.Clamp(layer.ChromaBlend, 0, 1))}");
            }

            chain.Append(",format=yuva420p");

            // The masks: grey pictures the size of the layer, white where it is to show. Each is a single picture
            // made once and held, not worked out again for every frame.
            var masks = new List<string>();
            var sources = new StringBuilder();
            if (layer.CustomMask && layer.HasMask && Exists(layer.MaskPath.Trim().Trim('"')))
            {
                sources.Append($";movie='{EscapeFilterPath(layer.MaskPath)}',scale={width}:{height},format=gray,trim=end_frame=1,loop=loop=-1:size=1[{name}_cmask{s}]");
                masks.Add($"[{name}_cmask{s}]");
            }

            if (BuildLayerMask(layer, width, height, _liveScale) is { } shape)
            {
                sources.Append($";color=c=black:s={width}x{height}:r=1,format=gray,geq=lum='255*{shape}',trim=end_frame=1,loop=loop=-1:size=1[{name}_smask{s}]");
                masks.Add($"[{name}_smask{s}]");
            }

            if (masks.Count > 0)
            {
                // The layer's own transparency is taken out, multiplied by each mask in turn, and put back.
                chain.Append($",split[{name}_pic{s}][{name}_a_in{s}];[{name}_a_in{s}]alphaextract[{name}_a0{s}]");
                chain.Append(sources);
                for (var k = 0; k < masks.Count; k++)
                    chain.Append($";[{name}_a{k}{s}]{masks[k]}blend=all_mode=multiply:shortest=1[{name}_a{k + 1}{s}]");
                chain.Append($";[{name}_pic{s}][{name}_a{masks.Count}{s}]alphamerge");
            }

            var opacity = Math.Clamp(layer.Opacity, 0, 100) / 100.0;
            if (opacity < 1)
                chain.Append($",lutyuv=a=val*{Number(opacity)}");

            // A still picture is one frame, and one frame cannot change shape as time passes: it is made into a
            // frame for every tick first. For an encode it is simply repeated at the canvas's rate. For Live
            // Preview it is laid on see-through frames cut from the player's own, which carry the player's clock
            // through every seek; a stream counted up from nothing would have to be run through to catch up.
            if (layer.IsImage && motion.Reshapes)
            {
                if (tick is null)
                    chain.Append($",loop=loop=-1:size=1,setpts=N/({CanvasRate}*TB)");
                else
                    chain.Append($"[{name}_still{s}];{tick}crop=2:2:0:0,format=yuva420p,lutyuv=a=0,scale={width}:{height}:flags=neighbor[{name}_clock{s}];"
                                 + $"[{name}_clock{s}][{name}_still{s}]overlay=format=auto,format=yuva420p");
            }

            // Turned about its middle, into a box large enough for its corners; what the box adds is transparent.
            var (boxWidth, boxHeight) = GetBox(layer, motion, width, height);
            if (motion.Angle is not null)
                chain.Append($",rotate='({motion.Angle})*PI/180':ow={boxWidth}:oh={boxHeight}:c=black@0");
            else if ((boxWidth, boxHeight) != (width, height) || (layer.HasFilters && Math.Abs(layer.Rotation % 360) >= 0.05))
                chain.Append($",rotate={Number(layer.Rotation)}*PI/180:ow={boxWidth}:oh={boxHeight}:c=black@0");

            // An animated size: the finished picture, box and all, scaled for each frame from the size it was built at.
            if (motion.Width is not null)
            {
                var across = boxWidth == width ? motion.Width : $"({motion.Width})*{Ratio((double)boxWidth / width)}";
                chain.Append($",scale=w='max(2,trunc({across}))':h='max(2,trunc(({across})*{Ratio((double)boxHeight / boxWidth)}))':eval=frame");
                if (_buildingLiveGraph)
                    chain.Append(":flags=fast_bilinear");
            }

            return chain.Append($"[{name}_layer{s}]").ToString();
        }

        private static string Ratio(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>#RRGGBB as FFmpeg writes a color, RRGGBB; anything unreadable becomes green.</summary>
        private static string KeyColor(string color)
        {
            var hex = (color ?? "").Trim().TrimStart('#');
            return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : "00FF00";
        }

        /// <summary>
        /// The geq expression (0 to 1 per pixel) for a layer's shape, or null when it is a plain rectangle.
        /// It measures how far a pixel is inside a rounded rectangle: zero on the edge, rising inwards. Rounded
        /// corners cut the picture off at that edge; a feather lets it fade in over its width instead.
        /// </summary>
        private static string? BuildLayerMask(Layer element, int width, int height, double pixelScale = 1)
        {
            var limit = Math.Max(Math.Min(width, height) / 2 - 1, 1);
            var corner = Math.Min((int)Math.Round(Math.Clamp(element.CornerRadius, 0, 50) / 100.0 * Math.Min(width, height)), limit);
            var feather = element.Feather ? Math.Clamp((int)Math.Round(element.FeatherRadius * pixelScale), 1, limit) : 0;
            if (corner == 0 && feather == 0)
                return null;

            // A feathered edge is at least as round as it is soft; without a feather the edge is one pixel of anti-aliasing.
            var radius = Math.Max(corner, feather);
            var ramp = Math.Max(feather, 1);
            return $"clip(({radius}.5-hypot(max({radius}-X,0)+max(X-(W-1-{radius}),0),max({radius}-Y,0)+max(Y-(H-1-{radius}),0)))/{ramp},0,1)";
        }

        private static string Fraction(double value) => Math.Clamp(value, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture);

        /// <summary>What is applied to the finished picture, once: the fades over everything.</summary>
        private List<string> BuildWholeVideoFilters(double duration)
        {
            var filters = new List<string>();

            if (FadeIn)
                filters.Add($"fade=t=in:st=0:d={Number(FadeSeconds)}");
            if (FadeOut && duration > FadeSeconds)
                filters.Add($"fade=t=out:st={Number(duration - FadeSeconds)}:d={Number(FadeSeconds)}");

            return filters;
        }
        /// <summary>One subtitles filter per track that is to be burned into the picture.</summary>
        private List<string> BuildSubtitleBurnFilters(string sourcePath)
        {
            var escaped = EscapeFilterPath(sourcePath);
            return S.BurnedSubtitleStreams.Select(index => $"subtitles='{escaped}':si={index}").ToList();
        }

        // ----- Motion as FFmpeg expressions -----

        /// <summary>
        /// A property's keyframes as an expression of t, the time on the picture being composed: the first value,
        /// plus for every stretch between two keyframes its change times how far through that stretch t is, held
        /// to 0..1 and bent into the easing of the keyframe the stretch leaves. That is the path from each
        /// keyframe to the next, level before the first and after the last, in one flat sum: one term for each
        /// stretch that changes anything, and none for one that does not.
        /// </summary>
        /// <param name="clipStart">Where the clip begins on that picture's clock, in seconds.</param>
        /// <param name="scale">What a value is multiplied by: the frame's width or height in pixels, or 1.</param>
        private static string KeyExpression(IReadOnlyList<Keyframe> keys, double clipStart, double scale)
        {
            var text = new StringBuilder(16 + keys.Count * 36);
            text.Append(Number(keys[0].Value * scale));
            for (var i = 1; i < keys.Count; i++)
            {
                var change = (keys[i].Value - keys[i - 1].Value) * scale;
                if (Math.Abs(change) < 0.0005)
                    continue;

                var (from, length) = (clipStart + keys[i - 1].Time, keys[i].Time - keys[i - 1].Time);
                text.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)));

                // Two keyframes at the same moment are a jump.
                if (length < 0.001)
                    text.Append("*gte(t,").Append(Number(from)).Append(')');
                else
                    text.Append('*').Append(FormatEasing($"clip((t{(from < 0 ? '+' : '-')}{Number(Math.Abs(from))})/{Number(length)},0,1)", keys[i - 1].Easing));
            }

            return text.ToString();
        }

        /// <summary>How a layer moves while a stretch of the picture is composed; every part is null for a layer that keeps still in that respect.</summary>
        /// <param name="X">Its left edge in pixels, as an expression.</param>
        /// <param name="Width">Its width in pixels, as an expression.</param>
        /// <param name="Angle">Degrees it is turned, as an expression.</param>
        /// <param name="LargestWidth">The widest it gets, as a fraction of the frame: what it is built at, so that it is only ever scaled down.</param>
        private readonly record struct Motion(string? X, string? Y, string? Width, string? Angle, double LargestWidth)
        {
            public bool Any => X is not null || Y is not null || Width is not null || Angle is not null;

            /// <summary>Whether the picture itself changes from frame to frame, not only where it is laid.</summary>
            public bool Reshapes => Width is not null || Angle is not null;
        }

        /// <param name="from">Where on the sequence the stretch being composed begins: its clock starts there.</param>
        private static Motion GetMotion(Layer layer, int frameWidth, int frameHeight, double from)
        {
            if (!layer.IsAnimated)
                return default;

            var start = layer.StartTime - from;
            string? Of(KeyProperty property, double scale) => layer.HasKeys(property) ? KeyExpression(layer.GetKeys(property), start, scale) : null;
            return new Motion(
                Of(KeyProperty.X, frameWidth), Of(KeyProperty.Y, frameHeight), Of(KeyProperty.Scale, frameWidth), layer.HasFilters ? Of(KeyProperty.Rotation, 1) : null,
                layer.HasKeys(KeyProperty.Scale) ? layer.GetKeys(KeyProperty.Scale).Max(k => k.Value) : 0);
        }


        /// <summary>
        /// The step that puts the captions on the picture as its top layer. They are drawn on a transparent
        /// canvas the size of the caption box, which is then laid over the frame where the box sits; that is
        /// what lets the captions be placed, sized and styled like any other layer: translucent, with rounded
        /// or soft edges (the same mask a picture gets) and a drop shadow (the words' own outline, in black).
        /// </summary>
        private string BuildCaptionOverlay(string assPath)
        {
            var (frameWidth, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
            var (x, y, width, height) = CaptionLayer.GetOutputRect(frameWidth, frameHeight, 0, 0);
            (width, height) = (Math.Max(width, 16), Math.Max(height, 16));

            // Live Preview draws everything smaller; the caption file is written for the box, whatever its size on the canvas.
            (x, y, width, height) = (Placed(x), Placed(y), Sized(width), Sized(height));
            var rate = GetTargetFramerate() ?? Number(SourceFrameRate);
            var opacity = Math.Clamp(CaptionLayer.Opacity, 0, 100) / 100.0;

            var layer = $"color=c=black@0:s={width}x{height}:r={rate},format=rgba,{CaptionFilter(assPath)}:alpha=1";
            if (BuildLayerMask(CaptionLayer, width, height, _liveScale) is { } mask)
                layer += $",format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{mask}{(opacity < 1 ? "*" + Number(opacity) : "")}'";
            else if (opacity < 1)
                layer += $",colorchannelmixer=aa={Number(opacity)}";

            // A mask of its own, as any layer can have: the words show where the mask is white.
            var maskPath = CaptionLayer.MaskPath.Trim().Trim('"');
            if (CaptionLayer.CustomMask && Exists(maskPath))
            {
                layer += $",format=yuva420p,split[cap_pic][cap_a_in];[cap_a_in]alphaextract[cap_a0];"
                         + $"movie='{EscapeFilterPath(maskPath)}',scale={width}:{height},format=gray,trim=end_frame=1,loop=loop=-1:size=1[cap_mask];"
                         + "[cap_a0][cap_mask]blend=all_mode=multiply:shortest=1[cap_a1];[cap_pic][cap_a1]alphamerge";
            }

            if (!CaptionLayer.Shadow)
                return $"null[cap_base];{layer}[cap_layer];[cap_base][cap_layer]overlay={x}:{y}:shortest=1";

            var offset = Placed(Math.Clamp(CaptionLayer.ShadowOffset, 0, 200));
            return $"null[cap_base];{layer},split[cap_layer][cap_shadow_in];"
                   + $"[cap_shadow_in]format=rgba,colorchannelmixer=rr=0:gg=0:bb=0:aa={Number(Math.Clamp(CaptionLayer.ShadowOpacity, 0, 1))},boxblur=2:1[cap_shadow];"
                   + $"[cap_base][cap_shadow]overlay={x + offset}:{y + offset}:shortest=1[cap_shaded];"
                   + $"[cap_shaded][cap_layer]overlay={x}:{y}:shortest=1";
        }
    }
}
