using System.Diagnostics;
using System.IO;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Live Encoded Preview software encoder (libx264, libx265, libsvtav1, libvpx-vp9) -----
//
// Encodes a paused frame with the export's own software encoder and video arguments (MainViewModel.BuildVideoCodecArguments: -c:v,
// -preset, -crf or -b:v, -pix_fmt, -tune, ...): one FFmpeg run per frame, NV12 on stdin, bitstream on stdout (Annex B for H.264/HEVC,
// OBUs for AV1, IVF for VP9), decoded again by the loopback's MF decoder. FFmpeg only encodes (Law 4: no filters). Runs on the thread
// pool, never the worker thread: the worker polls between scenes, and a newer frame submitted mid-encode replaces any waiting one.
// Paused frames only: software encodes take tens to thousands of ms, so playback shows the plain picture.

/// <summary>Software loopback encoder: one FFmpeg run per paused frame (<see cref="ILoopbackEncoder"/>).</summary>
internal sealed unsafe class FfmpegLoopbackEncoder : ILoopbackEncoder
{
    private readonly string _ffmpeg;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string _muxer;
    private readonly int _width, _height;
    private readonly double _fps;
    private readonly CancellationTokenSource _stop = new();

    // Frame in flight (its task yields the bitstream and sample time) and the newest frame waiting behind it.
    private Task<(byte[]? Bitstream, long Time, string Error)>? _running;
    private (byte[] Frame, long Time)? _waiting;

    public string Name { get; }

    public bool OnAskedCard => true;

    public bool PausedOnly => true;

    public bool TakesP010 => false;

    /// <summary>Slow presets (x265 or SVT-AV1 at their slowest, 4K) can take seconds per frame: abandon after 30 s.</summary>
    public int GiveUpMs => 30_000;

    private FfmpegLoopbackEncoder(string ffmpeg, IReadOnlyList<string> arguments, string muxer, int width, int height, double fps, string name) =>
        (_ffmpeg, _arguments, _muxer, _width, _height, _fps, Name) = (ffmpeg, arguments, muxer, width, height, fps, name);

    /// <summary>
    /// Creates the software encoder from the export's arguments (which name the encoder) and FFmpeg from Settings or the bundled copy.
    /// Null with <see cref="ExportLoopbackState.NoEncoder"/> when FFmpeg or the arguments are missing.
    /// </summary>
    public static FfmpegLoopbackEncoder? Create(in LoopbackConfig config, out ExportLoopbackState state)
    {
        state = ExportLoopbackState.NoEncoder;
        var arguments = SplitArguments(config.SoftwareArguments);
        var ffmpeg = DependencyUpdater.FfmpegPath;

        // Workers with a private data folder (goldens) use the FFmpeg named by HANDPEG_FFMPEG.
        if ((string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) && Environment.GetEnvironmentVariable("HANDPEG_FFMPEG") is { Length: > 0 } named && File.Exists(named))
            ffmpeg = named;
        if (arguments.Count == 0 || string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            AppLog.Write($"Live Encoded Preview: no software encoder loopback ({(arguments.Count == 0 ? "no arguments" : "FFmpeg not found")}).");
            return null;
        }

        var encoder = EncoderOf(arguments) ?? "the software encoder";
        state = ExportLoopbackState.Starting;
        AppLog.Write($"Live Encoded Preview: {encoder} through FFmpeg, paused frames only ({string.Join(' ', arguments)}).");
        return new FfmpegLoopbackEncoder(ffmpeg, arguments, MuxerOf(config.Codec), config.Width, config.Height, config.Fps, $"{encoder} (FFmpeg)");
    }

    /// <summary>Raw bitstream muxer per codec: Annex B (h264, hevc), AV1 OBUs (obu), VP9 in IVF (ivf).</summary>
    internal static string MuxerOf(Guid codec) => codec == MfGuids.Hevc ? "hevc" : codec == MfGuids.Av1 ? "obu" : codec == MfGuids.Vp9 ? "ivf" : "h264";

    /// <summary>The encoder named after -c:v, -vcodec or -codec:v; null when none.</summary>
    internal static string? EncoderOf(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] is "-c:v" or "-vcodec" or "-codec:v")
                return arguments[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Full FFmpeg command for one frame: NV12 at the given size and rate on pipe 0, one frame, no audio, the export's video arguments,
    /// the codec's raw format on pipe 1. Pure (golden).
    /// </summary>
    internal static List<string> CommandFor(IReadOnlyList<string> exportArguments, string muxer, int width, int height, double fps) =>
    [
        "-hide_banner", "-nostats", "-loglevel", "error",
        "-f", "rawvideo", "-pix_fmt", "nv12", "-video_size", FormattableString.Invariant($"{width}x{height}"),
        "-framerate", fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", "pipe:0",
        "-frames:v", "1", "-an", "-sn", "-dn",
        .. exportArguments,
        "-f", muxer, "pipe:1",
    ];

    /// <summary>Splits arguments like a command line (whitespace; double quotes around values with spaces). Pure (golden).</summary>
    internal static List<string> SplitArguments(string? text)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text ?? "")
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    /// <summary>First frame payload of an IVF file (VP9): after the file header (>= 32 bytes) and a 12-byte frame header (size, timestamp); null when absent.</summary>
    internal static byte[]? FirstIvfFrame(byte[] ivf)
    {
        if (ivf.Length < 44 || ivf[0] != (byte)'D' || ivf[1] != (byte)'K' || ivf[2] != (byte)'I' || ivf[3] != (byte)'F')
            return null;
        var headerBytes = BitConverter.ToUInt16(ivf, 6);
        if (headerBytes < 32 || ivf.Length < headerBytes + 12)
            return null;
        var size = (int)BitConverter.ToUInt32(ivf, headerBytes);
        return size > 0 && headerBytes + 12 + size <= ivf.Length ? ivf.AsSpan(headerBytes + 12, size).ToArray() : null;
    }

    public void SetQualityVsSpeed(int value)
    {
        // The preset comes from the export's arguments; playback frames never reach this encoder.
    }

    /// <summary>Takes ownership of the sample (released here), copies its NV12 bytes and encodes now or after the frame in flight.</summary>
    public void Submit(nint sample, bool forceKey)
    {
        try
        {
            var time = Mf.SampleTime(sample);
            if (Mf.GetBufferByIndex(sample, 0, out var buffer) < 0)
                return;
            try
            {
                MfEncoderMft.Check(Mf.Lock(buffer, out var data, out var length), "IMFMediaBuffer::Lock (software loopback)");
                try
                {
                    var frame = new byte[Math.Min((long)length, (long)_width * _height * 3 / 2)];
                    new ReadOnlySpan<byte>(data, frame.Length).CopyTo(frame);
                    _waiting = (frame, time);
                }
                finally
                {
                    _ = Mf.Unlock(buffer);
                }
            }
            finally
            {
                Com.Release(ref buffer);
            }
        }
        finally
        {
            Com.Release(ref sample);
        }

        StartWaiting();
    }

    private void StartWaiting()
    {
        if (_running is { IsCompleted: false } || _waiting is not { } next || _stop.IsCancellationRequested)
            return;
        _waiting = null;
        _running = Task.Run(() => Encode(next.Frame, next.Time));
    }

    // One FFmpeg run: write the frame, read all output, wait for exit (killed past GiveUpMs or on Dispose).
    private (byte[]? Bitstream, long Time, string Error) Encode(byte[] frame, long time)
    {
        var clock = Stopwatch.StartNew();
        var start = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in CommandFor(_arguments, _muxer, _width, _height, _fps))
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return (null, time, "FFmpeg did not start");
            using var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                process.StandardInput.BaseStream.Write(frame);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // FFmpeg exited before reading the frame; its stderr explains why.
            }

            // Poll in 100 ms steps so Dispose (preview off, setting changed) kills the run promptly.
            while (!process.WaitForExit(100) && clock.ElapsedMilliseconds < GiveUpMs && !_stop.IsCancellationRequested)
            {
            }

            if (!process.HasExited || _stop.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                return (null, time, _stop.IsCancellationRequested ? "" : $"FFmpeg took more than {GiveUpMs / 1000} s");
            }

            reading.Wait(2000);
            var said = errors.Wait(2000) ? errors.Result.Trim() : "";
            if (process.ExitCode != 0 || output.Length == 0)
                return (null, time, $"FFmpeg ended with {process.ExitCode}{(said.Length > 0 ? ": " + said : "")}");

            var bitstream = output.ToArray();
            if (_muxer == "ivf")
                bitstream = FirstIvfFrame(bitstream) ?? [];
            AppLog.Write(FormattableString.Invariant($"Live Encoded Preview: {Name} encoded a {_width}x{_height} frame in {clock.ElapsedMilliseconds} ms ({bitstream.Length:N0} bytes)."));
            return (bitstream.Length > 0 ? bitstream : null, time, bitstream.Length > 0 ? "" : "no frame in FFmpeg's output");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Write($"Live Encoded Preview: {Name} failed", ex);
            return (null, time, ex.Message);
        }
    }

    /// <summary>
    /// The encoded frame as an in-memory bitstream sample (with the frame's sample time) once FFmpeg finishes, else 0. A failed run throws
    /// GpuException (the loopback reports it and abandons the encoder).
    /// </summary>
    public nint TryTakeOutput()
    {
        if (_running is not { IsCompleted: true } done)
            return 0;
        _running = null;
        var (bitstream, time, error) = done.IsCompletedSuccessfully ? done.Result : (null, 0, done.Exception?.GetBaseException().Message ?? "the run failed");
        StartWaiting();
        if (bitstream is null)
        {
            if (_stop.IsCancellationRequested)
                return 0;
            AppLog.Write($"Live Encoded Preview: {Name}: {error}");
            throw new GpuException(unchecked((int)0x80004005), $"{Name}: {error}");
        }

        var sample = MfEncoderMft.Sample((uint)bitstream.Length);
        nint buffer = 0;
        try
        {
            MfEncoderMft.Check(Mf.GetBufferByIndex(sample, 0, out buffer), "IMFSample::GetBufferByIndex (software loopback)");
            MfEncoderMft.Check(Mf.Lock(buffer, out var data, out _), "IMFMediaBuffer::Lock (software loopback)");
            try
            {
                bitstream.CopyTo(new Span<byte>(data, bitstream.Length));
            }
            finally
            {
                _ = Mf.Unlock(buffer);
            }

            MfEncoderMft.Check(Mf.SetCurrentLength(buffer, (uint)bitstream.Length), "IMFMediaBuffer::SetCurrentLength (software loopback)");
            _ = Mf.SetSampleTime(sample, time);
            _ = Mf.SetSampleDuration(sample, (long)Math.Round(10_000_000 / Math.Max(_fps, 1)));
            return sample;
        }
        catch
        {
            Com.Release(ref sample);
            throw;
        }
        finally
        {
            Com.Release(ref buffer);
        }
    }

    /// <summary>Nothing is buffered: each run outputs its single frame whole.</summary>
    public bool Drain() => true;

    public byte[]? SequenceHeader() => null;

    /// <summary>Kills any run in flight (on its own thread, at its next poll) and drops the waiting frame.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _waiting = null;
    }
}
