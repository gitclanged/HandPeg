using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The .hproj format: a format 2 file read and written as format 3 (against a golden), read again and compared field by
/// field; a HandPeg 1.x project brought over; a newer format refused, opened only when asked, and never written over;
/// the seek indexes always written and taken up again. The files the projects name need not exist, except where seek
/// indexes are concerned (they are kept only for files exactly as they were), which use files made in the temp folder.
/// </summary>
public static class ProjectTests
{
    /// <summary>What a golden project file writes for the HandPeg that wrote it, so that a new version number does not change the golden.</summary>
    public const string WriterPlaceholder = "HandPeg <version>";

    public static IEnumerable<GraphResult> All(string fixtures, string scratch)
    {
        yield return Format2RoundTrip(fixtures);
        yield return Format1Migration(fixtures);
        yield return NewerFormat(fixtures, scratch);
        yield return NotProjects();
        yield return SeekIndexes(scratch);
    }

    private static GraphResult Format2RoundTrip(string fixtures)
    {
        var problems = new List<string>();
        var original = File.ReadAllText(Path.Combine(fixtures, "format2.hproj"));
        var first = ProjectStore.Deserialize(original);
        var written = ProjectStore.Serialize(first);
        var header = JsonNode.Parse(written)!.AsObject();
        Expect(problems, header["format"]?.GetValue<string>() == ProjectFile.FormatName, "the written file says it is a HandPeg project");
        Expect(problems, header["version"]?.GetValue<int>() == 3, $"the written file is format 3 (it says {header["version"]})");
        Expect(problems, header["writtenBy"]?.GetValue<string>() == ProjectStore.WriterName, $"the written file names the HandPeg that wrote it ({header["writtenBy"]})");

        // Values that a careless writer loses: a setting written under an older name (VerticalFramer is the Layer Engine), and
        // a Linear keyframe on an effect card, which 4.0 left out of the file as if it were the default and so read back as
        // Ease In-Out (a keyframe's constructor default).
        Expect(problems, first.Settings.FrameEngine, "the Layer Engine setting is read (VerticalFramer in the file)");
        var brightness = header["sequence"]?["tracks"]?[0]?["look"]?["effects"]?[0]?["keys"]?["brightness"]?.AsArray();
        Expect(problems, brightness is { Count: 2 } && brightness[0]?["easing"]?.GetValue<string>() == "EaseIn" && brightness[1]?["easing"]?.GetValue<string>() == "Linear",
            $"an effect card's Linear keyframe is written as Linear ({brightness?.ToJsonString()})");

        var second = ProjectStore.Deserialize(written);
        problems.AddRange(Compare(first, second, "state"));

        // Format 2 and 3 share their layout: everything the format 2 file said is still said, in the same words.
        problems.AddRange(Contains(JsonNode.Parse(original)!, JsonNode.Parse(written)!, "file", ignore: ["version"]));

        return new GraphResult("hproj-format2-to-3", "# The format 2 fixture (fixtures/format2.hproj) as this version writes it.\n\n" + Pretty(written), problems, 0);
    }

    private static GraphResult Format1Migration(string fixtures)
    {
        var problems = new List<string>();
        var path = Path.Combine(fixtures, "format1.txt");
        var text = File.ReadAllText(path);
        Expect(problems, LegacyProject.IsLegacyFile(path), "the HandPeg 1.x project is recognised by its shape");
        Expect(problems, ProjectStore.IsProjectFile(path), "Open Project and drops take the HandPeg 1.x project");

        var state = ProjectStore.Deserialize(text);
        Expect(problems, state.SourcePath == "D:/media/old source.mkv" && state.DestinationPath == "D:/out/old source_HandPeg.mp4", "its source and output come over");
        Expect(problems, state.Segments.Count == 2 && state.Segments[0] == new SegmentState(5000, 12000) && state.Segments[1] == new SegmentState(30000, 45500, true),
            $"its two good cuts come over, the skipped one still skipped, the one that ends before it starts left out ({string.Join(", ", state.Segments)})");
        Expect(problems, state.SnapToIFrames, "snapping to \"keyframes\" became snapping to I-frames");
        Expect(problems, state is { WebOptimized: true, ChaptersAtCuts: true, Container: "mp4" }, "its output settings come over");
        var settings = state.Settings;
        Expect(problems, settings is { VideoEncoder: "libx265", EncoderPreset: "slow", Quality: 26, AudioBitrate: "192k", NormalizeAudio: true, Denoise: true, FadeIn: true },
            "its encoding settings come over");
        Expect(problems, settings.Layers is [{ Name: "Inset", Kind: LayerKind.Video, CornerRadius: 12, Opacity: 90, Shadow: true, TrackId: 1 }], "its layer comes over");
        Expect(problems, settings is { FrameEngine: true, UseVerticalResolution: true, BlurRadius: 30, CaptionStyle.FontName: "Verdana" } && Math.Abs(settings.CenterZoom - 1.5) < 1e-9,
            "the frame, its background and the caption style come over (under their 1.x names, VerticalFramer and VerticalZoom)");
        Expect(problems, settings.Layers.Count == 1 && settings.Layers[0].FlipHorizontal && Math.Abs(settings.Layers[0].FilterContrast - 1.1) < 1e-9,
            "the layer's old filters come over (they become effect cards when the layer is made)");
        Expect(problems, settings.MainVideoIndex == 1 && settings.AudioTracks.Count == 2 && state.AudioTracks[1].Action == "Drop" && state.CaptionAudioTrackIndex == 1,
            "the stack order, the audio tracks and the captions' track come over");

        // The other way of writing cuts in a 1.x project: times as text.
        var asTimes = ProjectStore.Deserialize(text.Replace("{ \"StartMs\": 5000, \"EndMs\": 12000, \"Skipped\": false }", "{ \"Start\": \"00:00:05.500\", \"End\": \"00:00:12\" }"));
        Expect(problems, asTimes.Segments.Count == 2 && asTimes.Segments[0] == new SegmentState(5500, 12000), $"cuts written as times come over ({string.Join(", ", asTimes.Segments)})");

        var written = ProjectStore.Serialize(state);
        problems.AddRange(Compare(state, ProjectStore.Deserialize(written), "migrated state"));
        return new GraphResult("hproj-format1-migrated", "# The HandPeg 1.x fixture (fixtures/format1.txt) brought over and written as this version writes it.\n\n" + Pretty(written), problems, 0);
    }

    private static GraphResult NewerFormat(string fixtures, string scratch)
    {
        var problems = new List<string>();
        var newer = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "format2.hproj")))!.AsObject();
        newer["version"] = 4;
        newer["writtenBy"] = "HandPeg 9.9.9";
        newer["fromTheFuture"] = new JsonObject { ["something"] = "this version does not know" };
        var text = newer.ToJsonString();

        try
        {
            ProjectStore.Deserialize(text);
            problems.Add("a format 4 project was read without being asked to");
        }
        catch (NewerProjectFormatException refused)
        {
            Expect(problems, refused is { Version: 4, WrittenBy: "HandPeg 9.9.9" }, "the refusal says which format and which version wrote it");
            Expect(problems, refused.Message.Contains("HandPeg 9.9.9", StringComparison.Ordinal) && refused.Message.Contains("format 4", StringComparison.Ordinal),
                $"the message names both: {refused.Message}");
        }

        var opened = ProjectStore.Deserialize(text, allowNewer: true);
        problems.AddRange(Compare(ProjectStore.Deserialize(File.ReadAllText(Path.Combine(fixtures, "format2.hproj"))), opened, "opened anyway"));
        Expect(problems, ProjectStore.IsProject(newer), "the backup importer takes a newer project as one (copied unchanged; it asks before it opens)");

        // On disk: what it says it is is read from its first bytes, and saving over it is refused, while Save As elsewhere is not.
        Directory.CreateDirectory(scratch);
        var file = Path.Combine(scratch, "newer.hproj");
        File.WriteAllText(file, text);
        Expect(problems, ProjectStore.PeekHeader(file) == (ProjectFile.FormatName, 4, "HandPeg 9.9.9"), $"the header is read from the file ({ProjectStore.PeekHeader(file)})");
        try
        {
            ProjectStore.SaveTo(opened, file);
            problems.Add("a format 4 project was written over");
        }
        catch (IOException refused)
        {
            Expect(problems, refused.Message.Contains("Save As", StringComparison.Ordinal), $"the refusal points to Save As: {refused.Message}");
        }

        Expect(problems, File.ReadAllText(file) == text, "the newer file is left exactly as it was");
        var elsewhere = Path.Combine(scratch, "saved elsewhere.hproj");
        ProjectStore.SaveTo(opened, elsewhere);
        Expect(problems, ProjectStore.PeekHeader(elsewhere) is { Version: 3 }, "saved under another name, it is a format 3 project");

        // A format 2 or 3 file is saved over as it always was.
        ProjectStore.SaveTo(opened, elsewhere);
        return new GraphResult("hproj-newer-format", "# A format 4 file: refused unless asked, opened as far as known, never written over.\n", problems, 0);
    }

    private static GraphResult NotProjects()
    {
        var problems = new List<string>();
        foreach (var (text, what) in new[]
                 {
                     ("{\"name\":\"shopping\",\"items\":[\"milk\",\"bread\"]}", "a JSON file that is not a project"),
                     ("{\"format\":\"handpeg-project\",\"version\":1,\"name\":\"x\"}", "a project marked format 1"),
                     ("{\"SourcePath\":\"D:/a.mkv\",\"Settings\":{},\"Notes\":[]}", "a text file with some of a 1.x project's names"),
                 })
        {
            try
            {
                ProjectStore.Deserialize(text);
                problems.Add($"{what} was read as a project");
            }
            catch (InvalidDataException)
            {
            }
        }

        Expect(problems, !LegacyProject.IsLegacy(JsonNode.Parse("{\"SourcePath\":\"D:/a.mkv\",\"Settings\":{},\"Notes\":[]}")!.AsObject()), "a partial match is not a 1.x project");
        return new GraphResult("hproj-not-projects", "# Files that are not projects this version reads are refused with a reason.\n", problems, 0);
    }

    private static GraphResult SeekIndexes(string scratch)
    {
        var problems = new List<string>();
        Directory.CreateDirectory(scratch);

        // Written: the seek facts known for a project's video files go into the file, always.
        var written = Path.Combine(scratch, "written.mp4");
        File.WriteAllBytes(written, new byte[4096]);
        var info = new FileInfo(written);
        var iframes = new[] { 0.0, 2.002, 4.004, 6.006 };
        Expect(problems, MediaProbe.Adopt(new SeekFacts(written, info.Length, info.LastWriteTimeUtc.Ticks, 0.0, 0.0, 8.0, iframes)), "the facts of an existing file are adopted");
        var state = new ProjectState { Name = "Seek", SourcePath = written, LocalMediaPath = written };
        var json = JsonNode.Parse(ProjectStore.Serialize(state))!.AsObject();
        var kept = json["seekIndexes"]?.AsArray().FirstOrDefault()?.AsObject();
        Expect(problems, kept is not null && kept["length"]?.GetValue<long>() == info.Length && MediaProbe.UnpackTimes(kept["iFrames"]!.GetValue<string>()).SequenceEqual(iframes),
            "the project carries the file's I-frames, with its size");

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

    /// <summary>Every value the first JSON holds is in the second, at the same place (the second may hold more).</summary>
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
                            yield return $"{path}.{key}: in the format 2 file, not in the format 3 one";
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
                    yield return $"{path}: {arrayExpected.Count} items in the format 2 file, {arrayActual.Count} in the format 3 one";
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
                    yield return $"{path}: {valueExpected.ToJsonString()} in the format 2 file, {valueActual.ToJsonString()} in the format 3 one";
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
        || (a.TryGetValue<string>(out var s) && b.TryGetValue<string>(out var t) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ds)
            && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) && ds == dt);

    /// <summary>A project's JSON indented, a value to a line, with the writer's version as a placeholder.</summary>
    private static string Pretty(string json)
    {
        var node = JsonNode.Parse(json)!;
        if (node is JsonObject root && root["writtenBy"] is not null)
            root["writtenBy"] = WriterPlaceholder;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}
