using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

/// <summary>A saved project as listed in the project manager.</summary>
/// <param name="FilePath">The project file itself.</param>
/// <param name="SourceName">File name of the video the project was made from.</param>
/// <param name="SourcePath">Full path (or URL) of that video.</param>
public sealed record ProjectEntry(string FilePath, string Name, string SourceName, string SourcePath, DateTime SavedAt)
{
    public string SavedText => $"Saved {SavedAt:g}";
}

// ----- The project file (.hproj), format 5 -----
// A project is its timeline, a TimelineDocument (every track and clip, the main video's among them, the markers and the
// Sequence Output's effects), and what lies around it: where its video comes from, where the export goes, what is done
// with each of the main file's own audio streams, the recycle bin, and the export's settings. The file is laid out that
// way, the timeline last. Nothing is written that is still at its default. What says which file it is (the format, its
// version, the HandPeg that wrote it) comes first, so that it is read from the first few kilobytes, and before anything
// else of the file.
//
// Format 5 is HandPeg 4.3.0's, the first whose timeline is the document, and the only one this version opens. Projects
// of formats 1 to 4 (HandPeg 1.x's .txt, and the .hproj of HandPeg 2.0 to 4.2.0, laid out as a cut list, the main video
// and tracks of layers) are refused with the reason, and not converted. A newer format is opened only when the user
// asks, and is never written over.
//
// Since 4.4 a save also carries the undo and redo history (undoFormat, undoHistory, redoHistory, just before the timeline),
// so that Ctrl+Z goes on where the save left it. It is optional and left out when empty, so format 5 stays format 5: a
// version without it reads the file as before. Read leniently: a history of another shape, or one that is not a history
// at all, is let go, never the project.

/// <summary>A project as it is written to disk.</summary>
public sealed class ProjectFile
{
    public const string FormatName = "handpeg-project";

    /// <summary>The format this version writes, and the only one it opens (a newer one too, as far as it knows it, when asked).</summary>
    public const int CurrentVersion = 5;

    // No defaults here: a file that does not say what it is must not pass for one that does. These three come first in the
    // file (its values are written in the order they are declared here), where PeekHeader finds them.
    public string? Format { get; set; }
    public int Version { get; set; }

    /// <summary>The HandPeg that wrote the file ("HandPeg 4.3.0"). Named when a version too old for the file refuses it.</summary>
    public string? WrittenBy { get; set; }

    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public ProjectSource Source { get; set; } = new();
    public ProjectOutput Output { get; set; } = new();

    /// <summary>What of the sequence is not on its timeline: the main file's audio streams, the recycle bin, the captions' sound.</summary>
    public ProjectSequence Sequence { get; set; } = new();

    /// <summary>Everything that is not on the timeline: the frame, the encoders, the filters, the caption style.</summary>
    public EncodingPreset Settings { get; set; } = new();

    /// <summary>
    /// The mask pictures the clips, the tracks and the caption box use, each as Base64 under the name their looks give it:
    /// carried inside the project, so that the file is all another machine needs of them. Absent when nothing has a mask.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Masks { get; set; }

    /// <summary>
    /// What is known about seeking in the project's video files (the main video and every clip's video file), as it was at
    /// the save: taken up again on opening, for each file still exactly as it was, so the first graph can seek in them before
    /// anything is probed. Absent when nothing was known.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectSeekIndex>? SeekIndexes { get; set; }

    /// <summary>
    /// The shape of the undo history's steps this version writes and reads (the view model's TimelineSnapshot as JSON, packed:
    /// UndoSteps). Bump it when that changes shape incompatibly: a history of another shape is let go on opening.
    /// </summary>
    public const int CurrentUndoFormat = 1;

    /// <summary>The most steps of each history a file carries (Undo keeps twenty).</summary>
    public const int MaximumUndoSteps = 20;

    /// <summary>The shape the undo history's steps are in (<see cref="CurrentUndoFormat"/> as written); left out (0) with no history.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), JsonConverter(typeof(LenientIntConverter))]
    public int UndoFormat { get; set; }

    /// <summary>
    /// The undo history at the save (4.4): oldest first, at most twenty steps, each its name and the timeline it puts back as
    /// Undo keeps it (packed, Base64). Optional and left out when empty; read leniently (<see cref="UndoStepsConverter"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(UndoStepsConverter))]
    public List<ProjectUndoStep>? UndoHistory { get; set; }

    /// <summary>The redo history at the save, as <see cref="UndoHistory"/> is kept: the step Redo puts back first is the last.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(UndoStepsConverter))]
    public List<ProjectUndoStep>? RedoHistory { get; set; }

    /// <summary>
    /// The timeline: every track and clip (the main video's among them), the markers and the Sequence Output's effects.
    /// Last in the file, and required: a project without one is refused.
    /// </summary>
    public TimelineDocument? Timeline { get; set; }
}

/// <summary>One step of a project's undo history as the file carries it: its name ("delete Logo"), and the timeline it puts back, packed as Undo keeps it, in Base64.</summary>
public sealed class ProjectUndoStep
{
    public string What { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>
/// Reads an undo history leniently (4.4): a history is never the reason a project cannot be opened. What is not a list
/// reads as none, and an entry that is not a step (an object whose "what" and "state" are text) is passed over. Written as
/// a list of steps.
/// </summary>
internal sealed class UndoStepsConverter : JsonConverter<List<ProjectUndoStep>>
{
    public override List<ProjectUndoStep>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var steps = new List<ProjectUndoStep>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("what", out var what) && what.ValueKind == JsonValueKind.String
                && entry.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
            {
                steps.Add(new ProjectUndoStep { What = what.GetString() ?? "", State = state.GetString() ?? "" });
            }
        }

        return steps;
    }

    public override void Write(Utf8JsonWriter writer, List<ProjectUndoStep> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var step in value)
        {
            writer.WriteStartObject();
            writer.WriteString("what", step.What);
            writer.WriteString("state", step.State);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>Reads a whole number leniently: anything else (text, a list, an object) reads as 0, and never fails the file.</summary>
internal sealed class LenientIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            return number;

        // An object or a list is stepped over whole; any other value is one token, read already.
        reader.Skip();
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>One video file's seek facts in a project (see <see cref="SeekFacts"/>): the file as it was (size, last write) and where its I-frames are.</summary>
public sealed class ProjectSeekIndex
{
    public string Path { get; set; } = "";
    public long Length { get; set; }

    /// <summary>When the file was last written, in UTC ticks.</summary>
    public long Written { get; set; }

    public double ContainerStart { get; set; }
    public double VideoStart { get; set; }
    public double Duration { get; set; }

    /// <summary>The I-frames' times on the file's own clock: Base64 of the doubles, little-endian (see MediaProbe.PackTimes).</summary>
    public string IFrames { get; set; } = "";
}

public sealed class ProjectSource
{
    public string Path { get; set; } = "";

    /// <summary>The file actually read, when it is not the path itself: the download behind a web address.</summary>
    public string LocalPath { get; set; } = "";

    public string DownloadResolution { get; set; } = "Best";
    public bool DownloadSubtitles { get; set; }
}

public sealed class ProjectOutput
{
    public string Path { get; set; } = "";
    public string Container { get; set; } = "mp4";
    public bool WebOptimized { get; set; }
    public bool ChapterMarkers { get; set; } = true;

    /// <summary>A chapter where each clip of the main video starts (4.3C; until then, at each kept stretch of the cut list).</summary>
    public bool ChaptersAtCuts { get; set; }

    public bool ChaptersAtMarkers { get; set; }
    public string TargetFileSize { get; set; } = "";
    public string? ManualCommand { get; set; }

    /// <summary>The subtitle track (imported, or edited by hand): its cues, its offset, where it came from. Left out when there is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubtitleTrackData? Subtitles { get; set; }
}

/// <summary>What of the sequence is not on its timeline.</summary>
public sealed class ProjectSequence
{
    /// <summary>Splits and the playhead keep to the main video's I-frames.</summary>
    public bool SnapToIFrames { get; set; }

    /// <summary>The blurred background behind the main video is switched off, leaving the black canvas.</summary>
    public bool BackgroundHidden { get; set; }

    /// <summary>
    /// The main video's own audio streams, each as it is heard wherever the main video's clips are: what is done with it in
    /// the output (copied, encoded again, dropped; its codec, bitrate and title), its place in the mix (its gain, filters,
    /// ducking, and whether it is the voice), and its edits (its slip and its silenced stretches, in the file's own seconds).
    /// </summary>
    public List<AudioTrackState> Audio { get; set; } = [];

    public List<SubtitleTrackState> Subtitles { get; set; } = [];

    /// <summary>The recycle bin: clips deleted from the timeline, as they were, with the track each was on.</summary>
    public List<DeletedClip> Deleted { get; set; } = [];

    public string CaptionAudioPath { get; set; } = "";
    public bool CaptionUseExternalAudio { get; set; }
    public int CaptionAudioTrack { get; set; }
}

/// <summary>
/// A project file of a newer format than this version reads. It can still be opened when the user asks (reading what this
/// version knows of it), but is never written over: what the newer version put in it would be lost. An IOException, so that
/// whatever opens or lists projects reports it as a file it could not read.
/// </summary>
public sealed class NewerProjectFormatException(int version, string? writtenBy)
    : IOException($"This project was saved by {writtenBy ?? "a newer version of HandPeg"} in project format {version}; this version of HandPeg "
                           + $"({ProjectStore.WriterName}) reads format {ProjectFile.CurrentVersion}.")
{
    public int Version { get; } = version;
    public string? WrittenBy { get; } = writtenBy;
}

/// <summary>Project files live in the Projects folder (see <see cref="AppPaths"/>), as .hproj: JSON in the layout above, format 5.</summary>
public static class ProjectStore
{
    public const string Extension = ".hproj";

    /// <summary>The name written into each project as the HandPeg that wrote it: "HandPeg 4.3.0".</summary>
    public static string WriterName { get; } = typeof(ProjectStore).Assembly.GetName().Version is { } version
        ? $"HandPeg {version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}"
        : "HandPeg";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipDefaults } },
    };

    public static string Folder => AppPaths.Projects;

    /// <summary>Raised when projects were added to the folder from outside the project manager, so an open list can refresh.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Leaves out of the file whatever is as a new object of its kind would have it: zero, false, empty, or
    /// the value the property starts with (an opacity of 100, a contrast of 1). Reading puts the same values back.
    /// </summary>
    private static void SkipDefaults(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Type == typeof(ProjectFile))
            return;

        object? fresh = null;
        try
        {
            if (info.Type.GetConstructor(Type.EmptyTypes) is not null)
                fresh = Activator.CreateInstance(info.Type);
            else if (ReadingConstructor(info.Type) is { } constructor)
                fresh = constructor.Invoke(constructor.GetParameters().Select(DefaultArgument).ToArray());
        }
        catch (Exception ex) when (ex is MissingMethodException or System.Reflection.TargetInvocationException or ArgumentException)
        {
        }

        foreach (var property in info.Properties)
        {
            if (property.Get is not { } get)
                continue;

            var usual = fresh is not null ? get(fresh) : property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
            property.ShouldSerialize = (_, value) => value switch
            {
                null => false,
                string text => text != usual as string,
                System.Collections.ICollection { Count: 0 } => false,
                System.Collections.ICollection => true,
                _ => !value.Equals(usual),
            };
        }
    }

    /// <summary>
    /// The constructor a value is read through, when it has one whose parameters can all be left out: what it makes of
    /// nothing is what a value left out of the file reads as. A keyframe read without its easing is Ease In-Out (its
    /// constructor's default), so Ease In-Out is what is left out, and Linear is written.
    /// </summary>
    private static System.Reflection.ConstructorInfo? ReadingConstructor(Type type) =>
        type.GetConstructors().FirstOrDefault(c => c.IsDefined(typeof(JsonConstructorAttribute), inherit: false)
                                                   && c.GetParameters().All(p => p.HasDefaultValue || p.ParameterType.IsValueType));

    private static object? DefaultArgument(System.Reflection.ParameterInfo parameter) =>
        !parameter.HasDefaultValue ? Activator.CreateInstance(parameter.ParameterType)
        : parameter.ParameterType.IsEnum && parameter.DefaultValue is not null ? Enum.ToObject(parameter.ParameterType, parameter.DefaultValue)
        : parameter.DefaultValue;

    /// <summary>Saves the state under its name, replacing a project of the same name.</summary>
    public static string Save(ProjectState state)
    {
        Directory.CreateDirectory(Folder);

        var fileName = string.Concat(state.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (fileName.Length == 0)
            fileName = "Project";

        var path = Path.Combine(Folder, fileName + Extension);
        RefuseNewer(path);
        File.WriteAllText(path, Serialize(state));
        return path;
    }

    /// <summary>What an autosave's file name ends in, before the extension: "My Project.autosave.hproj".</summary>
    public const string AutosaveSuffix = ".autosave";

    /// <summary>Saves the state to a file of the user's choosing, wherever it is.</summary>
    public static string SaveTo(ProjectState state, string path)
    {
        RefuseNewer(path);
        WriteWhole(path, Serialize(state));
        return path;
    }

    /// <summary>
    /// Refuses to write over a project file of a newer format than this version writes, however it was opened: what the
    /// newer HandPeg put in it would be lost. An IOException, so that every save reports it as a save that could not be made.
    /// </summary>
    private static void RefuseNewer(string path)
    {
        if (PeekHeader(path) is { Format: ProjectFile.FormatName, Version: > ProjectFile.CurrentVersion } header)
        {
            throw new IOException($"{Path.GetFileName(path)} was saved by {header.WrittenBy ?? "a newer version of HandPeg"} in project format {header.Version}, "
                                  + "and saving over it would lose what that version put in it. Save the project under another name (Save As).");
        }
    }

    /// <summary>
    /// What a project file says it is (format, version, writer), read from its first few kilobytes: they come first in
    /// the file, so a project carrying megabytes of masks is not read whole. Null when there is no such file or it does
    /// not start like a project.
    /// </summary>
    public static (string? Format, int Version, string? WrittenBy)? PeekHeader(string path)
    {
        byte[] buffer;
        int read;
        try
        {
            if (!File.Exists(path))
                return null;

            buffer = new byte[8192];
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ReadHeader(buffer.AsSpan(0, read), isFinalBlock: read < buffer.Length);
    }

    /// <summary>
    /// What a project's text says it is (format, version, writer): the plain values of its top level. An object or a list
    /// among them (the source, say) is stepped over while it lies whole in <paramref name="bytes"/>; one that runs past them
    /// ends the search, with what was found so far, and so does finding all three. Null when the text does not start like
    /// a project (an object that names its format).
    /// </summary>
    /// <param name="isFinalBlock">Whether <paramref name="bytes"/> are the whole text, not only its start.</param>
    private static (string? Format, int Version, string? WrittenBy)? ReadHeader(ReadOnlySpan<byte> bytes, bool isFinalBlock)
    {
        if (bytes.StartsWith("﻿"u8))
            bytes = bytes[3..];

        var (format, version, writtenBy) = ((string?)null, 0, (string?)null);
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read())
                    break;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                        break;
                    continue;
                }

                switch (name)
                {
                    case "format" when reader.TokenType == JsonTokenType.String:
                        format = reader.GetString();
                        break;
                    case "version" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number):
                        version = number;
                        break;
                    case "writtenBy" when reader.TokenType == JsonTokenType.String:
                        writtenBy = reader.GetString();
                        break;
                }

                if (format is not null && version != 0 && writtenBy is not null)
                    break;
            }
        }
        catch (JsonException)
        {
            // Cut off in the middle of a value, or not JSON at all: what was read so far stands.
        }

        return format is null ? null : (format, version, writtenBy);
    }

    /// <summary>Where a project's autosave goes: beside the project when it has a file, in the projects folder under its name otherwise.</summary>
    public static string AutosavePath(string projectPath, string name)
    {
        if (projectPath.Length > 0 && Path.GetDirectoryName(projectPath) is { Length: > 0 } folder)
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(projectPath) + AutosaveSuffix + Extension);

        var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(Folder, (fileName.Length > 0 ? fileName : "Untitled") + AutosaveSuffix + Extension);
    }

    /// <summary>Writes a file so that it is either the old one or the new one, never half of each: beside it first, then moved over it.</summary>
    public static void WriteWhole(string path, string content)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        var partial = path + ".tmp";
        File.WriteAllText(partial, content);
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>The state as a format 5 file. The state is left as it was: its timeline is written from a copy of its own.</summary>
    public static string Serialize(ProjectState state) => JsonSerializer.Serialize(ToFile(state), JsonOptions);

    /// <param name="unpackMasks">Write the masks the project carries into the masks folder and point its looks at them, take up the seek indexes of its video files, and read its undo history (4.4): when it is being opened, not when it is only being listed.</param>
    /// <param name="allowNewer">Read a file of a newer format too, as far as this version knows it, instead of refusing it: for when the user asked to open it anyway.</param>
    public static ProjectState Load(string filePath, bool unpackMasks = true, bool allowNewer = false) => Deserialize(File.ReadAllText(filePath), unpackMasks, allowNewer);

    /// <summary>
    /// Reads a format 5 project from its text, its timeline with it (<see cref="ProjectState.Timeline"/>). What the text says
    /// it is is read first, before anything else of it: a file that is not a HandPeg project, or one of formats 1 to 4 (which
    /// are not converted), is refused with <see cref="InvalidDataException"/> saying so, and one of a newer format with
    /// <see cref="NewerProjectFormatException"/> unless <paramref name="allowNewer"/>. A project without its timeline is refused.
    /// </summary>
    public static ProjectState Deserialize(string json, bool unpackMasks = false, bool allowNewer = false)
    {
        // The header first: a file of another format is refused as what it is, not for a part of it this version reads otherwise.
        if (ReadHeader(Encoding.UTF8.GetBytes(json), isFinalBlock: true) is not { Format: ProjectFile.FormatName } header)
            throw new InvalidDataException("This is not a HandPeg project.");
        if (header.Version > ProjectFile.CurrentVersion && !allowNewer)
            throw new NewerProjectFormatException(header.Version, header.WrittenBy);
        if (header.Version < 1)
            throw new InvalidDataException("This HandPeg project does not say which format it is in.");
        if (header.Version < ProjectFile.CurrentVersion)
        {
            var writer = header.WrittenBy is { Length: > 0 } by ? $" (saved by {by})" : "";
            throw new InvalidDataException($"This project is in format {header.Version}{writer}. This version of HandPeg opens format {ProjectFile.CurrentVersion} "
                                           + "projects only: projects of formats 1 to 4 (HandPeg 4.2 and earlier) are not converted.");
        }

        var file = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions) ?? throw new InvalidDataException("The project file is empty.");
        if (file.Timeline is not { } timeline)
            throw new InvalidDataException("This project has no timeline: a format 5 project keeps its tracks and clips there.");
        return FromFile(file, timeline, unpackMasks);
    }

    /// <summary>A timeline as a format 5 file writes it: camel case, enums by name, nothing at its default.</summary>
    public static System.Text.Json.Nodes.JsonNode? TimelineNode(TimelineDocument document) => JsonSerializer.SerializeToNode(document, JsonOptions);

    /// <summary>Whether a piece of JSON says it is a .hproj project of a format this version opens: 5, or a newer one (which asks before it opens).</summary>
    public static bool IsProject(System.Text.Json.Nodes.JsonObject node) =>
        node["format"] is System.Text.Json.Nodes.JsonValue format && format.TryGetValue<string>(out var name) && name == ProjectFile.FormatName
        && node["version"] is System.Text.Json.Nodes.JsonValue version && version.TryGetValue<int>(out var number) && number >= ProjectFile.CurrentVersion;

    /// <summary>Whether a file is one to open as a project (typed in, dropped, chosen): a .hproj that is there.</summary>
    public static bool IsProjectFile(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    // ----- Between the window's state and the file -----

    /// <summary>A copy of a clip that is its own, its look too: one the file can point at a carried mask without changing the state's.</summary>
    private static TimelineClip CopyOf(TimelineClip clip)
    {
        var copy = clip.Snapshot();
        copy.State = TimelineTransaction.CopyOf(clip.State);
        return copy;
    }

    /// <summary>
    /// Everything in a project file that can carry a mask: each track's shared look, each clip's look (on the timeline and
    /// in the recycle bin), and the caption box.
    /// </summary>
    private static IEnumerable<LayerState> MaskedLooks(ProjectFile file, TimelineDocument timeline)
    {
        foreach (var track in timeline.Tracks)
        {
            if (track.TrackLook is { } shared)
                yield return shared;
            foreach (var clip in track.Clips)
            {
                if (clip.State is { } look)
                    yield return look;
            }
        }

        foreach (var deleted in file.Sequence.Deleted ?? [])
        {
            if (deleted?.Clip?.State is { } look)
                yield return look;
        }

        if (file.Settings.CaptionLayer is { } captions)
            yield return captions;
    }

    private static ProjectFile ToFile(ProjectState state)
    {
        var settings = state.Settings;

        // The settings without what the timeline and the sequence hold (the layers, the main video's look and place, its
        // background and its streams, the Sequence Output's effects): each is written once, where it is kept.
        var rest = JsonSerializer.Deserialize<EncodingPreset>(JsonSerializer.Serialize(settings))!;
        (rest.Name, rest.Layers, rest.MainLayer, rest.MainVideoIndex, rest.BackgroundHidden, rest.AudioTracks) = ("", [], null, 0, false, []);
        (rest.SequenceEffects, rest.SequenceEffectsBypassed) = (null, false);

        // The file's own timeline, its looks too: they are pointed at the masks carried with them (below), while the state's
        // (the window's, a queued job's) go on naming the files on the disk.
        var timeline = state.Timeline.Clone(deep: true);
        var file = new ProjectFile
        {
            Format = ProjectFile.FormatName,
            Version = ProjectFile.CurrentVersion,
            WrittenBy = WriterName,
            Name = state.Name,
            SavedAt = state.SavedAt,
            Source = new ProjectSource
            {
                Path = state.SourcePath,
                LocalPath = state.LocalMediaPath == state.SourcePath ? "" : state.LocalMediaPath,
                DownloadResolution = state.DownloadResolution,
                DownloadSubtitles = state.DownloadSubtitles,
            },
            Output = new ProjectOutput
            {
                Path = state.DestinationPath,
                Container = state.Container,
                WebOptimized = state.WebOptimized,
                ChapterMarkers = state.ChapterMarkers,
                ChaptersAtCuts = state.ChaptersAtCuts,
                ChaptersAtMarkers = state.ChaptersAtMarkers,
                Subtitles = state.Subtitles,
                TargetFileSize = state.TargetFileSize,
                ManualCommand = state.ManualCommand,
            },
            Sequence = new ProjectSequence
            {
                SnapToIFrames = state.SnapToIFrames,
                BackgroundHidden = settings.BackgroundHidden,
                Audio = state.AudioTracks,
                Subtitles = state.SubtitleTracks,

                // The bin's clips are the file's own copies too, as the timeline is.
                Deleted = [.. state.Deleted.Where(d => d?.Clip is not null).Select(d => d with { Clip = CopyOf(d.Clip) })],
                CaptionAudioPath = state.CaptionAudioPath,
                CaptionUseExternalAudio = state.CaptionUseExternalAudio ?? false,
                CaptionAudioTrack = state.CaptionAudioTrackIndex,
            },
            Settings = rest,
            Timeline = timeline,
        };

        // The looks are the file's own copies: they are pointed at the masks carried with them, not at the disk.
        var masks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var look in MaskedLooks(file, timeline))
            MaskStore.Pack(look, masks);
        file.Masks = masks.Count > 0 ? masks : null;

        var seeks = VideoFiles(state).Select(MediaProbe.KnownFacts).OfType<SeekFacts>().Select(f => new ProjectSeekIndex
        {
            Path = f.Path, Length = f.Length, Written = f.WrittenTicks, ContainerStart = f.ContainerStart, VideoStart = f.VideoStart, Duration = f.Duration,
            IFrames = MediaProbe.PackTimes(f.IFrames),
        }).ToList();
        file.SeekIndexes = seeks.Count > 0 ? seeks : null;

        // The undo history, when the state carries one (a save's, 4.4): with the shape of its steps. None is written otherwise.
        if (state.UndoHistory is { Count: > 0 } || state.RedoHistory is { Count: > 0 })
        {
            file.UndoFormat = ProjectFile.CurrentUndoFormat;
            (file.UndoHistory, file.RedoHistory) = (StepsOf(state.UndoHistory), StepsOf(state.RedoHistory));
        }

        return file;
    }

    /// <summary>A history as the file carries it: its newest twenty steps, oldest first, each in Base64; null when empty.</summary>
    private static List<ProjectUndoStep>? StepsOf(List<UndoStepState>? steps) =>
        steps is { Count: > 0 }
            ? [.. steps.Where(s => s is { State.Length: > 0 }).TakeLast(ProjectFile.MaximumUndoSteps).Select(s => new ProjectUndoStep { What = s.What, State = Convert.ToBase64String(s.State) })]
            : null;

    /// <summary>
    /// The undo and redo history a project carries (4.4), as the view model keeps them: only one whose steps are of the shape
    /// this version writes (another is let go, and logged), each step's Base64 read back (one that cannot be is passed over),
    /// the newest twenty of each. Never the reason a project cannot be opened: a step that turns out unreadable is let go
    /// when it is stepped to (MainViewModel.Step).
    /// </summary>
    private static (List<UndoStepState>? Undo, List<UndoStepState>? Redo) HistoryOf(ProjectFile file)
    {
        if (file.UndoHistory is not { Count: > 0 } && file.RedoHistory is not { Count: > 0 })
            return (null, null);

        if (file.UndoFormat != ProjectFile.CurrentUndoFormat)
        {
            AppLog.Write($"The project's undo history is of format {file.UndoFormat}, which this version does not read ({ProjectFile.CurrentUndoFormat}): let go");
            return (null, null);
        }

        var unreadable = 0;
        List<UndoStepState>? Read(List<ProjectUndoStep>? steps)
        {
            var read = new List<UndoStepState>();
            foreach (var step in steps ?? [])
            {
                try
                {
                    if (Convert.FromBase64String(step.State) is { Length: > 0 } packed)
                    {
                        read.Add(new UndoStepState(step.What, packed));
                        continue;
                    }
                }
                catch (FormatException)
                {
                }

                unreadable++;
            }

            return read.Count > 0 ? [.. read.TakeLast(ProjectFile.MaximumUndoSteps)] : null;
        }

        var history = (Read(file.UndoHistory), Read(file.RedoHistory));
        if (unreadable > 0)
            AppLog.Write($"{unreadable} step(s) of the project's undo history could not be read, and were let go");
        return history;
    }

    /// <summary>The video files a project plays: the main video, and the file of every clip that plays one (the main video's clips, video files), once each.</summary>
    private static IEnumerable<string> VideoFiles(ProjectState state) =>
        state.Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.ContentKind is LayerKind.VideoFile or LayerKind.MainVideo).Select(c => c.SourcePath)
            .Prepend(state.LocalMediaPath)
            .Select(p => (p ?? "").Trim().Trim('"')).Where(p => p.Length > 0 && !p.Contains("://", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the project's video files ready to be seeked in, as the project is opened and before anything is built from
    /// it: from the facts the project carries, for each file still exactly as it was; else from the Timeline cache.
    /// Logged: how many were ready, and from where.
    /// </summary>
    private static void TakeUpSeekIndexes(ProjectFile file, ProjectState state)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (carried, cached, missing) = (0, 0, 0);
        var adopted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seek in file.SeekIndexes ?? [])
        {
            if (MediaProbe.Adopt(new SeekFacts(seek.Path, seek.Length, seek.Written, seek.ContainerStart, seek.VideoStart, seek.Duration, MediaProbe.UnpackTimes(seek.IFrames))))
                adopted.Add(seek.Path);
        }

        foreach (var path in VideoFiles(state))
        {
            if (adopted.Contains(Path.GetFullPath(path)))
                carried++;
            else if (MediaProbe.Recall(path))
                cached++;
            else
                missing++;
        }

        AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Seek indexes on opening {file.Name}: {carried} from the project, {cached} from the cache, {missing} still to learn ({System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms)"));
    }

    private static ProjectState FromFile(ProjectFile file, TimelineDocument timeline, bool unpackMasks)
    {
        // The masks the project carries go into the masks folder, and its looks are pointed at them there.
        if (unpackMasks && file.Masks is { Count: > 0 } masks)
        {
            foreach (var look in MaskedLooks(file, timeline))
            {
                if (MaskStore.Unpack(look, masks) is { } problem)
                    AppLog.Write(problem);
            }
        }

        // The settings get back what the sequence holds for them; the layers, the main video's look and the Sequence
        // Output's effects are the timeline's (a clip's look, a track's, the document's effects), and stay empty here.
        var sequence = file.Sequence;
        var audio = sequence.Audio ?? [];
        var settings = file.Settings;
        settings.Name = file.Name;
        (settings.BackgroundHidden, settings.AudioTracks) = (sequence.BackgroundHidden, audio);

        var state = new ProjectState
        {
            Name = file.Name,
            SavedAt = file.SavedAt,
            SourcePath = file.Source.Path,
            LocalMediaPath = file.Source.LocalPath.Length > 0 ? file.Source.LocalPath : file.Source.Path,
            DownloadResolution = file.Source.DownloadResolution,
            DownloadSubtitles = file.Source.DownloadSubtitles,
            DestinationPath = file.Output.Path,
            Container = file.Output.Container,
            WebOptimized = file.Output.WebOptimized,
            ChapterMarkers = file.Output.ChapterMarkers,
            ChaptersAtCuts = file.Output.ChaptersAtCuts,
            ChaptersAtMarkers = file.Output.ChaptersAtMarkers,
            Subtitles = file.Output.Subtitles,
            TargetFileSize = file.Output.TargetFileSize,
            ManualCommand = file.Output.ManualCommand,
            Timeline = timeline,
            SnapToIFrames = sequence.SnapToIFrames,

            // An entry of another shape (a clip deleted in 4.2.0, left in a project translated by hand) has no clip to put back.
            Deleted = [.. (sequence.Deleted ?? []).Where(d => d?.Clip is not null)],
            Settings = settings,
            AudioTracks = audio,
            SubtitleTracks = sequence.Subtitles ?? [],
            CaptionAudioPath = sequence.CaptionAudioPath,
            CaptionUseExternalAudio = sequence.CaptionUseExternalAudio,
            CaptionAudioTrackIndex = sequence.CaptionAudioTrack,
        };

        // Opened (not only listed): its video files are made ready to be seeked in now, before the first graph is built, and
        // the undo history it carries is read (4.4).
        if (unpackMasks)
        {
            TakeUpSeekIndexes(file, state);
            (state.UndoHistory, state.RedoHistory) = HistoryOf(file);
        }

        return state;
    }

    /// <summary>
    /// Saved projects (the .hproj files), most recently saved first: format 5's, and a newer format's (which asks before it
    /// opens). Files that cannot be read are left out, and so are projects of formats 2 to 4, which this version does not open.
    /// </summary>
    public static List<ProjectEntry> ListRecent()
    {
        var entries = new List<ProjectEntry>();
        if (!Directory.Exists(Folder))
            return entries;

        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTime))
        {
            try
            {
                var state = Load(file.FullName, unpackMasks: false, allowNewer: true);
                var isUrl = Uri.TryCreate(state.SourcePath, UriKind.Absolute, out var uri) && !uri.IsFile;
                entries.Add(new ProjectEntry(
                    file.FullName,
                    Path.GetFileNameWithoutExtension(file.Name),
                    isUrl ? Path.GetFileName(state.LocalMediaPath) : Path.GetFileName(state.SourcePath),
                    state.SourcePath,
                    file.LastWriteTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
            }
        }

        return entries;
    }
}
