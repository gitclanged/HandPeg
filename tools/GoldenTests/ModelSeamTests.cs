using System.Diagnostics;
using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegGoldenTests;

/// <summary>
/// The model's single-source-of-truth seams: the clip id index agrees with a walk over the tracks through any edit, and costs O(1).
/// </summary>
public static class ModelSeamTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("layer-field-ownership", "# One ownership table (LayerFields): every Layer property is the clip's, the view's, probed, or a look LayerState holds; no Apply* writes a clip field; track looks folded once.", FieldOwnership);
        yield return Case("timeline-document-ops", "# Markers, Sequence Output effects, frame and frame rate change through the transaction: committed on their own, rolled back with it; the view model writes none directly.", DocumentOps);
        yield return Case("architecture-layers", "# Layer edges: Models use no Services or WPF, Services no window or UI type, Docking no MainWindow, ViewModels no message box or main window; today's edges allow-listed, shrinking only.", Layers);
        yield return Case("lowering-pure-and-cost", "# Lowered layers are measured without being changed (ComputeOutputRect); a 500-clip Sequence lowers well inside a frame (the per-clip cache waits for a measured need).", LoweringCost);
        yield return Case("encoding-preset-saved-once", "# Every value of a project's settings is written to its file once: what the timeline or the background keeps is not written twice.", PresetSavedOnce);
        yield return Case("timeline-clip-index", "# The clip id index: the same answers as a walk over every track (present, absent, moved, re-split, undone, changed directly), and O(1).", ClipIndex);
    }

    // ----- layer-field-ownership -----

    private static void FieldOwnership(Action<bool, string> expect)
    {
        var state = typeof(LayerState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var layer = typeof(Layer).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true }).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var orphans = layer.Where(name => LayerFields.Owner(name) == FieldOwner.Look && !state.Contains(name)).ToList();
        expect(layer.Count > 60, $"Layer's settable properties read: {(layer.Count > 60 ? "more than 60" : layer.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        expect(orphans.Count == 0, "every Layer property is the clip's, the view's, probed, or a look LayerState saves under its name"
            + (orphans.Count == 0 ? "" : ": unclassified " + string.Join(", ", orphans)));
        var notOnLayer = LayerFields.Clip.Concat(LayerFields.View).Concat(LayerFields.Alias).Where(name => !layer.Contains(name)).ToList();
        expect(notOnLayer.Count == 0, "the table names only properties Layer has" + (notOnLayer.Count == 0 ? "" : ": " + string.Join(", ", notOnLayer)));
        expect(LayerFields.UndoableLooks.Contains(nameof(Layer.PositionX)) && !LayerFields.UndoableLooks.Overlaps(LayerFields.Clip)
               && !LayerFields.UndoableLooks.Overlaps(LayerFields.View) && !LayerFields.UndoableLooks.Overlaps(LayerFields.Probe)
               && !LayerFields.UndoableLooks.Overlaps(LayerFields.Alias),
            "the undoable looks are LayerState's look properties only (no clip, view or probed value)");

        var serialization = HotPathTests.WithoutComments(SourceProbe.Src("Models", "Layer.Serialization.cs"));
        var writes = new List<string>();
        foreach (var method in new[] { "public void ApplyLook(", "public void ApplySource(", "public void ApplyPlace(" })
        {
            var body = SourceProbe.Member(serialization, method);
            writes.AddRange(LayerFields.Clip.Where(name => System.Text.RegularExpressions.Regex.IsMatch(body, @"(?<![\.\w])" + name + @"\b")).Select(name => $"{method.Trim('(')[12..]}: {name}"));
        }

        expect(writes.Count == 0, "ApplyLook, ApplySource and ApplyPlace write no clip field (the clip's own come from TakeClip)" + (writes.Count == 0 ? "" : ": " + string.Join(", ", writes)));
        var sources = string.Join("\n", new[] { SourceProbe.Src("ViewModels", "MainViewModel.Document.cs"), SourceProbe.Src("ViewModels", "MainViewModel.Undo.cs") }.Select(HotPathTests.WithoutComments));
        expect(!sources.Contains("ClipFields", StringComparison.Ordinal) && !sources.Contains("ViewOnly", StringComparison.Ordinal) && !sources.Contains("UndoLooks", StringComparison.Ordinal)
               && sources.Contains("LayerFields.Owner(name)", StringComparison.Ordinal) && sources.Contains("LayerFields.UndoableLooks", StringComparison.Ordinal),
            "the view model's sync and undo read the one table (no hand-kept ClipFields, ViewOnly or UndoLooks lists)");

        // Track looks: folded once; the lowering has no merge of its own.
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        using (var tx = doc.BeginTransaction("Set up"))
        {
            var track = tx.InsertTrack(0, TimelineTrackKind.Video);
            tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Text, End = 2, State = new LayerState { Name = "Own", PositionX = 0.25, KeepPitch = false, Opacity = 100 } });
            tx.Commit();
        }

        doc.Tracks[0].TrackLook = new LayerState { Name = "Shared", Opacity = 40, CornerRadius = 12, PositionX = 0.9 };
        var copy = doc.Clone();
        expect(copy.FoldTrackLooks() && !copy.HasTrackLooks && doc.HasTrackLooks, "folding a copy clears its track looks and leaves the original's");
        var look = copy.Tracks[0].Clips[0].State;
        expect(look is { Name: "Own", PositionX: 0.25, KeepPitch: false, Opacity: 40, CornerRadius: 12 } && doc.Tracks[0].Clips[0].State.Opacity == 100,
            "a folded clip keeps its own name, place and speed options and takes the track's shared look (opacity, corners); the original's look untouched");
        var lowering = HotPathTests.WithoutComments(SourceProbe.Src("Services", "Gpu", "TimelineLowering.cs"));
        expect(!lowering.Contains("Merged(", StringComparison.Ordinal) && !lowering.Contains(".TrackLook", StringComparison.Ordinal)
               && lowering.Contains("document.FoldTrackLooks();", StringComparison.Ordinal),
            "the lowering merges nothing itself: a document with track looks is lowered from a folded copy");
        expect(SourceProbe.InOrder(SourceProbe.MemberOfType("MainViewModel", "private void PutDocumentInPlace("), "document.FoldTrackLooks();", "Document = document;"),
            "a document put in place is folded before it becomes the timeline");
    }

    // ----- timeline-document-ops -----

    private static void DocumentOps(Action<bool, string> expect)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25, NaturalWidth = 1920, NaturalHeight = 1080 };
        var commits = new List<TimelineCommit>();
        doc.Committed += commits.Add;
        var effect = new HandPegApp.Models.Effects.EffectState { Type = HandPegApp.Models.Effects.EffectRegistry.MotionBlur };
        using (var tx = doc.BeginTransaction("document values"))
        {
            tx.SetMarkers([new TimelineMarker(1, "A"), new TimelineMarker(2, "B")]);
            tx.SetSequenceEffects([effect], bypassed: true);
            tx.SetFrame(1280, 720, explicitFrame: true);
            tx.SetFrameRate(FrameRate.Fps60, explicitRate: true);
            tx.Commit();
        }

        expect(commits is [{ TouchedClipIds.Count: 0, TouchedTrackIds.Count: 0 }], "one commit, no clip or track touched (a step of Undo like the master gain's)");
        expect(doc is { Markers.Count: 2, SequenceEffects.Count: 1, SequenceEffectsBypassed: true, NaturalWidth: 1280, NaturalHeight: 720, ExplicitFrame: true, ExplicitFrameRate: true }
               && doc.FrameRate == FrameRate.Fps60, "the markers, the Sequence Output's effects and bypass, the frame and the rate set");

        using (var tx = doc.BeginTransaction("rolled back"))
        {
            tx.SetMarkers([]);
            tx.SetSequenceEffects([], bypassed: false);
            tx.SetFrame(640, 360, explicitFrame: false);
            tx.SetFrameRate(FrameRate.Ntsc2997, explicitRate: false);
        }

        expect(commits.Count == 1 && doc is { Markers.Count: 2, SequenceEffects.Count: 1, SequenceEffectsBypassed: true, NaturalWidth: 1280, ExplicitFrame: true } && doc.FrameRate == FrameRate.Fps60,
            "a transaction disposed uncommitted puts every one of them back, and raises nothing");
        using (var tx = doc.BeginTransaction("same again"))
        {
            tx.SetMarkers([.. doc.Markers]);
            tx.SetFrameRate(FrameRate.Fps60, explicitRate: true);
            tx.Commit();
        }

        expect(commits.Count == 1, "the same values again change nothing and raise nothing");
        var refused = false;
        try
        {
            using var tx = doc.BeginTransaction("bad rate");
            tx.SetFrameRate(new FrameRate(0, 1), explicitRate: true);
        }
        catch (TimelineEditException)
        {
            refused = true;
        }

        expect(refused && doc.FrameRate == FrameRate.Fps60, "a rate that is not positive is refused");

        // The view model's code without FlushLooks, whose mirror flush (markers, effects, the frame's size and turn) is the allowed one.
        var flush = SourceProbe.MemberOfType("MainViewModel", "private void FlushLooks(");
        var viewModels = string.Concat(Directory.GetFiles(Path.Combine(SourceProbe.Root, "src", "ViewModels"), "*.cs")
            .Select(path => HotPathTests.WithoutComments(File.ReadAllText(path).ReplaceLineEndings("\n").Replace(flush, "", StringComparison.Ordinal))));
        var direct = System.Text.RegularExpressions.Regex.Matches(viewModels,
            @"Document\.(Markers|FrameRate|NaturalWidth|NaturalHeight|ExplicitFrame|ExplicitFrameRate|ColorSpace|PixelAspect|SampleRate|MasterGainDb)\s*=[^=]|\((Document\.\w+,\s*)+Document\.\w+\)\s*=[^=>]")
            .Select(m => m.Value.Trim()).ToList();
        expect(direct.Count == 0 && flush.Contains("Document.Markers = [.. Markers];", StringComparison.Ordinal),
            "the view model writes no document-level value itself outside the mirror flush (FlushLooks)" + (direct.Count == 0 ? "" : ": " + string.Join(" | ", direct)));
    }

    // ----- architecture-layers -----

    // The edges that exist today (folder/file|token). A new edge fails; a fixed one must leave this list (it only shrinks).
    private static readonly string[] KnownEdges = [];

    private static void Layers(Action<bool, string> expect)
    {
        var src = Path.Combine(SourceProbe.Root, "src");
        string Rel(string path) => Path.GetRelativePath(src, path).Replace('\\', '/');
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Select(path => (Rel: Rel(path), Code: HotPathTests.WithoutComments(File.ReadAllText(path)))).ToList();

        // The UI types: declared at the top level of a Controls or Dialogs file and nowhere else.
        var declaration = new System.Text.RegularExpressions.Regex(@"^(?:public|internal)[^\n=]*?\b(?:class|record\s+struct|record|struct|enum|interface)\s+(\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        var ui = new HashSet<string>(StringComparer.Ordinal);
        var other = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, code) in files)
        {
            var target = rel.StartsWith("Controls/", StringComparison.Ordinal) || rel.StartsWith("Dialogs/", StringComparison.Ordinal) ? ui : other;
            foreach (System.Text.RegularExpressions.Match match in declaration.Matches(code))
                target.Add(match.Groups[1].Value);
        }

        ui.ExceptWith(other);
        var rules = new (string Folder, string[] Patterns)[]
        {
            ("Models/", [@"HandPegApp\.Services\b", @"(?<![\w.])Services\.\w", @"System\.Windows\b"]),
            ("ViewModels/", [@"\bMessageBox\b", @"Application\.Current\??\.MainWindow"]),
            ("Docking/", [@"\bMainWindow\."]),
            // HandPeg's MainWindow type (WPF's Application.MainWindow property, a plain Window, is not a layer edge).
            ("Services/", [@"(?<![\w.])MainWindow\b", .. ui.Order(StringComparer.Ordinal).Select(type => $@"\b{type}\b")]),
        };
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (folder, patterns) in rules)
        {
            var pattern = new System.Text.RegularExpressions.Regex(string.Join("|", patterns));
            foreach (var (rel, code) in files.Where(f => f.Rel.StartsWith(folder, StringComparison.Ordinal)))
            {
                foreach (System.Text.RegularExpressions.Match match in pattern.Matches(code))
                    found.Add($"{rel}|{match.Value}");
            }
        }

        var added = found.Except(KnownEdges, StringComparer.Ordinal).ToList();
        var gone = KnownEdges.Except(found, StringComparer.Ordinal).ToList();
        expect(ui.Count > 100, "the UI types are read from the Controls and Dialogs folders (more than 100)");
        expect(added.Count == 0, "no new layer edge" + (added.Count == 0 ? "" : ": " + string.Join(", ", added)));
        expect(gone.Count == 0, $"every allow-listed edge still exists ({KnownEdges.Length}; a fixed one leaves the list)" + (gone.Count == 0 ? "" : ": " + string.Join(", ", gone)));
        expect(!found.Any(e => e.Contains("GpuViewport", StringComparison.Ordinal) || e.Contains("StyleFile", StringComparison.Ordinal) || e.Contains("FrameLatencyTurns", StringComparison.Ordinal))
               && File.Exists(Path.Combine(src, "Services", "StyleFile.cs")) && File.Exists(Path.Combine(src, "Services", "GpuViewport.cs"))
               && File.Exists(Path.Combine(src, "Models", "TextDefaults.cs")) && File.Exists(Path.Combine(src, "Controls", "IconGeometry.cs")),
            "the moves are in: StyleFile and GpuViewport in Services, TextDefaults in Models, the icon geometry in Controls");
    }

    // ----- lowering-pure-and-cost -----

    private static void LoweringCost(Action<bool, string> expect)
    {
        var layer = new Layer { SizeWidth = 0.5, SizeHeight = 0.25, LockAspectRatio = false, PositionX = 0.1, PositionY = 0.2 };
        var before = layer.HeightPerWidth;
        var pure = layer.ComputeOutputRect(1920, 1080, 1920, 1080);
        expect(pure == (192, 216, 960, 270) && layer.HeightPerWidth == before, "ComputeOutputRect measures the box and changes nothing on the layer");
        expect(layer.GetOutputRect(1920, 1080, 1920, 1080) == pure, "GetOutputRect (the editor's view) gives the same box");
        var plan = HotPathTests.WithoutComments(SourceProbe.Src("Services", "Gpu", "GpuScenePlan.Create.cs"));
        expect(!plan.Contains("GetOutputRect(", StringComparison.Ordinal) && plan.Contains("ComputeOutputRect(", StringComparison.Ordinal),
            "the scene plan, which reads lowered layers on several threads, never calls the remembering GetOutputRect");

        // 500 clips on 5 tracks with a look each, lowered afresh (a new document each time, as every edit makes): the median of 9.
        TimelineDocument Make()
        {
            var doc = new TimelineDocument { FrameRate = FrameRate.Fps60, Width = 1920, Height = 1080 };
            using var tx = doc.BeginTransaction("500 clips");
            for (var t = 0; t < 5; t++)
            {
                var track = tx.InsertTrack(t, TimelineTrackKind.Video);
                for (var i = 0; i < 100; i++)
                {
                    tx.PlaceClip(track, new TimelineClip
                    {
                        ContentKind = LayerKind.Text, Start = i, End = i + 0.9,
                        State = new LayerState { Name = $"T{t}C{i}", TextContent = "words", PositionX = 0.1, SizeWidth = 0.3, KeysOpacity = [new(0, 100), new(0.5, 50)] },
                    });
                }
            }

            tx.Commit();
            return doc;
        }

        var caption = Layer.FromState(new LayerState { Kind = LayerKind.Captions, Name = "Subtitles", SizeWidth = 0.9, SizeHeight = 0.2 }, 1920, 1080, 1920, 1080);
        var times = new List<double>();
        for (var round = 0; round < 9; round++)
        {
            var state = new HandPegApp.Services.SequenceExportState { Timeline = Make(), SourceFrameRate = 60, RequestedSize = (1920, 1080), OutputSize = (1920, 1080), CaptionLayer = caption };
            var watch = Stopwatch.StartNew();
            var lowered = HandPegApp.Services.Gpu.TimelineLowering.Lowering(state);
            watch.Stop();
            if (lowered.Stack.Count < 500)
                times.Add(double.NaN);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        Console.WriteLine($"  note  lowering-pure-and-cost: 500 clips lowered in {median:0.00} ms (median of 9, a debug build)");
        expect(!times.Any(double.IsNaN), "every clip lowered to a layer");
        expect(median < 50, $"500 clips lower in {(median < 50 ? "under 50 ms" : $"{median:0} ms (over 50)")} in a debug build: a per-clip cache is not needed yet");
    }

    // ----- encoding-preset-saved-once -----

    private static void PresetSavedOnce(Action<bool, string> expect)
    {
        // Every writable string and number of the settings given a value found nowhere else (a token, a number no default has).
        var preset = new EncodingPreset();
        var marks = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var property in typeof(EncodingPreset).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(p => p.CanWrite && p.CanRead))
        {
            index++;
            if (property.PropertyType == typeof(string))
            {
                property.SetValue(preset, $"pv{index:000}x");
                marks[property.Name] = $"\"pv{index:000}x\"";
            }
            else if (property.PropertyType == typeof(double))
            {
                property.SetValue(preset, 970000.0 + index);
                marks[property.Name] = (970000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (property.PropertyType == typeof(int))
            {
                property.SetValue(preset, 980000 + index);
                marks[property.Name] = (980000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"handpeg-preset-once-{Environment.ProcessId}.hproj");
        try
        {
            HandPegApp.Services.ProjectStore.SaveTo(new ProjectState { Name = "once", Timeline = new TimelineDocument(), Settings = preset }, path, durable: false);
            var text = File.ReadAllText(path);
            static int Count(string text, string token) => (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;
            var twice = marks.Where(m => Count(text, m.Value) > 1).Select(m => $"{m.Key} x{Count(text, m.Value)}").ToList();
            var written = marks.Count(m => Count(text, m.Value) == 1);
            expect(marks.Count > 40, $"the settings' strings and numbers marked: {(marks.Count > 40 ? "more than 40" : marks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            expect(twice.Count == 0, "none is written twice (the background's blur in its own section only, the name in the file's header only)" + (twice.Count == 0 ? "" : ": " + string.Join(", ", twice)));
            expect(written > 30, "and the export settings are in it (written once each)");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    // ----- timeline-clip-index -----

    private static (TimelineClip Clip, TimelineTrackModel Track)? Walk(TimelineDocument doc, Guid id)
    {
        foreach (var track in doc.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.Id == id)
                    return (clip, track);
            }
        }

        return null;
    }

    private static void ClipIndex(Action<bool, string> expect)
    {
        var random = new Random(5_6_4);
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        var ids = new List<Guid>();
        TimelineTrackModel v1, v2;
        using (var tx = doc.BeginTransaction("Set up"))
        {
            (v1, v2) = (tx.InsertTrack(0, TimelineTrackKind.Video), tx.InsertTrack(1, TimelineTrackKind.Video));
            for (var i = 0; i < 40; i++)
                ids.Add(tx.PlaceClip(i % 2 == 0 ? v1 : v2, new TimelineClip { ContentKind = LayerKind.Text, Start = i * 2, End = i * 2 + 1.5 }).Id);
            tx.Commit();
        }

        var mismatches = new List<string>();
        void Compare(string after)
        {
            // Every id ever made (some gone now) and a few that never existed.
            foreach (var id in ids.Concat([Guid.NewGuid(), Guid.Empty]))
            {
                var (indexed, walked) = (doc.Locate(id), Walk(doc, id));
                var same = indexed is null ? walked is null
                    : walked is { } w && ReferenceEquals(indexed.Value.Clip, w.Clip) && ReferenceEquals(indexed.Value.Track, w.Track);
                if (!same || !ReferenceEquals(doc.FindClip(id), walked?.Clip) || (walked is { } hit && !ReferenceEquals(doc.TrackOf(hit.Clip), hit.Track)))
                    mismatches.Add($"{after}: {id}");
            }
        }

        Compare("set up");
        var undo = new List<TimelineDocument>();
        for (var step = 0; step < 300; step++)
        {
            var clips = doc.Tracks.SelectMany(t => t.Clips).ToList();
            var label = "";
            switch (random.Next(7))
            {
                case 0 when clips.Count > 0:
                {
                    var clip = clips[random.Next(clips.Count)];
                    using var tx = doc.BeginTransaction(label = "split");
                    if (clip.End - clip.Start > 0.5)
                        ids.Add(tx.SplitClipAt(clip, (clip.Start + clip.End) / 2).Id);
                    tx.Commit();
                    break;
                }

                case 1 when clips.Count > 1:
                {
                    var clip = clips[random.Next(clips.Count)];
                    using var tx = doc.BeginTransaction(label = "delete");
                    tx.DeleteClip(clip);
                    tx.Commit();
                    break;
                }

                case 2 when clips.Count > 0:
                {
                    var clip = clips[random.Next(clips.Count)];
                    using var tx = doc.BeginTransaction(label = "move");
                    try
                    {
                        tx.MoveClip(clip, random.Next(2) == 0 ? v1 : v2, 200 + random.Next(400) * 0.25);
                        tx.Commit();
                    }
                    catch (InvalidOperationException)
                    {
                        // A refused move (an overlap): rolled back by the using.
                    }

                    break;
                }

                case 3:
                {
                    using var tx = doc.BeginTransaction(label = "rolled back");
                    if (clips.Count > 0)
                        tx.DeleteClip(clips[0]);
                    ids.Add(tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Text, Start = 900 + step, End = 900.5 + step }).Id);
                    // Lookups mid-transaction walk the tracks (the index is not used while one is open).
                    Compare("inside a transaction");
                    break; // disposed uncommitted: everything put back
                }

                case 4:
                    undo.Add(doc.Clone());
                    label = "snapshot";
                    break;

                case 5 when undo.Count > 0:
                {
                    // An undo step: the tracks replaced by a snapshot's, outside any transaction (as a project load does).
                    var back = undo[random.Next(undo.Count)].Clone();
                    doc.Tracks.Clear();
                    doc.Tracks.AddRange(back.Tracks);
                    (v1, v2) = (doc.Tracks[0], doc.Tracks[1]);
                    label = "tracks replaced directly";
                    break;
                }

                default:
                {
                    // A clip added and one moved straight on the lists, with no transaction (legacy conversion, the playback test).
                    var made = new TimelineClip { ContentKind = LayerKind.Text, Start = 5000 + step * 2, End = 5001 + step * 2 };
                    v1.Clips.Add(made);
                    ids.Add(made.Id);
                    if (v2.Clips.Count > 0 && v2.Clips[^1] is var last && last.Start < 4000)
                    {
                        v2.Clips.Remove(last);
                        last.Start += 8000;
                        last.End += 8000;
                        v1.Clips.Add(last);
                    }

                    label = "changed directly";
                    break;
                }
            }

            Compare(label);
        }

        expect(mismatches.Count == 0, mismatches.Count == 0
            ? "300 random edits (split, delete, move, rollback, undo-style replacement, direct list changes): index and walk agree for every id, present or not"
            : $"index and walk disagree {mismatches.Count} times, first: {mismatches[0]}");

        // Lookups between edits use one index; an outermost commit drops it.
        var builds = doc.ClipIndexBuilds;
        var present = doc.Tracks.SelectMany(t => t.Clips).Select(c => c.Id).ToList();
        foreach (var id in present)
            _ = doc.FindClip(id);
        var afterLookups = doc.ClipIndexBuilds;
        expect(afterLookups - builds <= 1, "looking up every clip between two edits builds the index at most once");
        using (var tx = doc.BeginTransaction("one more"))
        {
            tx.PlaceClip(v2, new TimelineClip { ContentKind = LayerKind.Text, Start = 20_000, End = 20_001 });
            tx.Commit();
        }

        _ = doc.FindClip(present[0]);
        expect(doc.ClipIndexBuilds == afterLookups + 1, "the outermost commit dropped it: the next lookup built it once");

        // O(1): 5,000 clips, every clip looked up 8 times (a row rebuild's per-block questions) in well under the walk's quadratic cost.
        var big = new TimelineDocument { FrameRate = FrameRate.Fps25 };
        using (var tx = big.BeginTransaction("5,000 clips"))
        {
            var track = tx.InsertTrack(0, TimelineTrackKind.Video);
            for (var i = 0; i < 5_000; i++)
                tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Text, Start = i, End = i + 0.5 });
            tx.Commit();
        }

        var all = big.Tracks[0].Clips.ToList();
        var watch = Stopwatch.StartNew();
        var found = 0;
        for (var round = 0; round < 8; round++)
        {
            foreach (var clip in all)
            {
                if (big.FindClip(clip.Id) is not null && big.TrackOf(clip) is not null)
                    found++;
            }
        }

        watch.Stop();
        expect(found == 40_000, "5,000 clips x 8 lookups: every one found with its track");
        expect(watch.Elapsed.TotalMilliseconds < 250, $"in {(watch.Elapsed.TotalMilliseconds < 250 ? "under 250 ms" : $"{watch.Elapsed.TotalMilliseconds:0} ms (over 250)")} (a walk would take about 100 million comparisons)");
    }
}
