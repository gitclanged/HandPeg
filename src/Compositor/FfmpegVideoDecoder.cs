using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// Fallback decoder for <see cref="MfVideoDecoder"/> when Media Foundation cannot decode a stream (HEVC without the Store HEVC Video
/// Extensions, e.g. a clean Windows on ARM laptop, or any missing codec or container). FFmpeg decodes and pipes raw NV12 (P010 for 10+ bit)
/// pictures; the decoder uploads them into the same luma/chroma planes as its CPU decode (UploadSoftware), so the cache, held mode, lane
/// stepping, key frames and seek landings are shared.
/// <para><b>Exact times.</b> <c>-stats_enc_pre</c> writes one stamp line per picture on stderr before the picture reaches stdout, with the
/// stream's own pts (<c>-copyts</c>, <c>-enc_time_base demux</c>, <c>-fps_mode passthrough</c>: one picture per decoded frame, stamps
/// untouched). A picture lasts until the next stamp, so Matroska's millisecond clock and B-frames are exact regardless of seek landing.</para>
/// <para><b>Seeking.</b> FFmpeg restarts with <c>-ss</c> (<c>-seek_timestamp 1</c>: file clock; <c>-noaccurate_seek</c>: every picture from
/// the landing key frame). With B-frames FFmpeg seeks 3/23 s early unless the container seeks by pts (Matroska does not, MP4 does), so the
/// target is moved that much later; a landing after the target shows the container needs no bias, and the bias is dropped for that file.</para>
/// <para><b>No filter graph.</b> Only <c>-pix_fmt nv12</c>/<c>p010le</c> (plane interleave, lossless, range kept) and <c>-noautorotate</c>
/// (frames as coded, like MF). Colour metadata (matrix, transfer, range) comes from ffprobe, mapped to MF enumerations.</para>
/// <para><b>Discrete adapters</b> (dedicated VRAM, e.g. a GTX 1070 without the HEVC extensions): FFmpeg decodes with <c>-hwaccel d3d11va</c>
/// on that adapter index and downloads each picture into the same planes. A failed GPU run retries on the CPU and the file is not tried on
/// the GPU again. Unified-memory adapters decode on the CPU (Adreno D3D11VA measured slower than its CPU).</para>
/// <para>Processes: one FFmpeg per decoder, in the worker's kill-on-close job (JobObjects: it dies with the worker however it ends), killed
/// on seek, end or dispose. Stdout is read on the decoder's lane thread, stderr on a pool thread.</para>
/// </summary>
internal sealed partial class FfmpegVideoDecoder : IDisposable
{
    /// <summary>FFmpeg and ffprobe paths the worker decodes with (the app's DependencyUpdater copies, passed on its command line).</summary>
    public static string FfmpegPath { get; set; } = "";

    public static string FfprobePath { get; set; } = "";

    // FFmpeg seeks 3/23 s back when a stream has B-frames and the container does not seek by pts (fftools' dts heuristic): 130,434 us,
    // plus one so the seek lands at the target itself.
    private const long BFrameSeekBias = 130_435;

    /// <summary>Maximum wait for a picture's stamp after the picture is read, ms (stamps are written first; this only covers pipe lag).</summary>
    private const int StampWaitMs = 10_000;

    // Prefix of FFmpeg's per-picture stamp lines on stderr.
    private const string StampPrefix = "@hp ";

    // Keyword for the stream's own time base: "demux" (FFmpeg 7.0+), "-1" before; the second is tried when the first is rejected.
    private static string _timeBase = "demux";

    private readonly string _path;
    private readonly int _stream;
    private readonly Facts _facts;
    private readonly int _card;
    private Run? _run;

    // Start of the next run: absolute stream time (100 ns); long.MinValue = file start.
    private long _from = long.MinValue;
    private bool _ended;
    private long _first = long.MinValue;

    private FfmpegVideoDecoder(string path, int stream, Facts facts, int card)
    {
        (_path, _stream, _facts, _card) = (path, stream, facts, card);
        Picture = new byte[facts.FrameBytes];
    }

    /// <summary>Why no FFmpeg is available; null when it is.</summary>
    public static string? Missing =>
        FfmpegPath.Length == 0 || FfprobePath.Length == 0 ? "no FFmpeg is installed"
        : !File.Exists(FfmpegPath) ? $"FFmpeg is not at {FfmpegPath}"
        : !File.Exists(FfprobePath) ? $"ffprobe is not at {FfprobePath}"
        : null;

    /// <summary>Failure reason without the " failed (0x...)" suffix of a <see cref="GpuException"/> message (for messages combining two).</summary>
    public static string ReasonOf(GpuException ex)
    {
        var at = ex.Message.LastIndexOf(" failed (0x", StringComparison.Ordinal);
        return at > 0 ? ex.Message[..at] : ex.Message;
    }

    /// <summary>Last picture read: luma rows (<see cref="LumaPitch"/> bytes each), then chroma rows from <see cref="ChromaOffset"/>.</summary>
    public byte[] Picture { get; }

    public int LumaPitch => _facts.LumaPitch;
    public int ChromaPitch => _facts.ChromaPitch;
    public int ChromaOffset => _facts.ChromaOffset;

    /// <summary>Picture size rounded down to even (as the chroma plane needs; an odd last row or column is dropped).</summary>
    public int Width => _facts.Width & ~1;

    public int Height => _facts.Height & ~1;

    /// <summary>P010 (10 bits or more), else NV12.</summary>
    public bool TenBit => _facts.TenBit;

    /// <summary>Stream colour in MF enumerations (MFVideoTransferMatrix, MFVideoTransferFunction, MFNominalRange_0_255).</summary>
    public int Matrix => _facts.Matrix;

    public int Transfer => _facts.Transfer;
    public bool FullRange => _facts.FullRange;

    /// <summary>Frame duration from the stream's average rate, seconds; 0 when unknown.</summary>
    public double FrameDuration => _facts.FrameDuration;

    public double DurationSeconds => _facts.DurationSeconds;

    /// <summary>Codec name as FFmpeg reports it ("hevc"), for worker messages.</summary>
    public string Codec => _facts.Codec;

    /// <summary>
    /// Probes the <paramref name="stream"/>-th video stream (cover art excluded, as MF excludes it) to decode on DXGI adapter
    /// <paramref name="card"/> (D3D11VA; -1 = CPU). Throws <see cref="GpuException"/> with the reason when FFmpeg is missing or cannot read it.
    /// </summary>
    public static FfmpegVideoDecoder Open(string path, int stream, int card = -1)
    {
        if (Missing is { } missing)
            throw new GpuException(unchecked((int)0xC00D5212), missing);
        return new FfmpegVideoDecoder(path, Math.Max(stream, 0), FactsOf(path, Math.Max(stream, 0)), card);
    }

    /// <summary>True when the next run decodes on the GPU (D3D11VA): an adapter was requested and this file has not failed there.</summary>
    public bool OnCard => _card >= 0 && !_facts.NotOnCard;

    /// <summary>
    /// Positions the next run at the key frame at or before <paramref name="ticks"/> (stream time, 100 ns): FFmpeg stops now and restarts on
    /// the next read. At or before the first picture it restarts from the file start.
    /// </summary>
    public void Seek(long ticks)
    {
        Stop();
        _from = _first != long.MinValue && ticks <= _first ? long.MinValue : ticks;
        _ended = false;
    }

    /// <summary>
    /// Reads the next picture into <see cref="Picture"/> with its stream time (100 ns) and duration (until the next picture; 0 for the last or
    /// when unknown). False at end of stream (until the next seek).
    /// </summary>
    public bool Read(out long ticks, out long length)
    {
        (ticks, length) = (0, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_ended)
                return false;
            var run = _run ??= Start(_from);
            if (!run.ReadPicture(Picture))
            {
                var failed = run.Frames == 0 ? run.Failure() : null;
                Stop();
                if (failed is not null && run.OnCard)
                {
                    // The GPU cannot decode it (no D3D11VA, unsupported profile): use the CPU for this file from now on.
                    _facts.NotOnCard = true;
                    attempt--;
                    continue;
                }

                if (failed is not null && attempt == 0 && _timeBase == "demux" && failed.Contains("time base", StringComparison.OrdinalIgnoreCase))
                {
                    // FFmpeg before 7.0 names the stream time base -1.
                    _timeBase = "-1";
                    continue;
                }

                if (failed is null && run.Frames == 0 && run.Target != long.MinValue && attempt < 2)
                {
                    // The seek found no picture after its landing (container longer than its video): retry 2 s earlier so the last
                    // picture is reached by reading forward, as MF's seek does.
                    var back = run.Target - 20_000_000;
                    _from = _first != long.MinValue && back <= _first ? long.MinValue : back;
                    continue;
                }

                _ended = true;
                if (failed is not null)
                    throw new GpuException(unchecked((int)0x80004005), failed.Contains("Unrecognized option", StringComparison.Ordinal)
                        ? $"FFmpeg 6.1 or later is needed ({failed})" : $"FFmpeg's decode ({failed})");
                return false;
            }

            var number = run.Frames - 1;
            if (!run.Stamp(number, out ticks))
            {
                Stop();
                throw new GpuException(unchecked((int)0x80004005), "FFmpeg gave a picture without its time");
            }

            // A B-frame-biased seek landed after the target: this container seeks by pts; retry without the bias.
            if (number == 0 && run.Biased && ticks > run.Target)
            {
                _facts.NoSeekBias = true;
                Stop();
                continue;
            }

            if (_from == long.MinValue && number == 0)
                _first = ticks;
            length = run.Stamp(number + 1, out var next) && next > ticks ? next - ticks : 0;
            return true;
        }

        return false;
    }

    /// <summary>Starts FFmpeg at <paramref name="from"/> (stream time, 100 ns; long.MinValue = file start).</summary>
    private Run Start(long from)
    {
        var biased = from != long.MinValue && _facts.HasBFrames && !_facts.NoSeekBias;
        var arguments = new StringBuilder("-hide_banner -nostdin -nostats -loglevel error -noautorotate");

        // GPU decode: one thread (the GPU decodes; FFmpeg frame threading is unsafe with some hardware decoders).
        var onCard = OnCard;
        if (onCard)
            arguments.Append(CultureInfo.InvariantCulture, $" -hwaccel d3d11va -hwaccel_device {_card} -threads 1");
        else
            arguments.Append(CultureInfo.InvariantCulture, $" -threads {DecodeThreads}");
        if (from != long.MinValue)
        {
            // Microseconds rounded up: FFmpeg parses at most six decimals, and a key frame exactly at the target must not be missed.
            var micro = (from + 9) / 10 + (biased ? BFrameSeekBias : 0);
            arguments.Append(CultureInfo.InvariantCulture, $" -seek_timestamp 1 -ss {micro / 1_000_000.0:0.000000} -noaccurate_seek");
        }

        arguments.Append(CultureInfo.InvariantCulture, $" -i \"{_path}\" -map 0:V:{_stream} -fps_mode passthrough -copyts -enc_time_base {_timeBase}");
        arguments.Append(TenBit ? " -pix_fmt p010le" : " -pix_fmt nv12");
        arguments.Append(" -stats_enc_pre pipe:2 -stats_enc_pre_fmt \"").Append(StampPrefix).Append("{pts} {tb}\" -f rawvideo pipe:1");

        MfVideoDecoder.Beat?.Invoke();
        var process = StartProcess(FfmpegPath, arguments.ToString(), binaryOutput: true);
        MfVideoDecoder.Beat?.Invoke();
        return new Run(process, from, biased) { OnCard = onCard };
    }

    /// <summary>Decode threads per FFmpeg: a few (a source may run several decoders, each with its own FFmpeg).</summary>
    private static int DecodeThreads => Math.Clamp(Environment.ProcessorCount / 3, 2, 4);

    private void Stop()
    {
        _run?.Dispose();
        _run = null;
    }

    public void Dispose() => Stop();

    /// <summary>Starts a tool with redirected pipes and assigns it to the worker's kill-on-close job immediately.</summary>
    private static Process StartProcess(string program, string arguments, bool binaryOutput)
    {
        var info = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(program) ?? "",
        };
        if (!binaryOutput)
            info.StandardOutputEncoding = Encoding.UTF8;

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new GpuException(unchecked((int)0x80004005), $"{Path.GetFileName(program)} would not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new GpuException(unchecked((int)0x80004005), $"{Path.GetFileName(program)} would not start ({ex.Message})");
        }

        // Killed with the worker however it ends (KILL_ON_JOB_CLOSE); the worker's job is nested in HandPeg's.
        JobObjects.Assign(process.Id, ProcessClass.Foreground, efficiencyMode: false);
        return process;
    }

    // ----- Stream facts (ffprobe, once per file) -----

    /// <summary>ffprobe facts for a stream plus what seeking in the file has revealed; cached for recently probed files.</summary>
    private sealed class Facts
    {
        public string Codec = "";
        public int Width, Height;
        public bool TenBit;
        public int Matrix = 1, Transfer;
        public bool FullRange;
        public double FrameDuration, DurationSeconds;
        public bool HasBFrames;

        /// <summary>A B-frame-biased seek landed after the target: this file's container seeks by pts.</summary>
        public volatile bool NoSeekBias;

        /// <summary>A GPU run failed for this file: decode it on the CPU.</summary>
        public volatile bool NotOnCard;

        public int LumaPitch => Width * (TenBit ? 2 : 1);
        public int ChromaPitch => (Width + 1) / 2 * 2 * (TenBit ? 2 : 1);
        public int ChromaOffset => LumaPitch * Height;
        public int FrameBytes => ChromaOffset + ChromaPitch * ((Height + 1) / 2);
    }

    // Facts of the most recently probed files (path, size, write time, stream) so a source's second decoder and every seek restart skip
    // ffprobe. At most eight entries.
    private static readonly List<(string Key, Facts Facts)> Known = [];

    private static Facts FactsOf(string path, int stream)
    {
        string key;
        try
        {
            var file = new FileInfo(path);
            key = string.Create(CultureInfo.InvariantCulture, $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{stream}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new GpuException(unchecked((int)0x80070002), $"The file cannot be read ({ex.Message})");
        }

        lock (Known)
        {
            foreach (var (known, facts) in Known)
            {
                if (known == key)
                    return facts;
            }
        }

        var probed = Probe(path, stream);
        lock (Known)
        {
            Known.RemoveAll(k => k.Key == key);
            Known.Add((key, probed));
            if (Known.Count > 8)
                Known.RemoveAt(0);
        }

        return probed;
    }

    private static Facts Probe(string path, int stream)
    {
        var arguments = string.Create(CultureInfo.InvariantCulture,
            $"-v error -select_streams V:{stream} -show_entries stream=codec_name,width,height,pix_fmt,bits_per_raw_sample,color_space,color_transfer,color_range,r_frame_rate,avg_frame_rate,has_b_frames,duration:format=duration -of json \"{path}\"");
        MfVideoDecoder.Beat?.Invoke();
        using var process = StartProcess(FfprobePath, arguments, binaryOutput: false);
        var errors = process.StandardError.ReadToEndAsync();

        // Bound the answer (and the read itself) to 30 s: a stalled share or a spinning ffprobe must not block the opening lane forever.
        var answer = process.StandardOutput.ReadToEndAsync();
        string json;
        try
        {
            json = answer.Wait(30_000) ? answer.Result : "";
        }
        catch (AggregateException ex) when (ex.InnerException is IOException or ObjectDisposedException)
        {
            throw new GpuException(unchecked((int)0x80004005), $"ffprobe's answer could not be read ({ex.InnerException.Message})");
        }

        if (!answer.IsCompleted || !process.WaitForExit(30_000))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Gone already.
            }

            throw new GpuException(unchecked((int)0x80004005), "ffprobe did not answer");
        }

        MfVideoDecoder.Beat?.Invoke();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
            {
                var said = Said(errors);
                throw new GpuException(unchecked((int)0xC00D36B3), said.Length > 0 ? $"ffprobe: {Cut(said)}" : "FFmpeg finds no such video stream");
            }

            var entry = streams[0];
            var facts = new Facts
            {
                Codec = Text(entry, "codec_name"),
                Width = Integer(entry, "width"),
                Height = Integer(entry, "height"),
                HasBFrames = Integer(entry, "has_b_frames") > 0,
            };
            if (facts.Width < 2 || facts.Height < 2)
                throw new GpuException(unchecked((int)0xC00D36B4), "FFmpeg gives the stream no picture size");

            var format = Text(entry, "pix_fmt");
            var bits = Integer(entry, "bits_per_raw_sample");
            facts.TenBit = bits > 8 || DepthOf(format) > 8;
            facts.Matrix = MatrixOf(Text(entry, "color_space"));
            facts.Transfer = TransferOf(Text(entry, "color_transfer"));
            var range = Text(entry, "color_range");
            facts.FullRange = range == "pc" || range is not "tv" && format.StartsWith("yuvj", StringComparison.Ordinal);

            // RGB streams (screen captures, FFV1/PNG in RGB) are converted to YUV by FFmpeg: BT.601, limited range.
            if (IsRgb(format))
                (facts.Matrix, facts.FullRange) = (2, false);

            var rate = RateOf(Text(entry, "avg_frame_rate"));
            if (rate is not (> 0 and <= 1000))
                rate = RateOf(Text(entry, "r_frame_rate"));
            facts.FrameDuration = rate is > 0 and <= 1000 ? 1 / rate : 0;

            var duration = Seconds(Text(entry, "duration"));
            if (!(duration > 0) && document.RootElement.TryGetProperty("format", out var container))
                duration = Seconds(Text(container, "duration"));
            facts.DurationSeconds = duration > 0 ? duration : 0;
            return facts;
        }
        catch (JsonException)
        {
            throw new GpuException(unchecked((int)0x80004005), "ffprobe's answer could not be read");
        }
    }

    private static string Cut(string text) => text.Length > 200 ? text[..200] + "..." : text;

    // The tool's stderr (read to its end alongside stdout), or "" when it could not be read.
    private static string Said(Task<string> errors)
    {
        try
        {
            return errors.Wait(1000) ? errors.Result.Trim() : "";
        }
        catch (AggregateException)
        {
            return "";
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        } : "";

    private static int Integer(JsonElement element, string name) =>
        int.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double Seconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double RateOf(string text)
    {
        var slash = text.IndexOf('/');
        return slash > 0 && double.TryParse(text.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
               && double.TryParse(text.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && num > 0 && den > 0
            ? num / den : 0;
    }

    // Sample bit depth from a pixel format name suffix ("yuv420p10le", "p010le", "gray12le"); 8 when none.
    [GeneratedRegex(@"(\d+)(le|be)$")]
    private static partial Regex DepthSuffix();

    private static int DepthOf(string format) =>
        DepthSuffix().Match(format) is { Success: true } match && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) ? bits : 8;

    private static bool IsRgb(string format) =>
        format.StartsWith("rgb", StringComparison.Ordinal) || format.StartsWith("bgr", StringComparison.Ordinal) || format.StartsWith("gbr", StringComparison.Ordinal)
        || format.StartsWith("argb", StringComparison.Ordinal) || format.StartsWith("abgr", StringComparison.Ordinal)
        || format.StartsWith("0rgb", StringComparison.Ordinal) || format.StartsWith("0bgr", StringComparison.Ordinal) || format == "pal8";

    /// <summary>FFmpeg colour space as MFVideoTransferMatrix: 1 BT.709 (also the default when unspecified, as on the MF path), 2 BT.601, 3 SMPTE 240M, 4 BT.2020.</summary>
    internal static int MatrixOf(string space) => space switch
    {
        "smpte170m" or "bt470bg" or "fcc" => 2,
        "smpte240m" => 3,
        "bt2020nc" or "bt2020c" => 4,
        _ => 1,
    };

    /// <summary>FFmpeg transfer as MFVideoTransferFunction: the renderer acts on 15 (PQ) and 16 (HLG); others per MF numbering; 0 unspecified.</summary>
    internal static int TransferOf(string transfer) => transfer switch
    {
        "linear" => 1,
        "gamma22" => 4,
        "bt709" or "smpte170m" or "bt1361e" => 5,
        "smpte240m" => 6,
        "iec61966-2-1" => 7,
        "gamma28" => 8,
        "log100" => 9,
        "log316" => 10,
        "bt2020-10" or "bt2020-12" => 13,
        "smpte2084" => 15,
        "arib-std-b67" => 16,
        _ => 0,
    };

    // ----- One FFmpeg run -----

    /// <summary>One FFmpeg process: stdout read picture by picture, stderr stamps collected as they arrive.</summary>
    private sealed class Run : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _output;
        private readonly object _gate = new();

        // Stamps of pictures from _offset on (100 ns, stream time); long.MinValue for an unparsable stamp.
        private readonly List<long> _stamps = [];
        private int _offset;
        private bool _errorsEnded;
        private readonly Queue<string> _said = new();

        public Run(Process process, long target, bool biased)
        {
            _process = process;
            Target = target;
            Biased = biased;
            _output = process.StandardOutput.BaseStream;
            process.ErrorDataReceived += OnError;
            process.BeginErrorReadLine();
        }

        /// <summary>Start position (long.MinValue = file start) and whether the B-frame seek bias was applied.</summary>
        public long Target { get; }

        public bool Biased { get; }

        /// <summary>True when this run decodes on the GPU (D3D11VA).</summary>
        public bool OnCard { get; init; }

        /// <summary>Pictures read so far.</summary>
        public int Frames { get; private set; }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            lock (_gate)
            {
                if (e.Data is not { } line)
                {
                    _errorsEnded = true;
                }
                else if (line.IndexOf(StampPrefix, StringComparison.Ordinal) is >= 0 and var at)
                {
                    // A stamp anywhere on the line: the encoder thread writes it outside the log lock, so it can land after a log
                    // line's "[hevc @ ...]" prefix (with that line's message on the next line).
                    _stamps.Add(ParseStamp(line.AsSpan(at + StampPrefix.Length)));
                }
                else if (line.Trim() is { Length: > 0 } said)
                {
                    _said.Enqueue(said);
                    if (_said.Count > 4)
                        _said.Dequeue();
                }

                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Parses "pts num/den" into 100 ns; long.MinValue when unreadable (the picture keeps its slot in the list).</summary>
        private static long ParseStamp(ReadOnlySpan<char> text)
        {
            var space = text.IndexOf(' ');
            var slash = text.LastIndexOf('/');
            if (space <= 0 || slash <= space
                || !long.TryParse(text[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)
                || !long.TryParse(text[(space + 1)..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
                || !long.TryParse(text[(slash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
                || num <= 0 || den <= 0 || pts == long.MinValue)
            {
                return long.MinValue;
            }

            // pts x num / den seconds in 100 ns, rounded to nearest (Int128: no overflow hours into a 90 kHz clock).
            var scaled = (Int128)pts * 10_000_000 * num;
            var half = (Int128)den / 2;
            return (long)((scaled >= 0 ? scaled + half : scaled - half) / den);
        }

        /// <summary>Reads one whole picture; false when the pipe ended first (end of stream, or FFmpeg stopped).</summary>
        public bool ReadPicture(byte[] into)
        {
            var got = 0;
            while (got < into.Length)
            {
                int read;
                try
                {
                    read = _output.Read(into, got, into.Length - got);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    read = 0;
                }

                if (read <= 0)
                    return false;
                got += read;
            }

            Frames++;
            return true;
        }

        /// <summary>
        /// Stamp of picture <paramref name="number"/> (0-based in this run), waiting while FFmpeg is still writing (a read picture's stamp is
        /// already there; the next arrives once that picture decodes). False when FFmpeg ended without it (after the last picture) or it was
        /// unreadable.
        /// </summary>
        public bool Stamp(int number, out long ticks)
        {
            var deadline = Environment.TickCount64 + StampWaitMs;
            lock (_gate)
            {
                while (_offset + _stamps.Count <= number && !_errorsEnded)
                {
                    var left = deadline - Environment.TickCount64;
                    if (left <= 0)
                        break;
                    Monitor.Wait(_gate, (int)Math.Min(left, 100));
                    MfVideoDecoder.Beat?.Invoke();
                }

                // Stamps older than the previous picture are never requested again: trim them.
                var behind = Math.Min(number - 1 - _offset, _stamps.Count);
                if (behind > 1024)
                {
                    _stamps.RemoveRange(0, behind);
                    _offset += behind;
                }

                var at = number - _offset;
                ticks = at >= 0 && at < _stamps.Count ? _stamps[at] : long.MinValue;
                return ticks != long.MinValue;
            }
        }

        /// <summary>FFmpeg's failure when it ended without a picture: its last lines, or its exit code; null when it reported nothing wrong.</summary>
        public string? Failure()
        {
            var exited = _process.WaitForExit(2000);
            lock (_gate)
            {
                // Its last stderr lines arrive after exit: wait briefly for them.
                var deadline = Environment.TickCount64 + 1000;
                while (exited && !_errorsEnded && Environment.TickCount64 < deadline)
                    Monitor.Wait(_gate, 50);
                if (_said.Count > 0)
                    return Cut(string.Join(" | ", _said));
            }

            return exited && _process.ExitCode != 0 ? string.Create(CultureInfo.InvariantCulture, $"exit code {_process.ExitCode}") : null;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Gone already.
            }

            _process.ErrorDataReceived -= OnError;
            _process.Dispose();
        }
    }
}
