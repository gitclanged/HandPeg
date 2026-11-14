using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5.1-AutoTest's HandPeg fixes (the Cutout Studio's wheel and shortcuts card are in CutoutWheelTests.cs, cutout-studio-wheel):
/// <para>legacy-preset-names: the Game Presets' names before 5.5.0 (LegacyPresetNames.Map) read as today's, a Track Preset Node of a project
/// read from its JSON and the drop overlay's Game Presets of the settings mapped (never the Smart Rules or default preset: Export Presets,
/// 5.5.2 Q2), every other name (the user's own) left as it is.</para>
/// <para>timeline-sound-stack-drag-wave: the real timeline control, no window, drawn into a bitmap: a video clip's closed sound stack's one bar
/// draws its lane's waveform where the bar is while the clip is dragged (the overlay, not the rows drawn once as the drag began), a lane of
/// another clip keeps its own where it is, and once let go the wave lies inside its bar.</para>
/// Every line says what it proved.
/// </summary>
public static class AutoTestFixesTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("legacy-preset-names", "# The Game Presets' names before 5.5.0 are read as today's: a project's Track Preset Nodes, the settings' drop overlay Game Presets.", PresetNames);
        yield return Case("playing-refresh-bound", "# The playing pump's refresh estimate starts at the monitor's and leaves out stalled turns; copies measure no display lead (5.5.2).", RefreshBound);
        yield return Case("timeline-sound-stack-drag-wave", "# A closed sound stack's one bar keeps its waveform inside it while its video clip is dragged, and after it is let go.", StackWave);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // ----- legacy-preset-names -----

    private static void PresetNames(Action<bool, string> expect, StringBuilder text)
    {
        expect(LegacyPresetNames.Map("Overwatch 2 Vertical HUD") == "Overwatch Vertical" && LegacyPresetNames.Map("Overwatch Vertical HUDv2") == "Overwatch Vertical",
            "\"Overwatch 2 Vertical HUD\" and \"Overwatch Vertical HUDv2\" read as \"Overwatch Vertical\"");
        expect(LegacyPresetNames.Map("Fortnite draft") == "Fortnite Vertical", "\"Fortnite draft\" reads as \"Fortnite Vertical\"");
        expect(LegacyPresetNames.Map("  fortnite DRAFT ") == "Fortnite Vertical", "case and spaces round the name are ignored");
        expect(LegacyPresetNames.Map("Overwatch 2 Vertical HUD" + StyleFile.Extension) == "Overwatch Vertical" + StyleFile.Extension,
            "as a file name with the Game Preset extension, the new file name");
        expect(LegacyPresetNames.Map("Overwatch Vertical") == "Overwatch Vertical" && LegacyPresetNames.Map("My Fortnite draft 2") == "My Fortnite draft 2"
               && LegacyPresetNames.Map("") == "" && LegacyPresetNames.Map(null) is null,
            "today's names, the user's own (even one holding an old name in it), empty and none are left as they are");
        expect(LegacyPresetNames.All.Count == 3 && LegacyPresetNames.All.Values.All(v => v is "Overwatch Vertical" or "Fortnite Vertical"),
            "the table is three old names, each mapped to a built-in of 5.5.0");

        // A project's Track Preset Nodes, as the project file holds them.
        var options = (JsonSerializerOptions)typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var document = new TimelineDocument();
        foreach (var (number, node) in new[] { (1, "Overwatch 2 Vertical HUD"), (2, "Fortnite draft"), (3, "Overwatch Vertical HUDv2"), (4, "My Own Preset"), (5, (string?)null) })
            document.Tracks.Add(new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = number, PresetNodeName = node });
        var json = JsonSerializer.Serialize(document, options);
        expect(json.Contains("Overwatch 2 Vertical HUD", StringComparison.Ordinal) && json.Contains("Fortnite draft", StringComparison.Ordinal),
            "a project written with the old names holds them (as a project of 5.4 does)");
        var read = JsonSerializer.Deserialize<TimelineDocument>(json, options)!;
        var nodes = read.Tracks.Select(t => t.PresetNodeName ?? "(none)").ToList();
        text.Append("read: ").Append(string.Join(" | ", nodes)).Append('\n');
        expect(nodes.SequenceEqual(["Overwatch Vertical", "Fortnite Vertical", "Overwatch Vertical", "My Own Preset", "(none)"]),
            "read back, each Track Preset Node of an old name has the new one; the user's own and a track of none are as they were");

        // The settings' drop overlay Game Presets are mapped; the Smart Rules and default preset (Export Presets) never are (5.5.2, Q2).
        var ext = StyleFile.Extension;
        var settings = new AppSettings
        {
            DefaultPreset = "Fortnite draft",
            SmartRules =
            [
                new SmartRule { Type = SmartRule.Keyword, Path = "Overwatch", Preset = "Overwatch 2 Vertical HUD" },
                new SmartRule { Type = SmartRule.Extension, Path = "*.mkv", Preset = "Discord 10MB" },
            ],
            SplashStylePresets = ["Overwatch 2 Vertical HUD" + ext, "Fortnite draft" + ext, "My Own" + ext, "Overwatch Vertical" + ext],
        };
        var changed = settings.MapLegacyPresetNames();
        text.Append("overlay: ").Append(string.Join(" | ", settings.SplashStylePresets)).Append('\n');
        expect(changed && settings.SplashStylePresets.SequenceEqual(["Overwatch Vertical" + ext, "Fortnite Vertical" + ext, "My Own" + ext]),
            "the drop overlay's Game Presets of an old name have the new one, in order, a duplicate of a mapped name dropped; the user's own kept");
        expect(settings.DefaultPreset == "Fortnite draft"
               && settings.SmartRules.Select(r => r.Preset).SequenceEqual(["Overwatch 2 Vertical HUD", "Discord 10MB"]),
            "the default preset and Smart Rules (Export Presets) are never mapped");
        expect(!settings.MapLegacyPresetNames(), "mapped again, nothing changes");
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Services", "AppSettings.cs"));
        var load = source.IndexOf("private static AppSettings Load()", StringComparison.Ordinal);
        var call = source.IndexOf("settings.MapLegacyPresetNames();", StringComparison.Ordinal);
        expect(load > 0 && call > load, "the settings are mapped as they are read (AppSettings.Load calls MapLegacyPresetNames)");
    }

    // ----- playing-refresh-bound (Pass 5.5.2, the Desktop's avsync-session) -----

    private static void RefreshBound(Action<bool, string> expect, StringBuilder text)
    {
        const double Hz500 = 1 / 500.0, Hz60 = 1 / 60.0;
        expect(GpuViewportControl.StartRefresh(Hz60, Hz500) == Hz500 && GpuViewportControl.StartRefresh(Hz500, Hz60) == Hz60,
            "a play starts its estimate at the monitor's refresh when it is off by more than half (1/60 on a 500 Hz display, and back)");
        expect(GpuViewportControl.StartRefresh(0.0021, Hz500) == 0.0021 && GpuViewportControl.StartRefresh(Hz60, double.NaN) == Hz60,
            "an estimate near the monitor's, or a monitor that cannot be told, is kept");

        // A cold play's first half second on the 500 Hz Desktop: turns of 37-52 ms among 2 ms ones.
        var refresh = GpuViewportControl.StartRefresh(Hz60, Hz500);
        var bounded = refresh;
        var unbounded = refresh;
        foreach (var interval in new[] { 0.037, 0.045, 0.052, 0.040, 0.047, 0.002, 0.039, 0.002, 0.051, 0.002, 0.002, 0.002 })
        {
            bounded = GpuViewportControl.NextRefresh(bounded, interval, Hz500);
            unbounded = GpuViewportControl.NextRefresh(unbounded, interval, double.NaN);
        }

        text.Append(F($"after a cold play's stalls: {bounded * 1000:0.###} ms bounded, {unbounded * 1000:0.###} ms as before 5.5.2")).Append('\n');
        expect(Math.Abs(bounded - Hz500) < 1e-4, "turns over 1.5 refreshes are stalls and leave the 500 Hz estimate at 2 ms");
        expect(unbounded > 0.008, "unbounded (5.4.4-5.5.1, or a monitor not told) the same turns pull it past 8 ms: the frames drawn that far ahead");
        expect(GpuViewportControl.NextRefresh(Hz60, 1 / 59.0, Hz60) is var near && near > Hz60 && near < 1 / 59.0,
            "a turn near the monitor's refresh still moves the estimate (the average as before)");
        expect(GpuViewportControl.NextRefresh(Hz500, 0.001, Hz500) == Hz500 && GpuViewportControl.NextRefresh(Hz500, 0.06, Hz500) == Hz500,
            "under 1.5 ms or over 50 ms counts for nothing (as before)");

        var viewport = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Controls", "GpuViewportControl.cs"));
        var pump = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Services", "GpuPreviewEngine.cs"));
        var kept = pump.ReplaceLineEndings("\n").IndexOf("_viewport.FrameOrigin = 0;\n                        var kept = _viewport.PresentNewest(vsync: true, force: true);", StringComparison.Ordinal);
        expect(kept > 0, "a playing copy of the frame shown last is presented with no origin: it measures no display lead");
        expect(viewport.Contains("if (id != 0 && FrameOrigin != 0)", StringComparison.Ordinal) && viewport.Contains("_leadOrigins[i] == 0", StringComparison.Ordinal),
            "MeasureLead keeps only new frames' presents, and asks the swap chain after every present (copies included)");
        expect(pump.Contains("GpuViewportControl.NextRefresh(_refresh, interval, monitorPeriod)", StringComparison.Ordinal)
               && pump.Contains("GpuViewportControl.StartRefresh(_refresh, monitorPeriod)", StringComparison.Ordinal),
            "the pump starts each play at the monitor's refresh and bounds every turn it measures");
    }

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }

    // ----- timeline-sound-stack-drag-wave -----

    private const int ShotWidth = 1000, ShotHeight = 300;

    private static byte[] Shot(UnifiedTimelineControl surface)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(ShotWidth, ShotHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var taken = new byte[ShotWidth * ShotHeight * 4];
        bitmap.CopyPixels(taken, ShotWidth * 4, 0);
        return taken;
    }

    // The columns (control x) in a lane's rows where a pixel passes a test (B, G, R).
    private static List<int> Columns(byte[] shot, (double Top, double Height) lane, Func<byte, byte, byte, bool> wanted)
    {
        var columns = new List<int>();
        var (top, bottom) = ((int)Math.Ceiling(lane.Top), (int)Math.Floor(lane.Top + lane.Height));
        for (var x = 0; x < ShotWidth; x++)
        {
            for (var y = Math.Max(top, 0); y < Math.Min(bottom, ShotHeight); y++)
            {
                var at = (y * ShotWidth + x) * 4;
                if (wanted(shot[at], shot[at + 1], shot[at + 2]))
                {
                    columns.Add(x);
                    break;
                }
            }
        }

        return columns;
    }

    // The wave: white at 0.7 over the dark green bar (about 178, 208, 178); the bar: dark green (0, 100, 0).
    private static bool Wave(byte b, byte g, byte r) => r > 110 && g > 110 && b > 110;

    private static bool Bar(byte b, byte g, byte r) => g > 70 && r < 40 && b < 40;

    private static int Count(List<int> columns, double from, double to) => columns.Count(x => x >= from && x <= to);

    private static void StackWave(Action<bool, string> expect, StringBuilder text)
    {
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(ShotWidth, ShotHeight));
        surface.Arrange(new Rect(0, 0, ShotWidth, ShotHeight));
        surface.SetView(0, 10, 10);
        var scale = surface.PixelsPerSecond;

        // Ten seconds of a steady sound, white.
        var pairs = (int)(10 * Waveforms.PeaksPerSecond);
        var (lows, highs) = (new float[pairs], new float[pairs]);
        for (var i = 0; i < pairs; i++)
            (lows[i], highs[i]) = (-0.5f, 0.5f);
        var data = new WaveformData(lows, highs, Brushes.White);

        // A video clip from 1 s to 3 s with its stream on its sound stack's lane, and another clip's stream on the same lane from 7 s to 8.5 s.
        var video = new TimelineRow { Key = "v", Data = "v", Name = "v", Kind = TimelineRowKind.Video, Height = 56 };
        var clip = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "", Clip = "clip", Fill = Brushes.SteelBlue, Family = new BlockFamily("clip", null, false, BlockEdges.None),
            CanDrag = () => true, Commit = (_, _) => { },
        };
        video.Blocks.Add(clip);
        var lane = new TimelineRow { Key = "a1", Data = "a1", Name = "a1", Kind = TimelineRowKind.Audio, Stack = ChildStack.Audio, Height = 40, Parent = video };
        var stream = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "", Clip = "stream", Fill = Brushes.DarkGreen, SoundWindow = new WaveWindow(data, 0, 2),
            Family = new BlockFamily("stream", "clip", true, BlockEdges.Start | BlockEdges.End),
        };
        var other = new TimelineBlock
        {
            Left = 7 * scale, Width = 1.5 * scale, Name = "", Clip = "other", Fill = Brushes.DarkGreen, SoundWindow = new WaveWindow(data, 0, 1.5),
            Family = new BlockFamily("other", "elsewhere", true, BlockEdges.Start | BlockEdges.End),
        };
        lane.Blocks.Add(stream);
        lane.Blocks.Add(other);
        video.Children.Add(lane);
        surface.SetRows([video]);
        surface.UpdateLayout();

        var laid = surface.LaidLanes.First(l => Equals(l.Key, "a1"));
        var videoLane = surface.LaidLanes.First(l => Equals(l.Key, "v"));
        var bar = (laid.Top, laid.Height);
        double X(double seconds) => surface.HeaderWidth + seconds * scale;
        text.Append(F($"the closed stack's bar: {laid.Height:0.#} DIPs tall, {scale:0.#} DIPs a second\n"));
        expect(laid.Height >= 4, "the video's sound stack is closed: its lane is one thin bar");

        var before = Shot(surface);
        var wave = Columns(before, bar, Wave);
        expect(Count(wave, X(1) + 2, X(3) - 2) > (int)(1.6 * scale) && Count(wave, X(7) + 2, X(8.5) - 2) > (int)(1.2 * scale) && Count(wave, X(4), X(6)) == 0,
            "before the drag: each stream's wave in the bar where its clip is (1 to 3 s, 7 to 8.5 s), none between");

        // Pressed and dragged 3 s later: the rows are drawn as the drag begins, then only the overlay moves with the pointer.
        var middle = videoLane.Top + videoLane.Height / 2;
        surface.PressBlockAt(video, clip, new Point(X(2), middle), System.Windows.Input.ModifierKeys.None);
        Shot(surface);
        surface.MoveBlockDrag(new Point(X(3), middle));
        Shot(surface);
        surface.MoveBlockDrag(new Point(X(5), middle));
        var dragged = Shot(surface);
        wave = Columns(dragged, bar, Wave);
        var bars = Columns(dragged, bar, Bar);
        text.Append(F($"dragged 3 s: the stream's block at {(stream.Left / scale):0.##} s\n"));
        expect(Math.Abs(stream.Left - 4 * scale) < 0.5, "the stream's block went with its clip (to 4 s)");
        expect(Count(bars, X(4) + 2, X(6) - 2) > (int)(1.6 * scale), "its bar is drawn where it is now (4 to 6 s)");
        expect(Count(wave, X(4) + 2, X(6) - 2) > (int)(1.6 * scale), "and its wave with it, inside the bar (4 to 6 s)");
        expect(Count(wave, X(1) + 2, X(3) - 2) == 0, "no wave is left where the clip was (1 to 3 s): the user's bug, the wave staying at its time before the drag");
        expect(Count(wave, X(7) + 2, X(8.5) - 2) > (int)(1.2 * scale), "the other clip's stream on the same lane keeps its wave where it is (7 to 8.5 s)");

        // Let go: the rows are drawn again with the clip where it was let go (the host's commit makes its rows again, as they are here).
        var released = surface.ReleaseBlockDrag();
        var after = Shot(surface);
        wave = Columns(after, bar, Wave);
        bars = Columns(after, bar, Bar);
        // In the time area, before the other clip (the header's words are left out).
        bool Ours(int x) => x > surface.HeaderWidth + 1 && x < X(6.5);
        var (waveFrom, waveTo) = (wave.Where(Ours).DefaultIfEmpty(-1).Min(), wave.Where(Ours).DefaultIfEmpty(-1).Max());
        var (barFrom, barTo) = (bars.Where(Ours).DefaultIfEmpty(-1).Min(), bars.Where(Ours).DefaultIfEmpty(-1).Max());
        expect(released && !surface.IsDragging, "let go, the drag is over");
        var inside = waveFrom >= 0 && waveFrom >= barFrom - 1 && waveTo <= barTo + 1 && waveFrom <= X(4) + 2 && waveTo >= X(6) - 2;
        expect(inside, "the wave lies inside its bar, across it (4 to 6 s): nothing offset after the let-go"
                       + (inside ? "" : F($" (the wave from x {waveFrom} to {waveTo}, its bar from {barFrom} to {barTo})")));
        expect(Count(wave, X(1) + 2, X(3) - 2) == 0, "and still none where the clip was");
    }
}
