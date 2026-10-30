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

// ----- Picture subtitles in the GPU preview (5.0-Verify) -----
// A picture subtitle track (a PGS .sup, a VobSub .idx/.sub; DVB and the others FFmpeg reads as well) is pictures, not
// words. libmpv drew the imported file over its picture itself; with it gone, the GPU preview draws them. The file is
// decoded once, in the background, by HandPeg's FFmpeg: its subtitle stream laid on a see-through canvas of the stream's
// own size (sub2video, which hands on a frame at every change: a picture coming, going), each frame written as a PNG
// down a pipe with its time on stderr (showinfo). Each picture is cut to what it covers and kept packed (Brotli) in
// memory, within a budget. The track the preview draws pairs the cues with them (the cue's picture is the one the file
// shows at the cue's own moment, so a cue retimed in the Subtitle Editor keeps its picture), and the scene plan hands a
// cue's picture to the worker as a still while the playhead is near it (GpuScenePlan.Pictures.cs). The export is as it
// was: picture subtitles are not written into it.

/// <summary>
/// One picture of a picture subtitle file: when the file shows it (on its own clock), where it is on the stream's canvas
/// (its pixels' bounding box), and its pixels, BGRA8 with straight alpha, kept packed. Immutable.
/// </summary>
public sealed class PictureSubtitlePicture
{
    private readonly byte[] _packed;

    private PictureSubtitlePicture(double start, double end, int x, int y, int width, int height, byte[] packed, long hash)
    {
        (Start, End, X, Y, Width, Height) = (start, end, x, y, width, height);
        (_packed, Hash) = (packed, hash);
    }

    /// <summary>When the file shows it: from (in) to (not), in the file's seconds; the end is infinity when nothing follows it.</summary>
    public double Start { get; }

    public double End { get; }

    /// <summary>Its place on the stream's canvas, in the canvas's pixels.</summary>
    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>What the GPU engine's resource table knows its still by: the file, the stream and the picture's place in it.</summary>
    public long Hash { get; }

    /// <summary>How much memory its packed pixels take.</summary>
    public int PackedBytes => _packed.Length;

    /// <summary>A picture from its pixels (<paramref name="bgra"/>: <paramref name="width"/> x <paramref name="height"/>, BGRA8, straight alpha).</summary>
    public static PictureSubtitlePicture Create(double start, double end, int x, int y, int width, int height, ReadOnlySpan<byte> bgra, long hash)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4)
            throw new ArgumentException("The pixels are not the picture's size.", nameof(bgra));
        return new PictureSubtitlePicture(start, end, x, y, width, height, Pack(bgra), hash);
    }

    /// <summary>The same picture at other times (its pixels are shared, never copied).</summary>
    internal PictureSubtitlePicture At(double start, double end) => new(start, end, X, Y, Width, Height, _packed, Hash);

    /// <summary>Its pixels unpacked: BGRA8, straight alpha, <see cref="Width"/> x <see cref="Height"/>.</summary>
    public byte[] Pixels()
    {
        var pixels = new byte[Width * Height * 4];
        if (!BrotliDecoder.TryDecompress(_packed, pixels, out var written) || written != pixels.Length)
            throw new InvalidDataException("A picture subtitle's packed pixels could not be unpacked.");
        return pixels;
    }

    // Brotli at quality 4: a subtitle picture is mostly runs of a few colours, which pack ten to a hundred times (1,500
    // busy 1,200 x 110 lines measured 38 MB, from 603 MB; quality 2 was a quarter larger, for a tenth less time).
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

/// <summary>A picture subtitle file decoded, or as far as it has been: the stream's canvas and its pictures in the order the file shows them.</summary>
public sealed class PictureSubtitleSet(string path, int canvasWidth, int canvasHeight, IReadOnlyList<PictureSubtitlePicture> pictures, bool isComplete, bool overBudget)
{
    public string Path { get; } = path;

    /// <summary>The canvas the stream's pictures are placed on (a PGS stream's video size, a VobSub's "size:"), in pixels.</summary>
    public int CanvasWidth { get; } = Math.Max(canvasWidth, 1);

    public int CanvasHeight { get; } = Math.Max(canvasHeight, 1);

    /// <summary>The pictures, by their start.</summary>
    public IReadOnlyList<PictureSubtitlePicture> Pictures { get; } = pictures;

    /// <summary>The whole file has been read (or as much of it as the budget keeps); false while more is coming.</summary>
    public bool IsComplete { get; } = isComplete;

    /// <summary>The pictures after a point were not kept: their packed pixels would have taken more than <see cref="PictureSubtitleDecoder.BudgetBytes"/>.</summary>
    public bool OverBudget { get; } = overBudget;

    public long PackedBytes { get; } = pictures.Sum(p => (long)p.PackedBytes);
}

/// <summary>A cue of the track the preview draws: when its picture is up on the sequence clock (from, in; to, not) and the picture.</summary>
public readonly record struct PictureSubtitleCue(double From, double To, PictureSubtitlePicture Picture);

/// <summary>
/// The subtitle track's pictures as the GPU preview draws them (<see cref="SequenceExportState.PictureSubtitles"/>): each
/// cue's picture and when it is up on the sequence clock, and the canvas they are placed on. Immutable.
/// </summary>
public sealed class PictureSubtitleTrack
{
    private PictureSubtitleTrack(int canvasWidth, int canvasHeight, PictureSubtitleCue[] cues) =>
        (CanvasWidth, CanvasHeight, CueArray) = (canvasWidth, canvasHeight, cues);

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    /// <summary>The cues, by their start.</summary>
    public IReadOnlyList<PictureSubtitleCue> Cues => CueArray;

    internal PictureSubtitleCue[] CueArray { get; }

    /// <summary>How far after a cue's own moment its picture may come and still be its picture (a VobSub's display delay), in seconds.</summary>
    public const double MatchToleranceSeconds = 0.5;

    /// <summary>
    /// The track of a decoded file's pictures, cue for cue: the <paramref name="fileCues"/> as they were read from the file,
    /// in its order, and the same cues by position as they are to be shown (retimed, the offset added). A cue's picture is
    /// the one the file shows at the cue's own moment there (or that comes within <see cref="MatchToleranceSeconds"/> of
    /// it); a cue with none (not decoded yet, or past the budget) is left out. Null when no cue has a picture.
    /// </summary>
    public static PictureSubtitleTrack? Match(PictureSubtitleSet set, IReadOnlyList<(double Start, double End)> fileCues, IReadOnlyList<(double Start, double End)> shownCues)
    {
        if (set.Pictures.Count == 0 || fileCues.Count != shownCues.Count)
            return null;

        var cues = new List<PictureSubtitleCue>(fileCues.Count);
        for (var i = 0; i < fileCues.Count; i++)
        {
            var (from, to) = shownCues[i];
            if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from)
                continue;
            if (PictureAt(set.Pictures, fileCues[i].Start) is { } picture)
                cues.Add(new PictureSubtitleCue(Math.Max(from, 0), to, picture));
        }

        cues.Sort(static (a, b) => a.From.CompareTo(b.From));
        return cues.Count == 0 ? null : new PictureSubtitleTrack(set.CanvasWidth, set.CanvasHeight, [.. cues]);
    }

    /// <summary>The picture up at a moment of the file, or failing that the first to come within the tolerance after it; null for none.</summary>
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

/// <summary>Reads the pictures of a picture subtitle file (or of a picture stream of a media file) with HandPeg's FFmpeg.</summary>
public static partial class PictureSubtitleDecoder
{
    /// <summary>
    /// How much memory the packed pictures of one file may take: a film's track (some 1,500 lines) packs to a few MB, and
    /// one made up of 1,500 busy, noisy lines to 38 MB, so only a pathological file reaches it. The pictures after that
    /// point are not kept, and the set says so.
    /// </summary>
    public const long BudgetBytes = 64L << 20;

    /// <summary>How often a decode in progress hands on what it has (seconds): the preview shows the start while the rest is read.</summary>
    private const double PublishSeconds = 1;

    [GeneratedRegex(@"\bn:\s*(\d+)\s+pts:\s*-?\d+\s+pts_time:(-?[0-9.]+(?:e[-+]?\d+)?)")]
    private static partial Regex FrameLineRegex();

    // A distinct frame of what sub2video handed on: a picture (cut to its pixels and packed, its times not set yet), or
    // the canvas cleared (null).
    private sealed record Change(int Frame, PictureSubtitlePicture? Picture);

    /// <summary>
    /// Decodes a picture subtitle file: the whole of it, in the background (call it on a background lane: it starts
    /// <paramref name="ffmpeg"/> through ProcessPipes, so the tool is in that lane's job). <paramref name="progress"/> is
    /// given what has been read so far about once a second, on the decoding thread. Null when nothing could be read (the
    /// reason is in the log).
    /// </summary>
    /// <param name="path">A .sup, a VobSub's .idx (its .sub beside it), or any file with a picture subtitle stream.</param>
    /// <param name="streamOrdinal">Which of the file's subtitle streams ("0:s:N"); 0 for a subtitle file's only one.</param>
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

        // -copyts keeps the file's own times (FFmpeg otherwise starts them at the first subtitle); every frame is passed on
        // at its time (passthrough), as a PNG (no prediction, the fastest packing: the pipe's cost is mostly the canvas's
        // empty rows); showinfo says when each frame is, without its checksums (which would cost more than the rest).
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
                    // Frames are numbered in order; one line lost would shift every time after it, so a gap is filled.
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

        // One PNG of the pipe taken in; false once the budget is spent (the decode stops there). sub2video hands on a picture
        // again just before what follows it, and the cleared canvas each time it is cleared: the same bytes are the same
        // frame, decoded once.
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
    /// The pictures of the changes whose times are known: each from its frame's time to the next change's (infinity for the
    /// last, and for one whose end is not known yet while the file is still being read).
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

    /// <summary>Reads the next PNG of the pipe whole (its signature, its chunks to IEND) into <paramref name="room"/> (grown as it needs); a length of 0 at the end.</summary>
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

    /// <summary>A frame of the canvas decoded and cut to the box its visible pixels fill (BGRA8, straight alpha); null for a cleared canvas.</summary>
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

                // Most rows are wholly see-through and empty: skipped at the speed of memory.
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
