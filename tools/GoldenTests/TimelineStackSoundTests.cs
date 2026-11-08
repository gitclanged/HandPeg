using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.3-Stage2's Timeline waveforms and the Live Preview switch's going:
/// timeline-stack-sound (the host's arithmetic and its pass over hand-made rows, then the real timeline control on this STA thread, laid out at
/// a size of its own with no window and drawn into a bitmap): a video block with a sound stack shows its audible lanes' sound as one, the most
/// either reaches, a muted lane (or the video track's master mute) left out, the clip hidden kept (its lanes still play); the same lanes give
/// the very sound kept, a gain changed a new one; a lane at another speed is left out; a lane slipped half a second lands 40 pairs over; a lane
/// under part of the block plays only its own stretch and the combined sound is that clip's stretch of the file alone (Law 2), and two halves
/// of a split clip share one sound; an empty timeline's pass lets go of what was drawn. Drawn, the stack's sound covers the whole block over
/// its frames, see-through, with no dark band under it, never its own sound beside it, and stays where its lanes play while the block is
/// slipped; a block with no stack sound beside it on the same track draws its own sound across it, and a J/L cut's sound stays in its band
/// below.
/// live-preview-gone (by reflection, a view model and the main window's XAML text): no LivePreview switch, the refresh after every edit renamed
/// and still raised, the preview state kept, no eye on the playback bar's list, and no binding to the switch in MainWindow.xaml.
/// Every line says what it proved.
/// </summary>
public static class TimelineStackSoundTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-stack-sound", "# A video clip with a sound stack shows its audible lanes' sound as one waveform, across its block, over its frames.", StackSound);
        yield return Case("live-preview-gone", "# The Live Preview switch is gone: the player always shows the preview; the refresh after every edit is renamed, not lost.", LivePreviewGone);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "FAILED: ").Append(what).Append('\n');
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

    // ----- timeline-stack-sound -----

    // Ten seconds of a sound, each pair as loud as asked (its low the same under 0).
    private static WaveformData Sound(int pairs, Func<int, float> level)
    {
        var (lows, highs) = (new float[pairs], new float[pairs]);
        for (var i = 0; i < pairs; i++)
            (lows[i], highs[i]) = (-level(i), level(i));
        return new WaveformData(lows, highs, Brushes.White);
    }

    // What a lane's block shows of its sound: its file from mediaFrom on, for so many seconds of the timeline at its speed and gain.
    private static WaveWindow Lane(WaveformData data, double mediaFrom = 0, double seconds = 6, double speed = 1, double gain = 1) =>
        new(data, mediaFrom, mediaFrom + seconds * speed, speed, gain);

    private static void StackSound(Action<bool, string> expect, StringBuilder text)
    {
        // S1 loud on the first half of every second, S2 on the second half; a 6 s video clip from 2 s, its file from its start.
        const double rate = Waveforms.PeaksPerSecond;
        var s1 = Sound(800, i => i % 80 < 40 ? 0.6f : 0.1f);
        var s2 = Sound(800, i => i % 80 >= 40 ? 0.3f : 0.05f);

        // (a) The two lanes under the whole clip: one sound on the clip's own file time, each pair the louder lane's.
        var both = MainWindow.StackSoundOf(2, 0, 1, 6, [(2, Lane(s1)), (2, Lane(s2))]);
        expect(both is { MediaFrom: 0, MediaTo: 6, Speed: 1, GainScale: 1 } w && w.Data.Count == 800 && !ReferenceEquals(w.Data, s1) && !ReferenceEquals(w.Data, s2),
            "two lanes under a 6 s clip: one sound of their 800 pairs, laid on the clip's file time from 0 to 6 s at its speed, at gain 1 (each lane's gain is in it)");
        if (both is not { } combined)
            return;

        var louder = Enumerable.Range(0, 800).All(i => combined.Data.HighsAt(0)[i] == Math.Max(s1.HighsAt(0)[i], s2.HighsAt(0)[i])
                                                      && combined.Data.LowsAt(0)[i] == Math.Min(s1.LowsAt(0)[i], s2.LowsAt(0)[i]));
        expect(louder, "each pair is the louder lane's (the most either reaches), not the two added");

        // (b) The same lanes again: the sound kept (its waveform tiles drawn again, never made again); a lane's gain changed, a new one.
        var again = MainWindow.StackSoundOf(2, 0, 1, 6, [(2, Lane(s1)), (2, Lane(s2))]);
        expect(again is { } kept && ReferenceEquals(kept.Data, combined.Data), "the same lanes again (a rebuild of the rows after an edit): the very sound kept");
        var gained = MainWindow.StackSoundOf(2, 0, 1, 6, [(2, Lane(s1)), (2, Lane(s2, gain: 2))]);
        expect(gained is { } louderLane && !ReferenceEquals(louderLane.Data, combined.Data) && Math.Abs(louderLane.Data.HighsAt(0)[40] - 0.6) < 1e-6
               && Math.Abs(combined.Data.HighsAt(0)[40] - 0.3) < 1e-6,
            "S2 at +6 dB (twice as tall): a new sound, S2's 0.3 now 0.6 where it is the louder");

        // (c) A lane at another speed is left out (never resampled); with none left, nothing.
        var fast = MainWindow.StackSoundOf(2, 0, 1, 6, [(2, Lane(s1)), (2, Lane(s2, seconds: 6, speed: 2))]);
        expect(fast is { } one && ReferenceEquals(one.Data, s1), "a lane at 2x under a 1x clip is left out: S1's own peaks (one lane as it is: its tiles drawn again)");
        expect(MainWindow.StackSoundOf(2, 0, 1, 6, [(2, Lane(s2, speed: 2))]) is null, "and with only that lane, the clip has no stack sound");

        // (d) A lane slipped half a second later: a spike at its pair 100 lands at the clip's pair 140, silence before it starts.
        var spike = Sound(800, i => i == 100 ? 0.9f : 0.02f);
        var slipped = MainWindow.StackSoundOf(2, 0, 1, 6, [(2.5, Lane(spike))]);
        var slippedHighs = slipped is { } s ? s.Data.HighsAt(0).ToArray() : [];
        var at = Array.IndexOf(slippedHighs, 0.9f);
        text.Append(CultureInfo.InvariantCulture, $"   a lane slipped 0.5 s: its spike at its pair 100 is the clip's pair {at}\n");
        expect(at == 100 + (int)(0.5 * rate) && slippedHighs[39] == 0 && slippedHighs[40] > 0, "a lane slipped 0.5 s later lands 40 pairs over, silent before it starts");

        // A lane trimmed to the first half of the clip plays only its own stretch; two halves of a split clip share one sound.
        var trimmed = MainWindow.StackSoundOf(2, 0, 1, 6, [(2, Lane(s1, seconds: 3)), (2, Lane(s2))]);
        expect(trimmed is { MediaFrom: 0, MediaTo: 6 } part && part.Data.Count == 480 && Math.Abs(part.Data.HighsAt(0)[300] - s2.HighsAt(0)[300]) < 1e-6
               && Math.Abs(part.Data.HighsAt(0)[20] - 0.6) < 1e-6,
            "a lane under the first 3 s only: its sound stops where it does (S2's alone after 3 s), the combined sound the clip's 6 s of the file alone (480 pairs)");
        var firstHalf = MainWindow.StackSoundOf(2, 0, 1, 3, [(2, Lane(s1, 0, 3)), (2, Lane(s2, 0, 3))]);
        var secondHalf = MainWindow.StackSoundOf(5, 3, 1, 3, [(5, Lane(s1, 3, 3)), (5, Lane(s2, 3, 3))]);
        expect(firstHalf is { } h1 && secondHalf is { MediaFrom: 3 } h2 && ReferenceEquals(h1.Data, h2.Data) && ReferenceEquals(h1.Data, combined.Data),
            "the two halves of the clip split at 5 s share one sound (their lanes under the whole of each), the whole clip's");

        // Law 2: with a lane under part of it, a clip of the file's 3 to 6 s holds only its own stretch of the file, laid from its first pair.
        var middle = MainWindow.StackSoundOf(5, 3, 1, 3, [(5, Lane(s1, 3, 1)), (5, Lane(s2, 3, 3))]);
        expect(middle is { MediaFrom: 0, MediaTo: 3 } m && m.Data.Count == 240 && Math.Abs(m.Data.HighsAt(0)[0] - 0.6) < 1e-6
               && Math.Abs(m.Data.HighsAt(0)[120] - 0.3) < 1e-6,
            "S1 under the first second of a clip of the file's 3 to 6 s: the combined sound is that clip's 3 s alone (240 pairs, laid from 0), never the whole file's");

        // The host's pass over its rows: by the closed stack's rules, a muted lane or the video track's master mute; the clip hidden kept.
        PassOverRows(expect, s1, combined.Data);

        // (e) Drawn by the real control.
        Drawn(expect);
    }

    private static void PassOverRows(Action<bool, string> expect, WaveformData s1, WaveformData both)
    {
        const double scale = 50;
        var clip = new Layer { Kind = LayerKind.VideoFile, Name = "V", StartTime = 2, Duration = 6 };
        var parentId = Guid.NewGuid();
        var video = new TimelineRow { Key = "V", Data = "V", Kind = TimelineRowKind.Video, Height = 56 };
        var block = new TimelineBlock { Left = 2 * scale, Width = 6 * scale, Clip = clip, Family = new BlockFamily(parentId, null, false, BlockEdges.None) };
        video.Blocks.Add(block);
        TimelineRow Stream(string key, WaveWindow sound)
        {
            var lane = new TimelineRow { Key = key, Data = key, Kind = TimelineRowKind.Audio, Stack = ChildStack.Audio, Height = 28, Parent = video };
            lane.Blocks.Add(new TimelineBlock { Left = 2 * scale, Width = 6 * scale, SoundWindow = sound, Family = new BlockFamily(Guid.NewGuid(), parentId, true, BlockEdges.Start | BlockEdges.End) });
            video.Children.Add(lane);
            return lane;
        }

        Stream("S1", Lane(s1));
        var second = Stream("S2", Lane(Sound(800, i => i % 80 >= 40 ? 0.3f : 0.05f)));
        WaveWindow? Pass()
        {
            block.StackSound = null;
            MainWindow.GiveStacksTheirSound([video], scale, 10);
            return block.StackSound;
        }

        var together = Pass();
        expect(together is { } heard && heard.Data.Count == both.Count && heard.Data.HighsAt(0).SequenceEqual(both.HighsAt(0)),
            "the host's pass: the video block's stack sound is its two lanes' together, found by its clip (each lane block's family names it)");
        second.State = RowSwitches.Muted;
        expect(Pass() is { } alone && ReferenceEquals(alone.Data, s1), "S2's lane muted: S1's alone, its own peaks");
        second.State = RowSwitches.None;
        video.State = RowSwitches.Muted;
        expect(Pass() is null, "the video track's master mute: no stack sound");
        video.State = RowSwitches.None;
        clip.IsHidden = true;
        expect(Pass() is { } stillHeard && together is { } shown && ReferenceEquals(stillHeard.Data, shown.Data),
            "the clip hidden (switched off, or its video track's eye off): its lanes still play, so its stack sound stays (the very sound kept)");

        // An empty timeline (a new project: BuildTimelineRows' early return makes a pass with no rows): what the last pass drew is let go.
        int Held() => (typeof(MainWindow).GetField("_stackSoundsDrawn", Any)?.GetValue(null) as System.Collections.ICollection)?.Count ?? -1;
        var before = Held();
        MainWindow.GiveStacksTheirSound([], 0, 0);
        expect(before > 0 && Held() == 0, "an empty timeline's pass (no rows): the combined sounds the last pass drew are let go, none held for it");
    }

    private static void Drawn(Action<bool, string> expect)
    {
        // A control 1000 DIPs wide (its headers 168 of it), ten seconds across. Row V: a video track with a sound stack (closed: its one
        // bar), its block from 1 to 5 s with frames and a stack sound loud for its file's first 2 s, silent after (and a loud sound of its
        // own given too, which it must not draw beside it); a second block from 6 to 9 s with its own sound and no stack sound (a file of
        // one stream beside a stacked clip). Row J: no stack, a block from 2 to 5 s with frames and a J/L cut of 0.5 s each way.
        var surface = new UnifiedTimelineControl();
        var (size, place) = (new Size(1000, 300), new Rect(0, 0, 1000, 300));
        surface.Measure(size);
        surface.Arrange(place);
        surface.SetView(0, 10, 10);
        var scale = surface.PixelsPerSecond;

        var grey = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        grey.Freeze();
        var frame = new DrawingImage(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(0, 0, 16, 9))));
        frame.Freeze();
        var film = new FilmWindow([frame], 1, 0, 1);
        var loudThenSilent = Sound(800, i => i < 160 ? 1f : 0f);
        var loud = Sound(800, _ => 1f);

        var video = new TimelineRow { Key = "V", Data = "V", Kind = TimelineRowKind.Video, Height = 56, Name = "V" };
        var stacked = new TimelineBlock
        {
            Left = 1 * scale, Width = 4 * scale, Fill = grey, Film = film, StackSound = new WaveWindow(loudThenSilent, 0, 4), SoundWindow = new WaveWindow(loud, 0, 4),
        };
        var unstacked = new TimelineBlock { Left = 6 * scale, Width = 3 * scale, Fill = grey, Film = film, SoundWindow = new WaveWindow(loud, 0, 3) };
        video.Blocks.Add(stacked);
        video.Blocks.Add(unstacked);
        video.Children.Add(new TimelineRow { Key = "S", Data = "S", Kind = TimelineRowKind.Audio, Stack = ChildStack.Audio, Height = 28, Name = "S" });
        var cut = new TimelineRow { Key = "J", Data = "J", Kind = TimelineRowKind.Video, Height = 56, Name = "J" };
        cut.Blocks.Add(new TimelineBlock { Left = 2 * scale, Width = 3 * scale, Fill = grey, Film = film, SoundWindow = new WaveWindow(loud, 0, 4), AudioSplit = (0.5, 0.5) });
        surface.SetRows([video, cut]);
        surface.Measure(size);
        surface.Arrange(place);
        surface.UpdateLayout();

        byte[] Shot()
        {
            var bitmap = new RenderTargetBitmap(1000, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var taken = new byte[1000 * 300 * 4];
            bitmap.CopyPixels(taken, 1000 * 4, 0);
            return taken;
        }

        var pixels = Shot();

        // How light the picture is at a moment (seconds) and a height within a lane's block (0 its top, 1 its foot): the blocks fill their
        // lanes less 2 px each side.
        double Light(string key, double seconds, double down)
        {
            var lane = surface.LaidLanes.First(l => Equals(l.Key, key));
            var (top, height) = (lane.Top + 2, lane.Height - 4);
            var (x, y) = ((int)(surface.HeaderWidth + seconds * scale), (int)(top + down * height));
            var at = (y * 1000 + x) * 4;
            return (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3.0;
        }

        const double frameLight = 0x50;
        bool IsFrame(double light) => Math.Abs(light - frameLight) <= 12;

        expect(IsFrame(Light("V", 4, 0.15)) && IsFrame(Light("V", 4, 0.85)), "laid out with no window: the frames are where the block is (its silent stretch shows them as they are)");
        expect(Light("V", 2, 0.15) > frameLight + 40 && Light("V", 2, 0.25) > frameLight + 40, "the stack sound covers the block's top 30 % too: drawn across the whole block, over its frames");
        expect(Light("V", 2, 0.15) < 235, "see-through: lighter than the frames, never a white slab over them");
        expect(Light("V", 4, 0.85) >= frameLight - 8 && Light("V", 4, 0.7) >= frameLight - 8, "no dark band under the sound: its silent stretch's lower part is the frames as they are");
        expect(IsFrame(Light("V", 4, 0.3)) && IsFrame(Light("V", 4, 0.7)),
            "a block with a stack sound never draws its own sound beside it: given a loud one too, its stack's silent stretch shows its frames");
        expect(Light("V", 7.5, 0.15) > frameLight + 40 && Light("V", 7.5, 0.85) > frameLight + 40,
            "a block with no stack sound beside a stacked one on the same track (a file of one stream) draws its own sound across it, over its frames");
        expect(IsFrame(Light("J", 3.5, 0.15)), "a J/L cut's block: its top shows its frames (its sound is not drawn across it)");
        expect(Light("J", 3.5, 0.85) > frameLight + 40 && Light("J", 1.75, 0.85) > frameLight + 40, "and its sound is in its band under SoundBandTop, reaching out before its picture");

        // (f) The stacked block slipped 2 s on (Alt-dragged: the control's own drag, set by reflection): a slip moves its frames, never its
        // lanes, so its stack sound stays where they play (its file's loud first 2 s still from 1 to 3 s), not slid with the frames.
        var control = typeof(UnifiedTimelineControl);
        var dragType = control.GetNestedType("BlockDrag", BindingFlags.NonPublic)!;
        var drag = Activator.CreateInstance(dragType, nonPublic: true)!;
        dragType.GetProperty("Row")!.SetValue(drag, video);
        dragType.GetProperty("Block")!.SetValue(drag, stacked);
        dragType.GetProperty("Kind")!.SetValue(drag, BlockEdit.Slip);
        dragType.GetProperty("Left")!.SetValue(drag, stacked.Left);
        dragType.GetProperty("Width")!.SetValue(drag, stacked.Width);
        dragType.GetProperty("Slip")!.SetValue(drag, 2 * scale);
        control.GetField("_drag", Hidden | BindingFlags.DeclaredOnly)!.SetValue(surface, drag);
        control.GetMethod("Invalidate", Hidden | BindingFlags.DeclaredOnly)!.Invoke(surface, null);
        surface.Measure(size);
        surface.Arrange(place);
        surface.UpdateLayout();
        pixels = Shot();
        expect(Light("V", 2, 0.15) > frameLight + 40 && IsFrame(Light("V", 4, 0.15)),
            "slipped 2 s (Alt-dragged): its stack sound stays where its lanes play (loud at 2 s, silent at 4 s), only its frames slide");
    }

    // ----- live-preview-gone -----

    private static void LivePreviewGone(Action<bool, string> expect, StringBuilder text)
    {
        var model = typeof(MainViewModel);
        expect(model.GetProperty("LivePreview", Any) is null && model.GetField("_livePreview", Any) is null, "the view model has no LivePreview switch");
        expect(model.GetMethod("InvalidateLiveFilter", Any) is null && model.GetEvent("LiveFilterInvalidated", Any) is null,
            "nor InvalidateLiveFilter or LiveFilterInvalidated (renamed, not lost)");
        expect(model.GetMethod("InvalidatePreviewState", Hidden) is not null && model.GetEvent("PreviewStateInvalidated") is not null && model.GetMethod("LivePreviewState") is not null,
            "InvalidatePreviewState and PreviewStateInvalidated are there, and LivePreviewState (the preview's SequenceExportState) keeps its name");
        expect(typeof(MainWindow).GetMethod("ApplyLiveFilter", Any) is null && typeof(MainWindow).GetMethod("ScheduleLiveFilter", Any) is null,
            "the main window has no ApplyLiveFilter or ScheduleLiveFilter (ScheduleGpuState is called itself)");

        // The refresh after every edit still reaches its listeners (the player's scene plan, the Timeline's rows), and the state is gathered afresh.
        var vm = new MainViewModel();
        vm.SourceWidth = 1920;
        vm.SourceHeight = 1080;
        var raised = 0;
        vm.PreviewStateInvalidated += () => raised++;
        var before = vm.LivePreviewState();
        var kept = vm.LivePreviewState();
        model.GetMethod("InvalidatePreviewState", Hidden)!.Invoke(vm, null);
        var after = vm.LivePreviewState();
        expect(ReferenceEquals(before.Timeline, kept.Timeline) && before.IsPreview, "the preview state is gathered once and kept until something changes");
        expect(raised == 1 && !ReferenceEquals(after.Timeline, before.Timeline), "InvalidatePreviewState raises PreviewStateInvalidated once and drops the state: the next is gathered afresh");

        // The playback bar's list has no eye; a list saved with it shows the rest.
        var set = new PlaybackControlSet();
        set.Load(null);
        text.Append("   the playback bar's defaults: ").Append(set.Save()).Append('\n');
        expect(set.Options.All(o => o.Id != "LivePreview") && !set.Save().Contains("LivePreview", StringComparison.Ordinal), "the playback bar offers no Live Preview control, and starts without it");
        set.Load("LivePreview,Volume,@2");
        expect(set["Volume"] && !set["LivePreview"] && set.Save() == "Volume,@2", "a bar saved with Live Preview on it loads the rest, and saves without it");

        // The main window's XAML binds nothing to the switch (a binding left would only fail at run time).
        var xaml = RepositoryFile(Path.Combine("src", "MainWindow.xaml"));
        expect(xaml is not null, "src\\MainWindow.xaml is found above the test");
        if (xaml is not null)
        {
            var words = File.ReadAllText(xaml);
            expect(!Regex.IsMatch(words, @"Binding\s+(Path=)?LivePreview\b") && !words.Contains("[LivePreview]", StringComparison.Ordinal),
                "MainWindow.xaml has no Binding LivePreview, nor a control shown by PlaybackControls[LivePreview] (its eye buttons are gone)");
        }
    }

    /// <summary>A file of the repository, found upwards from the built test (the repository root holds src\); null when it is not there.</summary>
    private static string? RepositoryFile(string relative)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, relative);
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
