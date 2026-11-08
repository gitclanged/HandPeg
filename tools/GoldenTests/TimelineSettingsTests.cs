using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.3's Timeline de-clutter and its Timeline Settings menu:
/// timeline-effect-lanes-fold (the host's own effect lane row and the switch that lets the tracks take their lanes, then the real timeline
/// control on this STA thread, laid out at a size of its own with no window, so a stack opens and closes at once): the host's lane takes
/// no room while closed, and the tracks take none with the effect lanes off; a track's effect lanes take no room while its stack is closed,
/// or while it is itself a thin bar of a closed stack, and come back at their full height as it opens, while its Cutouts and its sound stay
/// the thin bars they always were;
/// timeline-effect-lane-cards (a real view model with Overwatch 2's built-in Game Preset on a clip): a Cutout's own Mask and Edges cards
/// have no lanes and no fx badge unless asked for, unlocked from its clip by its Lock switch as well, any other effect has, the same cards
/// on a clip that is no Cutout have, and with the effect lanes off there are none while the badge stays;
/// timeline-settings-menu: the menu's groups and items from the one table both of its places are made from, each kept in a real setting,
/// the snap item's key, the track height presets, the Undo / Redo item (5.3-Stage2), and the six new settings' defaults through the
/// settings file's own JSON.
/// Every line says what it proved.
/// </summary>
public static class TimelineSettingsTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-effect-lanes-fold", "# A track's effect lanes take no room until its own stack is open; its Cutouts and its sound stay thin bars while closed.", LanesFold);
        yield return Case("timeline-effect-lane-cards", "# Overwatch 2's Cutouts: their own Mask and Edges cards have no lanes and no fx badge unless asked for; any other effect has.", LaneCards);
        yield return Case("timeline-settings-menu", "# The Timeline Settings menu: its groups and items from one table, each kept in a real setting; the six new settings' defaults.", SettingsMenu);
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

    // ----- timeline-effect-lanes-fold -----

    private static void LanesFold(Action<bool, string> expect, StringBuilder text)
    {
        // The host's own effect lane, as EffectRows makes each (MainWindow.EffectLaneRow): what it stands for, its name and its height, and
        // that it takes no room while its stack is closed. And BuildTimelineRows takes the lanes into the tracks' stacks only while the
        // Timeline's effect lanes are on (MainWindow.AdoptsEffectLanes).
        var track = new Layer { Kind = LayerKind.VideoFile, Name = "V", TrackId = 7 };
        var host = MainWindow.EffectLaneRow(track, 1);
        text.Append(string.Create(CultureInfo.InvariantCulture, $"   the host's second effect lane of track 7: key {host.Key}, \"{host.Name}\", {host.Height:0.##} px, {host.Kind}"))
            .Append(host.HidesWhenClosed ? ", no room while closed" : "").Append('\n');
        expect(host.HidesWhenClosed, "the host's effect lane (EffectLaneRow, the row EffectRows makes) takes no room while its stack is closed (HidesWhenClosed)");
        expect(Equals(host.Key, ("effects", 7, 1)) && host.Data is MainWindow.EffectLane { Index: 1 } laneData && ReferenceEquals(laneData.Track, track)
               && host.Name == "Effects 2" && host.Height == 26 && host.Kind == TimelineRowKind.Video,
            "and it is known by (\"effects\", its track, its index), stands for its track's second lane, is named \"Effects 2\" and is a 26 px picture lane");
        expect(MainWindow.AdoptsEffectLanes(new AppSettings()) && !MainWindow.AdoptsEffectLanes(new AppSettings { ShowTimelineEffectLanes = false }),
            "the tracks' stacks take their effect lanes with the Timeline's effect lanes on (a new install), and none with them off");

        // A video track V with three Cutouts in front of it (A, B, C, each with two effect lanes of its own), an effect lane of V's own, and
        // a sound lane in its audio stack. The rows are made here by hand, under readable keys, laid as BuildTimelineRows lays the host's;
        // each effect lane takes its height and its HidesWhenClosed from the host's lane above, so a host lane without the flag fails here too.
        var surface = new UnifiedTimelineControl();
        surface.Measure(new System.Windows.Size(1000, 600));
        surface.Arrange(new System.Windows.Rect(0, 0, 1000, 600));
        surface.SetView(0, 10, 10);

        static TimelineRow Row(string key, TimelineRowKind kind, double height) => new() { Key = key, Data = key, Kind = kind, Height = height, Name = key };
        TimelineRow EffectLane(string owner, int k)
        {
            var lane = Row($"{owner} fx{k}", TimelineRowKind.Video, host.Height);
            lane.HidesWhenClosed = host.HidesWhenClosed;
            return lane;
        }

        var video = Row("V", TimelineRowKind.Video, 56);
        var cutouts = new List<TimelineRow>();
        foreach (var name in new[] { "A", "B", "C" })
        {
            var cutout = Row(name, TimelineRowKind.Video, 56);
            cutout.InFront = true;
            cutout.Children.Add(EffectLane(name, 1));
            cutout.Children.Add(EffectLane(name, 2));
            video.Children.Add(cutout);
            cutouts.Add(cutout);
        }

        video.Children.Add(EffectLane("V", 1));
        var sound = Row("S", TimelineRowKind.Audio, 28);
        sound.Stack = ChildStack.Audio;
        video.Children.Add(sound);
        surface.SetRows([video]);

        var cutoutLanes = new[] { "A fx1", "A fx2", "B fx1", "B fx2", "C fx1", "C fx2" };
        (object Key, double Top, double Height, bool Mini, bool Detailed, int Stacks, bool Last) Lane(string key) => surface.LaidLanes.First(l => Equals(l.Key, key));
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        void Show(string state)
        {
            text.Append("-- ").Append(state).Append('\n');
            foreach (var lane in surface.LaidLanes)
            {
                text.Append(string.Create(CultureInfo.InvariantCulture, $"   {lane.Key,-6} top {lane.Top,6:0.##}  height {lane.Height,5:0.##}"))
                    .Append(lane.Mini ? "  small" : "").Append(lane.Detailed ? "  in full" : "").Append(lane.Last ? "  last of its stack" : "").Append('\n');
            }

            // The extent is every lane and its gap; a lane that takes no room has no gap either.
            var laid = surface.LaidLanes;
            var sum = laid.Sum(l => l.Height) + laid.Count(l => l.Height > 0);
            expect(Near(surface.ExtentHeight, sum), string.Create(CultureInfo.InvariantCulture,
                $"{state}: the extent ({surface.ExtentHeight:0.##}) is the lanes' heights and their gaps, none for a lane that takes no room"));
        }

        // All closed: the Cutouts are thin bars (a fifth of 56, 11.2 px), every effect lane nothing, the sound stack its one 8 px bar.
        Show("V closed");
        expect(cutouts.All(c => Lane((string)c.Key) is var lane && lane.Mini && Near(lane.Height, 11.2)), "V closed: its Cutouts are still thin bars of 11.2 px (a stack is never hidden)");
        expect(cutoutLanes.Append("V fx1").All(k => Lane(k).Height == 0), "V closed: none of the seven effect lanes takes any room (no purple bars)");
        expect(Lane("S") is var bar && bar.Mini && Near(bar.Height, 8), "V closed: its sound stack is its one 8 px bar, as before");
        expect(Lane("C").Last && !Lane("B").Last, "V closed: the last Cutout's bar ends the deck's line (V's own effect lane after it takes no room)");

        // V's stack open: the Cutouts at their full height above it, their own effect lanes still nothing (their stacks are closed), V's own
        // effect lane at its full 26 px under it.
        surface.SetExpanded(video, ChildStack.Visual, true);
        Show("V open");
        expect(cutouts.All(c => Lane((string)c.Key) is var lane && !lane.Mini && Near(lane.Height, 56) && lane.Top < Lane("V").Top), "V open: its three Cutouts are 56 px, laid above it");
        expect(cutoutLanes.All(k => Lane(k).Height == 0), "V open, its Cutouts closed: the six effect lanes of the Cutouts take no room (the bug: six 8 px purple bars)");
        expect(Lane("V fx1") is var own && own.Detailed && Near(own.Height, 26) && own.Top > Lane("V").Top, "V open: V's own effect lane is 26 px, in full, under it");
        expect((Lane("A").Stacks & 1) != 0, "a Cutout whose stack is effect lanes only keeps its chevron");
        expect(Lane("S") is var closedSound && closedSound.Mini && Near(closedSound.Height, 8), "V open: its sound stack, closed by itself, is still its one bar");

        // A's stack open: its two effect lanes come in at 26 px, in full; B's and C's are still nothing.
        surface.SetExpanded(cutouts[0], ChildStack.Visual, true);
        Show("V open, A open");
        expect(new[] { "A fx1", "A fx2" }.All(k => Lane(k) is var lane && lane.Detailed && Near(lane.Height, 26)), "A open: its two effect lanes are 26 px, in full");
        expect(new[] { "B fx1", "B fx2", "C fx1", "C fx2" }.All(k => Lane(k).Height == 0), "A open: B's and C's effect lanes still take no room");
        expect(Lane("A fx2").Last, "A open: its last effect lane ends its deck's line");

        // V closed again with A still open: A is a thin bar again, and its effect lanes nothing (a closed ancestor hides them).
        surface.SetExpanded(video, ChildStack.Visual, false);
        Show("V closed, A left open");
        expect(Lane("A") is var thin && thin.Mini && Near(thin.Height, 11.2), "V closed again: A is a thin bar, open or not");
        expect(cutoutLanes.Append("V fx1").All(k => Lane(k).Height == 0), "V closed again: A's effect lanes take no room although A's own stack is open");
        expect(surface.IsExpanded(cutouts[0], ChildStack.Visual), "and A's stack is still open, kept by its key");

        // V open again: A's effect lanes are back at their full height.
        surface.SetExpanded(video, ChildStack.Visual, true);
        Show("V open again");
        expect(new[] { "A fx1", "A fx2" }.All(k => Near(Lane(k).Height, 26)), "V open again: A's effect lanes are back at 26 px");
    }

    // ----- timeline-effect-lane-cards -----

    private static void LaneCards(Action<bool, string> expect, StringBuilder text)
    {
        var settings = AppSettings.Current;
        var (lanes, builtIns) = (settings.ShowTimelineEffectLanes, settings.ShowBuiltInMaskEffectLanes);
        try
        {
            (settings.ShowTimelineEffectLanes, settings.ShowBuiltInMaskEffectLanes) = (true, false);
            CutoutCards(expect, text, settings);
        }
        finally
        {
            (settings.ShowTimelineEffectLanes, settings.ShowBuiltInMaskEffectLanes) = (lanes, builtIns);
        }
    }

    private static void CutoutCards(Action<bool, string> expect, StringBuilder text, AppSettings settings)
    {
        // Overwatch 2's built-in Game Preset, written under a name of this test's own (the built-ins' own files are left to CutoutTests),
        // applied to the clip of the file opened as the Timeline's list does it.
        var vm = Opened("D:/media/overwatch.mkv", 2560, 1440, "hevc");
        var preset = GamePresetLibrary.All[0];
        var name = $"Lane Cards Golden {Guid.NewGuid():N}";
        Directory.CreateDirectory(AppPaths.Styles);
        var file = Path.Combine(AppPaths.Styles, name + StyleFile.Extension);
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(GamePresetLibrary.Build(preset)));
            var first = vm.Layers.First(l => l.ClipId != Guid.Empty);
            expect(vm.ApplyGamePreset([first], name), $"{preset.Name} applied to the clip");
        }
        finally
        {
            File.Delete(file);
        }

        var parentClip = vm.Document.Tracks.SelectMany(t => t.Clips).First(c => c.ContentKind == LayerKind.VideoFile && c.ParentClipId is null);
        var parent = vm.ViewOf(parentClip)!;
        var cutouts = vm.CutoutLayers.OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
        expect(cutouts.Select(c => c.Name).SequenceEqual(["Abilities & Ammo", "Health & Name", "Killfeed"]), $"its three Cutouts ({string.Join(", ", cutouts.Select(c => c.Name))})");
        expect(cutouts.All(c => MainWindow.CountsAsCutout(vm, c)) && !MainWindow.CountsAsCutout(vm, parent), "each counts as a Cutout for its lanes; the clip they hang from does not");

        static string Types(IEnumerable<EffectInstance> cards) => string.Join(", ", cards.Select(e => e.Type));
        foreach (var cutout in cutouts)
            text.Append("   ").Append(cutout.Name).Append(": cards ").Append(Types(cutout.Effects)).Append('\n');
        expect(cutouts.All(c => c.Effects.Select(e => e.Type).SequenceEqual([EffectRegistry.Mask, EffectRegistry.Edges])),
            "every Cutout is made with exactly two cards of its own: its Mask, then its Rounded & Feathered Edges");

        List<EffectInstance> Cards(Layer clip) => MainWindow.LaneCards(clip, MainWindow.CountsAsCutout(vm, clip), settings);
        string? Badge(Layer clip) => MainWindow.BadgeOf(1, 0, MainWindow.BadgeEffects(clip, MainWindow.CountsAsCutout(vm, clip), settings));
        static string Lanes(int count) => count == 0 ? "no lanes" : string.Join(", ", Enumerable.Range(0, count).Select(MainWindow.EffectLaneName));

        // As HandPeg starts: no lanes and no badge for a Cutout's own cards (the six purple bars of the report are gone).
        expect(cutouts.All(c => Cards(c).Count == 0), $"by default no Cutout has a lane card: {Lanes(cutouts.Max(c => Cards(c).Count))}");
        expect(cutouts.All(c => Badge(c) is null), "and no Cutout shows an fx badge for its own Mask and Edges");

        // Unlocked from its clip by its block's Lock switch (SetClipLocked, as BlockSwitched calls it) a Cutout is free, no longer linked, but
        // still on its Sub-Lane in its clip's stack: it is still a Cutout for its lanes, its own Mask and Edges still off them and its badge.
        // Locked again afterwards, as it was.
        var freed = cutouts.First(c => c.Name == "Health & Name");
        vm.SetClipLocked(freed, false);
        expect(vm.ClipLock(freed) == false && vm.ClipOf(freed) is { ParentClipId: null } && !vm.IsCutout(freed),
            "Health & Name unlocked by its Lock switch: free of its clip (no parent clip), still in its clip's stack, no longer linked as a Cutout");
        expect(vm.IsCutoutOrFreed(freed) && MainWindow.CountsAsCutout(vm, freed), "it still counts as a Cutout for its lanes (IsCutoutOrFreed)");
        expect(Cards(freed).Count == 0 && Badge(freed) is null, "and still has no lane card and no fx badge for its own Mask and Edges (not the fx 2 and two lanes of before)");
        vm.SetClipLocked(freed, true);
        expect(vm.ClipLock(freed) == true && vm.IsCutout(freed) && vm.ClipOf(freed)?.ParentClipId == parentClip.Id, "locked again, it is linked to its clip as before");

        // Asked for (Show Built-In Mask & Edge Lanes on Cutouts): both cards come back, on two lanes, and in the badge.
        settings.ShowBuiltInMaskEffectLanes = true;
        expect(cutouts.All(c => Types(Cards(c)) == $"{EffectRegistry.Mask}, {EffectRegistry.Edges}"),
            $"with the built-in lanes on, each Cutout's Mask and Edges are its lane cards: {Lanes(cutouts.Max(c => Cards(c).Count))}");
        expect(cutouts.All(c => Badge(c) == "fx 2"), "and its badge reads fx 2");
        settings.ShowBuiltInMaskEffectLanes = false;

        // An effect of the user's on a Cutout: its one lane card, on the first lane though it is not the first card of the stack.
        var killfeed = cutouts.First(c => c.Name == "Killfeed");
        using (Layer.Echo())
            killfeed.AddEffect(EffectRegistry.Find(EffectRegistry.MotionBlur)!.NewEffect());
        text.Append("   Killfeed with Motion Blur: cards ").Append(Types(killfeed.Effects)).Append("; lane cards ").Append(Types(Cards(killfeed))).Append('\n');
        expect(Cards(killfeed) is [{ Type: EffectRegistry.MotionBlur }], "Motion Blur added to Killfeed: exactly one lane card, Motion Blur, on its first lane (\"Effects\")");
        expect(Badge(killfeed) == "fx 1", "and its badge reads fx 1");
        expect(cutouts.Where(c => !ReferenceEquals(c, killfeed)).All(c => Cards(c).Count == 0), "the other Cutouts still have none");
        settings.ShowBuiltInMaskEffectLanes = true;
        expect(Cards(killfeed).Count == 3 && Badge(killfeed) == "fx 3", $"with the built-in lanes on, Killfeed has three lane cards ({Types(Cards(killfeed))})");
        settings.ShowBuiltInMaskEffectLanes = false;

        // A Mask a user puts on a clip that is no Cutout is an effect like any other: on its lanes.
        using (Layer.Echo())
            parent.AddEffect(EffectRegistry.Find(EffectRegistry.Mask)!.NewEffect());
        text.Append("   the clip they hang from, with a Mask: lane cards ").Append(Types(Cards(parent))).Append('\n');
        expect(Cards(parent).Any(e => e.Type == EffectRegistry.Mask), "a Mask card on the clip they hang from (no Cutout) is a lane card");

        // The effect lanes switched off: no lane cards anywhere, the badges as they were.
        var parentBadge = Badge(parent);
        settings.ShowTimelineEffectLanes = false;
        expect(cutouts.Append(parent).All(c => Cards(c).Count == 0), "with the effect lanes off, no clip has a lane card (no lanes at all)");
        expect(Badge(killfeed) == "fx 1" && Badge(parent) == parentBadge && parentBadge is not null, $"and the badges stay: Killfeed fx 1, the clip {parentBadge}");
        expect(MainWindow.BadgeOf(2, 0.12, 3) == "2x  +120 ms  fx 3", "a badge with a speed and a slip too: 2x  +120 ms  fx 3");
    }

    /// <summary>
    /// A view model holding one clip of the file opened (5.2-AuditB: a clip of a file like any other, filling the frame, from 0 to 8 s; the
    /// Sequence sized by its picture), as CutoutTests opens one: nothing is probed or decoded.
    /// </summary>
    private static MainViewModel Opened(string main, int width, int height, string codec)
    {
        var vm = new MainViewModel { SourceWidth = width, SourceHeight = height, LocalMediaPath = main };
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, new MediaInfo("matroska", 8, 0, new VideoStreamInfo(width, height, codec, 60, "16:9", "1:1", "bt709", "bt709", 0), [], []));
        var document = new TimelineDocument { Width = width, Height = height, NaturalWidth = width, NaturalHeight = height, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        var look = new LayerState { Kind = LayerKind.VideoFile, Name = "Main", SizeWidth = 1, ImagePath = main, ImageWidth = width, ImageHeight = height, MediaDuration = 8 };
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.VideoFile, SourcePath = main, Start = 0, End = 8, MediaOffset = 0, SourceDuration = 8, State = look });
        document.Tracks.Add(track);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        return vm;
    }

    // ----- timeline-settings-menu -----

    private static void SettingsMenu(Action<bool, string> expect, StringBuilder text)
    {
        var groups = MainWindow.TimelineSettingGroups;
        var fresh = new AppSettings();
        static string Shown(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

        foreach (var group in groups)
        {
            text.Append('[').Append(group.Header).Append("]\n");
            foreach (var item in group.Items)
            {
                text.Append("   ").Append(item.Header);
                if (item.KeyAction is { } action)
                    text.Append("  (key: ").Append(action).Append(", by default ").Append(string.Join(" / ", KeybindManager.Action(action)?.Defaults ?? [])).Append(')');
                text.Append("  -> ").Append(item.Kept is { } kept
                    ? $"AppSettings.{kept} ({Shown(typeof(AppSettings).GetProperty(kept)?.GetValue(fresh))} in a new install)"
                    : "the project's or the session's, not kept in the settings").Append('\n');
            }
        }

        expect(groups.Select(g => g.Header).SequenceEqual(["Lanes & Overlays", "Snapping & Playback", "Bars & Sizing", "Editing"]),
            "four groups, in order: Lanes & Overlays, Snapping & Playback, Bars & Sizing, Editing");
        var all = groups.SelectMany(g => g.Items).ToList();
        expect(Enum.GetValues<MainWindow.TimelineSetting>().All(s => all.Count(i => i.Setting == s) == 1), "every setting of the menu has exactly one item");
        expect(all.All(i => i.Header.Length > 0 && i.About.Length > 0 && !i.Header.Contains('(')), "every item has words and a tooltip, and no key written into its words");
        expect(all.Where(i => i.Kept is not null).All(i => typeof(AppSettings).GetProperty(i.Kept!) is { CanRead: true, CanWrite: true } property
                && (property.PropertyType == typeof(bool) || i.Setting == MainWindow.TimelineSetting.TrackHeight)),
            "each item kept in the settings names a real setting of AppSettings (a switch; Track Height the one height)");

        // Nothing the menu had before 5.3 went missing.
        var before = new[]
        {
            MainWindow.TimelineSetting.Snapping, MainWindow.TimelineSetting.SnapToIFrames, MainWindow.TimelineSetting.SnapToBeats, MainWindow.TimelineSetting.BeatGrid,
            MainWindow.TimelineSetting.CreatorHotbar, MainWindow.TimelineSetting.GainLines, MainWindow.TimelineSetting.RippleAllTracks,
            MainWindow.TimelineSetting.DroppedClipsJoinTrack, MainWindow.TimelineSetting.ShowLinkedAudio, MainWindow.TimelineSetting.TrackHeight,
        };
        expect(before.All(s => all.Any(i => i.Setting == s)), "every item of the menu before 5.3 is still in it (Snap Clips, I-frames, beats, beat grid, hotbar, gain lines, ripple, dropped clips, linked audio, track height)");

        // The snap item shows the snap action's key, never a key written into its words.
        var snap = all.Single(i => i.Setting == MainWindow.TimelineSetting.Snapping);
        expect(snap.KeyAction == "view.toggleSnap" && KeybindManager.Action("view.toggleSnap") is { } toggle && toggle.Defaults.Contains("Caps"),
            "Enable Timeline Snapping shows view.toggleSnap's key (Caps by default; S is Zoom Out Under the Pointer)");

        // The track height presets, and the one ticked for a height set by the slider.
        expect(MainWindow.TrackHeightPresets.SequenceEqual([("Compact", 8), ("Medium", 25), ("Large", 42), ("Extra Large", 50)]),
            "Track Height: Compact 8, Medium 25, Large 42, Extra Large 50");
        var nearest = new[] { 1.0, 16, 17, 30, 33, 34, 46, 50 }.Select(h => (h, MainWindow.NearestTrackHeight(h))).ToList();
        text.Append("   nearest preset: ").Append(string.Join(", ", nearest.Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.h:0} -> {n.Item2}")))).Append('\n');
        expect(nearest.Select(n => n.Item2).SequenceEqual([8, 8, 25, 25, 25, 42, 42, 50]), "the preset ticked is the nearest (1, 16 -> 8; 17, 30, 33 -> 25; 34, 46 -> 42; 50 -> 50)");

        // Undo / Redo on the row of tools (5.3-Stage2, the user's de-clutter): its item is in Bars & Sizing, after the hotbar's, and kept
        // in ShowTimelineUndoRedo, off in a new install.
        var bars = groups.Single(g => g.Header == "Bars & Sizing").Items;
        var undoRedo = all.Single(i => i.Setting == MainWindow.TimelineSetting.UndoRedoButtons);
        expect(bars.Contains(undoRedo) && bars.ToList().IndexOf(undoRedo) == bars.ToList().FindIndex(i => i.Setting == MainWindow.TimelineSetting.CreatorHotbar) + 1,
            "Show Undo / Redo Buttons is in Bars & Sizing, right after Show Quick FX Button");
        var quickFx = all.Single(i => i.Setting == MainWindow.TimelineSetting.CreatorHotbar);
        expect(quickFx is { Header: "Show Quick FX Button", Kept: nameof(AppSettings.ShowCreatorHotbar) },
            "the Quick FX switch reads Show Quick FX Button (5.3-Stage3, the user's answer), still kept in ShowCreatorHotbar so saved settings carry over");
        expect(undoRedo is { Header: "Show Undo / Redo Buttons", Kept: nameof(AppSettings.ShowTimelineUndoRedo), KeyAction: null } && !fresh.ShowTimelineUndoRedo,
            "it is kept in AppSettings.ShowTimelineUndoRedo, off in a new install (Ctrl+Z and Ctrl+Y undo and redo as ever), with no key of its own");

        // The six settings of 5.3: their defaults, and the settings file's own JSON (AppSettingsJsonContext) keeps them.
        expect(fresh is { ShowTimelineEffectLanes: true, ShowBuiltInMaskEffectLanes: false, ShowClipFilmstrips: true, ShowClipWaveforms: true, TimelineFollowsPlayhead: true, ShowTimelineUndoRedo: false },
            "a new install: effect lanes on, the built-in Mask & Edge lanes off, filmstrips, waveforms and following the playhead on, the Undo / Redo buttons off");
        var changed = fresh.Clone();
        (changed.ShowTimelineEffectLanes, changed.ShowBuiltInMaskEffectLanes, changed.ShowClipFilmstrips, changed.ShowClipWaveforms, changed.TimelineFollowsPlayhead) = (false, true, false, false, false);
        changed.ShowTimelineUndoRedo = true;
        var json = JsonSerializer.Serialize(changed, AppSettingsJsonContext.Default.AppSettings);
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            bool Written(string property, bool value) => root.TryGetProperty(property, out var element) && element.ValueKind == (value ? JsonValueKind.True : JsonValueKind.False);
            expect(Written(nameof(AppSettings.ShowTimelineEffectLanes), false) && Written(nameof(AppSettings.ShowBuiltInMaskEffectLanes), true) && Written(nameof(AppSettings.ShowClipFilmstrips), false)
                   && Written(nameof(AppSettings.ShowClipWaveforms), false) && Written(nameof(AppSettings.TimelineFollowsPlayhead), false) && Written(nameof(AppSettings.ShowTimelineUndoRedo), true),
                "each is written into the settings file by its own name");

            // And a new install's file says the Undo / Redo buttons are off, by name too.
            using var plain = JsonDocument.Parse(JsonSerializer.Serialize(fresh, AppSettingsJsonContext.Default.AppSettings));
            expect(plain.RootElement.TryGetProperty(nameof(AppSettings.ShowTimelineUndoRedo), out var off) && off.ValueKind == JsonValueKind.False,
                "a new install's settings file writes ShowTimelineUndoRedo false");
        }

        var back = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
        expect(back is { ShowTimelineEffectLanes: false, ShowBuiltInMaskEffectLanes: true, ShowClipFilmstrips: false, ShowClipWaveforms: false, TimelineFollowsPlayhead: false, ShowTimelineUndoRedo: true },
            "and read back as it was written");
        var unchanged = JsonSerializer.Deserialize(JsonSerializer.Serialize(fresh, AppSettingsJsonContext.Default.AppSettings), AppSettingsJsonContext.Default.AppSettings);
        expect(unchanged is { ShowTimelineUndoRedo: false }, "ShowTimelineUndoRedo false is read back false");
        var older = JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings);
        expect(older is { ShowTimelineEffectLanes: true, ShowBuiltInMaskEffectLanes: false, ShowClipFilmstrips: true, ShowClipWaveforms: true, TimelineFollowsPlayhead: true, ShowTimelineUndoRedo: false },
            "a settings file from before 5.3 (none of them in it) reads as a new install has them");
    }
}
