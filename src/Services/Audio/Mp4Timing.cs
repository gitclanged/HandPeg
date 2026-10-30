using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Audio;

/// <summary>
/// When an MP4 / MOV track's first sample is presented, as FFmpeg's mov demuxer has it (5.0C): the edit list's leading
/// empty edits (the track starts that much later), its first edit's media time (what FFmpeg skips: an AAC encoder's
/// priming samples, a video's composition delay), and the first sample's composition offset. FFmpeg's first presented
/// pts of a track is emptyEdit + firstCts - mediaTime; its sound's decoded sample k is at emptyEdit + (k - mediaTime) / rate.
/// Media Foundation's Source Reader may or may not apply the same list, so the decoder counts samples from MF's own first
/// one and places them by these numbers, which come from the file and are the same whatever reads it.
/// </summary>
internal static class Mp4Timing
{
    /// <summary>One track: its handler ("soun", "vide"), its time scale, and the three numbers (seconds).</summary>
    public sealed record Track(string Handler, uint TimeScale, double EmptyEdit, double MediaTime, double FirstCts)
    {
        /// <summary>FFmpeg's presentation time of the track's first sample (its first frame, its first sound after the priming).</summary>
        public double FirstPts => EmptyEdit + FirstCts - MediaTime;
    }

    private static readonly ConcurrentDictionary<string, (long Length, long Written, Track[]? Tracks)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's tracks in their order, or null for a file that is not an MP4 / MOV (or cannot be read). Learnt once per file as it is.</summary>
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

        // The top level: an MP4 starts with ftyp (or a box of its own family); moov may be anywhere, at the end for most recorders.
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
                if (size > 256L << 20)
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

        // Leading empty edits (media time -1) delay the track; the first real one says where in the media it starts.
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

    // The first sample's composition offset (ctts), in the track's units; 0 without one (a fragmented file keeps it in its fragments).
    private static long FirstCompositionOffset(ReadOnlySpan<byte> minf)
    {
        foreach (var (type, box) in Children(minf))
        {
            if (type != "stbl")
                continue;
            foreach (var (inner, table) in Children(box))
            {
                if (inner == "ctts" && table.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(table[4..]) > 0)
                    return table[0] == 1 ? BinaryPrimitives.ReadInt32BigEndian(table[12..]) : BinaryPrimitives.ReadUInt32BigEndian(table[12..]);
            }
        }

        return 0;
    }

    private static string Type(ReadOnlySpan<byte> four) => string.Create(4, four.ToArray(), (chars, bytes) =>
    {
        for (var i = 0; i < 4; i++)
            chars[i] = (char)bytes[i];
    });

    /// <summary>The boxes inside a box's body (each: its type and its body).</summary>
    private static List<(string Type, byte[] Body)> ChildrenList(ReadOnlySpan<byte> data)
    {
        var list = new List<(string, byte[])>();
        var at = 0;
        while (at + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            var type = Type(data[(at + 4)..(at + 8)]);
            var body = 8;
            if (size == 1 && at + 16 <= data.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
                body = 16;
            }
            else if (size == 0)
            {
                size = data.Length - at;
            }

            if (size < body || at + size > data.Length)
                break;
            list.Add((type, data[(at + body)..(int)(at + size)].ToArray()));
            at += (int)size;
        }

        return list;
    }

    private static List<(string Type, byte[] Body)> Children(ReadOnlySpan<byte> data) => ChildrenList(data);
}
