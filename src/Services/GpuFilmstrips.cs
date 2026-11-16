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
/// GPU-rendered filmstrips, layout- and file-compatible with FFmpeg strips (<see cref="FfmpegRunner.GenerateFilmstripAsync"/> tries this
/// first), so clip blocks, the Overview Scrubber and Media Browser tiles are unchanged. Each thumbnail is a one-layer exact-frame scene
/// (<see cref="FrameSceneHeader.FlagExactFrames"/>) read back (<see cref="FrameSceneHeader.FlagReadback"/>).
/// <para>Dedicated worker (no standby, small cache, one decode lane, background adapter) started on demand on its own thread, shared via
/// one queue, and shut down after <see cref="IdleMilliseconds"/> idle, releasing process, decoders and VRAM. Never uses the player's
/// compositor (single scene writer, player-owned cache).</para>
/// <para>Key-frame snapping: times within half the strip spacing of a key frame use that key frame (one decode per thumbnail even for
/// long-GOP recordings); others are exact.</para>
/// <para>Falls back to FFmpeg when disabled, no compositor, undecodable files (remembered for the session) or an unresponsive worker;
/// three consecutive engine failures disable GPU strips for the session.</para>
/// </summary>
public static class GpuFilmstrips
{
    // ----- The strip's shape: the one FfmpegRunner's strip has -----

    /// <summary>The most frames a strip holds (FfmpegRunner's strip takes as many inputs).</summary>
    public const int MaxFrames = 40;

    /// <summary>Strip frame count: as requested (1..<see cref="MaxFrames"/>), or 1 for videos of 0.2 s or less.</summary>
    public static int FrameCount(int count, double durationSeconds) => durationSeconds <= 0.2 ? 1 : Math.Clamp(count, 1, MaxFrames);

    /// <summary>How tall a strip's frames are made: an even number of pixels, 16 to 1080.</summary>
    public static int FrameHeight(int height) => Math.Clamp(height / 2 * 2, 16, 1080);

    /// <summary>
    /// Sample times (container clock, like FFmpeg -ss): centred in equal shares, the last kept 0.5 s before the end (container duration
    /// often exceeds the video stream).
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
    /// Frame width matching FFmpeg scale=-2:height (stored aspect, nearest even pixel, av_rescale half-away-from-zero rounding).
    /// </summary>
    public static int FrameWidth(int sourceWidth, int sourceHeight, int height) =>
        sourceWidth <= 0 || sourceHeight <= 0 ? 0 : (int)(((long)height * sourceWidth + sourceHeight) / (2L * sourceHeight)) * 2;

    /// <summary>Strip layout: frame count, frame size, and supersampling factor.</summary>
    public readonly record struct StripLayout(int Count, int FrameWidth, int Height, int Factor)
    {
        /// <summary>The strip's width in pixels.</summary>
        public int Width => Count * FrameWidth;
    }

    // Sampler footprint limit: heavily downscaled frames render at up to MaxFactor x size, then box-filter down here.
    private const int SamplerReach = 8, MaxFactor = 6, MaxOutput = 8192, MinOutput = 16;

    /// <summary>
    /// FFmpeg-equivalent layout (<see cref="FrameCount"/>, <see cref="FrameHeight"/>, <see cref="FrameWidth"/>) plus supersampling factor;
    /// null below the compositor's 16-pixel minimum width.
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
    /// Snaps each time within half <paramref name="spacing"/> of a key frame (<paramref name="keyFrames"/>, ascending) to it plus
    /// <paramref name="lead"/> (robust to clock rounding); others are unchanged. Snaps never collide (each stays in its half-spacing);
    /// the nearest key frame wins, ties to the earlier.
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
    /// Box-downsamples a frame rendered at <paramref name="factor"/>x into the strip (mean of factor^2 gamma-encoded pixels, like FFmpeg), opaque BGRA.
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

    // Source-table slot for the strip's file (released after each strip).
    private const int SourceId = 0;

    // The thumbnail worker's frame cache: the least the compositor keeps (a thumbnail needs only the frame it shows).
    private const long ThumbnailCacheBytes = 96L << 20;

    /// <summary>Idle timeout after which the thumbnail worker exits (releasing its process and VRAM).</summary>
    public const int IdleMilliseconds = 15_000;

    // Per-thumbnail timeout: the first opens the file (index read), later ones decode one frame.
    private const int FirstFrameMs = 15_000, FrameMs = 8_000;

    // A video this short is drawn at its exact moments even when its key frames are not known: no chase is longer than it.
    private const double ShortVideoSeconds = 30;

    // Magenta background: an all-magenta thumbnail means the layer was not drawn (undecodable), distinguishing it from black video.
    private const float SentinelR = 1, SentinelG = 0, SentinelB = 1;

    private const int JpegQuality = 88;

    // Files the compositor could not decode this session: their strips are FFmpeg's.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FfmpegOnly = new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _givenUp;
    private static int _failuresInARow;

    /// <summary>
    /// True when GPU strips are enabled: background adapter is a GPU (not Processor Only) and fewer than three consecutive engine failures.
    /// </summary>
    public static bool IsWanted => !_givenUp && !ForceFfmpeg && AppSettings.Current.BackgroundGpu != -2;

    /// <summary>For the golden tests only: true makes every strip FFmpeg's, to compare the two makers' strips.</summary>
    public static bool ForceFfmpeg { get; set; }

    /// <summary>
    /// Renders a strip to <paramref name="imagePath"/> (JPEG, or PNG by extension) identical in layout/timing to
    /// <see cref="FfmpegRunner.GenerateFilmstripAsync"/>. Returns false with nothing written when disabled (<see cref="IsWanted"/>) or
    /// failed (FFmpeg fallback). Runs inside background work; throws <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    public static async Task<bool> TryMakeAsync(string videoPath, string imagePath, int count, double durationSeconds, int height, CancellationToken token)
    {
        if (!IsWanted || FfmpegOnly.ContainsKey(videoPath))
            return false;

        Sheet sheet;
        try
        {
            // Probe size and clock origins (cached for files probed this session, else probed inside this work).
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

            // Key frames: the full list when known (also passed to the worker for exact seeks), else for long videos the landing key
            // frames for each sample time from the index alone.
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
            sheet = new Sheet(videoPath, imagePath, layout, targets, complete, token) { Pause = WorkScheduler.PausePoint() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Thumbnails of {Path.GetFileName(videoPath)} are not drawn by the GPU compositor", ex);
            return false;
        }

        return await RunAsync(sheet, videoPath, token);
    }

    /// <summary>
    /// Renders one frame to <paramref name="imagePath"/> (PNG by extension, else JPEG) via the strip compositor (posters, HUD trace stills).
    /// False with nothing written when unavailable (FFmpeg fallback).
    /// </summary>
    /// <param name="atSeconds">Container-clock time (as FFmpeg -ss before -i).</param>
    /// <param name="height">Output height; null for native size.</param>
    /// <param name="onAKeyFrame">Accept the nearest key frame (fast, for posters) instead of the exact frame.</param>
    public static async Task<bool> TryStillAsync(string videoPath, string imagePath, double atSeconds, int? height, bool onAKeyFrame, CancellationToken token)
    {
        if (!IsWanted || FfmpegOnly.ContainsKey(videoPath))
            return false;

        Sheet sheet;
        try
        {
            if (await MediaProbe.ProbeAsync(videoPath, token, onAKeyFrame ? WorkLane.Background : WorkLane.Interactive) is not { Video: { Width: > 0, Height: > 0 } video } info)
                return false;

            var containerStart = MediaProbe.KnownContainerStart(videoPath) ?? info.ContainerStartSeconds;
            var videoStart = MediaProbe.KnownVideoStart(videoPath) ?? info.VideoStartSeconds;
            var moment = Math.Max(atSeconds + containerStart - videoStart, 0);
            var complete = KnownIFrames(videoPath) is { Length: > 0 } listed ? ToWorker(listed, videoStart) : null;
            if (onAKeyFrame && complete is not null)
                moment = SnapToKeyFrames([moment], complete, Math.Max(info.DurationSeconds, 1), 0)[0];

            var tall = height is { } asked ? FrameHeight(asked) : video.Height;
            var wide = height is null ? video.Width : FrameWidth(video.Width, video.Height, tall);
            sheet = new Sheet(videoPath, imagePath, new StripLayout(1, wide, tall, 1), [moment], complete, token) { IsStill = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"A still of {Path.GetFileName(videoPath)} is not drawn by the GPU compositor", ex);
            return false;
        }

        return await RunAsync(sheet, videoPath, token);
    }

    // A sheet through the queue, a few decoding sessions at a time.
    private static async Task<bool> RunAsync(Sheet sheet, string videoPath, CancellationToken token)
    {
        Result result;
        while (true)
        {
            // Strips take a GPU decode permit (WorkScheduler.GpuDecodeGate); stills (priority 0 posters) skip it, being one frame on the
            // existing worker.
            if (!sheet.IsStill)
                await WorkScheduler.GpuDecodeGate.WaitAsync(token);
            try
            {
                using (token.Register(() => sheet.Done.TrySetResult(new Result(Outcome.Cancelled, ""))))
                    result = await Enqueue(sheet);
            }
            finally
            {
                if (!sheet.IsStill)
                    WorkScheduler.GpuDecodeGate.Release();
            }

            // Paused between frames: release permit and lane slot until playback/scrub ends, then redraw the strip from the start.
            if (result.Outcome != Outcome.Paused)
                break;
            await WorkScheduler.WhenBackgroundMayRunAsync(token);
            sheet = sheet.Again();
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

    // Known I-frames (container clock): session cache, seekindex.bin, FfmpegRunner.GetIFramesAsync cache, or the container index
    // (NativeKeyFrames); null when unavailable.
    private static double[]? KnownIFrames(string path)
    {
        if (MediaProbe.KnownIFrames(path) is { Length: > 0 } known)
            return known;
        if (MediaProbe.Recall(path) && MediaProbe.KnownIFrames(path) is { Length: > 0 } recalled)
            return recalled;
        var times = PreviewCache.Timeline.ReadBytes(path, "iframes", ".bin") is { Length: > 0 } kept && kept.Length % sizeof(double) == 0
            ? MemoryMarshal.Cast<byte, double>(kept).ToArray()
            : NativeKeyFrames.Read(path);
        if (times is not { Length: > 0 })
            return null;

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
    /// Distinct ascending landing key frames per sample time from one ffprobe reading one packet per seek (index only, no decode); null on failure.
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

        /// <summary>Worker failed to start or respond; three consecutive failures disable GPU strips.</summary>
        EngineFault,

        /// <summary>Paused between frames (playback or scrub hold); redrawn afterwards.</summary>
        Paused,
    }

    private readonly record struct Result(Outcome Outcome, string Why);

    private sealed record Sheet(string VideoPath, string ImagePath, StripLayout Layout, double[] Targets, double[]? IFrames, CancellationToken Token)
    {
        public TaskCompletionSource<Result> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Single-frame request (<see cref="TryStillAsync"/>), served before queued strips (priority 0).</summary>
        public bool IsStill { get; init; }

        /// <summary>
        /// Pause check captured from the requesting work (WorkScheduler.PausePoint); null never pauses (stills, non-background requests).
        /// </summary>
        public Func<bool>? Pause { get; init; }

        /// <summary>The same strip asked for again after it stopped: its own completion, the rest as it was.</summary>
        public Sheet Again() => new(VideoPath, ImagePath, Layout, Targets, IFrames, Token) { IsStill = IsStill, Pause = Pause };
    }

    private static readonly object Gate = new();
    private static readonly Queue<Sheet> Waiting = new();
    private static readonly Queue<Sheet> StillsWaiting = new();
    private static Thread? _server;
    private static long _serial;

    private static Task<Result> Enqueue(Sheet sheet)
    {
        lock (Gate)
        {
            (sheet.IsStill ? StillsWaiting : Waiting).Enqueue(sheet);
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
    /// Thumbnail thread: serves stills then strips FIFO, starts the compositor on demand and stops it (and itself) after
    /// <see cref="IdleMilliseconds"/> idle; new requests restart both. Sole scene writer for this worker.
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
                    // Idle with no worker: exit under the lock so a concurrent request starts a new thread.
                    if (Waiting.Count + StillsWaiting.Count == 0 && client is null)
                    {
                        _server = null;
                        return;
                    }

                    if (Waiting.Count + StillsWaiting.Count == 0)
                        Monitor.Wait(Gate, IdleMilliseconds);

                    // Stills first so visible posters never wait behind earlier strips.
                    if (StillsWaiting.Count > 0)
                        next = StillsWaiting.Dequeue();
                    else if (Waiting.Count > 0)
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
                while (StillsWaiting.TryDequeue(out var left) || Waiting.TryDequeue(out left))
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

    /// <summary>Starts the thumbnail worker on the background adapter (Compositor GPU by default), no standby, one decode lane.</summary>
    private static CompositorClient? Start()
    {
        var adapter = GpuAdapters.BackgroundAdapter?.Index ?? AppSettings.Current.CompositorGpuAdapter;
        GpuNativeBreadcrumbs.Mark("Filmstrips.Start", $"worker on adapter {adapter} ({GpuAdapters.BackgroundAdapter?.Name ?? "the compositor's"})");
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
            // Do not start during playback or a scrub hold.
            if (!MayGoOn(sheet))
                return new Result(Outcome.Paused, "");
            client ??= Start();
            if (client is null)
                return new Result(Outcome.EngineFault, "the compositor did not start");

            // Reused for subsequent strips; released with the thread.
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
                // The worker reads source-table changes at frame start, after waiting for exact frames; publish an empty frame first so the
                // strip's first real frame waits for its picture (otherwise it returned empty).
                WriteScene(client, width, height, 0, pathHash, clipKey, picture: false);
                if (!client.WaitForFrame(client.Publish(), FirstFrameMs))
                    return new Result(Outcome.EngineFault, "the compositor drew nothing in time");

                for (var i = 0; i < layout.Count; i++)
                {
                    if (sheet.Token.IsCancellationRequested || sheet.Done.Task.IsCompleted)
                        return new Result(Outcome.Cancelled, "");

                    // Pause point between frames during playback or scrub hold (source released below).
                    if (!MayGoOn(sheet))
                        return new Result(Outcome.Paused, "");

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
            AppLog.Write("GPU filmstrips: a strip could not be written", ex);
            return new Result(Outcome.NotDrawn, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU filmstrips: the engine failed", ex);
            return new Result(Outcome.EngineFault, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>In-place pause wait (ms) before a paused strip yields its queue position.</summary>
    private const int PausedInPlaceMs = 1500;

    /// <summary>
    /// True to continue with the next frame. When paused (Sheet.Pause), waits up to <see cref="PausedInPlaceMs"/> for the pause to end;
    /// returns false on timeout, immediately if a still is queued, or on cancellation.
    /// </summary>
    private static bool MayGoOn(Sheet sheet)
    {
        if (sheet.Pause is not { } pause || !pause())
            return true;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < PausedInPlaceMs)
        {
            lock (Gate)
            {
                if (StillsWaiting.Count > 0)
                    return false;
            }

            if (sheet.Token.IsCancellationRequested || !pause())
                return !sheet.Token.IsCancellationRequested;
            Thread.Sleep(20);
        }

        return !pause();
    }

    /// <summary>
    /// Thumbnail scene: frame at <paramref name="seconds"/> (worker clock) filling the canvas/output, awaited and read back; without
    /// <paramref name="picture"/>, an empty scene of the same size.
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
