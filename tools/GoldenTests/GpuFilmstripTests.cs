using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// GPU filmstrips (5.0D, GpuFilmstrips): the strip's layout against FFmpeg's (scale=-2's widths, the frames' number and
/// moments, the factor a frame is drawn at), the snapping of moments onto key frames, and the shrink of a frame drawn
/// larger; each answer compared with what it should be as well as with the golden file. Then, when HandPeg's FFmpeg is
/// installed, a long-GOP clip made here gets its strip made both ways (FFmpeg's, and the compositor's through a worker of
/// its own): the two pictures must be the same size and layout, and the compositor's must show the clip. What that
/// measures goes to the console, never into the golden file (it needs FFmpeg and a graphics card).
/// </summary>
public static class GpuFilmstripTests
{
    public static IEnumerable<GraphResult> All()
    {
        var text = new StringBuilder("# GPU filmstrips (5.0D): the strip's layout as FFmpeg's, key-frame snapping, the shrink, and a strip made both ways.\n");
        var problems = new List<string>();
        try
        {
            Layouts(text, problems);
            Moments(text, problems);
            Snapping(text, problems);
            Shrinking(text, problems);
            BothWays(problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("gpu-filmstrip", text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static string Times(IEnumerable<double> times) => string.Join(" ", times.Select(t => t.ToString("0.0000", CultureInfo.InvariantCulture)));

    // ----- The layout: scale=-2's width, the frames' number and height, the factor -----

    private static void Layouts(StringBuilder text, List<string> problems)
    {
        Line(text, $"layouts (source, height asked, frames asked, length -> frames x width x height, factor, strip):");
        (int W, int H, int Height, int Count, double Seconds, string? Expected)[] cases =
        [
            (1920, 1080, 54, 25, 600, "25 x 96 x 54, factor 3, strip 2400 x 54"),
            (1080, 1920, 54, 25, 600, "25 x 30 x 54, factor 5, strip 750 x 54"),
            (3840, 2160, 480, 15, 14400, "15 x 854 x 480, factor 1, strip 12810 x 480"),
            (3840, 2160, 54, 25, 14400, "25 x 96 x 54, factor 5, strip 2400 x 54"),
            (1920, 1080, 55, 25, 0.1, "1 x 96 x 54, factor 3, strip 96 x 54"),
            (640, 480, 54, 50, 30, "40 x 72 x 54, factor 2, strip 2880 x 54"),
            (720, 480, 54, 8, 30, "8 x 82 x 54, factor 2, strip 656 x 54"),
            (1920, 1080, 4000, 4, 30, "4 x 1920 x 1080, factor 1, strip 7680 x 1080"),
            (100, 4000, 16, 8, 30, null),
        ];

        foreach (var (w, h, height, count, seconds, expected) in cases)
        {
            var layout = GpuFilmstrips.Layout(count, seconds, height, w, h);
            var shown = layout is { } l ? string.Create(CultureInfo.InvariantCulture, $"{l.Count} x {l.FrameWidth} x {l.Height}, factor {l.Factor}, strip {l.Width} x {l.Height}") : null;
            Line(text, $"  {w}x{h}, {height} px, {count} frames, {seconds} s -> {shown ?? "not drawn by the compositor"}");
            if (shown != expected)
                problems.Add($"layout of {w}x{h} at {height} px: {shown ?? "none"}, not {expected ?? "none"}");
        }

        // scale=-2: the nearest even width (av_rescale rounds half away from zero).
        foreach (var (w, h, height, width) in new[] { (1920, 1080, 54, 96), (1280, 720, 54, 96), (853, 480, 54, 96), (1080, 1920, 54, 30), (1440, 1080, 54, 72), (2560, 1080, 54, 128) })
        {
            var got = GpuFilmstrips.FrameWidth(w, h, height);
            Line(text, $"  scale=-2:{height} of {w}x{h} is {got} wide");
            if (got != width)
                problems.Add($"scale=-2:{height} of {w}x{h} gave {got}, not {width}");
        }
    }

    // ----- The moments: FFmpeg's -ss of each frame -----

    private static void Moments(StringBuilder text, List<string> problems)
    {
        Line(text, $"moments (frames, length -> seconds from the file's start):");
        (int Count, double Seconds, double[] Expected)[] cases =
        [
            (4, 10, [1.25, 3.75, 6.25, 8.75]),
            (3, 1, [1 / 6.0, 0.5, 0.5]),
            (1, 0.1, [0]),
            (5, 3600, [360, 1080, 1800, 2520, 3240]),
        ];

        foreach (var (count, seconds, expected) in cases)
        {
            var got = GpuFilmstrips.Moments(GpuFilmstrips.FrameCount(count, seconds), seconds);
            Line(text, $"  {count} frames of {seconds} s: {Times(got)}");
            if (got.Length != expected.Length || got.Zip(expected).Any(p => Math.Abs(p.First - p.Second) > 1e-9))
                problems.Add($"moments of {count} frames of {seconds} s: {Times(got)}, not {Times(expected)}");
        }
    }

    // ----- Snapping onto key frames -----

    private static void Snapping(StringBuilder text, List<string> problems)
    {
        const double Lead = 0.004;
        Line(text, $"snapping (lead {Lead} s):");

        // A 4-hour recording, key frames every 7.3 s: every moment is moved onto the nearest key frame (one decode each).
        var keys = Enumerable.Range(0, (int)(14400 / 7.3) + 1).Select(i => i * 7.3).ToArray();
        var moments = GpuFilmstrips.Moments(15, 14400);
        var snapped = GpuFilmstrips.SnapToKeyFrames(moments, keys, 14400 / 15.0, Lead);
        Line(text, $"  4 h, key frames every 7.3 s, 15 frames: {Times(snapped)}");
        for (var i = 0; i < moments.Length; i++)
        {
            var nearest = keys.MinBy(k => Math.Abs(k - moments[i]));
            if (Math.Abs(snapped[i] - (nearest + Lead)) > 1e-9)
                problems.Add($"4 h: frame {i} at {moments[i]} went to {snapped[i]}, not the key frame {nearest} (+ lead)");
        }

        // A 10 s clip, a key frame every 2 s from 0.1, 20 frames (0.5 s apart): only the moments within 0.25 s of a key move.
        keys = [0.1, 2.1, 4.1, 6.1, 8.1];
        moments = GpuFilmstrips.Moments(20, 10);
        snapped = GpuFilmstrips.SnapToKeyFrames(moments, keys, 10 / 20.0, Lead);
        Line(text, $"  10 s, key frames every 2 s, 20 frames: {Times(snapped)}");
        for (var i = 0; i < moments.Length; i++)
        {
            var near = keys.Where(k => Math.Abs(k - moments[i]) < 0.25).ToList();
            var expected = near.Count > 0 ? near[0] + Lead : moments[i];
            if (Math.Abs(snapped[i] - expected) > 1e-9)
                problems.Add($"10 s: frame {i} at {moments[i]} went to {snapped[i]}, not {expected}");
        }

        // No key frames known: every moment is kept.
        snapped = GpuFilmstrips.SnapToKeyFrames(moments, [], 0.5, Lead);
        Line(text, $"  no key frames: {(snapped.SequenceEqual(moments) ? "every moment kept" : Times(snapped))}");
        if (!snapped.SequenceEqual(moments))
            problems.Add("with no key frames the moments were moved");

        // As near as two key frames: the earlier (where a seek lands anyway).
        snapped = GpuFilmstrips.SnapToKeyFrames([5.0], [4.9, 5.1], 1, Lead);
        Line(text, $"  5.0 between 4.9 and 5.1: {Times(snapped)}");
        if (Math.Abs(snapped[0] - (4.9 + Lead)) > 1e-9)
            problems.Add($"a tie went to {snapped[0]}, not the earlier key frame");

        // Two moments a second apart never share a key frame: only the one nearer than half the spacing moves.
        snapped = GpuFilmstrips.SnapToKeyFrames([1.0, 2.0], [1.49], 1, Lead);
        Line(text, $"  1.0 and 2.0 round 1.49: {Times(snapped)}");
        if (Math.Abs(snapped[0] - (1.49 + Lead)) > 1e-9 || snapped[1] != 2.0)
            problems.Add($"1.0 and 2.0 round 1.49 went to {Times(snapped)}");
        snapped = GpuFilmstrips.SnapToKeyFrames([1.0, 2.0], [1.5], 1, Lead);
        Line(text, $"  1.0 and 2.0 round 1.5 (half the spacing from both): {Times(snapped)}");
        if (snapped[0] != 1.0 || snapped[1] != 2.0)
            problems.Add($"1.0 and 2.0 round 1.5 went to {Times(snapped)}: a key frame half the spacing away is not near enough");
    }

    // ----- The shrink of a frame drawn larger -----

    private static void Shrinking(StringBuilder text, List<string> problems)
    {
        // A 6 x 4 frame drawn at twice the size of a 3 x 2 one, put at x = 1 of a strip 5 wide.
        const int Factor = 2, Width = 3, Height = 2, StripWidth = 5;
        var drawn = new byte[Width * Factor * Height * Factor * 4];
        for (var y = 0; y < Height * Factor; y++)
        {
            for (var x = 0; x < Width * Factor; x++)
            {
                var at = (y * Width * Factor + x) * 4;
                (drawn[at], drawn[at + 1], drawn[at + 2], drawn[at + 3]) = ((byte)(x * 40 + y), (byte)(255 - x * 40), (byte)(y * 60), 0);
            }
        }

        var strip = new byte[StripWidth * Height * 4];
        GpuFilmstrips.Shrink(drawn, Width * Factor, Factor, strip, StripWidth * 4, 1, Width, Height);
        var shown = new StringBuilder();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < StripWidth; x++)
            {
                var at = (y * StripWidth + x) * 4;
                shown.Append(CultureInfo.InvariantCulture, $"({strip[at]},{strip[at + 1]},{strip[at + 2]},{strip[at + 3]}) ");
                if (x == 0 || x == StripWidth - 1)
                {
                    if (strip[at] != 0 || strip[at + 3] != 0)
                        problems.Add($"the shrink wrote outside its frame, at {x},{y}");
                    continue;
                }

                // The mean of the 2 x 2 square, rounded; opaque.
                var (sx, sy) = ((x - 1) * Factor, y * Factor);
                var b = (sx * 40 + sy + (sx + 1) * 40 + sy + sx * 40 + sy + 1 + (sx + 1) * 40 + sy + 1 + 2) / 4;
                var g = (2 * (255 - sx * 40) + 2 * (255 - (sx + 1) * 40) + 2) / 4;
                var r = (2 * (sy * 60) + 2 * ((sy + 1) * 60) + 2) / 4;
                if ((strip[at], strip[at + 1], strip[at + 2], strip[at + 3]) != ((byte)b, (byte)g, (byte)r, (byte)255))
                    problems.Add($"the shrink at {x},{y}: ({strip[at]},{strip[at + 1]},{strip[at + 2]},{strip[at + 3]}), not ({b},{g},{r},255)");
            }

            shown.Append("/ ");
        }

        Line(text, $"shrink of a 6x4 frame by 2 into a strip 5 wide at x = 1: {shown.ToString().TrimEnd()}");
    }

    // ----- A strip made both ways (needs HandPeg's FFmpeg and a graphics card; problems only, never the golden text) -----

    private static void BothWays(List<string> problems)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the GPU filmstrip was not compared with FFmpeg's");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests", "filmstrip-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var settings = AppSettings.Current;
        var (savedFfmpeg, savedFfprobe) = (settings.FfmpegPath, settings.FfprobePath);
        try
        {
            settings.FfmpegPath = ffmpeg;
            settings.FfprobePath = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");

            // A long-GOP clip: 40 s at 30 fps, a key frame every 10 s, with B-frames (an OBS recording's shape, short).
            var clip = Path.Combine(folder, "long-gop.mp4");
            Run(ffmpeg, $"-f lavfi -i \"testsrc2=s=640x360:r=30:d=40\" -c:v libx264 -preset ultrafast -g 300 -keyint_min 300 -sc_threshold 0 -bf 2 -pix_fmt yuv420p -y \"{clip}\"");

            const int Frames = 8, Height = 54;
            const string ByFfmpeg = "ffmpeg", ByGpu = "gpu";
            var made = new Dictionary<string, (int Width, int Height, double Ms)>();
            foreach (var engine in new[] { ByFfmpeg, ByGpu })
            {
                // FFmpeg's strip is made with the compositor held off (the runner would otherwise hand it to the GPU).
                GpuFilmstrips.ForceFfmpeg = engine == ByFfmpeg;
                var path = Path.Combine(folder, $"strip-{engine}.jpg");
                var clock = Stopwatch.StartNew();
                var ok = engine == ByGpu
                    ? Task.Run(() => GpuFilmstrips.TryMakeAsync(clip, path, Frames, 40, Height, CancellationToken.None)).GetAwaiter().GetResult()
                    : Task.Run(() => FfmpegRunner.GenerateFilmstripAsync(clip, path, Frames, 40, CancellationToken.None, Height)).GetAwaiter().GetResult();
                if (!ok || !File.Exists(path))
                {
                    problems.Add($"the {engine} strip was not made");
                    continue;
                }

                using var file = File.OpenRead(path);
                var picture = BitmapDecoder.Create(file, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                made[engine] = (picture.PixelWidth, picture.PixelHeight, clock.Elapsed.TotalMilliseconds);

                // The compositor's frames show the clip: neither the background it draws where no picture is, nor black.
                if (engine == ByGpu)
                {
                    var stride = picture.PixelWidth * 4;
                    var pixels = new byte[stride * picture.PixelHeight];
                    new FormatConvertedBitmap(picture, System.Windows.Media.PixelFormats.Bgr32, null, 0).CopyPixels(pixels, stride, 0);
                    var frameWidth = picture.PixelWidth / Frames;
                    for (var i = 0; i < Frames; i++)
                    {
                        var at = (picture.PixelHeight / 2 * picture.PixelWidth + i * frameWidth + frameWidth / 2) * 4;
                        var (b, g, r) = (pixels[at], pixels[at + 1], pixels[at + 2]);
                        if (b > 230 && g < 25 && r > 230 || b + g + r < 6)
                            problems.Add($"GPU strip frame {i + 1} shows ({r},{g},{b}) in its middle: not the clip");
                    }
                }
            }

            if (made.TryGetValue(ByFfmpeg, out var byFfmpeg) && made.TryGetValue(ByGpu, out var byGpu))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"        strip of 8 frames of a 40 s long-GOP clip: FFmpeg {byFfmpeg.Width} x {byFfmpeg.Height} in {byFfmpeg.Ms:0} ms, GPU {byGpu.Width} x {byGpu.Height} in {byGpu.Ms:0} ms (the worker's start included)"));
                if ((byFfmpeg.Width, byFfmpeg.Height) != (byGpu.Width, byGpu.Height))
                    problems.Add($"the strips differ in size: FFmpeg's {byFfmpeg.Width} x {byFfmpeg.Height}, the compositor's {byGpu.Width} x {byGpu.Height}");
                if (GpuFilmstrips.Layout(Frames, 40, Height, 640, 360) is not { } layout || (layout.Width, layout.Height) != (byGpu.Width, byGpu.Height) || byGpu.Width % Frames != 0)
                    problems.Add($"the compositor's strip ({byGpu.Width} x {byGpu.Height}) is not {Frames} frames of the layout's width");
            }
        }
        finally
        {
            (settings.FfmpegPath, settings.FfprobePath) = (savedFfmpeg, savedFfprobe);
            GpuFilmstrips.ForceFfmpeg = false;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? FindFfmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[] { Path.Combine(local, "HandPeg-Data", "deps", "ffmpeg", "ffmpeg.exe"), Path.Combine(local, "HandPeg", "deps", "ffmpeg", "ffmpeg.exe") })
        {
            if (File.Exists(candidate) && File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "ffprobe.exe")))
                return candidate;
        }

        return null;
    }

    private static void Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, "-hide_banner -loglevel error " + arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed: {errors}");
    }
}
