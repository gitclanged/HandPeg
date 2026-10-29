using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The .hproj format, 5: the format 5 fixture read and written again (against a golden), read once more and compared field by
/// field; a newer format refused, opened only when asked, and never written over; files of formats 1 to 4, and files that are
/// not projects, refused with the reason; the seek indexes and the masks always written, the state's own looks left as they
/// were, and both taken up again on opening. The files the projects name need not exist, except where seek indexes and masks
/// are concerned (they are kept only for files that are there), which use files made in the scratch folder.
/// </summary>
public static class ProjectTests
{
    /// <summary>What a golden project file writes for the HandPeg that wrote it, so that a new version number does not change the golden.</summary>
    public const string WriterPlaceholder = "HandPeg <version>";

    /// <summary>The key the fixture carries its mask under: its file's name and the first bytes of its picture's hash (MaskStore).</summary>
    private const string FixtureMaskKey = "inset-mask_8fe91432.png";

    // Two mask pictures (2 by 2 grey PNGs, one the other's negative), as Base64.
    private const string MaskPicture = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAAAAABX3VL4AAAADklEQVR42mP4z8DA8B8ABgAB/2U0+PcAAAAASUVORK5CYII=";
    private const string OtherMaskPicture = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAAAAABX3VL4AAAADklEQVR42mNg+M/wnwEABgAB/5KZsuwAAAAASUVORK5CYII=";

    /// <summary>A HandPeg 1.x project (format 1): the window's state as it then was, in PascalCase, naming no format.</summary>
    private const string OneX = """{"SourcePath":"D:/media/old source.mkv","LocalMediaPath":"D:/media/old source.mkv","DestinationPath":"D:/out/old source_HandPeg.mp4","Segments":[{"StartMs":5000,"EndMs":12000,"Skipped":false}],"SnapToKeyframes":true,"Settings":{"VideoEncoder":"libx265"}}""";

    public static IEnumerable<GraphResult> All(string fixtures, string scratch)
    {
        yield return Format5RoundTrip(fixtures);
        yield return NewerFormat(fixtures, scratch);
        yield return NotProjects(scratch);
        yield return SeekIndexes(scratch);
    }

    private static GraphResult Format5RoundTrip(string fixtures)
    {
        var problems = new List<string>();
        var original = File.ReadAllText(Path.Combine(fixtures, "format5.hproj"));

        // Opened as the window opens a project: the mask it carries goes into the masks folder (the test's own), and is
        // carried again from there when it is written.
        var first = ProjectStore.Deserialize(original, unpackMasks: true);
        var mask = first.Timeline.Tracks[1].Clips[0].State.MaskPath;
        var written = ProjectStore.Serialize(first);
        var header = JsonNode.Parse(written)!.AsObject();
        var names = header.Select(p => p.Key).ToList();
        Expect(problems, names is ["format", "version", "writtenBy", ..] && names[^1] == "timeline",
            $"what says which file it is comes first, and the timeline last ({string.Join(", ", names)})");
        Expect(problems, header["format"]?.GetValue<string>() == ProjectFile.FormatName, "the written file says it is a HandPeg project");
        Expect(problems, header["version"]?.GetValue<int>() == 5 && header["version"]?.GetValue<int>() == ProjectFile.CurrentVersion,
            $"the written file is format 5 (it says {header["version"]})");
        Expect(problems, header["writtenBy"]?.GetValue<string>() == ProjectStore.WriterName, $"the written file names the HandPeg that wrote it ({header["writtenBy"]})");

        // Values that a careless writer loses: a setting written under an older name (VerticalFramer is the Layer Engine), a
        // Linear keyframe on an effect card (4.0 left it out of the file as if it were the default, so it read back as Ease
        // In-Out, a keyframe's constructor default), an effect no version knows yet with a setting of its own, and a Custom
        // Bézier's handles.
        Expect(problems, first.Settings.FrameEngine, "the Layer Engine setting is read (VerticalFramer in the file)");
        var clips = header["timeline"]?["tracks"]?[1]?["clips"];
        var brightness = clips?[0]?["state"]?["effects"]?[0]?["keys"]?["brightness"]?.AsArray();
        Expect(problems, brightness is { Count: 2 } && brightness[0]?["easing"]?.GetValue<string>() == "EaseIn" && brightness[1]?["easing"]?.GetValue<string>() == "Linear",
            $"an effect card's Linear keyframe is written as Linear ({brightness?.ToJsonString()})");
        Expect(problems, first.Timeline.Tracks[1].Clips[0].State.Effects is [_, _, { Type: "plugin.future.glow", Extra: { } extra }] && extra.ContainsKey("extraSetting"),
            "an effect this version does not know keeps a setting of its own");
        Expect(problems, first.Timeline.Tracks[1].Clips[1].State.KeysScale is [{ Easing: EasingType.CubicBezier, BezierX1: 0.7, BezierX2: 0.3, BezierY2: 1 }, { Easing: EasingType.Linear }],
            "a Custom Bézier keyframe keeps its handles");

        // The mask the file carries: unpacked on opening, carried again under the same key (its picture's), while the state's
        // own look goes on naming the file on the disk.
        Expect(problems, Path.GetDirectoryName(mask) == AppPaths.Masks && File.Exists(mask), $"the mask the project carries is unpacked into the masks folder ({mask})");
        Expect(problems, clips?[0]?["state"]?["maskPath"]?.GetValue<string>() == FixtureMaskKey && header["masks"]?[FixtureMaskKey]?.GetValue<string>() == JsonNode.Parse(original)!["masks"]?[FixtureMaskKey]?.GetValue<string>(),
            "written, the clip's look names the mask by its key, and the file carries the picture as it was");
        Expect(problems, first.Timeline.Tracks[1].Clips[0].State.MaskPath == mask, "writing the file leaves the state's own look as it was");

        // The document's own: its markers, and the recycle bin beside it, its clip with the track it was on.
        Expect(problems, first.Timeline.Markers is [{ Name: "Intro" }, { Name: "Highlight" }], "the markers are the timeline's");
        Expect(problems, first.Deleted is [{ TrackIndex: 1, TrackKind: TimelineTrackKind.Video, Clip.Id: var binned }] && binned == Fixed(91),
            "the recycle bin keeps its clip and the track it was on");
        var timelineProblems = first.Timeline.FindProblems(requireFrames: true);
        Expect(problems, timelineProblems.Count == 0, $"the timeline has no problems, and its edges are on frames ({string.Join("; ", timelineProblems)})");

        // Read again and written again: the same state, the same file.
        var second = ProjectStore.Deserialize(written, unpackMasks: true);
        problems.AddRange(Compare(first, second, "state"));
        Expect(problems, ProjectStore.Serialize(second) == written, "read and written again, the file is the same");

        // Everything the fixture says is still said, in the same words. Its seek index is for a file that is not on this
        // machine: it is not taken up, and so not written again (seek facts are written for files as they are).
        problems.AddRange(Contains(JsonNode.Parse(original)!, JsonNode.Parse(written)!, "file", ignore: ["writtenBy", "seekIndexes"]));

        // A recycle bin entry of 4.2.0's shape (a layer, or a piece of the main video), left in a file translated by hand, has
        // no clip to put back, and is passed over.
        var withOld = JsonNode.Parse(original)!.AsObject();
        withOld["sequence"]!["deleted"]!.AsArray().Add(new JsonObject { ["layer"] = new JsonObject { ["name"] = "old" }, ["index"] = 2 });
        Expect(problems, ProjectStore.Deserialize(withOld.ToJsonString()).Deleted.Count == 1, "a recycle bin entry of 4.2.0's shape is left out");

        return new GraphResult("hproj-format5-round-trip", "# The format 5 fixture (fixtures/format5.hproj) as this version writes it.\n\n" + Pretty(written), problems, 0);
    }

    private static GraphResult NewerFormat(string fixtures, string scratch)
    {
        var problems = new List<string>();
        var fixture = File.ReadAllText(Path.Combine(fixtures, "format5.hproj"));
        var newer = JsonNode.Parse(fixture)!.AsObject();
        var newerVersion = ProjectFile.CurrentVersion + 1;
        newer["version"] = newerVersion;
        newer["writtenBy"] = "HandPeg 9.9.9";

        // What the next format might add, at the top, in the timeline and on a clip: passed over, the file read as far as this
        // version knows it.
        newer["fromTheFuture"] = new JsonObject { ["something"] = "this version does not know" };
        newer["timeline"]!["fromTheFuture"] = new JsonArray(1, 2, 3);
        newer["timeline"]!["tracks"]![0]!["clips"]![0]!["fromTheFuture"] = "a clip's own new value";
        var text = newer.ToJsonString();

        try
        {
            ProjectStore.Deserialize(text);
            problems.Add($"a format {newerVersion} project was read without being asked to");
        }
        catch (NewerProjectFormatException refused)
        {
            Expect(problems, refused.Version == newerVersion && refused.WrittenBy == "HandPeg 9.9.9", "the refusal says which format and which version wrote it");
            Expect(problems, refused.Message.Contains("HandPeg 9.9.9", StringComparison.Ordinal) && refused.Message.Contains($"format {newerVersion}", StringComparison.Ordinal),
                $"the message names both: {refused.Message}");
        }

        var opened = ProjectStore.Deserialize(text, allowNewer: true);
        problems.AddRange(Compare(ProjectStore.Deserialize(fixture), opened, "opened anyway"));
        Expect(problems, ProjectStore.IsProject(newer), "the backup importer takes a newer project as one (copied unchanged; it asks before it opens)");

        // What a file says it is decides before anything else of it is read: a newer file whose timeline this version could
        // not read at all is still refused as newer.
        var unreadable = new JsonObject
        {
            ["format"] = ProjectFile.FormatName, ["version"] = newerVersion, ["writtenBy"] = "HandPeg 9.9.9", ["timeline"] = "not a timeline this version reads",
        };
        try
        {
            ProjectStore.Deserialize(unreadable.ToJsonString());
            problems.Add("a newer file this version cannot read was read");
        }
        catch (NewerProjectFormatException)
        {
        }
        catch (Exception other)
        {
            problems.Add($"a newer file this version cannot read was refused with a {other.GetType().Name}, not as newer: {other.Message}");
        }

        // On disk: what it says it is is read from its first bytes, and saving over it is refused, while Save As elsewhere is not.
        Directory.CreateDirectory(scratch);
        var file = Path.Combine(scratch, "newer.hproj");
        File.WriteAllText(file, text);
        Expect(problems, ProjectStore.PeekHeader(file) == (ProjectFile.FormatName, newerVersion, "HandPeg 9.9.9"), $"the header is read from the file ({ProjectStore.PeekHeader(file)})");
        try
        {
            ProjectStore.SaveTo(opened, file);
            problems.Add($"a format {newerVersion} project was written over");
        }
        catch (IOException refused)
        {
            Expect(problems, refused.Message.Contains("Save As", StringComparison.Ordinal), $"the refusal points to Save As: {refused.Message}");
        }

        Expect(problems, File.ReadAllText(file) == text, "the newer file is left exactly as it was");
        var elsewhere = Path.Combine(scratch, "saved elsewhere.hproj");
        ProjectStore.SaveTo(opened, elsewhere);
        Expect(problems, ProjectStore.PeekHeader(elsewhere)?.Version == ProjectFile.CurrentVersion, $"saved under another name, it is a format {ProjectFile.CurrentVersion} project");

        // A file of a format this version knows is saved over as it always was.
        ProjectStore.SaveTo(opened, elsewhere);
        return new GraphResult("hproj-newer-format", "# A file of the next format: refused unless asked, opened as far as known, never written over.\n", problems, 0);
    }

    private static GraphResult NotProjects(string scratch)
    {
        var problems = new List<string>();

        // Each refused as what it is (InvalidDataException), the reason saying so.
        void Refused(string text, string what, params string[] saying)
        {
            try
            {
                ProjectStore.Deserialize(text);
                problems.Add($"{what} was read as a project");
            }
            catch (InvalidDataException refused)
            {
                foreach (var words in saying.Where(w => !refused.Message.Contains(w, StringComparison.Ordinal)))
                    problems.Add($"{what}: the refusal does not say \"{words}\": {refused.Message}");
            }
            catch (Exception other)
            {
                problems.Add($"{what} was refused with a {other.GetType().Name}, not as what it is: {other.Message}");
            }
        }

        Refused("{\"name\":\"shopping\",\"items\":[\"milk\",\"bread\"]}", "a JSON file that is not a project", "not a HandPeg project");
        Refused("Words, not JSON.", "a text that is not JSON", "not a HandPeg project");
        Refused(OneX, "a HandPeg 1.x project (format 1, which names no format)", "not a HandPeg project");

        // Formats 2 to 4 (HandPeg 2.0 to 4.2.0) are not converted: refused, the reason naming the format and the one opened.
        foreach (var version in new[] { 2, 3, 4 })
            Refused(Older(version), $"a format {version} project", $"format {version}", $"format {ProjectFile.CurrentVersion} projects only", "not converted");

        // What a file says it is is read before anything else of it: a format 4 file whose sequence this version could not read
        // at all is refused as format 4, not for what in it did not fit.
        Refused(new JsonObject { ["format"] = ProjectFile.FormatName, ["version"] = 4, ["sequence"] = "not a sequence this version reads" }.ToJsonString(),
            "a format 4 file this version could not read", "format 4");

        // A format 5 project keeps its tracks and clips in its timeline: one without is not a project.
        Refused(new JsonObject { ["format"] = ProjectFile.FormatName, ["version"] = 5, ["writtenBy"] = "HandPeg 4.3.0", ["name"] = "No timeline" }.ToJsonString(),
            "a format 5 project without a timeline", "no timeline");
        Refused("{\"format\":\"handpeg-project\",\"version\":5,\"name\":\"Null timeline\",\"timeline\":null}", "a format 5 project whose timeline is null", "no timeline");
        Refused("{\"format\":\"handpeg-project\",\"name\":\"No number\",\"timeline\":{}}", "a project that does not say which format it is", "does not say which format");

        Expect(problems, !ProjectStore.IsProject(JsonNode.Parse(Older(4))!.AsObject()), "the backup importer does not take a format 4 project as one");
        Expect(problems, ProjectStore.IsProject(new JsonObject { ["format"] = ProjectFile.FormatName, ["version"] = 5 }), "it takes a format 5 project");

        // Open Project, drops and the address box take .hproj files only: a .txt, even a HandPeg 1.x project, is not one.
        Directory.CreateDirectory(scratch);
        var oneX = Path.Combine(scratch, "old project.txt");
        File.WriteAllText(oneX, OneX);
        Expect(problems, !ProjectStore.IsProjectFile(oneX), "a .txt, even a HandPeg 1.x project, is not a project file");
        var there = Path.Combine(scratch, "there.hproj");
        File.WriteAllText(there, "{}");
        Expect(problems, ProjectStore.IsProjectFile(there) && !ProjectStore.IsProjectFile(Path.Combine(scratch, "not there.hproj")), "a .hproj that is there is one");

        return new GraphResult("hproj-not-projects", "# Files that are not projects this version reads are refused with a reason.\n", problems, 0);
    }

    /// <summary>
    /// A project as HandPeg 2.0 to 4.2.0 wrote it (3 and 4 name their writer; 4 only adds values to 2's layout): its cut list,
    /// the main video's track, a track of layers with keys as arrays of numbers, and a recycle bin of that shape.
    /// </summary>
    private static string Older(int version)
    {
        var file = new JsonObject { ["format"] = ProjectFile.FormatName, ["version"] = version };
        if (version >= 3)
            file["writtenBy"] = version == 3 ? "HandPeg 4.1.0" : "HandPeg 4.2.0";
        file["name"] = "Older";
        file["source"] = new JsonObject { ["path"] = "D:/media/main.mp4" };
        file["sequence"] = JsonNode.Parse(
            """{"cuts":[[5000,12000],[14000,20000,1]],"markers":[{"seconds":12.5,"name":"Intro"}],"main":{"look":{"name":"main.mp4","kind":"MainVideo"},"clips":[{"duration":40}]},"mainIndex":1,"tracks":[{"id":1,"look":{"name":"clip1.mp4","kind":"VideoFile","imagePath":"D:/media/clip1.mp4"},"clips":[{"start":10,"duration":12,"keysX":[0,0.05,4,0.4],"easeX":[1,0]}]}],"deleted":[{"layer":{"name":"gone"},"index":0}]}""");
        file["settings"] = new JsonObject { ["videoEncoder"] = "libx264" };
        return file.ToJsonString();
    }

    private static GraphResult SeekIndexes(string scratch)
    {
        var problems = new List<string>();
        Directory.CreateDirectory(scratch);

        // Written: the seek facts known for a project's video files go into the file, always: the main video's (the file read,
        // and its two clips), and the file of a clip on another track, which only the timeline names.
        var written = Path.Combine(scratch, "written.mp4");
        File.WriteAllBytes(written, new byte[4096]);
        var info = new FileInfo(written);
        var iframes = new[] { 0.0, 2.002, 4.004, 6.006 };
        Expect(problems, MediaProbe.Adopt(new SeekFacts(written, info.Length, info.LastWriteTimeUtc.Ticks, 0.0, 0.0, 8.0, iframes)), "the facts of an existing file are adopted");
        var layer = Path.Combine(scratch, "layer.mp4");
        File.WriteAllBytes(layer, new byte[2048]);
        var layerInfo = new FileInfo(layer);
        var layerFrames = new[] { 0.0, 1.0, 2.0 };
        Expect(problems, MediaProbe.Adopt(new SeekFacts(layer, layerInfo.Length, layerInfo.LastWriteTimeUtc.Ticks, 0.1, 0.1, 3.0, layerFrames)), "the facts of a clip's file are adopted");

        // Masks: one on a clip's look (and on a clip in the recycle bin), another on a track's shared look.
        var clipMask = Path.Combine(scratch, "clip mask.png");
        var trackMask = Path.Combine(scratch, "track mask.png");
        File.WriteAllBytes(clipMask, Convert.FromBase64String(MaskPicture));
        File.WriteAllBytes(trackMask, Convert.FromBase64String(OtherMaskPicture));
        var timeline = new TimelineDocument
        {
            Tracks =
            [
                new TimelineTrackModel
                {
                    Id = Fixed(1), TrackNumber = 1,
                    Clips =
                    [
                        new TimelineClip
                        {
                            Id = Fixed(11), ContentKind = LayerKind.MainVideo, SourcePath = written, AudioStreamIndex = 0, End = 4, SourceDuration = 8,
                            State = new LayerState { Name = "Main", Kind = LayerKind.MainVideo },
                        },
                        new TimelineClip
                        {
                            Id = Fixed(12), ContentKind = LayerKind.MainVideo, SourcePath = written, AudioStreamIndex = 0, Start = 4, End = 6, MediaOffset = 6, SourceDuration = 8,
                            State = new LayerState { Name = "Main", Kind = LayerKind.MainVideo },
                        },
                    ],
                },
                new TimelineTrackModel
                {
                    Id = Fixed(2), TrackNumber = 2,
                    TrackLook = new LayerState { Name = "Inset", CustomMask = true, MaskPath = trackMask },
                    Clips =
                    [
                        new TimelineClip
                        {
                            Id = Fixed(21), SourcePath = layer, End = 3, SourceDuration = 3,
                            State = new LayerState { Name = "layer.mp4", Kind = LayerKind.VideoFile, ImagePath = layer, CustomMask = true, MaskPath = clipMask },
                        },
                    ],
                },
            ],
        };
        var binned = new TimelineClip
        {
            Id = Fixed(31), SourcePath = layer, Start = 3, End = 4, SourceDuration = 3,
            State = new LayerState { Name = "deleted", Kind = LayerKind.VideoFile, ImagePath = layer, CustomMask = true, MaskPath = clipMask },
        };
        var state = new ProjectState
        {
            Name = "Seek", SourcePath = written, LocalMediaPath = written, Timeline = timeline, Deleted = [new DeletedClip(binned, Fixed(2), TimelineTrackKind.Video, 1)],
        };
        var text = ProjectStore.Serialize(state);
        var json = JsonNode.Parse(text)!.AsObject();
        var seeks = json["seekIndexes"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        JsonObject? FactsOf(string path) => seeks.FirstOrDefault(s => string.Equals(s["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase));
        Expect(problems, FactsOf(written) is { } kept && kept["length"]?.GetValue<long>() == info.Length && MediaProbe.UnpackTimes(kept["iFrames"]!.GetValue<string>()).SequenceEqual(iframes),
            "the project carries the main video's I-frames, with its size");
        Expect(problems, FactsOf(layer) is { } layerKept && MediaProbe.UnpackTimes(layerKept["iFrames"]!.GetValue<string>()).SequenceEqual(layerFrames),
            "and the I-frames of a clip's video file, found on the timeline");
        Expect(problems, seeks.Count == 2, $"each file once ({seeks.Count} written)");

        // The masks: carried in the file under keys made from their pictures, the written looks naming those keys, while the
        // state's own looks go on naming the files on the disk.
        var masks = json["masks"]?.AsObject();
        var clipKey = json["timeline"]?["tracks"]?[1]?["clips"]?[0]?["state"]?["maskPath"]?.GetValue<string>();
        var trackKey = json["timeline"]?["tracks"]?[1]?["trackLook"]?["maskPath"]?.GetValue<string>();
        var binKey = json["sequence"]?["deleted"]?[0]?["clip"]?["state"]?["maskPath"]?.GetValue<string>();
        Expect(problems, masks is { Count: 2 } && clipKey is not null && trackKey is not null && clipKey != trackKey && masks.ContainsKey(clipKey) && masks.ContainsKey(trackKey),
            $"the clip's mask and the track's are carried, each under its own key ({clipKey}, {trackKey}; {masks?.Count ?? 0} carried)");
        Expect(problems, clipKey is not null && masks?[clipKey]?.GetValue<string>() == MaskPicture, "the clip's mask is carried byte for byte");
        Expect(problems, binKey == clipKey, $"a clip in the recycle bin names its mask by the same key: one picture, carried once ({binKey})");
        Expect(problems, timeline.Tracks[1].Clips[0].State.MaskPath == clipMask && timeline.Tracks[1].TrackLook?.MaskPath == trackMask && binned.State.MaskPath == clipMask,
            "writing the file leaves the state's own looks naming the files on the disk");

        // Opened: the masks go into the masks folder, and the looks name them there.
        var maskedPath = Path.Combine(scratch, "masked.hproj");
        File.WriteAllText(maskedPath, text);
        var opened = ProjectStore.Load(maskedPath);
        var openedMask = opened.Timeline.Tracks[1].Clips[0].State.MaskPath;
        var openedTrackMask = opened.Timeline.Tracks[1].TrackLook?.MaskPath ?? "";
        Expect(problems, Path.GetDirectoryName(openedMask) == AppPaths.Masks && File.Exists(openedMask) && File.ReadAllBytes(openedMask).SequenceEqual(File.ReadAllBytes(clipMask)),
            $"opened, the clip's mask is unpacked into the masks folder ({openedMask})");
        Expect(problems, Path.GetDirectoryName(openedTrackMask) == AppPaths.Masks && File.Exists(openedTrackMask) && File.ReadAllBytes(openedTrackMask).SequenceEqual(File.ReadAllBytes(trackMask)),
            $"and so is the track's ({openedTrackMask})");
        Expect(problems, opened.Deleted is [{ Clip.State.MaskPath: var openedBin }] && openedBin == openedMask, "and the recycle bin's clip names the same file");

        // Taken up: a project opened carries facts for a file still exactly as it was, and they are known at once.
        var read = Path.Combine(scratch, "read.mp4");
        File.WriteAllBytes(read, new byte[8192]);
        var readInfo = new FileInfo(read);
        var times = new[] { 0.5, 1.5, 2.5 };
        var file = JsonNode.Parse(ProjectStore.Serialize(new ProjectState { Name = "Seek 2", SourcePath = read, LocalMediaPath = read }))!.AsObject();
        file["seekIndexes"] = new JsonArray(new JsonObject
        {
            ["path"] = read, ["length"] = readInfo.Length, ["written"] = readInfo.LastWriteTimeUtc.Ticks, ["containerStart"] = 0.5, ["videoStart"] = 0.5,
            ["duration"] = 3, ["iFrames"] = MediaProbe.PackTimes(times),
        });
        var projectPath = Path.Combine(scratch, "seek.hproj");
        File.WriteAllText(projectPath, file.ToJsonString());
        ProjectStore.Load(projectPath);
        Expect(problems, MediaProbe.KnownSeekIndex(read) is { ContainerStart: 0.5 } index && index.IFrames.SequenceEqual(times), "the I-frames carried by the project are known once it is opened");

        return new GraphResult("hproj-seek-indexes", "# Seek indexes: always written for the files known, taken up again on opening.\n", problems, 0);
    }

    // ----- Comparing -----

    private static void Expect(List<string> problems, bool held, string what)
    {
        if (!held)
            problems.Add("not so: " + what);
    }

    private static Guid Fixed(int n) => new(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{n:D12}"));

    /// <summary>Two objects field by field (public properties, lists, dictionaries, records), naming each difference by its path.</summary>
    public static List<string> Compare(object? a, object? b, string path)
    {
        var differences = new List<string>();
        CompareInto(a, b, path, differences, 0);
        return differences;
    }

    private static void CompareInto(object? a, object? b, string path, List<string> differences, int depth)
    {
        if (differences.Count > 20 || depth > 40)
            return;
        if (a is null || b is null)
        {
            if (a is null != b is null && !(IsEmpty(a) && IsEmpty(b)))
                differences.Add($"{path}: {Show(a)} != {Show(b)}");
            return;
        }

        var type = a.GetType();
        if (type != b.GetType())
        {
            differences.Add($"{path}: a {type.Name} and a {b.GetType().Name}");
            return;
        }

        if (a is string || type.IsPrimitive || type.IsEnum || a is decimal or DateTime or DateTimeOffset or Guid or TimeSpan or JsonElement)
        {
            var same = a is JsonElement x && b is JsonElement y ? x.GetRawText() == y.GetRawText()
                : a is double da && b is double db ? da.Equals(db) || Math.Abs(da - db) < 1e-12
                : a.Equals(b);
            if (!same)
                differences.Add($"{path}: {Show(a)} != {Show(b)}");
            return;
        }

        if (a is IDictionary da2 && b is IDictionary db2)
        {
            foreach (var key in da2.Keys.Cast<object>().Concat(db2.Keys.Cast<object>()).Distinct())
                CompareInto(da2.Contains(key) ? da2[key] : null, db2.Contains(key) ? db2[key] : null, $"{path}[{key}]", differences, depth + 1);
            return;
        }

        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var (la, lb) = (ea.Cast<object?>().ToList(), eb.Cast<object?>().ToList());
            if (la.Count != lb.Count)
                differences.Add($"{path}: {la.Count} items != {lb.Count} items");
            for (var i = 0; i < Math.Min(la.Count, lb.Count); i++)
                CompareInto(la[i], lb[i], $"{path}[{i}]", differences, depth + 1);
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.CanRead))
            CompareInto(property.GetValue(a), property.GetValue(b), $"{path}.{property.Name}", differences, depth + 1);
    }

    private static bool IsEmpty(object? value) => value is null || value is ICollection { Count: 0 };

    private static string Show(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        JsonElement element => element.GetRawText(),
        _ => value.ToString() ?? "",
    };

    /// <summary>Every value the first JSON (the fixture) holds is in the second (what was written), at the same place; the second may hold more.</summary>
    private static IEnumerable<string> Contains(JsonNode expected, JsonNode actual, string path, string[] ignore)
    {
        switch (expected)
        {
            case JsonObject objectExpected when actual is JsonObject objectActual:
                foreach (var (key, value) in objectExpected)
                {
                    if (ignore.Contains(key) && path == "file")
                        continue;
                    if (!objectActual.TryGetPropertyValue(key, out var found))
                    {
                        // A value at its default is left out of the file (ProjectStore.SkipDefaults); the field-by-field
                        // comparison of what is read back says whether it still means the same.
                        if (!IsDefaultLooking(value))
                            yield return $"{path}.{key}: in the fixture, not in what was written";
                        continue;
                    }

                    if (value is not null && found is not null)
                    {
                        foreach (var difference in Contains(value, found, $"{path}.{key}", ignore))
                            yield return difference;
                    }
                }

                break;
            case JsonArray arrayExpected when actual is JsonArray arrayActual:
                if (arrayExpected.Count != arrayActual.Count)
                    yield return $"{path}: {arrayExpected.Count} items in the fixture, {arrayActual.Count} in what was written";
                for (var i = 0; i < Math.Min(arrayExpected.Count, arrayActual.Count); i++)
                {
                    if (arrayExpected[i] is { } e && arrayActual[i] is { } f)
                    {
                        foreach (var difference in Contains(e, f, $"{path}[{i}]", ignore))
                            yield return difference;
                    }
                }

                break;
            case JsonValue valueExpected when actual is JsonValue valueActual:
                if (!SameValue(valueExpected, valueActual))
                    yield return $"{path}: {valueExpected.ToJsonString()} in the fixture, {valueActual.ToJsonString()} in what was written";
                break;
            default:
                yield return $"{path}: not the same kind of value";
                break;
        }
    }

    private static bool IsDefaultLooking(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject => false,
        JsonValue v => v.ToJsonString() is "0" or "false" or "\"\"" or "null",
        _ => false,
    };

    private static bool SameValue(JsonValue a, JsonValue b) =>
        a.ToJsonString() == b.ToJsonString()
        || (a.TryGetValue<double>(out var x) && b.TryGetValue<double>(out var y) && x.Equals(y))
        || (a.TryGetValue<string>(out var s) && b.TryGetValue<string>(out var t)
            && (s == t || (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ds)
                           && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) && ds == dt)));

    /// <summary>A project's JSON indented, a value to a line, with the writer's version as a placeholder.</summary>
    private static string Pretty(string json)
    {
        var node = JsonNode.Parse(json)!;
        if (node is JsonObject root && root["writtenBy"] is not null)
            root["writtenBy"] = WriterPlaceholder;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}
