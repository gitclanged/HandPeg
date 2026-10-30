using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Audio;

/// <summary>
/// What a Matroska / WebM file's track entries say about where each track's sound starts (5.0-Verify), as FFmpeg's demuxer
/// has it: a track's CodecDelay (an encoder's priming: 1024 samples of AAC in every track of an OBS recording, an Opus
/// encoder's pre-skip), which FFmpeg drops before the first sample it gives, and Windows' Matroska source does not
/// (measured: every AAC track came 1024 samples late). Read from the file's head (EBML, Segment, Tracks before the first
/// Cluster, as recorders write it), once per file as it is.
/// </summary>
internal static class MatroskaTiming
{
    /// <summary>One track entry: its Matroska type (1 video, 2 audio, 17 subtitles) and its CodecDelay in seconds.</summary>
    public sealed record Track(int Type, double CodecDelay)
    {
        public bool IsAudio => Type == 2;
    }

    // Where the track entries are looked for: the head of the file (SeekHead, Info and Tracks come before the clusters).
    private const int HeadBytes = 4 << 20;

    private const uint EbmlId = 0x1A45DFA3, SegmentId = 0x18538067, TracksId = 0x1654AE6B, ClusterId = 0x1F43B675;
    private const uint TrackEntryId = 0xAE, TrackTypeId = 0x83, CodecDelayId = 0x56AA;

    private static readonly ConcurrentDictionary<string, (long Length, long Written, Track[]? Tracks)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's track entries in their order (FFmpeg's order of streams), or null for a file that is not Matroska (or cannot be read).</summary>
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

    /// <summary>The CodecDelay (seconds) of a file's n-th audio track ("0:a:N"); 0 when it has none or is not Matroska.</summary>
    public static double CodecDelayOfAudio(string path, int stream) =>
        Read(path)?.Where(t => t.IsAudio).Skip(stream).FirstOrDefault()?.CodecDelay ?? 0;

    private static Track[]? Parse(ReadOnlySpan<byte> data)
    {
        // The EBML header first, then the Segment (its size may be unknown: a recording still being written).
        if (!Element(data, 0, out var id, out var body, out var size, out _) || id != EbmlId)
            return null;
        var at = (int)(body + size);
        if (!Element(data, at, out id, out body, out size, out var unknown) || id != SegmentId)
            return null;

        var end = unknown ? data.Length : (int)Math.Min(body + size, data.Length);
        at = body;
        while (Element(data, at, out id, out body, out size, out unknown) && !unknown)
        {
            if (id == ClusterId)
                return null;
            if (id == TracksId)
                return Tracks(data.Slice(body, (int)Math.Min(size, data.Length - body)));
            at = body + (int)size;
            if (at >= end)
                break;
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
                    inner = value + (int)length;
                }

                found.Add(new Track(type, delay / 1e9));
            }

            at = body + (int)size;
        }

        return [.. found];
    }

    // An element at an offset: its ID (with its length marker, as Matroska writes IDs), where its body starts, its size.
    private static bool Element(ReadOnlySpan<byte> data, int at, out uint id, out int body, out long size, out bool unknown)
    {
        (id, body, size, unknown) = (0, 0, 0, false);
        if (at < 0 || at >= data.Length || !Number(data, at, keepMarker: true, out var rawId, out var idLength, out _) || idLength > 4)
            return false;
        if (!Number(data, at + idLength, keepMarker: false, out var length, out var sizeLength, out unknown))
            return false;
        (id, body, size) = ((uint)rawId, at + idLength + sizeLength, unknown ? long.MaxValue : (long)length);
        return body <= data.Length && (unknown || size <= data.Length - body || id is SegmentId or ClusterId);
    }

    // EBML's variable-length number: its length from the first byte's leading zeros; all ones is an unknown size.
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
