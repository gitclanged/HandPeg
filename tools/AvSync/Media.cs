using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// What the harness reads back with FFmpeg: sound as mono samples and pictures as small grey frames, each cut on the
/// file's own time stamps (-copyts and a trim by time stamp, never by position), so that a moment means the same
/// thing in the engine's output (written on the sequence's clock) and in the files the layers came from.
/// </summary>
internal static class Media
{
    public const int Rate = 48000;

    public static string Ffmpeg { get; set; } = "ffmpeg";

    public static string Ffprobe { get; set; } = "ffprobe";

    /// <summary>
    /// A file's sound from the time stamp <paramref name="from"/> for <paramref name="seconds"/>, mono, at 48 kHz unless asked otherwise. The
    /// file is opened two seconds early (so a decoder that needs a run-in, as Opus does, has settled) and cut exactly.
    /// </summary>
    /// <param name="streams">The audio streams to mix (FFmpeg's numbering among the audio streams), added up as the live mix adds them.</param>
    public static float[] Sound(string file, double from, double seconds, IReadOnlyList<int> streams, bool seek = true, int rate = Rate)
    {
        var cut = $"atrim=start={N(from)}:end={N(from + seconds)},aresample={rate},aformat=sample_fmts=flt:channel_layouts=mono";
        var graph = streams.Count == 1
            ? $"[0:a:{streams[0]}]{cut}[out]"
            : $"{string.Concat(streams.Select(s => $"[0:a:{s}]"))}amix=inputs={streams.Count}:normalize=0:duration=first,{cut}[out]";
        var bytes = Run(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", .. Opening(file, seek ? from - 2 : null),
            "-filter_complex", graph, "-map", "[out]", "-f", "f32le", "pipe:1"]);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    /// <summary>
    /// The frames of a file's first video stream whose time stamps fall in [from, from + seconds), each the region
    /// <paramref name="crop"/> picks out (filters; null: the whole frame) scaled to <paramref name="width"/> x <paramref name="height"/> grey
    /// pixels, with its time stamp.
    /// </summary>
    public static List<(double Pts, byte[] Pixels)> Frames(string file, double from, double seconds, string? crop, int width, int height, bool seek = true)
    {
        var filters = new List<string> { $"trim=start={N(from)}:end={N(from + seconds)}" };
        if (crop is not null)
            filters.Add(crop);
        filters.Add($"scale={width}:{height}:flags=area,format=gray,showinfo");
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        string[] arguments = ["-hide_banner", "-nostdin", .. Opening(file, seek ? from - 3 : null), "-map", "0:v:0",
                     "-vf", string.Join(",", filters), "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"];
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var log = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();

        // showinfo writes one line per frame, with its time stamp, in the order the frames are written.
        var stamps = log.Result.Split('\n')
            .Select(line => line.IndexOf(" pts_time:", StringComparison.Ordinal) is var at and >= 0 && line.Contains("Parsed_showinfo", StringComparison.Ordinal)
                ? double.Parse(line[(at + 10)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture)
                : double.NaN)
            .Where(t => !double.IsNaN(t)).ToList();
        var size = width * height;
        var bytes = buffer.ToArray();
        var frames = new List<(double, byte[])>();
        for (var i = 0; i < stamps.Count && (i + 1) * size <= bytes.Length; i++)
            frames.Add((stamps[i], bytes.AsSpan(i * size, size).ToArray()));
        return frames;
    }

    // A file opened on its own time stamps (-copyts), sought to a time stamp when one is given (a small file of the
    // harness's own is read from its start: FFmpeg seeking before the first time stamp of a Matroska file finds nothing).
    // -ss counts from the container's start (FFmpeg adds its start_time): -seek_timestamp with -copyts was measured to
    // lose 0.78 s of an MPEG-TS file's sound starting at 2.78 s, so the time stamp is made into a distance from there.
    private static string[] Opening(string file, double? seekTo)
    {
        var start = seekTo is { } at && at > 0 ? at - (Starts.GetOrAdd(file, f => ContainerStart(f) ?? 0)) : 0;
        return start > 0 ? ["-copyts", "-ss", N(start), "-i", file] : ["-copyts", "-i", file];
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> Starts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The time stamp a file's first stream of one kind ("v" or "a") starts at, as FFmpeg reports it; null when it has none.</summary>
    public static double? StreamStart(string file, string kind)
    {
        var text = Encoding.UTF8.GetString(Run(Ffprobe, ["-v", "error", "-select_streams", $"{kind}:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", file])).Trim();
        return double.TryParse(text.Split('\n')[0].Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ? start : null;
    }

    /// <summary>Where a file's container's clock starts (FFmpeg's format start_time: what the main video's clip offsets count from); null when it says none.</summary>
    public static double? ContainerStart(string file)
    {
        var text = Encoding.UTF8.GetString(Run(Ffprobe, ["-v", "error", "-show_entries", "format=start_time", "-of", "csv=p=0", file])).Trim();
        return double.TryParse(text.Split('\n')[0].Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ? start : null;
    }

    /// <summary>The time stamps of a file's first packets of one kind ("a" or "v").</summary>
    public static double FirstPts(string file, string kind)
    {
        var text = Encoding.UTF8.GetString(Run(Ffprobe, ["-v", "error", "-select_streams", $"{kind}:0", "-show_entries", "packet=pts_time",
            "-of", "csv=p=0", "-read_intervals", "%+#1", file])).Trim();
        return double.Parse(text.Split('\n')[0].Trim().TrimEnd(','), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where <paramref name="segment"/> is found in <paramref name="reference"/>: the lag in samples (to a fraction
    /// of one), the normalized correlation there, and the best correlation at least 5 ms away from it (how sure the
    /// match is: close to the best, and a repeating sound could be matched a period off).
    /// </summary>
    public static (double Lag, double Score, double Runner) Locate(float[] segment, float[] reference)
    {
        if (segment.Length == 0 || reference.Length < segment.Length)
            return (double.NaN, 0, 0);

        var n = 1;
        while (n < segment.Length + reference.Length)
            n <<= 1;
        var a = new Complex[n];
        var b = new Complex[n];
        for (var i = 0; i < reference.Length; i++)
            a[i] = reference[i];
        for (var i = 0; i < segment.Length; i++)
            b[i] = segment[i];
        Fft(a, false);
        Fft(b, false);
        for (var i = 0; i < n; i++)
            a[i] *= Complex.Conjugate(b[i]);
        Fft(a, true);

        var prefix = new double[reference.Length + 1];
        for (var i = 0; i < reference.Length; i++)
            prefix[i + 1] = prefix[i] + (double)reference[i] * reference[i];
        var energy = segment.Sum(x => (double)x * x);
        var lags = reference.Length - segment.Length;
        var scores = new double[lags + 1];
        var best = 0;
        for (var lag = 0; lag <= lags; lag++)
        {
            var e = prefix[lag + segment.Length] - prefix[lag];
            scores[lag] = e > 1e-12 && energy > 1e-12 ? a[lag].Real / n / Math.Sqrt(e * energy) : 0;
            if (scores[lag] > scores[best])
                best = lag;
        }

        var runner = 0.0;
        for (var lag = 0; lag <= lags; lag++)
        {
            if (Math.Abs(lag - best) > Rate / 200 && scores[lag] > runner)
                runner = scores[lag];
        }

        // The peak's top, between samples, from the parabola through it and its neighbours.
        var fraction = 0.0;
        if (best > 0 && best < lags)
        {
            double y0 = scores[best - 1], y1 = scores[best], y2 = scores[best + 1];
            var d = y0 - 2 * y1 + y2;
            if (Math.Abs(d) > 1e-12)
                fraction = 0.5 * (y0 - y2) / d;
        }

        return (best + fraction, scores[best], runner);
    }

    /// <summary>How loud a stretch of sound is, as its root mean square.</summary>
    public static double Level(float[] samples) => samples.Length == 0 ? 0 : Math.Sqrt(samples.Sum(x => (double)x * x) / samples.Length);

    private static void Fft(Complex[] x, bool inverse)
    {
        var n = x.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (x[i], x[j]) = (x[j], x[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var u = x[i + k];
                    var v = x[i + k + length / 2] * w;
                    x[i + k] = u + v;
                    x[i + k + length / 2] = u - v;
                    w *= step;
                }
            }
        }
    }

    private static byte[] Run(string exe, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var log = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {log.Result.Trim()}");
        return buffer.ToArray();
    }

    public static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
