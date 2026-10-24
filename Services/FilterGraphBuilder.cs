using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;

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

    /// <summary>How far the track is slipped against the picture, in seconds (later when above 0). Live Preview lays the slip out itself.</summary>
    public double Offset { get; init; }

    /// <summary>The stretches silenced on the timeline, in the track's own seconds.</summary>
    public IReadOnlyList<(double Start, double End)> MutedSpans { get; init; } = [];

    /// <summary>Whether the track is played just as it is in the file: nothing silenced, no slip, no gain or processing.</summary>
    public bool IsPlain => MutedSpans.Count == 0 && Math.Abs(Offset) < 0.001 && !HasProcessing;
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

    /// <summary>Chapters begin at the markers: switched on, and there are markers.</summary>
    public bool ChaptersAtMarkers { get; init; }
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
    /// The proxies ready for the video layers' files (see ProxyCache), by the file they stand for. Only live graphs
    /// read them; an export always reads the original. A proxy starts at its first frame (time 0), whatever the original's start.
    /// </summary>
    public IReadOnlyDictionary<string, string> PreviewPaths { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The height of the main video's proxy when the player plays that (Settings: proxies for the main video too); 0 when it plays the original.</summary>
    public int MainPreviewHeight { get; init; }

    /// <summary>The Sequence Output's effect stack (the Effects pane's Sequence Output target), applied to the finished picture after its colour. Copies.</summary>
    public IReadOnlyList<EffectInstance> SequenceEffects { get; init; } = [];
}

/// <summary>How the video layers from files are shown by a live graph.</summary>
public enum LiveLayerMode
{
    /// <summary>Decoded and played as the export plays them.</summary>
    Live,

    /// <summary>One frame each (the I-frame at or before the moment), held: for scrubbing, where only the picture under the playhead counts.</summary>
    Still,

    /// <summary>Left out: for scrubbing on a slow machine.</summary>
    Hidden,
}

/// <summary>What a live graph is built for: the player's size, and where it plays from.</summary>
public sealed record LiveGraphRequest
{
    /// <summary>The player on screen, in screen pixels: the picture is not built larger than it is shown.</summary>
    public double SurfaceWidth { get; init; }

    public double SurfaceHeight { get; init; }

    /// <summary>
    /// The moment playback starts from, in sequence seconds, for the layers' sound to be laid out from; null
    /// leaves the sound out of the graph, and the player plays the main video's own as it is.
    /// </summary>
    public double? AudioFrom { get; init; }

    /// <summary>Which of the main video's audio tracks the player plays, counted from 0; negative when it has none.</summary>
    public int MainAudioTrack { get; init; }

    /// <summary>
    /// The playhead, in sequence seconds. The graph holds only what is on screen around it (see
    /// <see cref="FilterGraphBuilder.LiveLookAhead"/>), and its video layers start reading their files there.
    /// Null composes the whole sequence, each layer read from its first frame.
    /// </summary>
    public double? VideoFrom { get; init; }

    public LiveLayerMode Layers { get; init; } = LiveLayerMode.Live;

    /// <summary>Threads each video layer's decoder may use inside the player; 0 leaves it to FFmpeg.</summary>
    public int DecodeThreads { get; init; }

    /// <summary>The player plays the composed timeline proxy: its picture is finished, and only the sound is mixed.</summary>
    public bool PictureIsComposed { get; init; }

    /// <summary>Scrubbing with Settings' Draft Mode on: the effects that cost Live Preview most (red) are left out until the playhead is let go.</summary>
    public bool DraftEffects { get; init; }
}

/// <param name="Text">The graph, for mpv's lavfi-complex; empty for none.</param>
/// <param name="RebuildAt">
/// The moment of the sequence, while it plays forwards, at which the graph is to be built again: something
/// that was left out of it is about to come on. Infinity when nothing is.
/// </param>
public readonly record struct LiveGraph(string Text, double RebuildAt);

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
    /// The graph for mpv's lavfi-complex: from its video track to the screen and, while the layers have sound
    /// to be heard, from its audio track to the speakers. Empty when the export would not filter the picture at
    /// all and there is no such sound.
    ///
    /// Given the playhead (<see cref="LiveGraphRequest.VideoFrom"/>), the graph holds only what is on screen
    /// from a moment before it to <see cref="LiveLookAhead"/> after it: a clip that has ended, or that starts
    /// later than that, is not opened at all. Every video layer is read from the playhead (seek_point), so a
    /// jump costs one I-frame interval of decoding, not everything from the start of the file. Such a graph
    /// belongs to its moment: it is built again for every jump, and while playing at
    /// <see cref="LiveGraph.RebuildAt"/>, just before something it left out comes on.
    /// </summary>
    public static LiveGraph BuildLiveGraph(SequenceExportState state, LiveGraphRequest request)
    {
        var pass = new Pass(state);
        if (request.VideoFrom is { } at)
            pass.SetLiveWindow(Math.Max(at, 0), request.Layers, request.DecodeThreads, request.DraftEffects);

        var picture = request.PictureIsComposed ? "" : pass.BuildLiveFilterGraph(request.SurfaceWidth, request.SurfaceHeight);
        var sound = request.AudioFrom is { } from ? pass.BuildLiveAudioGraph(Math.Max(from, 0), request.MainAudioTrack) : "";

        // A graph with no [vo] has mpv show no picture at all: sound alone still passes the video through.
        if (picture.Length == 0 && sound.Length > 0 && !state.SourceHasNoVideo)
            picture = "[vid1]null[vo]";
        var text = picture.Length > 0 && sound.Length > 0 ? $"{picture};{sound}" : picture + sound;
        return new LiveGraph(text, text.Length > 0 ? pass.LiveRebuildAt() : double.PositiveInfinity);
    }

    /// <summary>How far past the playhead a live graph holds what is on screen, in seconds.</summary>
    public const double LiveLookAhead = 30;

    /// <summary>How long before something left out of a live graph comes on the graph is built again, so its file is open and waiting by then.</summary>
    public const double LivePreRoll = 1.5;

    /// <summary>How far before the playhead a live graph still holds what is on screen: a step back of a frame or two needs no new graph.</summary>
    public const double LiveLookBehind = 0.5;

    /// <summary>The output pad of the sound half of a live graph: a graph that has it mixes the layers' sound in.</summary>
    public const string LiveAudioOutput = "[ao]";

    /// <summary>Render Preview: a short, small encode of the given stretches of the timeline.</summary>
    /// <param name="ranges">The stretches to show, joined end to end.</param>
    /// <param name="captionsPath">An auto-caption file made for these stretches, or null.</param>
    /// <param name="videoEncode">The encoder and its options, in place of the fast x264 a preview is made with; given, the output is silent (the timeline proxy).</param>
    public static string BuildPreviewCommand(
        SequenceExportState state, string outputPath, IReadOnlyList<(double Start, double End)> ranges, int percent, string? captionsPath, string? videoEncode = null) =>
        new Pass(state).BuildPreviewCommand(outputPath, ranges, percent, captionsPath, videoEncode);

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

    // Opacity is kept in whole percent: keyframes less than one apart hold it still.
    private const double SameOpacity = 0.5;

    /// <summary>
    /// Takes the keyframes off every property of a layer that they do not change, leaving the property at
    /// the one value they hold. For a copy of a layer that is being prepared for a
    /// <see cref="SequenceExportState"/>: what is then compiled for it is only what moves.
    /// </summary>
    /// <returns>How many properties were folded into plain values.</returns>
    public static int FoldConstantKeys(Layer layer)
    {
        var folded = 0;
        var sizes = layer.GetKeys(KeyProperty.Scale);
        var resized = sizes.Count > 0 && sizes.Max(k => k.Value) - sizes.Min(k => k.Value) >= SamePlace;
        foreach (var property in Enum.GetValues<KeyProperty>())
        {
            var keys = layer.GetKeys(property);
            if (keys.Count == 0)
                continue;

            // The place of a layer whose size changes keeps its keyframes, even when they all hold one value: keyed, they
            // are the corner's; with none, the place would be the anchor point's, and the layer would grow about it.
            if (resized && property is KeyProperty.X or KeyProperty.Y)
                continue;

            var (low, high) = (keys.Min(k => k.Value), keys.Max(k => k.Value));
            if (high - low >= property switch { KeyProperty.Rotation => SameAngle, KeyProperty.Opacity => SameOpacity, _ => SamePlace })
                continue;

            var value = keys[0].Value;
            layer.SetKeys(property, null);
            layer.SetValue(property, value);
            folded++;
        }

        return folded;
    }

    /// <summary>
    /// A stretch's progress (an expression that runs from 0 to 1) bent into a closed-form easing curve (see
    /// <see cref="EasingCurves.Expression"/>): FFmpeg evaluates it per frame like any other arithmetic, and Linear is
    /// the progress itself. Hold, Bounce and Custom Bézier are written as steps and straight pieces by KeyExpression.
    /// </summary>
    public static string FormatEasing(string progressExpr, EasingType type) => EasingCurves.Expression(progressExpr, type);

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

    // ----- The live graph's window in time -----
    // A live graph given the playhead holds only the stretch of the sequence around it: from a moment before
    // it to LiveLookAhead after it. What lies wholly outside is not in the graph at all, so its file is never
    // opened or decoded; the player is given a new graph before any of it comes on (LiveRebuildAt).

    private (double From, double To)? _window;
    private double _playhead;
    private LiveLayerMode _layerMode = LiveLayerMode.Live;
    private int _decodeThreads;

    // Draft Mode while scrubbing: red-cost effects are left out (see LiveGraphRequest.DraftEffects).
    private bool _skipHeavyEffects;

    public void SetLiveWindow(double playhead, LiveLayerMode layers, int decodeThreads, bool draftEffects = false)
    {
        (_playhead, _layerMode, _decodeThreads, _skipHeavyEffects) = (playhead, layers, Math.Max(decodeThreads, 0), draftEffects);
        _window = (playhead - LiveLookBehind, playhead + LiveLookAhead);
    }

    /// <summary>Whether a stretch of the sequence is inside the window: always, for a graph that has none.</summary>
    private bool InWindow(double start, double end) => _window is not { } window || (end > window.From && start < window.To);

    /// <summary>Whether a layer's picture is inside the window.</summary>
    private bool InWindow(Layer layer)
    {
        if (layer.IsMainVideo || !layer.HasTiming)
            return true;
        var (start, end) = layer.GetSpan(SequenceSeconds);
        return InWindow(start, end);
    }

    /// <summary>
    /// When the graph built with a window must be built again while it plays: a little before the first thing it
    /// left out comes on (a layer's picture or sound, or the main video coming back). Infinity when nothing does.
    /// </summary>
    public double LiveRebuildAt()
    {
        if (_window is not { } window)
            return double.PositiveInfinity;

        var next = double.PositiveInfinity;
        void Consider(double start)
        {
            if (start >= window.To && start < next)
                next = start;
        }

        foreach (var clip in GetMainClips())
            Consider(clip.Start);
        foreach (var layer in GetStack().Where(l => !l.IsMainVideo && l.HasTiming))
            Consider(layer.StartTime);
        foreach (var layer in GetLayerAudioSources())
            Consider(LayerAudioStart(layer));
        return double.IsPositiveInfinity(next) ? next : Math.Max(next - LivePreRoll, window.From + LiveLookBehind);
    }

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

            // With cuts made by filters the file is opened wound forward to what is kept, once for each run of
            // kept stretches that lie close together in it (see PlanCutInputs); otherwise it is opened once.
            var cutInputs = cutWithFilters ? PlanCutInputs(segments, tracks) : null;
            var hardware = HardwareDecoding && !copyVideo ? S.HardwareDecodeArguments : "";

            if (cutInputs is not null)
            {
                foreach (var (origin, length) in cutInputs.Inputs)
                {
                    var seek = origin > 0.001 ? $"-ss {Number(origin)} " : "";
                    args.Add($"{hardware}{seek}-t {Number(length)} -i {Quote(input)}");
                }
            }
            else
            {
                if (hardware.Length > 0)
                    args.Add(hardware.TrimEnd());

                if (cutWithDemuxer)
                {
                    _usesCutsFile = true;
                    args.Add($"-f concat -safe 0 -i {Quote(CutsFilePath)}");
                }
                else
                {
                    args.Add($"-i {Quote(input)}");
                }
            }

            // One chapter per kept segment and per marker (as asked), read from a metadata file given as the input after the video's.
            var videoInputs = cutInputs?.Inputs.Count ?? 1;
            var chaptersFromCuts = ((ChaptersAtCuts && segments.Count > 0) || S.ChaptersAtMarkers) && !animated;
            if (chaptersFromCuts)
            {
                _usesChaptersFile = true;
                args.Add($"-i {Quote(ChaptersFilePath)}");
            }

            int? voiceoverInput = null;
            if (voiceover is not null)
            {
                // After the video's inputs, and the chapters file when there is one.
                voiceoverInput = videoInputs + (chaptersFromCuts ? 1 : 0);
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
            // in front of those. Nor with cuts read from openings of the file wound forward to them: each kept
            // stretch then has its sound made ready on its own (AppendSegmentAudio).
            var preparedAudio = new Dictionary<int, string[]>();
            if (!cutWithDemuxer && cutInputs is null)
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
                    voices.AddRange(layerAudio.Where(l => l.IsVoice).Select(l => BuildLayerAudio(l)));

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

                    // The opening of the file this stretch is read from, and how far into the file it begins: its
                    // frames count from there.
                    var (opening, origin) = cutInputs is null ? (0, 0.0) : (cutInputs.InputOf[i], cutInputs.Inputs[cutInputs.InputOf[i]].Origin);
                    var chain = new List<string>();
                    if (FrameEngine)
                    {
                        // The engine composes this stretch of the sequence on a clock that starts at 0. Burned-in
                        // subtitles are placed by the main video's own time, which the clock is set to while they are drawn.
                        _inputOrigin = origin;
                        try
                        {
                            chain.AddRange(BuildVideoFilters(suffix, segments[i].Start.TotalSeconds, segments[i].End.TotalSeconds));
                        }
                        finally
                        {
                            _inputOrigin = 0;
                        }

                        if (burnFilters.Count > 0)
                        {
                            chain.Add($"setpts=PTS{Signed(segments[i].Start.TotalSeconds - MainShift)}/TB");
                            chain.AddRange(burnFilters);
                            chain.Add("setpts=PTS-STARTPTS");
                        }
                    }
                    else
                    {
                        // Burned-in subtitles are placed by timestamp, so they must be drawn before the timestamps are
                        // reset. The cut is placed by the file's own time, which a wound-forward opening is given back first.
                        var frameFilters = BuildVideoFilters(suffix);
                        if (origin > 0.001)
                            chain.Add($"setpts=PTS{Signed(origin)}/TB");
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

                    graph.Add($"[{opening}:v:0]{string.Join(",", chain)}[v{i}]");
                    concatInputs.Append($"[v{i}]");

                    if (cutInputs is not null)
                    {
                        foreach (var label in AppendSegmentAudio(graph, tracks, layerAudio, opening, origin, segments[i].Start.TotalSeconds, segments[i].End.TotalSeconds, suffix))
                            concatInputs.Append(label);
                        continue;
                    }

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
            args.Add(chaptersFromCuts ? $"-map_chapters {videoInputs}"
                : ChapterMarkers && segments.Count == 0 && !animated ? "-map_chapters 0"
                : "-map_chapters -1");

            if (WebOptimized && extension is "mp4" or "mov" or "m4v")
                args.Add("-movflags +faststart");

            args.Add(Quote(output));
            return WithOpenClDevice(string.Join(" ", args));
        }

        // ----- Cuts read from openings of the file wound forward to them -----

        /// <summary>Kept stretches further apart than this in the file (seconds) get an opening each; closer ones share one, read through.</summary>
        private const double CutJoinGap = 10;

        /// <summary>
        /// The most openings of the file one export makes. Each holds some 50 MB while it waits its turn (measured on
        /// a 1440p60 HEVC recording: 20 openings, 1.3 GB in all; 40, 2.2 GB), where reading through a gap instead
        /// costs a minute of decoding for every ten of the recording.
        /// </summary>
        private const int MaxCutInputs = 24;

        /// <summary>
        /// How an export with cuts opens the file. <see cref="Inputs"/> are the openings in input order, each wound
        /// forward to <c>Origin</c> seconds into the file and read for <c>Length</c> seconds; <see cref="InputOf"/>
        /// says which opening each kept stretch is read from.
        /// </summary>
        private sealed record CutPlan(List<(double Origin, double Length)> Inputs, int[] InputOf);

        /// <summary>
        /// Opens the file for an export with cuts once for each run of kept stretches that lie close together in
        /// it, wound forward (-ss before -i) to the first moment the run needs and read no further than its last
        /// (-t). FFmpeg then finds that moment through the file's index instead of decoding everything before it:
        /// with the whole file as one input, a stretch near the end of a recording hours long began to encode only
        /// after everything before it had been decoded, which was half an hour.
        ///
        /// A run is stretches that follow each other forwards in the file, fewer than <see cref="CutJoinGap"/>
        /// seconds apart: one opening serves them all and the gaps between are read through. A stretch whose
        /// picture comes before the one ahead of it gets an opening of its own, or the frames between would be held
        /// in memory until it was its turn. Past <see cref="MaxCutInputs"/> openings, the neighbours with the least
        /// between them are joined. Null when the file's length is not known: it is then read from the start.
        /// </summary>
        private CutPlan? PlanCutInputs(IReadOnlyList<ExportSegment> segments, IReadOnlyList<ExportAudioTrack> tracks)
        {
            if (!HasSource || MainMediaSeconds <= 0)
                return null;

            var heard = tracks.Where(t => !t.IsSilentBase).ToList();
            var inputOf = new int[segments.Count];
            var runs = new List<(double Origin, double End, double PictureStart, double PictureEnd)>();
            for (var i = 0; i < segments.Count; i++)
            {
                var (from, to) = (segments[i].Start.TotalSeconds, segments[i].End.TotalSeconds);
                var seen = GetSeenMain(from, to);

                // What the stretch needs of the file: the picture (in the Layer Engine wherever the main video's clips
                // take it from; otherwise the stretch is the file's own time) and every track's sound, slip and all.
                var picture = FrameEngine ? seen.ConvertAll(p => (Start: p.Source, End: p.Source + p.To - p.From)) : [(Start: from, End: to)];
                var needs = picture.Concat(heard.SelectMany(t => TrackParts(t, seen, from)).Select(p => (Start: p.Start, End: p.Start + p.Span))).ToList();
                if (needs.Count == 0)
                {
                    // Nothing of the file: it is given an opening below, of which it reads one frame.
                    inputOf[i] = -1;
                    continue;
                }

                var (start, end) = (Math.Max(needs.Min(n => n.Start), 0), needs.Max(n => n.End));
                var (pictureStart, pictureEnd) = picture.Count > 0 ? (picture.Min(p => p.Start), picture.Max(p => p.End)) : (start, start);
                if (runs.Count > 0 && runs[^1] is var run && start >= run.Origin && pictureStart >= run.PictureEnd - 0.05 && start <= run.End + CutJoinGap)
                    runs[^1] = (run.Origin, Math.Max(run.End, end), run.PictureStart, Math.Max(run.PictureEnd, pictureEnd));
                else
                    runs.Add((start, end, pictureStart, pictureEnd));
                inputOf[i] = runs.Count - 1;
            }

            if (runs.Count == 0)
                runs.Add((0, 1, 0, 0));

            // Too many openings: the two neighbours with the least between them become one, as long as the second
            // goes on forwards from the first.
            while (runs.Count > MaxCutInputs)
            {
                var (best, gap) = (-1, double.MaxValue);
                for (var j = 0; j + 1 < runs.Count; j++)
                {
                    var (a, b) = (runs[j], runs[j + 1]);
                    if (b.Origin >= a.Origin && b.PictureStart >= a.PictureEnd - 0.05 && b.Origin - a.End < gap)
                        (best, gap) = (j, b.Origin - a.End);
                }

                if (best < 0)
                    break;

                var (first, second) = (runs[best], runs[best + 1]);
                runs[best] = (first.Origin, Math.Max(first.End, second.End), first.PictureStart, Math.Max(first.PictureEnd, second.PictureEnd));
                runs.RemoveAt(best + 1);
                for (var i = 0; i < inputOf.Length; i++)
                {
                    if (inputOf[i] > best)
                        inputOf[i]--;
                }
            }

            // A stretch that needs nothing of the file shares the opening of the one before it, or else the one after.
            for (var i = 0; i < inputOf.Length; i++)
            {
                if (inputOf[i] >= 0)
                    continue;

                var near = Enumerable.Range(0, i).Reverse().Concat(Enumerable.Range(i + 1, inputOf.Length - i - 1)).FirstOrDefault(j => inputOf[j] >= 0, -1);
                inputOf[i] = near >= 0 ? inputOf[near] : 0;
            }

            // A second over: the cuts themselves are made exactly by the filters.
            return new CutPlan(runs.ConvertAll(r => (r.Origin, r.End - r.Origin + 1)), inputOf);
        }

        /// <summary>
        /// Where a track's sound for a stretch of the sequence comes from in the file, part by part: the moment of
        /// the file it starts at, how long it lasts, and how far into the stretch it is laid. The main video's clips
        /// decide what of the file is heard where, as they do for its picture; a track slipped later plays what the
        /// file had that much earlier, and before the file begins there is silence.
        /// </summary>
        private static List<(double Start, double Span, double Lead)> TrackParts(ExportAudioTrack track, List<(double From, double To, double Source)> seen, double from)
        {
            var parts = new List<(double Start, double Span, double Lead)>();
            foreach (var (a, b, source) in seen)
            {
                var (start, span, lead) = (source - track.Offset, b - a, a - from);
                if (start < 0)
                    (lead, span, start) = (lead - start, span + start, 0);
                if (span > 0.001)
                    parts.Add((start, span, lead));
            }

            return parts;
        }

        /// <summary>
        /// The sound of one kept stretch, for the concat filter: every track exactly as long as the stretch and
        /// counted from 0, read from the opening of the file the stretch has. Each track as the export has it without
        /// cuts: its silenced parts, its slip, laid where the main video's clips put it, ducked under the voices when
        /// it is set to duck, and the sound of the layers mixed into the first track. (A ducking compressor starts
        /// afresh at each stretch, which differs from one run over the whole by a fraction of a second at most.)
        /// </summary>
        /// <param name="input">The opening the stretch is read from, which begins <paramref name="origin"/> seconds into the file.</param>
        /// <param name="from">Where the stretch begins on the sequence, and (<paramref name="to"/>) where it ends.</param>
        /// <returns>Each track's label, in the order of <paramref name="tracks"/>.</returns>
        private List<string> AppendSegmentAudio(
            List<string> graph, List<ExportAudioTrack> tracks, List<Layer> layerAudio, int input, double origin, double from, double to, string s)
        {
            var length = to - from;
            var seen = GetSeenMain(from, to);

            // Only the layers that sound somewhere in this stretch.
            var layers = layerAudio.Where(l => LayerAudioStart(l) < to - 0.01 && (l.Duration <= 0.001 || LayerAudioStart(l) + l.Duration > from + 0.01)).ToList();

            // Auto-duck, as without cuts: the voices are mixed into one signal, and each ducked sound gets a copy of it.
            bool IsDucked(ExportAudioTrack track) => track is { AutoDuck: true, IsVoice: false } && !track.IsSilentBase;
            var ducked = tracks.Count(IsDucked) + layers.Count(l => l is { AutoDuck: true, IsVoice: false });
            var keys = new Queue<string>();
            if (ducked > 0)
            {
                var voices = tracks.Where(t => t.IsVoice && !t.IsSilentBase)
                    .Select(t => PlaceSegmentTrack(graph, t, seen, input, origin, from, length, $"dvsrc{t.Index}_{s}") + "anull")
                    .Concat(layers.Where(l => l.IsVoice).Select(l => BuildLayerAudio(l, from)))
                    .ToList();
                if (voices.Count > 0)
                {
                    for (var n = 0; n < voices.Count; n++)
                        graph.Add($"{voices[n]},aresample=48000,apad[dvoice{n}_{s}]");

                    var key = $"[dvoice0_{s}]";
                    if (voices.Count > 1)
                    {
                        key = $"[dkey_{s}]";
                        graph.Add($"{string.Concat(voices.Select((_, n) => $"[dvoice{n}_{s}]"))}amix=inputs={voices.Count}:normalize=0{key}");
                    }

                    if (ducked == 1)
                    {
                        keys.Enqueue(key);
                    }
                    else
                    {
                        var copies = Enumerable.Range(0, ducked).Select(n => $"[dkey{n}_{s}]").ToList();
                        graph.Add($"{key}asplit={ducked}{string.Concat(copies)}");
                        copies.ForEach(keys.Enqueue);
                    }
                }
            }

            // The compressor ends its output early, most of a second before the end of what it was given (seen with
            // FFmpeg 8): the sound is given endless silence after it, and what comes out is cut to the stretch.
            string Duck(string source, string name)
            {
                graph.Add($"{source}apad[{name}_in]");
                graph.Add($"[{name}_in]{keys.Dequeue()}{DuckFilter},atrim=end={Number(length)}[{name}]");
                return $"[{name}]";
            }

            var labels = new List<string>(tracks.Count);
            for (var k = 0; k < tracks.Count; k++)
            {
                var track = tracks[k];
                var label = PlaceSegmentTrack(graph, track, seen, input, origin, from, length, $"aplace{track.Index}_{s}");
                if (keys.Count > 0 && IsDucked(track))
                    label = Duck(label, $"aduck{track.Index}_{s}");

                // The layers' sound goes into the first track, which decides how long the mix is.
                if (k == 0 && layers.Count > 0)
                {
                    for (var n = 0; n < layers.Count; n++)
                    {
                        if (keys.Count > 0 && layers[n] is { AutoDuck: true, IsVoice: false })
                        {
                            Duck(BuildLayerAudio(layers[n], from) + ",", $"alayer{n}_{s}");
                        }
                        else
                        {
                            graph.Add($"{BuildLayerAudio(layers[n], from)}[alayer{n}_{s}]");
                        }
                    }

                    graph.Add($"{label}{string.Concat(layers.Select((_, n) => $"[alayer{n}_{s}]"))}amix=inputs={layers.Count + 1}:duration=first:normalize=0[amixed{track.Index}_{s}]");
                    label = $"[amixed{track.Index}_{s}]";
                }

                labels.Add(label);
            }

            return labels;
        }

        /// <summary>
        /// One track's sound for a kept stretch, from an opening of the file that begins <paramref name="origin"/>
        /// seconds into it: silenced where it was silenced, slipped, and laid where the main video's clips are in the
        /// stretch, with silence round them. Exactly <paramref name="length"/> seconds long.
        /// </summary>
        private string PlaceSegmentTrack(
            List<string> graph, ExportAudioTrack track, List<(double From, double To, double Source)> seen, int input, double origin, double from, double length, string tag)
        {
            var whole = Number(length);
            var parts = track.IsSilentBase ? [] : TrackParts(track, seen, from);
            if (parts.Count == 0)
            {
                graph.Add($"anullsrc=r=48000:cl=stereo:d={whole}[{tag}]");
                return $"[{tag}]";
            }

            // One channel coming out with silence first is made two-channel (see PlaceMainAudio), and so are parts
            // that are mixed.
            var stereo = track.IsMono ? ",aformat=channel_layouts=stereo" : "";
            var placed = new List<string>(parts.Count);
            for (var j = 0; j < parts.Count; j++)
            {
                var (start, span, lead) = parts[j];
                var at = start - origin;

                // The silenced stretches are in the track's own time, the file's; the opening's clock is behind it by its origin.
                var muted = track.MutedSpans.Where(m => m.End > start && m.Start < start + span)
                    .Select(m => $"volume=0:enable='between(t,{Number(m.Start - origin)},{Number(m.End - origin)})',");
                var piece = $"[{input}:a:{track.Index}]{string.Concat(muted)}atrim={(at > 0.0005 ? $"start={Number(at)}:" : "")}end={Number(at + span)},asetpts=PTS-STARTPTS"
                            + (lead > 0.001 ? $",adelay={(int)Math.Round(lead * 1000)}:all=1" : "")
                            + (lead > 0.001 || parts.Count > 1 ? stereo : "");
                if (parts.Count == 1)
                {
                    graph.Add($"{piece},apad=whole_dur={whole}[{tag}]");
                    return $"[{tag}]";
                }

                graph.Add($"{piece}[{tag}_p{j}]");
                placed.Add($"[{tag}_p{j}]");
            }

            // The mix is given a clock of its own, counted from its samples (see PlaceMainAudio).
            graph.Add($"{string.Concat(placed)}amix=inputs={placed.Count}:normalize=0:dropout_transition=0,asetpts=N/SR/TB,apad=whole_dur={whole}[{tag}]");
            return $"[{tag}]";
        }

        /// <summary>
        /// The filter graph Live Preview runs on the picture, for mpv's lavfi-complex: the same per-frame chain the
        /// export uses (crop, the Frame &amp; Layer Engine, scaling, color, LUT), with the source's burned-in subtitles, from the
        /// video track to the screen. Empty when the export would not filter the picture at all (Copy), which
        /// is then what the preview shows too.
        ///
        /// The cuts are not part of it: the preview stays on the source's own timeline, where Play only cut
        /// segments does the skipping. Fades are placed by the length of the output and are left out, and so are
        /// auto-captions, which the player draws by itself while Preview Subtitles is on.
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

                // Auto-captions are not part of it: drawn here, the processor draws them on every frame, which
                // a long video does not keep up with. Preview Subtitles has the player draw them itself.
                return chain.Count == 0 ? "" : $"[vid1]{string.Join(",", chain)}[vo]";
            }
            finally
            {
                (_buildingLiveGraph, _liveScale) = (false, 1);
            }
        }

        /// <summary>
        /// The sound half of the live graph, from the player's audio tracks to its speakers: every audio track
        /// of the video that is kept (not muted), with what the export does to it, and the sound of the layers
        /// (video layers that carry sound, and audio clips), mixed. Empty when there is nothing to it but one
        /// track played as it is: the player is then given no audio graph, and plays that track itself.
        ///
        /// Each track, as the export has it: its silenced stretches, its slip, ducked under the voices when it is
        /// set to duck, then its own gain and processing (filters, EQ, compressor...). The fades in and out are
        /// heard too. Not the cuts, which playback skips over by itself (Play Only Cut Segments), nor loudness
        /// normalization, which holds seconds of sound back and would fall out of step with the picture.
        ///
        /// A layer's sound is read from its file inside the graph (amovie), as the export reads it, and mpv
        /// does not seek such a source: after every jump it begins again at its beginning. Nor does the mix look
        /// at time stamps; it adds up whatever samples arrive. So the sound is laid out from one moment, the one
        /// playback starts at: every layer is cut and held back as that moment needs, each track of the video is
        /// made to begin exactly there (aresample's first_pts drops what comes before it and fills in what is
        /// missing), and all of them are then counted from that moment (asetpts) and start level. A jump to
        /// another moment needs the graph built again for that moment, which is for whoever plays it to ask for.
        /// </summary>
        /// <param name="from">The moment playback starts from, in sequence seconds.</param>
        /// <param name="mainTrack">Negative when the video has no sound of its own; otherwise every kept track is heard.</param>
        public string BuildLiveAudioGraph(double from, int mainTrack)
        {
            if (!HasSource)
                return "";

            // Only what still has something to play: a clip that ended before this moment is not opened at all,
            // nor one that starts after the window (the graph is built again before it does).
            var layers = GetLayerAudioSources()
                .Where(l => (l.Duration <= 0.001 || LayerAudioStart(l) + l.Duration > from + 0.05)
                            && InWindow(LayerAudioStart(l), l.Duration > 0.001 ? LayerAudioStart(l) + l.Duration : double.PositiveInfinity))
                .ToList();
            var tracks = mainTrack < 0 ? [] : GetOutputAudioTracks().Where(t => !t.IsSilentBase).ToList();

            // Nothing that could be heard at all. Otherwise the sound always goes through the graph, even one plain
            // track: once a graph has taken mpv's audio tracks, mpv does not give them back to plain playback, so a
            // graph without sound after one with sound is silence (muting down to one track did exactly that).
            // With every track muted, the graph plays silence rather than mpv playing a muted track.
            if (layers.Count == 0 && mainTrack < 0)
                return "";

            // Every input in the one form, and counted from the moment playback starts: what is mixed, and what is
            // ducked under what, then lines up sample for sample.
            const string form = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
            const string fromHere = "asetpts=N/SR/TB";
            var first = ((long)Math.Round(from * 48000)).ToString(CultureInfo.InvariantCulture);
            var graph = new List<string>();
            var inputs = new List<(string Name, bool Voice, bool Ducked, IReadOnlyList<string> Filters)>();

            // Where the main video is not on the sequence the player plays a stand-in stretch of its file
            // (see MainViewModel.PlayerSource), whose sound is not to be heard.
            var gate = IsMainWholeSequence ? "" : $"volume=0:enable='not({FilterGraphBuilder.BuildMainGate(GetMainClips(), 0, SequenceSeconds)})',";

            // And it fades in and out with its clips' pictures.
            if (MainSoundEnvelope() is { } envelope)
                gate += $"volume='{envelope}':eval=frame,";
            for (var k = 0; k < tracks.Count; k++)
            {
                var track = tracks[k];
                var steps = new List<string>();

                // Silenced where it was silenced on the timeline: the times are the track's own, as the player reads it.
                foreach (var (start, end) in track.MutedSpans)
                    steps.Add($"volume=0:enable='between(t,{Number(start)},{Number(end)})'");

                // Slipped later: held back by as much. Slipped earlier: what comes before the moment it now plays
                // at is dropped, and the rest moved up. (Slipped later, the first moments after a jump are silent:
                // what would play there comes from before the point the player reads the track from.)
                if (track.Offset > 0.001)
                    steps.Add($"adelay={(int)Math.Round(track.Offset * 1000)}:all=1");
                else if (track.Offset < -0.001)
                    steps.Add($"atrim=start={Number(from - track.Offset)},asetpts=PTS-{Number(-track.Offset)}/TB");

                graph.Add($"[aid{(track.Index + 1).ToString(CultureInfo.InvariantCulture)}]{gate}{string.Concat(steps.Select(step => step + ","))}"
                          + $"aresample=48000:async=1:first_pts={first},{fromHere},{form}[lt{k}]");
                inputs.Add(($"lt{k}", track.IsVoice, track.AutoDuck && !track.IsVoice, track.GetFilterChain()));
            }

            if (tracks.Count == 0)
            {
                // No sound of the video's own: silence to the end of the sequence, for the rest to be mixed into.
                graph.Add($"anullsrc=r=48000:cl=stereo,atrim=end={Number(Math.Max(SequenceSeconds - from, 0.1))},{form}[lt_base]");
                inputs.Add(("lt_base", false, false, []));
            }

            // The layers bring their own gain and processing (see BuildLayerAudio).
            for (var n = 0; n < layers.Count; n++)
            {
                graph.Add($"{BuildLayerAudio(layers[n], from)},{fromHere},{form}[ll{n}]");
                inputs.Add(($"ll{n}", layers[n].IsVoice, layers[n] is { AutoDuck: true, IsVoice: false }, []));
            }

            // Auto-duck: the voices are mixed into one signal, which presses each ducked sound down while it is loud.
            var voices = inputs.Where(i => i.Voice).ToList();
            var ducked = inputs.Where(i => i.Ducked).ToList();
            if (voices.Count > 0 && ducked.Count > 0)
            {
                var keys = new List<string>();
                foreach (var voice in voices)
                {
                    graph.Add($"[{voice.Name}]asplit=2[{voice.Name}_hear][{voice.Name}_key]");
                    keys.Add($"[{voice.Name}_key]");
                    Rename(voice.Name, $"{voice.Name}_hear");
                }

                var key = keys[0];
                if (keys.Count > 1)
                {
                    graph.Add($"{string.Concat(keys)}amix=inputs={keys.Count}:duration=longest:normalize=0[dkey]");
                    key = "[dkey]";
                }

                if (ducked.Count > 1)
                {
                    graph.Add($"{key}asplit={ducked.Count}{string.Concat(ducked.Select((_, n) => $"[dkey{n}]"))}");
                    for (var n = 0; n < ducked.Count; n++)
                        Duck(ducked[n].Name, $"[dkey{n}]");
                }
                else
                {
                    Duck(ducked[0].Name, key);
                }

                // (A ducked sound is never a voice, so it still has the name it was given.)
                void Duck(string name, string keyLabel)
                {
                    graph.Add($"[{name}]{keyLabel}{DuckFilter}[{name}_ducked]");
                    Rename(name, $"{name}_ducked");
                }
            }

            // Each track's own gain and processing, after the ducking, as in the export.
            foreach (var input in inputs.Where(i => i.Filters.Count > 0).ToList())
            {
                graph.Add($"[{input.Name}]{string.Join(",", input.Filters)}[{input.Name}_fx]");
                Rename(input.Name, $"{input.Name}_fx");
            }

            // The fades over the whole, on the sequence's clock: the mix is counted from the moment playback starts.
            var finish = new List<string>();
            if (FadeIn && from < FadeSeconds)
                finish.Add($"volume='clip((t+{Number(from)})/{Number(FadeSeconds)},0,1)':eval=frame");
            if (FadeOut && SequenceSeconds > FadeSeconds)
                finish.Add($"volume='clip(({Number(SequenceSeconds)}-(t+{Number(from)}))/{Number(FadeSeconds)},0,1)':eval=frame");

            // The first input sets how long the mix lasts: a track of the video, or the silence standing in for one.
            var mix = inputs.Count == 1 ? $"[{inputs[0].Name}]anull" : $"{string.Concat(inputs.Select(i => $"[{i.Name}]"))}amix=inputs={inputs.Count}:duration=first:normalize=0:dropout_transition=0";
            graph.Add($"{mix}{string.Concat(finish.Select(f => "," + f))}{LiveAudioOutput}");
            return string.Join(";", graph);

            void Rename(string name, string to)
            {
                var at = inputs.FindIndex(i => i.Name == name);
                inputs[at] = inputs[at] with { Name = to };
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

        /// <summary>
        /// How a picture that is scaled for every frame is scaled: the cheapest way for Live Preview, which is a
        /// preview; the default (bicubic) for an export. Stills and masks are scaled once and keep the default.
        /// </summary>
        private string LiveScaleFlags => _buildingLiveGraph ? ":flags=fast_bilinear" : "";

        // ----- Render preview -----

        /// <summary>
        /// A short, small encode that shows what the current filters do to the picture: the same per-frame
        /// chain, layers and burned-in subtitles as the real command, then scaled down. Speed matters more
        /// than quality here, so it always uses the fastest software settings.
        /// </summary>
        /// <param name="ranges">The stretches of the timeline to show, joined end to end: one, or several cut segments.</param>
        /// <param name="captionsPath">An auto-caption file made for these stretches, or null.</param>
        public string BuildPreviewCommand(string outputPath, IReadOnlyList<(double Start, double End)> ranges, int percent, string? captionsPath, string? videoEncode = null)
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
            // The timeline proxy gives its own encoder and wants no sound: what is heard comes from the original files.
            var encode = videoEncode is null ? "-c:v libx264 -preset ultrafast -crf 24 -c:a aac -b:a 128k -movflags +faststart" : $"{videoEncode} -an -movflags +faststart";
            var hasSound = videoEncode is null && S.SourceMayHaveAudio;

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
            var all = GetMainClips();
            var clips = IsMainWholeSequence ? [] : all;
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

                // Its fades, where it now lies on the sequence (the whole file, untouched, lies from 0).
                if (all.Count == 1)
                    steps.AddRange(MainSoundFades(all[0], at: all[0].Start));

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
                          + string.Concat(MainSoundFades(clip, at: 0).Select(fade => "," + fade))
                          + (clip.Start > 0.001 ? $",adelay={(int)Math.Round(clip.Start * 1000)}:all=1" : "") + (mono ? ",aformat=channel_layouts=stereo" : "") + $"[{tag}_p{j}]");
            }

            // The mix is given a clock of its own, counted from its samples: what it inherits from clips taken out of
            // order is not one an encoder can follow, and the track came out empty.
            graph.Add($"{string.Concat(clips.Select((_, j) => $"[{tag}_p{j}]"))}amix=inputs={clips.Count}:normalize=0:dropout_transition=0,asetpts=N/SR/TB[{tag}]");
            return $"[{tag}]";
        }

        /// <summary>A main video clip's sound fading in and out with its picture, on a clock where the clip starts at <paramref name="at"/>.</summary>
        private static IEnumerable<string> MainSoundFades(MainClip clip, double at)
        {
            var length = clip.End - clip.Start;
            var fadeIn = Math.Min(clip.FadeIn, length);
            if (fadeIn > 0.01)
                yield return $"afade=t=in:st={Number(at)}:d={Ratio(fadeIn)}";
            var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - fadeIn, 0));
            if (fadeOut > 0.01)
                yield return $"afade=t=out:st={Number(at + length - fadeOut)}:d={Ratio(fadeOut)}";
        }

        /// <summary>
        /// The main video's sound as its clips' fades shape it, on the sequence's clock, for the live mix: each fade's ramp,
        /// one everywhere else, so that a clip's fade leaves the clips beside it alone. Null when no clip fades.
        /// </summary>
        private string? MainSoundEnvelope()
        {
            var terms = new StringBuilder();
            foreach (var clip in GetMainClips())
            {
                var length = clip.End - clip.Start;
                var fadeIn = Math.Min(clip.FadeIn, length);
                if (fadeIn > 0.01)
                    terms.Append($"*({Progress("t", clip.Start, fadeIn)}+lt(t,{Number(clip.Start)}))");
                var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - fadeIn, 0));
                if (fadeOut > 0.01)
                    terms.Append($"*(clip(({Number(clip.End)}-t)/{Ratio(fadeOut)},0,1)+gte(t,{Number(clip.End)}))");
            }

            return terms.Length > 0 ? "1" + terms : null;
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
        /// <param name="from">
        /// The moment of the sequence the chain's sound begins at: 0 for the export, which plays the whole
        /// sequence, and for Live Preview the moment playback starts from. What the layer would have played
        /// before then is skipped in its file, and it is held back only by what is left until it starts.
        /// </param>
        private string BuildLayerAudio(Layer layer, double from = 0)
        {
            // The clip's length is in the timeline's seconds; at its speed it plays that many times as much of its file.
            var speed = GetSpeed(layer);
            var startsAt = LayerAudioStart(layer);
            var into = layer.MediaOffset + Math.Max(from - startsAt, 0) * speed;

            // Read from a little before where it is wanted, when that is well into the file: the cut that follows is exact.
            var source = $"amovie='{EscapeFilterPath(layer.ImagePath)}'";
            if (from > 0 && into > 2)
                source += $":seek_point={Number(into - 1)}";

            var chain = new List<string> { source };
            var trim = into > 0.01 ? $"atrim=start={Number(into)}" : "";
            if (layer.Duration > 0.001)
                trim = (trim.Length > 0 ? trim + ":" : "atrim=") + $"end={Number(layer.MediaOffset + layer.Duration * speed)}";
            if (trim.Length > 0)
                chain.Add(trim + ",asetpts=PTS-STARTPTS");

            chain.Add("aresample=48000");
            chain.AddRange(BuildTempo(speed));
            // Its own processing and gain, as a track of the video has them.
            chain.AddRange(layer.AudioFilters.BuildChain(Math.Clamp(layer.AudioGainDb, -40, 24)));
            chain.AddRange(BuildClipFades(layer, Math.Max(from - startsAt, 0)));
            if (startsAt - from > 0.01)
                chain.Add($"adelay={(int)Math.Round((startsAt - from) * 1000)}:all=1");
            return string.Join(",", chain);
        }

        /// <summary>
        /// A clip's sound fades (its gain line's handles, and crossfades): in over its first seconds, out over its last, on
        /// the clock of the chain, which starts <paramref name="into"/> seconds into the clip (Live Preview, played from
        /// the middle of it). Started inside a fade, the fade runs from there on, from silence (a preview's approximation).
        /// </summary>
        private static IEnumerable<string> BuildClipFades(Layer layer, double into)
        {
            var fadeIn = Math.Clamp(layer.FadeInSeconds, 0, 60);
            if (fadeIn > 0.01 && into < fadeIn - 0.01)
                yield return $"afade=t=in:st=0:d={Number(fadeIn - into)}";

            var fadeOut = Math.Clamp(layer.FadeOutSeconds, 0, 60);
            if (fadeOut > 0.01 && layer.Duration > 0.001 && into < layer.Duration - 0.01)
            {
                var start = Math.Max(layer.Duration - fadeOut - into, 0);
                yield return $"afade=t=out:st={Number(start)}:d={Number(Math.Min(fadeOut, layer.Duration - into))}";
            }
        }

        /// <summary>Where on the sequence a layer's sound starts: with its picture, or as far from there as it was slipped.</summary>
        private double LayerAudioStart(Layer layer) => Math.Max(layer.StartTime + (AudioLinked || layer.IsAudio ? 0 : layer.AudioOffset), 0);

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

                // The player's frames are the proxy's when the main video has one (S.MainPreviewHeight): smaller to
                // start with, they are only made smaller when they are still larger than the frame needs.
                var factor = S.MainPreviewHeight > 0 && SourceHeight > 0 ? Math.Min(SourceHeight * _liveScale / S.MainPreviewHeight, 1) : _liveScale;
                if (factor < 0.999)
                    filters.Add($"scale=trunc(iw*{Number(factor)}/2)*2:trunc(ih*{Number(factor)}/2)*2:flags=fast_bilinear");
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

            // The Sequence Output's own effects, in stack order, on the finished picture. Its keyframes and ranges are
            // in sequence seconds; this stretch's clock starts at its beginning.
            if (S.SequenceEffects.Count > 0)
            {
                var (outWidth, outHeight) = (Sized(Math.Min(FrameWidth, SourceWidth > 0 ? SourceWidth : FrameWidth)), Sized(Math.Min(FrameHeight, SourceHeight > 0 ? SourceHeight : FrameHeight)));
                var context = EffectContext(outWidth, outHeight, -from);
                var output = new List<(string Filter, bool Geometric)>();
                foreach (var effect in S.SequenceEffects)
                    AppendEffect(output, effect, EffectZone.Look, context);
                filters.AddRange(output.Select(f => f.Filter));
            }

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
            // be all of it, and lends the canvas its clock as it does for Live Preview. A live graph with a window
            // looks at the window only: where the main video is not on screen there, its branches (the blurred
            // background, the main video, the pieces cut from it) are not made at all.
            var (seenFrom, seenTo) = live && _window is { } window ? (Math.Max(window.From, 0), Math.Min(window.To, SequenceSeconds)) : (from, to);
            var seen = GetSeenMain(seenFrom, seenTo);
            var known = seenTo - seenFrom > 0.001 && MainMediaSeconds > 0;
            var mainSeen = !known || seen.Count > 0;
            var throughout = !known;
            if (known && seen.Count > 0 && seen[0].From - seenFrom < 0.001 && seenTo - seen[^1].To < 0.001)
            {
                throughout = true;
                for (var i = 1; i < seen.Count && throughout; i++)
                    throughout = seen[i].From - seen[i - 1].To < 0.001;
            }

            var clockFromInput = live || !known;

            // The stack, bottom first, without the layers that have nothing to show: nothing of theirs in the
            // window, and the video layers while scrubbing hides them.
            var stack = GetStack().Where(l => l.IsMainVideo ? !l.IsHidden && mainSeen
                : l.IsUsable && (!l.IsVideo || mainSeen) && InWindow(l) && !(live && l.IsVideoFile && _layerMode == LiveLayerMode.Hidden)).ToList();
            var motions = stack.ConvertAll(l => GetMotion(l, width, height, from));

            // Each one's fades, and what its slides and zooms do to where it is laid and how large (an adjustment layer's
            // fades are its effects coming and going: see AppendAdjustment).
            var fades = stack.ConvertAll(l => l.IsAdjustment ? [] : PictureFades(l, from));
            for (var n = 0; n < stack.Count; n++)
            {
                if (fades[n].Count > 0)
                    motions[n] = WithTransitions(motions[n], fades[n], stack[n].IsMainVideo ? center : stack[n].GetOutputRect(width, height, SourceWidth, SourceHeight), width, height);
            }

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
                else if (live && stack[n].IsStill && motions[n].Reshapes)
                    branches.Add($"[ui{n}_tick{s}]");
            }

            var graph = RentBuilder();

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

                // Made of the main video, it comes on and goes with it: from black, by a dissolve whatever the main video's style.
                if (stack.FindIndex(l => l.IsMainVideo) is >= 0 and var mainAt && fades[mainAt].Count > 0)
                    AppendPictureFades(graph, "bgf", s, fades[mainAt].ConvertAll(f => f with { Style = TransitionStyle.Dissolve }), width, height);

                graph.Append($"[bg{s}];[base{s}][bg{s}]overlay=0:0{mainEnding}{Enabled(mainGate)}");
            }

            // The layers, each on the picture built so far. The running picture is labelled between steps;
            // after the last one it is left open, so the rest of the chain continues from it.
            for (var n = 0; n < stack.Count; n++)
            {
                var (layer, motion) = (stack[n], motions[n]);

                // An adjustment layer has no picture: its effects work on the picture built so far, while it lasts.
                if (layer.IsAdjustment)
                {
                    graph.Append($"[stage{n}{s}];");
                    AppendAdjustment(graph, layer, n, s, width, height, from, PictureFades(layer, from));
                    continue;
                }

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
                var mainSource = layer.IsMainVideo ? $"[center_in{s}]{(centerCrop is null ? "" : centerCrop + ",")}scale={builtWidth}:{builtHeight}{LiveScaleFlags}" : null;
                var tick = live && layer.IsStill && motion.Reshapes ? $"[ui{n}_tick{s}]" : null;
                graph.Append($"[stage{n}{s}];");
                AppendLayer(graph, layer, name, s, builtWidth, builtHeight, from, motion, mainSource, tick, fades[n]);

                // A turned picture is larger than the layer it was; it stays where its middle was, or, turned about an
                // anchor point that is not its middle, where that point puts its middle (see Pivot).
                var (boxWidth, boxHeight) = GetBox(layer, motion, builtWidth, builtHeight);
                var (anchorX, anchorY) = (layer.AnchorX, layer.AnchorY);
                var pivots = layer.HasOffsetAnchor && layer.HasFilters && (motion.Angle is not null || Math.Abs(layer.Rotation % 360) >= 0.05);
                string placeX, placeY;
                if (motion.Any)
                {
                    // The box is that much larger than the layer on each side, and grows and shrinks with it.
                    var size = motion.Width is null ? "" : $"*({motion.Width})/{builtWidth}";
                    var (marginX, marginY) = ((boxWidth - builtWidth) / 2.0, (boxHeight - builtHeight) / 2.0);
                    var (widthNow, heightNow) = motion.Width is null
                        ? (Number(layerWidth), Number(layerHeight))
                        : ($"({motion.Width})", $"({motion.Width})*{Ratio((double)builtHeight / builtWidth)}");

                    // The corner: its keyframes; or, with the size animated and the place not, where it is with the anchor
                    // point held still as the layer grows and shrinks about it.
                    var cornerX = motion.X ?? (motion.Width is null ? Number(x) : $"{Number(x + anchorX * layerWidth)}-{Ratio(anchorX)}*{widthNow}");
                    var cornerY = motion.Y ?? (motion.Width is null ? Number(y) : $"{Number(y + anchorY * layerHeight)}-{Ratio(anchorY)}*{heightNow}");
                    placeX = cornerX + (marginX == 0 ? "" : $"-{Number(marginX)}{size}");
                    placeY = cornerY + (marginY == 0 ? "" : $"-{Number(marginY)}{size}");
                    if (pivots)
                    {
                        var (cosine, sine) = motion.Angle is { } angle
                            ? ($"(cos(({angle})*PI/180)-1)", $"sin(({angle})*PI/180)")
                            : ($"({Ratio(Math.Cos(layer.Rotation * Math.PI / 180) - 1)})", $"({Ratio(Math.Sin(layer.Rotation * Math.PI / 180))})");
                        var (dx, dy) = ($"({Ratio(0.5 - anchorX)}*{widthNow})", $"({Ratio(0.5 - anchorY)}*{heightNow})");
                        placeX += $"+{dx}*{cosine}-{dy}*{sine}";
                        placeY += $"+{dx}*{sine}+{dy}*{cosine}";
                    }

                    // Sliding on or off over its fades.
                    if (motion.ShiftX is { } slideX)
                        placeX += $"+({slideX})";
                    if (motion.ShiftY is { } slideY)
                        placeY += $"+({slideY})";
                }
                else
                {
                    var (px, py) = (x - (boxWidth - layerWidth) / 2.0, y - (boxHeight - layerHeight) / 2.0);
                    if (pivots)
                    {
                        var (dx, dy) = Layer.Pivot(layer.Rotation, (0.5 - anchorX) * layerWidth, (0.5 - anchorY) * layerHeight);
                        (px, py) = (px + dx, py + dy);
                    }

                    (placeX, placeY) = (Number(Math.Round(px)), Number(Math.Round(py)));
                }

                string At(int offset) => motion.Any
                    ? $"x='{placeX}{(offset == 0 ? "" : "+" + offset)}':y='{placeY}{(offset == 0 ? "" : "+" + offset)}':eval=frame"
                    : $"{int.Parse(placeX, CultureInfo.InvariantCulture) + offset}:{int.Parse(placeY, CultureInfo.InvariantCulture) + offset}";

                var ending = new StringBuilder();
                if (layer.IsMainVideo || layer.IsVideo)
                    ending.Append(mainEnding);

                // A video from a file ends as its source does (see AppendVideoFile): a clip with a length goes when
                // it is over and the picture under it carries on, one that repeats lasts as long as the picture
                // under it. So does a still that was made into a stream to be animated.
                if (layer.IsVideoFile)
                    ending.Append(_videoEnding);
                else if (layer.IsStill && motion.Reshapes && !live)
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

                var (under, top) = ($"stage{n}{s}", $"{name}_layer{s}");
                if (layer.Shadow && layer.FxOn(EffectRegistry.Shadow))
                {
                    // The shadow is the layer's own outline in black at reduced opacity, laid down first and offset.
                    var offset = Placed(Math.Clamp(layer.ShadowOffset, 0, 200));
                    graph.Append($";[{name}_layer{s}]split[{name}_top{s}][{name}_shadow_in{s}]");
                    graph.Append($";[{name}_shadow_in{s}]format=yuva420p,lutyuv=y=16:u=128:v=128:a=val*{Number(Math.Clamp(layer.ShadowOpacity, 0, 1))},"
                                 + $"boxblur=2:1:0:0:2:1[{name}_shadow{s}]");
                    graph.Append($";[stage{n}{s}][{name}_shadow{s}]overlay={At(offset)}{ending}[{name}_shaded{s}]");
                    (under, top) = ($"{name}_shaded{s}", $"{name}_top{s}");
                }

                // Zone 4's blend mode: Normal is the overlay every layer always had.
                if (layer.BlendModeName is "multiply" or "screen" or "addition" or "overlay" && !layer.IsMainVideo)
                    AppendBlend(graph, under, top, $"{name}{s}", $"{At(0)}{ending}", layer.BlendModeName);
                else
                    graph.Append($";[{under}][{top}]overlay={At(0)}{ending}");
            }

            return ReturnBuilder(graph);
        }

        /// <summary>
        /// A layer laid on with a blend mode (zone 4): placed on a see-through copy of the picture under it, its colours
        /// mixed with that picture's in RGB, and the mix laid on through the layer's own transparency (so outside the layer,
        /// and outside its time, nothing changes). Left open at the end, as an overlay is. Amber: two passes over the frame.
        /// </summary>
        private static void AppendBlend(StringBuilder graph, string under, string top, string p, string placement, string mode)
        {
            graph.Append($";[{under}]split=3[{p}_u0][{p}_u1][{p}_u2]");
            graph.Append($";[{p}_u0]format=yuva420p,lutyuv=y=16:u=128:v=128:a=0[{p}_cv]");
            graph.Append($";[{p}_cv][{top}]overlay={placement},format=gbrap,split[{p}_lc][{p}_la]");
            graph.Append($";[{p}_la]alphaextract[{p}_lm];[{p}_lc]format=gbrp[{p}_lrgb];[{p}_u1]format=gbrp[{p}_urgb]");
            graph.Append($";[{p}_urgb][{p}_lrgb]blend=all_mode={mode}:shortest=1[{p}_mix];[{p}_mix][{p}_lm]alphamerge[{p}_mixa]");
            graph.Append($";[{p}_u2][{p}_mixa]overlay=0:0");
        }

        /// <summary>
        /// An adjustment layer: its Look effects worked on the picture built so far, while the clip lasts. Over the whole
        /// frame each filter is switched on for that stretch; within a smaller rectangle that part is cut out, worked
        /// on, and laid back for the stretch. Left open at the end, as an overlay is.
        /// </summary>
        /// <param name="fades">Its fades: its effects come on and go over them, the worked picture laid over the picture as it was by a dissolve or a wipe (a slide or a zoom is a dissolve here).</param>
        private void AppendAdjustment(StringBuilder graph, Layer layer, int n, string s, int width, int height, double from, List<PictureFade> fades)
        {
            var (x, y, w, h) = layer.GetOutputRect(width, height, SourceWidth, SourceHeight);
            var (left, top) = (Math.Clamp(x, 0, width), Math.Clamp(y, 0, height));
            var (right, bottom) = (Math.Clamp(x + w, 0, width), Math.Clamp(y + h, 0, height));
            var (cutWidth, cutHeight) = ((right - left) / 2 * 2, (bottom - top) / 2 * 2);
            var appears = layer.StartTime - from;
            var timed = $"between(t,{Number(appears)},{(layer.Duration > 0.001 ? Number(appears + layer.Duration) : "1e9")})";
            fades = fades.ConvertAll(f => TransitionStyles.IsSlide(f.Style) || f.Style == TransitionStyle.Zoom ? f with { Style = TransitionStyle.Dissolve } : f);
            var whole = left <= 0 && top <= 0 && right >= width && bottom >= height;

            if (whole && fades.Count == 0)
            {
                var filters = BuildLookFilters(layer, width, height, appears, timed);
                graph.Append($"[stage{n}{s}]").Append(filters.Count > 0 ? string.Join(",", filters.Select(f => f.Filter)) : "null");
                return;
            }

            if (whole)
                (left, top, cutWidth, cutHeight) = (0, 0, width, height);
            var inside = cutWidth >= 2 && cutHeight >= 2 ? BuildLookFilters(layer, cutWidth, cutHeight, appears) : new List<(string Filter, bool Geometric)>();
            if (inside.Count == 0)
            {
                graph.Append($"[stage{n}{s}]null");
                return;
            }

            graph.Append($"[stage{n}{s}]split[adj{n}_base{s}][adj{n}_cut{s}];[adj{n}_cut{s}]{(whole ? "" : $"crop={cutWidth}:{cutHeight}:{left}:{top},")}"
                         + string.Join(",", inside.Select(f => f.Filter)));
            AppendPictureFades(graph, $"adj{n}", s, fades, cutWidth, cutHeight);
            graph.Append($"[adj{n}_fx{s}];[adj{n}_base{s}][adj{n}_fx{s}]overlay={left}:{top}:enable='{timed}'");
        }

        // One builder is kept for each thread and used again: a graph is some kilobytes, written many times a
        // second while a layer is dragged, and the room for it is not asked for afresh each time.
        [ThreadStatic] private static StringBuilder? _pooledBuilder;

        private static StringBuilder RentBuilder()
        {
            var builder = _pooledBuilder ?? new StringBuilder(4096);
            _pooledBuilder = null;
            return builder.Clear();
        }

        private static string ReturnBuilder(StringBuilder builder)
        {
            var text = builder.ToString();
            if (builder.Capacity <= 256 * 1024)
                _pooledBuilder = builder;
            return text;
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
            layer.Opacity >= 100 && !layer.HasKeys(KeyProperty.Opacity) && !layer.ChromaKey && !(layer.CustomMask && Exists(layer.MaskPath.Trim().Trim('"'))) && !layer.IsCropped
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

        // ----- Effect stacks (Models/Effects) -----

        /// <summary>What an effect's compile is told: the picture's size, the frame's, how much smaller Live Preview builds it, and the clip's clock.</summary>
        private EffectCompileContext EffectContext(int width, int height, double clipStart) => new()
        {
            Width = width, Height = height, FrameWidth = Sized(FrameWidth), PixelScale = _liveScale, ClipStart = clipStart,
            Exists = Exists, EscapePath = EscapeFilterPath, KeyExpression = (keys, start) => KeyExpression(keys, start, 1),
            InstanceName = $"fx{++_effectInstances}",
        };

        // How many effect contexts have been made for this graph: each gives its filters a name of their own (sendcmd).
        private int _effectInstances;

        /// <summary>
        /// A card's filters, if it belongs to the zone being compiled and does anything: switched off, a missing effect,
        /// values that change nothing and (while scrubbing in Draft Mode) a red-cost effect emit nothing. A card with a
        /// range is only at work inside it; <paramref name="gate"/> limits it further (an adjustment layer's own stretch).
        /// </summary>
        private void AppendEffect(List<(string Filter, bool Geometric)> filters, EffectInstance effect, EffectZone zone, EffectCompileContext context, string? gate = null)
        {
            if (!effect.Enabled || effect.Definition is not { Compile: { } compile } definition || definition.Zone != zone)
                return;
            if (_skipHeavyEffects && definition.Cost == EffectCost.Red)
                return;
            if (compile(context, effect) is not { Count: > 0 } compiled)
                return;

            var range = effect.Range is { } window
                ? $"between(t,{Number(context.ClipStart + window.Start)},{Number(context.ClipStart + window.End)})"
                : null;
            // sendcmd has no timeline: it is never switched off, and the filter it talks to is.
            var when = range is not null && gate is not null ? $"{range}*{gate}" : range ?? gate;
            foreach (var filter in compiled)
                filters.Add((when is null || filter.StartsWith("sendcmd=", StringComparison.Ordinal) ? filter : WithOption(filter, $"enable='{when}'"), definition.Geometric));
        }

        /// <summary>One more option on a filter: after its others, or as its first.</summary>
        private static string WithOption(string filter, string option) => filter.Contains('=') ? $"{filter}:{option}" : $"{filter}={option}";

        /// <summary>A layer's Look effects (zone 2), in stack order: each filter, and whether it moves pixels (a flip).</summary>
        /// <param name="clipStart">Where the clip begins on the clock of the picture being composed.</param>
        private List<(string Filter, bool Geometric)> BuildLookFilters(Layer layer, int width, int height, double clipStart, string? gate = null)
        {
            var filters = new List<(string Filter, bool Geometric)>();
            if (!layer.HasFilters || layer.EffectsBypassed)
                return filters;

            var context = EffectContext(width, height, clipStart);
            foreach (var effect in layer.Effects)
                AppendEffect(filters, effect, EffectZone.Look, context, gate);
            return filters;
        }

        /// <summary>
        /// The Key and Matte zone (3), in stack order. The key writes the transparency itself; everything else works on
        /// it: consecutive matte steps share one split, alphaextract ... alphamerge, the mask and the edges multiplying
        /// it (one group, where the first of their cards is; with neither, the crop's at the end) and the choke and
        /// smoothing filtering it. A layer that only has the old key, mask and edges compiles exactly as before stacks.
        /// </summary>
        private void AppendKeyAndMatte(StringBuilder chain, Layer layer, string name, string s, int width, int height, double clipStart)
        {
            var context = EffectContext(width, height, clipStart);
            var formatted = false;
            var run = new List<string>();
            var sources = new StringBuilder();
            var runs = 0;
            var maskPlaced = false;

            void Format()
            {
                if (!formatted)
                    chain.Append(",format=yuva420p");
                formatted = true;
            }

            // The run's labels: the first run keeps the names the mask chain always had.
            string Prefix() => runs == 0 ? name : $"{name}r{runs}";

            void Flush()
            {
                if (run.Count == 0)
                    return;

                Format();
                var p = Prefix();
                chain.Append($",split[{p}_pic{s}][{p}_a_in{s}];[{p}_a_in{s}]alphaextract[{p}_a0{s}]");
                chain.Append(sources);
                for (var k = 0; k < run.Count; k++)
                    chain.Append($";[{p}_a{k}{s}]{run[k]}[{p}_a{k + 1}{s}]");
                chain.Append($";[{p}_pic{s}][{p}_a{run.Count}{s}]alphamerge");
                run.Clear();
                sources.Clear();
                runs++;
            }

            // The mask and the edges: grey pictures the size of the layer, white where it is to show, each made once
            // and held, not worked out again for every frame. With a mask, Soft Edges softens the mask's outline,
            // and the rectangle only has its corners rounded.
            void PlaceMasks()
            {
                maskPlaced = true;
                var p = Prefix();
                var maskFile = layer.CustomMask && layer.HasMask && layer.FxOn(EffectRegistry.Mask) ? layer.MaskPath.Trim().Trim('"') : "";
                var hasMask = maskFile.Length > 0 && Exists(maskFile);
                if (hasMask || layer.IsCropped)
                {
                    sources.Append(';').Append(BuildMaskSource(layer, hasMask ? maskFile : null, hasMask && S.AlphaMasks.Contains(maskFile), width, height, _liveScale))
                        .Append($",trim=end_frame=1,loop=loop=-1:size=1[{p}_cmask{s}]");
                    run.Add($"[{p}_cmask{s}]blend=all_mode=multiply:shortest=1");
                }

                if (BuildLayerMask(layer, width, height, _liveScale, softEdge: !hasMask) is { } shape)
                {
                    sources.Append($";color=c=black:s={width}x{height}:r=1,format=gray,geq=lum='255*{shape}',trim=end_frame=1,loop=loop=-1:size=1[{p}_smask{s}]");
                    run.Add($"[{p}_smask{s}]blend=all_mode=multiply:shortest=1");
                }
            }

            IEnumerable<EffectInstance> cards = layer.EffectsBypassed ? [] : layer.Effects;
            foreach (var effect in cards)
            {
                if (!effect.Enabled || effect.Definition is not { Zone: EffectZone.KeyAndMatte } definition || _skipHeavyEffects && definition.Cost == EffectCost.Red)
                    continue;

                switch (definition.Type)
                {
                    case EffectRegistry.ChromaKey when layer.ChromaKey && layer.HasKeying:
                    {
                        Flush();
                        var key = KeyColor(layer.ChromaColor);
                        var gate = effect.Range is { } window ? $":enable='between(t,{Number(clipStart + window.Start)},{Number(clipStart + window.End)})'" : "";

                        // Similarity and Blend animated: chromakey takes them as commands (it has no per-frame expressions).
                        var (similarity, blend) = (Number(Math.Clamp(layer.ChromaSimilarity, 0.01, 1)), Number(Math.Clamp(layer.ChromaBlend, 0, 1)));
                        var keyer = "chromakey";
                        if (context.Commands(effect, "chromakey", ("similarity", "similarity", 0.01, 1), ("blend", "blend", 0, 1)) is { } commands)
                        {
                            if (effect.HasKeys("similarity"))
                                similarity = context.Initial(effect, "similarity", 0.01, 1);
                            if (effect.HasKeys("blend"))
                                blend = context.Initial(effect, "blend", 0, 1);
                            keyer = $"chromakey@{context.InstanceName}";
                            chain.Append(',').Append(commands);
                        }

                        chain.Append($",{keyer}=0x{key}:{similarity}:{blend}{gate}");

                        // The key colour's spill on what is left: green or blue, whichever the key is nearer.
                        if (effect.Get("despill") is >= 0.005 and var despill)
                        {
                            var blue = Convert.ToInt32(key[4..6], 16) > Convert.ToInt32(key[2..4], 16);
                            chain.Append($",despill=type={(blue ? "blue" : "green")}:mix={Number(Math.Clamp(despill, 0, 1))}:expand=0{gate}");
                        }

                        break;
                    }

                    case EffectRegistry.Mask or EffectRegistry.Edges when !maskPlaced:
                        PlaceMasks();
                        break;

                    default:
                        if (definition.AlphaOnly)
                        {
                            var filters = new List<(string Filter, bool Geometric)>();
                            AppendEffect(filters, effect, EffectZone.KeyAndMatte, context);
                            if (filters.Count > 0)
                                run.Add(string.Join(",", filters.Select(f => f.Filter)));
                        }

                        break;
                }
            }

            // A cropped layer with no mask or edges card still has its crop cut away.
            if (!maskPlaced && layer.IsCropped)
                PlaceMasks();
            Flush();
            Format();
        }

        /// <summary>
        /// Writes one layer into the graph as a finished picture with its transparency, labelled [name_layer]: cut from the main video
        /// (or the main video itself), or read from its image or video file, and scaled. Its own filters are then
        /// applied to it. What shows of it is the product of everything that makes parts of it transparent: its
        /// own transparency (a PNG's, or a color keyed out), a custom mask from a file, and the rounded or
        /// feathered outline. Opacity scales whatever results, and last of all the picture is turned, and, when
        /// its size is animated, scaled frame by frame.
        /// </summary>
        /// <param name="width">The size it is built at: its size on the frame, or the largest it gets when that is animated.</param>
        /// <param name="mainSource">For the main video: the chain that brings its picture, already scaled.</param>
        /// <param name="tick">For a still that changes shape in Live Preview: the branch of the player's frames that gives it a clock.</param>
        private void AppendLayer(StringBuilder chain, Layer layer, string name, string s, int width, int height, double startSeconds, Motion motion, string? mainSource = null, string? tick = null,
            List<PictureFade>? fades = null)
        {
            if (mainSource is not null)
            {
                chain.Append(mainSource);
            }
            else if (layer.IsVideoFile)
            {
                AppendVideoFile(chain, layer, startSeconds);
                chain.Append($",scale={width}:{height}{LiveScaleFlags}");
            }
            else if (layer.IsImage)
            {
                // A single frame, which the overlay repeats for as long as the video runs.
                chain.Append($"movie='{EscapeFilterPath(layer.ImagePath)}',scale={width}:{height}");
            }
            else if (layer.IsText)
            {
                AppendTextSource(chain, layer, width, height);
            }
            else
            {
                // Cut out of the source by fractions of the frame, so the same layer works at any source size.
                chain.Append($"[{name}_in{s}]crop=trunc(iw*{Fraction(layer.SourceWidth)}/2)*2:trunc(ih*{Fraction(layer.SourceHeight)}/2)*2:"
                             + $"iw*{Fraction(layer.SourceX)}:ih*{Fraction(layer.SourceY)},scale={width}:{height}{LiveScaleFlags}");
            }

            // Zone 2, the Look effects, in stack order.
            var clipStart = layer.IsMainVideo ? 0 : layer.StartTime - startSeconds;
            var own = BuildLookFilters(layer, width, height, clipStart);
            if (own.Count > 0 && layer.IsStill)
            {
                // The colour filters know nothing of transparency: a picture's own is set aside and put back after them,
                // the flips done to it as well, the whole zone at once.
                chain.Append($",format=yuva420p,split[{name}_fc{s}][{name}_fa_in{s}];[{name}_fa_in{s}]alphaextract"
                             + $"{string.Concat(own.Where(f => f.Geometric).Select(f => "," + f.Filter))}[{name}_fa{s}];"
                             + $"[{name}_fc{s}]{string.Join(",", own.Select(f => f.Filter))}[{name}_fd{s}];[{name}_fd{s}][{name}_fa{s}]alphamerge");
            }
            else if (own.Count > 0)
            {
                chain.Append(',').Append(string.Join(",", own.Select(f => f.Filter)));
            }

            // Zone 3, the key and the matte, in stack order; the picture has its transparency from here on.
            AppendKeyAndMatte(chain, layer, name, s, width, height, clipStart);

            var opacity = Math.Clamp(layer.Opacity, 0, 100) / 100.0;
            if (opacity < 1 && motion.Opacity is null)
                chain.Append($",lutyuv=a=val*{Number(opacity)}");

            // A still picture is one frame, and one frame cannot change shape as time passes: it is made into a
            // frame for every tick first. For an encode it is simply repeated at the canvas's rate. For Live
            // Preview it is laid on see-through frames cut from the player's own, which carry the player's clock
            // through every seek; a stream counted up from nothing would have to be run through to catch up.
            if (layer.IsStill && motion.Reshapes)
            {
                if (tick is null)
                    chain.Append($",loop=loop=-1:size=1,setpts=N/({CanvasRate}*TB)");
                else
                    chain.Append($"[{name}_still{s}];{tick}crop=2:2:0:0,format=yuva420p,lutyuv=a=0,scale={width}:{height}:flags=neighbor[{name}_clock{s}];"
                                 + $"[{name}_clock{s}][{name}_still{s}]overlay=format=auto,format=yuva420p");
            }

            // Fading by keyframes: only the transparency is worked out again for each frame, on the frame's own time.
            if (motion.Opacity is { } fade)
            {
                chain.Append($",format=yuva420p,split[{name}_op{s}][{name}_opa_in{s}];[{name}_opa_in{s}]alphaextract,geq=lum='lum(X,Y)*clip({fade},0,1)'[{name}_opa{s}];"
                             + $"[{name}_op{s}][{name}_opa{s}]alphamerge");
            }

            // Turned about its middle, into a box large enough for its corners; what the box adds is transparent.
            var (boxWidth, boxHeight) = GetBox(layer, motion, width, height);
            if (motion.Angle is not null)
                chain.Append($",rotate='({motion.Angle})*PI/180':ow={boxWidth}:oh={boxHeight}:c=black@0");
            else if ((boxWidth, boxHeight) != (width, height) || (layer.HasFilters && Math.Abs(layer.Rotation % 360) >= 0.05))
                chain.Append($",rotate={Number(layer.Rotation)}*PI/180:ow={boxWidth}:oh={boxHeight}:c=black@0");

            // Its fades' transparency (dissolve, wipe, blur), along the frame's own axes, before any size that changes by
            // the frame: every filter in the chain keeps the size it was made for.
            if (fades is { Count: > 0 })
                AppendPictureFades(chain, name, s, fades, boxWidth, boxHeight);

            // An animated size: the finished picture, box and all, scaled for each frame from the size it was built at.
            if (motion.Width is not null)
            {
                var across = boxWidth == width ? motion.Width : $"({motion.Width})*{Ratio((double)boxWidth / width)}";
                chain.Append($",scale=w='max(2,trunc({across}))':h='max(2,trunc(({across})*{Ratio((double)boxHeight / boxWidth)}))':eval=frame");
                if (_buildingLiveGraph)
                    chain.Append(":flags=fast_bilinear");
            }

            chain.Append($"[{name}_layer{s}]");
        }

        private static string Ratio(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        // How the video layer last written by AppendVideoFile ends, for its overlay.
        private string _videoEnding = ":shortest=1";

        /// <summary>
        /// A video layer's picture, read by a source filter inside the graph (so no second -i is needed), on the
        /// clock of the stretch being composed: its first moment (MediaOffset into its file) lands where the clip
        /// begins.
        ///
        /// A clip with a length, from a file whose start is known, is read from where it is first wanted: the file
        /// is opened at the I-frame before that moment (seek_point), cut exactly there and at the clip's end on the
        /// file's own time stamps, and moved to its place. Nothing before that moment is decoded and nothing after
        /// the clip's end: its decoder stops there, and the overlay lets the picture under it through. The moment
        /// is the playhead for a live graph with a window, the start of the stretch for an export.
        ///
        /// A clip with no length repeats without end from the file's first frame, counted on an even clock, as
        /// layers always were; so is a clip from a file not yet inspected, in an export (a live graph takes such a
        /// file to start at 0). While scrubbing (<see cref="LiveLayerMode.Still"/>) a clip is one frame, the I-frame
        /// at or before the playhead, held.
        /// At a speed other than 1 the clip covers its file that many times as fast: how far into the file it is, is
        /// worked out in the file's own seconds, and its frames are then given the timeline's.
        /// </summary>
        // How far before the moment wanted a video layer's file is asked to seek to, in seconds: live, and in an export.
        private const double LiveSeekLead = 0.05, ExportSeekLead = 1;

        // How far before the playhead a live graph's video layer is cut from, in the timeline's seconds (see AppendVideoFile).
        private const double LiveCutLead = 0.1;

        private void AppendVideoFile(StringBuilder chain, Layer layer, double startSeconds)
        {
            // Live Preview reads the file's proxy when it has one: a small copy, starting at its first frame.
            var file = layer.ImagePath.Trim().Trim('"');
            string? proxy = null;
            var proxied = _buildingLiveGraph && S.PreviewPaths.TryGetValue(file, out proxy);
            var path = EscapeFilterPath(proxied ? proxy! : file);
            var speed = GetSpeed(layer);
            var retimed = IsRetimed(speed);
            var windowed = _buildingLiveGraph && _window is not null;
            var origin = 0.0;
            var known = proxied || S.MediaStarts.TryGetValue(file, out origin) || _buildingLiveGraph;

            if (layer.Duration > 0.001 && known)
            {
                // How far into the file it is read from, and where on this stretch's clock its first moment falls.
                var into = layer.MediaOffset + Math.Max((windowed ? _playhead : startSeconds) - layer.StartTime, 0) * speed;
                var lead = layer.StartTime - startSeconds;
                chain.Append($"movie='{path}'");

                if (windowed && _layerMode == LiveLayerMode.Still)
                {
                    // Half-second steps, so that a drag of a pixel or two asks for the frame it already has.
                    var at = Math.Floor(into * 2) / 2;
                    if (at > 0.01)
                        chain.Append(":sp=").Append(Number(at));
                    chain.Append(",trim=end_frame=1,setpts=PTS-STARTPTS");
                    _videoEnding = "";
                    return;
                }

                // The seek lands on the I-frame at or before the point asked for (movie= seeks backwards), and the
                // cut that follows is exact. Live, it is asked a hair early, only so that rounding never lands it past
                // the point: a second early decoded up to a second more before the first frame came out, which was
                // the wait on pressing Play. An export keeps the second: a file with no index (MPEG-TS) may land on a
                // frame that is not an I-frame, and the frames up to the next one would be lost.
                // Live, the cut starts a few frames before the playhead (never before the clip): the frame mpv stops on
                // after a seek can be the main picture's frame just before the moment asked for, and with a layer that
                // only began after it, overlay lets the picture under it through, and the paused frame has no layer
                // (seen with the graph that carries sound, which mpv lands a frame early on). The clock is unchanged.
                var cutFrom = windowed ? Math.Max(into - LiveCutLead * speed, layer.MediaOffset) : into;
                var early = _buildingLiveGraph ? LiveSeekLead : ExportSeekLead;

                // Live, with the file's I-frames listed: opened at the I-frame at or before the cut, exactly. A lead before
                // the cut lands one I-frame early whenever an I-frame lies within the lead of the cut (a whole GOP more to
                // decode, seconds of 4K AV1). The seek point is counted from the container's clock, and is a millisecond past
                // the I-frame (never halfway to the next), so that rounding can never put it before it.
                if (_buildingLiveGraph && !proxied && S.SeekIndexes.TryGetValue(file, out var index) && index.Around(origin + cutFrom) is { At: var iFrame, Next: var next })
                {
                    var point = iFrame - index.ContainerStart + Math.Min(0.001, (next - iFrame) / 2);
                    if (point > 0.0005)
                        chain.Append(":sp=").Append(point.ToString("0.######", CultureInfo.InvariantCulture));
                }
                else if (cutFrom - early > 0.01)
                {
                    chain.Append(":sp=").Append(Number(cutFrom - early));
                }
                if (_buildingLiveGraph && _decodeThreads > 0)
                    chain.Append(":dec_threads=").Append(_decodeThreads.ToString(CultureInfo.InvariantCulture));

                chain.Append(",trim=");
                if (cutFrom > 0.001)
                    chain.Append("start=").Append(Number(origin + cutFrom)).Append(':');
                chain.Append("end=").Append(Number(origin + layer.MediaOffset + layer.Duration * speed));

                // The file's moment f is shown at lead + (f - origin - MediaOffset) / speed.
                var shift = origin + layer.MediaOffset;
                chain.Append(retimed
                    ? $",setpts=(PTS-{Number(shift)}/TB)/{Ratio(speed)}{Signed(lead)}/TB"
                    : $",setpts=PTS{Signed(lead - shift)}/TB");
                _videoEnding = ":eof_action=pass";
                return;
            }

            // Repeating without end, given an even clock, and started as far in as this stretch is.
            chain.Append($"movie='{path}':loop=0,setpts=N/FRAME_RATE/TB");
            var from = (startSeconds - layer.StartTime) * speed + layer.MediaOffset;
            if (from > 0.01)
                chain.Append($",trim=start={Number(from)},setpts=PTS-STARTPTS{(retimed ? "," + BuildRetime(speed) : "")}");
            else if (from < -0.01)
                chain.Append(retimed ? $",{BuildRetime(speed)}+{Number(-from / speed)}/TB" : $",setpts=PTS+{Number(-from)}/TB");
            else if (retimed)
                chain.Append(',').Append(BuildRetime(speed));
            _videoEnding = ":shortest=1";
        }

        /// <summary>
        /// A text layer's picture: its words drawn by drawtext on a see-through canvas the size the layer is built
        /// at. It is one frame, as an image from a file is, and from here on it is treated as one: masked, faded,
        /// turned, and moved and scaled by its keyframes like any other picture.
        ///
        /// Each line is drawn by a drawtext of its own, centred, with its baseline where the layout puts it
        /// (drawtext places a line by the top of its tallest letter, so that height is taken off again: "y-ascent").
        /// The font is scaled to the canvas, so the words fill the layer's box at whatever size it is built.
        /// The canvas is see-through but has a colour, the outline's or the letters': the soft edge of a letter
        /// fades towards it, and so shows no dark fringe.
        /// </summary>
        private static void AppendTextSource(StringBuilder chain, Layer layer, int width, int height)
        {
            var layout = TextLayout.Measure(layer);
            var scale = Math.Min((double)width / layout.Width, (double)height / layout.Height);
            var border = layer.OutlineThickness > 0 ? Math.Max((int)Math.Round(Math.Clamp(layer.OutlineThickness, 0, TextLayout.LargestOutline) * scale), 1) : 0;
            var (letters, outline) = (TextColor(layer.FontColor, "FFFFFF"), TextColor(layer.OutlineColor, "000000"));
            var font = layout.FontFile.Length > 0 ? $"fontfile='{EscapeFilterPath(layout.FontFile)}'" : $"font={EscapeDrawText(layout.FontName)}";

            chain.Append($"color=c=0x{(border > 0 ? outline : letters)}@0:s={width}x{height}:r=1,format=yuva444p");

            // The lines are in the middle of the canvas, top to bottom, as they are in the middle of the layout.
            var top = (height - layout.Lines.Count * layout.LineHeight * scale) / 2;
            for (var i = 0; i < layout.Lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(layout.Lines[i]))
                    continue;

                var baseline = top + (i * layout.LineHeight + layout.Baseline) * scale;
                chain.Append($",drawtext={font}:text={EscapeDrawText(layout.Lines[i])}:expansion=none"
                             + $":fontsize={Number(Math.Max(layer.FontSize * scale, 1))}:fontcolor=0x{letters}");
                if (border > 0)
                    chain.Append($":bordercolor=0x{outline}:borderw={border}");
                chain.Append($":x=(w-text_w)/2:y={Number(baseline)}-ascent");
            }

            chain.Append(",trim=end_frame=1");
        }

        /// <summary>#RRGGBB as FFmpeg writes a color, RRGGBB; anything unreadable becomes the fallback.</summary>
        private static string TextColor(string? color, string fallback)
        {
            var hex = (color ?? "").Trim().TrimStart('#');
            return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : fallback;
        }

        /// <summary>
        /// Words as the value of a drawtext option, quoted, for a filter graph that is itself inside a command.
        /// The words pass through three readers, and each has characters of its own to be kept from:
        ///
        ///   drawtext itself   would expand "%{...}" and "\"; it is told not to (expansion=none), so nothing here.
        ///   the option list   ends a value at ":" and reads "\" and "'" itself: each gets a backslash.
        ///   the filter graph  reads "'", "\", ",", ";", "[" and "]": the value is put in single quotes, where
        ///                     all of them are plain, and a quote inside it is written by closing the quotes,
        ///                     escaping one, and opening them again.
        ///
        /// Outside all of those, the graph sits in double quotes on a command line, where a double quote cannot be
        /// written at all without the command being read differently by everything that reads it. A straight
        /// double quote is therefore drawn as a typographic one, opening or closing by where it stands.
        /// Line breaks and other control characters never get here: a line is one drawtext.
        /// </summary>
        private static string EscapeDrawText(string text)
        {
            var value = new StringBuilder(text.Length + 16).Append('\'');
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\\': value.Append(@"\\"); break;
                    case ':': value.Append(@"\:"); break;
                    case '\'': value.Append(@"\'\''"); break;
                    case '"': value.Append(i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is '(' or '[' or '{' ? '\u201C' : '\u201D'); break;
                    default:
                        if (!char.IsControl(c))
                            value.Append(c);
                        break;
                }
            }

            return value.Append('\'').ToString();
        }

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
        private static string? BuildLayerMask(Layer element, int width, int height, double pixelScale = 1, bool softEdge = true)
        {
            // The edges card switched off (or every effect bypassed): a plain rectangle.
            if (!element.FxOn(EffectRegistry.Edges))
                return null;

            var limit = Math.Max(Math.Min(width, height) / 2 - 1, 1);
            var corner = Math.Min((int)Math.Round(Math.Clamp(element.CornerRadius, 0, 50) / 100.0 * Math.Min(width, height)), limit);
            var feather = softEdge && element.Feather ? Math.Clamp((int)Math.Round(element.FeatherRadius * pixelScale), 1, limit) : 0;
            if (corner == 0 && feather == 0)
                return null;

            // A feathered edge is at least as round as it is soft; without a feather the edge is one pixel of anti-aliasing.
            var radius = Math.Max(corner, feather);
            var ramp = Math.Max(feather, 1);
            return $"clip(({radius}.5-hypot(max({radius}-X,0)+max(X-(W-1-{radius}),0),max({radius}-Y,0)+max(Y-(H-1-{radius}),0)))/{ramp},0,1)";
        }

        /// <summary>
        /// A layer's mask as a grey picture the size of the layer, white where it shows. From its file: for a
        /// mask of the whole frame, the part under the layer's cut-out, cut as the picture is; then grown or
        /// shrunk, and turned inside out. Then the cropped edges are cut away, and the outline softened. With no
        /// file, the crop alone. It is one picture, worked out once: none of this is done again for each frame.
        /// </summary>
        /// <param name="fromAlpha">The picture is read by its transparency: painted shows, see-through is cut away.</param>
        private static string BuildMaskSource(Layer layer, string? file, bool fromAlpha, int width, int height, double pixelScale)
        {
            var source = new StringBuilder();
            if (file is null)
            {
                source.Append($"color=c=white:s={width}x{height}:r=1,format=gray");
            }
            else
            {
                source.Append($"movie='{EscapeFilterPath(file)}',{MaskReader(fromAlpha)}");
                if (layer.UsesFrameMask)
                    source.Append($",crop=iw*{Fraction(layer.SourceWidth)}:ih*{Fraction(layer.SourceHeight)}:iw*{Fraction(layer.SourceX)}:ih*{Fraction(layer.SourceY)}");
                source.Append($",scale={width}:{height}");
                if (layer.MaskInvert)
                    source.Append(",geq=lum='255-lum(X,Y)'");

                // Grown or shrunk by so many pixels: one pass of dilation (or erosion) moves the edge out (or in) by one.
                var grow = layer.MaskGrow == 0 ? 0 : Math.Max((int)Math.Round(Math.Abs(Math.Clamp(layer.MaskGrow, -Layer.MaskGrowLimit, Layer.MaskGrowLimit)) * pixelScale), 1);
                for (var pass = 0; pass < grow; pass++)
                    source.Append(layer.MaskGrow > 0 ? ",dilation" : ",erosion");
            }

            // Cropped: each edge is black for as far in as it is cut.
            if (layer.IsCropped)
            {
                var (left, right) = ((int)Math.Round(layer.CropLeft * width), (int)Math.Round(layer.CropRight * width));
                var (top, bottom) = ((int)Math.Round(layer.CropTop * height), (int)Math.Round(layer.CropBottom * height));
                source.Append($",geq=lum='lum(X,Y)*gte(X,{left})*lt(X,{width - right})*gte(Y,{top})*lt(Y,{height - bottom})'");
            }

            // Soft edges: the mask's own outline, and the crop's, fade out over the Soft Edge width. A box blur run
            // twice, as wide as a Gaussian of half the Soft Edge would be: gblur takes libmpv down when its result is
            // blended into a layer's transparency (the export's FFmpeg does not mind, but the player must run the same graph).
            if (layer.Feather && layer.FxOn(EffectRegistry.Edges) && BoxRadius(Math.Clamp(layer.FeatherRadius, 1, 100) * pixelScale / 2, width, height) is > 0 and var radius)
                source.Append($",boxblur=luma_radius={radius}:luma_power=2");
            return source.ToString();
        }

        /// <summary>
        /// The radius of a box blur that, run twice, spreads as far as a Gaussian of the given sigma: two passes of
        /// a box of radius r have a variance of ((2r + 1)^2 - 1) / 6. Held to what boxblur accepts for a picture of
        /// this size (no more than half its shorter side); 0 for no blur at all.
        /// </summary>
        private static int BoxRadius(double sigma, int width, int height)
        {
            var radius = (int)Math.Round((Math.Sqrt(6 * sigma * sigma + 1) - 1) / 2);
            return Math.Clamp(radius, 0, Math.Max(Math.Min(width, height) / 2 - 1, 0));
        }

        /// <summary>How a mask picture is turned into grey: its transparency, or its brightness.</summary>
        private static string MaskReader(bool fromAlpha) => fromAlpha ? "format=rgba,alphaextract" : "format=gray";

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
        /// <param name="time">What the clock is called where the expression is used: t in most filters, T in geq.</param>
        private static string KeyExpression(IReadOnlyList<Keyframe> keys, double clipStart, double scale, string time = "t")
        {
            var text = new StringBuilder(16 + keys.Count * 36);
            text.Append(Number(keys[0].Value * scale));
            for (var i = 1; i < keys.Count; i++)
            {
                var change = (keys[i].Value - keys[i - 1].Value) * scale;
                if (Math.Abs(change) < 0.0005)
                    continue;

                var (from, length) = (clipStart + keys[i - 1].Time, keys[i].Time - keys[i - 1].Time);
                var easing = keys[i - 1].Easing;

                // Two keyframes at the same moment are a jump; a Hold is one at the next keyframe.
                if (length < 0.001 || easing == EasingType.Hold)
                {
                    text.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)));
                    text.Append($"*gte({time},").Append(Number(easing == EasingType.Hold ? from + length : from)).Append(')');
                }

                // A curve with no closed form (Bounce, Custom Bézier): one clipped ramp for each of its straight pieces,
                // every one a term of the same flat sum. A piece that changes nothing adds nothing.
                else if (EasingCurves.IsPieced(easing))
                {
                    var points = EasingCurves.PiecesOf(keys[i - 1]);
                    for (var k = 1; k < points.Length; k++)
                    {
                        var part = change * (points[k].E - points[k - 1].E);
                        if (Math.Abs(part) < 0.0005)
                            continue;

                        var (pieceFrom, pieceLength) = (from + points[k - 1].U * length, (points[k].U - points[k - 1].U) * length);
                        text.Append(part < 0 ? '-' : '+').Append(Number(Math.Abs(part)));
                        if (pieceLength < 0.001)
                            text.Append($"*gte({time},").Append(Number(pieceFrom)).Append(')');
                        else
                            text.Append('*').Append(Progress(time, pieceFrom, pieceLength));
                    }
                }
                else
                {
                    text.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)));
                    text.Append('*').Append(FormatEasing(Progress(time, from, length), easing));
                }
            }

            return text.ToString();
        }

        /// <summary>How far through a stretch the clock is, held to 0..1: clip((t-from)/length,0,1).</summary>
        private static string Progress(string time, double from, double length) =>
            $"clip(({time}{(from < 0 ? '+' : '-')}{Number(Math.Abs(from))})/{Ratio(length)},0,1)";

        /// <summary>How a layer moves while a stretch of the picture is composed; every part is null for a layer that keeps still in that respect.</summary>
        /// <param name="X">Its left edge in pixels, as an expression.</param>
        /// <param name="Width">Its width in pixels, as an expression.</param>
        /// <param name="Angle">Degrees it is turned, as an expression.</param>
        /// <param name="LargestWidth">The widest it gets, as a fraction of the frame: what it is built at, so that it is only ever scaled down.</param>
        /// <param name="Opacity">How solid it is, 0 to 1, as an expression of the frame's time (T, for geq).</param>
        /// <param name="ShiftX">What its slides add to where it is laid across, in pixels, as an expression (see WithTransitions).</param>
        /// <param name="Fades">Whether its picture fades in or out somewhere in the stretch (dissolve, wipe, blur): it changes from frame to frame.</param>
        private readonly record struct Motion(string? X, string? Y, string? Width, string? Angle, double LargestWidth, string? Opacity = null,
            string? ShiftX = null, string? ShiftY = null, bool Fades = false)
        {
            public bool Any => X is not null || Y is not null || Width is not null || Angle is not null || Opacity is not null || ShiftX is not null || ShiftY is not null;

            /// <summary>Whether the picture itself changes from frame to frame, not only where it is laid.</summary>
            public bool Reshapes => Width is not null || Angle is not null || Opacity is not null || Fades;
        }

        // ----- Fades and transitions of a clip's picture -----
        // Every clip fades on its own (Layer.FadeInSeconds and the rest; the main video's clips' from MainClip), over
        // whatever the stack has under it. How its picture comes on or goes is its style: a dissolve and a blur dissolve
        // are its transparency (fade, cheap; geq on the alpha where a fade began before the stretch), a wipe is its
        // transparency cut behind a moving soft edge (geq on the alpha, only while it wipes), a slide is where it is laid,
        // a zoom its size about its anchor (the motion's expressions, so they work with its keyframes). Each is at work
        // only inside its fade, so a clip with fades costs nothing more outside them.

        /// <summary>A fade of a picture, on the clock of the stretch being composed: from Start, Length seconds, coming on (In) or going, in a style.</summary>
        private readonly record struct PictureFade(double Start, double Length, bool In, TransitionStyle Style)
        {
            public double End => Start + Length;

            /// <summary>While it is at work, as a timeline expression: from its start (inclusive) to its end (not).</summary>
            public string Gate(string time = "t") => $"(gte({time},{Number(Start)})*lt({time},{Number(End)}))";

            /// <summary>How much of the picture is there through it, 0 to 1, as an expression of the time, in its style's curve.</summary>
            public string Shown(string time = "t")
            {
                var progress = Progress(time, Start, Length);
                return EasingCurves.Expression(In ? progress : $"(1-{progress})", TransitionStyles.Easing(Style));
            }
        }

        /// <summary>
        /// The fades of a layer's picture in this stretch: its own two, the main video's those of each of its clips. In a
        /// live graph, only those its window sees. None for a sound, an adjustment layer's (see AppendAdjustment) or a fade
        /// a crossfade has given to the clip over it (PictureFadeInOff).
        /// </summary>
        private List<PictureFade> PictureFades(Layer layer, double from)
        {
            var fades = new List<PictureFade>();
            void Add(double start, double length, bool fadeIn, TransitionStyle style)
            {
                if (length > 0.01 && InWindow(start, start + length))
                    fades.Add(new PictureFade(start - from, length, fadeIn, style));
            }

            if (layer.IsMainVideo)
            {
                foreach (var clip in GetMainClips())
                {
                    var length = clip.End - clip.Start;
                    Add(clip.Start, Math.Min(clip.FadeIn, length), true, clip.FadeInStyle);
                    var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - clip.FadeIn, 0));
                    Add(clip.End - fadeOut, fadeOut, false, clip.FadeOutStyle);
                }
            }
            else if (layer.HasTiming && !layer.IsAudio)
            {
                var (start, end) = layer.GetSpan(SequenceSeconds);
                var length = end - start;
                if (!layer.PictureFadeInOff)
                    Add(start, Math.Min(layer.FadeInSeconds, length), true, layer.FadeInStyle);
                var fadeOut = Math.Min(layer.FadeOutSeconds, Math.Max(length - (layer.PictureFadeInOff ? 0 : layer.FadeInSeconds), 0));
                if (!layer.PictureFadeOutOff)
                    Add(end - fadeOut, fadeOut, false, layer.FadeOutStyle);
            }

            return fades;
        }

        /// <summary>
        /// A layer's motion with what its slides and zooms do to it: a slide adds to where it is laid, from beyond the edge
        /// of the frame it comes from to where it is (or from there to beyond the edge it goes to); a zoom multiplies its
        /// size, about its anchor point, from nothing (with a little overshoot) to all of it. <paramref name="rect"/> is where
        /// the layer is in the frame, in the frame's pixels.
        /// </summary>
        private static Motion WithTransitions(Motion motion, List<PictureFade> fades, (int X, int Y, int Width, int Height) rect, int width, int height)
        {
            if (fades.Count == 0)
                return motion;

            var (shiftX, shiftY, zoom) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
            foreach (var fade in fades)
            {
                var (dx, dy) = TransitionStyles.Direction(fade.Style);
                if (TransitionStyles.IsSlide(fade.Style))
                {
                    // Coming on, it comes from the side it travels away from; going, it goes to the side it travels to.
                    var side = fade.In ? -1 : 1;
                    double Beyond(int toward, int at, int size, int frame) => toward > 0 ? frame - at + 2 : -(at + size) - 2;
                    if (dx != 0)
                        shiftX.Append($"+{Signed2(Beyond(dx * side, rect.X, rect.Width, width))}*(1-{fade.Shown()})*{fade.Gate()}");
                    if (dy != 0)
                        shiftY.Append($"+{Signed2(Beyond(dy * side, rect.Y, rect.Height, height))}*(1-{fade.Shown()})*{fade.Gate()}");
                }
                else if (fade.Style == TransitionStyle.Zoom)
                {
                    zoom.Append($"*(1-(1-{fade.Shown()})*{fade.Gate()})");
                }
            }

            motion = motion with { Fades = true };
            if (shiftX.Length > 0)
                motion = motion with { ShiftX = $"0{shiftX}" };
            if (shiftY.Length > 0)
                motion = motion with { ShiftY = $"0{shiftY}" };
            if (zoom.Length > 0)
            {
                // Built a little larger than it ever is, for the overshoot, and scaled for each frame from there.
                var largest = motion.Width is null ? (double)rect.Width / Math.Max(width, 1) : motion.LargestWidth;
                motion = motion with { Width = $"({motion.Width ?? Number(rect.Width)}){zoom}", LargestWidth = largest * 1.12 };
            }

            return motion;
        }

        private static string Signed2(double value) => value < 0 ? $"({Number(value)})" : Number(value);

        private static bool EndsWith(StringBuilder text, string end)
        {
            if (text.Length < end.Length)
                return false;
            for (var i = 0; i < end.Length; i++)
            {
                if (text[text.Length - end.Length + i] != end[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The fades of a picture that are its transparency (dissolves, blur dissolves, wipes, and the first moments of a
        /// zoom, so that nothing is left of it but a dot): appended to its chain, each at work only inside its fade. A blur
        /// dissolve's blur is an avgblur told its radius a dozen times a second (sendcmd), off outside the fade.
        /// </summary>
        private void AppendPictureFades(StringBuilder chain, string name, string s, List<PictureFade> fades, int width, int height)
        {
            if (fades.Count == 0)
                return;

            if (!EndsWith(chain, "format=yuva420p"))
                chain.Append(",format=yuva420p");

            // Worked out on the transparency set aside: wipes, and fades that began before this stretch (fade cannot
            // start before 0). The geq runs only inside each.
            var onAlpha = new List<(string Factor, PictureFade Fade)>();
            var feather = Math.Max(2, Math.Round(Math.Max(width, height) * 0.02));
            foreach (var fade in fades)
            {
                var (dx, dy) = TransitionStyles.Direction(fade.Style);
                if (TransitionStyles.IsWipe(fade.Style))
                {
                    // What is left of it: the part on the side the edge leaves behind (coming on) or travels towards (going).
                    var q = fade.Shown("T");
                    var keepLowSide = fade.In ? dx + dy > 0 : dx + dy < 0;
                    var (along, size) = dx != 0 ? ("X", "W") : ("Y", "H");
                    var factor = keepLowSide
                        ? $"clip((({size}+{Number(feather)})*{q}-{along})/{Number(feather)},0,1)"
                        : $"clip(({along}-({size}+{Number(feather)})*(1-{q}))/{Number(feather)}+1,0,1)";
                    onAlpha.Add((factor, fade));
                }
                else if (Dissolves(fade) is { } dissolve && dissolve.Start < 0)
                {
                    onAlpha.Add(($"({dissolve.Shown("T")})", dissolve));
                }
            }

            if (onAlpha.Count > 0)
            {
                chain.Append($",split[{name}_tf{s}][{name}_tfa_in{s}];[{name}_tfa_in{s}]alphaextract");
                foreach (var (factor, fade) in onAlpha)
                    chain.Append($",geq=lum='lum(X,Y)*{factor}':enable='between(t,{Number(fade.Start)},{Number(fade.End)})'");
                chain.Append($"[{name}_tfa{s}];[{name}_tf{s}][{name}_tfa{s}]alphamerge");
            }

            foreach (var fade in fades)
            {
                // The blur of a blur dissolve: all of it where the picture is not there yet, none where all of it is.
                if (fade.Style == TransitionStyle.BlurDissolve)
                {
                    var most = Math.Clamp((int)Math.Round(Math.Max(width, height) / 40.0), 2, 64);
                    var instance = $"fx{++_effectInstances}";
                    int RadiusAt(double t) => Math.Max((int)Math.Round(most * (1 - EasingCurves.Evaluate(fade.In ? (t - fade.Start) / fade.Length : (fade.End - t) / fade.Length, default))), 1);
                    var commands = new StringBuilder();
                    var steps = Math.Clamp((int)Math.Ceiling(fade.Length * 12), 1, 720);
                    for (var i = 0; i <= steps; i++)
                    {
                        var t = fade.Start + fade.Length * i / steps;
                        if (t <= 0.0005)
                            continue;
                        var radius = RadiusAt(t).ToString(CultureInfo.InvariantCulture);
                        commands.Append(commands.Length > 0 ? ";" : "").Append($"{Number(t)} avgblur@{instance} sizeX {radius}, avgblur@{instance} sizeY {radius}");
                    }

                    var first = RadiusAt(Math.Max(fade.Start, 0)).ToString(CultureInfo.InvariantCulture);
                    if (commands.Length > 0)
                        chain.Append($",sendcmd=c='{commands}'");
                    chain.Append($",avgblur@{instance}=sizeX={first}:sizeY={first}:enable='between(t,{Number(fade.Start)},{Number(fade.End)})'");
                }

                if (Dissolves(fade) is { Start: >= 0 } dissolve)
                    chain.Append($",fade=t={(dissolve.In ? "in" : "out")}:st={Number(dissolve.Start)}:d={Ratio(dissolve.Length)}:alpha=1:enable='between(t,{Number(dissolve.Start)},{Number(dissolve.End)})'");
            }

            // The transparency of a fade's own: a dissolve, a blur dissolve, and a zoom's first (or last) fifth.
            static PictureFade? Dissolves(PictureFade fade) => fade.Style switch
            {
                TransitionStyle.Dissolve or TransitionStyle.BlurDissolve => fade with { Style = TransitionStyle.Dissolve },
                TransitionStyle.Zoom => fade.In ? fade with { Length = fade.Length / 5, Style = TransitionStyle.Dissolve }
                    : fade with { Start = fade.End - fade.Length / 5, Length = fade.Length / 5, Style = TransitionStyle.Dissolve },
                _ => null,
            };
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
                layer.HasKeys(KeyProperty.Scale) ? layer.GetKeys(KeyProperty.Scale).Max(k => k.Value) : 0,
                layer.HasKeys(KeyProperty.Opacity) && layer.IsPicture ? KeyExpression(layer.GetKeys(KeyProperty.Opacity), start, 0.01, "T") : null);
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
                         + $"movie='{EscapeFilterPath(maskPath)}',{MaskReader(S.AlphaMasks.Contains(maskPath))},scale={width}:{height},trim=end_frame=1,loop=loop=-1:size=1[cap_mask];"
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
