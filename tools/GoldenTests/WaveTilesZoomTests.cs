using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.4: the Timeline's waveform tiles through a zoom glide (wave-tiles-zoom). The real timeline control on this STA thread, laid out
/// at a size of its own with no window and drawn into a bitmap: its first render makes its tiles as always; twelve zoom steps across an
/// octave in, then twenty-four out across two, make no tile while the scale moves (the tiles of a step either side stand in, stretched:
/// coarser zooming in, finer zooming out) and the sound is drawn after every step; the rest after the last step makes exactly the final
/// step's tiles, once; the tiles kept never pass the cap. Every line says what it proved.
/// </summary>
public static class WaveTilesZoomTests
{
    public static IEnumerable<GraphResult> All()
    {
        var text = new StringBuilder("# The Timeline's waveforms through a zoom (5.4.4): stand-ins while the scale moves, the crisp tiles made once it rests.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            ZoomGlide(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("wave-tiles-zoom", text.ToString(), problems, 0);
    }

    /// <summary>The cases that need the WPF Application (run after UiScaleTests made it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    private const int Width = 1000, Height = 160;
    private const double SoundSeconds = 16, TilePixels = 512;

    private static void ZoomGlide(Action<bool, string> expect)
    {
        // One audio track, one block from 0 s with a 16 s sound loud all through (each pair at 0.8 of full scale), ten seconds across at first.
        var surface = new UnifiedTimelineControl();
        var (size, place) = (new Size(Width, Height), new Rect(0, 0, Width, Height));
        var pairs = (int)(SoundSeconds * Waveforms.PeaksPerSecond);
        var (lows, highs) = (new float[pairs], new float[pairs]);
        for (var i = 0; i < pairs; i++)
            (lows[i], highs[i]) = (-0.8f, 0.8f);
        var sound = new WaveformData(lows, highs, Brushes.White);
        var grey = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        grey.Freeze();

        var row = new TimelineRow { Key = "A", Data = "A", Kind = TimelineRowKind.Audio, Height = 96, Name = "A" };
        var block = new TimelineBlock { Left = 0, Width = SoundSeconds * (Width - surface.HeaderWidth) / 10, Fill = grey, SoundWindow = new WaveWindow(sound, 0, SoundSeconds) };
        row.Blocks.Add(block);
        surface.SetView(0, 10, SoundSeconds);
        surface.SetRows([row]);

        void Lay()
        {
            surface.Measure(size);
            surface.Arrange(place);
            surface.UpdateLayout();
        }

        // A zoom step as the host makes it: the view's span, then the blocks laid out again at the new scale.
        void Zoom(double span)
        {
            surface.SetView(0, span, SoundSeconds);
            block.Width = SoundSeconds * surface.PixelsPerSecond;
            surface.Redraw();
            Lay();
        }

        // Whether the sound is drawn at 2 s, a third of the way down its block (the band's fill, lighter than the block's grey).
        bool Shows()
        {
            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[Width * Height * 4];
            bitmap.CopyPixels(pixels, Width * 4, 0);
            var lane = surface.LaidLanes.First(l => Equals(l.Key, "A"));
            var (x, y) = ((int)(surface.HeaderWidth + 2 * surface.PixelsPerSecond), (int)(lane.Top + 2 + 0.3 * (lane.Height - 4)));
            var at = (y * Width + x) * 4;
            return (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3.0 > 0x50 + 40;
        }

        // The tiles of the scale's zoom step the rows' render wants: the sound's seconds within the view and half a view either side.
        int Wanted()
        {
            var (scale, width) = (surface.PixelsPerSecond, surface.TimeAreaWidth);
            var margin = Math.Max(width / 2, 256);
            var bucket = (int)Math.Floor(Math.Log2(scale));
            var tileSeconds = TilePixels / Math.ScaleB(1.0, bucket);
            var end = Math.Min(SoundSeconds, (width + margin) / scale);
            var count = 0;
            for (var tile = 0L; tile * tileSeconds < end; tile++)
                count++;
            return count;
        }

        static int Built() => UnifiedTimelineControl.WaveTilesForTests.Built;
        var mostKept = 0;
        void Kept() => mostKept = Math.Max(mostKept, UnifiedTimelineControl.WaveTilesForTests.Kept);

        // (a) The first render: not a zoom, its tiles made as always.
        var before = Built();
        Lay();
        var first = Built() - before;
        Kept();
        Console.WriteLine($"  (the first render made {first} tile(s), {Wanted()} in view)");
        expect(first > 0 && first == Wanted() && !surface.WaveZoomMoving && Shows(),
            "the first render makes its tiles at once, as before (not a zoom): its step's tiles in view, and the sound is drawn");

        // (b) Twelve steps in, from 10 s across to 5 s (an octave): nothing made while the scale moves, the coarser step stands in.
        var (made, blank) = (0, 0);
        for (var k = 1; k <= 12; k++)
        {
            before = Built();
            Zoom(10 / Math.Pow(2, k / 12.0));
            made += Built() - before;
            blank += Shows() ? 0 : 1;
            Kept();
        }

        expect(made == 0 && surface.WaveZoomMoving, $"twelve zoom steps in across an octave make no tile while the scale moves ({made} made)");
        expect(blank == 0, "and the sound is drawn after every step (the coarser step's tiles stand in, stretched and cut to each tile)");

        // (c) The rest: the final step's tiles made once, exactly those in view; a second rest makes none.
        before = Built();
        surface.SettleWaveZoom();
        Lay();
        var settled = Built() - before;
        var wanted = Wanted();
        Console.WriteLine($"  (the rest after zooming in made {settled} tile(s), {wanted} in view)");
        expect(settled == wanted && settled > 0 && !surface.WaveZoomMoving && Shows(),
            "the zoom's rest makes the final step's tiles in one render, exactly those in view, and the sound is drawn crisp");
        before = Built();
        surface.SettleWaveZoom();
        Lay();
        expect(Built() == before, "a second rest makes none");

        // (d) Twenty-four steps out, from 5 s across to 20 s (two octaves): nothing made, the finer steps' tiles stand in where the
        // coarser ones are not made.
        (made, blank) = (0, 0);
        for (var k = 1; k <= 24; k++)
        {
            before = Built();
            Zoom(5 * Math.Pow(2, k / 12.0));
            made += Built() - before;
            blank += Shows() ? 0 : 1;
            Kept();
        }

        expect(made == 0 && surface.WaveZoomMoving, $"twenty-four zoom steps out across two octaves make no tile while the scale moves ({made} made): the finer steps stand in");
        expect(blank == 0, "and the sound is drawn after every step");
        before = Built();
        surface.SettleWaveZoom();
        Lay();
        settled = Built() - before;
        wanted = Wanted();
        Console.WriteLine($"  (the rest after zooming out made {settled} tile(s), {wanted} in view)");
        expect(settled == wanted && settled > 0 && Shows(), "at rest the final step's tiles are made once, exactly those in view");

        // (e) The memory bound is the one it was.
        Console.WriteLine($"  (at most {mostKept} tile(s) held, the cap {UnifiedTimelineControl.WaveTileCapacity})");
        expect(mostKept > 0 && mostKept <= UnifiedTimelineControl.WaveTileCapacity, "the tiles kept never pass the cap (WaveTileCapacity)");

        // (f) The source: one timer, stopped by its own tick, made at the first zoom (never before the first frame); no edit outside the drawing.
        var render = File.ReadAllText(Path.Combine(Root(), "src", "Controls", "UnifiedTimelineControl.Render.cs")).ReplaceLineEndings("\n");
        expect(render.Contains("_waveSettle?.Stop();", StringComparison.Ordinal) && render.Contains("new DispatcherTimer(DispatcherPriority.Background, Dispatcher)", StringComparison.Ordinal)
               && render.IndexOf("new DispatcherTimer(", StringComparison.Ordinal) > render.IndexOf("private void RestartWaveSettle()", StringComparison.Ordinal),
            "the rest is a Background timer made at the first zoom and stopped by its own tick (it never keeps the control alive)");

        // (g) 5.4.4-Final (the user's: a wave past its clip's end again): DrawWave cuts its whole tiles to its band, from the sound's first second
        // shown to its last, inside the method itself, so no caller (a closed sound stack's bar has no block shape round it) shows a wave past its segment.
        var drawWave = render[render.IndexOf("private void DrawWave(", StringComparison.Ordinal)..render.IndexOf("private MatrixTransform Placed(", StringComparison.Ordinal)];
        var push = drawWave.IndexOf("drawing.PushClip(WaveClip(new Rect(Math.Max(left, origin + start * fileScale), band.Top,", StringComparison.Ordinal);
        var loop = drawWave.IndexOf("for (var tile = (long)Math.Floor(start / tileSeconds)", StringComparison.Ordinal);
        expect(push > 0 && loop > push && drawWave.IndexOf("drawing.Pop();\n\n        // Silence is still a line", StringComparison.Ordinal) > loop,
            "every wave is clipped to its band and its sound's own stretch before its tiles are drawn (whole tiles never show past the clip's end), and the clip popped after them");
    }

    private static string Root()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }
}
