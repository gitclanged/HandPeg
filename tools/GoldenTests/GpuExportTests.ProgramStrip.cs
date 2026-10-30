using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// gpu-program-strip (Pass 5.0-Verify, the user's call: the Overview Bar drawn from what the GPU engine renders). The
/// game style after Remove Dead Air (a vertical frame, four main clips, three HUD cut-outs with masks, the blurred
/// background) on the made HEVC 60 fps source: its sheet is drawn by a compositor of its own, each picture the finished
/// frame at its moment (against FFmpeg's export of the same timeline, shrunk the same way); drawn again unchanged it
/// draws nothing; with one clip moved it draws only the pictures over that clip; with the key frames known, a moment near
/// one is moved onto it.
/// </summary>
public static partial class GpuExportTests
{
    // How alike a picture and FFmpeg's frame at its moment must be (both shrunk to the picture's size), dB of luma.
    private const double StripFloor = 22;

    /// <summary>gpu-program-strip: the Overview Bar's sheet of the game style, its pictures, its redraws and its moments.</summary>
    public static GraphResult ProgramStrip()
    {
        var text = new StringBuilder("# The Overview Bar's pictures drawn from the sequence by the GPU engine (GpuProgramStrip).\n");
        var problems = new List<string>();
        try
        {
            OnThisThread(() => RunProgramStrip(text, problems));
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("gpu-program-strip", text.ToString(), problems, 0);
    }

    private static async Task<bool> RunProgramStrip(StringBuilder text, List<string> problems)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.WriteLine("  note  no FFmpeg found (%LOCALAPPDATA%\\HandPeg-Data\\deps\\ffmpeg): the Overview Bar's pictures were not checked");
            return false;
        }

        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.GpuExportFrames");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "hevc-60.mkv");
        var source = new EverySource("the game style on the HEVC 60 fps source", file, 0, true, true);
        if (!File.Exists(file) || !File.Exists(StyleMask(folder)))
        {
            MakeEverySources(ffmpeg,
            [
                new("", Path.Combine(folder, "h264-60.mp4"), 0, true), source, new("", Path.Combine(folder, "hevc-420.mkv"), 0, true),
            ]);
        }

        var (width, height, fps, seconds) = Probe(ffmpeg, file);
        var state = EveryState(ffmpeg, folder, source, width, height, fps, seconds, StyleLengths.Sum());

        // FFmpeg's export of the same timeline: what each picture must be.
        if (!File.Exists(state.OutputPath) || new FileInfo(state.OutputPath).Length == 0)
            await FfmpegRunner.RunAsync(FilterGraphBuilder.BuildExportCommand(state).CommandLine, new Progress<FfmpegProgress>(), CancellationToken.None, graphInFile: true);

        using var strip = new GpuProgramStrip();
        var clock = Stopwatch.StartNew();
        var sheet = await strip.DrawNowAsync(state, settingInterval: 1, forStrip: true, CancellationToken.None);
        var first = clock.Elapsed.TotalMilliseconds;
        if (sheet is null)
        {
            problems.Add("the sheet was not drawn (the log says why)");
            return false;
        }

        text.Append(CultureInfo.InvariantCulture, $"canvas {EveryH} x {EveryW}, {StyleLengths.Sum():0.###} s; sheet of {sheet.Count} pictures of {sheet.CellWidth} x {sheet.CellHeight}, {sheet.Columns} a row, one every {sheet.IntervalSeconds:0.###} s; {strip.LastDrawn} drawn\n");
        CheckMoments(sheet, StyleLengths.Sum(), problems);

        // Each picture against FFmpeg's frame at its moment, both shrunk to the picture's size by area.
        var how = $"-map 0:v:0 -vf \"scale={sheet.CellWidth}:{sheet.CellHeight}:flags=area:in_color_matrix=bt709:in_range=tv,format=rgb24\" -f rawvideo -";
        var reference = Read(ffmpeg, state.OutputPath, how);
        var size = sheet.CellWidth * sheet.CellHeight * 3;
        var frames = reference.Length / size;
        var (worst, worstAt) = (double.MaxValue, -1);
        for (var i = 0; i < sheet.Count; i++)
        {
            var frame = Math.Clamp((int)Math.Floor(sheet.Moments[i] * 60 + 1e-6), 0, frames - 1);
            var psnr = LumaPsnr(Rgb(sheet.Cell(i)), reference.AsSpan(frame * size, size));
            if (psnr < worst)
                (worst, worstAt) = (psnr, i);
        }

        text.Append(CultureInfo.InvariantCulture, $"every picture the finished frame at its moment: luma {StripFloor:0} dB or better against FFmpeg's export\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        the sheet: {first:0} ms for {strip.LastDrawn} pictures (the worker started for it); worst picture {worstAt + 1} at {worst:0.0} dB"));
        if (worst < StripFloor)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"picture {worstAt + 1} (at {sheet.Moments[worstAt]:0.###} s) is {worst:0.0} dB from FFmpeg's frame there (floor {StripFloor:0})"));

        // The same state again: nothing to draw.
        clock.Restart();
        var again = await strip.DrawNowAsync(state, settingInterval: 1, forStrip: true, CancellationToken.None);
        text.Append(CultureInfo.InvariantCulture, $"the same state again: {strip.LastDrawn} drawn\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        unchanged: {clock.Elapsed.TotalMilliseconds:0} ms"));
        if (again is null || strip.LastDrawn != 0)
            problems.Add($"the same state drew {strip.LastDrawn} pictures again (none expected)");

        // The third main clip moved: only the pictures over it are drawn.
        var moved = state.Timeline.Clone();
        var mains = moved.Tracks.SelectMany(t => t.Clips).Where(c => c.ContentKind == LayerKind.MainVideo).OrderBy(c => c.Start).ToList();
        var third = mains[2];
        // (A look is never changed in place: a copy of it, moved.)
        var look = System.Text.Json.JsonSerializer.Deserialize<LayerState>(System.Text.Json.JsonSerializer.Serialize(third.State))!;
        look.PositionX += 0.1;
        third.State = look;
        var expected = sheet.Moments.Count(m => m >= third.Start && m < third.End);
        clock.Restart();
        var after = await strip.DrawNowAsync(state with { Timeline = moved }, settingInterval: 1, forStrip: true, CancellationToken.None);
        text.Append(CultureInfo.InvariantCulture, $"the third main clip moved: {strip.LastDrawn} drawn, the {expected} over it expected\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        one clip moved: {clock.Elapsed.TotalMilliseconds:0} ms"));
        if (after is null || strip.LastDrawn != expected)
            problems.Add($"moving the third main clip drew {strip.LastDrawn} pictures ({expected} expected: those over it)");

        // The key frames known (every 4 s of the source): the moment that reads the source near one is moved onto it.
        MediaProbe.NoteIFrames(file, ListIFrames(ffmpeg, file));
        var keyed = await strip.DrawNowAsync(state, settingInterval: 1, forStrip: false, CancellationToken.None);
        if (keyed is null)
        {
            problems.Add("the sheet with key frames known was not drawn");
            return false;
        }

        text.Append("one picture a second, the key frames known: moments ")
            .AppendJoin(" ", keyed.Moments.Select(m => m.ToString("0.00", CultureInfo.InvariantCulture))).Append('\n');
        CheckMoments(keyed, StyleLengths.Sum(), problems, keyFrameReach: 4);
        return true;
    }

    // Each moment in its own stretch of the sheet, in order. With the key frames known (key frames only, 5.0-Release) a
    // picture is moved onto the nearest key frame of its clip however far, so within a GOP (the source's 4 s) of its stretch,
    // two neighbours may share one, and the moments never go back.
    private static void CheckMoments(ThumbnailSheet sheet, double duration, List<string> problems, double keyFrameReach = 0)
    {
        for (var i = 0; i < sheet.Count; i++)
        {
            var (from, to) = (i * sheet.IntervalSeconds, Math.Min((i + 1) * sheet.IntervalSeconds, duration));
            if (sheet.Moments[i] < from - keyFrameReach - 1e-9 || sheet.Moments[i] >= to + keyFrameReach || sheet.Moments[i] < 0 || sheet.Moments[i] >= duration)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"picture {i + 1}'s moment {sheet.Moments[i]:0.###} s is outside its stretch ({from:0.###} to {to:0.###} s, reach {keyFrameReach:0.#} s)"));
            if (i > 0 && sheet.Moments[i] < sheet.Moments[i - 1] - 1e-9)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"picture {i + 1}'s moment {sheet.Moments[i]:0.###} s is before picture {i}'s ({sheet.Moments[i - 1]:0.###} s)"));
        }
    }

    // A picture of the sheet as RGB24.
    private static byte[] Rgb(System.Windows.Media.Imaging.BitmapSource picture)
    {
        var (w, h) = (picture.PixelWidth, picture.PixelHeight);
        var bgra = new byte[w * h * 4];
        new System.Windows.Media.Imaging.FormatConvertedBitmap(picture, System.Windows.Media.PixelFormats.Bgr32, null, 0).CopyPixels(bgra, w * 4, 0);
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);
        return rgb;
    }

    // PSNR of the luma of two RGB24 pictures.
    private static double LumaPsnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var sum = 0.0;
        var pixels = Math.Min(a.Length, b.Length) / 3;
        for (var i = 0; i < pixels; i++)
        {
            var d = 0.2126 * (a[i * 3] - b[i * 3]) + 0.7152 * (a[i * 3 + 1] - b[i * 3 + 1]) + 0.0722 * (a[i * 3 + 2] - b[i * 3 + 2]);
            sum += d * d;
        }

        var mse = sum / Math.Max(pixels, 1);
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
