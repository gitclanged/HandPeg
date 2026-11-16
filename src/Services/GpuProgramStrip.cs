using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// Overview Scrubber thumbnail sheet: <see cref="Count"/> cells of <see cref="CellWidth"/> x <see cref="CellHeight"/>, <see cref="Columns"/>
/// per row in raster order; cell i covers from i x <see cref="IntervalSeconds"/>. Rendered from the Sequence (<see cref="GpuProgramStrip"/>,
/// at <see cref="Moments"/>) or, as fallback, the opened file's key frames (FFmpeg hover sheet, no moments).
/// </summary>
public sealed record ThumbnailSheet(BitmapSource Picture, int Columns, int Count, int CellWidth, int CellHeight, double IntervalSeconds, double[] Moments)
{
    /// <summary>
    /// Partial sheet during progressive fill: undrawn cells show their nearest drawn neighbour. The complete sheet follows.
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>The picture standing for a moment (seconds on the sheet's clock), clamped to the sheet.</summary>
    public int IndexAt(double seconds) => IntervalSeconds > 0 ? Math.Clamp((int)(seconds / IntervalSeconds), 0, Math.Max(Count - 1, 0)) : 0;

    /// <summary>One picture of the sheet, frozen.</summary>
    public BitmapSource Cell(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(Count - 1, 0));
        var cell = new CroppedBitmap(Picture, new System.Windows.Int32Rect(index % Columns * CellWidth, index / Columns * CellHeight, CellWidth, CellHeight));
        cell.Freeze();
        return cell;
    }
}

/// <summary>
/// Overview Scrubber thumbnails rendered from the Sequence (all tracks, placement, crop, mask, effects, blur fill; no fades or captions)
/// at evenly spaced times, from the live preview state (proxies when available).
/// <list type="bullet">
/// <item><b>Dedicated compositor.</b> Background-adapter worker, no standby, small cache, one decode lane, own resource tables; never the
/// player's. Started on demand, stopped after <see cref="IdleMilliseconds"/> idle (releasing process and VRAM).</item>
/// <item><b>Dedicated STA thread.</b> Plans state (WPF text), bakes masks/stills, renders and reads back, all off the UI thread. Newest
/// state wins: an in-progress sheet stops between cells on a new edit.</item>
/// <item><b>Incremental.</b> Cells are keyed by a scene hash (layer descriptors plus texture recipes); unchanged cells are reused, so edits
/// redraw only affected cells and a restarted worker redraws nothing.</item>
/// <item><b>Prewarm.</b> Hovering or dragging a file on a fast local drive (DriveKind) starts the worker and opens the file
/// (<see cref="Prewarm"/>) so a drop's first sheet starts immediately.</item>
/// <item><b>One decode per cell.</b> Times snap to key frames of the bottom clip's file (<see cref="GpuFilmstrips.SnapToKeyFrames"/>).</item>
/// </list>
/// <para>On failure (disabled, worker unresponsive) no sheet is produced and the scrubber keeps file key frames; three consecutive faults
/// disable it for the session (<see cref="GaveUp"/>).</para>
/// </summary>
public sealed class GpuProgramStrip : IDisposable
{
    /// <summary>The most pictures a sheet holds (the hover sheet's 10 x 10).</summary>
    public const int MaxPictures = FfmpegRunner.SpriteGridSize * FfmpegRunner.SpriteGridSize;

    /// <summary>The pictures to a row of the sheet.</summary>
    public const int Columns = FfmpegRunner.SpriteGridSize;

    /// <summary>Cell bounding square (px): 160 x 90 landscape, 90 x 160 portrait.</summary>
    public const int PictureBox = FfmpegRunner.SpriteThumbnailWidth;

    /// <summary>With nothing to draw for this long, the worker ends (its process and its video memory with it).</summary>
    public const int IdleMilliseconds = 20_000;

    /// <summary>Debounce (ms) after the last edit before redrawing (a slider drag redraws once at rest).</summary>
    public const int SettleMilliseconds = 350;

    /// <summary>Shorter debounce (ms) for the first sheet after opening a file.</summary>
    public const int FirstSettleMilliseconds = 60;

    // The worker's frame cache: a picture needs only the frames it shows.
    private const long CacheBytes = 160L << 20;

    // Per-cell timeout: the first opens files (index reads), later cells decode about one frame.
    private const int FirstPictureMs = 15_000, PictureMs = 8_000;

    /// <summary>
    /// Progressive publish interval (ms) while filling, so long recordings show coverage immediately instead of after the full sheet.
    /// </summary>
    public const int ProgressMilliseconds = 400;

    // How far into a key frame a moment moved onto it is taken (container time stamps are rounded to the millisecond).
    private const double KeyLead = 0.001;

    // Max supersampling factor before box downsampling (sampler footprint of 8 source px per output px; same rule as GpuFilmstrips).
    private const int SamplerReach = 8, MaxFactor = 4;

    private readonly Dispatcher _home;
    private readonly DispatcherTimer _settle;
    private SequenceExportState? _waiting;
    private object? _waitingMark;
    private CancellationTokenSource? _drawing;
    private long _asked;
    private bool _disposed;

    // The worker's thread and what it owns (written by that thread alone).
    private readonly object _gate = new();
    private readonly Queue<Job> _jobs = new();
    private Thread? _server;
    private CompositorClient? _client;
    private GpuResourceBroker? _broker;
    private byte[]? _drawn;
    private readonly Dictionary<long, byte[]> _pictures = [];
    private readonly GpuMediaHint[] _hints = new GpuMediaHint[CompositorProtocol.MaxHints];
    private static int _faultsInARow;
    private static volatile bool _gaveUp;

    // Nothing drawn since the last Clear (a file just opened): the states of its first burst settle for less.
    private bool _fresh = true;

    // A file to open in the worker ahead of a drop (Prewarm), taken by the worker's thread.
    private string? _prewarm;

    private sealed record Job(SequenceExportState State, double SettingInterval, bool ForStrip, long Asked, CancellationToken Token)
    {
        public TaskCompletionSource<ThumbnailSheet?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Told of the sheet as it fills (the worker's thread): null to be given the whole sheet alone.</summary>
        public Action<ThumbnailSheet>? Progress { get; init; }

        /// <summary>
        /// Pause check captured from background work (WorkScheduler.PausePoint); null never pauses (DrawNowAsync).
        /// </summary>
        public Func<bool>? Pause { get; init; }

        /// <summary>Set by the worker thread when paused via <see cref="Pause"/>; the work waits and re-requests.</summary>
        public volatile bool Held;
    }

    /// <summary>Made on the interface's thread, whose dispatcher <see cref="SheetReady"/> is raised on.</summary>
    public GpuProgramStrip()
    {
        _home = Dispatcher.CurrentDispatcher;
        _settle = new DispatcherTimer(DispatcherPriority.Background, _home) { Interval = TimeSpan.FromMilliseconds(SettleMilliseconds) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (Interlocked.Exchange(ref _waiting, null) is { } state)
            {
                _fresh = false;
                Draw(state, _waitingMark);
            }
        };
    }

    /// <summary>
    /// True when Sequence-rendered thumbnails are enabled: background adapter is a GPU and fewer than three consecutive worker failures.
    /// </summary>
    public static bool IsWanted => !_gaveUp && AppSettings.Current.BackgroundGpu != -2;

    /// <summary>A new sheet of the newest state (the interface's thread).</summary>
    public event Action<ThumbnailSheet>? SheetReady;

    /// <summary>Raised on the UI thread after three consecutive failures; the scrubber reverts to file key frames.</summary>
    public event Action? GaveUp;

    /// <summary>Cells actually rendered in the last sheet (others reused); goldens verify incremental redraw.</summary>
    public int LastDrawn { get; private set; }

    /// <summary>
    /// Undo mark of the last complete sheet's request, set when raised via <see cref="SheetReady"/>; null after <see cref="Clear"/>. UI thread.
    /// </summary>
    public object? SheetMark { get; private set; }

    /// <summary>
    /// True while a newer sheet is pending (debouncing or rendering). UI thread. Tab deactivation caches a sheet only when false.
    /// </summary>
    public bool IsPending => _waiting is not null || Volatile.Read(ref _drawing) is not null;

    /// <summary>
    /// Submits the current immutable state (UI thread); rendered <see cref="SettleMilliseconds"/> after a burst. <paramref name="mark"/>
    /// travels with the sheet (<see cref="SheetMark"/>).
    /// </summary>
    public void Request(SequenceExportState state, object? mark = null)
    {
        if (_disposed || !IsWanted)
            return;
        _waiting = state;
        _waitingMark = mark;
        _settle.Stop();
        _settle.Interval = TimeSpan.FromMilliseconds(_fresh ? FirstSettleMilliseconds : SettleMilliseconds);
        _settle.Start();
    }

    /// <summary>Nothing open: what was asked for and what is being drawn are dropped.</summary>
    public void Clear()
    {
        _settle.Stop();
        (_waiting, _waitingMark, SheetMark) = (null, null, null);
        _fresh = true;
        Interlocked.Exchange(ref _drawing, null)?.Cancel();
        Interlocked.Increment(ref _asked);
    }

    /// <summary>
    /// Prewarms a likely drop target (UI thread): starts the worker if needed, registers the file and decodes its first frame so the
    /// drop's sheet starts immediately. Nothing is kept; normal idle shutdown applies.
    /// </summary>
    public void Prewarm(string path)
    {
        if (_disposed || !IsWanted || string.IsNullOrWhiteSpace(path))
            return;
        Volatile.Write(ref _prewarm, path);
        lock (_gate)
        {
            if (_server is null)
            {
                _server = new Thread(Serve) { Name = "GPU program strip", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _server.SetApartmentState(ApartmentState.STA);
                _server.Start();
            }
            else
            {
                Monitor.Pulse(_gate);
            }
        }
    }

    /// <summary>
    /// Renders a sheet immediately, bypassing debounce and the background lane (goldens); null on failure or when superseded.
    /// </summary>
    public async Task<ThumbnailSheet?> DrawNowAsync(SequenceExportState state, double settingInterval, bool forStrip, CancellationToken token)
    {
        var job = new Job(state, settingInterval, forStrip, Interlocked.Increment(ref _asked), token);
        using (token.Register(() => job.Done.TrySetResult(null)))
            return await Enqueue(job);
    }

    private void Draw(SequenceExportState state, object? mark) => Faults.Forget(DrawAsync(state, mark), nameof(Draw));

    private async Task DrawAsync(SequenceExportState state, object? mark)
    {
        var asked = Interlocked.Increment(ref _asked);
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _drawing, cancellation)?.Cancel();
        var settings = AppSettings.Current;
        try
        {
            var sheet = await WorkScheduler.Run(new WorkSpec("Sequence Overview Scrubber pictures", WorkLane.Background) { Visible = false }, async work =>
            {
                var pause = WorkScheduler.PausePoint();
                while (true)
                {
                    // A few decoding sessions at a time on the card, whatever asked for them.
                    await WorkScheduler.GpuDecodeGate.WaitAsync(work.Token);
                    Job job;
                    ThumbnailSheet? drawn;
                    try
                    {
                        // The sheet as it fills goes to the bar at once, while it is still the newest asked for.
                        job = new Job(state, settings.ThumbnailIntervalSeconds, settings.ShowTimelineThumbnails == true, asked, work.Token)
                        {
                            Progress = partial => _home.BeginInvoke(() =>
                            {
                                if (asked == Volatile.Read(ref _asked) && !_disposed)
                                    SheetReady?.Invoke(partial);
                            }),
                            Pause = pause,
                        };
                        using (work.Token.Register(() => job.Done.TrySetResult(null)))
                            drawn = await Enqueue(job);
                    }
                    finally
                    {
                        WorkScheduler.GpuDecodeGate.Release();
                    }

                    // Paused before a cell (playback or scrub hold): release permit and lane slot, then redo the sheet (drawn cells reused by key).
                    if (!job.Held || work.Token.IsCancellationRequested || asked != Volatile.Read(ref _asked))
                        return drawn;
                    await WorkScheduler.WhenBackgroundMayRunAsync(work.Token);
                }
            }, cancellation.Token);

            if (sheet is not null && asked == Volatile.Read(ref _asked) && !_disposed)
            {
                SheetMark = mark;
                SheetReady?.Invoke(sheet);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Sequence Overview Scrubber pictures", ex);
        }
        finally
        {
            Interlocked.CompareExchange(ref _drawing, null, cancellation);
            cancellation.Dispose();
        }
    }

    // ----- The worker's thread -----

    private Task<ThumbnailSheet?> Enqueue(Job job)
    {
        lock (_gate)
        {
            // The newest wins: what waits is dropped.
            while (_jobs.TryDequeue(out var older))
                older.Done.TrySetResult(null);
            _jobs.Enqueue(job);
            if (_server is null)
            {
                _server = new Thread(Serve) { Name = "GPU program strip", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _server.SetApartmentState(ApartmentState.STA);
                _server.Start();
            }
            else
            {
                Monitor.Pulse(_gate);
            }
        }

        return job.Done.Task;
    }

    private void Serve()
    {
        Job? next = null;
        try
        {
            while (true)
            {
                next = null;
                var prewarmed = false;
                lock (_gate)
                {
                    if (_jobs.Count == 0 && (_client is null || _disposed) && Volatile.Read(ref _prewarm) is null)
                    {
                        _server = null;
                        return;
                    }

                    if (_jobs.Count == 0 && Volatile.Read(ref _prewarm) is null)
                        Monitor.Wait(_gate, IdleMilliseconds);
                    if (_jobs.Count > 0)
                        next = _jobs.Dequeue();
                }

                // A file to open ahead of a drop (none is drawn while a sheet waits: the sheet comes first).
                if (next is null && Interlocked.Exchange(ref _prewarm, null) is { } file)
                {
                    OpenAhead(file);
                    prewarmed = true;
                }

                if (next is null)
                {
                    if (!prewarmed)
                        Shut();
                    continue;
                }

                if (next.Done.Task.IsCompleted || next.Token.IsCancellationRequested || next.Asked != Volatile.Read(ref _asked))
                {
                    next.Done.TrySetResult(null);
                    continue;
                }

                next.Done.TrySetResult(DrawSheet(next));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never out of this thread: the Sequence Overview Scrubber keeps what it has.
            AppLog.Write("GPU program strip", ex);
            next?.Done.TrySetResult(null);
            lock (_gate)
            {
                while (_jobs.TryDequeue(out var left))
                    left.Done.TrySetResult(null);
                _server = null;
            }
        }
        finally
        {
            Shut();

            // The text clips drawn here gave the thread a dispatcher (and its message window): ended with it.
            Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }

    private void Shut()
    {
        try
        {
            _broker?.Dispose();
            _client?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU program strip: the compositor did not close cleanly", ex);
        }

        (_broker, _client, _drawn) = (null, null, null);
    }

    private bool Start()
    {
        var settings = AppSettings.Current;
        var adapter = GpuAdapters.BackgroundAdapter?.Index ?? settings.CompositorGpuAdapter;
        GpuNativeBreadcrumbs.Mark("ProgramStrip.Start", $"worker on adapter {adapter} ({GpuAdapters.BackgroundAdapter?.Name ?? "the compositor's"})");
        try
        {
            _client = new CompositorClient(adapter, keepStandby: false, CacheBytes, decodeThreads: 1, announcesRecovery: false,
                softwareDecode: !settings.PlayerHardwareAcceleration);
            var clock = Stopwatch.StartNew();
            while (_client.State == CompositorWorkerState.Starting && clock.ElapsedMilliseconds < 8000)
                Thread.Sleep(10);
            if (_client.State == CompositorWorkerState.Ready)
            {
                _broker = new GpuResourceBroker(_client, bakeOnCallingThread: true);
                return true;
            }

            AppLog.Write($"GPU program strip: the compositor did not start ({_client.State}: {_client.Message}).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU program strip: the compositor could not be started", ex);
        }

        Shut();
        return false;
    }

    /// <summary>
    /// Worker-thread prewarm: starts the worker if needed, registers the file and renders one small frame so its decoder and index are ready.
    /// </summary>
    private void OpenAhead(string file)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            if (_client is null && !Start())
                return;
            var (client, broker) = (_client!, _broker!);
            var source = broker.SourceFor(file, 0);
            if (source < 0)
                return;
            var started = clock.ElapsedMilliseconds;

            client.ClearScene();
            ref var header = ref client.Scene;
            (header.CanvasWidth, header.CanvasHeight, header.OutputWidth, header.OutputHeight, header.ViewScale) = (64, 64, 64, 64, 1);
            header.ActiveLayerCount = 1;
            header.Flags |= FrameSceneHeader.FlagExactFrames;
            ref var layer = ref client.Layer(0);
            layer = default;
            (layer.SourceKind, layer.SourceId, layer.SourcePathHash, layer.MediaSeconds) = ((int)GpuSourceKind.Media, source, broker.PathHashOf(source), 0);
            (layer.X, layer.Y, layer.Width, layer.Height, layer.AnchorX, layer.AnchorY, layer.Opacity) = (0, 0, 64, 64, 0.5f, 0.5f, 1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            var opened = client.WaitForFrame(client.Publish(), FirstPictureMs);
            AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                $"GPU program strip: opened ahead of a drop ({(started > 5 ? $"worker started in {started} ms, " : "")}first frame {(opened ? "in" : "not in")} {clock.ElapsedMilliseconds} ms)."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("GPU program strip: a file could not be opened ahead of a drop", ex);
        }
    }

    /// <summary>Builds one sheet: plan, render or reuse each cell, assemble; null on failure.</summary>
    private ThumbnailSheet? DrawSheet(Job job)
    {
        // A worker that does not start will not start the next time either: the opened file's key frames at once.
        if (_client is null && !Start())
        {
            _faultsInARow = 2;
            return Fault("the compositor did not start");
        }

        var (client, broker) = (_client!, _broker!);

        try
        {
            var plan = GpuScenePlan.Create(job.State with { FadeIn = false, FadeOut = false, AutoCaptions = false, PictureSubtitles = null }, broker);
            var duration = plan.SequenceSeconds;
            if (ReferenceEquals(plan, GpuScenePlan.Empty) || !(duration > 0) || plan.CanvasWidth <= 0 || plan.CanvasHeight <= 0)
                return null;

            // How many: the hover sheet's rule (MainViewModel.GenerateSpriteSheetAsync), the strip's 40 at least.
            var wanted = Math.Max(job.SettingInterval, 1.0);
            if (job.ForStrip)
                wanted = Math.Min(wanted, Math.Max(duration / 40, 0.2));
            var interval = Math.Max(wanted, duration / MaxPictures);
            var count = Math.Clamp((int)Math.Ceiling(duration / interval - 1e-9), 1, MaxPictures);

            // Each picture's size, and how much larger it is drawn.
            var (canvasWidth, canvasHeight) = (plan.CanvasWidth, plan.CanvasHeight);
            var (cellWidth, cellHeight) = canvasWidth >= canvasHeight
                ? (PictureBox, Even(PictureBox * (double)canvasHeight / canvasWidth))
                : (Even(PictureBox * (double)canvasWidth / canvasHeight), PictureBox);
            var factor = Math.Clamp((int)Math.Ceiling(canvasHeight / (double)(SamplerReach * cellHeight)), 1, MaxFactor);
            var (drawWidth, drawHeight) = (cellWidth * factor, cellHeight * factor);
            var drawnBytes = drawWidth * drawHeight * 4;
            client.EnsureReadback(drawnBytes);
            if (_drawn is null || _drawn.Length < drawnBytes)
                _drawn = GC.AllocateUninitializedArray<byte>(drawnBytes);

            var rows = (count + Columns - 1) / Columns;
            var stride = Columns * cellWidth * 4;
            var sheet = new byte[stride * rows * cellHeight];
            var cellBytes = cellWidth * cellHeight * 4;
            var used = new Dictionary<long, byte[]>(count);
            var moments = Moments(plan, broker, count, interval, duration);
            var clock = Stopwatch.StartNew();

            // Publish an empty frame first so the worker loads the new source table before the first real cell waits for exact frames.
            client.ClearScene();
            ref var blank = ref client.Scene;
            (blank.CanvasWidth, blank.CanvasHeight, blank.OutputWidth, blank.OutputHeight, blank.ViewScale) = (canvasWidth, canvasHeight, drawWidth, drawHeight, 1);
            if (!client.WaitForFrame(client.Publish(), FirstPictureMs))
                return Fault("the compositor drew nothing in time");

            // Coarse-to-fine order (DrawingOrder) with progressive publishing.
            var drawnNow = 0;
            var placed = new bool[count];
            var (placedCount, handedOver, lastHanded) = (0, 0, 0L);
            foreach (var i in DrawingOrder(count))
            {
                if (job.Token.IsCancellationRequested || job.Asked != Volatile.Read(ref _asked))
                    return null;

                client.ClearScene();
                ref var header = ref client.Scene;
                var layers = MemoryMarshal.CreateSpan(ref client.Layer(0), CompositorProtocol.MaxLayers);
                var on = Math.Min(plan.Evaluate(moments[i], ref header, layers), CompositorProtocol.MaxLayers);
                (header.OutputWidth, header.OutputHeight, header.ViewScale, header.IsPlaying) = (drawWidth, drawHeight, 1, 0);
                header.Flags |= FrameSceneHeader.FlagExactFrames | FrameSceneHeader.FlagReadback;

                var key = KeyOf(header, layers[..on], broker, drawWidth, drawHeight);
                if (!_pictures.TryGetValue(key, out var picture))
                {
                    // Pause during playback or scrub hold; the work re-requests afterwards.
                    if (!MayGoOn(job))
                    {
                        job.Held = !job.Token.IsCancellationRequested;
                        return null;
                    }

                    var sequence = client.Publish();
                    if (!client.TryReadback(sequence, _drawn, out var gotWidth, out var gotHeight, drawnNow == 0 ? FirstPictureMs : PictureMs))
                        return Fault(string.Create(CultureInfo.InvariantCulture, $"picture {i + 1} did not come back in time"));
                    if ((gotWidth, gotHeight) != (drawWidth, drawHeight))
                        return null;

                    picture = new byte[cellBytes];
                    GpuFilmstrips.Shrink(_drawn, drawWidth, factor, picture, cellWidth * 4, 0, cellWidth, cellHeight);
                    _pictures[key] = picture;
                    drawnNow++;
                }

                used[key] = picture;
                var (left, top) = (i % Columns * cellWidth * 4, i / Columns * cellHeight);
                for (var y = 0; y < cellHeight; y++)
                    picture.AsSpan(y * cellWidth * 4, cellWidth * 4).CopyTo(sheet.AsSpan((top + y) * stride + left, cellWidth * 4));
                placed[i] = true;
                placedCount++;

                // The first picture at once, then the sheet as it fills, every so often.
                if (job.Progress is { } progress && placedCount < count && placedCount > handedOver
                    && (handedOver == 0 || clock.ElapsedMilliseconds - lastHanded >= ProgressMilliseconds))
                {
                    progress(Partial(sheet, placed, stride, cellWidth, cellHeight, rows));
                    (handedOver, lastHanded) = (placedCount, clock.ElapsedMilliseconds);
                }
            }

            // Kept for the next sheet: this one's pictures alone.
            _pictures.Clear();
            foreach (var (key, picture) in used)
                _pictures[key] = picture;
            Volatile.Write(ref _faultsInARow, 0);
            LastDrawn = drawnNow;
            if (drawnNow > 0)
            {
                AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                    $"GPU program strip: {drawnNow} of {count} pictures drawn in {clock.ElapsedMilliseconds} ms ({_keyFramed} on key frames)."));
            }

            var bitmap = BitmapSource.Create(Columns * cellWidth, rows * cellHeight, 96, 96, PixelFormats.Bgr32, null, sheet, stride);
            bitmap.Freeze();
            return new ThumbnailSheet(bitmap, Columns, count, cellWidth, cellHeight, interval, moments);

            // The sheet so far: each picture not drawn yet is its nearest drawn neighbour's (the one before it on a tie).
            ThumbnailSheet Partial(byte[] drawn, bool[] done, int rowBytes, int width, int height, int sheetRows)
            {
                var copy = (byte[])drawn.Clone();
                for (var n = 0; n < done.Length; n++)
                {
                    if (done[n])
                        continue;
                    var from = -1;
                    for (var reach = 1; reach < done.Length && from < 0; reach++)
                        from = n - reach >= 0 && done[n - reach] ? n - reach : n + reach < done.Length && done[n + reach] ? n + reach : -1;
                    if (from < 0)
                        continue;
                    var (fromLeft, fromTop, toLeft, toTop) = (from % Columns * width * 4, from / Columns * height, n % Columns * width * 4, n / Columns * height);
                    for (var y = 0; y < height; y++)
                        drawn.AsSpan((fromTop + y) * rowBytes + fromLeft, width * 4).CopyTo(copy.AsSpan((toTop + y) * rowBytes + toLeft, width * 4));
                }

                var picture = BitmapSource.Create(Columns * width, sheetRows * height, 96, 96, PixelFormats.Bgr32, null, copy, rowBytes);
                picture.Freeze();
                return new ThumbnailSheet(picture, Columns, done.Length, width, height, interval, moments) { IsPartial = true };
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or COMException or InvalidOperationException)
        {
            AppLog.Write("GPU program strip: a sheet could not be drawn", ex);
            return null;
        }
        finally
        {
            // Pictures drawn for a sheet that was stopped stay for the next; but never more than two sheets' worth.
            if (_pictures.Count > 2 * MaxPictures)
                _pictures.Clear();
        }
    }

    /// <summary>In-place pause wait (ms) before releasing lane slot and permit.</summary>
    private const int PausedInPlaceMs = 1500;

    /// <summary>
    /// True to render the next cell. When paused (Job.Pause), waits up to <see cref="PausedInPlaceMs"/>; false on timeout, cancellation or a
    /// newer request.
    /// </summary>
    private bool MayGoOn(Job job)
    {
        if (job.Pause is not { } pause || !pause())
            return true;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < PausedInPlaceMs)
        {
            if (job.Token.IsCancellationRequested || job.Asked != Volatile.Read(ref _asked))
                return false;
            if (!pause())
                return true;
            Thread.Sleep(20);
        }

        return !pause();
    }

    private ThumbnailSheet? Fault(string why)
    {
        AppLog.Write($"GPU program strip: {why}.");
        Shut();
        if (Interlocked.Increment(ref _faultsInARow) >= 3 && !_gaveUp)
        {
            _gaveUp = true;
            AppLog.Write("GPU program strip: three faults in a row; the Sequence Overview Scrubber shows the opened file's key frames for the rest of the session.");
            _home.BeginInvoke(() => GaveUp?.Invoke());
        }

        return null;
    }

    // ----- Where and what -----

    /// <summary>
    /// Coarse-to-fine cell order (first, middle, quarters, ...; each once) so partial sheets cover the Sequence evenly.
    /// </summary>
    public static int[] DrawingOrder(int count)
    {
        var order = new List<int>(Math.Max(count, 0));
        var taken = new bool[Math.Max(count, 0)];
        var step = 1;
        while (step * 2 < count)
            step *= 2;
        for (; step >= 1; step /= 2)
        {
            for (var i = 0; i < count; i += step)
            {
                if (taken[i])
                    continue;
                taken[i] = true;
                order.Add(i);
            }
        }

        return [.. order];
    }

    /// <summary>
    /// Cell times: interval centres snapped to the nearest key frame (either direction, any distance) of the bottom clip's file when key
    /// frames are known and the snap stays within that clip; one decode per cell instead of a GOP chase. Clips without a key frame keep the centre.
    /// </summary>
    private double[] Moments(GpuScenePlan plan, GpuResourceBroker broker, int count, double interval, double duration)
    {
        var moments = new double[count];
        var last = Math.Max(duration - 0.001, 0);
        var keysOf = new Dictionary<int, double[]?>();
        _keyFramed = 0;
        for (var i = 0; i < count; i++)
        {
            var at = Math.Min((i + 0.5) * interval, last);
            moments[i] = OntoKeyFrame(plan, broker, keysOf, at, double.PositiveInfinity, last);
            _keyFramed += moments[i] != at ? 1 : 0;
        }

        return moments;
    }

    private double OntoKeyFrame(GpuScenePlan plan, GpuResourceBroker broker, Dictionary<int, double[]?> keysOf, double at, double reach, double last)
    {
        if (BottomMoment(plan, at) is not { } bottom)
            return at;
        var (source, media) = bottom;
        if (!keysOf.TryGetValue(source, out var keys))
            keysOf[source] = keys = broker.IFramesOfSource(source);
        if (keys is not { Length: > 0 })
            return at;

        var low = Array.BinarySearch(keys, media);
        low = low < 0 ? ~low : low;
        var (best, distance) = (at, double.PositiveInfinity);
        foreach (var index in (ReadOnlySpan<int>)[low - 1, low])
        {
            if (index < 0 || index >= keys.Length)
                continue;

            // Moved by as much as the file is (a clip played at its own speed); kept only where the move lands on it.
            var target = keys[index] + KeyLead;
            var moved = at + (target - media);
            if (Math.Abs(moved - at) >= reach || moved < 0 || moved > last || Math.Abs(moved - at) >= distance)
                continue;
            if (MomentOf(plan, source, moved) is { } landed && Math.Abs(landed - target) < 0.0005)
                (best, distance) = (moved, Math.Abs(moved - at));
        }

        return best;
    }

    // How many of the last sheet's moments were moved onto a key frame (the log's timing line).
    private int _keyFramed;

    // The file the bottom picture reads at a moment of the sequence, and where (the worker's time), when one does.
    private (int Source, double Media)? BottomMoment(GpuScenePlan plan, double at) =>
        plan.MediaAt(at, _hints) > 0 ? (_hints[0].SourceId, _hints[0].MediaSeconds) : null;

    // Where the sequence's moment reads a file (the worker's time), when it does.
    private double? MomentOf(GpuScenePlan plan, int source, double at)
    {
        var found = plan.MediaAt(at, _hints);
        for (var i = 0; i < found; i++)
        {
            if (_hints[i].SourceId == source)
                return _hints[i].MediaSeconds;
        }

        return null;
    }

    /// <summary>Cell cache key: scene hash (canvas, background, layer descriptors, texture recipes) plus size.</summary>
    private static long KeyOf(in FrameSceneHeader header, ReadOnlySpan<GpuLayerDescriptor> layers, GpuResourceBroker broker, int width, int height)
    {
        var hash = default(GpuHash).Add(header.CanvasWidth).Add(header.CanvasHeight).Add(width).Add(height)
            .Add(header.BackgroundR).Add(header.BackgroundG).Add(header.BackgroundB).Add(layers.Length);
        foreach (ref readonly var layer in layers)
        {
            hash = hash.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<GpuLayerDescriptor>(in layer)))
                .Add(broker.ContentOf(layer.MaskResourceId)).Add(broker.ContentOf(layer.LutResourceId))
                .Add(broker.ContentOf(layer.CurveResourceId)).Add(broker.ContentOf(layer.StillResourceId));
        }

        return hash.Value;
    }

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 16);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Clear();
        lock (_gate)
        {
            while (_jobs.TryDequeue(out var left))
                left.Done.TrySetResult(null);
            Monitor.Pulse(_gate);
        }
    }
}
