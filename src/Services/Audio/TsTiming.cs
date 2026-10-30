using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Audio;

/// <summary>
/// Where an MPEG transport stream's elementary streams start on FFmpeg's clock (5.0-Verify): each one's first time stamp,
/// the PTS of its first PES, as FFmpeg's demuxer gives it (90 kHz, put past the 33-bit wrap as FFmpeg's wrap reference puts
/// it: 60 s before the program's first time stamp). Windows' TS source counts a clock of its own from where the stream
/// starts, so its reader's times are not FFmpeg's (the A/V check found a TS layer's sound 2.1 s early while its picture was
/// right); the first sound sample the reader gives is that stream's first PES, which ties the two clocks. Read from the
/// file's head (the PAT, the first program's PMT, then each stream's first PES), in 188-byte packets or 192 (M2TS: a time
/// code before each), once per file as it is.
/// </summary>
internal static class TsTiming
{
    /// <summary>One elementary stream of the program, in its PMT's order (FFmpeg's order of streams): sound or picture, and its first PTS in seconds (NaN when none was found).</summary>
    public sealed record Elementary(bool IsAudio, bool IsVideo, double FirstPts);

    // How far into the file the first time stamps are looked for: a stream that starts later than that has none.
    private const int HeadBytes = 24 << 20;
    private const long Wrap = 1L << 33, Minute = 60 * 90000;

    private static readonly ConcurrentDictionary<string, (long Length, long Written, Elementary[]? Streams)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first program's streams in their order, or null for a file that is not a transport stream (or cannot be read).</summary>
    public static Elementary[]? Read(string path)
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
            return kept.Streams;

        Elementary[]? streams = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            streams = Parse(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            AppLog.Write("Sound engine: the time stamps of a transport stream could not be read", ex);
        }

        if (Known.Count > 256)
            Known.Clear();
        Known[path] = (length, written, streams);
        return streams;
    }

    /// <summary>The first PTS (seconds, FFmpeg's clock) of a file's n-th audio stream ("0:a:N"); null when it is not a transport stream or the stream has none.</summary>
    public static double? FirstAudioPts(Elementary[]? streams, int stream) =>
        streams?.Where(s => s.IsAudio).Skip(stream).FirstOrDefault() is { } found && double.IsFinite(found.FirstPts) ? found.FirstPts : null;

    /// <summary>The first PTS (seconds, FFmpeg's clock) of a file's first video stream; null when it is not a transport stream or has none.</summary>
    public static double? FirstVideoPts(Elementary[]? streams) =>
        streams?.FirstOrDefault(s => s.IsVideo) is { } found && double.IsFinite(found.FirstPts) ? found.FirstPts : null;

    private static Elementary[]? Parse(FileStream file)
    {
        var head = new byte[3 * 192 + 4];
        if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            return null;
        var (size, skip) = Synced(head, 188, 0) ? (188, 0) : Synced(head, 192, 4) ? (192, 4) : (0, 0);
        if (size == 0)
            return null;
        file.Position = 0;

        var state = new Scan();
        var buffer = new byte[size * 4096];
        var total = 0L;
        while (total < HeadBytes && !state.Done)
        {
            var count = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) / size * size;
            if (count == 0)
                break;
            total += count;
            for (var at = 0; at < count && !state.Done; at += size)
            {
                if (!state.Packet(buffer.AsSpan(at + skip, 188)))
                    return state.Result();
            }
        }

        return state.Result();
    }

    private static bool Synced(ReadOnlySpan<byte> head, int size, int skip) =>
        head[skip] == 0x47 && head[skip + size] == 0x47 && head[skip + 2 * size] == 0x47;

    /// <summary>The walk through the packets: the tables as they come, then the first time stamps.</summary>
    private sealed class Scan
    {
        private int _pmtPid = -1;
        private List<(int Pid, bool Audio, bool Video)>? _streams;
        private readonly Dictionary<int, long> _first = [];
        private long? _reference;

        // A table section being gathered over packets.
        private readonly byte[] _section = new byte[4096];
        private int _sectionPid = -1, _sectionFill;

        public bool Done => _streams is { } streams && streams.Where(s => s.Audio || s.Video).All(s => _first.ContainsKey(s.Pid));

        /// <summary>One 188-byte packet; false when it is not one (the stream is lost: what was found stands).</summary>
        public bool Packet(ReadOnlySpan<byte> packet)
        {
            if (packet[0] != 0x47)
                return false;
            var start = (packet[1] & 0x40) != 0;
            var pid = (packet[1] & 0x1F) << 8 | packet[2];
            var control = packet[3] >> 4 & 3;
            if ((control & 1) == 0)
                return true;
            var at = 4 + ((control & 2) != 0 ? 1 + packet[4] : 0);
            if (at >= packet.Length)
                return true;
            var payload = packet[at..];

            if (pid == 0 || pid == _pmtPid)
                Section(pid, start, payload);
            else if (start && _streams is { } streams && !_first.ContainsKey(pid) && streams.Exists(s => s.Pid == pid))
                Pes(pid, payload);
            return true;
        }

        public Elementary[]? Result() => _streams?.Select(s => new Elementary(s.Audio, s.Video, _first.TryGetValue(s.Pid, out var pts) ? Unwrapped(pts) / 90000.0 : double.NaN)).ToArray();

        // FFmpeg's wrap reference: 60 s before the program's first time stamp; a later one below it has wrapped (added on),
        // or, when that first one is within the last minute before the wrap, one at or past the reference is taken off.
        private long Unwrapped(long pts)
        {
            if (_reference is not { } first)
                return pts;
            var reference = first - Minute;
            return first < Wrap - Minute ? (pts < reference ? pts + Wrap : pts) : (pts >= reference ? pts - Wrap : pts);
        }

        private void Section(int pid, bool start, ReadOnlySpan<byte> payload)
        {
            if (start)
            {
                var pointer = payload[0];
                if (1 + pointer >= payload.Length)
                    return;
                payload = payload[(1 + pointer)..];
                (_sectionPid, _sectionFill) = (pid, 0);
            }
            else if (pid != _sectionPid || _sectionFill == 0)
                return;

            var take = Math.Min(payload.Length, _section.Length - _sectionFill);
            payload[..take].CopyTo(_section.AsSpan(_sectionFill));
            _sectionFill += take;
            if (_sectionFill < 3)
                return;
            var length = 3 + ((_section[1] & 0x0F) << 8 | _section[2]);
            if (_sectionFill < length && _sectionFill < _section.Length)
                return;

            var section = _section.AsSpan(0, Math.Min(length, _sectionFill));
            (_sectionPid, _sectionFill) = (-1, 0);
            if (section.Length < 12)
                return;
            if (pid == 0 && section[0] == 0x00 && _pmtPid < 0)
            {
                // The PAT: the first program (number 0 is the network's table).
                for (var at = 8; at + 4 <= section.Length - 4; at += 4)
                {
                    if ((section[at] << 8 | section[at + 1]) != 0)
                    {
                        _pmtPid = (section[at + 2] & 0x1F) << 8 | section[at + 3];
                        break;
                    }
                }
            }
            else if (pid == _pmtPid && section[0] == 0x02 && _streams is null)
                Pmt(section);
        }

        private void Pmt(ReadOnlySpan<byte> section)
        {
            var info = (section[10] & 0x0F) << 8 | section[11];
            var end = section.Length - 4;
            if (12 + info > end)
                return;
            var hdmv = Registered(section.Slice(12, info), "HDMV");
            var streams = new List<(int, bool, bool)>();
            for (var at = 12 + info; at + 5 <= end;)
            {
                var type = section[at];
                var pid = (section[at + 1] & 0x1F) << 8 | section[at + 2];
                var length = (section[at + 3] & 0x0F) << 8 | section[at + 4];
                var descriptors = section.Slice(at + 5, Math.Min(length, Math.Max(end - at - 5, 0)));
                streams.Add((pid, IsAudio(type, descriptors, hdmv), IsVideo(type)));
                at += 5 + length;
            }

            _streams = streams;
        }

        private void Pes(int pid, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 14 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
                return;
            // Streams without the optional header (padding, private stream 2, ECM, EMM, DSM-CC, H.222.1 type E, directory).
            if (payload[3] is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF)
                return;
            var flags = payload[7] >> 6;
            if ((flags & 2) == 0)
                return;
            var pts = Stamp(payload[9..]);
            _reference ??= flags == 3 && payload.Length >= 19 ? Stamp(payload[14..]) : pts;
            _first[pid] = pts;
        }

        private static long Stamp(ReadOnlySpan<byte> b) =>
            (long)(b[0] >> 1 & 7) << 30 | (long)b[1] << 22 | (long)(b[2] >> 1) << 15 | (long)b[3] << 7 | (long)(b[4] >> 1);

        // FFmpeg's types of stream (mpegts.c's tables): MPEG-1/2, MPEG-4, H.264, HEVC, VVC, CAVS, AVS2/3, VC-1 and Dirac pictures.
        private static bool IsVideo(int type) => type is 0x01 or 0x02 or 0x10 or 0x1B or 0x20 or 0x24 or 0x33 or 0x42 or 0xD1 or 0xD2 or 0xD4 or 0xEA;

        // Sound: MPEG audio, AAC (ADTS and LATM), AC-3 and E-AC-3 (ATSC's types); Blu-ray's LPCM, DTS and TrueHD under an
        // "HDMV" registration; and a private stream (0x06) by its descriptor (AC-3, E-AC-3, DTS, AAC) or registration.
        private static bool IsAudio(int type, ReadOnlySpan<byte> descriptors, bool hdmv)
        {
            if (type is 0x03 or 0x04 or 0x0F or 0x11 or 0x1C or 0x81 or 0x87)
                return true;
            if (hdmv && type is 0x80 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2)
                return true;
            if (type != 0x06)
                return false;
            for (var at = 0; at + 2 <= descriptors.Length; at += 2 + descriptors[at + 1])
            {
                if (descriptors[at] is 0x6A or 0x7A or 0x7B or 0x7C)
                    return true;
            }

            return Registered(descriptors, "Opus") || Registered(descriptors, "AC-3") || Registered(descriptors, "EAC3") ||
                Registered(descriptors, "DTS1") || Registered(descriptors, "DTS2") || Registered(descriptors, "DTS3");
        }

        // Whether a descriptor loop holds a registration descriptor (tag 5) for a format identifier.
        private static bool Registered(ReadOnlySpan<byte> descriptors, string format)
        {
            for (var at = 0; at + 2 <= descriptors.Length; at += 2 + descriptors[at + 1])
            {
                var length = descriptors[at + 1];
                if (descriptors[at] == 0x05 && length >= 4 && at + 6 <= descriptors.Length &&
                    descriptors[at + 2] == format[0] && descriptors[at + 3] == format[1] && descriptors[at + 4] == format[2] && descriptors[at + 5] == format[3])
                    return true;
            }

            return false;
        }
    }
}
