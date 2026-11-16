using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Audio;

/// <summary>
/// Reads Matroska/WebM track entries for each track's CodecDelay (encoder priming: 1024 AAC samples in OBS recordings, Opus pre-skip).
/// FFmpeg drops it before the first sample; the Windows Matroska source does not (measured: AAC tracks arrive 1024 samples late).
/// Parses only the file head (EBML, Segment, Tracks before the first Cluster); cached per path, length and write time.
/// </summary>
internal static class MatroskaTiming
{
    /// <summary>A track entry: Matroska type (1 video, 2 audio, 17 subtitles) and CodecDelay in seconds.</summary>
    public sealed record Track(int Type, double CodecDelay)
    {
        public bool IsAudio => Type == 2;
    }

    // Bytes read from the file head, where SeekHead, Info and Tracks precede the clusters.
    private const int HeadBytes = 4 << 20;

    private const uint EbmlId = 0x1A45DFA3, SegmentId = 0x18538067, TracksId = 0x1654AE6B, ClusterId = 0x1F43B675;
    private const uint TrackEntryId = 0xAE, TrackTypeId = 0x83, CodecDelayId = 0x56AA;

    private static readonly ConcurrentDictionary<string, (long Length, long Written, Track[]? Tracks)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Track entries in file order (FFmpeg stream order); null when not Matroska or unreadable.</summary>
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
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var head = new byte[(int)Math.Min(file.Length, HeadBytes)];
            file.ReadExactly(head);
            tracks = Parse(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            AppLog.Write("Sound engine: the Matroska track entries of a file could not be read", ex);
        }

        if (Known.Count > 256)
            Known.Clear();
        Known[path] = (length, written, tracks);
        return tracks;
    }

    /// <summary>CodecDelay in seconds of the file's n-th audio track ("0:a:N"); 0 when absent or not Matroska.</summary>
    public static double CodecDelayOfAudio(string path, int stream) =>
        Read(path)?.Where(t => t.IsAudio).Skip(stream).FirstOrDefault()?.CodecDelay ?? 0;

    private static Track[]? Parse(ReadOnlySpan<byte> data)
    {
        // EBML header, then the Segment (size may be unknown while a recording is still being written).
        if (!Element(data, 0, out var id, out var body, out var size, out _) || id != EbmlId)
            return null;
        var at = (int)(body + size);
        if (!Element(data, at, out id, out body, out size, out var unknown, oversizeOk: true) || id != SegmentId)
            return null;

        var end = unknown ? data.Length : (int)Math.Min(body + size, data.Length);
        at = body;
        while (Element(data, at, out id, out body, out size, out unknown) && !unknown)
        {
            if (id == ClusterId)
                return null;
            if (id == TracksId)
                return Tracks(data.Slice(body, (int)Math.Min(size, data.Length - body)));

            // Advance in 64-bit arithmetic so a crafted size can neither loop on one element nor wrap past the end.
            var next = (long)body + size;
            if (next <= at || next >= end)
                break;
            at = (int)next;
        }

        return null;
    }

    private static Track[] Tracks(ReadOnlySpan<byte> tracks)
    {
        var found = new List<Track>();
        var at = 0;
        while (Element(tracks, at, out var id, out var body, out var size, out var unknown) && !unknown)
        {
            if (id == TrackEntryId)
            {
                var entry = tracks.Slice(body, (int)Math.Min(size, tracks.Length - body));
                var (type, delay) = (0, 0L);
                var inner = 0;
                while (Element(entry, inner, out var child, out var value, out var length, out var open) && !open)
                {
                    if (child == TrackTypeId)
                        type = (int)Unsigned(entry.Slice(value, (int)length));
                    else if (child == CodecDelayId)
                        delay = (long)Math.Min(Unsigned(entry.Slice(value, (int)length)), long.MaxValue);
                    var step = (long)value + length;
                    if (step <= inner || step > entry.Length)
                        break;
                    inner = (int)step;
                }

                found.Add(new Track(type, delay / 1e9));
            }

            var next = (long)body + size;
            if (next <= at || next > tracks.Length)
                break;
            at = (int)next;
        }

        return [.. found];
    }

    // Parses an element header: ID (length marker kept, as Matroska IDs are written), body offset and size. Elements larger than
    // the data are rejected unless oversizeOk (the Segment, whose declared size usually exceeds the head read).
    private static bool Element(ReadOnlySpan<byte> data, int at, out uint id, out int body, out long size, out bool unknown, bool oversizeOk = false)
    {
        (id, body, size, unknown) = (0, 0, 0, false);
        if (at < 0 || at >= data.Length || !Number(data, at, keepMarker: true, out var rawId, out var idLength, out _) || idLength > 4)
            return false;
        if (!Number(data, at + idLength, keepMarker: false, out var length, out var sizeLength, out unknown))
            return false;
        (id, body, size) = ((uint)rawId, at + idLength + sizeLength, unknown ? long.MaxValue : (long)length);
        return body <= data.Length && (unknown || size <= data.Length - body || oversizeOk);
    }

    // EBML variable-length integer: length from the first byte's leading zeros; all value bits set means unknown size.
    private static bool Number(ReadOnlySpan<byte> data, int at, bool keepMarker, out ulong value, out int length, out bool allOnes)
    {
        (value, length, allOnes) = (0, 0, false);
        if (at >= data.Length || data[at] == 0)
            return false;
        length = System.Numerics.BitOperations.LeadingZeroCount((uint)data[at]) - 23;
        if (at + length > data.Length)
            return false;
        value = keepMarker ? data[at] : (ulong)(data[at] & (0xFF >> length));
        var ones = value == (ulong)(0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            value = (value << 8) | data[at + i];
            ones &= data[at + i] == 0xFF;
        }

        allOnes = !keepMarker && ones;
        return true;
    }

    private static ulong Unsigned(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 8)
            return 0;
        Span<byte> wide = stackalloc byte[8];
        bytes.CopyTo(wide[(8 - bytes.Length)..]);
        return BinaryPrimitives.ReadUInt64BigEndian(wide);
    }
}
