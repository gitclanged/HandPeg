using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// A sheet of the Overview Bar's pictures: <see cref="Count"/> thumbnails of <see cref="CellWidth"/> x
/// <see cref="CellHeight"/>, <see cref="Columns"/> to a row, left to right and top to bottom, the i-th standing for the
/// stretch from i x <see cref="IntervalSeconds"/>. The sequence's own (<see cref="GpuProgramStrip"/>, drawn at
/// <see cref="Moments"/>), or the main file's key frames (FFmpeg's hover sheet, no moments) where the GPU cannot draw them.
/// </summary>
public sealed record ThumbnailSheet(BitmapSource Picture, int Columns, int Count, int CellWidth, int CellHeight, double IntervalSeconds, double[] Moments)
{
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
/// The Overview Bar's pictures drawn from the sequence itself (Pass 5.0-Verify, the user's call: the timeline shows what
/// the GPU engine renders, never the main file's frames): every track, placement, crop, mask, effect and blurred
/// background at evenly spread moments of the sequence, as the player draws them (fades and captions left out: a
/// thumbnail is the picture), from the live preview's state (proxies where they are made).
/// <list type="bullet">
/// <item><b>Its own compositor.</b> A worker on the background card (the high-performance one when there is no other),
/// with no standby, a small frame cache and one decode lane, and its own resource tables; never the player's, whose frame
/// cache is Instant Seek's. Started for the first sheet, ended after <see cref="IdleMilliseconds"/> with nothing to draw:
/// its process and its video memory go with it.</item>
/// <item><b>Its own thread.</b> One STA thread plans the state (WPF draws text clips there), bakes masks and stills,
/// draws and reads back: nothing of it runs on the interface's thread. The newest state wins: a sheet being drawn when an
/// edit comes in stops between two pictures.</item>
/// <item><b>Only what changed.</b> Each picture is known by a hash of its scene (the layers' descriptors at that moment
/// and the recipes of the textures they read): one whose scene is unchanged is taken from the last sheets, so an edit
/// redraws only the moments it touched, and a worker started again redraws nothing.</item>
/// <item><b>Started ahead.</b> A file hovered in the Media pane or dragged onto a style (on a fast local drive, DriveKind)
/// starts the worker and opens the file in it (<see cref="Prewarm"/>), so the sheet a drop asks for begins at its first
/// picture.</item>
/// <item><b>One decode a picture.</b> A moment within half a picture's stretch of one of the main file's key frames is
/// moved onto it (as the source strips are, <see cref="GpuFilmstrips.SnapToKeyFrames"/>): a long recording costs one
/// decoded frame per picture, never a chase from the key frame before it.</item>
/// </list>
/// <para>Whenever it cannot draw a sheet (the setting, a worker that does not start or answer) it gives none, and the
/// Overview Bar keeps the main file's key frames; three faults in a row end it for the session (<see cref="GaveUp"/>).</para>
/// </summary>
public sealed class GpuProgramStrip : IDisposable
{
    /// <summary>The most pictures a sheet holds (the hover sheet's 10 x 10).</summary>
    public const int MaxPictures = FfmpegRunner.SpriteGridSize * FfmpegRunner.SpriteGridSize;

    /// <summary>The pictures to a row of the sheet.</summary>
    public const int Columns = FfmpegRunner.SpriteGridSize;

    /// <summary>A picture fits in a square this many pixels a side (the hover sheet's width): 160 x 90 for a wide frame, 90 x 160 for a tall one.</summary>
    public const int PictureBox = FfmpegRunner.SpriteThumbnailWidth;

    /// <summary>With nothing to draw for this long, the worker ends (its process and its video memory with it).</summary>
    public const int IdleMilliseconds = 20_000;

    /// <summary>How long after the last edit of a burst the sheet is drawn again, ms (a slider dragged is drawn once, when it rests).</summary>
    public const int SettleMilliseconds = 350;

    /// <summary>The first sheet of a file just opened (nothing to show yet): drawn this soon after its first state, ms.</summary>
    public const int FirstSettleMilliseconds = 60;

    // The worker's frame cache: a picture needs only the frames it shows.
    private const long CacheBytes = 160L << 20;

    // How long a picture may take: the first opens the files (a long recording's index is read), the rest decode a frame or so.
    private const int FirstPictureMs = 15_000, PictureMs = 8_000;

    // How far into a key frame a moment moved onto it is taken (container time stamps are rounded to the millisecond).
    private const double KeyLead = 0.001;

    // Each picture is drawn this many times larger at most, then shrunk by whole blocks (the sampler spreads its taps over
    // at most 8 source pixels per pixel drawn: GpuFilmstrips' rule).
    private const int SamplerReach = 8, MaxFactor = 4;

    private readonly Dispatcher _home;
    private readonly DispatcherTimer _settle;
    private SequenceExportState? _waiting;
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
                Draw(state);
            }
        };
    }

    /// <summary>
    /// Whether the Overview Bar's pictures are the sequence's: with a background card that is a card (Settings' Background
    /// GPU is not Processor Only) and a worker that has not failed three times in a row this session.
    /// </summary>
    public static bool IsWanted => !_gaveUp && AppSettings.Current.BackgroundGpu != -2;

    /// <summary>A new sheet of the newest state (the interface's thread).</summary>
    public event Action<ThumbnailSheet>? SheetReady;

    /// <summary>The worker failed three times in a row: the Overview Bar goes back to the main file's key frames (the interface's thread).</summary>
    public event Action? GaveUp;

    /// <summary>How many pictures the last sheet drew (the rest were unchanged and kept): the golden tests' check that an edit redraws only what it touched.</summary>
    public int LastDrawn { get; private set; }

    /// <summary>
    /// The sequence as it is now (the interface's thread; a state nothing edits afterwards, as the player's is): drawn
    /// <see cref="SettleMilliseconds"/> after the last of a burst.
    /// </summary>
    public void Request(SequenceExportState state)
    {
        if (_disposed || !IsWanted)
            return;
        _waiting = state;
        _settle.Stop();
        _settle.Interval = TimeSpan.FromMilliseconds(_fresh ? FirstSettleMilliseconds : SettleMilliseconds);
        _settle.Start();
    }

    /// <summary>Nothing open: what was asked for and what is being drawn are dropped.</summary>
    public void Clear()
    {
        _settle.Stop();
        _waiting = null;
        _fresh = true;
        Interlocked.Exchange(ref _drawing, null)?.Cancel();
        Interlocked.Increment(ref _asked);
    }

    /// <summary>
    /// A file a drop is likely to open (the interface's thread; Pass 5.0-Release): the worker is started when it is not
    /// running, and the file put in its source table and its first frame decoded, so that the sheet the drop asks for
    /// starts drawing at once. Nothing is drawn or kept for it; the worker ends as ever when nothing more comes.
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
    /// A sheet of a state drawn now, past the settling and the background lane (the golden tests): null when it was not
    /// (or a newer one was asked for meanwhile).
    /// </summary>
    public async Task<ThumbnailSheet?> DrawNowAsync(SequenceExportState state, double settingInterval, bool forStrip, CancellationToken token)
    {
        var job = new Job(state, settingInterval, forStrip, Interlocked.Increment(ref _asked), token);
        using (token.Register(() => job.Done.TrySetResult(null)))
            return await Enqueue(job);
    }

    private async void Draw(SequenceExportState state)
    {
        var asked = Interlocked.Increment(ref _asked);
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _drawing, cancellation)?.Cancel();
        var settings = AppSettings.Current;
        try
        {
            var sheet = await WorkScheduler.Run(new WorkSpec("Overview Bar pictures", WorkLane.Background) { Visible = false }, async work =>
            {
                // A few decoding sessions at a time on the card, whatever asked for them.
                await WorkScheduler.GpuDecodeGate.WaitAsync(work.Token);
                try
                {
                    var job = new Job(state, settings.ThumbnailIntervalSeconds, settings.ShowTimelineThumbnails == true, asked, work.Token);
                    using (work.Token.Register(() => job.Done.TrySetResult(null)))
                        return await Enqueue(job);
                }
                finally
                {
                    WorkScheduler.GpuDecodeGate.Release();
                }
            }, cancellation.Token);

            if (sheet is not null && asked == Volatile.Read(ref _asked) && !_disposed)
                SheetReady?.Invoke(sheet);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Overview Bar pictures", ex);
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
            // Never out of this thread: the Overview Bar keeps what it has.
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
    /// Opens a file in the worker ahead of a drop (the worker's thread): started when it is not running, the file in its
    /// source table, and its first frame drawn once, small, so that its decoder is open and its index read.
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

    /// <summary>One sheet of the state: planned, each picture drawn (or taken from the last sheets), the sheet made; null when it was not.</summary>
    private ThumbnailSheet? DrawSheet(Job job)
    {
        // A worker that does not start will not start the next time either: the main file's key frames at once.
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

            // The worker takes up a changed source table as it begins a frame, after waiting for that frame's exact
            // pictures: an empty frame first, so that the first picture waits for its frames as every other does.
            client.ClearScene();
            ref var blank = ref client.Scene;
            (blank.CanvasWidth, blank.CanvasHeight, blank.OutputWidth, blank.OutputHeight, blank.ViewScale) = (canvasWidth, canvasHeight, drawWidth, drawHeight, 1);
            if (!client.WaitForFrame(client.Publish(), FirstPictureMs))
                return Fault("the compositor drew nothing in time");

            var drawnNow = 0;
            for (var i = 0; i < count; i++)
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

    private ThumbnailSheet? Fault(string why)
    {
        AppLog.Write($"GPU program strip: {why}.");
        Shut();
        if (Interlocked.Increment(ref _faultsInARow) >= 3 && !_gaveUp)
        {
            _gaveUp = true;
            AppLog.Write("GPU program strip: three faults in a row; the Overview Bar shows the main file's key frames for the rest of the session.");
            _home.BeginInvoke(() => GaveUp?.Invoke());
        }

        return null;
    }

    // ----- Where and what -----

    /// <summary>
    /// The moments the pictures are drawn at: each in the middle of its stretch, moved onto the main file's nearest key frame
    /// (before or after where that moment reads it, however far: key frames only, the user's call in 5.0-Release, since a key
    /// frame is one decode and a moment between two is a chase through up to a whole GOP), when HandPeg knows its key frames
    /// and the move lands on that key frame in the same clip. Only a picture whose clip holds no key frame is drawn where it is.
    /// </summary>
    private double[] Moments(GpuScenePlan plan, GpuResourceBroker broker, int count, double interval, double duration)
    {
        var moments = new double[count];
        var last = Math.Max(duration - 0.001, 0);
        var keys = plan.PrimarySource >= 0 ? broker.IFramesOfSource(plan.PrimarySource) : null;
        _keyFramed = 0;
        for (var i = 0; i < count; i++)
        {
            var at = Math.Min((i + 0.5) * interval, last);
            moments[i] = keys is { Length: > 0 } ? OntoKeyFrame(plan, keys, at, double.PositiveInfinity, last) : at;
            _keyFramed += moments[i] != at ? 1 : 0;
        }

        return moments;
    }

    private double OntoKeyFrame(GpuScenePlan plan, double[] keys, double at, double reach, double last)
    {
        if (PrimaryMoment(plan, at) is not { } media)
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
            if (PrimaryMoment(plan, moved) is { } landed && Math.Abs(landed - target) < 0.0005)
                (best, distance) = (moved, Math.Abs(moved - at));
        }

        return best;
    }

    // How many of the last sheet's moments were moved onto a key frame (the log's timing line).
    private int _keyFramed;

    // Where the sequence's moment reads the main file (the worker's time), when it does.
    private double? PrimaryMoment(GpuScenePlan plan, double at)
    {
        var found = plan.MediaAt(at, _hints);
        for (var i = 0; i < found; i++)
        {
            if (_hints[i].SourceId == plan.PrimarySource)
                return _hints[i].MediaSeconds;
        }

        return null;
    }

    /// <summary>A picture's key: the scene at its moment (the canvas, the background, each layer's descriptor and the recipes of the textures it reads) and its size.</summary>
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
