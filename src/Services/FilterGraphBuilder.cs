using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

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

    /// <summary>How many channels the stream has in the file (2 when not known).</summary>
    public int Channels { get; init; } = 2;
}

/// <summary>
/// Everything a filter graph is built from, gathered at one moment: the sequence (<see cref="Timeline"/>: its tracks
/// of clips, its frame, its rate), the primary file the player plays, what is cropped of it, the sound's output
/// tracks, and the encode settings that end up in the command. The view model compiles one of these and hands
/// it to <see cref="FilterGraphBuilder"/>, which reads nothing else: not the window, not the settings, not the disk.
///
/// It is a snapshot, not a view. Its timeline is one made for it, which nothing else holds or edits, so what is
/// built from it cannot change under the builder or be changed by it.
///
/// The spine of a sequence is its canvas: a black picture the timeline's width by its height, as long as the
/// timeline (<see cref="TimelineDocument.TotalDuration"/>), ticking at its frame rate. Every clip on every track is
/// laid on that; none of them is what the others are measured against (see FilterGraphBuilder.Timeline.cs). The output
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

    /// <summary>Frames a second of the primary file (30 when not known): Live Preview plays a fast one at half its rate.</summary>
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

    /// <summary>The value for setsar, or null to leave the pixel shape alone.</summary>
    public string? SampleAspectRatio { get; init; }

    // ----- The crop -----

    public bool HasCrop { get; init; }

    /// <summary>The crop as fractions (0 to 1) of the source's width and height.</summary>
    public (double Left, double Top, double Right, double Bottom) CropFractions { get; init; }

    // ----- The primary file: what the player plays and the export opens first -----
    // Its clips are on the timeline like any other (the primary chain, see FilterGraphBuilder.Timeline.cs); what is here is
    // the file itself, and the sequence's settings that are made from it.

    /// <summary>The file FFmpeg reads as its first input and the player plays; blank before anything is loaded.</summary>
    public string LocalMediaPath { get; init; } = "";

    public bool HasSource { get; init; }

    /// <summary>The source was inspected and has no picture.</summary>
    public bool SourceHasNoVideo { get; init; }

    /// <summary>The source has sound, or could not be inspected (in which case it is taken to have some).</summary>
    public bool SourceMayHaveAudio { get; init; } = true;

    /// <summary>How long the primary file is, in seconds; 0 while that is not known.</summary>
    public double MainMediaSeconds { get; init; }

    /// <summary>
    /// Where the primary chain sits on the frame, in the frame's pixels: its left, top, width and height. The
    /// zoom and the offsets it is set with are worked out into this rectangle before the builder is asked, in
    /// one place (the view model's GetCenterRect), which the layout pane draws from too; so what is dragged
    /// there and what is encoded cannot differ. The builder scales it down for Live Preview and emits it as numbers.
    /// </summary>
    public (int X, int Y, int Width, int Height) CenterRect { get; init; }

    // ----- The Layer Engine: what is laid on the canvas -----

    /// <summary>Whether the Layer Engine composes the picture. Off, the file's own frames are filtered as they are, the whole file being the timeline.</summary>
    public bool FrameEngine { get; init; }

    /// <summary>The blurred background (made of the primary chain's frames, under everything) is switched off, leaving the black canvas.</summary>
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

    // ----- Chapters, subtitles, the list a copied picture is cut by -----

    /// <summary>The source's own chapters are carried over: only while the primary chain is the whole file as it is, their times being the file's.</summary>
    public bool ChapterMarkers { get; init; }

    /// <summary>
    /// A chapter begins at each clip of the primary chain (4.2.0's Chapters at Cut Points, now that cuts are splits): read
    /// from <see cref="ChaptersFilePath"/>, which the view model writes, once the chain is in more than one clip.
    /// </summary>
    public bool ChaptersAtCuts { get; init; }

    /// <summary>Chapters begin at the markers: switched on, and there are markers.</summary>
    public bool ChaptersAtMarkers { get; init; }

    /// <summary>
    /// Where the concat list of a Copy export is (see <see cref="FilterGraphBuilder.BuildCutsList"/>): the primary chain's
    /// clips as stretches of its file, which a picture that is only copied can be cut by. Written by the view model when
    /// the command reads it (<see cref="ExportCommand.UsesCutsFile"/>).
    /// </summary>
    public string CutsFilePath { get; init; } = "";

    /// <summary>The chapters, as an FFmpeg metadata file, when the command reads them (<see cref="ExportCommand.UsesChaptersFile"/>).</summary>
    public string ChaptersFilePath { get; init; } = "";

    /// <summary>The subtitle streams to carry over as they are, by their number ("0:s:N"): only while the primary chain is the whole file as it is, their times being the file's.</summary>
    public IReadOnlyList<int> SoftSubtitleStreams { get; init; } = [];

    /// <summary>The subtitle streams to draw into the picture.</summary>
    public IReadOnlyList<int> BurnedSubtitleStreams { get; init; } = [];

    // ----- Sound -----

    /// <summary>
    /// The audio tracks that reach the output, in order: the primary file's streams that are kept, each with its output
    /// choices and its own edits (silenced stretches, a slip), placed where the primary chain's sound is. The sound of
    /// every other clip is mixed into the first.
    /// </summary>
    public IReadOnlyList<ExportAudioTrack> AudioTracks { get; init; } = [];

    /// <summary>Whether the clips' own sound is mixed in (video clips that carry any, and sound clips): off while Live Preview plays some of the primary file's tracks alone.</summary>
    public bool MixClipSound { get; init; } = true;

    public bool MergeAudioTracks { get; init; }
    public bool NormalizeAudio { get; init; }

    /// <summary>The old one-box ducking of the first track under the second. Only set by presets from before ducking was per track.</summary>
    public bool DuckAudio { get; init; }

    /// <summary>How far a sound set to Auto-Duck is turned down while a voice speaks, in decibels.</summary>
    public double DuckAmountDb { get; init; } = -15;

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
    /// The proxies ready for the video layers' files (see ProxyCache), by the file they stand for. Only live graphs
    /// read them; an export always reads the original. A proxy starts at its first frame (time 0), whatever the original's start.
    /// </summary>
    public IReadOnlyDictionary<string, string> PreviewPaths { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The height of the main video's proxy when the player plays that (Settings: proxies for the main video too); 0 when it plays the original.</summary>
    public int MainPreviewHeight { get; init; }
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
/// <param name="UsesCutsFile">
/// The command reads the primary chain's clips from <see cref="SequenceExportState.CutsFilePath"/> (a Copy export of a main
/// video that is not its whole file: see <see cref="FilterGraphBuilder.BuildCutsList"/>), which has to be written before it runs.
/// </param>
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
public static partial class FilterGraphBuilder
{
    /// <summary>The export: the whole command line, from "ffmpeg" to the output file.</summary>
    public static ExportCommand BuildExportCommand(SequenceExportState state)
    {
        var pass = new Pass(state);
        var command = pass.BuildFfmpegCommand();
        return new ExportCommand(command, pass.UsesCutsFile, pass.UsesChaptersFile);
    }

    /// <summary>
    /// The concat list a Copy export reads its picture and sound from (<see cref="ExportCommand.UsesCutsFile"/>), in the
    /// ffconcat form: the primary file named once for each clip of the primary chain, in the order they come on the
    /// sequence, with where the clip begins and ends in the file (inpoint, outpoint: the file's own seconds). The demuxer
    /// lays the stretches end to end; a copied picture can only start on an I-frame, so each begins at the I-frame at or
    /// before its in point. For the view model to write to <see cref="SequenceExportState.CutsFilePath"/>.
    /// </summary>
    public static string BuildCutsList(SequenceExportState state)
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        var input = string.IsNullOrWhiteSpace(state.LocalMediaPath) ? "<source>" : state.LocalMediaPath;

        // Quoted for the list: the slashes forwards, and a quote closed, escaped and opened again.
        var file = input.Replace('\\', '/').Replace("'", @"'\''");
        foreach (var clip in Lower(state).MainClips.OrderBy(c => c.Start))
        {
            list.Append("file '").Append(file).Append("'\n");
            list.Append("inpoint ").Append(Number(Math.Max(clip.Offset, 0))).Append('\n');
            list.Append("outpoint ").Append(Number(clip.Offset + (clip.End - clip.Start))).Append('\n');
        }

        return list.ToString();
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

        // In the order they start, each stretch as far as the furthest of those it joins: a list may hold clips that reach
        // past each other (the sound of J and L cuts).
        foreach (var clip in clips.OrderBy(c => c.Start))
        {
            var (a, b) = (Math.Max(clip.Start, from), Math.Min(clip.End, to));
            if (b - a < 0.001)
                continue;

            if (!double.IsNaN(end) && a - end < 0.001)
            {
                end = Math.Max(end, b);
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
    private sealed partial class Pass(SequenceExportState state)
    {
    private SequenceExportState S { get; } = state;

    /// <summary>Set while the export's command is built: it reads the primary chain's clips (a Copy export), or the chapters, from a list beside it.</summary>
    public bool UsesCutsFile => _usesCutsFile;

    public bool UsesChaptersFile => _usesChaptersFile;

    private bool _usesCutsFile;
    private bool _usesChaptersFile;

    /// <summary>The state's timeline, lowered (FilterGraphBuilder.Timeline.cs): once for every graph built from its document.</summary>
    private Lowered T => _lowered ??= Lower(S);

    private Lowered? _lowered;

    // The state and its lowered timeline, under the names the code below reads them by.
    private double SequenceSeconds => T.SequenceSeconds;
    private double SourceFrameRate => S.SourceFrameRate;
    private int FrameWidth => S.Timeline.Width;
    private int FrameHeight => S.Timeline.Height;
    private int SourceWidth => S.SourceWidth;
    private int SourceHeight => S.SourceHeight;
    private string OutputWidth => S.OutputWidth;
    private string OutputHeight => S.OutputHeight;
    private bool UseVerticalResolution => S.Timeline.UseVerticalResolution;
    private bool HasCrop => S.HasCrop;
    private string LocalMediaPath => S.LocalMediaPath;
    private bool HasSource => S.HasSource;
    private double MainMediaSeconds => S.MainMediaSeconds;
    private double MainShift => T.MainShift;
    private bool IsMainSpliced => T.IsMainSpliced;
    private bool IsMainWholeSequence => T.IsMainWholeSequence;

    /// <summary>Whether the primary chain is one clip of the whole file from the sequence's start: its picture as the file has it.</summary>
    private bool IsWholePicture(IReadOnlyList<MainClip> chain) =>
        chain is [var only] && only.Start < 0.001 && only.Offset < 0.001 && (MainMediaSeconds <= 0 || Math.Abs(only.End - only.Start - MainMediaSeconds) < 0.02);

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
    private bool MergeAudioTracks => S.MergeAudioTracks;
    private bool NormalizeAudio => S.NormalizeAudio;
    private bool DuckAudio => S.DuckAudio;
    private double DuckAmountDb => S.DuckAmountDb;
    private string MixedAudioCodec => S.MixedAudioCodec;
    private string AudioBitrate => S.AudioBitrate;
    private int AudioSampleRate => S.AudioSampleRate;
    private string AudioChannels => S.AudioChannels;

    /// <summary>The rate silence and loudness-normalized sound are made at: the chosen export rate, or 48 kHz on Auto.</summary>
    private string MixRate => (S.AudioSampleRate > 0 ? S.AudioSampleRate : 48000).ToString(CultureInfo.InvariantCulture);
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

    private IReadOnlyList<MainClip> GetMainClips() => T.MainClips;

    /// <summary>Where the primary file's sound is heard (see Lowered.MainSound): the clips of <see cref="GetMainClips"/> while it is placed as the picture is.</summary>
    private IReadOnlyList<MainClip> GetMainSound() => T.MainSound;

    /// <summary>The chain's heard clips where their pictures are, with their gains (Lowered.MainHeard): what the player's own stream carries.</summary>
    private IReadOnlyList<MainClip> GetMainHeard() => T.MainHeard;

    /// <summary>Length of the output in seconds: the sequence's, which the output is moment for moment.</summary>
    private double GetOutputDuration() => SequenceSeconds;
    private List<ExportAudioTrack> GetOutputAudioTracks() => [.. S.AudioTracks];
    private List<Layer> GetLayerAudioSources() => S.MixClipSound ? [.. T.LayerAudio] : [];
    private List<Layer> GetStack() => [.. T.Stack];
    private string? GetTargetFramerate() => S.TargetFramerate;
    private string? GetSampleAspectRatio() => S.SampleAspectRatio;
    private (int Width, int Height)? GetRequestedSize() => S.RequestedSize;
    private (int Width, int Height)? GetOutputSize() => S.OutputSize;
    private (double Left, double Top, double Right, double Bottom) GetCropFractions() => S.CropFractions;
    private (int X, int Y, int Width, int Height) GetCenterRect() => S.CenterRect;
    private List<string> BuildColorFilters() => [.. S.ColorFilters];
    private string BuildVideoCodecArguments() => S.VideoCodecArguments;

    /// <summary>
    /// The last step of an export for a hardware encoder: even sides (a screen recording can be any size), then the
    /// encoder's own 4:2:0 format, converted with full chroma filtering. Left to the encoder's surface upload, a 4:4:4
    /// or BGR picture came out with 2-pixel orange and green stripes. Null for a software encoder.
    /// </summary>
    private string? EncoderFormatFilters() => S.EncoderPixelFormat is { Length: > 0 } format
        ? $"crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0,scale=flags=bicubic+accurate_rnd+full_chroma_int,format={format}"
        : null;
    private string BuildMainGate(double from, double to) => FilterGraphBuilder.BuildMainGate(T.MainClips, from, to);

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
        private string NormalizeFilters => $"loudnorm=I=-16:TP=-1.5:LRA=11,aresample={MixRate}";

        // Presses the first input down while the second (the side chain) is above the threshold.
        private const string DuckingFilter = "sidechaincompress=threshold=0.05:ratio=8:attack=20:release=400";

        private const double FadeSeconds = 1;

        public string BuildFfmpegCommand()
        {
            var input = string.IsNullOrWhiteSpace(LocalMediaPath) ? "<source>" : LocalMediaPath;
            var output = S.OutputPath;

            // What the file can hold depends on the name it is saved under, which the user may have changed by hand.
            var extension = Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0)
                extension = Container;

            // GIF and WebP are picture formats: always encoded, never with sound.
            var animated = extension is "gif" or "webp";

            var copyVideo = !animated && S.CopyVideoEncoder;

            // A copied picture cannot pass through a filter, so a primary chain that is not its whole file as it is
            // (trimmed, moved, in several clips) is cut by the concat demuxer instead, which works on packets: the file is
            // read through a list of the chain's clips (BuildCutsList), which it lays end to end, every stream with the
            // picture. Only the chain is in such an export: nothing can be laid on a copied picture, and the clips' own
            // sound is placed on the sequence's clock, which the list's stretches end to end are not (a gap is closed up).
            // (Only the picture decides: a chain that is its whole file, from the sequences start, is read as it is, whatever is
            // done to its sound or laid past its end; IsMainWholeSequence asks more than that.)
            var cutWithDemuxer = copyVideo && GetMainClips() is { Count: > 0 } mainClips && !IsWholePicture(mainClips);

            // (The silence that stands in for the sequence's own sound is there for the clips' sound to be mixed into, which such an export leaves out.)
            var tracks = animated ? [] : GetOutputAudioTracks().Where(t => !(cutWithDemuxer && t.IsSilentBase)).ToList();
            var mixAudio = MergeAudioTracks && tracks.Count > 1;

            // The voiceover from the Voiceover Studio, as one more input. It is mixed into the first audio
            // track, which therefore cannot be a copied one.
            var voiceover = !animated && IncludeVoiceover ? VoiceoverPath : null;
            // So is the sound of video layers, for the same reason.
            var layerAudio = animated || cutWithDemuxer || tracks.Count == 0 ? [] : GetLayerAudioSources();

            // Ducking needs the game and microphone tracks decoded, which a mix always does.
            var duckAudio = DuckAudio && tracks.Count >= 2 && (mixAudio || tracks.Take(2).All(t => !t.IsPassthrough));

            // How long the output is: the sequence, or the list's stretches end to end.
            var duration = cutWithDemuxer ? GetMainClips().Sum(c => c.End - c.Start) : GetOutputDuration();

            // A bare "ffmpeg" means the local copy; an ffmpeg.exe chosen in the settings is spelled out.
            var ffmpeg = S.FfmpegExecutable;
            var args = new List<string> { ffmpeg, "-hide_banner", "-y" };

            var hardware = HardwareDecoding && !copyVideo ? S.HardwareDecodeArguments : "";
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

            // A chapter at each clip of the primary chain and at each marker (as asked), read from a metadata file given
            // as the input after the video's, input 1. (A chain in one clip has no chapter of its own to give.)
            const int chaptersInput = 1;
            var chaptersFromList = ((ChaptersAtCuts && IsMainSpliced) || S.ChaptersAtMarkers) && !animated;
            if (chaptersFromList)
            {
                _usesChaptersFile = true;
                args.Add($"-i {Quote(ChaptersFilePath)}");
            }

            int? voiceoverInput = null;
            if (voiceover is not null)
            {
                // After the video's input, and the chapters file when there is one.
                voiceoverInput = chaptersFromList ? chaptersInput + 1 : 1;
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
            if (!copyVideo && !animated && EncoderFormatFilters() is { } encoderFormat)
                lastFilters.Add(encoderFormat);

            // Every label in the graph is unique: each track's steps carry its number, and the outputs come last.
            var graph = new List<string>();
            var maps = new List<string>();
            string? simpleVideoFilters = null;

            // Audio that was edited on the timeline (slipped, silenced in places), that sits on the sequence
            // somewhere other than its start, that is ducked under a voice, or that has layer sound to take in, is
            // made ready first, each track on a chain of its own; what follows then works from that instead of
            // from the input. Not when the file is read through the concat list: what the demuxer gives is the
            // chain's clips already, end to end, and a track it copies is packets, which no filter can be put in front of.
            var preparedAudio = new Dictionary<int, string>();
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
                    voices.AddRange(layerAudio.Where(l => l.IsVoice).Select(l => BuildLayerAudio(l)));

                    // The voiceover is timed against the finished video, which is the sequence.
                    if (voiceoverInput is { } voiceInput)
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
                        graph.Add($"anullsrc=r={MixRate}:cl=stereo:d={Number(Math.Max(SequenceSeconds, 0.1))}[asilent]");
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

                    preparedAudio[track.Index] = label;
                }
            }

            // The picture: the whole sequence composed on its own clock, which is the output's. A copied one is passed on
            // as it comes (the file, or the list's stretches of it).
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

            var audioOptions = BuildAudio(tracks, mixAudio, duckAudio, duration, voiceoverInput, preparedAudio, graph, maps);

            // Soft subtitles keep the file's own times, so they are carried over only while the primary chain is the whole
            // file as it is: cut, moved or spliced, the file's times are no longer the output's.
            var softSubtitles = animated || !IsMainWholeSequence ? [] : S.SoftSubtitleStreams.ToList();
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

            // The source's own chapters are at the file's times, which line up only while the primary chain is the whole file as it is.
            args.Add(chaptersFromList ? string.Create(CultureInfo.InvariantCulture, $"-map_chapters {chaptersInput}")
                : ChapterMarkers && IsMainWholeSequence && !animated ? "-map_chapters 0"
                : "-map_chapters -1");

            if (WebOptimized && extension is "mp4" or "mov" or "m4v")
                args.Add("-movflags +faststart");

            args.Add(Quote(output));
            return WithOpenClDevice(string.Join(" ", args));
        }

        /// <summary>
        /// The filter graph Live Preview runs on the picture, for mpv's lavfi-complex: the same per-frame chain the
        /// export uses (crop, the Frame &amp; Layer Engine, scaling, color, LUT), with the source's burned-in subtitles, from the
        /// video track to the screen. Empty when the export would not filter the picture at all (Copy), which
        /// is then what the preview shows too.
        ///
        /// It runs on the sequence's clock, which is the output's: what was cut out is not on the timeline, so there is
        /// nothing to skip. The fades over the whole are left out of the picture, and so are auto-captions, which the
        /// player draws by itself while Preview Subtitles is on.
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
        /// A layer's drop shadow (4.1.1-B), as the chain that makes it from the layer's own picture, and where it is
        /// laid against the layer: its outline in the shadow's colour at the shadow's opacity, moved along the light's
        /// angle. A soft shadow is first given room round it (padded, see-through) so its edge can feather outwards,
        /// and its alpha is blurred twice by a box of the softness (boxblur's power 2: a smooth, nearly Gaussian edge).
        /// </summary>
        private (string Chain, int X, int Y) ShadowOf(Layer layer)
        {
            var (shiftX, shiftY) = layer.ShadowShift;
            var soft = Placed(Math.Clamp(layer.ShadowSoftness, 0, 50));
            var room = soft + (soft & 1); // even, so a 4:2:0 picture is padded exactly
            var (y, u, v) = ShadowYuv(layer.ShadowColor);
            var chain = new StringBuilder("format=yuva420p");
            if (soft > 0)
                chain.Append(CultureInfo.InvariantCulture, $",pad=w=iw+{2 * room}:h=ih+{2 * room}:x={room}:y={room}:color=black@0");
            chain.Append(CultureInfo.InvariantCulture, $",lutyuv=y={y}:u={u}:v={v}:a=val*{Number(Math.Clamp(layer.ShadowOpacity, 0, 1))}");
            chain.Append(soft > 0 ? string.Create(CultureInfo.InvariantCulture, $",boxblur=0:0:0:0:{soft}:2") : "");
            return (chain.ToString(), Placed(shiftX) - room, Placed(shiftY) - room);
        }

        /// <summary>A shadow colour (#RRGGBB) as limited-range BT.709 Y, U and V; black (16, 128, 128) when it cannot be read.</summary>
        private static (int Y, int U, int V) ShadowYuv(string color)
        {
            var hex = color.Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return (16, 128, 128);

            var (r, g, b) = (((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
            var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            return ((int)Math.Round(16 + 219 * luma), (int)Math.Round(128 + 224 * (b - luma) / 1.8556), (int)Math.Round(128 + 224 * (r - luma) / 1.5748));
        }

        /// <summary>A shift added to a placement expression: nothing for 0, "+N" or "-N" otherwise.</summary>
        private static string Shift(int pixels) => pixels == 0 ? "" : (pixels > 0 ? "+" : "-") + Math.Abs(pixels).ToString(CultureInfo.InvariantCulture);

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
        /// <param name="ranges">The stretches of the timeline to show, joined end to end: one (what the app asks for), or several.</param>
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

            // With the engine every stretch of the sequence is composed on its own clock, from its start. The file is
            // opened once per stretch, wound forward to the first frame that stretch shows of it, which is where its
            // frames then count from.
            var shift = MainShift;
            var inputs = new List<string>();
            var graph = new List<string>();
            for (var i = 0; i < ranges.Count; i++)
            {
                var (from, to) = ranges[i];
                var (seen, heard) = (GetSeenMain(from, to), GetSeenSound(from, to));
                var needed = hasSound ? seen.Concat(heard).ToList() : seen;
                var origin = needed.Count > 0 ? Math.Max(needed.Min(p => p.Source), 0) : 0;
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

                // The main video's first track for as long as it is heard in this stretch, and silence around it.
                if (heard.Count == 0)
                {
                    graph.Add($"anullsrc=r={MixRate}:cl=stereo:d={Number(to - from)}[pa{i}]");
                    continue;
                }

                for (var j = 0; j < heard.Count; j++)
                {
                    var (start, lead) = (heard[j].Source - origin, (int)Math.Round((heard[j].From - from) * 1000));
                    graph.Add($"[{i}:a:0]atrim=start={Number(start)}:end={Number(start + heard[j].To - heard[j].From)},asetpts=PTS-STARTPTS{(lead > 0 ? $",adelay={lead}:all=1" : "")},aformat=channel_layouts=stereo[pa{i}_{j}]");
                }

                var mixed = heard.Count == 1 ? $"[pa{i}_0]" : $"{string.Concat(heard.Select((_, j) => $"[pa{i}_{j}]"))}amix=inputs={heard.Count}:normalize=0:dropout_transition=0,asetpts=N/SR/TB,";
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

    }
}
