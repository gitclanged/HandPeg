using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Audio;

/// <summary>
/// MP4/MOV track presentation timing as FFmpeg's mov demuxer computes it: leading empty edits (track delay), the first edit's media
/// time (skipped samples: AAC priming, video composition delay) and the first sample's composition offset. FFmpeg's first pts is
/// emptyEdit + firstCts - mediaTime; decoded audio sample k is at emptyEdit + (k - mediaTime) / rate. The MF Source Reader may or may
/// not apply the edit list, so the decoder counts from MF's first sample and places samples by these file-derived values.
/// </summary>
internal static class Mp4Timing
{
    /// <summary>One track: handler ("soun", "vide"), time scale, and the three timing values in seconds.</summary>
    public sealed record Track(string Handler, uint TimeScale, double EmptyEdit, double MediaTime, double FirstCts)
    {
        /// <summary>FFmpeg presentation time of the track's first sample (first frame, or first sound after priming).</summary>
        public double FirstPts => EmptyEdit + FirstCts - MediaTime;
    }

    private static readonly ConcurrentDictionary<string, (long Length, long Written, Track[]? Tracks)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks in file order; null when not MP4/MOV or unreadable. Cached per path, length and write time.</summary>
    public static Track[]? Read(string path)
    {
        long length, written;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            (length, written) = (info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (Known.TryGetValue(path, out var kept) && kept.Length == length && kept.Written == written)
            return kept.Tracks;

        Track[]? tracks = null;
        try
        {
            tracks = ReadFresh(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            AppLog.Write("Sound engine: the MP4 timing of a file could not be read", ex);
        }

        if (Known.Count > 256)
            Known.Clear();
        Known[path] = (length, written, tracks);
        return tracks;
    }

    private static Track[]? ReadFresh(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[16];

        // Top level: an MP4 starts with ftyp (or a related box type); moov may be anywhere, usually at the end for recorders.
        long at = 0;
        var first = true;
        while (at + 8 <= file.Length)
        {
            file.Position = at;
            if (file.Read(header[..8]) < 8)
                return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Type(header[4..8]);
            var body = 8;
            if (size == 1)
            {
                if (file.Read(header[8..16]) < 8)
                    return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                body = 16;
            }
            else if (size == 0)
            {
                size = file.Length - at;
            }

            if (first && type is not ("ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "pnot" or "uuid"))
                return null;
            first = false;
            if (size < body)
                return null;

            if (type == "moov")
            {
                // Bounded by both the file size and 256 MB: an oversized moov is rejected before allocating.
                if (size > 256L << 20 || at + size > file.Length)
                    return null;
                var moov = new byte[size - body];
                file.Position = at + body;
                file.ReadExactly(moov);
                return Movie(moov);
            }

            at += size;
        }

        return null;
    }

    private static Track[] Movie(ReadOnlySpan<byte> moov)
    {
        uint movieScale = 1000;
        var tracks = new List<Track>();
        foreach (var (type, box) in Children(moov))
        {
            if (type == "mvhd" && box.Length >= 24)
                movieScale = box[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(box[20..]) : BinaryPrimitives.ReadUInt32BigEndian(box[12..]);
        }

        foreach (var (type, box) in Children(moov))
        {
            if (type == "trak" && Trak(box, Math.Max(movieScale, 1)) is { } track)
                tracks.Add(track);
        }

        return [.. tracks];
    }

    private static Track? Trak(ReadOnlySpan<byte> trak, uint movieScale)
    {
        var (handler, scale, emptyEdit, mediaTime, firstCts) = ("", 0u, 0.0, 0L, 0L);
        var edits = ReadOnlySpan<byte>.Empty;
        foreach (var (type, box) in Children(trak))
        {
            if (type == "edts")
            {
                foreach (var (inner, list) in Children(box))
                {
                    if (inner == "elst")
                        edits = list;
                }
            }
            else if (type == "mdia")
            {
                foreach (var (inner, part) in Children(box))
                {
                    if (inner == "mdhd" && part.Length >= 24)
                        scale = part[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(part[20..]) : BinaryPrimitives.ReadUInt32BigEndian(part[12..]);
                    else if (inner == "hdlr" && part.Length >= 12)
                        handler = Type(part[8..12]);
                    else if (inner == "minf")
                        firstCts = FirstCompositionOffset(part);
                }
            }
        }

        if (scale == 0 || handler.Length == 0)
            return null;

        // Leading empty edits (media time -1) delay the track; the first real edit gives the media start.
        if (edits.Length >= 8)
        {
            var version = edits[0];
            var count = BinaryPrimitives.ReadUInt32BigEndian(edits[4..]);
            var entry = version == 1 ? 20 : 12;
            for (var i = 0; i < count && 8 + (i + 1) * entry <= edits.Length; i++)
            {
                var e = edits[(8 + i * entry)..];
                var duration = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(e) : BinaryPrimitives.ReadUInt32BigEndian(e);
                var media = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(e[8..]) : BinaryPrimitives.ReadInt32BigEndian(e[4..]);
                if (media == -1)
                {
                    emptyEdit += (double)duration / movieScale;
                    continue;
                }

                mediaTime = media;
                break;
            }
        }

        return new Track(handler, scale, emptyEdit, (double)mediaTime / scale, (double)firstCts / scale);
    }

    // First sample's composition offset (ctts) in track units; 0 when absent (fragmented files keep it in the fragments).
    private static long FirstCompositionOffset(ReadOnlySpan<byte> minf)
    {
        foreach (var (type, box) in Children(minf))
        {
            if (type != "stbl")
                continue;
            foreach (var (inner, table) in Children(box))
            {
                if (inner == "ctts" && table.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(table[4..]) > 0)
                    return BinaryPrimitives.ReadInt32BigEndian(table[12..]);
            }
        }

        return 0;
    }

    private static string Type(ReadOnlySpan<byte> four) => string.Create(4, four.ToArray(), (chars, bytes) =>
    {
        for (var i = 0; i < 4; i++)
            chars[i] = (char)bytes[i];
    });

    /// <summary>Child boxes of a box body (type and body) as zero-copy slices of the parent.</summary>
    private static Boxes Children(ReadOnlySpan<byte> data) => new(data);

    private readonly ref struct Boxes(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public BoxWalk GetEnumerator() => new(_data);
    }

    private ref struct BoxWalk(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public Box Current { get; private set; }

        public bool MoveNext()
        {
            if (_at + 8 > _data.Length)
                return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(_data[_at..]);
            var type = Type(_data[(_at + 4)..(_at + 8)]);
            var body = 8;
            if (size == 1 && _at + 16 <= _data.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(_data[(_at + 8)..]);
                body = 16;
            }
            else if (size == 0)
            {
                size = _data.Length - _at;
            }

            // A child larger than its parent (or smaller than its header) ends the walk; sizes compare in 64 bits.
            if (size < body || size > _data.Length - _at)
                return false;
            Current = new Box(type, _data[(_at + body)..(int)(_at + size)]);
            _at += (int)size;
            return true;
        }
    }

    private readonly ref struct Box(string type, ReadOnlySpan<byte> body)
    {
        public string Type { get; } = type;

        public ReadOnlySpan<byte> Body { get; } = body;

        public void Deconstruct(out string type, out ReadOnlySpan<byte> body)
        {
            type = Type;
            body = Body;
        }
    }
}
