using System.Reflection;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Timeline and project correctness: tilt keyframes moved with their clip by every timing transform, every keyframed channel in the one
/// list those transforms walk, a stretch of a clip deleted atomically (both cuts or neither), unsaved changes seen in projects without
/// media, a paused voiceover take holding its project, switched-off audio filters keeping their settings, the filters dialog's Undo over
/// Reset and the mix keys, and the Sequence's rate as the one frame stepping and timecode count in.
/// </summary>
public static class EngineCorrectnessTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-tilt-keys", "# A clip's tilt keyframes through a split and a trim, as its place and opacity keys go.", TiltKeys);
        yield return Case("motion-channels", "# Every keyframed channel of a look is in LayerState.MotionChannels, the list every timing transform walks.", Channels);
        yield return Case("timeline-delete-stretch", "# DeleteStretchOf: both cuts and the lift in one step, or nothing at all.", DeleteStretch);
        yield return Case("project-dirty-without-media", "# Unsaved changes in a project with no media file: its last clip deleted, a new project's first clip.", Dirty);
        yield return Case("voiceover-paused-holds-tab", "# A paused voiceover take belongs to its project: the tabs stay as they are.", PausedTake);
        yield return Case("audio-filters-kept-off", "# Audio filters switched off keep their settings through a save, an undo step and a cold tab.", FiltersKept);
        yield return Case("sequence-rate-consumers", "# Frame stepping and the timecode count in the Sequence's rate, not the last file's.", SequenceRate);
    }

    /// <summary>Cases that need the WPF application (a dialog made from XAML).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("audio-filters-dialog-undo", "# The audio filters dialog's Undo: Reset and the mix keys are steps, put back whole.", DialogUndo);
    }

    private static TimelineDocument OneText(FrameRate rate)
    {
        var doc = new TimelineDocument { FrameRate = rate };
        using var setup = doc.BeginTransaction("Set up");
        setup.PlaceClip(setup.InsertTrack(0, TimelineTrackKind.Video), Words(10));
        setup.Commit();
        return doc;
    }

    private static TimelineDocument OneClip(FrameRate rate, LayerState? look = null)
    {
        var doc = new TimelineDocument { FrameRate = rate };
        using var setup = doc.BeginTransaction("Set up");
        var track = setup.InsertTrack(0, TimelineTrackKind.Video);
        setup.PlaceClip(track, new TimelineClip { SourcePath = "D:/media/clip.mp4", End = 10, SourceDuration = 12, State = look ?? new LayerState() });
        setup.Commit();
        return doc;
    }

    private static void TiltKeys(Action<bool, string> expect)
    {
        var look = new LayerState
        {
            KeysTiltX = [new Keyframe(0, 0, EasingType.Linear), new Keyframe(10, 60, EasingType.Linear)],
            KeysTiltY = [new Keyframe(0, 10, EasingType.Linear), new Keyframe(10, 20, EasingType.Linear)],
        };
        var doc = OneClip(FrameRate.Fps30, look);
        TimelineClip second;
        using (var tx = doc.BeginTransaction("Split"))
        {
            second = tx.SplitClipAt(doc.Tracks[0].Clips[0], 5);
            tx.Commit();
        }

        var first = doc.Tracks[0].Clips[0];
        expect(second.State.KeysTiltX is [{ Time: 0, Value: var start }, ..] && Math.Abs(start - 30) < 1e-6 && second.State.KeysTiltX[^1].Time == 5,
            "a tilt-only animation split at its middle: the second half starts at 30 degrees, its keys on its own clock (0 to 5 s)");
        expect(first.State.KeysTiltX is [{ Time: 0 }, .., { Time: 5, Value: var end }] && Math.Abs(end - 30) < 1e-6,
            "the first half ends on the cut at 30 degrees");
        expect(second.State.KeysTiltY is [{ Value: var y }, ..] && Math.Abs(y - 15) < 1e-6, "Tilt Y is cut at its value there too (15 degrees)");

        using (var tx = doc.BeginTransaction("Trim"))
        {
            tx.TrimClipStart(second, 7);
            tx.Commit();
        }

        expect(second.State.KeysTiltX is [{ Time: var shifted }, ..] && Math.Abs(shifted - -2) < 1e-6,
            "a trim of the start moves the tilt keys with what the clip shows (2 s earlier on the clip's clock)");
    }

    private static void Channels(Action<bool, string> expect)
    {
        var keyed = typeof(LayerState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(List<Keyframe>)).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var listed = LayerState.MotionChannels.Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        expect(keyed.Count > 0 && keyed.SequenceEqual(listed),
            "the keyframed channels of a look and the motion-channel list are the same" + (keyed.SequenceEqual(listed) ? "" : $": look {string.Join(",", keyed)}, list {string.Join(",", listed)}"));
        var state = new LayerState();
        var each = LayerState.MotionChannels.All(c =>
        {
            c.Set(state, [new Keyframe(1, 2, EasingType.Linear)]);
            return c.Get(state) is [{ Time: 1, Value: 2 }];
        });
        expect(each, "each channel's reader and writer reach its own property");
    }

    private static void DeleteStretch(Action<bool, string> expect)
    {
        // 24 fps: 0.055 s lands on frame 1, shorter than the shortest clip (2 frames): the stretch is left exactly as it was.
        var at24 = OneClip(FrameRate.Fps24);
        var refused = false;
        using (var tx = at24.BeginTransaction("Dead air"))
        {
            try
            {
                tx.DeleteStretchOf(at24.Tracks[0].Clips[0], 0.055, 2.0);
            }
            catch (TimelineEditException)
            {
                refused = true;
            }

            tx.Commit();
        }

        expect(refused && at24.Tracks[0].Clips is [{ Start: 0, End: 10 }],
            "24 fps, 0.055 to 2 s: the first cut is refused, so neither is made (one clip, 0 to 10 s, no stray cut at 2 s)");

        // 30 fps: 0.055 s rounds to frame 2, the shortest clip: both cuts are made and the stretch goes.
        var at30 = OneClip(FrameRate.Fps30);
        double removed;
        using (var tx = at30.BeginTransaction("Dead air"))
        {
            removed = tx.DeleteStretchOf(at30.Tracks[0].Clips[0], 0.055, 2.0);
            tx.Commit();
        }

        expect(at30.Tracks[0].Clips is [{ Start: 0 } a, { Start: 2, End: 10 }] && Math.Abs(a.End - 2.0 / 30) < 1e-6 && Math.Abs(removed - (2 - 2.0 / 30)) < 1e-6,
            "30 fps, the same stretch: cut at frame 2 and at 2 s, the piece between lifted out (what follows stays where it was)");

        var inside = OneClip(FrameRate.Fps30);
        using (var tx = inside.BeginTransaction("Dead air"))
        {
            removed = tx.DeleteStretchOf(inside.Tracks[0].Clips[0], 3, 5);
            tx.Commit();
        }

        expect(inside.Tracks[0].Clips is [{ Start: 0, End: 3 }, { Start: 5, End: 10 }] && Math.Abs(removed - 2) < 1e-9,
            "a stretch well inside the clip: the two cuts and the lift, as a split, a split and a delete made it");
        var edge = OneClip(FrameRate.Fps30);
        using (var tx = edge.BeginTransaction("Dead air"))
        {
            tx.DeleteStretchOf(edge.Tracks[0].Clips[0], 0.02, 4);
            tx.Commit();
        }

        expect(edge.Tracks[0].Clips is [{ Start: 4, End: 10 }], "a stretch beginning within the shortest clip of the clip's start takes it from the start");
    }

    // A text clip: it needs no file, so no earlier suite's knowledge of a path (an unreadable file is taken off the timeline) touches it.
    private static TimelineClip Words(double end) =>
        new() { ContentKind = LayerKind.Text, End = end, State = new LayerState { Kind = LayerKind.Text, Name = "Title", TextContent = "Title" } };

    private static void Dirty(Action<bool, string> expect)
    {
        var document = new TimelineDocument { FrameRate = FrameRate.Fps30 };
        using (var setup = document.BeginTransaction("Set up"))
        {
            setup.PlaceClip(setup.InsertTrack(0, TimelineTrackKind.Video), Words(10));
            setup.Commit();
        }

        var vm = ViewModelFixture.Create(document);
        vm.MarkSaved();
        vm.NoteUserInput();
        expect(!vm.HasUnsavedChanges, "a project of one text clip and no media file, just saved and touched: no unsaved changes");
        var deleted = vm.Edit("delete", edit => edit.DeleteClip(vm.Document.Tracks.SelectMany(t => t.Clips).First()));
        var (empty, dirty, mayBe) = (vm.Document.Tracks.All(t => t.Clips.Count == 0), vm.HasUnsavedChanges, vm.MayHaveUnsavedChanges);
        expect(deleted && empty && dirty && mayBe,
            "its last clip deleted: unsaved changes (the deletion is saved on close, and recovery keeps its copy)"
            + (deleted && empty && dirty && mayBe ? "" : $": edit {deleted}, empty {empty}, unsaved {dirty}, may be {mayBe}"));

        var fresh = ViewModelFixture.Create(new TimelineDocument { FrameRate = FrameRate.Fps30 });
        fresh.MarkSaved();
        fresh.NoteUserInput();
        expect(!fresh.HasUnsavedChanges, "a new project set up and touched, nothing in it: no unsaved changes");
        fresh.Edit("add", edit => edit.PlaceClip(edit.InsertTrack(0, TimelineTrackKind.Video), Words(4)));
        expect(fresh.HasUnsavedChanges, "its first clip added: unsaved changes, before it was ever saved");
    }

    private static void PausedTake(Action<bool, string> expect)
    {
        var other = ViewModelFixture.Create(OneText(FrameRate.Fps30));
        var context = other.DetachForTab("Other");
        var vm = ViewModelFixture.Create(OneText(FrameRate.Fps30));
        vm.IsRecordingPaused = true;
        expect(vm.HoldsRecording && !vm.AttachTab(context), "a take paused (not recording) still holds the project: another tab is not attached");
        vm.IsRecordingPaused = false;
        expect(!vm.HoldsRecording, "no take: nothing held");
    }

    private static void FiltersKept(Action<bool, string> expect)
    {
        var layer = new Layer { AudioFilters = new TrackAudioFilters { Compressor = false, CompressorThreshold = -43 } };
        var state = layer.ToState();
        expect(state.AudioFilters is { Compressor: false, CompressorThreshold: -43 }, "a compressor switched off keeps its -43 dB threshold in the saved state");
        var back = Layer.FromState(state, 0, 0, 1920, 1080);
        expect(back.AudioFilters is { Compressor: false, CompressorThreshold: -43 }, "and has it again when the state is read back");
        expect(new Layer().ToState().AudioFilters is null, "filters all at their defaults are left out of the state");
    }

    private static void SequenceRate(Action<bool, string> expect)
    {
        var (rate, frames) = (TimeDisplay.FrameRate, TimeDisplay.UseFrames);
        try
        {
            var doc = OneText(FrameRate.Fps30);
            doc.ExplicitFrameRate = true;
            var sixty = new MediaInfo("matroska", 8, 0, new VideoStreamInfo(1920, 1080, "h264", 60, "16:9", "1:1", "bt709", "bt709", 0), [], []);
            var vm = ViewModelFixture.Create(doc, sixty);
            expect(vm.SourceFrameRate == 60 && vm.SequenceFps == 30, "an explicit 30p Sequence over a 60 fps file: the Sequence's rate is 30");
            vm.PositionMs = 0;
            vm.StepFrames(1);
            expect(Math.Abs(vm.PositionMs - 1000.0 / 30) < 1e-6, "a frame step moves 1/30 s (33.333 ms), the Sequence's frame");
            vm.StepFrames(2);
            expect(Math.Abs(vm.PositionMs - 3000.0 / 30) < 1e-6, "and lands on its frames (frame 3 after two more)");
            expect(TimeDisplay.FrameRate == 30, "the timecode readouts count in 30 fps");
        }
        finally
        {
            (TimeDisplay.FrameRate, TimeDisplay.UseFrames) = (rate, frames);
        }
    }

    private static void DialogUndo(Action<bool, string> expect)
    {
        // The dialog's static resources live in App.xaml, which the goldens never load: stand-ins while it is made.
        var resources = EnsureApplication().Resources;
        var standIns = new List<string>();
        foreach (var (key, value) in new (string, object)[] { ("ValueBox", new System.Windows.Style(typeof(System.Windows.Controls.TextBox))), ("BoolToVisibility", new System.Windows.Controls.BooleanToVisibilityConverter()) })
        {
            if (!resources.Contains(key))
            {
                resources[key] = value;
                standIns.Add(key);
            }
        }

        TrackAudioFiltersDialog dialog;
        try
        {
            dialog = new TrackAudioFiltersDialog("Test", new TrackAudioFilters { Compressor = true, CompressorThreshold = -43 }, keyTime: 1.0);
        }
        finally
        {
            foreach (var key in standIns)
                resources.Remove(key);
        }

        try
        {
            dialog.History.BeginPress();
            dialog.ResetFilters();
            dialog.History.EndPress();
            expect(!dialog.Filters.Compressor, "Reset gives a fresh set (the compressor off)");
            dialog.History.Undo();
            expect(dialog.Filters is { Compressor: true, CompressorThreshold: -43 } && ReferenceEquals(dialog.DataContext, dialog.Filters),
                "Undo puts the whole set back (on, -43 dB), and the dialog shows that set");

            dialog.History.BeginPress();
            dialog.AddMixKey();
            dialog.History.EndPress();
            expect(dialog.Filters.StudioVoiceMixKeys is [{ Time: 1 }], "Add Mix Key puts a key at the playhead");
            dialog.History.Undo();
            expect(dialog.Filters.StudioVoiceMixKeys is null or { Count: 0 }, "Undo takes the key off again");
            dialog.History.Redo();
            expect(dialog.Filters.StudioVoiceMixKeys is [{ Time: 1 }], "Redo puts it back");
        }
        finally
        {
            dialog.Close();
        }
    }
}
