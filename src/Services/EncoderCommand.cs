using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

// ----- Export FFmpeg command: encode and mux only -----
// The compositor renders frames (GpuExport), the audio engine renders tracks (ExportSoundPlan), GifEncoder writes GIFs. FFmpeg receives
// raw piped frames (GPU NV12, I420 for planar-chroma encoders, RGB for RGB encoders), raw float audio renders, stream copies (passthrough
// tracks; soft subtitles and chapters while the Sequence is the whole source), the chapters list, encoders and container flags.
// Never -vf, -af or -filter_complex; FFmpeg only performs the pixel-format conversion an encoder requires.

/// <summary>
/// Export frame timing: the canvas ticks at its rate over the Sequence length (ms precision); at a different output rate each slot shows
/// the last canvas frame whose time, rounded to the output clock, is not after the slot (FFmpeg fps rule). Only shown canvas frames are
/// rendered, each piped as many times as shown.
/// </summary>
/// <param name="CanvasNumerator">Canvas rate numerator (fps = <c>CanvasNumerator / CanvasDenominator</c>).</param>
/// <param name="CanvasFrames">Canvas frame count: frames whose microsecond time precedes the duration.</param>
/// <param name="OutputNumerator">Output rate numerator (canvas rate when unspecified).</param>
/// <param name="OutputFrames">Output frame count per the fps rule at the canvas end.</param>
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
    /// Latest sample time: half a canvas frame before the last picture ends (<see cref="SequenceSpans.ProgramSeconds"/>), preventing a
    /// trailing black frame from ms rounding or off-grid ends. Infinity when unset.
    /// </summary>
    public double SampleLimitSeconds { get; init; } = double.PositiveInfinity;

    /// <summary>Sample time for a canvas frame: frame time + <paramref name="hair"/> (GpuExport.FrameHair), capped at <see cref="SampleLimitSeconds"/>.</summary>
    public double SampleSeconds(long frame, double hair) => Math.Min(CanvasSeconds(frame) + hair, SampleLimitSeconds);

    /// <summary>Where an output frame is in the export, in seconds.</summary>
    public double OutputSeconds(long frame) => (double)frame * OutputDenominator / OutputNumerator;

    /// <summary>
    /// Canvas frame shown at output <paramref name="slot"/>: last canvas frame whose time rounded to the output clock (half away from zero)
    /// is not after the slot.
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
    /// Audio Only export (FLAC, WAV, MP3): no piped frames or compositor; the single input is the audio render (<see cref="EncoderCommand.BuildAudioOnly"/>).
    /// </summary>
    Sound,
}

/// <summary>
/// Export encode (<see cref="EncoderCommand.Build"/>): executable and arguments (audio renders as <see cref="EncoderCommand.SoundToken"/>
/// placeholders), stdin frame format (size, pixel format, packed rows) and frame timing.
/// </summary>
/// <param name="Width">Piped frame width in pixels (Sequence frame, or smaller canvas).</param>
/// <param name="UsesChaptersFile">Reads chapters from <see cref="SequenceExportState.ChaptersFilePath"/>, which must be written first.</param>
public sealed record GpuExportCommand(string Executable, IReadOnlyList<string> Arguments, int Width, int Height, GpuExportTiming Timing, bool UsesChaptersFile)
{
    /// <summary>Always true: the compositor renders Sequence fades.</summary>
    public bool FadesOnGpu { get; init; } = true;

    /// <summary>The compositor draws the captions (from the words beside their caption file, or the file's lines).</summary>
    public bool CaptionsOnGpu { get; init; }

    /// <summary>Piped pixel format: <see cref="EncoderCommand.PipeNv12"/>, <see cref="EncoderCommand.PipeI420"/>, <see cref="EncoderCommand.PipeRgb"/>, or BGRA for GIF.</summary>
    public string PipeFormat { get; init; } = EncoderCommand.PipeNv12;

    /// <summary>What encodes the frames.</summary>
    public ExportEncoder Encoder { get; init; } = ExportEncoder.Ffmpeg;

    /// <summary>The file written (the GIF's, for HandPeg's own encoder).</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>Audio tracks read by the command (renders produced beforehand; copies mapped from files).</summary>
    public IReadOnlyList<ExportSoundTrack> Sound { get; init; } = [];

    /// <summary>The sequence's frame the picture is drawn from: the frames piped are it, or it drawn smaller.</summary>
    public int CanvasWidth { get; init; }

    public int CanvasHeight { get; init; }

    /// <summary>True for NV12 readback (FlagReadbackNv12), piped as NV12 or converted to I420.</summary>
    public bool PipesNv12 => PipeFormat is EncoderCommand.PipeNv12 or EncoderCommand.PipeI420;

    /// <summary>True for 10-bit P010 readback (FlagReadbackP010) when exporting HDR.</summary>
    public bool PipesP010 => PipeFormat == EncoderCommand.PipeP010;

    /// <summary>The bytes of one piped frame.</summary>
    public long FrameBytes => PipesP010 ? CompositorProtocol.P010Bytes(Width, Height) : PipesNv12 ? CompositorProtocol.Nv12Bytes(Width, Height) : (long)Width * Height * 4;

    /// <summary>Single-line command with required quoting (command box, log, goldens).</summary>
    public string CommandLine => Encoder == ExportEncoder.Gif
        ? $"HandPeg GIF encoder: {Width} x {Height} at {Timing.OutputRate} (one palette, dithered) -> \"{OutputPath}\""
        : Executable + " " + string.Join(" ", Arguments.Select(EncoderCommand.QuoteArgument));
}

/// <summary>Copy export command: stream-copies one file, via a concat cuts list when the Sequence uses several clips or trims.</summary>
/// <param name="UsesCutsFile">Reads clip ranges from <see cref="SequenceExportState.CutsFilePath"/>, which must be written first.</param>
/// <param name="UsesChaptersFile">Reads chapters from <see cref="SequenceExportState.ChaptersFilePath"/>.</param>
public sealed record ExportCommand(string CommandLine, bool UsesCutsFile, bool UsesChaptersFile);

/// <summary>Builds the FFmpeg commands that encode and mux an export (see the top of this file).</summary>
public static class EncoderCommand
{
    /// <summary>NV12 4:2:0 BT.709 limited range, GPU-converted (62 % fewer bytes than BGRA).</summary>
    public const string PipeNv12 = "nv12";

    /// <summary>P010: 10-bit 4:2:0, MSB-aligned in 16 bits (HDR).</summary>
    public const string PipeP010 = "p010le";

    /// <summary>I420: NV12 with chroma de-interleaved while piping, for yuv420p-only encoders (x265, SVT-AV1, VP9, ...).</summary>
    public const string PipeI420 = "yuv420p";

    /// <summary>BGRA readback (alpha ignored; output is opaque) for RGB encoders.</summary>
    public const string PipeRgb = "bgr0";

    /// <summary>The compositor's BGRA readback for HandPeg's GIF encoder.</summary>
    public const string PipeBgra = "bgra";

    /// <summary>Maximum and minimum compositor frame side (output textures are clamped to it).</summary>
    public const int LargestSide = 8192, SmallestSide = 16;

    /// <summary>Placeholder for an audio render path until produced ({sound:1} for the first).</summary>
    public const string SoundToken = "{sound:";

    // The software encoders that take NV12 as it comes, and those that take RGB.
    private static readonly HashSet<string> TakesNv12 = new(StringComparer.Ordinal) { "libx264" };
    private static readonly HashSet<string> RgbFormats = new(StringComparer.Ordinal) { "rgb24", "bgr24", "rgba", "bgra", "argb", "abgr", "gbrp", "gbrp10le", "rgb48le", "bgr0", "rgb0" };

    /// <summary>Lower-case output container from the extension, else the chosen container.</summary>
    public static string Extension(SequenceExportState state)
    {
        var extension = Path.GetExtension(state.OutputPath).TrimStart('.').ToLowerInvariant();
        return extension.Length > 0 ? extension : state.Container.ToLowerInvariant();
    }

    /// <summary>Whether the output is a picture alone, never with sound (GIF, WebP).</summary>
    public static bool IsPictureOnly(SequenceExportState state) => state.IsAnimatedOutput || Extension(state) is "gif" or "webp";

    /// <summary>True for audio-only outputs (FLAC, WAV, MP3); no frames rendered (<see cref="BuildAudioOnly"/>).</summary>
    public static bool IsAudioOnly(SequenceExportState state) => IsAudioOnlyExtension(Extension(state));

    /// <summary>True for audio-only extensions (lower case, no dot): "flac", "wav", "mp3".</summary>
    public static bool IsAudioOnlyExtension(string extension) => extension is "flac" or "wav" or "mp3";

    /// <summary>Encoder for an audio-only kind: FLAC, 16-bit PCM WAV, or LAME MP3.</summary>
    public static string AudioOnlyCodec(string extension) => extension switch
    {
        "flac" => "flac",
        "wav" => "pcm_s16le",
        _ => "libmp3lame",
    };

    /// <summary>
    /// Reason an audio-only export is impossible (empty or silent Sequence); null when possible. Frame size and tables are irrelevant.
    /// </summary>
    public static string? WhyNotAudio(SequenceExportState state, ExportSoundPlan sound) =>
        TimelineLowering.Lowering(state).SequenceSeconds <= 0 ? "the sequence is empty"
        : sound.Tracks.Count == 0 ? "the Sequence has nothing to hear"
        : null;

    /// <summary>
    /// Reason an export is impossible (unrenderable frame size, unsupported rate, empty Sequence); null when possible. Copy exports use
    /// <see cref="BuildCopy"/>.
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

    /// <summary>Frame timing for the state: canvas over the Sequence length (ms precision) at the export rate.</summary>
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
    /// Latest sample time for a Sequence ending at <paramref name="programSeconds"/>: half a canvas frame earlier
    /// (<see cref="GpuExportTiming.SampleLimitSeconds"/>), floored at 0.
    /// </summary>
    public static double SampleLimitOf(double programSeconds, FrameRate canvas) => Math.Max(programSeconds - 0.5 * canvas.FrameDuration, 0);

    /// <summary>
    /// Builds the encode: piped frames in the encoder's preferred format, audio tracks (render tokens or stream copies), chapters (list,
    /// or the source's own when the Sequence is that whole file), soft subtitles likewise, video encoder options, BT.709 tags. GIFs use
    /// GifEncoder. HDR exports (<see cref="SequenceExportState.HdrTransfer"/>) pipe P010 tagged BT.2020nc/BT.2020/transfer and feed the
    /// encoder 10-bit (p010le hardware, yuv420p10le software); SDR commands are unchanged.
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

        // File inputs for stream copies: each copied track's file, plus the source file for soft subtitles/chapters when the Sequence is
        // that whole file (times match). One input per file.
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

        // Copied audio can run past the video (padded containers): -t ends the output with the picture (muxer option, not a filter).
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
    /// Audio Only encode: one raw-float render (<see cref="ExportSoundPlan.For(SequenceExportState, bool, bool)"/>) encoded as 16-bit FLAC,
    /// 16-bit PCM WAV or MP3 at the track bitrate. No pipe:0/rawvideo, colour tags, chapters or -movflags; never a filter.
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

        // Force 16-bit FLAC (FFmpeg would otherwise derive depth from float); PCM codec implies 16-bit; MP3 uses the track bitrate.
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
    /// Reason a Copy export is refused up front (status text), or null: no clip shows a file's picture, or every clip of the target
    /// file plays at a non-1x speed, which stream copy cannot cut (<see cref="SequenceSpans.CopyCutOf"/>). Multi-clip 1x files are fine (cuts list).
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
    /// Copy export: packet-copies the most-used file (<see cref="SequenceSpans.CopyCutOf"/>), through a concat list for multiple, trimmed
    /// or moved clips (<see cref="BuildCutsList"/>). Only that file's streams are included; other clips are omitted (<see cref="CopyCut.LeftOut"/>,
    /// warned by the view model). Audio: Sub-Lane streams by ordinal per each choice (default copy), else the first clip's stream. With no
    /// picture clip the input is the literal "&lt;source&gt;" (FFmpeg will fail).
    /// </summary>
    public static ExportCommand BuildCopy(SequenceExportState state)
    {
        // Uncuttable Sequences (WhyCopyCannotCut) are refused before running; the command is still built for Command Preview.
        var copy = SequenceSpans.CopyCutOf(state.Timeline);
        var input = copy is { Path.Length: > 0 } ? copy.Path : "<source>";
        var cut = copy is { IsCut: true };

        // One list item per argument (titles stay single arguments); joined with QuoteArgument, which FFmpeg splits back exactly.
        var args = new List<string> { "-hide_banner", "-y" };
        if (cut)
            args.AddRange(["-f", "concat", "-safe", "0", "-i", state.CutsFilePath]);
        else
            args.AddRange(["-i", input]);

        var chaptersFromList = (state.ChaptersAtCuts && copy is { Pieces.Count: > 1 }) || state.ChaptersAtMarkers;
        if (chaptersFromList)
            args.AddRange(["-i", state.ChaptersFilePath]);

        args.AddRange(["-map", "0:v:0"]);
        var tracks = copy is null ? [] : CopiedStreams(state, copy);
        for (var k = 0; k < tracks.Count; k++)
        {
            var (stream, title, _, _) = tracks[k];
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"0:a:{stream}?")]);
            if (!string.IsNullOrWhiteSpace(title))
                args.AddRange([string.Create(CultureInfo.InvariantCulture, $"-metadata:s:a:{k}"), "title=" + title.Trim()]);
        }

        // The stream source's own subtitles and chapters, while the copy is that whole file as the whole Sequence.
        var streamSource = Clean(state.StreamSourcePath);
        var ownFile = !cut && copy is not null && streamSource.Length > 0 && string.Equals(copy.Path, streamSource, StringComparison.OrdinalIgnoreCase)
                      && SequenceSpans.WholeFiles(state.Timeline).Contains(streamSource);
        var soft = ownFile ? state.SoftSubtitleStreams : [];
        foreach (var subtitle in soft)
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"0:s:{subtitle}?")]);

        args.AddRange(["-c:v", "copy"]);
        for (var k = 0; k < tracks.Count; k++)
        {
            var (_, _, codec, bitrate) = tracks[k];
            args.AddRange([string.Create(CultureInfo.InvariantCulture, $"-c:a:{k}"), codec]);
            if (codec != "copy" && !AudioTrack.IsLossless(codec))
                args.AddRange([string.Create(CultureInfo.InvariantCulture, $"-b:a:{k}"), bitrate]);
        }

        if (soft.Count > 0)
            args.AddRange(["-c:s", "copy"]);

        // Whole-file copy with audio longer than video (TimelineClip.PictureDuration): -t ends at the picture like a GPU export (muxer option).
        if (!cut && tracks.Count > 0 && copy is { Pieces: [var only] } && state.Timeline.FindClip(only.ClipId) is { PictureDuration: > 0 } held
            && held.SourceDuration - only.Duration > 0.001)
            args.AddRange(["-t", string.Create(CultureInfo.InvariantCulture, $"{only.Duration:0.######}")]);
        args.AddRange(["-map_chapters", chaptersFromList ? "1" : state.ChapterMarkers && ownFile ? "0" : "-1"]);
        if (state.WebOptimized && Extension(state) is "mp4" or "mov" or "m4v")
            args.AddRange(["-movflags", "+faststart"]);
        args.Add(state.OutputPath);
        return new ExportCommand(state.FfmpegExecutable + " " + string.Join(" ", args.Select(QuoteArgument)), cut, chaptersFromList);
    }

    // Copy export audio by stream ordinal: each Sub-Lane's stream per its choice (drop, copy, encode; default copy), titled by choice or
    // lane; without Sub-Lanes, the first clip's stream (ordinal 0's choice).
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
    /// Concat demuxer list for a Copy export (<see cref="SequenceSpans.CopyCutOf"/>): per clip, inpoint/outpoint converted from media time
    /// (first video frame = 0) to the container clock, in playback order.
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

    // Video start offset from container start (MPEG-TS/Matroska), converting media time to concat time: from state, else MediaProbe's
    // session cache; 0 when unknown.
    private static double ContainerShift(SequenceExportState state, string path)
    {
        var video = state.MediaStarts.TryGetValue(path, out var start) ? start : MediaProbe.KnownVideoStart(path);
        var container = state.SeekIndexes.TryGetValue(path, out var index) ? index.ContainerStart : MediaProbe.KnownContainerStart(path);
        return video is { } v && container is { } c && double.IsFinite(v - c) ? v - c : 0;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Substitutes audio render paths for sound tokens, in track order.</summary>
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

    /// <summary>Parses "30", "59.94" or "30000/1001" to a fraction (59.94 = 2997/50).</summary>
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
    /// <summary>
    /// One argument written for a Windows command line so that <c>CommandLineToArgvW</c> (FfmpegRunner.SplitArguments, and FFmpeg's own
    /// C runtime) reads it back exactly: left as it is unless it is empty or holds a space, tab, newline or quote; otherwise quoted, every
    /// quote escaped, and each run of backslashes doubled where it comes right before a quote or before the closing quote (a title
    /// ending in a backslash, "My Mic\", must not swallow the rest of the line).
    /// </summary>
    public static string QuoteArgument(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return a;

        var quoted = new StringBuilder(a.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in a)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    // ----- What the frames are piped as -----

    // BT.709, limited range: what the card converts the picture to (and what an RGB-to-YUV conversion is told).
    private static readonly string[] ColourTags = ["-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];

    /// <summary>
    /// HDR tags matching the P010 readback (FlagReadbackP010): BT.2020 NCL, BT.2020 primaries, limited range, and the project transfer
    /// ("smpte2084" PQ or "arib-std-b67" HLG), applied to input and output.
    /// </summary>
    public static string[] HdrColourTags(string transfer) => ["-color_range", "tv", "-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", transfer];

    /// <summary>
    /// HDR encode settings or null for SDR: tags plus a default 10-bit encoder pixel format (p010le hardware, yuv420p10le software; FFmpeg
    /// converts without a filter). Only when <see cref="SequenceExportState.HdrTransfer"/> is set; never for animated outputs.
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

    // Video encoder named by the options and its requested pixel format (extra options, hardware default, 10-bit).
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

    // Pipe format avoiding FFmpeg conversion where possible: NV12 for x264 and hardware encoders, I420 for other 4:2:0, RGB for RGB;
    // otherwise I420.
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
}
