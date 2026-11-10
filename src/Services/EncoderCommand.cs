using System.Globalization;
using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

// ----- The export's FFmpeg command: an encoder and a muxer, nothing else (Pass 5.2-Audit) -----
// FFmpeg's filter graphs are gone: the GPU compositor draws every frame (GpuExport), the sound engine renders every sound track
// (ExportSoundPlan), and HandPeg writes GIFs itself (GifEncoder). What is left for FFmpeg is what only an encoder and a muxer do:
// the frames piped in raw (NV12 as the card converted them, I420 for an encoder that wants its chroma planar, RGB for one that
// takes RGB), each sound track's render read as raw float, a file's own streams copied where they are kept as they are (a
// passthrough track from its file; soft subtitles and chapters from the stream source while the Sequence is that whole file), the
// chapters list, the encoders and the container's flags. No -vf, -af or -filter_complex is ever written; the only change FFmpeg
// makes to a frame is the pixel format an encoder needs.

/// <summary>
/// Which frames an export draws and pipes: the sequence's canvas ticks at its rate (as long as the sequence, its length as
/// FFmpeg read times, to the millisecond), and an export at another rate shows, at each of its slots, the last canvas frame
/// whose time rounded to the output's clock is not after the slot (FFmpeg's fps rule, which the frames have always followed).
/// The GPU export draws only the canvas frames shown, and pipes each as often as it is shown.
/// </summary>
/// <param name="CanvasNumerator">The canvas's rate, frames a second: <c>CanvasNumerator / CanvasDenominator</c>.</param>
/// <param name="CanvasFrames">How many frames the canvas makes: those whose time, in microseconds, is before the duration.</param>
/// <param name="OutputNumerator">The export's rate (the canvas's when no other is chosen).</param>
/// <param name="OutputFrames">How many frames the export has: the fps rule's count at the end of the canvas.</param>
public readonly record struct GpuExportTiming(long CanvasNumerator, long CanvasDenominator, long CanvasFrames, long OutputNumerator, long OutputDenominator, long OutputFrames)
{
    /// <summary>Whether the export's rate is the canvas's (every canvas frame is piped once).</summary>
    public bool SameRate => (Int128)OutputNumerator * CanvasDenominator == (Int128)CanvasNumerator * OutputDenominator;

    /// <summary>The export's rate as FFmpeg's -framerate reads it.</summary>
    public string OutputRate => string.Create(CultureInfo.InvariantCulture, $"{OutputNumerator}/{OutputDenominator}");

    /// <summary>The export's rate in frames a second.</summary>
    public double OutputFramesPerSecond => (double)OutputNumerator / Math.Max(OutputDenominator, 1);

    /// <summary>Where a canvas frame is on the sequence, in seconds.</summary>
    public double CanvasSeconds(long frame) => (double)frame * CanvasDenominator / CanvasNumerator;

    /// <summary>
    /// The latest moment a frame is sampled at (5.4A, no trailing black frame): half a canvas frame before the Sequence's last picture
    /// ends (<see cref="SequenceSpans.ProgramSeconds"/>), so the last frame never asks for a moment at or past a clip's end, whatever
    /// the millisecond rounding of the length or an end off the frame grid made of the count. No limit (infinity) when none is set.
    /// </summary>
    public double SampleLimitSeconds { get; init; } = double.PositiveInfinity;

    /// <summary>The moment a canvas frame is sampled at: its time and <paramref name="hair"/> (GpuExport.FrameHair), never past <see cref="SampleLimitSeconds"/>.</summary>
    public double SampleSeconds(long frame, double hair) => Math.Min(CanvasSeconds(frame) + hair, SampleLimitSeconds);

    /// <summary>Where an output frame is in the export, in seconds.</summary>
    public double OutputSeconds(long frame) => (double)frame * OutputDenominator / OutputNumerator;

    /// <summary>
    /// The canvas frame the export shows at output frame <paramref name="slot"/>: the last canvas frame whose time, rounded to
    /// the output's clock (to the nearest, halves away from zero), is not after the slot.
    /// </summary>
    public long CanvasFrameOf(long slot)
    {
        if (SameRate)
            return Math.Clamp(slot, 0, Math.Max(CanvasFrames - 1, 0));

        // round(n x c) <= k, with c = (canvas den x output num) / (canvas num x output den), is 2 n x den x num < num x den x (2k + 1).
        var a = (Int128)CanvasNumerator * OutputDenominator * (2 * slot + 1);
        var b = (Int128)2 * CanvasDenominator * OutputNumerator;
        var frame = (long)((a - 1) / b);
        return Math.Clamp(frame, 0, Math.Max(CanvasFrames - 1, 0));
    }

    /// <summary>The timing of a canvas of <paramref name="durationMicros"/> at a rate, exported at another.</summary>
    public static GpuExportTiming Of(long durationMicros, FrameRate canvas, (long Numerator, long Denominator)? output)
    {
        var (cn, cd) = canvas.IsValid ? ((long)canvas.Numerator, (long)canvas.Denominator) : (60L, 1L);

        // Frame n is made while n's time in microseconds (to the nearest) is before the duration.
        var estimate = (long)((Int128)durationMicros * cn / ((Int128)cd * 1_000_000));
        var frames = Math.Max(estimate - 2, 0);
        while (RoundNear((Int128)frames * cd * 1_000_000, cn) < durationMicros)
            frames++;

        var (on, od) = output is { Numerator: > 0, Denominator: > 0 } rate ? rate : (cn, cd);
        var timing = new GpuExportTiming(cn, cd, frames, on, od, frames);
        if (timing.SameRate)
            return timing;

        // At another rate the export ends at the canvas's end, rounded to its own clock: round(N x out / canvas) frames.
        return timing with { OutputFrames = (long)RoundNear((Int128)frames * cd * on, (Int128)cn * od) };
    }

    // Rounding to the nearest for what is not negative: halves away from zero.
    private static Int128 RoundNear(Int128 numerator, Int128 denominator) => (2 * numerator + denominator) / (2 * denominator);
}

/// <summary>What encodes an export's piped frames.</summary>
public enum ExportEncoder
{
    /// <summary>FFmpeg, from its standard input (<see cref="GpuExportCommand.Arguments"/>).</summary>
    Ffmpeg,

    /// <summary>HandPeg's own GIF encoder (<see cref="GifEncoder"/>): no FFmpeg at all.</summary>
    Gif,

    /// <summary>
    /// FFmpeg encodes the engine's sound only (5.4C, an Audio Only export: FLAC, WAV, MP3): no frames are piped, no compositor is
    /// started; its one input is the sound's render (<see cref="EncoderCommand.BuildAudioOnly"/>).
    /// </summary>
    Sound,
}

/// <summary>
/// An export's encode (<see cref="EncoderCommand.Build"/>): the program and its arguments (each sound track's render named by a
/// token, <see cref="EncoderCommand.SoundToken"/>, until it is made), the frames it reads from its standard input (their size and
/// pixels, rows packed), and which frames those are.
/// </summary>
/// <param name="Width">The frames piped, in pixels: the sequence's frame (or smaller, the canvas drawn into it).</param>
/// <param name="UsesChaptersFile">The command reads its chapters from <see cref="SequenceExportState.ChaptersFilePath"/>, which has to be written first.</param>
public sealed record GpuExportCommand(string Executable, IReadOnlyList<string> Arguments, int Width, int Height, GpuExportTiming Timing, bool UsesChaptersFile)
{
    /// <summary>The compositor draws the sequence's fades: always, since 5.2-Audit (nothing is left to FFmpeg).</summary>
    public bool FadesOnGpu { get; init; } = true;

    /// <summary>The compositor draws the captions (from the words beside their caption file, or the file's lines).</summary>
    public bool CaptionsOnGpu { get; init; }

    /// <summary>The pixels piped: <see cref="EncoderCommand.PipeNv12"/>, <see cref="EncoderCommand.PipeI420"/>, <see cref="EncoderCommand.PipeRgb"/>, or BGRA for a GIF.</summary>
    public string PipeFormat { get; init; } = EncoderCommand.PipeNv12;

    /// <summary>What encodes the frames.</summary>
    public ExportEncoder Encoder { get; init; } = ExportEncoder.Ffmpeg;

    /// <summary>The file written (the GIF's, for HandPeg's own encoder).</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>The sound tracks the command reads (their renders made before it runs, the copies mapped from the file).</summary>
    public IReadOnlyList<ExportSoundTrack> Sound { get; init; } = [];

    /// <summary>The sequence's frame the picture is drawn from: the frames piped are it, or it drawn smaller.</summary>
    public int CanvasWidth { get; init; }

    public int CanvasHeight { get; init; }

    /// <summary>Whether the frames are read back as NV12 (FlagReadbackNv12): NV12 piped, or I420 made from it as it is piped.</summary>
    public bool PipesNv12 => PipeFormat is EncoderCommand.PipeNv12 or EncoderCommand.PipeI420;

    /// <summary>Whether the frames are read back as 10-bit P010 (FlagReadbackP010, 5.4B: an HDR project kept HDR).</summary>
    public bool PipesP010 => PipeFormat == EncoderCommand.PipeP010;

    /// <summary>The bytes of one piped frame.</summary>
    public long FrameBytes => PipesP010 ? CompositorProtocol.P010Bytes(Width, Height) : PipesNv12 ? CompositorProtocol.Nv12Bytes(Width, Height) : (long)Width * Height * 4;

    /// <summary>The command as one line (the command box, the log, the golden tests): each argument quoted where it has to be.</summary>
    public string CommandLine => Encoder == ExportEncoder.Gif
        ? $"HandPeg GIF encoder: {Width} x {Height} at {Timing.OutputRate} (one palette, dithered) -> \"{OutputPath}\""
        : Executable + " " + string.Join(" ", Arguments.Select(EncoderCommand.QuoteArgument));
}

/// <summary>A Copy export's command: a file's own streams, cut by a list beside it where the Sequence plays it in several clips (or trimmed).</summary>
/// <param name="UsesCutsFile">The command reads the copied file's clips from <see cref="SequenceExportState.CutsFilePath"/>, which has to be written first.</param>
/// <param name="UsesChaptersFile">The command reads its chapters from <see cref="SequenceExportState.ChaptersFilePath"/>.</param>
public sealed record ExportCommand(string CommandLine, bool UsesCutsFile, bool UsesChaptersFile);

/// <summary>Builds the FFmpeg commands that encode and mux an export (see the top of this file).</summary>
public static class EncoderCommand
{
    /// <summary>The piped frames' pixels: 4:2:0 BT.709, limited range, converted on the card (62 % fewer bytes than BGRA).</summary>
    public const string PipeNv12 = "nv12";

    /// <summary>10-bit 4:2:0, each sample in the top 10 bits of 16 (5.4B: HDR passthrough).</summary>
    public const string PipeP010 = "p010le";

    /// <summary>The same, its chroma made planar as it is piped: for an encoder that takes yuv420p and not NV12 (x265, SVT-AV1, VP9...).</summary>
    public const string PipeI420 = "yuv420p";

    /// <summary>The compositor's BGRA readback, its alpha ignored (the finished picture is opaque): for an encoder that takes RGB.</summary>
    public const string PipeRgb = "bgr0";

    /// <summary>The compositor's BGRA readback for HandPeg's GIF encoder.</summary>
    public const string PipeBgra = "bgra";

    /// <summary>The largest frame the compositor renders on a side (its output textures are clamped to it), and the smallest.</summary>
    public const int LargestSide = 8192, SmallestSide = 16;

    /// <summary>The token a sound track's render is named by in a command until it is made: {sound:1} for the first.</summary>
    public const string SoundToken = "{sound:";

    // The software encoders that take NV12 as it comes, and those that take RGB.
    private static readonly HashSet<string> TakesNv12 = new(StringComparer.Ordinal) { "libx264" };
    private static readonly HashSet<string> RgbFormats = new(StringComparer.Ordinal) { "rgb24", "bgr24", "rgba", "bgra", "argb", "abgr", "gbrp", "gbrp10le", "rgb48le", "bgr0", "rgb0" };

    /// <summary>The kind of file the output's name says (its extension, or the container chosen when it has none), lower case.</summary>
    public static string Extension(SequenceExportState state)
    {
        var extension = Path.GetExtension(state.OutputPath).TrimStart('.').ToLowerInvariant();
        return extension.Length > 0 ? extension : state.Container.ToLowerInvariant();
    }

    /// <summary>Whether the output is a picture alone, never with sound (GIF, WebP).</summary>
    public static bool IsPictureOnly(SequenceExportState state) => state.IsAnimatedOutput || Extension(state) is "gif" or "webp";

    /// <summary>Whether the output is sound alone, never a picture (5.4C: FLAC, WAV, MP3): no frame is drawn or piped (<see cref="BuildAudioOnly"/>).</summary>
    public static bool IsAudioOnly(SequenceExportState state) => IsAudioOnlyExtension(Extension(state));

    /// <summary>Whether a file of this kind (its extension, lower case, no dot) holds sound alone: "flac", "wav", "mp3".</summary>
    public static bool IsAudioOnlyExtension(string extension) => extension is "flac" or "wav" or "mp3";

    /// <summary>The encoder an audio-only file of this kind is written with: FLAC, 16-bit PCM in a WAV, LAME's MP3.</summary>
    public static string AudioOnlyCodec(string extension) => extension switch
    {
        "flac" => "flac",
        "wav" => "pcm_s16le",
        _ => "libmp3lame",
    };

    /// <summary>
    /// Why an audio-only export of the state cannot be made (5.4C); null when it can. No frame is rendered, so the frame's size and the
    /// compositor's tables do not matter: an empty Sequence, or one with nothing to hear.
    /// </summary>
    public static string? WhyNotAudio(SequenceExportState state, ExportSoundPlan sound) =>
        TimelineLowering.Lowering(state).SequenceSeconds <= 0 ? "the sequence is empty"
        : sound.Tracks.Count == 0 ? "the Sequence has nothing to hear"
        : null;

    /// <summary>
    /// Why an export of the state cannot be made, in plain words; null when it can (a Copy export is <see cref="BuildCopy"/>'s).
    /// What is left: a frame the compositor cannot render, a rate the frames cannot follow, nothing on the sequence.
    /// </summary>
    public static string? WhyNot(SequenceExportState state)
    {
        var (width, height) = (state.Timeline.Width, state.Timeline.Height);
        if (width is < SmallestSide or > LargestSide || height is < SmallestSide or > LargestSide)
            return string.Create(CultureInfo.InvariantCulture, $"the frame is {width} x {height} (the compositor renders {SmallestSide} to {LargestSide} pixels a side)");
        if (Timing(state) is not { } timing)
            return $"the export's frame rate ({state.TargetFramerate}) is not one the export can follow";
        return timing.OutputFrames <= 0 ? "the sequence is empty" : null;
    }

    /// <summary>The frames an export of the state has: its canvas (the sequence's length, to the millisecond) at the export's rate.</summary>
    public static GpuExportTiming? Timing(SequenceExportState state)
    {
        var output = ParseFrameRate(state.TargetFramerate);
        if (state.TargetFramerate is { Length: > 0 } && output is null)
            return null;

        var program = Math.Max(TimelineLowering.Lowering(state).SequenceSeconds, 0);
        var seconds = Math.Round(program, 3, MidpointRounding.AwayFromZero);
        var timing = GpuExportTiming.Of((long)Math.Round(seconds * 1_000_000), state.Timeline.FrameRate, output);
        return timing with { SampleLimitSeconds = SampleLimitOf(program, state.Timeline.FrameRate) };
    }

    /// <summary>
    /// The latest moment a Sequence that ends at <paramref name="programSeconds"/> is sampled at (5.4A): half a canvas frame before
    /// its end (<see cref="GpuExportTiming.SampleLimitSeconds"/>); never before its start.
    /// </summary>
    public static double SampleLimitOf(double programSeconds, FrameRate canvas) => Math.Max(programSeconds - 0.5 * canvas.FrameDuration, 0);

    /// <summary>
    /// The export's encode: the frames piped (the format its encoder takes), each sound track (its render's token, or a file's
    /// stream copied), the chapters (the list beside it, or the stream source's own while the whole Sequence is one clip of that
    /// file as it is), soft subtitles carried over (likewise), the video encoder and its options, colour tagged BT.709. A GIF is
    /// HandPeg's own. An export that keeps the project's HDR (5.4B, <see cref="SequenceExportState.HdrTransfer"/>) pipes 10-bit P010,
    /// tagged BT.2020nc / BT.2020 / its transfer in and out, and its encoder is handed 10 bits (p010le, or yuv420p10le for a software
    /// one); every SDR command is as it was.
    /// </summary>
    public static GpuExportCommand Build(SequenceExportState state, ExportSoundPlan sound, GpuExportTiming timing, bool captionsOnGpu)
    {
        var (width, height) = (state.Timeline.Width, state.Timeline.Height);
        var extension = Extension(state);
        var output = state.OutputPath;
        if (extension == "gif")
        {
            return new GpuExportCommand("", [], width, height, timing, UsesChaptersFile: false)
            {
                Encoder = ExportEncoder.Gif, PipeFormat = PipeBgra, OutputPath = output, CaptionsOnGpu = captionsOnGpu, CanvasWidth = width, CanvasHeight = height,
            };
        }

        var pictureOnly = IsPictureOnly(state);
        var (codec, pixelFormat) = VideoCodec(state, extension);
        var hdr = HdrTail(state, extension, codec, pixelFormat);
        var pipe = hdr is null ? PipeFor(codec, pixelFormat) : PipeP010;
        var colourTags = hdr?.Tags ?? ColourTags;
        var args = new List<string> { "-hide_banner", "-y" };

        // Input 0: the picture, piped.
        args.AddRange(["-f", "rawvideo", "-pix_fmt", pipe, "-video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"), "-framerate", timing.OutputRate]);
        if (pipe != PipeRgb)
            args.AddRange(colourTags);
        args.AddRange(["-i", "pipe:0"]);
        var inputs = 1;

        // The sound tracks' renders, one input each, raw float.
        var tracks = pictureOnly ? [] : sound.Tracks;
        var renderInput = new Dictionary<int, int>();
        for (var n = 0; n < tracks.Count; n++)
        {
            if (tracks[n].IsCopy)
                continue;
            args.AddRange(["-f", "f32le", "-ar", sound.SampleRate.ToString(CultureInfo.InvariantCulture), "-ch_layout", tracks[n].Layout,
                "-i", string.Create(CultureInfo.InvariantCulture, $"{SoundToken}{n + 1}}}")]);
            renderInput[n] = inputs++;
        }

        // The files themselves, for what is kept as it is: each copied track's file, and the stream source's (its soft subtitles,
        // its own chapters) while the whole Sequence is one clip of it as it is, their times being its own. One input a file.
        var streamSource = Clean(state.StreamSourcePath);
        var whole = streamSource.Length > 0 && SequenceSpans.WholeFiles(state.Timeline).Contains(streamSource);
        var softSubtitles = pictureOnly || !whole ? [] : state.SoftSubtitleStreams.ToList();
        var chaptersFromList = ((state.ChaptersAtCuts && !whole) || state.ChaptersAtMarkers) && !pictureOnly;
        var sourceChapters = !chaptersFromList && state.ChapterMarkers && whole && !pictureOnly;
        var fileInputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int InputOf(string path)
        {
            if (!fileInputs.TryGetValue(path, out var input))
            {
                args.AddRange(["-i", path]);
                fileInputs[path] = input = inputs++;
            }

            return input;
        }

        var copied = new Dictionary<int, int>();
        for (var n = 0; n < tracks.Count; n++)
        {
            if (tracks[n].IsCopy)
                copied[n] = InputOf(Clean(tracks[n].CopyPath));
        }

        var source = softSubtitles.Count > 0 || sourceChapters ? InputOf(streamSource) : -1;

        var chapters = -1;
        if (chaptersFromList)
        {
            args.AddRange(["-i", state.ChaptersFilePath]);
            chapters = inputs++;
        }

        // The maps: the picture, each sound track in order, the subtitles.
        args.AddRange(["-map", "0:v:0"]);
        for (var n = 0; n < tracks.Count; n++)
        {
            args.AddRange(["-map", tracks[n].IsCopy
                ? string.Create(CultureInfo.InvariantCulture, $"{copied[n]}:a:{tracks[n].CopyStream}?")
                : string.Create(CultureInfo.InvariantCulture, $"{renderInput[n]}:a:0")]);
        }

        foreach (var subtitle in softSubtitles)
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{source}:s:{subtitle}?")]);

        // The encoders.
        args.AddRange(extension switch
        {
            "webp" => ["-c:v", "libwebp", "-q:v", "75", "-loop", "0"],
            _ => FfmpegRunner.SplitArguments(state.VideoCodecArguments),
        });
        if (hdr?.PixelFormat is { } tenBits)
            args.AddRange(["-pix_fmt", tenBits]);
        if (pipe != PipeRgb && extension != "webp")
            args.AddRange(colourTags);
        if (SampleAspect(state, width, height) is { } aspect)
            args.AddRange(["-aspect", aspect]);

        for (var n = 0; n < tracks.Count; n++)
        {
            var track = tracks[n];
            var k = n.ToString(CultureInfo.InvariantCulture);
            if (track.IsCopy)
            {
                args.AddRange([$"-c:a:{k}", "copy"]);
            }
            else
            {
                args.AddRange([$"-c:a:{k}", track.Codec]);
                if (!AudioTrack.IsLossless(track.Codec) && track.Bitrate.Length > 0)
                    args.AddRange([$"-b:a:{k}", track.Bitrate]);
            }

            if (!string.IsNullOrWhiteSpace(track.Title))
                args.AddRange([$"-metadata:s:a:{k}", $"title={track.Title.Trim()}"]);
        }

        // A file's sound copied as it is runs to the end of its stream, which can be past the last picture (a container whose sound is
        // padded past its video): the output ends with the picture (5.4A; -t is the muxer's, no filter is written).
        if (tracks.Any(t => t.IsCopy))
            args.AddRange(["-t", timing.OutputSeconds(timing.OutputFrames).ToString("0.######", CultureInfo.InvariantCulture)]);
        if (pictureOnly)
            args.Add("-an");
        if (softSubtitles.Count > 0)
            args.AddRange(["-c:s", extension switch { "mp4" or "mov" or "m4v" => "mov_text", "webm" => "webvtt", _ => "copy" }]);
        args.AddRange(["-map_chapters", (chapters >= 0 ? chapters : sourceChapters ? source : -1).ToString(CultureInfo.InvariantCulture)]);
        if (state.WebOptimized && extension is "mp4" or "mov" or "m4v")
            args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);

        return new GpuExportCommand(state.FfmpegExecutable.Trim('"'), args, width, height, timing, chapters >= 0)
        {
            PipeFormat = pipe, OutputPath = output, Sound = tracks, CaptionsOnGpu = captionsOnGpu, CanvasWidth = width, CanvasHeight = height,
        };
    }

    /// <summary>
    /// An Audio Only export's encode (5.4C): the engine's one rendered sound track (<see cref="ExportSoundPlan.For(SequenceExportState, bool, bool)"/>)
    /// read as raw float, encoded as the file's kind wants it: FLAC in 16 bits, 16-bit PCM in a WAV, MP3 at the track's bitrate. No
    /// picture: nothing piped (no pipe:0, no rawvideo), no colour tags, no chapters, no -movflags; never a -vf, -af or -filter_complex.
    /// </summary>
    public static GpuExportCommand BuildAudioOnly(SequenceExportState state, ExportSoundPlan sound, GpuExportTiming timing)
    {
        if (sound.Tracks is not [var track] || track.IsCopy)
            throw new InvalidOperationException("an audio-only export encodes exactly one rendered sound track");

        var extension = Extension(state);
        var output = state.OutputPath;
        var args = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "f32le", "-ar", sound.SampleRate.ToString(CultureInfo.InvariantCulture), "-ch_layout", track.Layout,
            "-i", string.Create(CultureInfo.InvariantCulture, $"{SoundToken}1}}"),
            "-map", "0:a:0", "-map_chapters", "-1", "-c:a", track.Codec,
        };

        // FLAC is asked for 16 bits outright (never left to FFmpeg's choice from float); PCM's codec names its 16; MP3 keeps its bitrate.
        if (extension == "flac")
            args.AddRange(["-sample_fmt", "s16"]);
        if (!AudioTrack.IsLossless(track.Codec) && track.Bitrate.Length > 0)
            args.AddRange(["-b:a", track.Bitrate]);
        args.Add(output);

        return new GpuExportCommand(state.FfmpegExecutable.Trim('"'), args, 0, 0, timing, UsesChaptersFile: false)
        {
            Encoder = ExportEncoder.Sound, OutputPath = output, Sound = [track],
        };
    }

    /// <summary>
    /// Why a Copy export of a Sequence cannot be made, in words for the status bar; null when it can (5.2-Arm64AndNpu, the user's
    /// call: such an export is refused before it starts, with this reason, rather than warned of and handed to FFmpeg to fail). The
    /// two ways (<see cref="SequenceSpans.CopyCutOf"/>): no clip shows a file's picture, so there is nothing to copy; or every clip
    /// of the file it would copy plays at another speed, which a stream copy cannot cut (its pieces are none). A file cut into
    /// clips at speed 1 is copied through its cuts list, and is no reason.
    /// </summary>
    public static string? WhyCopyCannotCut(TimelineDocument document) => SequenceSpans.CopyCutOf(document) switch
    {
        null => "Copy cannot export this Sequence: no clip on the timeline shows a file's picture, so there is nothing to copy. "
                + "Choose a video codec on the Export tab to export it.",
        { Pieces.Count: 0 } copy => $"Copy cannot export this Sequence: the clips of {Path.GetFileName(copy.Path)} play at another speed, which a "
                                    + "stream copy cannot cut without encoding. Choose a video codec on the Export tab to export it.",
        _ => null,
    };

    /// <summary>
    /// A Copy export (5.2-AuditB): the picture (and sound) of the file the Sequence plays most (<see cref="SequenceSpans.CopyCutOf"/>)
    /// copied as it is, packet for packet; that file in several clips (or trimmed, or moved) read through the concat list of its
    /// clips (<see cref="BuildCutsList"/>), which lays them end to end. Only that file's own streams are in it: nothing can be laid
    /// on a copied picture, and every other clip is left out (<see cref="CopyCut.LeftOut"/>, which the view model warns of). Its
    /// sound: the streams its clips play on the Sub-Lanes, by stream place, as each place's choice says (no choice: copied); a file
    /// without Sub-Lanes, the stream its first clip plays. A Sequence with no clip of a file's picture copies "&lt;source&gt;", which
    /// FFmpeg cannot open.
    /// </summary>
    public static ExportCommand BuildCopy(SequenceExportState state)
    {
        // (A Sequence a copy cannot cut, WhyCopyCannotCut, is refused before this is run; the command for it is still written,
        // as the Command Preview shows it.)
        var copy = SequenceSpans.CopyCutOf(state.Timeline);
        var input = copy is { Path.Length: > 0 } ? copy.Path : "<source>";
        var cut = copy is { IsCut: true };
        var args = new List<string> { state.FfmpegExecutable, "-hide_banner", "-y" };
        args.Add(cut ? $"-f concat -safe 0 -i {Quote(state.CutsFilePath)}" : $"-i {Quote(input)}");

        var chaptersFromList = (state.ChaptersAtCuts && copy is { Pieces.Count: > 1 }) || state.ChaptersAtMarkers;
        if (chaptersFromList)
            args.Add($"-i {Quote(state.ChaptersFilePath)}");

        args.Add("-map 0:v:0");
        var tracks = copy is null ? [] : CopiedStreams(state, copy);
        for (var k = 0; k < tracks.Count; k++)
        {
            var (stream, title, _, _) = tracks[k];
            args.Add(string.Create(CultureInfo.InvariantCulture, $"-map 0:a:{stream}?"));
            if (!string.IsNullOrWhiteSpace(title))
                args.Add($"-metadata:s:a:{k} title=\"{title.Trim().Replace('"', '\'')}\"");
        }

        // The stream source's own subtitles and chapters, while the copy is that whole file as the whole Sequence.
        var streamSource = Clean(state.StreamSourcePath);
        var ownFile = !cut && copy is not null && streamSource.Length > 0 && string.Equals(copy.Path, streamSource, StringComparison.OrdinalIgnoreCase)
                      && SequenceSpans.WholeFiles(state.Timeline).Contains(streamSource);
        var soft = ownFile ? state.SoftSubtitleStreams : [];
        foreach (var subtitle in soft)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"-map 0:s:{subtitle}?"));

        args.Add("-c:v copy");
        for (var k = 0; k < tracks.Count; k++)
        {
            var (_, _, codec, bitrate) = tracks[k];
            args.Add(codec == "copy" || AudioTrack.IsLossless(codec)
                ? string.Create(CultureInfo.InvariantCulture, $"-c:a:{k} {codec}")
                : string.Create(CultureInfo.InvariantCulture, $"-c:a:{k} {codec} -b:a:{k} {bitrate}"));
        }

        if (soft.Count > 0)
            args.Add("-c:s copy");

        // The whole file copied (no cuts list to end it) while its sound runs on past its picture (5.4A: a padded container, its clip
        // ending with the picture, TimelineClip.PictureDuration): the output ends with the picture, as a GPU export's does (-t is the
        // muxer's; nothing is filtered).
        if (!cut && tracks.Count > 0 && copy is { Pieces: [var only] } && state.Timeline.FindClip(only.ClipId) is { PictureDuration: > 0 } held
            && held.SourceDuration - only.Duration > 0.001)
            args.Add(string.Create(CultureInfo.InvariantCulture, $"-t {only.Duration:0.######}"));
        args.Add(chaptersFromList ? "-map_chapters 1" : state.ChapterMarkers && ownFile ? "-map_chapters 0" : "-map_chapters -1");
        if (state.WebOptimized && Extension(state) is "mp4" or "mov" or "m4v")
            args.Add("-movflags +faststart");
        args.Add(Quote(state.OutputPath));
        return new ExportCommand(string.Join(" ", args), cut, chaptersFromList);
    }

    // The copied file's sound tracks in a Copy export, by stream place: the stream of the file that a Sub-Lane of each place plays
    // (the place's choice: left out, copied, or encoded; no choice, copied), titled by the choice or the lane; a file played on no
    // Sub-Lane, the stream its first clip plays (stream place 0's choice).
    private static List<(int Stream, string Title, string Codec, string Bitrate)> CopiedStreams(SequenceExportState state, CopyCut copy)
    {
        var choices = new Dictionary<int, AudioOutputChoice>();
        foreach (var output in state.AudioOutputs)
            choices.TryAdd(output.Ordinal, output);

        var places = new SortedDictionary<int, (int Stream, string Name)>();
        foreach (var track in state.Timeline.Tracks)
        {
            if (track.StreamOrdinal is not { } ordinal || ordinal < 0 || places.ContainsKey(ordinal))
                continue;
            foreach (var clip in track.Clips)
            {
                if (clip.AudioStreamIndex >= 0 && clip.SourcePath is { } path && string.Equals(Clean(path), copy.Path, StringComparison.OrdinalIgnoreCase))
                {
                    places[ordinal] = (clip.AudioStreamIndex, track.Name.Trim());
                    break;
                }
            }
        }

        if (places.Count == 0 && copy.Pieces.Count > 0 && state.Timeline.FindClip(copy.Pieces[0].ClipId) is { AudioStreamIndex: >= 0 } first)
            places[0] = (first.AudioStreamIndex, "");

        var streams = new List<(int Stream, string Title, string Codec, string Bitrate)>();
        foreach (var (ordinal, (stream, name)) in places)
        {
            var choice = choices.GetValueOrDefault(ordinal);
            if (choice is { Dropped: true })
                continue;
            streams.Add(choice is { Passthrough: false }
                ? (stream, choice.Title, choice.Codec.Length > 0 ? choice.Codec : "copy", choice.Bitrate)
                : (stream, choice?.Title ?? name, "copy", choice?.Bitrate ?? ""));
        }

        return streams;
    }

    /// <summary>
    /// The concat demuxer's list a Copy export reads its file's clips through (<see cref="SequenceSpans.CopyCutOf"/>): each clip a
    /// stretch of the file (its inpoint and outpoint, on the file's own clock: the clips' media time, which counts from the first
    /// video frame, moved to the container's), in the order the sequence plays them.
    /// </summary>
    public static string BuildCutsList(SequenceExportState state)
    {
        var list = new System.Text.StringBuilder("ffconcat version 1.0\n");
        if (SequenceSpans.CopyCutOf(state.Timeline) is not { } copy)
            return list.ToString();

        // Quoted for the list: the slashes forwards, and a quote closed, escaped and opened again.
        var file = copy.Path.Replace('\\', '/').Replace("'", @"'\''");
        var shift = ContainerShift(state, copy.Path);
        foreach (var piece in copy.Pieces)
        {
            list.Append("file '").Append(file).Append("'\n");
            list.Append("inpoint ").Append(Number(Math.Max(piece.MediaOffset + shift, 0))).Append('\n');
            list.Append("outpoint ").Append(Number(piece.MediaOffset + shift + (piece.End - piece.Start))).Append('\n');
        }

        return list.ToString();
    }

    // How far a file's picture starts after its container starts (an MPEG-TS or Matroska recording's: the clips' media time counts
    // from the first video frame, the concat list's from the container's start): the state's, else what MediaProbe learnt this
    // session; 0 while either is not known.
    private static double ContainerShift(SequenceExportState state, string path)
    {
        var video = state.MediaStarts.TryGetValue(path, out var start) ? start : MediaProbe.KnownVideoStart(path);
        var container = state.SeekIndexes.TryGetValue(path, out var index) ? index.ContainerStart : MediaProbe.KnownContainerStart(path);
        return video is { } v && container is { } c && double.IsFinite(v - c) ? v - c : 0;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A command's arguments with each sound token replaced by its track's render (in the command's track order).</summary>
    public static List<string> WithRenders(IEnumerable<string> arguments, IReadOnlyList<ExportSoundTrack> rendered)
    {
        var result = new List<string>();
        foreach (var argument in arguments)
        {
            var text = argument;
            for (var n = 0; n < rendered.Count; n++)
            {
                var token = string.Create(CultureInfo.InvariantCulture, $"{SoundToken}{n + 1}}}");
                if (text.Contains(token, StringComparison.Ordinal))
                    text = text.Replace(token, rendered[n].RenderPath ?? throw new InvalidOperationException($"sound track {n + 1} was not rendered"), StringComparison.Ordinal);
            }

            if (text.Contains(SoundToken, StringComparison.Ordinal))
                throw new InvalidOperationException($"the command names a sound track it does not have ({text})");
            result.Add(text);
        }

        return result;
    }

    /// <summary>A frame rate as the view model writes it ("30", "59.94", "30000/1001") as a fraction (59.94 is 2997/50).</summary>
    public static (long Numerator, long Denominator)? ParseFrameRate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        var slash = value.IndexOf('/');
        if (slash > 0)
        {
            return long.TryParse(value.AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                   && long.TryParse(value.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0
                ? Reduced(n, d)
                : null;
        }

        var point = value.IndexOf('.');
        var decimals = point < 0 ? 0 : value.Length - point - 1;
        if (decimals > 6 || !long.TryParse(value.Replace(".", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var digits) || digits <= 0)
            return null;
        var denominator = 1L;
        for (var i = 0; i < decimals; i++)
            denominator *= 10;
        return Reduced(digits, denominator);

        static (long, long) Reduced(long n, long d)
        {
            var (a, b) = (n, d);
            while (b != 0)
                (a, b) = (b, a % b);
            return (n / a, d / a);
        }
    }

    /// <summary>An argument as the command box shows it: quoted where it holds a space or a quote, or is empty.</summary>
    public static string QuoteArgument(string a) =>
        a.Length == 0 || a.Any(c => c is ' ' or '"' or '\t') ? $"\"{a.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : a;

    // ----- What the frames are piped as -----

    // BT.709, limited range: what the card converts the picture to (and what an RGB-to-YUV conversion is told).
    private static readonly string[] ColourTags = ["-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];

    /// <summary>
    /// HDR kept (5.4B): BT.2020 non-constant luminance, BT.2020 primaries, limited range, and the project's transfer ("smpte2084" PQ or
    /// "arib-std-b67" HLG): what the card's P010 readback is (FlagReadbackP010), on the pipe and on the encoded stream.
    /// </summary>
    public static string[] HdrColourTags(string transfer) => ["-color_range", "tv", "-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", transfer];

    /// <summary>
    /// The HDR part of an encode, or null for SDR: the tags, and the 10-bit pixel format the encoder is handed when its options name none
    /// (p010le for a hardware encoder, yuv420p10le for a software one: FFmpeg turns the piped P010 planar without a filter). Only for a
    /// state that keeps HDR (<see cref="SequenceExportState.HdrTransfer"/>), never an animated output.
    /// </summary>
    private static (string[] Tags, string? PixelFormat)? HdrTail(SequenceExportState state, string extension, string codec, string? pixelFormat)
    {
        if (state.HdrTransfer is not { Length: > 0 } transfer || extension is "gif" or "webp" || state.CopyVideoEncoder)
            return null;

        var named = FfmpegRunner.SplitArguments(state.VideoCodecArguments).Contains("-pix_fmt");
        var tenBits = named ? null
            : pixelFormat is { } format && format.Contains("10", StringComparison.Ordinal) ? format
            : codec.Contains('_', StringComparison.Ordinal) ? PipeP010 : "yuv420p10le";
        return (HdrColourTags(transfer), tenBits);
    }

    // The video encoder the options name, and the pixel format they ask of it (the extra options', a hardware encoder's, 10-bit).
    private static (string Codec, string? PixelFormat) VideoCodec(SequenceExportState state, string extension)
    {
        if (extension == "webp")
            return ("libwebp", PipeI420);
        var arguments = FfmpegRunner.SplitArguments(state.VideoCodecArguments);
        var codec = "";
        string? format = null;
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] is "-c:v" or "-vcodec" or "-codec:v")
                codec = arguments[i + 1];
            else if (arguments[i] == "-pix_fmt")
                format = arguments[i + 1];
        }

        return (codec, format ?? state.EncoderPixelFormat);
    }

    // The pixels to pipe for an encoder: what it takes as it comes, so FFmpeg converts nothing where it can (NV12 for x264 and the
    // hardware encoders, I420 for the rest of the 4:2:0 ones, RGB for an RGB format), else I420 for FFmpeg to convert from.
    private static string PipeFor(string codec, string? pixelFormat)
    {
        if (pixelFormat is { } format)
        {
            if (RgbFormats.Contains(format))
                return PipeRgb;
            if (format == PipeNv12)
                return PipeNv12;
            if (format == PipeI420)
                return PipeI420;
            return format.StartsWith("p0", StringComparison.Ordinal) || format.StartsWith("nv", StringComparison.Ordinal) ? PipeNv12 : PipeI420;
        }

        return TakesNv12.Contains(codec) || codec.EndsWith("_nvenc", StringComparison.Ordinal) || codec.EndsWith("_qsv", StringComparison.Ordinal)
               || codec.EndsWith("_amf", StringComparison.Ordinal) || codec.EndsWith("_mf", StringComparison.Ordinal)
            ? PipeNv12
            : PipeI420;
    }

    // The display shape the pixel shape asks for (-aspect), when the pixels are not square.
    private static string? SampleAspect(SequenceExportState state, int width, int height)
    {
        if (state.SampleAspectRatio is not { Length: > 0 } sar)
            return null;
        var parts = sar.Split(':', '/');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                              || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || n <= 0 || d <= 0 || Math.Abs(n / d - 1) < 1e-6)
            return null;
        return string.Create(CultureInfo.InvariantCulture, $"{width * n / (height * d):0.######}");
    }

    private static string Clean(string path) => path.Trim().Trim('"');

    private static string Quote(string value) => $"\"{value}\"";
}
