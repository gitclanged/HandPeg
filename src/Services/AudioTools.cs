using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CliWrap;
using CliWrap.Buffered;
using Lanes = System.Numerics.Vector<float>;

namespace HandPegApp.Services;

/// <summary>Waveform request for one audio track or a mix of tracks.</summary>
/// <param name="Width">Aspect width units per <paramref name="Height"/>.</param>
/// <param name="Color">Colour name (gray) or 0xRRGGBB.</param>
public sealed record WaveformRequest(IReadOnlyList<int> Tracks, int Width, int Height, string Color)
{
    /// <summary>
    /// Cache key encoding rate and tracks ("peaks80-0", "peaks80-0-1"). .peaks files store only the pair count, so the rate is in the key;
    /// legacy keys at other rates are never matched (avoiding misread lengths) and age out of the cache.
    /// </summary>
    public string CacheKind => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"peaks{Waveforms.PeaksPerSecond:0}-{string.Join("-", Tracks)}");
}

/// <summary>Pictures of sound: the waveform images shown on the timeline and in the mixer.</summary>
public static partial class Waveforms
{
    // Decode rate (content below 4 kHz suffices for an envelope), reduced to 80 min/max pairs per second so syllables remain visible
    // when zoomed. One hour = 288,000 pairs, 2.3 MB (+~1/7 for mip folds, WaveformData).
    private const int SampleRate = 8000, SamplesPerPeak = 100;

    /// <summary>How many pairs of peaks there are to a second of sound.</summary>
    public const double PeaksPerSecond = (double)SampleRate / SamplesPerPeak;

    // The picture made for places that want a picture has at most this many columns, however long the sound.
    private const int MostColumns = 4000;

    // The peaks behind each picture, for what draws a waveform itself at the size it is shown (the time bars).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, WaveformData> Peaks = [];

    /// <summary>The peaks a waveform picture was drawn from; null for a picture that is not one of these.</summary>
    public static WaveformData? DataOf(ImageSource? picture) => picture is not null && Peaks.TryGetValue(picture, out var data) ? data : null;

    /// <summary>
    /// Reads a full audio track as peaks and returns a vector waveform (single filled geometry). Samples are streamed and reduced; no image
    /// file or bitmap. Peaks are attached to the drawing (<see cref="DataOf"/>). Null on failure.
    /// </summary>
    /// <param name="trackIndexes">Zero-based audio track indexes; several are mixed.</param>
    /// <param name="imagePath">Unused (vector output); kept for caller compatibility.</param>
    /// <param name="width">Aspect width units per <paramref name="height"/>.</param>
    /// <param name="color">Colour name (gray) or 0xRRGGBB.</param>
    public static async Task<ImageSource?> RenderAsync(
        string mediaPath, IReadOnlyList<int> trackIndexes, string imagePath, int width, int height, string color, CancellationToken cancellationToken) =>
        (await RenderManyAsync(mediaPath, [new WaveformRequest(trackIndexes, width, height, color)], cancellationToken))[0];

    /// <summary>
    /// Builds several waveforms of one file in a single decode pass (each requested track or mix as one channel of one stream) instead of
    /// one read per waveform. Cached results are reused.
    /// </summary>
    /// <returns>One drawing per request in order; null entries on failure.</returns>
    /// <param name="lane">Scheduler lane: Background unless the user is waiting.</param>
    public static Task<ImageSource?[]> RenderManyAsync(string mediaPath, IReadOnlyList<WaveformRequest> requests, CancellationToken cancellationToken, WorkLane lane = WorkLane.Background) =>
        ReadManyAsync(mediaPath, requests, cancellationToken, lane, draw: true);

    /// <summary>
    /// Pre-bakes peaks to the Timeline cache only (folder pre-index, priority 3): no drawing, nothing kept in memory. Cached entries are skipped.
    /// </summary>
    public static async Task BakeAsync(string mediaPath, IReadOnlyList<WaveformRequest> requests, CancellationToken cancellationToken) =>
        await ReadManyAsync(mediaPath, requests, cancellationToken, WorkLane.Idle, draw: false);

    // RenderManyAsync's reading; without draw, the peaks only written to the cache (BakeAsync), every picture null.
    private static async Task<ImageSource?[]> ReadManyAsync(string mediaPath, IReadOnlyList<WaveformRequest> requests, CancellationToken cancellationToken, WorkLane lane, bool draw)
    {
        var pictures = new ImageSource?[requests.Count];
        if (!File.Exists(mediaPath))
            return pictures;

        // Read before, for this file as it is and these tracks: the peaks are taken from the cache.
        var wanted = new List<int>();
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Tracks.Count == 0 || !draw && PreviewCache.Timeline.Find(mediaPath, requests[i].CacheKind, ".peaks") is not null)
                continue;
            if (draw && ReadPeaks(PreviewCache.Timeline.Find(mediaPath, requests[i].CacheKind, ".peaks")) is { } kept)
                pictures[i] = Picture(requests[i], new WaveformData(kept.Lows, kept.Highs, BrushOf(requests[i].Color)));
            else
                wanted.Add(i);
        }

        if (wanted.Count == 0)
            return pictures;

        // Scheduled background work (WorkScheduler), deduplicated by key (bakes use a separate key: they return no drawings).
        var key = $"wave|{WorkScheduler.PathKey(mediaPath)}|{string.Join(",", wanted.Select(i => requests[i].CacheKind))}{(draw ? "" : "|bake")}";
        return await WorkScheduler.Run(new WorkSpec($"Waveforms of {Path.GetFileName(mediaPath)}", lane) { Key = key },
            work => DecodeAsync(work.Token), cancellationToken);

        async Task<ImageSource?[]> DecodeAsync(CancellationToken cancellationToken)
        {
            // Native in-process decode first (Waveforms.Native.cs): each track once, single pass, on a low-priority thread.
            var distinct = wanted.SelectMany(i => requests[i].Tracks).Distinct().ToList();
            var native = distinct.Select(_ => new PeakReader()).ToArray();
            var written = new bool[requests.Count];

            // Background hold (scrub, drag, playback): the reading thread yields between reads (Waveforms.YieldWhilePaused).
            var pause = WorkScheduler.PausePoint();
            try
            {
                var read = await Task.Factory.StartNew(() =>
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    try
                    {
                        return ReadPeaksNatively(mediaPath, distinct, native, cancellationToken, pause);
                    }
                    finally
                    {
                        Thread.CurrentThread.Priority = ThreadPriority.Normal;
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                if (read)
                {
                    foreach (var one in native)
                        one.Finish();
                    foreach (var w in wanted)
                    {
                        // Mix envelope = sum of track envelopes: an upper bound, exact where only one track is active.
                        var request = requests[w];
                        float[]? lows = null, highs = null;
                        foreach (var track in request.Tracks)
                        {
                            if (native[distinct.IndexOf(track)].TakePeaks() is not var (trackLows, trackHighs))
                                continue;
                            if (lows is null)
                            {
                                (lows, highs) = (trackLows, trackHighs);
                                continue;
                            }

                            if (trackLows.Length > lows.Length)
                                (lows, highs, trackLows, trackHighs) = (trackLows, trackHighs, lows, highs!);
                            for (var i = 0; i < trackLows.Length; i++)
                                (lows[i], highs![i]) = (Math.Max(lows[i] + trackLows[i], -1), Math.Min(highs[i] + trackHighs[i], 1));
                        }

                        if (lows is null || highs is null)
                            continue;

                        var (keptLows, keptHighs) = (lows, highs);
                        PreviewCache.Timeline.Write(mediaPath, request.CacheKind, ".peaks", stream => WritePeaks(stream, keptLows, keptHighs));
                        written[w] = true;
                        if (draw)
                            pictures[w] = Picture(request, new WaveformData(lows, highs, BrushOf(request.Color)));
                    }

                    if (wanted.All(w => written[w]))
                        return pictures;
                }
            }
            finally
            {
                foreach (var one in native)
                    one.Dispose();
            }

            if (!File.Exists(DependencyUpdater.FfmpegPath))
                return pictures;

            // Each track goes to as many waveforms as want it: split where that is more than one.
            var uses = wanted.SelectMany(i => requests[i].Tracks).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            var taken = uses.Keys.ToDictionary(t => t, _ => 0);
            var graph = new System.Text.StringBuilder();
            foreach (var (track, count) in uses.Where(u => u.Value > 1))
            {
                graph.Append($"[0:a:{track}]asplit={count}");
                for (var k = 0; k < count; k++)
                    graph.Append($"[t{track}_{k}]");
                graph.Append(';');
            }

            string Input(int track) => uses[track] > 1 ? $"[t{track}_{taken[track]++}]" : $"[0:a:{track}]";
            for (var w = 0; w < wanted.Count; w++)
            {
                var tracks = requests[wanted[w]].Tracks;
                graph.Append(string.Concat(tracks.Select(Input)));
                if (tracks.Count > 1)
                    graph.Append($"amix=inputs={tracks.Count}:normalize=0,");
                graph.Append($"aresample={SampleRate},aformat=sample_fmts=s16:channel_layouts=mono[w{w}];");
            }

            // Every waveform a channel of the one stream that comes out.
            if (wanted.Count > 1)
                graph.Append(string.Concat(Enumerable.Range(0, wanted.Count).Select(w => $"[w{w}]"))).Append($"amerge=inputs={wanted.Count}[wave]");
            else
                graph.Append("[w0]anull[wave]");

            var readers = wanted.Select(_ => new PeakReader()).ToArray();
            var samples = PipeTarget.Create(async (stream, token) =>
            {
                // Borrowed, and given back: what FFmpeg writes passes through the same room for every waveform.
                var frame = 2 * readers.Length;
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768 / frame * frame + frame);
                try
                {
                    var held = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(held, buffer.Length / frame * frame - held), token)) > 0)
                    {
                        var have = held + read;
                        var whole = have / frame * frame;
                        Deal(buffer, whole, readers);

                        // Part of a frame at the end of what was read waits for the rest of it.
                        held = have - whole;
                        if (held > 0)
                            buffer.AsSpan(whole, held).CopyTo(buffer);
                    }

                    foreach (var reader in readers)
                        reader.Finish();
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            });

            try
            {
                var result = await ProcessPipes.RunGatedAsync(Cli.Wrap(DependencyUpdater.FfmpegPath)
                    .WithArguments(["-hide_banner", "-loglevel", "error", "-i", mediaPath, "-filter_complex", graph.ToString(), "-map", "[wave]", "-f", "s16le", "-"])
                    .WithStandardOutputPipe(samples)
                    .WithValidation(CommandResultValidation.None),
                    cancellationToken);

                if (result.ExitCode != 0)
                    return pictures;

                for (var w = 0; w < wanted.Count; w++)
                {
                    var request = requests[wanted[w]];
                    if (readers[w].TakePeaks() is not var (lows, highs))
                        continue;

                    PreviewCache.Timeline.Write(mediaPath, request.CacheKind, ".peaks", stream => WritePeaks(stream, lows, highs));
                    if (draw)
                        pictures[wanted[w]] = Picture(request, new WaveformData(lows, highs, BrushOf(request.Color)));
                }

                return pictures;
            }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
            }
        }

        static ImageSource Picture(WaveformRequest request, WaveformData data)
        {
            var picture = Draw(data, Math.Max(request.Width, 2), Math.Max(request.Height, 2));
            Peaks.AddOrUpdate(picture, data);
            return picture;
        }
    }

    /// <summary>De-interleaves 16-bit little-endian frames (one sample per waveform) into per-waveform reducers.</summary>
    private static void Deal(byte[] buffer, int length, PeakReader[] readers)
    {
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, length));
        for (var at = 0; at < values.Length; at += readers.Length)
        {
            for (var c = 0; c < readers.Length; c++)
                readers[c].Add(values[at + c]);
        }
    }

    // .peaks format: pair count, lows, highs (float32). Read/written directly from/to the waveform arrays without intermediate copies.
    private static void WritePeaks(Stream stream, float[] lows, float[] highs)
    {
        Span<byte> count = stackalloc byte[4];
        BitConverter.TryWriteBytes(count, highs.Length);
        stream.Write(count);
        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(lows.AsSpan()));
        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(highs.AsSpan()));
    }

    private static (float[] Lows, float[] Highs)? ReadPeaks(string? path)
    {
        if (path is null)
            return null;

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = RandomAccess.GetLength(handle);
            Span<byte> head = stackalloc byte[4];
            if (length < 4 || RandomAccess.Read(handle, head, 0) != 4)
                return null;

            var count = BitConverter.ToInt32(head);
            if (count <= 0 || length != 4 + count * 8L)
                return null;

            var (lows, highs) = (new float[count], new float[count]);
            var lowBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(lows.AsSpan());
            var highBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(highs.AsSpan());
            return RandomAccess.Read(handle, lowBytes, 4) == lowBytes.Length && RandomAccess.Read(handle, highBytes, 4 + lowBytes.Length) == highBytes.Length
                ? (lows, highs)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Streaming min/max reducer for 16-bit samples, accumulating into a growable pooled buffer copied once at final size.
    /// </summary>
    private sealed class PeakReader : IDisposable
    {
        private float[] _lows = System.Buffers.ArrayPool<float>.Shared.Rent(4096), _highs = System.Buffers.ArrayPool<float>.Shared.Rent(4096);
        private int _pairs, _count, _low, _high;

        public void Add(short sample)
        {
            if (sample < _low)
                _low = sample;
            if (sample > _high)
                _high = sample;
            if (++_count == SamplesPerPeak)
                Finish();
        }

        public void Finish()
        {
            if (_count == 0)
                return;

            if (_pairs == _lows.Length)
            {
                Grow(ref _lows);
                Grow(ref _highs);
            }

            _lows[_pairs] = _low / 32768f;
            _highs[_pairs++] = _high / 32768f;
            (_count, _low, _high) = (0, 0, 0);
        }

        /// <summary>The peaks, in arrays of their own exact size; null when there were none.</summary>
        public (float[] Lows, float[] Highs)? TakePeaks() => _pairs == 0 ? null : (_lows.AsSpan(0, _pairs).ToArray(), _highs.AsSpan(0, _pairs).ToArray());

        private void Grow(ref float[] room)
        {
            var larger = System.Buffers.ArrayPool<float>.Shared.Rent(room.Length * 2);
            room.AsSpan(0, _pairs).CopyTo(larger);
            System.Buffers.ArrayPool<float>.Shared.Return(room);
            room = larger;
        }

        public void Dispose()
        {
            System.Buffers.ArrayPool<float>.Shared.Return(_lows);
            System.Buffers.ArrayPool<float>.Shared.Return(_highs);
            (_lows, _highs) = ([], []);
        }
    }

    /// <summary>
    /// Sub-range of a waveform between two length fractions (trimmed audio); null for non-waveform drawings.
    /// </summary>
    public static ImageSource? Cut(ImageSource? picture, double from, double to, int width, int height)
    {
        if (DataOf(picture) is not { Count: > 0 } whole)
            return null;

        var data = whole.Slice(from, to);
        var cut = Draw(data, Math.Max(width, 2), Math.Max(height, 2));
        Peaks.AddOrUpdate(cut, data);
        return cut;
    }

    /// <summary>Builds the vector envelope: one shape, one column per bucket of pairs (WaveformData.Envelope).</summary>
    private static ImageSource Draw(WaveformData data, double width, double height)
    {
        var columns = Math.Max(Math.Min(data.Count, MostColumns), 1);
        var (step, middle) = (width / columns, height / 2);
        var pool = System.Buffers.ArrayPool<double>.Shared;
        var floats = System.Buffers.ArrayPool<float>.Shared;
        var (tops, bottoms) = (pool.Rent(columns), pool.Rent(columns));
        var (lows, highs) = (floats.Rent(columns), floats.Rent(columns));
        data.Envelope(0, data.Seconds, lows.AsSpan(0, columns), highs.AsSpan(0, columns));
        for (var c = 0; c < columns; c++)
        {
            // Silence is still a line: the sound is there, and quiet.
            tops[c] = middle - Math.Max(highs[c] * middle, height / 200);
            bottoms[c] = middle - Math.Min(lows[c] * middle, -height / 200);
        }

        floats.Return(lows);
        floats.Return(highs);

        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(new Point(0, tops[0]), isFilled: true, isClosed: true);
            for (var c = 0; c < columns; c++)
                path.LineTo(new Point((c + 0.5) * step, tops[c]), false, false);
            path.LineTo(new Point(width, tops[columns - 1]), false, false);
            path.LineTo(new Point(width, bottoms[columns - 1]), false, false);
            for (var c = columns - 1; c >= 0; c--)
                path.LineTo(new Point((c + 0.5) * step, bottoms[c]), false, false);
            path.LineTo(new Point(0, bottoms[0]), false, false);
        }

        outline.Freeze();
        pool.Return(tops);
        pool.Return(bottoms);

        // Transparent bounds rectangle fixes full height; otherwise bounds would shrink to the loudest peak.
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, width, height))));
        drawing.Children.Add(new GeometryDrawing(data.Fill, null, outline));
        drawing.Freeze();

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static Brush BrushOf(string color)
    {
        try
        {
            var name = color.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "#" + color[2..] : color;
            if (ColorConverter.ConvertFromString(name) is Color parsed)
            {
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
            // Gray, then.
        }

        return Brushes.Gray;
    }

    /// <summary>Loads an image fully into memory and releases the file handle.</summary>
    public static ImageSource? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Windows notifications via a notification-area icon (shown as toasts; no installer registration needed). Always silent
/// (NIIF_NOSOUND) and suppressed while HandPeg is foreground, where <see cref="Said"/> shows the text on the status line instead.
/// </summary>
public static partial class Notifier
{
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NiifUser = 0x4, NiifNoSound = 0x10;
    private const uint IconId = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static readonly object Gate = new();

    // Notification-area icon (kept alive while shown) and its owner HWND; zero before first use.
    private static System.Drawing.Icon? _icon;
    private static IntPtr _owner;

    /// <summary>Raised (any thread) with title and text when a notification is suppressed because HandPeg is foreground.</summary>
    public static event Action<string, string>? Said;

    public static void Show(string title, string message)
    {
        if (IsActiveApplication())
        {
            Said?.Invoke(title, message);
            return;
        }

        try
        {
            lock (Gate)
            {
                var adding = _owner == IntPtr.Zero;
                if (adding)
                {
                    using (var self = Process.GetCurrentProcess())
                        _owner = self.MainWindowHandle;
                    if (_owner == IntPtr.Zero)
                        return;
                    _icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application;
                }

                var data = Data(NifIcon | NifTip | NifInfo);
                unsafe
                {
                    Copy("HandPeg", data.Tip, 128);
                    Copy(title, data.InfoTitle, 64);
                    Copy(message, data.Info, 256);
                }

                // Only in the notification area while there is something to say; never with the notification sound.
                data.InfoFlags = NiifUser | NiifNoSound;
                if (!ShellNotifyIcon(adding ? NimAdd : NimModify, ref data) && !adding)
                    ShellNotifyIcon(NimAdd, ref data);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or IOException)
        {
            // A notification that cannot be shown is not worth interrupting anything for.
        }
    }

    /// <summary>Takes the icon out of the notification area. Called as the application closes.</summary>
    public static void Dispose()
    {
        lock (Gate)
        {
            if (_owner != IntPtr.Zero)
            {
                var data = Data(0);
                ShellNotifyIcon(NimDelete, ref data);
            }

            _icon?.Dispose();
            (_icon, _owner) = (null, IntPtr.Zero);
        }
    }

    private static NotifyIconData Data(uint flags) => new()
    {
        Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconData>(),
        Window = _owner,
        Id = IconId,
        Flags = flags,
        Icon = _icon?.Handle ?? IntPtr.Zero,
        BalloonIcon = _icon?.Handle ?? IntPtr.Zero,
    };

    private static unsafe void Copy(string text, char* into, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < length; i++)
            into[i] = text[i];
        into[length] = '\0';
    }

    /// <summary>True when the foreground window belongs to this process (any thread).</summary>
    private static bool IsActiveApplication()
    {
        var front = GetForegroundWindow();
        return front != IntPtr.Zero && GetWindowThreadProcessId(front, out var process) != 0 && process == (uint)Environment.ProcessId;
    }
}

/// <summary>
/// Audio envelope: min/max sample (-1..1) per 1/80 s. Renderers query the range per pixel span. A mip pyramid (1:8, 1:64, 1:512) built
/// with the peaks answers any range in a few dozen reads. Mips are memory-only; .peaks files hold level 0.
/// </summary>
public sealed class WaveformData
{
    /// <summary>
    /// Silence ranges for Remove Dead Air from in-memory peaks (under 1 ms per hour of audio): runs where both peaks are below the level,
    /// at 12.5 ms pair resolution. A trailing silence closes at <paramref name="duration"/>.
    /// </summary>
    /// <param name="noiseDb">Silence threshold (dB).</param>
    /// <param name="minimumSeconds">Minimum silence length.</param>
    public List<(double Start, double End)> Silences(double noiseDb, double minimumSeconds, double duration)
    {
        var level = (float)Math.Pow(10, noiseDb / 20);
        var found = new List<(double Start, double End)>();
        var from = -1;
        var (lows, highs) = (_lows[0], _highs[0]);
        void Close(double at)
        {
            var start = from / PairsPerSecond;
            if (from >= 0 && at - start >= minimumSeconds)
                found.Add((start, at));
        }

        for (var i = 0; i < highs.Length; i++)
        {
            var silent = highs[i] < level && lows[i] > -level;
            if (silent && from < 0)
            {
                from = i;
            }
            else if (!silent && from >= 0)
            {
                Close(i / PairsPerSecond);
                from = -1;
            }
        }

        if (from >= 0)
            Close(Math.Max(duration, highs.Length / PairsPerSecond));
        return found;
    }

    /// <summary>Mip fold factor between levels (1:1, 1:8, 1:64, 1:512).</summary>
    public const int MipFactor = 8, MipLevels = 4;

    // Mip levels [level][i]: level 0 is the source; each level folds 8 below (the last pair folds the remainder; no padding).
    private readonly float[][] _lows, _highs;

    public WaveformData(float[] lows, float[] highs, Brush fill, double pairsPerSecond = Waveforms.PeaksPerSecond)
    {
        Fill = fill;
        PairsPerSecond = pairsPerSecond > 0 ? pairsPerSecond : Waveforms.PeaksPerSecond;
        (_lows, _highs) = (new float[MipLevels][], new float[MipLevels][]);
        (_lows[0], _highs[0]) = (lows, highs);
        for (var level = 1; level < MipLevels; level++)
        {
            var (belowLows, belowHighs) = (_lows[level - 1], _highs[level - 1]);
            var count = Math.Min(belowLows.Length, belowHighs.Length);
            var folded = (count + MipFactor - 1) / MipFactor;
            var (levelLows, levelHighs) = (new float[folded], new float[folded]);
            for (var j = 0; j < folded; j++)
            {
                var (start, end) = (j * MipFactor, Math.Min(j * MipFactor + MipFactor, count));
                var (low, high) = (belowLows[start], belowHighs[start]);
                for (var i = start + 1; i < end; i++)
                {
                    if (belowLows[i] < low)
                        low = belowLows[i];
                    if (belowHighs[i] > high)
                        high = belowHighs[i];
                }

                (levelLows[j], levelHighs[j]) = (low, high);
            }

            (_lows[level], _highs[level]) = (levelLows, levelHighs);
        }
    }

    public int Count => _highs[0].Length;

    /// <summary>The colour it is drawn in.</summary>
    public Brush Fill { get; }

    /// <summary>Pairs per second (80).</summary>
    public double PairsPerSecond { get; }

    /// <summary>Duration covered by the pairs (may differ from container duration).</summary>
    public double Seconds => Count / PairsPerSecond;

    /// <summary>How many pairs a level has.</summary>
    public int CountAt(int level) => _highs[level].Length;

    public ReadOnlySpan<float> LowsAt(int level) => _lows[level];

    public ReadOnlySpan<float> HighsAt(int level) => _highs[level];

    /// <summary>The level whose pairs are no wider than a column: 0 below 8 pairs a column, then 1, 2 and 3.</summary>
    public static int LevelFor(double pairsPerColumn) =>
        pairsPerColumn >= 512 ? 3 : pairsPerColumn >= 64 ? 2 : pairsPerColumn >= 8 ? 1 : 0;

    /// <summary>Clamp for combined peaks (<see cref="Combine"/>): 4x band, the maximum drawn height.</summary>
    public const float MostCombined = 4;

    /// <summary>
    /// Combines several envelopes (a video clip's audible Sub-Lanes): per pair, max of gain x high and min of gain x low, clamped to
    /// +/-<see cref="MostCombined"/>; silence where none plays. OffsetShift maps combined second 0 to each part (part = combined + shift,
    /// nearest pair). Parts with zero gain, no pairs or a different rate are skipped; null when none remain. A single unity-gain unshifted
    /// part returns itself (tile cache reuse). SIMD via System.Numerics (NEON/AVX) with a scalar tail producing identical results.
    /// </summary>
    public static WaveformData? Combine(IReadOnlyList<(WaveformData Data, double GainFactor, double OffsetShift)> parts)
    {
        // The parts kept (their gain, their shift in pairs, how many pairs they have), and how long the combined sound is.
        var kept = new List<(WaveformData Data, double Gain, int Shift, int Pairs)>(parts.Count);
        var count = 0;
        foreach (var (data, gain, offset) in parts)
        {
            var pairs = Math.Min(data._lows[0].Length, data._highs[0].Length);
            var pairsShift = offset * data.PairsPerSecond;
            if (pairs == 0 || !(gain > 0) || !double.IsFinite(gain) || !(Math.Abs(pairsShift) < 1 << 30)
                || (kept.Count > 0 && data.PairsPerSecond != kept[0].Data.PairsPerSecond))
                continue;

            var shift = (int)Math.Round(pairsShift);
            kept.Add((data, gain, shift, pairs));
            count = Math.Max(count, pairs - shift);
        }

        if (kept.Count == 0 || count <= 0)
            return null;
        if (kept.Count == 1 && Math.Abs(kept[0].Gain - 1) <= 1e-6 && kept[0].Shift == 0)
            return kept[0].Data;

        var (lows, highs) = (new float[count], new float[count]);
        foreach (var (data, gain, shift, pairs) in kept)
        {
            // The stretch of the combined sound the part plays in: from its start (or the combined sound's) to its end.
            var from = Math.Max(0, -shift);
            var end = Math.Min(count, pairs - shift);
            if (end <= from)
                continue;

            Widen(highs.AsSpan(from, end - from), data._highs[0].AsSpan(from + shift, end - from), (float)gain, upward: true);
            Widen(lows.AsSpan(from, end - from), data._lows[0].AsSpan(from + shift, end - from), (float)gain, upward: false);
        }

        return new WaveformData(lows, highs, kept[0].Data.Fill, kept[0].Data.PairsPerSecond);
    }

    // into = max (upward) or min of into and clamp(from x gain); vectorized then scalar tail with identical arithmetic.
    private static void Widen(Span<float> into, ReadOnlySpan<float> from, float gain, bool upward)
    {
        var length = Math.Min(into.Length, from.Length);
        var vectors = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Lanes>(into[..length]);
        var sources = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Lanes>(from[..length]);
        var (scale, least, most) = (new Lanes(gain), new Lanes(-MostCombined), new Lanes(MostCombined));
        for (var v = 0; v < vectors.Length; v++)
        {
            var scaled = System.Numerics.Vector.Min(System.Numerics.Vector.Max(sources[v] * scale, least), most);
            vectors[v] = upward ? System.Numerics.Vector.Max(vectors[v], scaled) : System.Numerics.Vector.Min(vectors[v], scaled);
        }

        for (var i = vectors.Length * Lanes.Count; i < length; i++)
        {
            var scaled = Math.Clamp(from[i] * gain, -MostCombined, MostCombined);
            into[i] = upward ? Math.Max(into[i], scaled) : Math.Min(into[i], scaled);
        }
    }

    /// <summary>The peaks between two fractions of its length (0 to 1), at least one of them.</summary>
    public WaveformData Slice(double from, double to)
    {
        var (lows, highs) = (_lows[0], _highs[0]);
        var first = Math.Clamp((int)(from * highs.Length), 0, highs.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * highs.Length), first + 1, highs.Length);
        return new WaveformData(lows[first..last], highs[first..last], Fill, PairsPerSecond);
    }

    /// <summary>Min/max between two length fractions (0-1).</summary>
    public (float Low, float High) Range(double from, double to)
    {
        var count = Count;
        if (count == 0 || to <= 0 || from >= 1)
            return (0, 0);

        var first = Math.Clamp((int)(from * count), 0, count - 1);
        var last = Math.Clamp((int)Math.Ceiling(to * count) - 1, first, count - 1);
        return RangeOf(first, last + 1);
    }

    /// <summary>
    /// Exact min/max over pairs [<paramref name="first"/>, <paramref name="end"/>): ragged ends at level 0, aligned interior via higher mip
    /// levels. (0, 0) for an empty range.
    /// </summary>
    public (float Low, float High) RangeOf(int first, int end)
    {
        first = Math.Max(first, 0);
        end = Math.Min(end, Count);
        if (end <= first)
            return (0, 0);

        var (low, high) = (float.PositiveInfinity, float.NegativeInfinity);
        for (var level = 0; ; level++)
        {
            var (lows, highs) = (_lows[level], _highs[level]);
            if (level == MipLevels - 1 || end - first <= 2 * MipFactor)
            {
                for (var i = first; i < end; i++)
                    Merge(lows[i], highs[i]);
                break;
            }

            // The start up to a whole fold, pair by pair.
            while (first % MipFactor != 0 && first < end)
            {
                Merge(lows[first], highs[first]);
                first++;
            }

            // The end down to a whole fold, unless it is the level's end: its last fold covers exactly what is left.
            var whole = end == lows.Length;
            if (!whole)
            {
                while (end % MipFactor != 0 && end > first)
                {
                    end--;
                    Merge(lows[end], highs[end]);
                }
            }

            if (end <= first)
                break;

            first /= MipFactor;
            end = whole ? (end + MipFactor - 1) / MipFactor : end / MipFactor;
        }

        return (low, high);

        void Merge(float pairLow, float pairHigh)
        {
            if (pairLow < low)
                low = pairLow;
            if (pairHigh > high)
                high = pairHigh;
        }
    }

    /// <summary>
    /// Per-column min/max over [<paramref name="fromSeconds"/>, <paramref name="toSeconds"/>]: one sweep at the finest mip level no wider
    /// than a column, binning folds by centre; empty columns take the fold at their centre or silence. Columns may include up to one fold
    /// of neighbouring signal but never omit their own.
    /// </summary>
    public void Envelope(double fromSeconds, double toSeconds, Span<float> lows, Span<float> highs)
    {
        var columns = Math.Min(lows.Length, highs.Length);
        if (columns == 0)
            return;

        if (Count == 0 || !(toSeconds > fromSeconds))
        {
            lows[..columns].Clear();
            highs[..columns].Clear();
            return;
        }

        var (pairsFrom, pairsTo) = (fromSeconds * PairsPerSecond, toSeconds * PairsPerSecond);
        var perColumn = (pairsTo - pairsFrom) / columns;
        var level = LevelFor(perColumn);
        var fold = 1 << (3 * level);
        var (levelLows, levelHighs) = (_lows[level], _highs[level]);
        lows[..columns].Fill(float.PositiveInfinity);
        highs[..columns].Fill(float.NegativeInfinity);

        var firstFold = (int)Math.Clamp(Math.Floor(pairsFrom / fold), 0, levelLows.Length);
        var endFold = (int)Math.Clamp(Math.Ceiling(pairsTo / fold), firstFold, levelLows.Length);
        for (var i = firstFold; i < endFold; i++)
        {
            var c = (int)Math.Clamp(((i + 0.5) * fold - pairsFrom) / perColumn, 0, columns - 1);
            if (levelLows[i] < lows[c])
                lows[c] = levelLows[i];
            if (levelHighs[i] > highs[c])
                highs[c] = levelHighs[i];
        }

        for (var c = 0; c < columns; c++)
        {
            if (!float.IsPositiveInfinity(lows[c]))
                continue;

            var under = (int)Math.Floor((pairsFrom + (c + 0.5) * perColumn) / fold);
            (lows[c], highs[c]) = under >= 0 && under < levelLows.Length ? (levelLows[under], levelHighs[under]) : (0f, 0f);
        }
    }
}
