using System.Globalization;
using System.IO;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- The export's command when the GPU compositor draws the picture (Pass 5.0-AudioAnd3D, GPU export) -----
// The GPU export (GpuExport.cs) renders every frame of the sequence on the compositor and pipes it to FFmpeg. Everything
// else is still FFmpeg's, from the very command the FFmpeg path runs (BuildExportCommand): the same inputs, the same sound
// graph and encoders, the same soft subtitles and chapters. Only the picture is swapped: the composed picture (-map 0:v:0
// and its -vf) gives way to the piped frames (a raw input after the others) and what the graph does to the finished
// picture after the composition (the source's burned subtitles, the fades over everything, the auto-captions, the
// encoder's own format), so the sound is the FFmpeg export's to the sample. Nothing here changes what any other entry
// point writes: the golden graphs stay as they are.

/// <summary>
/// Which frames a GPU export draws and pipes (<see cref="FilterGraphBuilder.BuildGpuExportCommand"/>): the sequence's canvas
/// ticks at its rate (the graph's color source: <c>color=...:r=R:d=D</c>), and an export at another rate is that canvas
/// through FFmpeg's fps filter. The GPU export draws only the canvas frames the fps filter keeps, and pipes each as often as
/// the filter would repeat it, so the encoder is handed the very frames, at the very times, the graph's would be.
/// </summary>
/// <param name="CanvasNumerator">The canvas's rate, frames a second: <c>CanvasNumerator / CanvasDenominator</c>.</param>
/// <param name="CanvasFrames">How many frames the canvas makes: those whose time, in microseconds as FFmpeg rounds it, is before the duration.</param>
/// <param name="OutputNumerator">The export's rate (the canvas's when no other is chosen).</param>
/// <param name="OutputFrames">How many frames the export has: the fps filter's count at the end of the canvas.</param>
public readonly record struct GpuExportTiming(long CanvasNumerator, long CanvasDenominator, long CanvasFrames, long OutputNumerator, long OutputDenominator, long OutputFrames)
{
    /// <summary>Whether the export's rate is the canvas's (every canvas frame is piped once).</summary>
    public bool SameRate => (Int128)OutputNumerator * CanvasDenominator == (Int128)CanvasNumerator * OutputDenominator;

    /// <summary>
    /// The sequence's canvas frame the export's first frame is (0 for an export; a stretch's first for Render Preview,
    /// <see cref="Window"/>): <see cref="CanvasFrames"/> counts from it, the export's own clock starts at it.
    /// </summary>
    public long FirstCanvasFrame { get; init; }

    /// <summary>The export's rate as FFmpeg's -framerate reads it.</summary>
    public string OutputRate => string.Create(CultureInfo.InvariantCulture, $"{OutputNumerator}/{OutputDenominator}");

    /// <summary>Where a canvas frame is on the sequence, in seconds.</summary>
    public double CanvasSeconds(long frame) => (double)frame * CanvasDenominator / CanvasNumerator;

    /// <summary>Where an output frame is in the export, in seconds.</summary>
    public double OutputSeconds(long frame) => (double)frame * OutputDenominator / OutputNumerator;

    /// <summary>
    /// The canvas frame the export shows at output frame <paramref name="slot"/>: the fps filter's, the last canvas frame
    /// whose time, rounded to the output's clock (to the nearest, halves away from zero), is not after the slot.
    /// </summary>
    public long CanvasFrameOf(long slot)
    {
        if (SameRate)
            return FirstCanvasFrame + Math.Clamp(slot, 0, Math.Max(CanvasFrames - 1, 0));

        // round(n x c) <= k, with c = (canvas den x output num) / (canvas num x output den), is 2 n x den x num < num x den x (2k + 1).
        var a = (Int128)CanvasNumerator * OutputDenominator * (2 * slot + 1);
        var b = (Int128)2 * CanvasDenominator * OutputNumerator;
        var frame = (long)((a - 1) / b);
        return FirstCanvasFrame + Math.Clamp(frame, 0, Math.Max(CanvasFrames - 1, 0));
    }

    /// <summary>
    /// The frames of a stretch of the sequence from <paramref name="fromSeconds"/> to <paramref name="toSeconds"/> (Render
    /// Preview): the canvas frames at or after its start (the first is ceil(from x rate)) and before its end, on a clock
    /// that starts at the first of them, exported at <paramref name="output"/> as the fps filter would count them.
    /// </summary>
    public static GpuExportTiming Window(double fromSeconds, double toSeconds, FrameRate canvas, (long Numerator, long Denominator)? output)
    {
        var (cn, cd) = canvas.IsValid ? ((long)canvas.Numerator, (long)canvas.Denominator) : (60L, 1L);

        // A frame within a microsecond of the start or the end is on it (the times are written to the microsecond).
        static long FirstAtOrAfter(double seconds, long n, long d) => (long)Math.Ceiling(Math.Max(seconds, 0) * n / d - 1e-6 * n / d);
        var first = FirstAtOrAfter(fromSeconds, cn, cd);
        var frames = Math.Max(FirstAtOrAfter(toSeconds, cn, cd) - first, 0);

        var (on, od) = output is { Numerator: > 0, Denominator: > 0 } rate ? rate : (cn, cd);
        var timing = new GpuExportTiming(cn, cd, frames, on, od, frames) { FirstCanvasFrame = first };
        return timing.SameRate ? timing : timing with { OutputFrames = (long)RoundNear((Int128)frames * cd * on, (Int128)cn * od) };
    }

    /// <summary>The timing of a canvas of <paramref name="durationMicros"/> (as FFmpeg reads the color source's d=) at a rate, exported at another.</summary>
    public static GpuExportTiming Of(long durationMicros, FrameRate canvas, (long Numerator, long Denominator)? output)
    {
        var (cn, cd) = canvas.IsValid ? ((long)canvas.Numerator, (long)canvas.Denominator) : (60L, 1L);

        // The color source makes frame n while n's time in microseconds (av_rescale_q, to the nearest) is before d.
        var estimate = (long)((Int128)durationMicros * cn / ((Int128)cd * 1_000_000));
        var frames = Math.Max(estimate - 2, 0);
        while (RoundNear((Int128)frames * cd * 1_000_000, cn) < durationMicros)
            frames++;

        var (on, od) = output is { Numerator: > 0, Denominator: > 0 } rate ? rate : (cn, cd);
        var timing = new GpuExportTiming(cn, cd, frames, on, od, frames);
        if (timing.SameRate)
            return timing;

        // The fps filter ends at the canvas's end, rounded to its own clock: round(N x out / canvas) frames.
        return timing with { OutputFrames = (long)RoundNear((Int128)frames * cd * on, (Int128)cn * od) };
    }

    // av_rescale's AV_ROUND_NEAR_INF for what is not negative: halves away from zero.
    private static Int128 RoundNear(Int128 numerator, Int128 denominator) => (2 * numerator + denominator) / (2 * denominator);
}

/// <summary>
/// The GPU export's FFmpeg command (<see cref="FilterGraphBuilder.BuildGpuExportCommand"/>): the program, its arguments
/// (as FFmpeg is to receive them, graphs inline: the runner puts them in files), the size of the frames it reads from its
/// standard input (<see cref="FilterGraphBuilder.GpuPipePixelFormat"/>, rows packed), and which frames those are.
/// </summary>
/// <param name="UsesChaptersFile">The command reads its chapters from <see cref="SequenceExportState.ChaptersFilePath"/>, as the FFmpeg export's does.</param>
public sealed record GpuExportCommand(string Executable, IReadOnlyList<string> Arguments, int Width, int Height, GpuExportTiming Timing, bool UsesChaptersFile)
{
    /// <summary>
    /// The compositor draws the sequence's fades (5.1-Polish, so the captions over them are the preview's): not FFmpeg after
    /// it. False while the source's subtitles are burned in by FFmpeg, which lie under the fades.
    /// </summary>
    public bool FadesOnGpu { get; init; }

    /// <summary>
    /// The compositor draws the captions (5.1-Polish, the user's Q2: through its stack, where the preview draws them), from
    /// the words beside their caption file (CaptionGenerator.ReadWords); FFmpeg's libass does not.
    /// </summary>
    public bool CaptionsOnGpu { get; init; }

    /// <summary>
    /// The pixels piped (5.1-CleanupStudioAndFfmpegPurge): NV12, converted on the card, for every encoder that takes YUV;
    /// the compositor's RGB as it is for one that takes RGB.
    /// </summary>
    public string PipeFormat { get; init; } = FilterGraphBuilder.GpuPipePixelFormat;

    /// <summary>Whether the frames are read back as NV12 (FlagReadbackNv12).</summary>
    public bool PipesNv12 => PipeFormat == FilterGraphBuilder.GpuPipePixelFormat;

    /// <summary>The bytes of one piped frame.</summary>
    public long FrameBytes => PipesNv12 ? CompositorProtocol.Nv12Bytes(Width, Height) : (long)Width * Height * 4;

    /// <summary>The command as one line (the log, the golden test): each argument quoted where it has to be.</summary>
    public string CommandLine => Executable + " " + string.Join(" ", Arguments.Select(a => a.Length == 0 || a.Any(c => c is ' ' or '"' or '\t') ? $"\"{a.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : a));
}

public static partial class FilterGraphBuilder
{
    /// <summary>The piped frames' pixels: 4:2:0 BT.709, limited range, converted on the card (5.1-CleanupStudioAndFfmpegPurge: 62 % fewer bytes than BGRA).</summary>
    public const string GpuPipePixelFormat = "nv12";

    /// <summary>The pixels piped for an encoder that takes RGB: the compositor's BGRA readback, its alpha ignored (the finished picture is opaque).</summary>
    public const string GpuPipeRgbFormat = "bgr0";

    /// <summary>The raw sound <see cref="BuildGpuExportCommand(SequenceExportState, bool, string?, out string)"/> reads, and AudioMixer renders: f32le, this rate, stereo.</summary>
    public const int GpuSoundRate = 48000;

    /// <summary>
    /// Why an export's sound cannot be the sound engine's render (AudioMixer.Render), which is what is heard while editing: what it
    /// has no part in, and FFmpeg's graph then makes as it always has. Null when it can.
    /// </summary>
    public static string? GpuSoundWhyNot(SequenceExportState state) => new Pass(state).GpuSoundWhyNot();

    /// <summary>The largest frame the compositor renders on a side (its output textures are clamped to it).</summary>
    public const int GpuLargestSide = 8192;

    /// <summary>
    /// The export's command with the picture taken from FFmpeg's standard input (frames the GPU compositor drew of the
    /// sequence) instead of the composition graph: everything else as <see cref="BuildExportCommand"/> writes it. Null
    /// (and why, in plain words) when the export cannot be made that way: a Copy export, a GIF or WebP, a deinterlaced or
    /// tone-mapped picture, a main video of no known length, the source's subtitles burned into a spliced main video, captions
    /// among the layers the compositor cannot draw.
    /// </summary>
    public static GpuExportCommand? BuildGpuExportCommand(SequenceExportState state, out string whyNot) => BuildGpuExportCommand(state, captionsOnGpu: false, null, out whyNot);

    /// <summary><see cref="BuildGpuExportCommand(SequenceExportState, bool, string?, out string)"/> with the sound FFmpeg's graph makes.</summary>
    public static GpuExportCommand? BuildGpuExportCommand(SequenceExportState state, bool captionsOnGpu, out string whyNot) => BuildGpuExportCommand(state, captionsOnGpu, null, out whyNot);

    /// <summary>
    /// <see cref="BuildGpuExportCommand(SequenceExportState, out string)"/>, the compositor drawing the captions too when
    /// <paramref name="captionsOnGpu"/> (their words are there to draw them from: 5.1-Polish, the user's Q2). The compositor
    /// draws the fades whenever the source's subtitles are not burned in (FFmpeg's, which lie under them); captions it does
    /// not draw are burned in over everything by FFmpeg, as before, and captions put among the layers it cannot draw make the
    /// export FFmpeg's. With <paramref name="soundPath"/> the sound graph and the sound's maps give way to that file, the sound
    /// engine's render of the program (f32le, <see cref="GpuSoundRate"/>, stereo, from the first frame): FFmpeg only encodes it.
    /// </summary>
    public static GpuExportCommand? BuildGpuExportCommand(SequenceExportState state, bool captionsOnGpu, string? soundPath, out string whyNot)
    {
        // No OpenCL device and no hardware decoding of the first input: its picture is never decoded (the composed picture
        // is the GPU's), and nothing left in the command blurs. The sound's graph and every input stay the same.
        var piped = state with { OpenClBlur = false, HardwareDecodeArguments = "" };
        var pass = new Pass(piped);
        if (pass.GpuExportWhyNot() is { } reason)
        {
            whyNot = reason;
            return null;
        }

        if (pass.GpuTiming() is not { } timing)
        {
            whyNot = $"the export's frame rate ({state.TargetFramerate}) is not one the GPU export can follow";
            return null;
        }

        if (timing.OutputFrames <= 0)
        {
            whyNot = "the sequence is empty";
            return null;
        }

        var fadesOnGpu = state.BurnedSubtitleStreams.Count == 0;
        var gpuCaptions = state.AutoCaptions && captionsOnGpu && fadesOnGpu;
        if (state.AutoCaptions && pass.CaptionsAmongLayers && !gpuCaptions)
        {
            whyNot = fadesOnGpu ? "the captions are among the layers, and their words could not be read for the compositor"
                : "the captions are among the layers, and the source's subtitles are burned in by FFmpeg";
            return null;
        }

        var ffmpeg = piped.FfmpegExecutable;
        var line = pass.BuildFfmpegCommand();
        if (!line.StartsWith(ffmpeg + " ", StringComparison.Ordinal))
        {
            whyNot = "the export's command did not start with its FFmpeg";
            return null;
        }

        var arguments = FfmpegRunner.SplitArguments(line[(ffmpeg.Length + 1)..]);
        var lastInput = arguments.LastIndexOf("-i");
        var picture = IndexOfPair(arguments, "-map", "0:v:0");
        var filters = arguments.IndexOf("-vf");
        if (lastInput < 0 || lastInput + 1 >= arguments.Count || picture < 0 || picture < lastInput)
        {
            whyNot = "the export's command has no picture of the first input to replace";
            return null;
        }

        // The encoder's pixel format, where the command names one (a hardware encoder's, 10-bit, the extra options').
        string? format = null;
        for (var i = lastInput + 2; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] == "-pix_fmt")
                format = arguments[i + 1];
        }

        // The piped picture: the composition's chain gives way to the finishing steps, the first input's picture to the pipe's.
        var (width, height) = (state.Timeline.Width, state.Timeline.Height);
        var pipe = format is null || IsYuvFormat(format) ? GpuPipePixelFormat : GpuPipeRgbFormat;
        var finishing = pass.GpuFinishing(format, fades: !fadesOnGpu, captions: state.AutoCaptions && !gpuCaptions, nv12: pipe == GpuPipePixelFormat);
        if (filters >= 0 && filters + 1 < arguments.Count)
        {
            arguments[filters + 1] = finishing;
        }
        else
        {
            var at = picture + 2;
            while (at + 1 < arguments.Count && arguments[at] == "-map")
                at += 2;
            arguments.InsertRange(at, ["-vf", finishing]);
        }

        var inputs = arguments.Count(a => a == "-i");
        arguments[picture + 1] = string.Create(CultureInfo.InvariantCulture, $"{inputs}:v:0");
        arguments.InsertRange(lastInput + 2,
        [
            "-f", "rawvideo", "-pix_fmt", pipe,
            "-video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-framerate", timing.OutputRate,
            "-i", "pipe:0",
        ]);
        if (soundPath is not null && !UseRenderedSound(arguments, soundPath, inputs + 1))
        {
            whyNot = "the export's command has sound the sound engine's render cannot replace";
            return null;
        }

        whyNot = "";
        return new GpuExportCommand(ffmpeg.Trim('"'), arguments, width, height, timing, pass.UsesChaptersFile) { FadesOnGpu = fadesOnGpu, CaptionsOnGpu = gpuCaptions, PipeFormat = pipe };
    }

    // The command's sound (its graph, its sound maps) replaced by a file the sound engine rendered, input number soundInput, encoded as
    // the command says. The graph is the sound's alone once the picture is piped: anything else in it and nothing is changed.
    private static bool UseRenderedSound(List<string> arguments, string soundPath, int soundInput)
    {
        var graph = arguments.IndexOf("-filter_complex");
        if (graph >= 0)
        {
            if (graph + 1 >= arguments.Count || arguments[graph + 1].Contains(":v", StringComparison.Ordinal))
                return false;
            arguments.RemoveRange(graph, 2);
        }

        var mapped = false;
        for (var i = arguments.Count - 2; i >= 0; i--)
        {
            if (arguments[i] != "-map" || !(arguments[i + 1].StartsWith('[') || arguments[i + 1].Contains(":a", StringComparison.Ordinal)))
                continue;

            if (mapped)
            {
                arguments.RemoveRange(i, 2);
            }
            else
            {
                arguments[i + 1] = string.Create(CultureInfo.InvariantCulture, $"{soundInput}:a:0");
                mapped = true;
            }
        }

        if (!mapped)
            return false;

        var last = arguments.LastIndexOf("-i");
        arguments.InsertRange(last + 2, ["-f", "f32le", "-ar", GpuSoundRate.ToString(CultureInfo.InvariantCulture), "-ch_layout", "stereo", "-i", soundPath]);
        return true;
    }

    /// <summary>
    /// Render Preview on the GPU compositor (5.0-Verify Q5): FFmpeg's command for a stretch of the sequence whose frames the
    /// compositor draws and pipes (rawvideo, as the export's) and whose sound is the sound engine's render of the same
    /// program (AudioMixer.Render: what is heard while editing, which is the export's sound), written beforehand to
    /// <paramref name="soundPath"/> as 48 kHz stereo float; then BuildPreviewCommand's finishing on the piped picture (the
    /// sequence's fades where the stretch overlaps them, the stretch's captions, the preview's size) and its fast x264.
    /// Null (and why) when the GPU cannot draw it: the export's reasons, and the source's burned subtitles, which the FFmpeg
    /// preview places.
    /// </summary>
    /// <param name="soundPath">The stretch's sound (f32le, 48 kHz, stereo, from the first piped frame), or null for none.</param>
    /// <param name="captionsOnGpu">
    /// The compositor draws the captions (5.1-Polish: their words are there to draw them from); otherwise FFmpeg burns them in
    /// over the piped frames, and captions among the layers make the preview FFmpeg's. The fades are the compositor's.
    /// </param>
    public static GpuExportCommand? BuildGpuPreviewCommand(
        SequenceExportState state, string outputPath, double fromSeconds, double toSeconds, int percent, string? captionsPath, string? soundPath, out string whyNot,
        bool captionsOnGpu = false)
    {
        var piped = state with { OpenClBlur = false, HardwareDecodeArguments = "" };
        var pass = new Pass(piped);
        if (pass.GpuExportWhyNot() is { } reason)
        {
            whyNot = reason;
            return null;
        }

        if (state.BurnedSubtitleStreams.Count > 0)
        {
            whyNot = "the source's burned subtitles are placed by the FFmpeg preview";
            return null;
        }

        var output = ParseFrameRate(pass.TargetFramerate);
        if (pass.TargetFramerate is { Length: > 0 } && output is null)
        {
            whyNot = $"the export's frame rate ({state.TargetFramerate}) is not one the GPU export can follow";
            return null;
        }

        var timing = GpuExportTiming.Window(fromSeconds, Math.Min(toSeconds, pass.SequenceLength), state.Timeline.FrameRate, output);
        if (timing.OutputFrames <= 0)
        {
            whyNot = "the stretch holds no frame";
            return null;
        }

        var gpuCaptions = captionsPath is not null && captionsOnGpu;
        if (captionsPath is not null && pass.CaptionsAmongLayers && !gpuCaptions)
        {
            whyNot = "the captions are among the layers, and their words could not be read for the compositor";
            return null;
        }

        var (width, height) = (state.Timeline.Width, state.Timeline.Height);
        var arguments = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "rawvideo", "-pix_fmt", GpuPipePixelFormat,
            "-video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-framerate", timing.OutputRate,
            "-i", "pipe:0",
        };
        if (soundPath is not null)
            arguments.AddRange(["-f", "f32le", "-ar", "48000", "-ch_layout", "stereo", "-i", soundPath]);
        arguments.AddRange(["-map", "0:v:0"]);
        if (soundPath is not null)
            arguments.AddRange(["-map", "1:a:0"]);

        // The captions were made for the stretch from its start; the first piped frame is the first canvas frame at or after it.
        var late = timing.CanvasSeconds(timing.FirstCanvasFrame) - fromSeconds;
        arguments.AddRange(["-vf", pass.GpuPreviewFinishing(gpuCaptions ? null : captionsPath, late, percent)]);
        arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "24"]);
        if (soundPath is not null)
            arguments.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        arguments.AddRange(["-movflags", "+faststart", outputPath]);

        whyNot = "";
        return new GpuExportCommand(piped.FfmpegExecutable.Trim('"'), arguments, width, height, timing, UsesChaptersFile: false) { FadesOnGpu = true, CaptionsOnGpu = gpuCaptions };
    }

    /// <summary>Where an option is followed by a value (both whole arguments); -1 when it is not.</summary>
    private static int IndexOfPair(List<string> arguments, string option, string value)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] == option && arguments[i + 1] == value)
                return i;
        }

        return -1;
    }

    /// <summary>Whether a pixel format is a YUV one (FFmpeg's names: yuv..., nv12, p010le ...), which the piped RGB is converted to with BT.709's matrix.</summary>
    private static bool IsYuvFormat(string format) =>
        format.StartsWith("yuv", StringComparison.Ordinal) || format.StartsWith("nv", StringComparison.Ordinal)
        || (format.Length > 1 && format[0] == 'p' && char.IsAsciiDigit(format[1]));

    /// <summary>A frame rate as the view model writes it ("30", "59.94": GetTargetFramerate) as FFmpeg's fps option reads it (av_d2q: 59.94 is 2997/50).</summary>
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

    private sealed partial class Pass
    {
        /// <summary>See <see cref="FilterGraphBuilder.GpuSoundWhyNot"/>.</summary>
        public string? GpuSoundWhyNot()
        {
            var tracks = GetOutputAudioTracks();
            if (tracks.Count == 0)
                return "the export has no sound track";
            if (NormalizeAudio)
                return "loudness normalization is FFmpeg's (it holds seconds back, so editing never hears it)";
            if (DuckAudio)
                return "the old one-box ducking of presets is FFmpeg's";
            if (tracks.Count > 1 && !MergeAudioTracks)
                return "the sound is kept as several tracks, and the sound engine renders one mix";
            if (tracks.Any(t => t.IsPassthrough))
                return "a track is copied as it is, which only FFmpeg can do";
            return tracks.Any(t => t.Channels > 2) ? "the sound engine mixes to stereo, and a track has more channels" : null;
        }

        /// <summary>Why the export cannot take its picture from the GPU compositor, as far as the graph goes; null when it can.</summary>
        public string? GpuExportWhyNot()
        {
            var extension = Path.GetExtension(S.OutputPath).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0)
                extension = Container;
            if (S.CopyVideoEncoder)
                return "a Copy export keeps the file's own picture";
            if (IsAnimatedOutput || extension is "gif" or "webp")
                return "GIF and WebP are made by FFmpeg's palette steps";
            if (!FrameEngine)
                return "the picture is not composed on the sequence's canvas";
            if (!HasSource || S.SourceHasNoVideo || MainMediaSeconds <= 0)
                return "the main video's length is not known, so FFmpeg's canvas takes its clock from it";
            if (Deinterlace)
                return "deinterlacing is FFmpeg's (the compositor has no deinterlacer yet)";
            if (S.TonemapToSdr)
                return "tone mapping to SDR is FFmpeg's (the compositor has none yet)";
            if (S.BurnedSubtitleStreams.Count > 0 && IsMainSpliced)
                return "the source's subtitles are burned into a main video in several clips, on its own frames";
            if (FrameWidth is < 16 or > GpuLargestSide || FrameHeight is < 16 or > GpuLargestSide)
                return string.Create(CultureInfo.InvariantCulture, $"the frame is {FrameWidth} x {FrameHeight} (the compositor renders 16 to {GpuLargestSide} pixels a side)");
            return null;
        }

        /// <summary>The export's frame rate as the view model gives it (GetTargetFramerate), null for the sequence's own.</summary>
        public string? TargetFramerate => GetTargetFramerate();

        /// <summary>How long the sequence is, in seconds (its canvas's length).</summary>
        public double SequenceLength => SequenceSeconds;

        /// <summary>
        /// Render Preview's finishing on the piped frames (BuildPreviewCommand's, after the compositor): 4:2:0 BT.709 already, as
        /// the export's (GpuFinishing), square pixels, the stretch's captions when FFmpeg burns them in (made from its start,
        /// <paramref name="late"/> seconds before the first frame: the clock is set back for them), the preview's size,
        /// yuv420p. The sequence's fades where the stretch overlaps them are the compositor's (5.1-Polish: its plan draws them
        /// at their moments on the sequence, where BuildStretchFades puts them; the sound engine's render fades the same
        /// moments), under the captions, as the preview has them.
        /// </summary>
        public string GpuPreviewFinishing(string? captionsPath, double late, int percent)
        {
            var chain = new List<string>
            {
                "setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
                "format=yuv420p",
                "setsar=1",
            };
            if (captionsPath is not null)
            {
                var shifted = late > 0.0005;
                if (shifted)
                    chain.Add($"setpts=PTS+{Number(late)}/TB");
                chain.Add(BuildCaptionOverlay(captionsPath));
                if (shifted)
                    chain.Add("setpts=PTS-STARTPTS");
            }

            var factor = Number(Math.Clamp(percent, 5, 100) / 100.0);
            chain.Add($"scale=trunc(iw*{factor}/2)*2:trunc(ih*{factor}/2)*2");
            chain.Add("format=yuv420p");
            return string.Join(",", chain);
        }

        /// <summary>The frames the export has, from the canvas the graph composes on (BuildFrameEngine's color source) and the fps filter after it.</summary>
        public GpuExportTiming? GpuTiming()
        {
            var target = GetTargetFramerate();
            var output = ParseFrameRate(target);
            if (target is { Length: > 0 } && output is null)
                return null;

            // The color source's d= as written (Number), read as FFmpeg reads a time: whole microseconds.
            var written = double.Parse(Number(SequenceSeconds), CultureInfo.InvariantCulture);
            return GpuExportTiming.Of((long)Math.Round(written * 1_000_000), S.Timeline.FrameRate, output);
        }

        /// <summary>
        /// What the export does to its picture once it is composed, for the piped frames: tagged as the 4:2:0 BT.709 the card
        /// converted them to (5.1-CleanupStudioAndFfmpegPurge; RGB piped, for an encoder that takes it, is left to FFmpeg's conversion),
        /// square pixels, then as BuildFfmpegCommand has them: the source's burned subtitles on the
        /// main video's own clock, the fades over everything (unless the compositor drew them: <paramref name="fades"/> false),
        /// the auto-captions (unless it drew them: <paramref name="captions"/> false), the hardware encoder's own format. No fps
        /// filter: the frames piped are already those it would keep (GpuExportTiming).
        /// </summary>
        /// <param name="encoderFormat">The -pix_fmt the command gives the encoder, if any: an RGB one is left to FFmpeg's own conversion.</param>
        /// <param name="nv12">The frames are piped as NV12.</param>
        public string GpuFinishing(string? encoderFormat, bool fades, bool captions, bool nv12)
        {
            var chain = new List<string>();
            if (nv12)
            {
                chain.Add("setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709");
                chain.Add("format=yuv420p");
            }

            chain.Add("setsar=1");

            // Burned-in subtitles are placed by the main video's own time (as BuildFfmpegCommand places them).
            var burn = BuildSubtitleBurnFilters(LocalMediaPath);
            if (burn.Count > 0 && Math.Abs(MainShift) > 0.001)
            {
                chain.Add($"setpts=PTS{Signed(-MainShift)}/TB");
                chain.AddRange(burn);
                chain.Add($"setpts=PTS{Signed(MainShift)}/TB");
            }
            else
            {
                chain.AddRange(burn);
            }

            if (fades)
                chain.AddRange(BuildWholeVideoFilters(GetOutputDuration()));
            if (captions && AutoCaptions)
                chain.Add(BuildCaptionOverlay(CaptionsFilePath));
            if (EncoderFormatFilters() is { } format)
                chain.Add(format);
            return string.Join(",", chain);
        }
    }
}
