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
/// The decoder <see cref="MfVideoDecoder"/> falls back to when Media Foundation cannot decode a stream (Pass 5.2-Arm64AndNpu):
/// HEVC on a PC without the Store's HEVC Video Extensions (a clean Windows on ARM laptop), or any codec or container it lacks.
/// FFmpeg decodes it on the processor and pipes the raw pictures, NV12 (P010 for 10 bits and more), one after the other; the
/// decoder uploads each into the same luma and chroma planes as its own processor's decode (UploadSoftware), and the rest of
/// it (the cache, held mode, the lanes' stepping, the key frames, the seeks' landings) is the same for both.
/// <para><b>Exact times.</b> Every picture comes with its own time stamp, from FFmpeg itself: <c>-stats_enc_pre</c> writes one
/// line per picture on the error pipe, before the picture is written on the output pipe, with the stream's own presentation
/// time (<c>-copyts</c>, <c>-enc_time_base demux</c>, <c>-fps_mode passthrough</c>: one picture out for each picture decoded,
/// its stamp untouched). A picture lasts until the next one starts (a stamp is there before its picture), so Matroska's
/// millisecond clock (420 fps: 0, 2, 5, 7 ms) and B-frames are exact, and nothing depends on where a seek lands.</para>
/// <para><b>Seeking.</b> FFmpeg is started again with <c>-ss</c> at the moment (<c>-seek_timestamp 1</c>: the file's own
/// clock; <c>-noaccurate_seek</c>: every picture from the key frame it lands on). For a stream with B-frames FFmpeg seeks
/// 3/23 s early unless the container seeks by presentation time (Matroska does not, MP4 does): the moment is moved that much
/// later so it lands on the key frame at or before it; a landing after the moment shows the container was one that needs no
/// such move, which is then not made for that file again.</para>
/// <para><b>No filter graph.</b> Only <c>-pix_fmt nv12</c> or <c>p010le</c> (a plane interleave, measured lossless, the range
/// kept); no rotation (<c>-noautorotate</c>: the frame as coded, as Media Foundation gives it). The colour (matrix, transfer,
/// range) is the stream's, from ffprobe, in Media Foundation's numbers.</para>
/// <para><b>On a card of its own</b> (5.2-Release, the user's Q2): where the worker's card has memory of its own (a discrete card: a
/// GTX 1070 on a Windows without the HEVC Video Extensions), FFmpeg decodes on it (<c>-hwaccel d3d11va</c>, that card's
/// index) and brings each picture down for the pipe itself, in the same planes. A run that fails there goes again on the processor, and
/// that file is not tried on the card again. A card that shares the computer's memory decodes on the processor: on a Snapdragon X Elite's
/// Adreno D3D11VA was slower than its processor (5.2-Arm64AndNpu).</para>
/// <para>Processes: one FFmpeg at a time per decoder, in the worker's kill-on-close job (JobObjects: it dies with the worker
/// whatever way the worker ends) and killed when the decoder seeks, ends or is disposed. Its pipes are read on the thread that
/// drives the decoder (a decode lane), its error pipe on a pool thread.</para>
/// </summary>
internal sealed partial class FfmpegVideoDecoder : IDisposable
{
    /// <summary>The FFmpeg and ffprobe the worker decodes with: the app's (DependencyUpdater), handed over on its command line.</summary>
    public static string FfmpegPath { get; set; } = "";

    public static string FfprobePath { get; set; } = "";

    // FFmpeg's seek moves 3/23 s back for a container that does not seek by presentation time when a stream has B-frames
    // (fftools' dts heuristic): 130 434 microseconds, so one more lands at the moment itself.
    private const long BFrameSeekBias = 130_435;

    /// <summary>How long a picture's stamp is waited for once its picture is read, ms (it is written first: a pipe's lag only).</summary>
    private const int StampWaitMs = 10_000;

    // The prefix of FFmpeg's per-picture lines on the error pipe.
    private const string StampPrefix = "@hp ";

    // How FFmpeg is asked for the stream's own time base: "demux" (7.0 and later), "-1" before; the second is tried when the first is refused.
    private static string _timeBase = "demux";

    private readonly string _path;
    private readonly int _stream;
    private readonly Facts _facts;
    private readonly int _card;
    private Run? _run;

    // Where the next run starts: absolute stream time (100 ns); long.MinValue from the file's start.
    private long _from = long.MinValue;
    private bool _ended;
    private long _first = long.MinValue;

    private FfmpegVideoDecoder(string path, int stream, Facts facts, int card)
    {
        (_path, _stream, _facts, _card) = (path, stream, facts, card);
        Picture = new byte[facts.FrameBytes];
    }

    /// <summary>Why there is no FFmpeg to decode with; null when there is.</summary>
    public static string? Missing =>
        FfmpegPath.Length == 0 || FfprobePath.Length == 0 ? "no FFmpeg is installed"
        : !File.Exists(FfmpegPath) ? $"FFmpeg is not at {FfmpegPath}"
        : !File.Exists(FfprobePath) ? $"ffprobe is not at {FfprobePath}"
        : null;

    /// <summary>What went wrong, without the " failed (0x...)" a <see cref="GpuException"/>'s message ends with (for a message that holds two).</summary>
    public static string ReasonOf(GpuException ex)
    {
        var at = ex.Message.LastIndexOf(" failed (0x", StringComparison.Ordinal);
        return at > 0 ? ex.Message[..at] : ex.Message;
    }

    /// <summary>The picture read last: luma rows (<see cref="LumaPitch"/> bytes each), then chroma rows from <see cref="ChromaOffset"/>.</summary>
    public byte[] Picture { get; }

    public int LumaPitch => _facts.LumaPitch;
    public int ChromaPitch => _facts.ChromaPitch;
    public int ChromaOffset => _facts.ChromaOffset;

    /// <summary>The picture's size, even (as the chroma plane needs; an odd row or column of FFmpeg's picture is left out).</summary>
    public int Width => _facts.Width & ~1;

    public int Height => _facts.Height & ~1;

    /// <summary>P010 (10 bits or more), else NV12.</summary>
    public bool TenBit => _facts.TenBit;

    /// <summary>The stream's colour in Media Foundation's numbers (MFVideoTransferMatrix, MFVideoTransferFunction, MFNominalRange_0_255).</summary>
    public int Matrix => _facts.Matrix;

    public int Transfer => _facts.Transfer;
    public bool FullRange => _facts.FullRange;

    /// <summary>One frame's length by the stream's average rate, seconds; 0 when it gives none.</summary>
    public double FrameDuration => _facts.FrameDuration;

    public double DurationSeconds => _facts.DurationSeconds;

    /// <summary>The codec, as FFmpeg names it ("hevc"), for the worker's messages.</summary>
    public string Codec => _facts.Codec;

    /// <summary>
    /// Probes the <paramref name="stream"/>-th video stream (cover pictures not counted, as Media Foundation does not count
    /// them) of a file, to be decoded on the card of DXGI index <paramref name="card"/> (D3D11VA; -1: on the processor). Throws
    /// <see cref="GpuException"/> with the reason when there is no FFmpeg or it cannot read it.
    /// </summary>
    public static FfmpegVideoDecoder Open(string path, int stream, int card = -1)
    {
        if (Missing is { } missing)
            throw new GpuException(unchecked((int)0xC00D5212), missing);
        return new FfmpegVideoDecoder(path, Math.Max(stream, 0), FactsOf(path, Math.Max(stream, 0)), card);
    }

    /// <summary>The next run decodes on the card (D3D11VA): one was asked for, and this file has not failed there.</summary>
    public bool OnCard => _card >= 0 && !_facts.NotOnCard;

    /// <summary>
    /// The next run starts at the key frame at or before <paramref name="ticks"/> (the stream's own time, 100 ns): FFmpeg is
    /// stopped now and started again when the next picture is read. At or before the first picture, it starts from the file's start.
    /// </summary>
    public void Seek(long ticks)
    {
        Stop();
        _from = _first != long.MinValue && ticks <= _first ? long.MinValue : ticks;
        _ended = false;
    }

    /// <summary>
    /// Reads the next picture into <see cref="Picture"/>: its time (the stream's own, 100 ns) and how long it lasts (until the
    /// next picture; 0 for the last one, or when it is not known). False at the end of the stream (until the next seek).
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
                    // The card cannot (no D3D11VA there, a profile it lacks): this file on the processor from now on.
                    _facts.NotOnCard = true;
                    attempt--;
                    continue;
                }

                if (failed is not null && attempt == 0 && _timeBase == "demux" && failed.Contains("time base", StringComparison.OrdinalIgnoreCase))
                {
                    // An FFmpeg before 7.0 names the stream's time base -1.
                    _timeBase = "-1";
                    continue;
                }

                if (failed is null && run.Frames == 0 && run.Target != long.MinValue && attempt < 2)
                {
                    // A seek that found no picture after where it landed (a container longer than its video): from 2 s further
                    // back, so the last picture is reached by reading forward to it, as Media Foundation's seek does.
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

            // A seek moved later for B-frames that landed after the moment: this container seeks by presentation time; again, unmoved.
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

    /// <summary>Starts FFmpeg at <paramref name="from"/> (the stream's own time, 100 ns; long.MinValue for the file's start).</summary>
    private Run Start(long from)
    {
        var biased = from != long.MinValue && _facts.HasBFrames && !_facts.NoSeekBias;
        var arguments = new StringBuilder("-hide_banner -nostdin -nostats -loglevel error -noautorotate");

        // On the card: one decoding thread (the card decodes; FFmpeg's frame threads are not safe with every hardware decoder).
        var onCard = OnCard;
        if (onCard)
            arguments.Append(CultureInfo.InvariantCulture, $" -hwaccel d3d11va -hwaccel_device {_card} -threads 1");
        else
            arguments.Append(CultureInfo.InvariantCulture, $" -threads {DecodeThreads}");
        if (from != long.MinValue)
        {
            // Microseconds, rounded up: FFmpeg reads at most six decimals, and a key frame exactly at the moment must not be missed.
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

    /// <summary>Decode threads for one FFmpeg: a few (a source may have several decoders, each its own FFmpeg).</summary>
    private static int DecodeThreads => Math.Clamp(Environment.ProcessorCount / 3, 2, 4);

    private void Stop()
    {
        _run?.Dispose();
        _run = null;
    }

    public void Dispose() => Stop();

    /// <summary>Starts a tool with its pipes redirected, in the worker's kill-on-close job before it has done much.</summary>
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

        // Dies with the worker (KILL_ON_JOB_CLOSE) however it ends; the worker's own job, nested in HandPeg's.
        JobObjects.Assign(process.Id, ProcessClass.Foreground, efficiencyMode: false);
        return process;
    }

    // ----- The stream's facts (ffprobe, once per file) -----

    /// <summary>What ffprobe says of a stream, and what seeking in its file has taught: kept for the files probed last.</summary>
    private sealed class Facts
    {
        public string Codec = "";
        public int Width, Height;
        public bool TenBit;
        public int Matrix = 1, Transfer;
        public bool FullRange;
        public double FrameDuration, DurationSeconds;
        public bool HasBFrames;

        /// <summary>A seek moved later for B-frames landed after the moment: this file's container seeks by presentation time.</summary>
        public volatile bool NoSeekBias;

        /// <summary>A run on the card failed for this file: it is decoded on the processor.</summary>
        public volatile bool NotOnCard;

        public int LumaPitch => Width * (TenBit ? 2 : 1);
        public int ChromaPitch => (Width + 1) / 2 * 2 * (TenBit ? 2 : 1);
        public int ChromaOffset => LumaPitch * Height;
        public int FrameBytes => ChromaOffset + ChromaPitch * ((Height + 1) / 2);
    }

    // The facts of the files probed last (path, size, time written, stream): a few numbers each, so a source's second decoder
    // and every seek's restart need no ffprobe. At most eight files.
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

        // Its answer within 30 s, the read itself held to it: a file on a share that stalls (or one ffprobe spins on) must not
        // keep the lane that opens it waiting for ever.
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

            // An RGB stream (a screen capture, FFV1 or PNG in RGB) is made YUV by FFmpeg's own conversion: BT.601, limited range.
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

    // What a tool wrote on its error pipe (read to its end beside its output), or nothing when that could not be read.
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

    // The bits of a pixel format's samples, as its name ends ("yuv420p10le", "p010le", "gray12le"); 8 for one that names none.
    [GeneratedRegex(@"(\d+)(le|be)$")]
    private static partial Regex DepthSuffix();

    private static int DepthOf(string format) =>
        DepthSuffix().Match(format) is { Success: true } match && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) ? bits : 8;

    private static bool IsRgb(string format) =>
        format.StartsWith("rgb", StringComparison.Ordinal) || format.StartsWith("bgr", StringComparison.Ordinal) || format.StartsWith("gbr", StringComparison.Ordinal)
        || format.StartsWith("argb", StringComparison.Ordinal) || format.StartsWith("abgr", StringComparison.Ordinal)
        || format.StartsWith("0rgb", StringComparison.Ordinal) || format.StartsWith("0bgr", StringComparison.Ordinal) || format == "pal8";

    /// <summary>FFmpeg's colour space as MFVideoTransferMatrix: 1 BT.709 (also what a stream that says nothing gets, as Media Foundation's path keeps), 2 BT.601, 3 SMPTE 240M, 4 BT.2020.</summary>
    internal static int MatrixOf(string space) => space switch
    {
        "smpte170m" or "bt470bg" or "fcc" => 2,
        "smpte240m" => 3,
        "bt2020nc" or "bt2020c" => 4,
        _ => 1,
    };

    /// <summary>FFmpeg's transfer as MFVideoTransferFunction: 15 PQ and 16 HLG are what the renderer acts on; the rest as Media Foundation numbers them, 0 not said.</summary>
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

    // ----- One run of FFmpeg -----

    /// <summary>One FFmpeg process: its output pipe read picture by picture, its error pipe's stamps kept as they come.</summary>
    private sealed class Run : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _output;
        private readonly object _gate = new();

        // The stamps of the pictures from _offset on (100 ns, the stream's own time); long.MinValue for one not read.
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

        /// <summary>Where it was started (long.MinValue: the file's start), and whether the B-frame move was made.</summary>
        public long Target { get; }

        public bool Biased { get; }

        /// <summary>This run decodes on the card (D3D11VA).</summary>
        public bool OnCard { get; init; }

        /// <summary>The pictures read so far.</summary>
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
                    // A stamp, wherever it is on the line: the encoder's thread writes it outside the log's lock, so it can land
                    // after a log line's "[hevc @ ...]" prefix (and that line's message on the next line).
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

        /// <summary>"pts num/den" as 100 ns; long.MinValue when it cannot be read (the picture's place in the list is kept).</summary>
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

            // pts x num / den seconds, in 100 ns, rounded to the nearest (128 bits: a 90 kHz clock hours in does not overflow).
            var scaled = (Int128)pts * 10_000_000 * num;
            var half = (Int128)den / 2;
            return (long)((scaled >= 0 ? scaled + half : scaled - half) / den);
        }

        /// <summary>Reads one whole picture; false when the pipe ended first (the end of the stream, or FFmpeg stopped).</summary>
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
        /// The stamp of picture <paramref name="number"/> (from 0 in this run), waited for while FFmpeg is still writing (the
        /// stamp of a picture read is there already; the next one's comes as soon as that picture is decoded). False when FFmpeg
        /// ended without it (the last picture's next), or it could not be read.
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

                // What is before the picture before this one is not asked for again.
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

        /// <summary>What FFmpeg said when it ended without a picture: its last lines, or its exit code; null when it said nothing wrong.</summary>
        public string? Failure()
        {
            var exited = _process.WaitForExit(2000);
            lock (_gate)
            {
                // Its last words come down the error pipe after it ended: waited for, briefly.
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
