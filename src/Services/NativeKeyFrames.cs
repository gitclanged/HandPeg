using System.Buffers.Binary;
using System.IO;

namespace HandPegApp.Services;

/// <summary>
/// A file's key frames read from its own index, in C#, with nothing decoded and no ffprobe (5.4.1-M4): an MP4 / MOV's first video
/// track's sync samples (stss; every sample without one) placed by its stts, ctts and edit list, and an MKV / WebM's Cues for its first
/// video track. The times are FFmpeg's packet pts_time, as <see cref="FfmpegRunner.GetIFramesAsync"/> lists them from the packets'
/// flags, ascending. Null when the file is neither, has no index (a fragmented MP4, an MKV still being written without Cues) or cannot
/// be read: ffprobe then lists them as before. Only the boxes and elements wanted are read, never the media.
/// </summary>
internal static class NativeKeyFrames
{
    // The most of one index table read (bytes): a sample table this big is a recording of days.
    private const long MostTable = 64L << 20;

    // The most key frames listed (a day of every frame a key frame at 194 fps); a table claiming more is not believed: ffprobe lists them.
    private const long MostSamples = 1L << 24;

    /// <summary>The key frames of <paramref name="path"/> (seconds, ascending), or null when they are not read this way.</summary>
    public static double[]? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Span<byte> magic = stackalloc byte[8];
            if (file.Read(magic) < 8)
                return null;
            var times = BinaryPrimitives.ReadUInt32BigEndian(magic) == 0x1A45DFA3 ? Matroska(file) : Mp4(file);
            if (times is not { Length: > 0 })
                return null;
            Array.Sort(times);
            return times;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            AppLog.Write($"The key frames of {Path.GetFileName(path)} are not read from its index", ex);
            return null;
        }
    }

    // ----- MP4 / MOV -----

    private static double[]? Mp4(FileStream file)
    {
        var first = true;
        foreach (var (type, body, end) in Boxes(file, 0, file.Length))
        {
            if (first && type is not ("ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "pnot" or "uuid"))
                return null;
            first = false;
            if (type != "moov")
                continue;
            foreach (var (inner, trakBody, trakEnd) in Boxes(file, body, end))
            {
                if (inner == "trak" && VideoTrak(file, trakBody, trakEnd) is { } times)
                    return times;
            }

            return null;
        }

        return null;
    }

    // The track's key frames when it is a video track; null otherwise (or when its tables are missing).
    private static double[]? VideoTrak(FileStream file, long body, long end)
    {
        byte[]? elst = null, mdhd = null, hdlr = null, stts = null, ctts = null, stss = null;
        uint movieScale = 0;
        foreach (var (type, at, stop) in Boxes(file, body, end))
        {
            if (type == "edts")
            {
                foreach (var (inner, b, e) in Boxes(file, at, stop))
                    elst = inner == "elst" ? Body(file, b, e) : elst;
            }
            else if (type == "mdia")
            {
                foreach (var (inner, b, e) in Boxes(file, at, stop))
                {
                    if (inner == "mdhd")
                        mdhd = Body(file, b, e);
                    else if (inner == "hdlr")
                        hdlr = Body(file, b, e);
                    else if (inner == "minf")
                    {
                        foreach (var (stblType, sb, se) in Boxes(file, b, e))
                        {
                            if (stblType != "stbl")
                                continue;
                            foreach (var (table, tb, te) in Boxes(file, sb, se))
                            {
                                if (table == "stts")
                                    stts = Body(file, tb, te);
                                else if (table == "ctts")
                                    ctts = Body(file, tb, te);
                                else if (table == "stss")
                                    stss = Body(file, tb, te);
                            }
                        }
                    }
                }
            }
        }

        if (hdlr is not { Length: >= 12 } || BinaryPrimitives.ReadUInt32BigEndian(hdlr.AsSpan(8)) != 0x76696465 /* vide */ || mdhd is not { Length: >= 24 } || stts is not { Length: >= 8 })
            return null;
        var scale = mdhd[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(12));
        if (scale == 0)
            return null;

        // The edit list (as Mp4Timing reads it): leading empty edits delay the track (movie units), the first real one's media time is skipped.
        double emptyEdit = 0;
        long mediaTime = 0;
        if (elst is { Length: >= 8 })
        {
            movieScale = MovieScale(file);
            var version = elst[0];
            var count = BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(4));
            var entry = version == 1 ? 20 : 12;
            for (var i = 0; i < count && 8 + (i + 1) * entry <= elst.Length; i++)
            {
                var e = elst.AsSpan(8 + i * entry);
                var duration = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(e) : BinaryPrimitives.ReadUInt32BigEndian(e);
                var media = version == 1 ? BinaryPrimitives.ReadInt64BigEndian(e[8..]) : BinaryPrimitives.ReadInt32BigEndian(e[4..]);
                if (media == -1)
                {
                    emptyEdit += (double)duration / Math.Max(movieScale, 1);
                    continue;
                }

                mediaTime = media;
                break;
            }
        }

        // The samples to place: stss's (1-based, ascending), or every sample.
        var timing = new SampleCursor(stts, 8);
        var offsets = ctts is { Length: >= 8 } ? new SampleCursor(ctts, 8) : null;
        var listed = stss is { Length: >= 8 };
        var keys = listed ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stss!.AsSpan(4)), (uint)((stss!.Length - 8) / 4)) : timing.Total;
        if (keys is <= 0 or > MostSamples)
            return null;

        var times = new List<double>((int)keys);
        for (var i = 0; i < keys; i++)
        {
            long sample = listed ? BinaryPrimitives.ReadUInt32BigEndian(stss!.AsSpan(8 + i * 4)) : i + 1;
            if (sample < 1 || !timing.MoveTo(sample))
                break;
            var dts = timing.Time;
            // A composition offset is signed as FFmpeg reads it, whatever the box's version says.
            long cts = offsets is not null && offsets.MoveTo(sample) ? (int)offsets.Value : 0;
            times.Add(emptyEdit + (double)(dts + cts - mediaTime) / scale);
        }

        return [.. times];
    }

    // mvhd's time scale (the edit list's units); 1000 when not found.
    private static uint MovieScale(FileStream file)
    {
        foreach (var (type, body, end) in Boxes(file, 0, file.Length))
        {
            if (type != "moov")
                continue;
            foreach (var (inner, b, e) in Boxes(file, body, end))
            {
                if (inner == "mvhd" && Body(file, b, Math.Min(e, b + 32)) is { Length: >= 24 } mvhd)
                    return mvhd[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
            }
        }

        return 1000;
    }

    /// <summary>
    /// A walk through a run-length table of (count, value) entries after a full box's 8-byte header (stts: deltas, ctts: offsets), by
    /// 1-based sample number, forward only: <see cref="Time"/> is the sum of the values of the samples before it (with stts, its dts),
    /// <see cref="Value"/> its entry's value.
    /// </summary>
    private sealed class SampleCursor(byte[] table, int start)
    {
        private readonly int _entries = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(4)), (uint)((table.Length - start) / 8));
        private int _entry = -1;
        private long _next = 1, _nextTime, _left;
        private uint _value;

        public long Time { get; private set; }

        public uint Value { get; private set; }

        public long Total
        {
            get
            {
                long total = 0;
                for (var i = 0; i < _entries; i++)
                    total += BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(start + i * 8));
                return total;
            }
        }

        /// <summary>Moves to <paramref name="sample"/>; false when the table ends before it, or it lies behind the cursor.</summary>
        public bool MoveTo(long sample)
        {
            if (sample < _next)
                return false;
            while (true)
            {
                if (_left == 0)
                {
                    if (++_entry >= _entries)
                        return false;
                    _left = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(start + _entry * 8));
                    _value = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(start + _entry * 8 + 4));
                    continue;
                }

                if (sample < _next + _left)
                {
                    (Time, Value) = (_nextTime + (sample - _next) * _value, _value);
                    (_nextTime, _left, _next) = (Time, _left - (sample - _next), sample);
                    return true;
                }

                (_nextTime, _next, _left) = (_nextTime + _left * _value, _next + _left, 0);
            }
        }
    }

    // The boxes between from and to: (type, body start, end).
    private static IEnumerable<(string Type, long Body, long End)> Boxes(FileStream file, long from, long to)
    {
        var header = new byte[16];
        var at = from;
        while (at + 8 <= to)
        {
            file.Position = at;
            if (file.Read(header, 0, 8) < 8)
                yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = string.Create(4, header, (chars, bytes) =>
            {
                for (var i = 0; i < 4; i++)
                    chars[i] = (char)bytes[4 + i];
            });
            var body = 8;
            if (size == 1)
            {
                if (file.Read(header, 8, 8) < 8)
                    yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                body = 16;
            }
            else if (size == 0)
            {
                size = to - at;
            }

            if (size < body || size > to - at)
                yield break;
            yield return (type, at + body, at + size);
            at += size;
        }
    }

    private static byte[]? Body(FileStream file, long body, long end)
    {
        if (end - body > MostTable)
            return null;
        var bytes = new byte[end - body];
        file.Position = body;
        file.ReadExactly(bytes);
        return bytes;
    }

    // ----- Matroska / WebM -----

    private const uint SegmentId = 0x18538067, SeekHeadId = 0x114D9B74, SeekId = 0x4DBB, SeekIdId = 0x53AB, SeekPositionId = 0x53AC;
    private const uint InfoId = 0x1549A966, TimecodeScaleId = 0x2AD7B1, TracksId = 0x1654AE6B, TrackEntryId = 0xAE, TrackNumberId = 0xD7, TrackTypeId = 0x83;
    private const uint CuesId = 0x1C53BB6B, CuePointId = 0xBB, CueTimeId = 0xB3, CueTrackPositionsId = 0xB7, CueTrackId = 0xF7, ClusterId = 0x1F43B675;

    private static double[]? Matroska(FileStream file)
    {
        // The EBML header, then the Segment.
        file.Position = 4;
        var headerSize = ReadSize(file, out _);
        file.Position += headerSize;
        if (ReadId(file) != SegmentId)
            return null;
        var segmentSize = ReadSize(file, out var unknown);
        var segment = file.Position;
        var segmentEnd = unknown ? file.Length : Math.Min(segment + segmentSize, file.Length);

        // The top level up to the first Cluster (the SeekHead, Info and Tracks are there in every muxer's files); the rest by the SeekHead.
        long info = -1, tracks = -1, cues = -1;
        var seekHeads = new List<long>();
        var at = segment;
        while (at < segmentEnd)
        {
            file.Position = at;
            var id = ReadId(file);
            var size = ReadSize(file, out var sizeUnknown);
            var body = file.Position;
            if (id is 0 or ClusterId || sizeUnknown)
                break;
            if (id == SeekHeadId)
                seekHeads.Add(at);
            else if (id == InfoId)
                info = at;
            else if (id == TracksId)
                tracks = at;
            else if (id == CuesId)
                cues = at;
            at = body + size;
        }

        for (var i = 0; i < seekHeads.Count && i < 4; i++)
        {
            foreach (var (id, position) in Seeks(Element(file, seekHeads[i], SeekHeadId)))
            {
                var where = position > (ulong)file.Length ? -1 : segment + (long)position;
                if (id == InfoId && info < 0)
                    info = where;
                else if (id == TracksId && tracks < 0)
                    tracks = where;
                else if (id == CuesId && cues < 0)
                    cues = where;
                else if (id == SeekHeadId && !seekHeads.Contains(where))
                    seekHeads.Add(where);
            }
        }

        if (tracks < 0 || cues < 0 || Element(file, tracks, TracksId) is not { } tracksBody || VideoTrack(tracksBody) is not { } video
            || Element(file, cues, CuesId) is not { } cuesBody)
            return null;

        ulong timecodeScale = 1_000_000;
        if (info >= 0 && Element(file, info, InfoId) is { } infoBody)
        {
            foreach (var (id, start, length) in Children(infoBody, 0, infoBody.Length))
                timecodeScale = id == TimecodeScaleId ? Unsigned(infoBody, start, length) : timecodeScale;
        }

        var times = new List<double>();
        foreach (var (id, start, length) in Children(cuesBody, 0, cuesBody.Length))
        {
            if (id != CuePointId)
                continue;
            ulong? time = null;
            var forVideo = false;
            foreach (var (inner, s, l) in Children(cuesBody, start, start + length))
            {
                if (inner == CueTimeId)
                    time = Unsigned(cuesBody, s, l);
                else if (inner == CueTrackPositionsId)
                {
                    foreach (var (positionId, ps, pl) in Children(cuesBody, s, s + l))
                        forVideo |= positionId == CueTrackId && Unsigned(cuesBody, ps, pl) == video;
                }
            }

            if (time is { } t && forVideo)
                times.Add(t * (double)timecodeScale / 1e9);
        }

        return [.. times];
    }

    // The first video track's number.
    private static ulong? VideoTrack(byte[] tracks)
    {
        foreach (var (id, start, length) in Children(tracks, 0, tracks.Length))
        {
            if (id != TrackEntryId)
                continue;
            ulong number = 0, type = 0;
            foreach (var (inner, s, l) in Children(tracks, start, start + length))
            {
                if (inner == TrackNumberId)
                    number = Unsigned(tracks, s, l);
                else if (inner == TrackTypeId)
                    type = Unsigned(tracks, s, l);
            }

            if (type == 1 && number > 0)
                return number;
        }

        return null;
    }

    private static IEnumerable<(uint Id, ulong Position)> Seeks(byte[]? seekHead)
    {
        if (seekHead is null)
            yield break;
        foreach (var (id, start, length) in Children(seekHead, 0, seekHead.Length))
        {
            if (id != SeekId)
                continue;
            uint target = 0;
            ulong? position = null;
            foreach (var (inner, s, l) in Children(seekHead, start, start + length))
            {
                if (inner == SeekIdId && l is > 0 and <= 4)
                    target = (uint)Unsigned(seekHead, s, l);
                else if (inner == SeekPositionId)
                    position = Unsigned(seekHead, s, l);
            }

            if (target != 0 && position is { } p)
                yield return (target, p);
        }
    }

    // The body of the element at `at` when it is the one expected.
    private static byte[]? Element(FileStream file, long at, uint expected)
    {
        if (at < 0 || at >= file.Length)
            return null;
        file.Position = at;
        if (ReadId(file) != expected)
            return null;
        var size = ReadSize(file, out var unknown);
        if (unknown || size > MostTable || file.Position + size > file.Length)
            return null;
        var body = new byte[size];
        file.ReadExactly(body);
        return body;
    }

    private static IEnumerable<(uint Id, int Start, int Length)> Children(byte[] data, int from, int to)
    {
        var at = from;
        while (at < to)
        {
            var id = Vint(data, ref at, keepMarker: true, out _);
            var size = Vint(data, ref at, keepMarker: false, out var unknown);
            if (unknown || at + (long)size > to)
                yield break;
            yield return ((uint)id, at, (int)size);
            at += (int)size;
        }
    }

    private static ulong Unsigned(byte[] data, int start, int length)
    {
        ulong value = 0;
        for (var i = 0; i < length && i < 8; i++)
            value = value << 8 | data[start + i];
        return value;
    }

    // An EBML variable-length integer: an ID keeps its length marker, a size drops it (all ones: unknown).
    private static ulong Vint(byte[] data, ref int at, bool keepMarker, out bool unknown)
    {
        if (at >= data.Length)
            throw new InvalidDataException("An EBML number runs past its element.");
        var first = data[at];
        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
        if (first == 0 || length > 8 || at + length > data.Length)
            throw new InvalidDataException("An EBML number is not valid.");
        ulong value = keepMarker ? first : (ulong)(first & (0xFF >> length));
        var ones = value == (ulong)(0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            value = value << 8 | data[at + i];
            ones &= data[at + i] == 0xFF;
        }

        at += length;
        unknown = !keepMarker && ones;
        return value;
    }

    private static uint ReadId(FileStream file)
    {
        var bytes = new byte[4];
        if (file.Read(bytes, 0, 1) < 1)
            return 0;
        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)bytes[0]) - 23;
        if (bytes[0] == 0 || length > 4 || file.Read(bytes, 1, length - 1) < length - 1)
            return 0;
        var at = 0;
        return (uint)Vint(bytes, ref at, keepMarker: true, out _);
    }

    private static long ReadSize(FileStream file, out bool unknown)
    {
        var bytes = new byte[8];
        unknown = false;
        if (file.Read(bytes, 0, 1) < 1)
            throw new InvalidDataException("The file ends inside an EBML size.");
        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)bytes[0]) - 23;
        if (bytes[0] == 0 || length > 8 || file.Read(bytes, 1, length - 1) < length - 1)
            throw new InvalidDataException("An EBML size is not valid.");
        var at = 0;
        var size = Vint(bytes, ref at, keepMarker: false, out unknown);
        return unknown || size > long.MaxValue ? 0 : (long)size;
    }
}
