using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CliWrap;

namespace HandPegApp.Services;

/// <summary>
/// Strips of frames drawn by the GPU compositor (Pass 5.0-AudioAnd3D, 5.0D): the same picture FFmpeg's strip is
/// (<see cref="FfmpegRunner.GenerateFilmstripAsync"/>, which asks here first), with the same frames' sizes, moments and
/// order and written to the same file, so every picture made from a strip (a clip's block, the Overview Bar, the media
/// browser's tiles) is unchanged. Each thumbnail is a one-layer scene the size of the thumbnail, drawn with its exact frame
/// (<see cref="FrameSceneHeader.FlagExactFrames"/>) and read back (<see cref="FrameSceneHeader.FlagReadback"/>).
/// <para>The worker is a compositor of its own (no standby, a small frame cache, one decode lane, on the background card),
/// started on demand on a thread of its own the first time a strip is asked for, shared by every strip through one queue,
/// and ended after <see cref="IdleMilliseconds"/> with nothing to do: its process, its decoders and its video memory go
/// with it, so nothing is held while HandPeg sits idle. The player's own compositor is never asked: its scenes have one
/// writer (the frame pump), and its frame cache is the player's.</para>
/// <para>Key frames: a moment that lies within half a strip's spacing of a key frame is taken at that key frame, which
/// decodes alone; a 4-hour recording with key frames 10 s apart then costs one decode per thumbnail, never a chase from the
/// key frame before it. A moment with no key frame that near is taken exactly (a short clip's strip shows every moment it
/// asks for, where FFmpeg's shows the key frame before each).</para>
/// <para>Whenever this cannot draw a strip (the setting, no compositor, a file the card cannot decode, a worker that does
/// not answer) it says so and FFmpeg makes the strip as before; a file the compositor could not decode is left to FFmpeg
/// for the rest of the session, and three failures of the engine in a row leave every strip to FFmpeg.</para>
/// </summary>
public static class GpuFilmstrips
{
    // ----- The strip's shape: the one FfmpegRunner's strip has -----

    /// <summary>The most frames a strip holds (FfmpegRunner's strip takes as many inputs).</summary>
    public const int MaxFrames = 40;

    /// <summary>The frames a strip of a video so long holds: as asked (1 to <see cref="MaxFrames"/>), and one for a video of 0.2 s or less.</summary>
    public static int FrameCount(int count, double durationSeconds) => durationSeconds <= 0.2 ? 1 : Math.Clamp(count, 1, MaxFrames);

    /// <summary>How tall a strip's frames are made: an even number of pixels, 16 to 1080.</summary>
    public static int FrameHeight(int height) => Math.Clamp(height / 2 * 2, 16, 1080);

    /// <summary>
    /// The moments a strip's frames are taken at, in seconds from the start of the file (its container's clock, as FFmpeg's
    /// -ss counts): evenly spread, each in the middle of its share of the length, the last kept half a second clear of the end
    /// (a file's length is often a little more than its picture's).
    /// </summary>
    public static double[] Moments(int count, double durationSeconds)
    {
        var moments = new double[Math.Max(count, 0)];
        var last = Math.Max(durationSeconds - 0.5, 0);
        for (var i = 0; i < moments.Length; i++)
            moments[i] = durationSeconds > 0.2 ? Math.Min(durationSeconds * (i + 0.5) / moments.Length, last) : 0;
        return moments;
    }

    /// <summary>
    /// How wide a frame of a strip is: what FFmpeg's scale=-2:height makes of a picture this size (its stored shape, rounded
    /// to the nearest even pixel, as av_rescale rounds: half away from zero).
    /// </summary>
    public static int FrameWidth(int sourceWidth, int sourceHeight, int height) =>
        sourceWidth <= 0 || sourceHeight <= 0 ? 0 : (int)(((long)height * sourceWidth + sourceHeight) / (2L * sourceHeight)) * 2;

    /// <summary>A strip's layout: so many frames, each so wide and tall, side by side; and how many times larger each is drawn before it is shrunk.</summary>
    public readonly record struct StripLayout(int Count, int FrameWidth, int Height, int Factor)
    {
        /// <summary>The strip's width in pixels.</summary>
        public int Width => Count * FrameWidth;
    }

    // The compositor's sampler spreads its taps over at most this many source pixels per pixel drawn: a frame much smaller
    // than its source is drawn this many times larger (at most MaxFactor), then shrunk by whole blocks here.
    private const int SamplerReach = 8, MaxFactor = 6, MaxOutput = 8192, MinOutput = 16;

    /// <summary>
    /// The layout of the strip FFmpeg makes of a video of this size (<see cref="FrameCount"/>, <see cref="FrameHeight"/>,
    /// <see cref="FrameWidth"/>), and the factor each frame is drawn at; null when the compositor cannot draw frames of that
    /// size (narrower than its 16-pixel least: a very tall video at a tiny height).
    /// </summary>
    public static StripLayout? Layout(int count, double durationSeconds, int height, int sourceWidth, int sourceHeight)
    {
        height = FrameHeight(height);
        var width = FrameWidth(sourceWidth, sourceHeight, height);
        var factor = Math.Clamp((int)Math.Ceiling(sourceHeight / (double)(SamplerReach * height)), 1, MaxFactor);
        while (factor > 1 && (factor * width > MaxOutput || factor * height > MaxOutput))
            factor--;
        if (width * factor < MinOutput || width * factor > MaxOutput || height * factor > MaxOutput)
            return null;
        return new StripLayout(FrameCount(count, durationSeconds), width, height, factor);
    }

    /// <summary>
    /// The moments a strip's frames are drawn at: each moment that lies nearer than half <paramref name="spacing"/> to a key
    /// frame (<paramref name="keyFrames"/>, ascending, on the moments' clock) is moved onto it, <paramref name="lead"/> past
    /// its start (well inside the key frame, so that a seek to it lands on it whatever the rounding of the two clocks); the
    /// rest are kept. Two moments never share a key frame (each is moved only inside its own half of the spacing), and the
    /// nearer of the key frames round a moment wins (the earlier one when they are as near).
    /// </summary>
    public static double[] SnapToKeyFrames(ReadOnlySpan<double> moments, ReadOnlySpan<double> keyFrames, double spacing, double lead)
    {
        var snapped = moments.ToArray();
        if (keyFrames.IsEmpty || !(spacing > 0))
            return snapped;

        var reach = spacing / 2;
        for (var i = 0; i < snapped.Length; i++)
        {
            var at = moments[i];

            // The first key frame at or after the moment, and the one before it.
            int low = 0, high = keyFrames.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (keyFrames[middle] < at)
                    low = middle + 1;
                else
                    high = middle;
            }

            var (best, distance) = (double.NaN, double.PositiveInfinity);
            if (low < keyFrames.Length)
                (best, distance) = (keyFrames[low], keyFrames[low] - at);
            if (low > 0 && at - keyFrames[low - 1] <= distance)
                (best, distance) = (keyFrames[low - 1], at - keyFrames[low - 1]);
            if (distance < reach)
                snapped[i] = Math.Max(best + lead, 0);
        }

        return snapped;
    }

    /// <summary>
    /// A frame drawn <paramref name="factor"/> times larger, shrunk into its place in the strip: the mean of each square of
    /// factor x factor pixels (on the gamma-encoded values, as FFmpeg's scaler works), opaque. BGRA.
    /// </summary>
    public static void Shrink(ReadOnlySpan<byte> drawn, int drawnWidth, int factor, Span<byte> strip, int stripStride, int left, int width, int height)
    {
        if (factor <= 1)
        {
            for (var y = 0; y < height; y++)
                drawn.Slice(y * drawnWidth * 4, width * 4).CopyTo(strip.Slice(y * stripStride + left * 4, width * 4));
            return;
        }

        var area = factor * factor;
        var half = area / 2;
        for (var y = 0; y < height; y++)
        {
            var into = strip.Slice(y * stripStride + left * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = drawn.Slice(((y * factor + dy) * drawnWidth + x * factor) * 4, factor * 4);
                    for (var dx = 0; dx < row.Length; dx += 4)
                    {
                        b += row[dx];
                        g += row[dx + 1];
                        r += row[dx + 2];
                    }
                }

                into[x * 4] = (byte)((b + half) / area);
                into[x * 4 + 1] = (byte)((g + half) / area);
                into[x * 4 + 2] = (byte)((r + half) / area);
                into[x * 4 + 3] = 255;
            }
        }
    }

    // ----- Whether to ask the compositor -----

    // A strip's file is decoded alone: the source id the thumbnail worker gives it (let go after each strip).
    private const int SourceId = 0;

    // The thumbnail worker's frame cache: the least the compositor keeps (a thumbnail needs only the frame it shows).
    private const long ThumbnailCacheBytes = 96L << 20;

    /// <summary>With nothing to draw for this long, the thumbnail worker ends (its process and its video memory with it).</summary>
    public const int IdleMilliseconds = 15_000;

    // How long a thumbnail may take: the first of a strip opens the file (a long recording's index is read), the rest decode one frame.
    private const int FirstFrameMs = 15_000, FrameMs = 8_000;

    // A video this short is drawn at its exact moments even when its key frames are not known: no chase is longer than it.
    private const double ShortVideoSeconds = 30;

    // Where no picture is drawn, the scene shows this (magenta): a thumbnail that is all of it was not drawn at all (a file
    // the card cannot decode leaves its layer out), never a video's own black.
    private const float SentinelR = 1, SentinelG = 0, SentinelB = 1;

    private const int JpegQuality = 88;

    // Files the compositor could not decode this session: their strips are FFmpeg's.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FfmpegOnly = new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _givenUp;
    private static int _failuresInARow;

    /// <summary>
    /// Whether strips are drawn by the compositor: with a background card that is a card (Settings' Background GPU is not
    /// Processor Only) and an engine that has not failed three times in a row this session.
    /// </summary>
    public static bool IsWanted => !_givenUp && !ForceFfmpeg && AppSettings.Current.BackgroundGpu != -2;

    /// <summary>For the golden tests only: true makes every strip FFmpeg's, to compare the two makers' strips.</summary>
    public static bool ForceFfmpeg { get; set; }

    /// <summary>
    /// Draws a video's strip with the compositor into <paramref name="imagePath"/> (a JPEG, or a PNG by its extension): the
    /// strip <see cref="FfmpegRunner.GenerateFilmstripAsync"/> would make, frame for frame in size and moment. False, and
    /// nothing written, when it is not drawn this way (see <see cref="IsWanted"/>) or could not be: FFmpeg then makes it.
    /// Called inside the strip's background work; throws <see cref="OperationCanceledException"/> when that is cancelled.
    /// </summary>
    public static async Task<bool> TryMakeAsync(string videoPath, string imagePath, int count, double durationSeconds, int height, CancellationToken token)
    {
        if (!IsWanted || FfmpegOnly.ContainsKey(videoPath))
            return false;

        Sheet sheet;
        try
        {
            // What the file is: its picture's size and where its two clocks start (answered from memory when the file has
            // been inspected this session; inside the strip's own work otherwise).
            if (await MediaProbe.ProbeAsync(videoPath, token, WorkLane.Background) is not { Video: { Width: > 0, Height: > 0 } video } info
                || Layout(count, durationSeconds, height, video.Width, video.Height) is not { } layout)
                return false;

            var containerStart = MediaProbe.KnownContainerStart(videoPath) ?? info.ContainerStartSeconds;
            var videoStart = MediaProbe.KnownVideoStart(videoPath) ?? info.VideoStartSeconds;
            var moments = Moments(layout.Count, durationSeconds);

            // The worker's clock is 0 at the first frame; the moments count from the container's start.
            var worker = new double[moments.Length];
            for (var i = 0; i < moments.Length; i++)
                worker[i] = Math.Max(moments[i] + containerStart - videoStart, 0);

            // The key frames: the file's whole list when HandPeg has it (also handed to the worker, which then seeks
            // exactly), else for a long video the ones its moments' seeks land on, read from its index alone.
            double[]? complete = null, keys = null;
            if (KnownIFrames(videoPath) is { Length: > 0 } listed)
            {
                complete = ToWorker(listed, videoStart);
                keys = complete;
            }
            else if (durationSeconds > ShortVideoSeconds)
            {
                var absolute = new double[moments.Length];
                for (var i = 0; i < moments.Length; i++)
                    absolute[i] = moments[i] + containerStart;
                if (await LandingsAsync(videoPath, absolute, token) is { Length: > 0 } landings)
                    keys = ToWorker(landings, videoStart);
            }

            // A long video whose key frames cannot be known: FFmpeg's own seeks to them cost less than a chase per frame.
            if (keys is null && durationSeconds > ShortVideoSeconds)
                return false;

            var lead = video.FrameRate > 0 ? Math.Min(0.25 / video.FrameRate, 0.004) : 0.002;
            var targets = keys is null ? worker : SnapToKeyFrames(worker, keys, durationSeconds / layout.Count, lead);
            sheet = new Sheet(videoPath, imagePath, layout, targets, complete, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Thumbnails of {Path.GetFileName(videoPath)} are not drawn by the GPU compositor", ex);
            return false;
        }

        // A few decoding sessions at a time on the card, whatever asked for them (see WorkScheduler.GpuDecodeGate).
        await WorkScheduler.GpuDecodeGate.WaitAsync(token);
        Result result;
        try
        {
            using (token.Register(() => sheet.Done.TrySetResult(new Result(Outcome.Cancelled, ""))))
                result = await Enqueue(sheet);
        }
        finally
        {
            WorkScheduler.GpuDecodeGate.Release();
        }

        switch (result.Outcome)
        {
            case Outcome.Done:
                return true;
            case Outcome.Cancelled:
                token.ThrowIfCancellationRequested();
                return false;
            case Outcome.Undecodable:
                FfmpegOnly[videoPath] = true;
                break;
        }

        AppLog.Write($"Thumbnails of {Path.GetFileName(videoPath)} could not be drawn by the GPU compositor ({result.Why}); FFmpeg makes them.");
        return false;
    }

    // A file's I-frames on the container's clock, as HandPeg knows them: from this session, from the Timeline cache's seek
    // facts (seekindex.bin), or from the list FfmpegRunner.GetIFramesAsync kept there; null when none of them has it.
    private static double[]? KnownIFrames(string path)
    {
        if (MediaProbe.KnownIFrames(path) is { Length: > 0 } known)
            return known;
        if (MediaProbe.Recall(path) && MediaProbe.KnownIFrames(path) is { Length: > 0 } recalled)
            return recalled;
        if (PreviewCache.Timeline.ReadBytes(path, "iframes", ".bin") is not { Length: > 0 } kept || kept.Length % sizeof(double) != 0)
            return null;

        var times = MemoryMarshal.Cast<byte, double>(kept).ToArray();
        MediaProbe.NoteIFrames(path, times);
        return times;
    }

    // Times on the container's clock as the worker counts them: from the first frame (GpuResourceBroker's rule).
    private static double[] ToWorker(double[] times, double videoStart)
    {
        var moved = new double[times.Length];
        for (var i = 0; i < times.Length; i++)
            moved[i] = Math.Max(times[i] - videoStart, 0);
        Array.Sort(moved);
        return moved;
    }

    /// <summary>
    /// The key frame each moment's seek lands on (container clock, ascending, each once), from one ffprobe that seeks to every
    /// moment and reads the one packet it lands on (the index alone, nothing decoded: a moment for a 4-hour recording); null
    /// when it could not be read.
    /// </summary>
    private static async Task<double[]?> LandingsAsync(string path, double[] moments, CancellationToken token)
    {
        if (!File.Exists(DependencyUpdater.FfprobePath))
            return null;

        var intervals = string.Join(",", moments.Select(at => string.Create(CultureInfo.InvariantCulture, $"{at:0.######}%+#1")));
        var landed = new List<double>();
        await ProcessPipes.RunAsync(Cli.Wrap(DependencyUpdater.FfprobePath)
            .WithArguments(["-v", "error", "-select_streams", "v:0", "-read_intervals", intervals, "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-i", path])
            .WithStandardOutputPipe(ProcessPipes.Lines(line =>
            {
                var comma = line.IndexOf(',');
                if (comma > 0 && comma + 1 < line.Length && line[comma + 1] == 'K'
                    && double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    lock (landed)
                        landed.Add(seconds);
                }
            }))
            .WithValidation(CommandResultValidation.None), token);

        lock (landed)
            return landed.Count == 0 ? null : landed.Distinct().Order().ToArray();
    }

    // ----- The thumbnail worker and its queue -----

    private enum Outcome
    {
        Done,
        Cancelled,

        /// <summary>The compositor drew nothing of the file (it cannot decode it): FFmpeg's for the session.</summary>
        Undecodable,

        /// <summary>This strip could not be drawn (written, read back): FFmpeg's this time.</summary>
        NotDrawn,

        /// <summary>The worker did not start or did not answer: counted, three in a row and every strip is FFmpeg's.</summary>
        EngineFault,
    }

    private readonly record struct Result(Outcome Outcome, string Why);

    private sealed record Sheet(string VideoPath, string ImagePath, StripLayout Layout, double[] Targets, double[]? IFrames, CancellationToken Token)
    {
        public TaskCompletionSource<Result> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object Gate = new();
    private static readonly Queue<Sheet> Waiting = new();
    private static Thread? _server;
    private static long _serial;

    private static Task<Result> Enqueue(Sheet sheet)
    {
        lock (Gate)
        {
            Waiting.Enqueue(sheet);
            if (_server is null)
            {
                _server = new Thread(Serve) { Name = "GPU filmstrips", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _server.Start();
            }
            else
            {
                Monitor.Pulse(Gate);
            }
        }

        return sheet.Done.Task;
    }

    /// <summary>
    /// The thumbnail worker's thread: the strips in the order they were asked for, the compositor started for the first and
    /// ended once nothing has been asked for <see cref="IdleMilliseconds"/>; then the thread ends too (a new strip starts both
    /// again). The only thread that writes the worker's scenes.
    /// </summary>
    private static void Serve()
    {
        CompositorClient? client = null;
        byte[]? drawn = null, strip = null;
        Sheet? next = null;
        try
        {
            while (true)
            {
                next = null;
                lock (Gate)
                {
                    // Nothing left and no worker: the thread goes (under the lock, so a strip asked for now starts a new one).
                    if (Waiting.Count == 0 && client is null)
                    {
                        _server = null;
                        return;
                    }

                    if (Waiting.Count == 0)
                        Monitor.Wait(Gate, IdleMilliseconds);
                    if (Waiting.Count > 0)
                        next = Waiting.Dequeue();
                }

                if (next is null)
                {
                    Shut(ref client);
                    continue;
                }

                // Cancelled while it waited: never drawn.
                if (next.Done.Task.IsCompleted || next.Token.IsCancellationRequested)
                {
                    next.Done.TrySetResult(new Result(Outcome.Cancelled, ""));
                    continue;
                }

                var result = Draw(next, ref client, ref drawn, ref strip);
                if (result.Outcome == Outcome.EngineFault)
                {
                    Shut(ref client);
                    if (Interlocked.Increment(ref _failuresInARow) >= 3 && !_givenUp)
                    {
                        _givenUp = true;
                        AppLog.Write("GPU filmstrips: the compositor failed three times in a row; FFmpeg makes every strip for the rest of the session.");
                    }
                }
                else if (result.Outcome == Outcome.Done)
                {
                    Volatile.Write(ref _failuresInARow, 0);
                }

                next.Done.TrySetResult(result);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never out of this thread: what was being drawn, and what was waiting, is FFmpeg's.
            AppLog.Write("GPU filmstrips", ex);
            next?.Done.TrySetResult(new Result(Outcome.NotDrawn, ex.Message));
            lock (Gate)
            {
                while (Waiting.TryDequeue(out var left))
                    left.Done.TrySetResult(new Result(Outcome.NotDrawn, ex.Message));
                _server = null;
            }
        }
        finally
        {
            Shut(ref client);

            // A bitmap made here gave the thread a dispatcher (and its message window): ended with it.
            Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }

    private static void Shut(ref CompositorClient? client)
    {
        try
        {
            client?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU filmstrips: the compositor did not close cleanly", ex);
        }

        client = null;
    }

    /// <summary>The thumbnail worker: on the background card (the high-performance one when there is no other), no standby, one decode lane.</summary>
    private static CompositorClient? Start()
    {
        var adapter = GpuAdapters.BackgroundAdapter?.Index ?? AppSettings.Current.CompositorGpuAdapter;
        CompositorClient? client = null;
        try
        {
            client = new CompositorClient(adapter, keepStandby: false, ThumbnailCacheBytes, decodeThreads: 1, announcesRecovery: false,
                softwareDecode: !AppSettings.Current.PlayerHardwareAcceleration);
            var clock = Stopwatch.StartNew();
            while (client.State == CompositorWorkerState.Starting && clock.ElapsedMilliseconds < 8000)
                Thread.Sleep(10);
            if (client.State == CompositorWorkerState.Ready)
                return client;
            AppLog.Write($"GPU filmstrips: the compositor did not start ({client.State}: {client.Message}).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU filmstrips: the compositor could not be started", ex);
        }

        Shut(ref client);
        return null;
    }

    /// <summary>One strip: each frame drawn, read back and shrunk into its place, then the strip written.</summary>
    private static Result Draw(Sheet sheet, ref CompositorClient? client, ref byte[]? drawn, ref byte[]? strip)
    {
        var layout = sheet.Layout;
        var (width, height) = (layout.FrameWidth * layout.Factor, layout.Height * layout.Factor);
        var shot = width * height * 4;
        var stride = layout.Width * 4;
        try
        {
            client ??= Start();
            if (client is null)
                return new Result(Outcome.EngineFault, "the compositor did not start");

            // Kept for the thread's next strips (and let go with it).
            if (drawn is null || drawn.Length < shot)
                drawn = GC.AllocateUninitializedArray<byte>(shot);
            if (strip is null || strip.Length < stride * layout.Height)
                strip = GC.AllocateUninitializedArray<byte>(stride * layout.Height);

            client.EnsureReadback(shot);
            client.SetSource(SourceId, sheet.VideoPath, 0, sheet.IFrames);
            var pathHash = CompositorSharedMemory.PathHash(sheet.VideoPath);
            var clipKey = pathHash ^ Interlocked.Increment(ref _serial);
            try
            {
                // The worker takes up a changed source table as it begins drawing a frame, after it has waited for that
                // frame's exact pictures: an empty frame first, so that the strip's first frame waits for its picture as
                // every other does (without it, the first came back empty).
                WriteScene(client, width, height, 0, pathHash, clipKey, picture: false);
                if (!client.WaitForFrame(client.Publish(), FirstFrameMs))
                    return new Result(Outcome.EngineFault, "the compositor drew nothing in time");

                for (var i = 0; i < layout.Count; i++)
                {
                    if (sheet.Token.IsCancellationRequested || sheet.Done.Task.IsCompleted)
                        return new Result(Outcome.Cancelled, "");

                    WriteScene(client, width, height, sheet.Targets[i], pathHash, clipKey, picture: true);
                    var sequence = client.Publish();
                    if (!client.TryReadback(sequence, drawn, out var gotWidth, out var gotHeight, i == 0 ? FirstFrameMs : FrameMs))
                        return new Result(Outcome.EngineFault, string.Create(CultureInfo.InvariantCulture, $"frame {i + 1} did not come back in time"));
                    if ((gotWidth, gotHeight) != (width, height))
                        return new Result(Outcome.NotDrawn, string.Create(CultureInfo.InvariantCulture, $"a frame came back {gotWidth} x {gotHeight}, not {width} x {height}"));
                    if (IsUndrawn(drawn, width, height))
                        return new Result(Outcome.Undecodable, client.Message is { Length: > 0 } said ? said : "nothing of it was decoded");

                    Shrink(drawn, width, layout.Factor, strip, stride, i * layout.FrameWidth, layout.FrameWidth, layout.Height);
                }
            }
            finally
            {
                // Let go of at once: its decoders close, and nothing is decoded round the last frame in idle time.
                client?.ClearSource(SourceId);
            }

            Save(strip, layout.Width, layout.Height, stride, sheet.ImagePath);
            return new Result(Outcome.Done, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // The strip's file (or the readback's section) could not be written: this strip is FFmpeg's.
            return new Result(Outcome.NotDrawn, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Result(Outcome.EngineFault, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A thumbnail's scene: the frame at <paramref name="seconds"/> (the worker's clock) over the whole canvas, which is the
    /// output, waited for and read back; without <paramref name="picture"/>, the same size with nothing on it.
    /// </summary>
    private static void WriteScene(CompositorClient client, int width, int height, double seconds, long pathHash, long clipKey, bool picture)
    {
        client.ClearScene();
        ref var scene = ref client.Scene;
        (scene.CanvasWidth, scene.CanvasHeight, scene.OutputWidth, scene.OutputHeight) = (width, height, width, height);
        (scene.BackgroundR, scene.BackgroundG, scene.BackgroundB) = (SentinelR, SentinelG, SentinelB);
        scene.ViewScale = 1;
        scene.PlayheadSeconds = seconds;
        if (!picture)
            return;

        scene.Flags = FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback;
        scene.ActiveLayerCount = 1;

        ref var layer = ref client.Layer(0);
        layer.ClipKey = clipKey;
        (layer.SourceKind, layer.SourceId, layer.SourcePathHash, layer.MediaSeconds) = ((int)GpuSourceKind.Media, SourceId, pathHash, seconds);
        (layer.CropLeft, layer.CropTop, layer.CropRight, layer.CropBottom) = (0, 0, 1, 1);
        (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, width, height);
        (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
        (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
        layer.Flags = GpuLayerDescriptor.FlagVisible;
    }

    // Where a frame read back is looked at, in quarters of its width and height: its middle and four points round it.
    private static readonly (int X, int Y)[] Probes = [(2, 2), (1, 1), (3, 1), (1, 3), (3, 3)];

    // Whether a frame read back shows the scene's background at every probe, where the picture should be.
    private static bool IsUndrawn(ReadOnlySpan<byte> bgra, int width, int height)
    {
        foreach (var (x, y) in Probes)
        {
            var at = (height * y / 4 * width + width * x / 4) * 4;
            if (bgra[at] < 250 || bgra[at + 1] > 5 || bgra[at + 2] < 250)
                return false;
        }

        return true;
    }

    /// <summary>The strip written as FFmpeg wrote it: a JPEG (a PNG when the path says .png), 96 DPI, opaque.</summary>
    private static void Save(byte[] pixels, int width, int height, int stride, string path)
    {
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        picture.Freeze();
        BitmapEncoder encoder = string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(picture));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }
}
