using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>Pass 5.4A: a clip moved onto a new track, the Background Blur under every picture, drops that add to the open project, an export that ends on its last picture, the track-height pill.</summary>
public static class NleFixesTests
{
    private const int W = 1920, H = 1080;
    private const string A = "D:/media/obs-a.mkv", B = "D:/media/obs-b.mp4", Second = "D:/media/second.mkv", Other = "D:/media/other.mkv";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("clip-to-new-track", "# A clip onto a new track above or below (5.4A): past its track's Cutout lanes, its stream sounds and Cutouts on lanes under the new track, one step of Undo.", ClipToNewTrack);
        yield return Case("clip-to-new-track-keys", "# Alt+Up / Alt+Down and the clip menu: onto the next track where it is free, else a new track; a track per source, one step of Undo, the selection kept.", ClipToNewTrackKeys);
        yield return Case("timeline-new-track-seam", "# The timeline control's seams: above, below and between the picture rows a drag shows the + New Video Track bar and lets go onto a new track.", NewTrackSeam);
        yield return Case("blur-fill-bottom", "# Every Background Blur twin at the very bottom of the stack (Z = 0), under every picture of every track.", BlurFillBottom);
        yield return Case("blur-fill-auto-apply", "# A file opened gets a blur fill only where it leaves the frame bare, and only as the Sequence's first picture.", BlurFillAutoApply);
        yield return Case("drop-routing", "# Where a drop goes: with clips on the Sequence, media dropped anywhere is added to the timeline, never opened over the project.", DropRouting);
        yield return Case("export-picture-end", "# An export ends on its last picture: no frames of black for an audio tail, and the last frame sampled inside its clip.", ExportPictureEnd);
        yield return Case("track-height-pill-icon", "# The track-height pill: three track bars and a double-headed arrow, never a Keyframe diamond.", TrackHeightPillIcon);
        yield return Case("whole-file-picture-end", "# A file whose container outlasts its picture, opened untouched, is still the whole file (Copy remux, its subtitles, chapters and copied sound); a trim of a frame makes it a cut.", WholeFilePictureEnd);
        yield return Case("loop-picture-rounds", "# A padded file looped to the end of the timeline: every round as long as its picture, never its container, so no padding at a seam or at the end.", LoopPictureRounds);
        yield return Case("new-project-location-debounce", "# New Project's location box offers its free name once typing rests, and only for a folder that is there.", NewProjectLocationDebounce);
        yield return Case("video-duration-rounding", "# A video stream's probed length, rounded a hair short of its last frame, still reaches that frame.", VideoDurationRounding);
        yield return Case("alt-arrow-focus", "# Alt+Up / Alt+Down are left to the focused control only for a combo box and the three buttons that open a flyout on Alt+Down.", AltArrowFocus);
        yield return Case("chapters-output-end", "# Chapters end with the export (its last picture), not with an audio tail past it.", ChaptersOutputEnd);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
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
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // A video clip of a file on a track, with its sound on it (its first stream), 10 s of file.
    private static TimelineClip VideoClip(string file, double start, double end, double offset, int firstStream) => new()
    {
        ContentKind = LayerKind.VideoFile, SourcePath = file, Start = start, End = end, MediaOffset = offset, SourceDuration = 10, AudioStreamIndex = firstStream,
        State = new LayerState { Kind = LayerKind.VideoFile, Name = System.IO.Path.GetFileNameWithoutExtension(file), ImagePath = file, ImageWidth = W, ImageHeight = H, MediaDuration = 10, HasAudio = firstStream >= 0, SizeWidth = 0.5 },
    };

    private static MediaInfo Probed(string container, params (int Index, string Title)[] streams) =>
        new(container, 10, 0, new VideoStreamInfo(W, H, "h264", 60, "16:9", "1:1", "bt709", "bt709", 0),
            [.. streams.Select(s => new AudioStreamInfo(s.Index, "aac", 2, "stereo", 48000, 160000, s.Title, ""))], []);

    private static void Unpack(MainViewModel vm, TimelineClip clip, MediaInfo info) =>
        typeof(MainViewModel).GetMethod("UnpackStreams", Hidden)!.Invoke(vm, [vm.ViewOf(clip)!, info, false]);

    // ----- clip-to-new-track -----

    private static void ClipToNewTrack(Action<bool, string> expect)
    {
        // Track T: A 0-3, B 3-6, A 6-8, each with three streams; a Cutout lane over it holding a Cutout of A's first clip and one of B.
        var made = new TimelineTrackModel { Kind = TimelineTrackKind.Video };
        made.Clips.AddRange([VideoClip(A, 0, 3, 0, 1), VideoClip(B, 3, 6, 0, 2), VideoClip(A, 6, 8, 3, 1)]);
        var cutouts = new TimelineTrackModel { Kind = TimelineTrackKind.Video, ParentTrackId = made.Id };
        var heroA = VideoClip(A, 0, 3, 0, -1);
        heroA.State.Name = "Hero";
        heroA.ParentClipId = made.Clips[0].Id;
        var heroB = VideoClip(B, 3, 6, 0, -1);
        heroB.State.Name = "Hero";
        heroB.ParentClipId = made.Clips[1].Id;
        cutouts.Clips.AddRange([heroA, heroB]);
        var vm = DogfoodTests.Open(made, cutouts);
        var (trackId, laneId, firstId, bId, heroId, mainId) = (made.Id, cutouts.Id, made.Clips[0].Id, made.Clips[1].Id, heroB.Id, vm.Document.Tracks[0].Clips[0].Id);
        TimelineTrackModel Track(Guid id) => vm.Document.Tracks.First(t => t.Id == id);
        TimelineClip Clip(Guid id) => vm.Document.FindClip(id)!;
        Layer View(Guid id) => vm.ViewOf(Clip(id))!;
        int At(TimelineTrackModel track) => vm.Document.Tracks.IndexOf(track);
        List<TimelineClip> Streams(Guid parent) => [.. vm.Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio).SelectMany(t => t.Clips).Where(c => c.ParentClipId == parent)];

        var (infoA, infoB) = (Probed("matroska", (1, "Game"), (2, "Mic"), (3, "Discord")), Probed("mp4", (2, "Game"), (3, "Mic"), (4, "Discord")));
        foreach (var clip in Track(trackId).Clips.ToList())
            Unpack(vm, clip, clip.SourcePath == A ? infoA : infoB);
        expect(Streams(bId).Count == 3 && Streams(bId).All(s => vm.Document.TrackOf(s)!.ParentTrackId == trackId), "set up: B's three streams on its track's shared lanes");
        var count = vm.Document.Tracks.Count;

        // Above: past the track's Cutout lane, B's family on lanes under the new track.
        expect(vm.CanMoveClipToNewTrack(View(bId), View(firstId), above: true, 3), "B may go onto a new track above its own");
        expect(vm.MoveClipToNewTrack(View(bId), View(firstId), above: true, 3), "B moved onto a new track above");
        var b = Clip(bId);
        var fresh = vm.Document.TrackOf(b)!;
        expect(fresh.Id != trackId && fresh is { Kind: TimelineTrackKind.Video, ParentTrackId: null } && b.Start == 3 && b.End == 6,
            "on a new top-level video track, at its own time (3 to 6 s)");
        expect(At(fresh) == At(Track(laneId)) + 1 && At(Track(laneId)) == At(Track(trackId)) + 1,
            "the new track lies past its old track's Cutout lane: nothing comes between a track and its Cutouts");
        var streams = Streams(bId);
        expect(streams.Count == 3 && streams.All(s => vm.Document.TrackOf(s)!.ParentTrackId == fresh.Id && s.Start == 3 && s.End == 6),
            "its three stream sounds on lanes under the new track, still its children, at its time");
        expect(streams.Select(s => vm.Document.TrackOf(s)!.StreamOrdinal).Order().SequenceEqual(new int?[] { 0, 1, 2 })
            && streams.OrderBy(s => vm.Document.TrackOf(s)!.StreamOrdinal).Select(s => s.AudioStreamIndex).SequenceEqual([2, 3, 4]),
            "each on the lane of its stream place (Stream 1 to 3), its own stream number kept");
        var heroLane = vm.Document.TrackOf(Clip(heroId))!;
        expect(heroLane.ParentTrackId == fresh.Id && At(heroLane) == At(fresh) + 1 && Clip(heroId).ParentClipId == bId,
            "its Cutout on a lane under the new track, just over it");
        var oldLanes = vm.Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId == trackId).ToList();
        expect(oldLanes.Count == 3 && oldLanes.All(l => l.Clips.Count == 2 && l.Clips.All(c => c.SourcePath == A)), "the old track's lanes keep only A's streams");
        expect(Track(laneId).Clips.Count == 1 && Track(laneId).Clips[0].Id == heroA.Id, "and its Cutout lane only A's Cutout");
        expect(vm.Document.Tracks.Count == count + 5, "five tracks more: the new track, its three stream lanes and its Cutout lane");
        expect(vm.Document.FindProblems().Count == 0, "the document holds together");

        vm.Undo();
        expect(vm.Document.TrackOf(Clip(bId))!.Id == trackId && vm.Document.Tracks.Count == count, "one Undo puts B back on its track, the tracks as they were");
        expect(Streams(bId).All(s => vm.Document.TrackOf(s)!.ParentTrackId == trackId) && vm.Document.TrackOf(Clip(heroId))!.Id == laneId,
            "with its streams and its Cutout on their old lanes");
        vm.Redo();
        expect(vm.Document.TrackOf(Clip(bId))!.Id != trackId && vm.Document.Tracks.Count == count + 5, "Redo moves it again");
        vm.Undo();

        // Below: under the track's whole family, and later in time as it goes.
        expect(vm.MoveClipToNewTrack(View(bId), View(firstId), above: false, 10), "B moved onto a new track below, dragged to 10 s");
        b = Clip(bId);
        fresh = vm.Document.TrackOf(b)!;
        var takenLane = vm.Document.TrackOf(Clip(heroId))!;
        expect(At(takenLane) == At(fresh) + 1 && At(Track(trackId)) == At(takenLane) + 1 && At(fresh) > At(Track(vm.Document.Tracks.First(t => t.Clips.Any(c => c.Id == mainId)).Id)),
            "the new track lies just under the track (B's Cutout lane between them, over its own track), over the track under it");
        expect(b.Start == 10 && b.End == 13 && Streams(bId).All(s => s.Start == 10 && s.End == 13 && vm.Document.TrackOf(s)!.ParentTrackId == fresh.Id),
            "at 10 s, its streams with it in time and on its lanes");
        vm.Undo();

        // What never moves this way, and the seams that change nothing.
        var main = View(mainId);
        expect(!vm.CanMoveClipToNewTrack(main, main, above: true, 0) && !vm.CanMoveClipToNewTrack(main, View(firstId), above: false, 0),
            "a clip alone on its track: neither seam of its own track (a no-op)");
        expect(vm.CanMoveClipToNewTrack(main, View(firstId), above: true, 0), "but past the next track it may go");
        expect(!vm.CanMoveClipToNewTrack(View(heroId), View(firstId), above: true, 3), "a Cutout is refused");
        expect(!vm.CanMoveClipToNewTrack(vm.ViewOf(Streams(bId)[0])!, View(firstId), above: true, 3), "a stream's sound is refused");
    }

    // ----- clip-to-new-track-keys -----

    private static void ClipToNewTrackKeys(Action<bool, string> expect)
    {
        // Main 0-8 alone on Track 1; T: X 0-3, Y 4-7; U: W 4-6, Z 10-12.
        var t = DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 3), DogfoodTests.FileClip(Second, 4, 7, 4));
        var u = DogfoodTests.Lane(DogfoodTests.FileClip(Other, 4, 6), DogfoodTests.FileClip(Other, 10, 12, 6));
        var vm = DogfoodTests.Open(t, u);
        var (tId, uId) = (t.Id, u.Id);
        var (xId, yId, zId, mainId) = (t.Clips[0].Id, t.Clips[1].Id, u.Clips[1].Id, vm.Document.Tracks[0].Clips[0].Id);
        TimelineTrackModel Track(Guid id) => vm.Document.Tracks.First(k => k.Id == id);
        TimelineTrackModel On(Guid clip) => vm.Document.TrackOf(vm.Document.FindClip(clip)!)!;
        Layer View(Guid id) => vm.ViewOf(vm.Document.FindClip(id)!)!;
        int At(TimelineTrackModel track) => vm.Document.Tracks.IndexOf(track);
        var count = vm.Document.Tracks.Count;

        expect(vm.MoveClipsToNewTrack([View(xId)], above: true, ontoNeighbour: true), "Alt+Up on X");
        expect(On(xId).Id == uId && vm.Document.Tracks.Count == count, "the track above is free for it: onto that track, no new one");
        vm.Undo();

        expect(vm.MoveClipsToNewTrack([View(yId)], above: true, ontoNeighbour: true), "Alt+Up on Y");
        expect(On(yId).Id is var y && y != tId && y != uId && At(On(yId)) == At(Track(tId)) + 1 && At(On(yId)) < At(Track(uId)) && vm.Document.Tracks.Count == count + 1,
            "the track above holds a clip in its way: a new track directly above its own");
        vm.Undo();

        expect(vm.MoveClipsToNewTrack([View(xId), View(zId)], above: true), "the menu's Move to New Track Above on X and Z together");
        expect(vm.Document.Tracks.Count == count + 2 && At(On(xId)) == At(Track(tId)) + 1 && At(On(zId)) == At(Track(uId)) + 1,
            "a new track for each track they came from, each just above its own");
        expect(vm.SelectedClips.Select(v => v.ClipId).ToHashSet().SetEquals([xId, zId]), "the two clips stay selected");
        vm.Undo();
        expect(vm.Document.Tracks.Count == count && On(xId).Id == tId && On(zId).Id == uId, "one Undo puts both back");

        expect(!vm.MoveClipsToNewTrack([View(mainId)], above: false, ontoNeighbour: true) && vm.Document.Tracks.Count == count && vm.StatusText.Contains("already", StringComparison.Ordinal),
            "Alt+Down on a clip alone on the bottom track: refused, said in the status bar");
        expect(vm.MoveClipsToNewTrack([View(mainId)], above: true, ontoNeighbour: true), "Alt+Up on that clip, the track above full where it lies");
        expect(At(On(mainId)) == At(Track(tId)) + 1 && At(On(mainId)) < At(Track(uId)) && vm.Document.Tracks.Count == count,
            "alone on its track, it goes onto a new track past the next track up (its old track goes): it moves one level");
        vm.Undo();
        expect(!vm.CanMoveClipsToNewTrack([]), "nothing selected, nothing to move (the key is left to the focused control)");

        expect(KeybindManager.Action("clip.newTrackAbove") is { Defaults: ["Alt+Up"] } above && above.Has(KeyActionFlags.ChangesProject)
            && KeybindManager.Action("clip.newTrackBelow") is { Defaults: ["Alt+Down"] },
            "the keys: Alt+Up and Alt+Down, changing the project (refused while a drag is held)");
        expect(KeybindManager.Parse("Alt+Up") == new InputChord(ModifierKeys.Alt, Key.Up) && KeybindManager.Parse("Alt+Down") == new InputChord(ModifierKeys.Alt, Key.Down),
            "read as the Alt chords of the arrows");
        expect(KeybindManager.CheckPresets().Count == 0, "no preset gives one chord to two actions");
    }

    // ----- timeline-new-track-seam -----

    private static void NewTrackSeam(Action<bool, string> expect)
    {
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(1000, 300));
        surface.Arrange(new Rect(0, 0, 1000, 300));
        surface.SetView(0, 10, 10);
        var scale = surface.PixelsPerSecond;
        var calls = new List<string>();
        var block = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "clip", Clip = "clip",
            CanDrag = () => true,
            Commit = (_, _) => calls.Add("commit"),
            CanMoveTo = (_, _) => true,
            MoveTo = (data, _) => calls.Add($"move to {data}"),
            CanMoveToNewTrack = (_, _, _) => true,
            MoveToNewTrack = (data, above, left) => calls.Add(string.Create(CultureInfo.InvariantCulture, $"new track {(above ? "above" : "below")} {data} at {left / scale:0.00} s")),
        };
        var captions = new TimelineRow { Key = "captions", Data = "captions", Kind = TimelineRowKind.Captions, Height = 56 };
        var upper = new TimelineRow { Key = "upper", Data = "upper", Kind = TimelineRowKind.Video, Height = 56 };
        var lower = new TimelineRow { Key = "lower", Data = "lower", Kind = TimelineRowKind.Video, Height = 56 };
        var background = new TimelineRow { Key = "background", Data = "background", Kind = TimelineRowKind.Background, Height = 56 };
        lower.Blocks.Add(block);
        surface.SetRows([captions, upper, lower, background]);
        surface.UpdateLayout();

        (double Top, double Bottom) Lane(string key)
        {
            var lane = surface.LaidLanes.First(l => Equals(l.Key, key));
            return (lane.Top, lane.Top + lane.Height);
        }

        var (upperLane, lowerLane, captionsLane, backgroundLane) = (Lane("upper"), Lane("lower"), Lane("captions"), Lane("background"));
        var seamY = (upperLane.Bottom + lowerLane.Top) / 2;
        Point At(double seconds, double y) => new(surface.HeaderWidth + seconds * scale, y);
        bool Seam(string key, bool above) => surface.NewTrackSeam is { } seam && Equals(seam.Key, key) && seam.Above == above;

        byte[] Shot()
        {
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1000, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var taken = new byte[1000 * 300 * 4];
            bitmap.CopyPixels(taken, 1000 * 4, 0);
            return taken;
        }

        surface.PressBlockAt(lower, block, At(2, lowerLane.Top + 28), ModifierKeys.None);
        expect(surface.MoveBlockDrag(At(2, seamY)) && Seam("lower", above: true), "dragged up to the seam between the two picture rows: a new track there (said as above the lower row)");
        var atSeam = Shot();
        surface.MoveBlockDrag(At(2, (upperLane.Top + upperLane.Bottom) / 2));
        expect(surface.NewTrackSeam is null, "in the middle of the upper row: no seam, the row itself is the target as before");
        var onRow = Shot();
        var (barX, barY) = ((int)At(8.5, 0).X, (int)Math.Round(seamY));
        var at = (barY * 1000 + barX) * 4;
        expect(atSeam[at] != onRow[at] || atSeam[at + 1] != onRow[at + 1] || atSeam[at + 2] != onRow[at + 2], "at the seam the insertion bar is drawn across the time area (far from the clip)");
        surface.MoveBlockDrag(At(2, captionsLane.Top + 20));
        expect(Seam("upper", above: true), "over the Captions row above every picture row: a new track above the top one");
        surface.MoveBlockDrag(At(2, -10));
        expect(Seam("upper", above: true), "above the control altogether: the same");
        surface.MoveBlockDrag(At(2, backgroundLane.Top + 28));
        expect(Seam("lower", above: false), "over the Background row under them: a new track below the bottom one");
        surface.MoveBlockDrag(At(2, seamY));
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["new track above lower at 1.00 s"]), "let go at the seam: onto a new track there, once, and neither a move along its row nor onto another");

        // A move along its own row near its top edge, under the guard's vertical distance: never a seam.
        calls.Clear();
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, lowerLane.Top + 4), ModifierKeys.None);
        surface.MoveBlockDrag(At(2.6, lowerLane.Top + 6));
        expect(surface.NewTrackSeam is null, "along its own row in its top band, moved 2 DIPs down: no seam (the guard)");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["commit"]), "let go, a plain move along its row");

        // Its own row's edges: only the one it is heading for.
        calls.Clear();
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, lowerLane.Bottom - 2), ModifierKeys.None);
        surface.MoveBlockDrag(At(2, lowerLane.Bottom - 12));
        expect(surface.NewTrackSeam is null, "moved up inside its own row's bottom band: not the seam under it");
        surface.MoveBlockDrag(At(2, lowerLane.Top + 3));
        expect(Seam("lower", above: true), "moved on up into its top band: the seam over it");
        surface.MoveBlockDrag(At(2, lowerLane.Top + 28));
        surface.ReleaseBlockDrag();

        // A selected group goes onto no other row, and onto no new track either.
        var member = new TimelineBlock { Left = 6 * scale, Width = 1 * scale, Name = "member", Clip = "member", CanDrag = () => true, Commit = (_, _) => calls.Add("member") };
        upper.Blocks.Add(member);
        surface.IsSelected = clip => clip is "clip" or "member";
        surface.SetRows([captions, upper, lower, background]);
        surface.UpdateLayout();
        calls.Clear();
        block.Left = 1 * scale;
        surface.PressBlockAt(lower, block, At(2, lowerLane.Top + 28), ModifierKeys.None);
        surface.MoveBlockDrag(At(2, seamY));
        expect(surface.NewTrackSeam is null, "a group dragged to the seam: no new track");
        surface.MoveBlockDrag(At(2, lowerLane.Top + 28));
        surface.ReleaseBlockDrag();
        surface.IsSelected = null;
        expect(!calls.Exists(c => c.StartsWith("new track", StringComparison.Ordinal)), "and none made when it is let go");
    }

    // ----- blur-fill-bottom -----

    private static GpuSceneLowering Lowered(MainViewModel vm)
    {
        typeof(MainViewModel).GetField("_previewState", Hidden)!.SetValue(vm, null);
        return TimelineLowering.LowerForGpu(vm.LivePreviewState());
    }

    private static void BlurFillBottom(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 8)));
        var (mainId, secondId) = (vm.Document.Tracks[0].Clips[0].Id, vm.Document.Tracks[1].Clips[0].Id);
        Layer View(Guid id) => vm.ViewOf(vm.Document.FindClip(id)!)!;
        static string Names(GpuSceneLowering low, Guid main) =>
            string.Join(" | ", low.Stack.Select(l => (low.BlurTwins.Contains(l) ? "Background Blur of " : "") + (l.ClipId == main ? "Main" : "Second")));

        vm.SetBlurFill(View(secondId), true);
        var low = Lowered(vm);
        expect(Names(low, mainId) == "Background Blur of Second | Main | Second", $"Track 2's blur fill on: {Names(low, mainId)}");
        var twins = Enumerable.Range(0, low.Stack.Count).Where(k => low.BlurTwins.Contains(low.Stack[k])).ToList();
        var pictures = Enumerable.Range(0, low.Stack.Count).Where(k => !low.BlurTwins.Contains(low.Stack[k])).ToList();
        expect(twins is [0] && twins.Max() < pictures.Min(), "its twin at the very bottom, under Track 1's picture: an upper track's blur never covers a lower track");

        vm.SetBlurFill(View(mainId), true);
        low = Lowered(vm);
        expect(Names(low, mainId) == "Background Blur of Main | Background Blur of Second | Main | Second", $"both tracks' blur fills on: {Names(low, mainId)}");

        vm.Document.Tracks[0].CaptionsUnder = true;
        low = Lowered(vm);
        expect(low.CaptionsAt == 2, "captions put under Track 1 stay above every twin");
        vm.Document.Tracks[0].CaptionsUnder = false;

        vm.SetClipOn(View(mainId), false);
        low = Lowered(vm);
        expect(low.BlurTwins.Count == 1 && low.Stack[0].ClipId == secondId, "a clip switched off has no twin");
    }

    // ----- blur-fill-auto-apply -----

    private static MainViewModel Empty()
    {
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations, settings.DroppedClipsJoinTrack) = (false, false, false, false);
        var vm = new MainViewModel { SourceWidth = W, SourceHeight = H };
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [new TimelineDocument { Width = W, Height = H, NaturalWidth = W, NaturalHeight = H, FrameRate = FrameRate.Fps30 }]);
        return vm;
    }

    // A file opened as the view model opens one once probed (PlaceOpenedFile), over nothing: its clip, or null.
    private static TimelineClip? OpenFile(MainViewModel vm, string path, MediaInfo info)
    {
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, info);
        (vm.SourceWidth, vm.SourceHeight) = (info.Video?.Width ?? 0, info.Video?.Height ?? 0);
        vm.LocalMediaPath = path;
        typeof(MainViewModel).GetMethod("PlaceOpenedFile", Hidden)!.Invoke(vm, [""]);
        return vm.Document.Tracks.SelectMany(t => t.Clips).LastOrDefault(c => c.ParentClipId is null && c.SourcePath == path);
    }

    private static MediaInfo Video(int width, int height) => new("matroska", 8, 0, new VideoStreamInfo(width, height, "h264", 30, "16:9", "1:1", "bt709", "bt709", 0), [], []);

    private static void BlurFillAutoApply(Action<bool, string> expect)
    {
        var settings = AppSettings.Current;
        var sizing = settings.NewClipSizing;
        try
        {
            settings.NewClipSizing = AppSettings.NewClipFit;
            expect(OpenFile(Empty(), Second, Video(W, H)) is { State.BlurFill: null }, "a 16:9 file into an empty 16:9 Sequence fills it: no blur fill");
            expect(OpenFile(Empty(), Second, Video(1080, 1920)) is { State.BlurFill: null }, "a 9:16 file into an empty Sequence, which takes its shape: no blur fill");

            var vertical = Empty();
            vertical.UseVerticalResolution = true;
            expect(OpenFile(vertical, Second, Video(W, H)) is { State.BlurFill: true }, "a 16:9 file into a 9:16 vertical Sequence leaves bars: blur fill on");

            var hidden = Empty();
            hidden.UseVerticalResolution = true;
            hidden.SetBackgroundShown(false);
            expect(OpenFile(hidden, Second, Video(W, H)) is { State.BlurFill: null }, "the same with the Background Blur row switched off: none");

            var opened = DogfoodTests.Open();
            opened.UseVerticalResolution = true;
            expect(OpenFile(opened, Other, Video(W, H)) is { State.BlurFill: null }, "a file opened onto a Sequence that has clips is a further layer: none, though it leaves bars");

            settings.NewClipSizing = AppSettings.NewClipFill;
            var filled = Empty();
            filled.UseVerticalResolution = true;
            expect(OpenFile(filled, Second, Video(W, H)) is { State.BlurFill: null }, "Settings' New Clips set to fill: it covers the frame, so none");
        }
        finally
        {
            settings.NewClipSizing = sizing;
        }
    }

    // ----- drop-routing -----

    private static void DropRouting(Action<bool, string> expect)
    {
        static MainWindow.DropRoute Route(bool hasClips, bool hasSource, string[] files, bool busy = false, bool onTimeline = false, bool onAudioTab = false, bool fromBrowser = false, bool isLink = false,
            bool onMediaBin = false) =>
            MainWindow.RouteOf(hasClips, hasSource, busy, onTimeline, onAudioTab, fromBrowser, isLink, files, onMediaBin);

        string[] mp4 = ["C:/clips/a.mp4"], png = ["C:/clips/logo.png"], wav = ["C:/clips/voice.wav"], project = ["C:/clips/edit.hproj"], bundle = ["C:/clips/edit.hpb"];
        expect(Route(true, true, mp4) == MainWindow.DropRoute.AddAtPlayhead, "clips on the Sequence, an mp4 from Explorer on the Player: added at the playhead");
        expect(Route(true, true, png) == MainWindow.DropRoute.AddAtPlayhead && Route(true, true, wav) == MainWindow.DropRoute.AddAtPlayhead, "a picture or a sound the same");
        expect(Route(true, true, ["C:/clips/a.mp4", "C:/clips/edit.hproj"]) == MainWindow.DropRoute.AddAtPlayhead
            && MainWindow.PlayheadFiles(["C:/clips/a.mp4", "C:/clips/edit.hproj"], fromBrowser: false) is ["C:/clips/a.mp4"], "a video with a project: the video added, the project left out");
        expect(Route(true, true, project) == MainWindow.DropRoute.OpenProject && Route(true, true, bundle) == MainWindow.DropRoute.OpenProject,
            "a project or a bundle with one open: opened in a Project Tab, never over it");
        expect(Route(true, true, project, busy: true) == MainWindow.DropRoute.OpenOrReplace, "while busy: the open path, which says to wait");
        expect(Route(true, true, ["C:/clips/notes.txt"]) == MainWindow.DropRoute.OpenOrReplace, "a file that is neither media nor a project keeps today's path");
        expect(Route(false, false, mp4) == MainWindow.DropRoute.OpenOrReplace, "nothing open, one mp4: opened");
        expect(Route(false, false, ["C:/clips/a.mp4", "C:/clips/b.mp4", "C:/clips/c.mp4"]) == MainWindow.DropRoute.OpenThenFollow
            && MainWindow.InLineAfter(["C:/clips/a.mp4", "C:/clips/b.mp4", "C:/clips/c.mp4"]).Length == 2, "nothing open, three: the first opened, the others following it");
        expect(Route(false, false, ["C:/clips/a.mp4", "C:/clips/voice.wav"]) == MainWindow.DropRoute.OpenOrReplace, "a sound never follows in line");
        expect(Route(true, true, mp4, onTimeline: true) == MainWindow.DropRoute.TimelinePlace && Route(true, true, mp4, onAudioTab: true) == MainWindow.DropRoute.AudioTab,
            "on the timeline where it was let go; on the Audio tab as sound");
        expect(Route(true, true, [], isLink: true) == MainWindow.DropRoute.AddAtPlayhead && Route(false, false, [], isLink: true) == MainWindow.DropRoute.OpenOrReplace
            && Route(true, true, [], isLink: true, onTimeline: true) == MainWindow.DropRoute.TimelinePlace,
            "a link: downloaded and added with clips on the Sequence, opened with none, placed where let go on the timeline");
        expect(Route(false, true, mp4, fromBrowser: true) == MainWindow.DropRoute.AddAtPlayhead && Route(false, true, mp4) == MainWindow.DropRoute.OpenOrReplace,
            "a project open with no clips: a tile from the media browser is added (4.1.1-B), a file from Explorer opened as before");

        // 5.4C: onto the Media Browser pane, with a project open, media goes into Project Media (the project's bin), nothing onto the timeline.
        expect(Route(true, true, mp4, onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia && Route(true, true, png, onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia
            && Route(true, true, wav, onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia,
            "media from Explorer onto the Media Browser pane: into Project Media (a video, a picture, a sound)");
        expect(Route(false, true, mp4, onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia, "a project open with no clips yet: into Project Media too");
        expect(Route(true, true, mp4, fromBrowser: true, onMediaBin: true) == MainWindow.DropRoute.None, "a tile dragged from the browser and let go over it: no drop (5.4.4, the user's self-drop cancel)");
        expect(Route(true, true, ["C:/clips/a.mp4", "C:/clips/edit.hproj"], onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia
            && Route(true, true, project, onMediaBin: true) == MainWindow.DropRoute.OpenProject,
            "a video with a project over the bin: the video into Project Media; a project alone: opened in a Project Tab");
        expect(Route(true, true, [], isLink: true, onMediaBin: true) == MainWindow.DropRoute.AddAtPlayhead && Route(true, true, ["C:/clips/notes.txt"], onMediaBin: true) == MainWindow.DropRoute.OpenOrReplace,
            "a link over the bin keeps today's path (downloaded, at the playhead); a file that is not media too");
        // 5.4D (Q4 b): with nothing open too; a project alone, a file that is not media and a tile from the browser go as before.
        expect(Route(false, false, mp4, onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia
               && Route(false, false, ["C:/clips/a.mp4", "C:/clips/b.mov"], onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia
               && Route(false, false, project, onMediaBin: true) == MainWindow.DropRoute.OpenOrReplace
               && Route(false, false, ["C:/clips/notes.txt"], onMediaBin: true) == MainWindow.DropRoute.OpenOrReplace
               && Route(false, false, mp4, fromBrowser: true, onMediaBin: true) == MainWindow.DropRoute.None,
            "with nothing open (5.4D), media over the Media Browser goes into Project Media too (the blank window keeps it); a project alone and a file that is not media are opened as before; a tile from the browser let go over it is no drop (5.4.4)");

        var replaced = 0;
        foreach (var busy in new[] { false, true })
            foreach (var onTimeline in new[] { false, true })
                foreach (var onAudioTab in new[] { false, true })
                    foreach (var fromBrowser in new[] { false, true })
                        foreach (var onMediaBin in new[] { false, true })
                            foreach (var files in new[] { mp4, png, wav })
                                replaced += Route(true, true, files, busy, onTimeline, onAudioTab, fromBrowser, onMediaBin: onMediaBin) is MainWindow.DropRoute.OpenOrReplace or MainWindow.DropRoute.OpenThenFollow ? 1 : 0;
        expect(replaced == 0, "with clips on the Sequence no drop of media, anywhere, ever opens over the project");
    }

    // ----- export-picture-end -----

    private static TimelineDocument Document(FrameRate rate, params TimelineTrackModel[] tracks)
    {
        var document = new TimelineDocument { Width = W, Height = H, NaturalWidth = W, NaturalHeight = H, FrameRate = rate };
        var number = 1;
        foreach (var track in tracks)
        {
            track.TrackNumber = number++;
            document.Tracks.Add(track);
        }

        return document;
    }

    private static TimelineTrackModel Track(TimelineTrackKind kind, params TimelineClip[] clips)
    {
        var track = new TimelineTrackModel { Kind = kind };
        track.Clips.AddRange(clips);
        return track;
    }

    private static SequenceExportState StateOf(TimelineDocument document) => new()
    {
        Timeline = document, SourceFrameRate = document.FrameRate.Fps, CaptionLayer = new Layer { Kind = LayerKind.Captions },
    };

    private static TimelineClip Picture(double start, double end) => VideoClip(A, start, end, 0, 0);

    private static TimelineClip Sound(double start, double end) => new()
    {
        ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", Start = start, End = end, SourceDuration = 60, AudioStreamIndex = 0,
        State = new LayerState { Kind = LayerKind.Audio, Name = "music", ImagePath = "D:/media/music.wav", MediaDuration = 60, HasAudio = true },
    };

    private static void ExportPictureEnd(Action<bool, string> expect)
    {
        static long Frames(TimelineDocument document) => EncoderCommand.Timing(StateOf(document))!.Value.CanvasFrames;

        var tailed = Picture(0, 8);
        tailed.AudioTailOutSeconds = 0.5;
        var lCut = Document(FrameRate.Fps30, Track(TimelineTrackKind.Video, tailed));
        expect(lCut.TotalDuration > 8.4 && SequenceSpans.ProgramSeconds(lCut) == 8 && Frames(lCut) == 240,
            "a video 0-8 s with an L-cut tail of 0.5 s: exported to its picture's end, 240 frames, not 255 (15 of them black)");

        var music = Document(FrameRate.Fps30, Track(TimelineTrackKind.Video, Picture(0, 8)), Track(TimelineTrackKind.Audio, Sound(0, 10)));
        expect(SequenceSpans.ProgramSeconds(music) == 10 && Frames(music) == 300, "standalone music placed past the picture still runs to its end: 300 frames");

        var soundOnly = Document(FrameRate.Fps30, Track(TimelineTrackKind.Audio, Sound(0, 5)));
        expect(SequenceSpans.ProgramSeconds(soundOnly) == 5 && Frames(soundOnly) == 150, "no picture at all: the whole timeline, 150 frames");

        var title = new TimelineClip { ContentKind = LayerKind.Text, Start = 6, End = 9, State = new LayerState { Kind = LayerKind.Text, Name = "Title" } };
        var adjustment = new TimelineClip { ContentKind = LayerKind.Adjustment, Start = 0, End = 12, State = new LayerState { Kind = LayerKind.Adjustment, Name = "Look" } };
        var titled = Document(FrameRate.Fps30, Track(TimelineTrackKind.Video, Picture(0, 8)), Track(TimelineTrackKind.Video, title), Track(TimelineTrackKind.Adjustment, adjustment));
        expect(SequenceSpans.ProgramSeconds(titled) == 9 && Frames(titled) == 270, "a title ending last is a picture: to its end; an adjustment past it adds nothing");

        var parent = Picture(0, 8);
        var video = Track(TimelineTrackKind.Video, parent);
        var child = Sound(0, 8.4);
        child.ParentClipId = parent.Id;
        var lane = Track(TimelineTrackKind.Audio, child);
        lane.ParentTrackId = video.Id;
        lane.StreamOrdinal = 0;
        expect(SequenceSpans.ProgramSeconds(Document(FrameRate.Fps30, video, lane)) == 8, "a stream's sound (a child) past its video adds no frame");

        // Streams unpacked from a clip are never past it.
        var made = new TimelineTrackModel { Kind = TimelineTrackKind.Video };
        made.Clips.Add(VideoClip(A, 0, 6, 0, 1));
        var vm = DogfoodTests.Open(made);
        var clip = vm.Document.FindClip(made.Clips[0].Id)!;
        Unpack(vm, clip, Probed("matroska", (1, "Game"), (2, "Mic")));
        var children = vm.Document.ChildrenOf(clip).Select(c => c.Clip).ToList();
        expect(children.Count == 2 && children.All(c => c.End <= clip.End + 1e-9), "unpacked streams end with their video");

        // An end off the frame grid: the last frame is sampled half a frame inside the clip, never at or past its end.
        var free = Document(FrameRate.Fps60, Track(TimelineTrackKind.Video, Picture(0, 10.0169)));
        free.QuantizeToFrames = false;
        var timing = EncoderCommand.Timing(StateOf(free))!.Value;
        var last = timing.CanvasFrames - 1;
        expect(timing.CanvasSeconds(last) + GpuExport.FrameHair >= 10.0169, "an end at 10.0169 s at 60 fps: the last frame's own moment falls at or past the clip's end (the black frame)");
        expect(timing.SampleSeconds(last, GpuExport.FrameHair) <= 10.0169 - 0.5 / 60 + 1e-9, "sampled no later than half a frame before it");
        expect(timing.SampleSeconds(0, GpuExport.FrameHair) == GpuExport.FrameHair && GpuExportTiming.Of(1_000_000, FrameRate.Fps30, null).SampleLimitSeconds == double.PositiveInfinity,
            "every other frame sampled as before; a timing made without a limit has none");

        // A container longer than its picture: the clip ends where the video stream does, its file still the container's length.
        const string json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30/1","duration":"9.950000"},{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","duration":"10.020000"}],"format":{"format_long_name":"QuickTime / MOV","duration":"10.020000"}}""";
        using var parsed = JsonDocument.Parse(json);
        var info = (MediaInfo)typeof(MediaProbe).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static, [typeof(JsonElement)])!.Invoke(null, [parsed.RootElement])!;
        expect(info.DurationSeconds == 10.02 && info.Video is { DurationSeconds: 9.95 }, "the probe keeps the video stream's own length (9.95 s) beside the container's (10.02 s)");
        var placed = OpenFile(Empty(), Second, info);
        expect(placed is { End: > 9.9 and <= 9.95, SourceDuration: 10.02 }, "opened, its clip ends with its picture, its file as long as the container (a trim can still reach the rest)");
    }

    // ----- track-height-pill-icon -----

    private static void TrackHeightPillIcon(Action<bool, string> expect)
    {
        var strokes = TrackHeightPill.MarkStrokes(18, 20);
        var bars = strokes.Where(s => Math.Abs(s.From.Y - s.To.Y) < 1e-9).ToList();
        var shafts = strokes.Where(s => Math.Abs(s.From.X - s.To.X) < 1e-9).ToList();
        var heads = strokes.Except(bars).Except(shafts).ToList();
        expect(bars.Count == 3 && bars.Select(s => s.From.Y).Distinct().Count() == 3 && bars.All(s => Math.Abs(s.To.X - s.From.X) >= 6), "three horizontal track bars at three heights, each at least 6 DIPs long");
        expect(shafts is [var shaft] && Math.Abs(shaft.To.Y - shaft.From.Y) >= 0.6 * 20, "one vertical shaft over most of the height");
        var ends = shafts.Count == 1 ? new[] { shafts[0].From, shafts[0].To } : [];
        expect(heads.Count == 4 && heads.All(h => ends.Contains(h.To)) && ends.All(e => heads.Count(h => h.To == e) == 2), "an arrowhead of two strokes at each end of the shaft: up and down");
        var feet = heads.GroupBy(h => h.To).Select(g => g.Select(h => h.From.Y).ToList()).ToList();
        expect(feet.Count == 2 && feet[0].All(a => feet[1].All(b => Math.Abs(a - b) >= 4)), "the two heads' feet far apart: no two slanted strokes close into a diamond (the old chevrons' were 0.24 DIPs apart)");
        expect(bars.All(s => Math.Max(s.From.X, s.To.X) < shafts[0].From.X - 2.5), "the bars stand clear of the arrow");

        var pill = new TrackHeightPill();
        pill.Measure(new Size(18, 20));
        pill.Arrange(new Rect(0, 0, 18, 20));
        pill.UpdateLayout();
        var bitmap = new RenderTargetBitmap(18, 20, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(pill);
        var pixels = new byte[18 * 20 * 4];
        bitmap.CopyPixels(pixels, 18 * 4, 0);
        bool Lit(int x, int y) => pixels[(y * 18 + x) * 4 + 3] > 60;
        var barRows = Enumerable.Range(0, 20).Count(y => Enumerable.Range(0, 11).Count(x => Lit(x, y)) >= 6);
        var shaftRows = Enumerable.Range(0, 20).Count(y => Lit(14, y) || Lit(15, y));
        expect(barRows >= 3, "drawn: rows of lit pixels across the left (the bars)");
        expect(shaftRows >= 10, "and a column of them on the right (the arrow)");
    }

    // ----- whole-file-picture-end -----

    // A file probed as a recording often is: its picture 9.95 s at 30 fps, its sound (and so its container) padded to 10.02 s.
    private static MediaInfo Padded() =>
        new("mov,mp4,m4a,3gp,3g2,mj2", 10.02, 0, new VideoStreamInfo(W, H, "h264", 30, "16:9", "1:1", "bt709", "bt709", 0) { DurationSeconds = 9.95 },
            [new AudioStreamInfo(0, "aac", 2, "stereo", 48000, 160000, "", "")], []);

    private static void WholeFilePictureEnd(Action<bool, string> expect)
    {
        var vm = Empty();
        var placed = OpenFile(vm, Second, Padded());
        expect(placed is { SourceDuration: 10.02 } && Math.Abs(placed.End - 298 / 30.0) < 1e-6 && Math.Abs(placed.PictureDuration - placed.End) < 1e-6,
            "opened: its clip ends on the picture's last frame (298 frames at 30 fps) and keeps that as its picture's length, its file the container's 10.02 s");
        if (placed is null)
            return;

        expect(SequenceSpans.WholeFiles(vm.Document).Contains(Second), "the untouched clip is the whole file (WholeFiles: its subtitles, chapters and copied sound go with it), though 87 ms shorter than its container");
        expect(SequenceSpans.CopyCutOf(vm.Document) is { IsCut: false, Pieces.Count: 1 }, "a Copy export of it needs no cuts list: the file remuxed as it is");
        var command = EncoderCommand.BuildCopy(StateOf(vm.Document)).CommandLine;
        expect(!command.Contains("-f concat", StringComparison.Ordinal) && command.Contains(" -t 9.933333 ", StringComparison.Ordinal),
            "the Copy command reads the file itself, its copied sound held to the picture's end by the muxer's -t");

        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var saved = JsonSerializer.Serialize(placed, options);
        expect(saved.Contains("\"PictureDuration\":", StringComparison.Ordinal) && JsonSerializer.Deserialize<TimelineClip>(saved, options) is { } back && back.PictureDuration == placed.PictureDuration,
            "saved with the project (and copied, pasted or undone, which go through JSON) it keeps its picture's length");

        var trimmed = vm.Document.Clone(deep: true);
        var cut = trimmed.FindClip(placed.Id)!;
        expect(cut.PictureDuration == placed.PictureDuration, "a copy of the Sequence keeps it too");
        cut.End -= 1 / 30.0;
        expect(!SequenceSpans.WholeFiles(trimmed).Contains(Second) && SequenceSpans.CopyCutOf(trimmed) is { IsCut: true },
            "a frame trimmed off its end: no longer the whole file, and a Copy export cuts it");

        var plain = Empty();
        var full = new MediaInfo("matroska", 10, 0, new VideoStreamInfo(W, H, "h264", 30, "16:9", "1:1", "bt709", "bt709", 0) { DurationSeconds = 10 },
            [new AudioStreamInfo(0, "aac", 2, "stereo", 48000, 160000, "", "")], []);
        var untouched = OpenFile(plain, Other, full);
        expect(untouched is { PictureDuration: 0, End: 10 } && SequenceSpans.WholeFiles(plain.Document).Contains(Other)
            && !EncoderCommand.BuildCopy(StateOf(plain.Document)).CommandLine.Contains(" -t ", StringComparison.Ordinal)
            && !JsonSerializer.Serialize(untouched, options).Contains("PictureDuration", StringComparison.Ordinal),
            "a file whose picture is as long as its container: no picture length kept (nor written), the whole file as before, no -t");
    }

    // ----- loop-picture-rounds -----

    private static void LoopPictureRounds(Action<bool, string> expect)
    {
        var vm = Empty();
        var placed = OpenFile(vm, Second, Padded());
        expect(placed is { SourceDuration: 10.02 } && Math.Abs(placed.WholeDuration - 298 / 30.0) < 1e-6,
            "opened: a padded file (picture 9.95 s at 30 fps, container 10.02 s), its whole length the picture's 298 frames");
        if (placed is null)
            return;

        // Laid out to loop to 30 s, as a clip dropped to run to the end of the timeline is once its file is read.
        var layOut = typeof(MainViewModel).GetMethod("LayOut", Hidden)!;
        Action<ITimelineTransaction> loop = edit => layOut.Invoke(vm, [edit, placed, 30.0]);
        typeof(MainViewModel).GetMethod("Edit", Hidden)!.Invoke(vm, ["fill in a video clip", loop, false, true]);

        var whole = placed.WholeDuration;
        var rounds = vm.Document.TrackOf(placed)!.Clips.Where(c => c.ParentClipId is null && c.SourcePath == Second).OrderBy(c => c.Start).ToList();
        expect(rounds.Count == 4 && Math.Abs(rounds[0].Start) < 1e-6 && Math.Abs(rounds[^1].End - 30) < 1e-6,
            "four rounds (three of 298 frames and a short last one) from 0 s to the 30 s end");
        expect(rounds.All(r => r.End - r.Start <= whole + 1e-6), "every round, the first included, no longer than the picture: no padding shown at a seam");
        expect(rounds.All(r => r.MediaOffset + (r.End - r.Start) * r.Speed <= whole + 1e-6), "and every round's media inside the picture, the last one too: no frozen or black tail at the end");
        expect(rounds.Zip(rounds.Skip(1)).All(p => Math.Abs(p.First.End - p.Second.Start) < 1e-6), "the rounds meet end to start, with no gap");
        expect(rounds.Skip(1).All(r => r.IsMuted && r.MediaOffset == 0), "the rounds after the first from the file's first frame, silent (4.2.0's loop)");
    }

    // ----- new-project-location-debounce -----

    private static void NewProjectLocationDebounce(Action<bool, string> expect)
    {
        // The dialog's own handlers on a bare instance (none of its XAML read): its two boxes and its timer put in by hand.
        var window = (ProjectManagerWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ProjectManagerWindow));
        var type = typeof(ProjectManagerWindow);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        var (folderBox, nameBox) = (new System.Windows.Controls.TextBox(), new System.Windows.Controls.TextBox());
        type.GetField("_offerTimer", Hidden)!.SetValue(window, timer);
        type.GetField("NewFolderBox", Hidden)!.SetValue(window, folderBox);
        type.GetField("NewNameBox", Hidden)!.SetValue(window, nameBox);
        type.GetField("_offeredName", Hidden)!.SetValue(window, "");
        void Typed() => type.GetMethod("NewFolder_TextChanged", Hidden)!.Invoke(window, [folderBox, null]);
        void Rested() => type.GetMethod("OfferTimer_Tick", Hidden)!.Invoke(window, [timer, EventArgs.Empty]);

        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HandPeg.GoldenTests.NewProject", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            folderBox.Text = System.IO.Path.Combine(folder, "not-there");
            Typed();
            expect(timer.IsEnabled && nameBox.Text.Length == 0, "a keystroke in the location box: the folder not looked through yet, the pause timer running");
            Rested();
            expect(!timer.IsEnabled && nameBox.Text.Length == 0, "typing rested on a folder that is not there: no name offered (nothing probed), the timer stopped");

            folderBox.Text = folder;
            Typed();
            Typed();
            Rested();
            expect(!timer.IsEnabled && nameBox.Text == "New Project", "rested on a folder that is there (two keystrokes, one look): the first free name offered");
        }
        finally
        {
            timer.Stop();
            try
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // Left for the next run: a temp folder.
            }
        }
    }

    // ----- video-duration-rounding -----

    private static void VideoDurationRounding(Action<bool, string> expect)
    {
        static MediaInfo Probe(double fps, double video, double container) =>
            new("matroska", container, 0, new VideoStreamInfo(W, H, "h264", fps, "16:9", "1:1", "bt709", "bt709", 0) { DurationSeconds = video }, [], []);

        var ntsc = Empty();
        var at2997 = OpenFile(ntsc, Second, Probe(29.97, 10.010, 10.05));
        expect(ntsc.Document.FrameRate == FrameRate.Ntsc2997 && at2997 is not null && Math.Round(at2997.End * 30000 / 1001) == 300,
            "29.97 fps, the video stream read as 10.010 s (300 frames are 10.01001 s), the container 10.05 s: 300 frames, the last one kept");

        var sixty = Empty();
        var at60 = OpenFile(sixty, Second, Probe(60, 10.016, 10.05));
        expect(sixty.Document.FrameRate == FrameRate.Fps60 && at60 is not null && Math.Round(at60.End * 60) == 601,
            "60 fps, 601 frames (10.01667 s) read as 10.016 s: 601 frames, not 600");

        var thirty = Empty();
        var at30 = OpenFile(thirty, Second, Probe(30, 9.95, 10.02));
        expect(at30 is not null && Math.Round(at30.End * 30) == 298,
            "a stream that really ends inside a frame (9.95 s at 30 fps): still the last whole frame, 298, never past the picture");
    }

    // ----- alt-arrow-focus -----

    private static void AltArrowFocus(Action<bool, string> expect)
    {
        static System.Windows.Controls.Button Named(string name) => new() { Name = name };
        string[] flyouts = ["QuickFxButton", "AddLayerButton", "AddButton"];

        expect(flyouts.All(n => MainWindow.AltArrowBelongsToFocus(Key.Down, Named(n))), "Alt+Down on Quick FX, Add Track or the Effects pane's Add is theirs: it opens the flyout");
        expect(flyouts.All(n => !MainWindow.AltArrowBelongsToFocus(Key.Up, Named(n))), "Alt+Up on them moves the clip (they open nothing on Up)");
        expect(!MainWindow.AltArrowBelongsToFocus(Key.Down, Named("PlayPauseButton")) && !MainWindow.AltArrowBelongsToFocus(Key.Up, Named("SnapButton"))
            && !MainWindow.AltArrowBelongsToFocus(Key.Down, new System.Windows.Controls.Primitives.ToggleButton()),
            "any other button that kept the keyboard (Play, Snap, a toggle) gives both up: the clip moves");
        var combo = new System.Windows.Controls.ComboBox();
        expect(MainWindow.AltArrowBelongsToFocus(Key.Down, combo) && MainWindow.AltArrowBelongsToFocus(Key.Up, combo), "a combo box keeps both (Alt+Down opens it, Alt+Up closes it)");
        expect(!MainWindow.AltArrowBelongsToFocus(Key.Down, new System.Windows.Controls.ListBox()) && !MainWindow.AltArrowBelongsToFocus(Key.Up, null),
            "a list gives them up, and so does nothing focused");
    }

    // ----- chapters-output-end -----

    private static void ChaptersOutputEnd(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)));
        var main = vm.Document.Tracks.SelectMany(t => t.Clips).Single(c => c.SourcePath != Second);
        (main.AudioStreamIndex, main.AudioTailOutSeconds) = (0, 1);
        expect(vm.Document.TotalDuration == 9 && SequenceSpans.ProgramSeconds(vm.Document) == 8,
            "a Sequence whose last picture ends at 8 s, its sound running on 1 s past it (an L-cut): 9 s long, 8 s exported");

        (vm.ChaptersAtCuts, vm.ChaptersAtMarkers) = (true, true);
        vm.Markers.Add(new TimelineMarker(6, "Middle"));
        vm.Markers.Add(new TimelineMarker(8.5, "Tail"));
        var chapters = (List<(double Start, string Title)>)typeof(MainViewModel).GetMethod("BuildChapters", Hidden)!.Invoke(vm, [false])!;
        var listed = string.Join(", ", chapters.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Title} at {c.Start:0.###}")));
        expect(listed == "Part 1 at 0, Part 2 at 4, Middle at 6",
            $"chapters {listed}: none at the last picture's end (8 s, the export's last moment), none for the marker at 8.5 s (past the output)");

        typeof(MainViewModel).GetMethod("WriteChaptersFile", Hidden)!.Invoke(vm, [false]);
        var ends = System.IO.File.ReadAllLines(vm.ChaptersFilePath).Where(l => l.StartsWith("END=", StringComparison.Ordinal)).ToList();
        expect(ends.Count == 3 && ends[^1] == "END=8000", "the last chapter ends with the export, at 8 s (END=8000), not at the sound's 9 s");
    }
}
