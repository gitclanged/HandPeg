using System.IO;
using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Invalidation domains and gesture scopes: the views list replaced in one Reset per edit, look-only changes kept off the Timeline's rows
/// and the Projected Output, one undo step per editing gesture under the single run rule, and one key tolerance for every editor.
/// </summary>
public static class InvalidationTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("sync-views-reset", "# An edit tells the views list's listeners once (one Reset), whatever it adds, removes or moves; the order is the document's.", SyncViewsReset);
        yield return Case("preview-change-look", "# An effect value written in a look batch: one TimelineChanged and one command, look-only (no row rebuild, no Projected Output); a card added is not.", LookOnly);
        yield return Case("gesture-undo-rule", "# One undo step per gesture; lone writes under one run rule; the view model reads no WPF input and keeps no thread-static echo.", GestureUndo);
        yield return Case("fingerprint-live-equals-copy", "# Dirty tracking serializes the live project (no deep copy): byte for byte the deep copy's text, and a tab put behind compares equal.", FingerprintLive);
        yield return Case("save-waits-off-ui", "# Autosave never waits for its thumbnail on the window's thread; an edit flushes the looks once; a mask's alpha is known from its header first.", SaveWaits);
        yield return Case("key-edit-tolerance", "# One key tolerance (inclusive, Layer.KeyTolerance) and one key-time rounding for every editor; one value quantizer for both panes.", KeyTolerance);
    }

    private static TimelineDocument Text(int tracks, int clipsPerTrack)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        using var tx = doc.BeginTransaction("Set up");
        for (var t = 0; t < tracks; t++)
        {
            var track = tx.InsertTrack(t, TimelineTrackKind.Video);
            for (var i = 0; i < clipsPerTrack; i++)
                tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Text, Start = i * 2, End = i * 2 + 1.5, State = new LayerState { Name = $"T{t}C{i}" } });
        }

        tx.Commit();
        return doc;
    }

    // ----- sync-views-reset -----

    private static void SyncViewsReset(Action<bool, string> expect)
    {
        var vm = ViewModelFixture.Create(Text(3, 100));
        var events = 0;
        vm.Layers.CollectionChanged += (_, e) => events += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 100;
        List<Guid> Order() => [.. vm.Layers.Select(l => l.ClipId)];
        List<Guid> Fresh()
        {
            var copy = ViewModelFixture.Create(vm.Document.Clone());
            return [.. copy.Layers.Select(l => l.ClipId)];
        }

        var picture = vm.Document.Tracks.First(t => t.Kind == TimelineTrackKind.Video && t.Clips.Count > 0);
        var clip = picture.Clips[3];
        vm.Edit("trim", edit => edit.TrimClipEnd(clip, clip.End - 0.5));
        expect(events <= 1, $"a trim: at most one notification ({events})");

        events = 0;
        var first = picture.Clips[0];
        vm.Edit("delete", edit => edit.DeleteClip(first));
        expect(events == 1, $"the first clip of the bottom picture track deleted: one Reset (every later view moves up; was one event per view), {events}");
        expect(Order().SequenceEqual(Fresh()), "the list's order is the order a fresh sync of the same document gives");

        events = 0;
        vm.Edit("200 splits", edit =>
        {
            var cuts = 0;
            foreach (var track in vm.Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Video).ToList())
            {
                foreach (var each in track.Clips.ToList())
                {
                    if (cuts++ >= 200)
                        break;
                    edit.SplitClipAt(each, each.Start + 0.75);
                }
            }
        });
        expect(events == 1, $"one edit splitting 200 clips across the tracks (as Remove Dead Air does): one Reset, {events}");
        expect(Order().SequenceEqual(Fresh()) && vm.Layers.Count == vm.Document.Tracks.Sum(t => Math.Max(t.Clips.Count, 1)),
            "and every view in the document's stacking order, one per clip");
        var source = SourceProbe.MemberOfType("MainViewModel", "private void SyncViews(");
        expect(source.Contains("Layers.ReplaceAll(wanted, syncing: true);", StringComparison.Ordinal) && !source.Contains("Layers.Insert(", StringComparison.Ordinal)
               && !source.Contains("Layers.Move(", StringComparison.Ordinal), "SyncViews replaces the list whole (no per-view Insert or Move)");
    }

    // ----- preview-change-look -----

    private static void LookOnly(Action<bool, string> expect)
    {
        var vm = ViewModelFixture.Create(Text(1, 3));
        var view = vm.Layers.First(l => l.ClipId != Guid.Empty);
        vm.KeyLayer = view;
        var card = vm.AddEffect(view, EffectRegistry.Find(EffectRegistry.ColorStudio)!)!;
        var (timeline, previews, rows) = (new List<PreviewChange>(), new List<PreviewChange>(), 0);
        vm.TimelineChanged += () => timeline.Add(vm.LastChange);
        vm.PreviewStateInvalidated += () => previews.Add(vm.LastChange);
        vm.OutputInfoRows.CollectionChanged += (_, _) => rows++;

        for (var flush = 0; flush < 30; flush++)
        {
            using (vm.BatchLookWrites())
            using (vm.OwnLookScope())
            {
                card.Set(ColorGradeKeys.LiftR, 0.01 * flush);
                card.Set(ColorGradeKeys.LiftG, -0.01 * flush);
                card.Set(ColorGradeKeys.LiftB, 0.005 * flush);
            }
        }

        expect(timeline.Count == 30 && previews.Count == 30, $"30 flushes of 3 values: 30 TimelineChanged and 30 invalidations, one per flush ({timeline.Count}, {previews.Count}; was 6 each per flush)");
        expect(timeline.All(c => c == PreviewChange.Look) && previews.All(c => c == PreviewChange.Look), "each announced look-only (PreviewChange.Look)");
        expect(rows == 0, $"the Projected Output was not rebuilt ({rows} changes)");
        expect(vm.LastChange == PreviewChange.Output, "outside an announcement the change reads Output (a listener never sees a stale Look)");

        timeline.Clear();
        previews.Clear();
        card.Enabled = false;
        expect(timeline.Count > 0 && timeline.All(c => c != PreviewChange.Look), "a card switched off is not look-only: its lane and badge change (Rows)");
        timeline.Clear();
        vm.AddEffect(view, EffectRegistry.Find(EffectRegistry.MotionBlur)!);
        expect(timeline.Count > 0 && timeline.All(c => c != PreviewChange.Look), "a card added is not look-only either");

        var window = SourceProbe.MemberOfType("MainWindow", "private void ScheduleClipRedrawUnlessLook(");
        expect(SourceProbe.InOrder(window, "if (_viewModel.LastChange == PreviewChange.Look)", "return;", "ScheduleClipRedraw();")
               && SourceProbe.Src("MainWindow.xaml.cs").Contains("_viewModel.PreviewStateInvalidated += ScheduleClipRedrawUnlessLook;", StringComparison.Ordinal)
               && SourceProbe.Src("MainWindow.xaml.cs").Contains("_viewModel.TimelineChanged += ScheduleClipRedrawUnlessLook;", StringComparison.Ordinal),
            "the Timeline's rows ignore a look-only change on both their subscriptions");
    }

    // ----- gesture-undo-rule -----

    private static long Steps(MainViewModel vm) => vm.UndoStepsTaken;

    private static void GestureUndo(Action<bool, string> expect)
    {
        var vm = ViewModelFixture.Create(Text(1, 3));
        var view = vm.Layers.First(l => l.ClipId != Guid.Empty);
        var card = vm.AddEffect(view, EffectRegistry.Find(EffectRegistry.ColorStudio)!)!;
        vm.NoteUserInput(); // a touch ends the document's settling (no step is taken while a restore settles)
        vm.ForgetUndoRuns();

        // A gesture: ten writes of two values, one step.
        var writer = new GestureWriter<string>(() => vm);
        var before = Steps(vm);
        writer.Begin();
        for (var i = 0; i < 10; i++)
        {
            var value = i * 0.01;
            writer.Queue("r", () => { vm.StepForLook(card, "r", "change Color Grade", writer.Gesture); card.Set(ColorGradeKeys.LiftR, value); });
            writer.Queue("g", () => { vm.StepForLook(card, "g", "change Color Grade", writer.Gesture); card.Set(ColorGradeKeys.LiftG, value); });
            writer.Flush();
        }

        writer.End();
        expect(Steps(vm) == before + 1 && Math.Abs(card.Get(ColorGradeKeys.LiftR) - 0.09) < 1e-9, $"a gesture of ten flushes: one undo step ({Steps(vm) - before}), its last value written");

        // A second gesture right after: a step of its own.
        writer.Begin();
        writer.Queue("r", () => { vm.StepForLook(card, "r", "change Color Grade", writer.Gesture); card.Set(ColorGradeKeys.LiftR, 0.5); });
        writer.End();
        expect(Steps(vm) == before + 2, "a second gesture at once: one more step");

        // Lone writes: a run of one field is one step; another field after the grace is another; startRun always steps.
        vm.ForgetUndoRuns();
        before = Steps(vm);
        for (var i = 0; i < 5; i++)
            vm.StepForLook(card, "exposure", "change Exposure");
        expect(Steps(vm) == before + 1, "five lone writes of one field within the window: one step");
        vm.ForgetUndoRuns();
        vm.StepForLook(card, "contrast", "change Contrast");
        expect(Steps(vm) == before + 2, "another field (the grace over): its own step");
        vm.StepForLook(card, "contrast", "switch", startRun: true);
        expect(Steps(vm) == before + 3, "a switch or a key (startRun): always its own step");

        // Source rules: no WPF input in the view model, no thread-static echo on the model, one pacing constant, one merge window.
        var viewModels = Directory.GetFiles(Path.Combine(SourceProbe.Root, "src", "ViewModels"), "*.cs").Select(f => HotPathTests.WithoutComments(File.ReadAllText(f))).ToList();
        expect(viewModels.All(t => !t.Contains("System.Windows.Input", StringComparison.Ordinal) && !t.Contains("Mouse.", StringComparison.Ordinal)
                                   && !t.Contains("Keyboard.", StringComparison.Ordinal) && !t.Contains("TextBoxBase", StringComparison.Ordinal)),
            "the view model reads no mouse or keyboard state (the window injects IsPointerHeld and IsTypingInTextBox)");
        var layer = HotPathTests.WithoutComments(SourceProbe.Src("Models", "Layer.Effects.cs"));
        expect(!layer.Contains("ThreadStatic", StringComparison.Ordinal) && !layer.Contains("Echo(", StringComparison.Ordinal),
            "the model keeps no thread-static echo (the view model's OwnLookScope replaces Layer.Echo)");
        var views = Directory.GetFiles(Path.Combine(SourceProbe.Root, "src", "Views"), "*.cs").Where(f => Path.GetFileName(f).StartsWith("EffectsPane", StringComparison.Ordinal)
            || Path.GetFileName(f).StartsWith("ColorStudioPane", StringComparison.Ordinal)).Select(f => HotPathTests.WithoutComments(File.ReadAllText(f))).ToList();
        expect(views.Count >= 6 && views.All(t => !t.Contains("TotalMilliseconds > 1000", StringComparison.Ordinal) && !t.Contains("WritesPerSecond", StringComparison.Ordinal)
                              && !t.Contains("Mouse.Captured", StringComparison.Ordinal)),
            "no pane keeps its own merge window, write rate or pointer-capture test (GestureWriter, CaptureThrottle, Pacing)");
    }

    // ----- fingerprint-live-equals-copy -----

    private static void FingerprintLive(Action<bool, string> expect)
    {
        // A rich project: clip looks with effects, keyframes and a mask path; a track look; Sequence Output effects with keys; markers; a
        // clip in the Recycle Bin.
        var doc = Text(2, 4);
        var first = doc.Tracks[0];
        first.TrackLook = new LayerState { Opacity = 60, Name = "Track look" };
        foreach (var clip in doc.Tracks.SelectMany(t => t.Clips).Take(3))
        {
            clip.State.KeysOpacity = [new(0.5, 100), new(1.2, 40)];
            clip.State.MaskPath = "D:/media/mask.png";
            clip.State.Effects = [new EffectState { Type = EffectRegistry.MotionBlur, Values = new() { ["amount"] = System.Text.Json.JsonSerializer.SerializeToElement(75) }, Keys = new() { ["amount"] = [new(0.2, 10), new(0.9, 90)] } }];
        }

        doc.SequenceEffects = [new EffectState { Type = EffectRegistry.ColorStudio, Values = new() { [ColorGradeKeys.LiftR] = System.Text.Json.JsonSerializer.SerializeToElement(0.1) }, Keys = new() { [ColorGradeKeys.LiftG] = [new(1, 0.2)] } }];
        doc.Markers = [new TimelineMarker(1.5, "Kill")];
        var vm = ViewModelFixture.Create(doc);
        vm.NoteUserInput();
        var doomed = vm.Document.Tracks.Last(t => t.Clips.Count > 0).Clips[^1];
        vm.DeleteClips([vm.ViewOf(doomed)!], ripple: false);
        expect(vm.RecycleBin.Count == 1, "a clip deleted into the Recycle Bin");

        var (live, copy) = (vm.Fingerprint(), vm.FingerprintOfCopy());
        expect(live.Length > 1000 && live == copy, $"the live fingerprint is the deep copy's text, byte for byte{(live == copy ? "" : $" (first difference at {FirstDifference(live, copy)} of {live.Length})")}");
        var frozen = vm.CaptureStateForTab("Behind");
        expect(!MainViewModel.FrozenStateDiffers(frozen, live), "a tab put behind (its frozen copy) compares equal to the live fingerprint: no false dirty dot");
        vm.MarkSaved();
        expect(!vm.HasUnsavedChanges, "marked saved: no unsaved changes");
        var source = SourceProbe.MemberOfType("MainViewModel", "internal string Fingerprint(");
        expect(source.Contains("liveTimeline: true, copyBin: false", StringComparison.Ordinal) && source.Contains("FingerprintOptions", StringComparison.Ordinal),
            "Fingerprint serializes the live state with the frozen-state options (no Document.Clone(deep: true))");
    }

    private static int FirstDifference(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
                return i;
        }

        return length;
    }

    // ----- save-waits-off-ui -----

    private static void SaveWaits(Action<bool, string> expect)
    {
        var autosave = SourceProbe.MemberOfType("MainViewModel", "public async Task AutosaveAsync(");
        var code = HotPathTests.WithoutComments(autosave);
        expect(!code.Contains(".Wait(", StringComparison.Ordinal) && !code.Contains("HasUnsavedChanges", StringComparison.Ordinal)
               && SourceProbe.InOrder(autosave, "var fingerprint = Fingerprint();", "waitForThumbnail: false", "ThumbnailRequest()", "await Task.Run(async () =>", "FinishThumbnailAsync(thumbnail)"),
            "autosave: one fingerprint, the state without waiting, the fresh thumbnail awaited on the pool before the write");
        var edit = SourceProbe.MemberOfType("MainViewModel", "internal bool Edit(");
        expect(SourceProbe.InOrder(edit, "var before = undoable && !_isBackgroundWorker ? CaptureSnapshot() : null;", "if (before is null)", "FlushLooks();")
               && SourceProbe.IndexOf(edit, "FlushLooks();") == SourceProbe.IndexOf(edit, "FlushLooks();", SourceProbe.IndexOf(edit, "if (before is null)")),
            "an edit flushes the looks once (CaptureSnapshot does it for an undoable edit)");

        // Mask alpha: opaque and transparent 32-bit PNGs, a 24-bit PNG and a JPEG.
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-masks-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        string Write(string name, System.Windows.Media.PixelFormat format, byte alpha, System.Windows.Media.Imaging.BitmapEncoder encoder)
        {
            var bpp = format.BitsPerPixel / 8;
            var pixels = new byte[16 * 16 * bpp];
            for (var i = 0; i < pixels.Length; i += bpp)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 200;
                if (bpp == 4)
                    pixels[i + 3] = i < pixels.Length / 2 ? alpha : (byte)255;
            }

            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96, format, null, pixels, 16 * bpp);
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var path = Path.Combine(folder, name);
            using (var stream = File.Create(path))
                encoder.Save(stream);
            return path;
        }

        var transparent = Write("transparent.png", System.Windows.Media.PixelFormats.Bgra32, 0, new System.Windows.Media.Imaging.PngBitmapEncoder());
        var opaque = Write("opaque.png", System.Windows.Media.PixelFormats.Bgra32, 255, new System.Windows.Media.Imaging.PngBitmapEncoder());
        var rgb = Write("rgb.png", System.Windows.Media.PixelFormats.Bgr24, 255, new System.Windows.Media.Imaging.PngBitmapEncoder());
        var jpeg = Write("photo.jpg", System.Windows.Media.PixelFormats.Bgr24, 255, new System.Windows.Media.Imaging.JpegBitmapEncoder());
        expect(HandPegApp.Services.MaskStore.UsesAlpha(transparent) && !HandPegApp.Services.MaskStore.UsesAlpha(opaque)
               && !HandPegApp.Services.MaskStore.UsesAlpha(rgb) && !HandPegApp.Services.MaskStore.UsesAlpha(jpeg),
            "a mask with transparency is read by its alpha; an opaque 32-bit PNG, a 24-bit PNG and a JPEG by their brightness");
        expect(SourceProbe.InOrder(SourceProbe.Src("Services", "MaskStore.cs"), "BitmapCreateOptions.DelayCreation", "if (!HasAlphaChannel(format))", "BitmapCacheOption.OnLoad"),
            "the pixel format is read from the header before any decode: a picture without alpha is never decoded");
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ----- key-edit-tolerance -----

    private static void KeyTolerance(Action<bool, string> expect)
    {
        IReadOnlyList<Keyframe> keys = [new(0.0, 10), new(2.0, 20)];
        expect(KeyEdit.IndexAt(keys, Layer.KeyTolerance) == 0 && KeyEdit.IndexAt(keys, Layer.KeyTolerance * 1.5) == -1,
            "a key exactly KeyTolerance away is the key there (inclusive); half as far again is not");
        var upserted = KeyEdit.Upsert(keys, 0.01, 15);
        expect(upserted.Count == 2 && upserted[0] == keys[0] with { Value = 15 }, "Upsert at a key: its value set, its time kept");
        var added = KeyEdit.Upsert(keys, 3.12345, 30);
        expect(added.Count == 3 && added[2].Time == 3.123 && KeyEdit.Upsert(keys, -1, 5)[2].Time == 0, "Upsert elsewhere: a key at the time rounded to the millisecond, never before 0");
        var toggled = KeyEdit.Toggle(keys, 2.0, 0, out var removed);
        expect(removed && toggled.Count == 1 && !KeyEdit.Toggle(keys, 5, 1, out var removedNone).SequenceEqual(keys) && !removedNone, "Toggle removes a key there, adds one where none is");

        var param = EffectRegistry.Find(EffectRegistry.ColorStudio)!.Params.First(p => p.Step >= 1);
        expect(param.Quantize(param.Min + 0.4 * param.Step) == param.Min && param.Quantize(param.Max + 99) == param.Max,
            $"Quantize: a whole-step value ({param.Label}) rounded to its step and held to its range, as both panes store it");
        var views = new[] { "EffectsPane.xaml.cs", "EffectsPane.Audio.cs", "EffectsPane.Speed.cs", "ColorStudioPane.Writes.cs", "ColorStudioPane.xaml.cs" }
            .Select(name => HotPathTests.WithoutComments(SourceProbe.Src("Views", name))).Append(HotPathTests.WithoutComments(SourceProbe.Src("Dialogs", "TrackAudioFiltersDialog.xaml.cs"))).ToList();
        expect(views.All(t => !t.Contains("< 0.02)", StringComparison.Ordinal) && !t.Contains("< 0.001)", StringComparison.Ordinal)),
            "no pane or dialog keeps its own key tolerance literal (KeyEdit, TrackAudioFilters.PutMixKey)");
    }
}
