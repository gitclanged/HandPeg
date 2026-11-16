using System.Collections.Concurrent;
using System.IO;

namespace HandPegApp.Services.Audio;

/// <summary>
/// First PTS of each MPEG-TS elementary stream on FFmpeg's clock (90 kHz, unwrapped past 33 bits by FFmpeg's wrap reference: 60 s before
/// the program's first timestamp). The Windows TS source runs its own clock from stream start (A/V checks found TS audio 2.1 s early with
/// correct video); the reader's first audio sample is the stream's first PES, which ties the clocks. Parses the head (PAT, first
/// program's PMT, each stream's first PES) in 188- or 192-byte (M2TS) packets; cached per path, length and write time.
/// </summary>
internal static class TsTiming
{
    /// <summary>An elementary stream in PMT order (FFmpeg stream order): audio/video flags and first PTS in seconds (NaN when not found).</summary>
    public sealed record Elementary(bool IsAudio, bool IsVideo, double FirstPts);

    // Scan limit from the file head; streams that start later report no PTS.
    private const int HeadBytes = 24 << 20;
    private const long Wrap = 1L << 33, Minute = 60 * 90000;

    private static readonly ConcurrentDictionary<string, (long Length, long Written, Elementary[]? Streams)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First program's streams in order; null when not a transport stream or unreadable.</summary>
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

    /// <summary>First PTS (seconds, FFmpeg clock) of the n-th audio stream ("0:a:N"); null when not TS or none was found.</summary>
    public static double? FirstAudioPts(Elementary[]? streams, int stream) =>
        streams?.Where(s => s.IsAudio).Skip(stream).FirstOrDefault() is { } found && double.IsFinite(found.FirstPts) ? found.FirstPts : null;

    /// <summary>First PTS (seconds, FFmpeg clock) of the first video stream; null when not TS or none was found.</summary>
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

    /// <summary>Packet walker: collects PAT/PMT, then each stream's first timestamp.</summary>
    private sealed class Scan
    {
        private int _pmtPid = -1;
        private List<(int Pid, bool Audio, bool Video)>? _streams;
        private readonly Dictionary<int, long> _first = [];
        private long? _reference;

        // Table section being reassembled across packets.
        private readonly byte[] _section = new byte[4096];
        private int _sectionPid = -1, _sectionFill;

        public bool Done => _streams is { } streams && streams.Where(s => s.Audio || s.Video).All(s => _first.ContainsKey(s.Pid));

        /// <summary>Processes one 188-byte packet; false on lost sync (results so far stand).</summary>
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

        // FFmpeg's wrap reference (60 s before the first timestamp): later stamps below it have wrapped (+2^33); when the first stamp
        // lies within the last minute before the wrap, stamps at or past the reference are moved back (-2^33).
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
                // PAT: take the first program (program number 0 is the network table).
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

        // FFmpeg's video stream types (mpegts.c): MPEG-1/2, MPEG-4, H.264, HEVC, VVC, CAVS, AVS2/3, VC-1, Dirac.
        private static bool IsVideo(int type) => type is 0x01 or 0x02 or 0x10 or 0x1B or 0x20 or 0x24 or 0x33 or 0x42 or 0xD1 or 0xD2 or 0xD4 or 0xEA;

        // Audio: MPEG audio, AAC (ADTS, LATM), AC-3/E-AC-3 (ATSC types); Blu-ray LPCM, DTS and TrueHD under an "HDMV"
        // registration; private streams (0x06) by descriptor (AC-3, E-AC-3, DTS, AAC) or registration.
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

        // True when the descriptor loop holds a registration descriptor (tag 5) for the format identifier.
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
