using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CliWrap;

namespace HandPegApp.Services.Gpu;

// ----- Picture subtitles in the GPU preview -----
// Bitmap subtitle tracks (PGS .sup, VobSub .idx/.sub, DVB and other FFmpeg-readable formats) are decoded once in the background by FFmpeg:
// the stream is rendered on a transparent canvas of its own size (sub2video emits a frame at every change), each frame piped as a PNG with
// its time on stderr (showinfo). Each picture is cropped to its content and kept Brotli-packed in memory within a budget. The preview track
// pairs cues with pictures by the cue's own file time (so cues retimed in Subtitle Studio keep their picture), and the scene plan uploads a
// cue's picture as a still near the playhead (GpuScenePlan.Pictures.cs). Exports do not include picture subtitles.

/// <summary>
/// One picture of a bitmap subtitle file: its display interval (file clock), its bounding box on the stream canvas, and its BGRA8
/// straight-alpha pixels, kept packed. Immutable.
/// </summary>
public sealed class PictureSubtitlePicture
{
    private readonly byte[] _packed;

    private PictureSubtitlePicture(double start, double end, int x, int y, int width, int height, byte[] packed, long hash)
    {
        (Start, End, X, Y, Width, Height) = (start, end, x, y, width, height);
        (_packed, Hash) = (packed, hash);
    }

    /// <summary>Display interval [Start, End) in file seconds; End is infinity when nothing follows.</summary>
    public double Start { get; }

    public double End { get; }

    /// <summary>Position on the stream canvas, in canvas pixels.</summary>
    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Resource-table key for its still: file, stream and picture index.</summary>
    public long Hash { get; }

    /// <summary>Size of the packed pixels in bytes.</summary>
    public int PackedBytes => _packed.Length;

    /// <summary>Creates a picture from <paramref name="bgra"/>: <paramref name="width"/> x <paramref name="height"/> BGRA8 straight-alpha pixels.</summary>
    public static PictureSubtitlePicture Create(double start, double end, int x, int y, int width, int height, ReadOnlySpan<byte> bgra, long hash)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4)
            throw new ArgumentException("The pixels are not the picture's size.", nameof(bgra));
        return new PictureSubtitlePicture(start, end, x, y, width, height, Pack(bgra), hash);
    }

    /// <summary>The same picture with other times (pixels shared, never copied).</summary>
    internal PictureSubtitlePicture At(double start, double end) => new(start, end, X, Y, Width, Height, _packed, Hash);

    /// <summary>Unpacked pixels: BGRA8 straight alpha, <see cref="Width"/> x <see cref="Height"/>.</summary>
    public byte[] Pixels()
    {
        var pixels = new byte[Width * Height * 4];
        if (!BrotliDecoder.TryDecompress(_packed, pixels, out var written) || written != pixels.Length)
            throw new InvalidDataException("A picture subtitle's packed pixels could not be unpacked.");
        return pixels;
    }

    // Brotli quality 4: subtitle pictures are mostly runs of a few colours and pack 10-100x (1,500 busy 1,200x110 lines: 603 MB to 38 MB;
    // quality 2 was 25 % larger for 10 % less time).
    private static byte[] Pack(ReadOnlySpan<byte> pixels)
    {
        var room = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(pixels.Length));
        try
        {
            if (!BrotliEncoder.TryCompress(pixels, room, out var written, quality: 4, window: 22))
                throw new InvalidDataException("A picture subtitle's pixels could not be packed.");
            return room.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(room);
        }
    }
}

/// <summary>A decoded (or partially decoded) bitmap subtitle file: the stream canvas and its pictures in display order.</summary>
public sealed class PictureSubtitleSet(string path, int canvasWidth, int canvasHeight, IReadOnlyList<PictureSubtitlePicture> pictures, bool isComplete, bool overBudget)
{
    public string Path { get; } = path;

    /// <summary>Canvas the pictures are placed on (PGS video size, VobSub "size:"), in pixels.</summary>
    public int CanvasWidth { get; } = Math.Max(canvasWidth, 1);

    public int CanvasHeight { get; } = Math.Max(canvasHeight, 1);

    /// <summary>Pictures sorted by start.</summary>
    public IReadOnlyList<PictureSubtitlePicture> Pictures { get; } = pictures;

    /// <summary>True once the whole file (or as much as the budget keeps) has been read; false while decoding continues.</summary>
    public bool IsComplete { get; } = isComplete;

    /// <summary>True when later pictures were dropped because their packed size would exceed <see cref="PictureSubtitleDecoder.BudgetBytes"/>.</summary>
    public bool OverBudget { get; } = overBudget;

    public long PackedBytes { get; } = pictures.Sum(p => (long)p.PackedBytes);
}

/// <summary>
/// A preview track cue: display interval [From, To) on the sequence clock, its picture, and its clip when known (burned subtitles: the clip
/// that placed this piece; empty: the scene plan finds the visible clip of the stream's file).
/// </summary>
public readonly record struct PictureSubtitleCue(double From, double To, PictureSubtitlePicture Picture, Guid ClipId = default);

/// <summary>
/// Picture-subtitle track as the GPU preview draws it (<see cref="SequenceExportState.PictureSubtitles"/>): each cue's picture and sequence
/// interval, plus the canvas they are placed on. Immutable.
/// </summary>
public sealed class PictureSubtitleTrack
{
    private PictureSubtitleTrack(int canvasWidth, int canvasHeight, PictureSubtitleCue[] cues) =>
        (CanvasWidth, CanvasHeight, CueArray) = (canvasWidth, canvasHeight, cues);

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    /// <summary>Cues sorted by start.</summary>
    public IReadOnlyList<PictureSubtitleCue> Cues => CueArray;

    internal PictureSubtitleCue[] CueArray { get; }

    /// <summary>How late a picture may start after its cue's time and still match it (VobSub display delay), seconds.</summary>
    public const double MatchToleranceSeconds = 0.5;

    /// <summary>
    /// Builds the track by pairing cues with decoded pictures: <paramref name="fileCues"/> as read from the file and the same cues by position
    /// as shown (retimed, offset applied). A cue's picture is the one the file shows at the cue's file time (or starting within
    /// <see cref="MatchToleranceSeconds"/> after it); cues without one (not decoded yet, or over budget) are skipped. <paramref name="shownClips"/>,
    /// when given, holds each shown cue's clip by position (<see cref="PictureSubtitleCue.ClipId"/>). Null when no cue has a picture.
    /// </summary>
    public static PictureSubtitleTrack? Match(PictureSubtitleSet set, IReadOnlyList<(double Start, double End)> fileCues, IReadOnlyList<(double Start, double End)> shownCues,
        IReadOnlyList<Guid>? shownClips = null)
    {
        if (set.Pictures.Count == 0 || fileCues.Count != shownCues.Count || (shownClips is not null && shownClips.Count != shownCues.Count))
            return null;

        var cues = new List<PictureSubtitleCue>(fileCues.Count);
        for (var i = 0; i < fileCues.Count; i++)
        {
            var (from, to) = shownCues[i];
            if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from)
                continue;
            if (PictureAt(set.Pictures, fileCues[i].Start) is { } picture)
                cues.Add(new PictureSubtitleCue(Math.Max(from, 0), to, picture, shownClips?[i] ?? Guid.Empty));
        }

        cues.Sort(static (a, b) => a.From.CompareTo(b.From));
        return cues.Count == 0 ? null : new PictureSubtitleTrack(set.CanvasWidth, set.CanvasHeight, [.. cues]);
    }

    /// <summary>Picture visible at file time <paramref name="t"/>, else the first starting within the tolerance after it; null when none.</summary>
    private static PictureSubtitlePicture? PictureAt(IReadOnlyList<PictureSubtitlePicture> pictures, double t)
    {
        const double Epsilon = 1e-4;
        int low = 0, high = pictures.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (pictures[middle].Start <= t + Epsilon)
                (found, low) = (middle, middle + 1);
            else
                high = middle - 1;
        }

        if (found >= 0 && pictures[found].End > t + Epsilon)
            return pictures[found];
        return found + 1 < pictures.Count && pictures[found + 1].Start <= t + MatchToleranceSeconds ? pictures[found + 1] : null;
    }
}

/// <summary>Decodes the pictures of a bitmap subtitle file (or a picture stream in a media file) with HandPeg's FFmpeg.</summary>
public static partial class PictureSubtitleDecoder
{
    /// <summary>
    /// Memory budget for one file's packed pictures: a film track (~1,500 lines) packs to a few MB and 1,500 busy, noisy lines to 38 MB, so
    /// only pathological files reach it. Later pictures are dropped and the set reports it.
    /// </summary>
    public const long BudgetBytes = 64L << 20;

    /// <summary>Interval at which an in-progress decode publishes partial results (seconds), so the preview shows the start early.</summary>
    private const double PublishSeconds = 1;

    [GeneratedRegex(@"\bn:\s*(\d+)\s+pts:\s*-?\d+\s+pts_time:(-?[0-9.]+(?:e[-+]?\d+)?)")]
    private static partial Regex FrameLineRegex();

    // A distinct sub2video frame: a picture (cropped and packed, times not yet assigned) or a cleared canvas (null).
    private sealed record Change(int Frame, PictureSubtitlePicture? Picture);

    /// <summary>
    /// Decodes a bitmap subtitle file fully in the background (call on a background lane: it starts <paramref name="ffmpeg"/> via ProcessPipes,
    /// so the tool joins that lane's job). <paramref name="progress"/> receives partial results about once a second on the decoding thread.
    /// Null when nothing could be read (reason logged).
    /// </summary>
    /// <param name="path">A .sup, a VobSub .idx (with its .sub alongside), or any file with a bitmap subtitle stream.</param>
    /// <param name="streamOrdinal">Subtitle stream index ("0:s:N"); 0 for a subtitle file's only stream.</param>
    public static async Task<PictureSubtitleSet?> DecodeAsync(string path, int streamOrdinal, string ffmpeg, Action<PictureSubtitleSet>? progress, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || !File.Exists(ffmpeg))
        {
            AppLog.Write($"Picture subtitles: {(info.Exists ? "FFmpeg is not installed" : "the file is not there")}, so they are not drawn in the preview.");
            return null;
        }

        var identity = default(GpuHash).Add("picture-subtitle").Add(info.FullName).Add(info.Length).Add(info.LastWriteTimeUtc.Ticks).Add(streamOrdinal);
        var stream = Math.Max(streamOrdinal, 0).ToString(CultureInfo.InvariantCulture);

        // -copyts keeps the file's own times (FFmpeg otherwise rebases to the first subtitle); every frame passes through at its time as a
        // PNG (no prediction, fastest compression: pipe cost is dominated by empty canvas rows); showinfo reports frame times without
        // checksums (which would cost more than everything else).
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-copyts", "-i", path,
            "-filter_complex", $"[0:s:{stream}]showinfo=checksum=0[pictures]", "-map", "[pictures]",
            "-fps_mode", "passthrough", "-c:v", "png", "-pred", "none", "-compression_level", "1", "-f", "image2pipe", "pipe:1",
        ];

        var times = new List<double>();
        var said = new Queue<string>();
        var changes = new List<Change>();
        var (canvasWidth, canvasHeight, packed, overBudget, published) = (0, 0, 0L, false, System.Diagnostics.Stopwatch.StartNew());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnLine(string line)
        {
            if (line.Contains("showinfo", StringComparison.Ordinal) && FrameLineRegex().Match(line) is { Success: true } match)
            {
                var (frame, at) = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                lock (times)
                {
                    // Frames are numbered sequentially; a lost line would shift every later time, so gaps are filled with NaN.
                    while (times.Count < frame)
                        times.Add(double.NaN);
                    if (times.Count == frame)
                        times.Add(at);
                }

                return;
            }

            lock (said)
            {
                said.Enqueue(line);
                if (said.Count > 12)
                    said.Dequeue();
            }
        }

        PictureSubtitleSet Snapshot(bool complete)
        {
            lock (changes)
            {
                lock (times)
                    return new PictureSubtitleSet(path, canvasWidth, canvasHeight, Pictures(changes, times), complete, overBudget);
            }
        }

        // Takes one PNG from the pipe; false once the budget is exhausted (decoding stops). sub2video repeats a picture just before the next
        // one and the cleared canvas at each clear: identical bytes are the same frame and are decoded once.
        byte[]? previous = null, blank = null;
        bool Take(ReadOnlySpan<byte> bytes, int frame)
        {
            if (previous is not null && bytes.SequenceEqual(previous))
                return true;
            previous = bytes.ToArray();
            if (blank is not null && bytes.SequenceEqual(blank))
            {
                lock (changes)
                    changes.Add(new Change(frame, null));
                return true;
            }

            var cut = Decode(bytes, out var width, out var height);
            var picture = cut is { } c ? PictureSubtitlePicture.Create(0, 0, c.X, c.Y, c.Width, c.Height, c.Pixels, identity.Add(frame).Value) : null;
            lock (changes)
            {
                (canvasWidth, canvasHeight) = canvasWidth == 0 ? (width, height) : (canvasWidth, canvasHeight);
                if (picture is null)
                {
                    blank = previous;
                }
                else if (packed + picture.PackedBytes > BudgetBytes)
                {
                    overBudget = true;
                    changes.Add(new Change(frame, null));
                    AppLog.Write($"Picture subtitles: {Path.GetFileName(path)} has more pictures than the preview keeps ({BudgetBytes >> 20} MB packed): the later ones are not drawn.");
                    return false;
                }

                packed += picture?.PackedBytes ?? 0;
                changes.Add(new Change(frame, picture));
            }

            if (progress is not null && published.Elapsed.TotalSeconds >= PublishSeconds)
            {
                progress(Snapshot(complete: false));
                published.Restart();
            }

            return true;
        }

        var reader = PipeTarget.Create(async (output, token) =>
        {
            using var buffered = new BufferedStream(output, 1 << 16);
            var png = new byte[1 << 16];
            for (var frame = 0; ; frame++)
            {
                (png, var length) = await ReadPngAsync(buffered, png, token);
                if (length == 0)
                    break;
                if (!Take(png.AsSpan(0, length), frame))
                {
                    stop.Cancel();
                    break;
                }
            }
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int exitCode;
        try
        {
            var result = await ProcessPipes.RunAsync(Cli.Wrap(ffmpeg)
                .WithArguments(arguments)
                .WithStandardOutputPipe(reader)
                .WithStandardErrorPipe(ProcessPipes.Lines(OnLine))
                .WithValidation(CommandResultValidation.None), stop.Token);
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException) when (overBudget && !cancellationToken.IsCancellationRequested)
        {
            exitCode = 0;
        }

        var set = Snapshot(complete: true);
        if (exitCode != 0 && set.Pictures.Count == 0)
        {
            string tail;
            lock (said)
                tail = string.Join(" | ", said);
            AppLog.Write($"Picture subtitles: FFmpeg could not read {Path.GetFileName(path)} (exit code {exitCode}): {tail}");
            return null;
        }

        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Picture subtitles: {set.Pictures.Count} picture(s) of {Path.GetFileName(path)} read on a {set.CanvasWidth} x {set.CanvasHeight} canvas in {clock.ElapsedMilliseconds} ms ({set.PackedBytes / 1024} KB packed)."));
        return set;
    }

    /// <summary>
    /// Pictures of the changes whose times are known, each lasting until the next change (infinity for the last, and for one whose end is
    /// still unknown while reading).
    /// </summary>
    private static List<PictureSubtitlePicture> Pictures(List<Change> changes, List<double> times)
    {
        double TimeOf(int frame) => frame < times.Count ? times[frame] : double.NaN;
        var pictures = new List<PictureSubtitlePicture>();
        for (var i = 0; i < changes.Count; i++)
        {
            var start = TimeOf(changes[i].Frame);
            if (changes[i].Picture is not { } picture || !double.IsFinite(start))
                continue;

            var end = i + 1 < changes.Count ? TimeOf(changes[i + 1].Frame) : double.PositiveInfinity;
            pictures.Add(picture.At(start, double.IsFinite(end) && end > start ? end : double.PositiveInfinity));
        }

        pictures.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return pictures;
    }

    /// <summary>Reads the next complete PNG (signature, chunks through IEND) into <paramref name="room"/>, growing it; length 0 at end of stream.</summary>
    private static async Task<(byte[] Room, int Length)> ReadPngAsync(Stream input, byte[] room, CancellationToken token)
    {
        var at = 0;
        async Task<bool> Take(int count)
        {
            if (at + count > room.Length)
                Array.Resize(ref room, Math.Max(room.Length * 2, at + count));
            var read = 0;
            while (read < count)
            {
                var got = await input.ReadAsync(room.AsMemory(at + read, count - read), token);
                if (got == 0)
                    return false;
                read += got;
            }

            at += count;
            return true;
        }

        if (!await Take(8))
            return (room, 0);
        if (BinaryPrimitives.ReadUInt64BigEndian(room) != 0x89504E470D0A1A0AUL)
            throw new InvalidDataException("FFmpeg's picture pipe did not hold a PNG.");

        while (true)
        {
            if (!await Take(8))
                throw new InvalidDataException("FFmpeg's picture pipe ended inside a PNG.");
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(room.AsSpan(at - 8));
            var kind = BinaryPrimitives.ReadUInt32BigEndian(room.AsSpan(at - 4));
            if (length < 0 || !await Take(length + 4))
                throw new InvalidDataException("FFmpeg's picture pipe ended inside a PNG.");
            if (kind == 0x49454E44) // IEND
                return (room, at);
        }
    }

    /// <summary>Decodes a canvas frame and crops it to the bounding box of its visible pixels (BGRA8, straight alpha); null for a cleared canvas.</summary>
    private static (int X, int Y, int Width, int Height, byte[] Pixels)? Decode(ReadOnlySpan<byte> png, out int width, out int height)
    {
        BitmapSource bitmap;
        using (var stream = new MemoryStream(png.ToArray(), writable: false))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            bitmap = decoder.Frames[0];
            if (bitmap.Format != PixelFormats.Bgra32)
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        }

        (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
        var stride = width * 4;
        var canvas = ArrayPool<byte>.Shared.Rent(stride * height);
        try
        {
            bitmap.CopyPixels(canvas, stride, 0);
            var pixels = MemoryMarshal.Cast<byte, uint>(canvas.AsSpan(0, stride * height));
            var (left, top, right, bottom) = (width, height, -1, -1);
            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * width, width);

                // Most rows are fully transparent: skipped at memory speed.
                if (row.IndexOfAnyExcept(0u) < 0)
                    continue;
                var first = -1;
                for (var x = 0; x < width; x++)
                {
                    if ((row[x] & 0xFF000000u) != 0)
                    {
                        first = x;
                        break;
                    }
                }

                if (first < 0)
                    continue;
                var last = width - 1;
                while ((row[last] & 0xFF000000u) == 0)
                    last--;
                (left, right) = (Math.Min(left, first), Math.Max(right, last));
                (top, bottom) = (Math.Min(top, y), y);
            }

            if (right < left)
                return null;

            var (cutWidth, cutHeight) = (right - left + 1, bottom - top + 1);
            var cut = new byte[cutWidth * cutHeight * 4];
            for (var y = 0; y < cutHeight; y++)
                canvas.AsSpan((top + y) * stride + left * 4, cutWidth * 4).CopyTo(cut.AsSpan(y * cutWidth * 4));
            return (left, top, cutWidth, cutHeight, cut);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(canvas);
        }
    }
}
