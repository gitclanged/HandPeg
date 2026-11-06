using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.2A on a real view model: a track's own Game Preset (a Track Preset Node) goes on what comes onto it and swaps a moved clip's preset
/// in one step of Undo; the clip clipboard copies, pastes, duplicates and pastes attributes with whole families; a fully portable save
/// writes a folder that holds its media and opens again wherever the folder is carried. Every line says what it proved.
/// </summary>
public static class TimelineErgonomicsTests
{
    private const string Second = "D:/media/second.mkv", Third = "D:/media/third.mkv";

    private static readonly List<CutoutPiece> Two =
    [
        new("Hero", "0.02,0.8 0.2,0.8 0.2,0.95 0.02,0.95", new System.Windows.Rect(0.02, 0.8, 0.18, 0.15), 0.05, 0.75, 0.5),
        new("Map", "0.8,0.02 0.98,0.02 0.98,0.2 0.8,0.2", new System.Windows.Rect(0.8, 0.02, 0.18, 0.18), 0.75, 0.05, 0.4),
    ];

    private static readonly List<CutoutPiece> One = [new("Killfeed", "0.7,0.1 0.95,0.1 0.95,0.25 0.7,0.25", new System.Windows.Rect(0.7, 0.1, 0.25, 0.15), 0.7, 0.1, 0.4)];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("track-preset-nodes", "# A Track Preset Node: a track's own Game Preset, on its clips and on what comes onto it; a move between two swaps it, one step of Undo.", PresetNodes);
        yield return Case("clip-clipboard", "# Ctrl+C / Ctrl+V / Ctrl+D / Ctrl+Alt+V: whole families pasted at the playhead, duplicated after themselves, attributes pasted, each one step of Undo.", Clipboard);
        yield return Case("portable-project-save", "# A fully portable save: Projects\\Name\\Name.hproj with its media in media\\, named by relative paths, opened again wherever it is carried.", Portable);
        yield return Case("track-preset-frame-shape", "# A Track Preset turns the frame to its shape only where the Sequence is its to shape: alone, or at the default 16:9 with no other preset (5.2B, Q2).", PresetFrameShape);
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

    private static List<TimelineClip> Cutouts(MainViewModel vm, TimelineClip parent) =>
        vm.Document.ChildrenOf(parent).Where(c => c.Track.Kind == TimelineTrackKind.Video).Select(c => c.Clip).ToList();

    private static void PresetNodes(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)), DogfoodTests.Lane(DogfoodTests.FileClip(Third, 10, 14)));
        expect(vm.SaveCutoutPreset("Node A", Two, 1.7) is null && vm.SaveCutoutPreset("Node B", One, 1.7) is null, "two Game Presets saved: two Cutouts, and one");
        var (trackA, trackB) = (vm.Document.Tracks[1], vm.Document.Tracks[2]);
        var (clipId, otherId) = (trackA.Clips[0].Id, trackB.Clips[0].Id);
        var rowA = vm.ViewOf(trackA.Clips[0])!;
        var rowB = vm.ViewOf(trackB.Clips[0])!;
        expect(vm.CanHaveTrackPreset(rowA) && vm.TrackPresetOf(rowA) is null, "a video track can carry a preset, and has none at first");

        expect(vm.SetTrackPreset(rowA, "Node A") && trackA.PresetNodeName == "Node A", "set on track A");
        expect(Cutouts(vm, vm.Document.FindClip(clipId)!).Select(c => c.State.Name).Order().SequenceEqual(["Hero", "Map"]), "the clip on it took that preset's two Cutouts");
        var json = JsonSerializer.Serialize(trackA);
        expect(json.Contains("\"PresetNodeName\":\"Node A\"", StringComparison.Ordinal) && !JsonSerializer.Serialize(trackB).Contains("PresetNodeName", StringComparison.Ordinal),
            "the node is kept with its track in the project, and left out of a track without one");

        vm.Undo();
        expect(vm.Document.Tracks[1].PresetNodeName is null && Cutouts(vm, vm.Document.FindClip(clipId)!).Count == 0, "Undo takes the node and its Cutouts away in one step");
        vm.Redo();
        expect(vm.Document.Tracks[1].PresetNodeName == "Node A" && Cutouts(vm, vm.Document.FindClip(clipId)!).Count == 2, "Redo puts both back");

        (trackA, trackB) = (vm.Document.Tracks.First(t => t.Clips.Any(c => c.Id == clipId)), vm.Document.Tracks.First(t => t.Clips.Any(c => c.Id == otherId)));
        rowB = vm.ViewOf(trackB.Clips[0])!;
        expect(vm.SetTrackPreset(rowB, "Node B") && Cutouts(vm, vm.Document.FindClip(otherId)!).Select(c => c.State.Name).SequenceEqual(["Killfeed"]), "set on track B: its clip took the one Cutout");

        // A clip moved from A's track to B's: its preset and Cutouts swapped for B's, in the move's own step.
        var moving = vm.ViewOf(vm.Document.FindClip(clipId)!)!;
        expect(vm.MoveClipToTrack(moving, rowB, 20), "a clip moved from track A to track B");
        var moved = vm.Document.FindClip(clipId)!;
        expect(vm.Document.TrackOf(moved)!.PresetNodeName == "Node B", "it lies on B's track");
        expect(Cutouts(vm, moved).Select(c => c.State.Name).SequenceEqual(["Killfeed"]), "its Cutouts are B's preset's now, A's gone");
        expect(Cutouts(vm, moved).All(c => c.Start == moved.Start && c.End == moved.End), "over its own span");
        vm.Undo();
        var back = vm.Document.FindClip(clipId)!;
        expect(vm.Document.TrackOf(back)!.PresetNodeName == "Node A" && Cutouts(vm, back).Select(c => c.State.Name).Order().SequenceEqual(["Hero", "Map"]),
            "one Undo puts it back on A's track with A's Cutouts");

        // Cleared, the node leaves its clips as they are.
        trackB = vm.Document.Tracks.First(t => t.Clips.Any(c => c.Id == otherId));
        expect(vm.SetTrackPreset(vm.ViewOf(trackB.Clips[0])!, null) && trackB.PresetNodeName is null, "Clear Track Preset takes the node away");
        expect(Cutouts(vm, vm.Document.FindClip(otherId)!).Count == 1, "and its clip keeps the Cutout it has");
        var lane = vm.Document.TrackOf(Cutouts(vm, vm.Document.FindClip(otherId)!)[0])!;
        expect(!vm.CanHaveTrackPreset(vm.ViewOf(lane.Clips[0])), "a Cutout's lane carries no preset of its own");
    }

    private static void Clipboard(Action<bool, string> expect)
    {
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)));
        expect(vm.SaveCutoutPreset("Clipboard Preset", Two, 1.7) is null, "a Game Preset saved");
        var track = vm.Document.Tracks[1];
        var clip = track.Clips[0];
        var view = vm.ViewOf(clip)!;
        expect(vm.ApplyGamePreset([view], "Clipboard Preset") && Cutouts(vm, clip).Count == 2, "a clip with two Cutouts");
        var clipsBefore = vm.Document.Tracks.Sum(t => t.Clips.Count);

        expect(vm.CopyClips([view]) == 1 && vm.HasCopiedClips, "Ctrl+C copies the clip");
        expect(vm.PasteClips(10), "Ctrl+V pastes it at the playhead");
        var pasted = vm.Document.Tracks.SelectMany(t => t.Clips).Single(c => c.ParentClipId is null && c.Start == 10 && c.SourcePath == Second);
        expect(pasted.Id != clip.Id && pasted.End == 14 && ReferenceEquals(vm.Document.TrackOf(pasted), vm.Document.Tracks.First(t => t.Clips.Any(c => c.Id == clip.Id))),
            "a new clip at 10 s, as long, on the track it came from");
        var pastedCutouts = Cutouts(vm, pasted);
        expect(pastedCutouts.Count == 2 && pastedCutouts.All(c => c.Start == 10 && c.End == 14), "with its own two Cutouts over its span");
        expect(pastedCutouts.All(c => vm.Document.TrackOf(c)!.ParentTrackId == vm.Document.TrackOf(pasted)!.Id), "on lanes under its track");
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clipsBefore + 3, "three clips more, nothing else");
        vm.Undo();
        expect(vm.Document.Tracks.Sum(t => t.Clips.Count) == clipsBefore, "one Undo takes the paste away");

        var again = vm.ViewOf(vm.Document.FindClip(clip.Id)!)!;
        expect(vm.DuplicateClips([again]), "Ctrl+D duplicates it");
        var duplicate = vm.Document.Tracks.SelectMany(t => t.Clips).SingleOrDefault(c => c.ParentClipId is null && c.Start == 4 && c.SourcePath == Second);
        expect(duplicate is not null && Cutouts(vm, duplicate).Count == 2, "right after itself, 4 to 8 s, with its Cutouts");
        vm.Undo();

        // Two clips of two tracks: pasted as far apart as they were, each on its own track.
        var main = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        var second = vm.ViewOf(vm.Document.FindClip(clip.Id)!)!;
        expect(vm.CopyClips([main, second]) == 2, "two clips of two tracks copied");
        expect(vm.PasteClips(30), "and pasted at 30 s");
        var mainPaste = vm.Document.Tracks.SelectMany(t => t.Clips).Count(c => c.Start == 30 && c.ParentClipId is null);
        expect(mainPaste == 2, "both start at 30 s (they started together)");

        // Paste Attributes: the copied clip's transform, 3D tilt among it, onto another clip; one Undo.
        vm.Undo();
        var source = vm.ViewOf(vm.Document.FindClip(clip.Id)!)!;
        (source.PositionX, source.TiltY, source.Rotation) = (0.2, 30, 10);
        expect(vm.CopyClips([source]) == 1, "a turned and tilted clip copied");
        var target = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        expect(vm.PasteAttributes([target], MainViewModel.ClipAttributes.Transform) == 1, "its transform pasted onto the main video");
        target = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        expect(Math.Abs(target.TiltY - 30) < 1e-9 && Math.Abs(target.Rotation - 10) < 1e-9 && Math.Abs(target.PositionX - 0.2) < 1e-9, "its place, turn and tilt are the copied clip's");
        expect(Cutouts(vm, vm.Document.Tracks[0].Clips[0]).Count == 0, "and nothing else came with them");
        vm.Undo();
        target = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        expect(target.TiltY == 0 && target.Rotation == 0, "one Undo takes them back");
    }

    private static void Portable(Action<bool, string> expect)
    {
        var media = Path.Combine(Path.GetDirectoryName(AppPaths.Projects)!, "portable-source");
        Directory.CreateDirectory(media);
        var file = Path.Combine(media, "match.mkv");
        File.WriteAllBytes(file, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(file.Replace('\\', '/'), 0, 4)));

        var settings = AppSettings.Current;
        var was = settings.FullyPortableProjectSave;
        try
        {
            // A history to carry (5.2B, the user's Q1): a step to undo and one to redo, each a timeline that names the file where it is.
            vm.Checkpoint("touch the match");
            vm.Checkpoint("touch the match again");
            vm.Undo();

            settings.FullyPortableProjectSave = true;
            var said = vm.SaveProject("Carry Me");
            var path = Path.Combine(AppPaths.Projects, "Carry Me", "Carry Me" + ProjectStore.Extension);
            expect(said.StartsWith("Project saved", StringComparison.Ordinal) && File.Exists(path), "Save writes Projects\\Carry Me\\Carry Me.hproj");
            expect(vm.CurrentProjectPath == path, "and that is where the next Ctrl+S goes");
            var copy = Path.Combine(AppPaths.Projects, "Carry Me", "media", "match.mkv");
            expect(File.Exists(copy) && new FileInfo(copy).Length == 4096, "its media is in media\\ beside it (hard-linked on the same drive)");
            var text = File.ReadAllText(path);
            expect(text.Contains("\"portableMedia\":true", StringComparison.Ordinal), "the project says it is portable");
            expect(text.Contains("media\\\\match.mkv", StringComparison.Ordinal) && !text.Contains("portable-source", StringComparison.Ordinal), "and names its media by relative paths only");
            var history = HistoryIn(text);
            expect(history.Count == 2 && history.All(step => MatchPaths(step) is { Count: > 0 } paths && paths.All(p => p == Path.Combine("media", "match.mkv"))),
                "its undo and redo steps name the media by the same relative paths (unpacked, rewritten, packed again)");
            expect(history.All(step => !JsonSerializer.Serialize(step).Contains("portable-source", StringComparison.Ordinal)), "and nothing in them names the folder the media came from");

            // Carried elsewhere (another drive, another PC): opened there, it reads its own media.
            var carried = Path.Combine(Path.GetDirectoryName(AppPaths.Projects)!, "usb", "Carry Me");
            Directory.CreateDirectory(Path.Combine(carried, "media"));
            File.Copy(path, Path.Combine(carried, "Carry Me" + ProjectStore.Extension), overwrite: true);
            File.Copy(copy, Path.Combine(carried, "media", "match.mkv"), overwrite: true);
            var opened = ProjectStore.Load(Path.Combine(carried, "Carry Me" + ProjectStore.Extension), unpackMasks: false);
            var reads = opened.Timeline!.Tracks.SelectMany(t => t.Clips).Select(c => c.SourcePath).OfType<string>().Where(p => p.EndsWith("match.mkv", StringComparison.OrdinalIgnoreCase)).ToList();
            expect(reads.Count > 0 && reads.All(p => string.Equals(p, Path.Combine(carried, "media", "match.mkv"), StringComparison.OrdinalIgnoreCase)),
                "carried to another folder, it opens reading the media beside it");

            // Opened there as the window opens it (its history read): every step Undo and Redo put back reads the media beside it too.
            var reopened = ProjectStore.Load(Path.Combine(carried, "Carry Me" + ProjectStore.Extension), unpackMasks: true);
            var steps = (reopened.UndoHistory ?? []).Concat(reopened.RedoHistory ?? []).Select(s => UndoSteps.Unpack(s.State)).ToList();
            expect(reopened.UndoHistory is { Count: 1 } && reopened.RedoHistory is { Count: 1 }, "it opens with its step to undo and its step to redo");
            expect(steps.All(step => step is not null && MatchPaths(step) is { Count: > 0 } paths
                                     && paths.All(p => string.Equals(p, Path.Combine(carried, "media", "match.mkv"), StringComparison.OrdinalIgnoreCase))),
                "and each step's clips read the media beside the carried project, as the project's own do");
            expect(ProjectStore.PathOfName("Carry Me") == path, "the Swap Project list finds it in its folder");

            // Saved again: the same file in media\ is kept, not copied twice.
            vm.SaveProjectTo(vm.CurrentProjectPath);
            expect(Directory.GetFiles(Path.Combine(AppPaths.Projects, "Carry Me", "media")).Length == 1, "saved again, its media folder still holds the one file");

            settings.FullyPortableProjectSave = false;
            vm.SaveProject("Flat One");
            expect(File.Exists(Path.Combine(AppPaths.Projects, "Flat One" + ProjectStore.Extension)) && !Directory.Exists(Path.Combine(AppPaths.Projects, "Flat One")),
                "with the setting off, a save is one file in Projects, as before");
        }
        finally
        {
            settings.FullyPortableProjectSave = was;
        }
    }

    // The timelines of a project file's undo and redo steps, as written (its "state" Base64, unpacked as Undo reads it), undo's first.
    private static List<TimelineSnapshot> HistoryIn(string projectText)
    {
        var found = new List<TimelineSnapshot>();
        if (JsonNode.Parse(projectText) is not JsonObject project)
            return found;

        foreach (var name in new[] { "undoHistory", "redoHistory" })
        {
            if (project[name] is not JsonArray steps)
                continue;

            foreach (var step in steps)
            {
                if (step?["state"] is JsonValue state && state.TryGetValue<string>(out var packed) && UndoSteps.Unpack(Convert.FromBase64String(packed)) is { } timeline)
                    found.Add(timeline);
            }
        }

        return found;
    }

    // What a step's clips name of match.mkv (their files and their looks' pictures).
    private static List<string> MatchPaths(TimelineSnapshot step) =>
        step.Document.Tracks.SelectMany(t => t.Clips).SelectMany(c => new[] { c.SourcePath, c.State?.ImagePath })
            .OfType<string>().Where(p => p.EndsWith("match.mkv", StringComparison.OrdinalIgnoreCase)).ToList();

    private static void PresetFrameShape(Action<bool, string> expect)
    {
        // Alone on the Sequence (no other track holds a clip): the preset's shape, even from a frame the user made 4:3.
        var alone = DogfoodTests.Open();
        expect(alone.SaveCutoutPreset("Shape A", Two, 1.7) is null && alone.SaveCutoutPreset("Shape B", One, 1.7) is null, "two Game Presets saved, both of the upright shape");
        alone.KeepAspectRatio = false;
        (alone.OutputWidth, alone.OutputHeight) = ("1440", "1080");
        expect(!alone.UseVerticalResolution && alone.FrameWidth == 1440 && alone.FrameHeight == 1080, "a Sequence of one track, its frame made 4:3");
        expect(alone.SetTrackPreset(alone.ViewOf(alone.Document.Tracks[0].Clips[0])!, "Shape A") && alone.UseVerticalResolution,
            "a Track Preset on the only track that holds clips turns the frame to the preset's shape");

        // Clips on other tracks: at the default 16:9 with no other preset, the first Track Preset turns the frame.
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)), DogfoodTests.Lane(DogfoodTests.FileClip(Third, 10, 14)));
        var (clipId, otherId) = (vm.Document.Tracks[1].Clips[0].Id, vm.Document.Tracks[2].Clips[0].Id);
        expect(!vm.UseVerticalResolution && vm.FrameWidth == 1920 && vm.FrameHeight == 1080, "three tracks of clips, the frame the default 16:9");
        expect(vm.SetTrackPreset(vm.ViewOf(vm.Document.FindClip(clipId)!)!, "Shape A") && vm.UseVerticalResolution, "the first Track Preset turns the frame to its shape");

        // Another track already has a different preset: the frame stays as it is (turned back by hand here), the rest of the preset goes on.
        vm.UseVerticalResolution = false;
        expect(vm.SetTrackPreset(vm.ViewOf(vm.Document.FindClip(otherId)!)!, "Shape B") && !vm.UseVerticalResolution,
            "a different Track Preset beside it leaves the frame's shape as it is");
        var other = vm.Document.FindClip(otherId)!;
        expect(vm.Document.TrackOf(other)!.PresetNodeName == "Shape B" && Cutouts(vm, other).Select(c => c.State.Name).SequenceEqual(["Killfeed"]),
            "while its node and its clip's Cutout still go on");

        // Clips on other tracks and a frame the user made 4:3 (not the default): the preset leaves its shape.
        var four = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Second, 0, 4)));
        four.KeepAspectRatio = false;
        (four.OutputWidth, four.OutputHeight) = ("1440", "1080");
        expect(!four.UseVerticalResolution && four.FrameWidth == 1440 && four.FrameHeight == 1080, "two tracks of clips, the frame made 4:3");
        expect(four.SetTrackPreset(four.ViewOf(four.Document.Tracks[1].Clips[0])!, "Shape A") && !four.UseVerticalResolution,
            "beside other clips on a frame made 4:3, a Track Preset leaves the frame's shape");

        // The only picture track beside a music track (sound only) on a frame made 4:3: the music's clips are another track's clips, so the
        // preset leaves the frame's shape too (the user's Q2, as worded: any other track that holds clips).
        var music = new TimelineTrackModel { Kind = TimelineTrackKind.Audio };
        music.Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.Audio, SourcePath = "D:/media/music.wav", AudioStreamIndex = 0, Start = 0, End = 8, SourceDuration = 120,
            State = new LayerState { Kind = LayerKind.Audio, Name = "Music", ImagePath = "D:/media/music.wav", MediaDuration = 120, HasAudio = true },
        });
        var scored = DogfoodTests.Open(music);
        scored.KeepAspectRatio = false;
        (scored.OutputWidth, scored.OutputHeight) = ("1440", "1080");
        expect(!scored.UseVerticalResolution && scored.FrameWidth == 1440 && scored.FrameHeight == 1080
               && scored.Document.Tracks.Count(t => t.Kind == TimelineTrackKind.Video && t.Clips.Count > 0) == 1,
            "one picture track and a music track, the frame made 4:3");
        expect(scored.SetTrackPreset(scored.ViewOf(scored.Document.Tracks[0].Clips[0])!, "Shape A") && !scored.UseVerticalResolution,
            "beside a music track's clips on a frame made 4:3, a Track Preset on the only picture track leaves the frame's shape");
    }
}
