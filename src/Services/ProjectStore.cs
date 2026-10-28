using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>A saved project as listed in the project manager.</summary>
/// <param name="FilePath">The project file itself.</param>
/// <param name="SourceName">File name of the video the project was made from.</param>
/// <param name="SourcePath">Full path (or URL) of that video.</param>
public sealed record ProjectEntry(string FilePath, string Name, string SourceName, string SourcePath, DateTime SavedAt)
{
    public string SavedText => $"Saved {SavedAt:g}";
}

// ----- The project file (.hproj), format 3 -----
// A project is a sequence: cuts, the main video's clip, and tracks of clips. The file is laid out that way.
// What a track's clips share (what they show, their look, their filters) is written once, on the track; a clip
// carries only what is its own: when it is on, where it sits, and its keyframes. Nothing is written that is
// still at its default, and times and keyframes are plain arrays of numbers.
//
// The formats: 1 was HandPeg 1.x's project, a .txt holding the window's state in another layout (read, as far as it
// still maps, by LegacyProject). 2 is this layout, written by HandPeg 2.0 to 4.0. 3 (HandPeg 4.1) is the same layout
// with the name of the HandPeg that wrote it, so that an older HandPeg handed a newer file can say which version it
// needs. 4 (HandPeg 4.1.1) adds the export's sample rate and channels and the 3D drop shadow (distance, angle,
// softness, colour, in place of the offset). 5 (HandPeg 4.2.0) adds a layer's Mask Offset (MaskOffsetX / MaskOffsetY,
// left out at 0), which an older HandPeg would drop, showing the mask un-nudged. 2 to 5 are read; a newer format is
// opened only when the user asks, and is never written over. The TimelineDocument (v4.3.0) takes the next format.

/// <summary>A project as it is written to disk.</summary>
public sealed class ProjectFile
{
    public const string FormatName = "handpeg-project";

    /// <summary>The format this version writes.</summary>
    public const int CurrentVersion = 5;

    /// <summary>The oldest format of .hproj this version reads: 2, the first in this layout.</summary>
    public const int OldestVersion = 2;

    // No defaults here: a file that does not say what it is must not pass for one that does.
    public string? Format { get; set; }
    public int Version { get; set; }

    /// <summary>The HandPeg that wrote the file ("HandPeg 4.1.0"); absent in format 2. Named when a version too old for the file refuses it.</summary>
    public string? WrittenBy { get; set; }

    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public ProjectSource Source { get; set; } = new();
    public ProjectOutput Output { get; set; } = new();
    public ProjectSequence Sequence { get; set; } = new();

    /// <summary>Everything that is not on the timeline: the frame, the encoders, the filters, the caption style.</summary>
    public EncodingPreset Settings { get; set; } = new();

    /// <summary>
    /// The mask pictures the layers use, each as Base64 under the name the layers give it: carried inside the
    /// project, so that the file is all another machine needs of them. Absent when no layer has a mask.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Masks { get; set; }

    /// <summary>
    /// What is known about seeking in the project's video files (the main video and the video layers), as it was at the
    /// save: taken up again on opening, for each file still exactly as it was, so the first graph can seek in them before
    /// anything is probed. Absent when nothing was known.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectSeekIndex>? SeekIndexes { get; set; }
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
    public bool ChaptersAtCuts { get; set; }
    public bool ChaptersAtMarkers { get; set; }
    public string TargetFileSize { get; set; } = "";
    public string? ManualCommand { get; set; }

    /// <summary>The subtitle track (imported, or edited by hand): its cues, its offset, where it came from. Left out when there is none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SubtitleTrackData? Subtitles { get; set; }
}

public sealed class ProjectSequence
{
    /// <summary>The kept stretches of the timeline, each as [start, end] in milliseconds; a third number of 1 marks one that is skipped.</summary>
    public List<double[]> Cuts { get; set; } = [];

    /// <summary>The markers: each its moment in seconds and its name.</summary>
    public List<TimelineMarker> Markers { get; set; } = [];

    public bool SnapToIFrames { get; set; }
    public bool AudioLinked { get; set; } = true;

    /// <summary>The main video: the one clip of its track, and how that track looks.</summary>
    public ProjectTrack Main { get; set; } = new();

    /// <summary>How many of the tracks lie under the main video.</summary>
    public int MainIndex { get; set; }

    public bool BackgroundHidden { get; set; }

    /// <summary>The tracks, bottom first; sounds after the pictures.</summary>
    public List<ProjectTrack> Tracks { get; set; } = [];

    /// <summary>What is done with each audio track of the main video.</summary>
    public List<AudioTrackState> Audio { get; set; } = [];

    public List<SubtitleTrackState> Subtitles { get; set; } = [];

    /// <summary>The recycle bin: clips deleted from the timeline, as they were.</summary>
    public List<DeletedClip> Deleted { get; set; } = [];

    public string CaptionAudioPath { get; set; } = "";
    public bool CaptionUseExternalAudio { get; set; }
    public int CaptionAudioTrack { get; set; }
}

public sealed class ProjectTrack
{
    public int Id { get; set; }

    /// <summary>What every clip of the track has in common. Its timing, place and keyframes are left blank: those are the clips'.</summary>
    public LayerState Look { get; set; } = new();

    public List<ProjectClip> Clips { get; set; } = [];
}

/// <summary>The file a clip plays, when it is not its track's: clips of different files can follow each other on one track.</summary>
public sealed class ProjectClipFile
{
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double MediaDuration { get; set; }
    public bool HasAudio { get; set; }
    public string AudioCodec { get; set; } = "";
    public string AudioChannels { get; set; } = "";
}

public sealed class ProjectClip
{
    public string? Name { get; set; }

    /// <summary>The clip's own file; null for the track's (its look's).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProjectClipFile? File { get; set; }

    public double Start { get; set; }
    public double Duration { get; set; }
    public double Offset { get; set; }

    /// <summary>How many times as fast as recorded the clip runs; left out of the file at 1.</summary>
    public double Speed { get; set; } = 1;
    public bool Hidden { get; set; }

    /// <summary>Live Preview's proxy for the clip's file: Make or Original; left out for Auto.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ProxyChoice Proxy { get; set; }
    public double AudioOffset { get; set; }

    // Its fades in and out (picture and sound), and a crossfade from the clip before it (seconds); left out while 0.
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public double Transition { get; set; }

    // How its picture comes on and goes over those fades; left out for a dissolve.
    public TransitionStyle FadeInStyle { get; set; }
    public TransitionStyle FadeOutStyle { get; set; }

    /// <summary>Where it sits on the frame: left, top, width, and (for one not held to its shape) height, as fractions of the frame.</summary>
    public double[] Place { get; set; } = [];

    // Keyframes, each property as time, value, time, value...
    public double[]? KeysX { get; set; }
    public double[]? KeysY { get; set; }
    public double[]? KeysScale { get; set; }
    public double[]? KeysRotation { get; set; }
    public double[]? KeysOpacity { get; set; }

    // How the clip moves on from each of those keyframes, in the same order, as the number of its EasingType.
    // Left out while every one of them is Linear, which is also what a project from before easing reads as:
    // its motion stays as it was made.
    public int[]? EaseX { get; set; }
    public int[]? EaseY { get; set; }
    public int[]? EaseScale { get; set; }
    public int[]? EaseRotation { get; set; }
    public int[]? EaseOpacity { get; set; }

    // A Custom Bézier's handles, four numbers (x1, y1, x2, y2) for each of those keyframes, in the same order; left out
    // while none of them is a Custom Bézier.
    public double[]? CurveX { get; set; }
    public double[]? CurveY { get; set; }
    public double[]? CurveScale { get; set; }
    public double[]? CurveRotation { get; set; }
    public double[]? CurveOpacity { get; set; }
}

/// <summary>
/// A project file of a newer format than this version reads. It can still be opened when the user asks (reading what this
/// version knows of it), but is never written over: what the newer version put in it would be lost. An IOException, so that
/// whatever opens or lists projects reports it as a file it could not read.
/// </summary>
public sealed class NewerProjectFormatException(int version, string? writtenBy)
    : IOException($"This project was saved by {writtenBy ?? "a newer version of HandPeg"} in project format {version}; this version of HandPeg "
                           + $"({ProjectStore.WriterName}) reads formats up to {ProjectFile.CurrentVersion}.")
{
    public int Version { get; } = version;
    public string? WrittenBy { get; } = writtenBy;
}

/// <summary>
/// Project files live in the Projects folder (see <see cref="AppPaths"/>), as .hproj: JSON in the layout above.
/// Projects saved by HandPeg 1.x (format 1: a .txt in another layout) are recognised by their shape and brought over
/// as far as they still map (see <see cref="LegacyProject"/>).
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".hproj";

    /// <summary>What a format 1 project's file name ends in.</summary>
    public const string LegacyExtension = ".txt";

    /// <summary>The name written into each project as the HandPeg that wrote it: "HandPeg 4.1.0".</summary>
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

        var bytes = buffer.AsSpan(0, read);
        if (bytes.StartsWith("﻿"u8))
            bytes = bytes[3..];

        var (format, version, writtenBy) = ((string?)null, 0, (string?)null);
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: read < buffer.Length, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            // The top level's plain values. An object or a list among them (the source, say) is stepped over while it lies
            // whole in what was read; one that runs past it ends the search, with what was found so far.
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

    public static string Serialize(ProjectState state) => JsonSerializer.Serialize(ToFile(state), JsonOptions);

    /// <param name="unpackMasks">Write the masks the project carries into the masks folder and point its layers at them, and take up the seek indexes of its video files: when it is being opened, not when it is only being listed.</param>
    /// <param name="allowNewer">Read a file of a newer format too, as far as this version knows it, instead of refusing it: for when the user asked to open it anyway.</param>
    public static ProjectState Load(string filePath, bool unpackMasks = true, bool allowNewer = false) => Deserialize(File.ReadAllText(filePath), unpackMasks, allowNewer);

    /// <summary>
    /// Reads a project from its text: format 2 or 3, or a HandPeg 1.x project (format 1, recognised by its shape). A newer
    /// format is refused with <see cref="NewerProjectFormatException"/> unless <paramref name="allowNewer"/>; anything else
    /// is refused with the reason.
    /// </summary>
    public static ProjectState Deserialize(string json, bool unpackMasks = false, bool allowNewer = false)
    {
        var file = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions) ?? throw new InvalidDataException("The project file is empty.");
        if (file.Format != ProjectFile.FormatName)
        {
            return LegacyProject.TryRead(json) ?? throw new InvalidDataException("This is not a HandPeg project.");
        }

        if (file.Version > ProjectFile.CurrentVersion && !allowNewer)
            throw new NewerProjectFormatException(file.Version, file.WrittenBy);
        if (file.Version < ProjectFile.OldestVersion)
            throw new InvalidDataException($"This project is in format {file.Version}, which this version of HandPeg does not read.");
        return FromFile(file, unpackMasks);
    }

    /// <summary>Whether a piece of JSON says it is a .hproj project: of a format this version reads, or of a newer one.</summary>
    public static bool IsProject(System.Text.Json.Nodes.JsonObject node) =>
        node["format"] is System.Text.Json.Nodes.JsonValue format && format.TryGetValue<string>(out var name) && name == ProjectFile.FormatName
        && node["version"] is System.Text.Json.Nodes.JsonValue version && version.TryGetValue<int>(out var number) && number >= ProjectFile.OldestVersion;

    /// <summary>
    /// Whether a file is a project this version can open: a .hproj, or a .txt that is a HandPeg 1.x project by its shape. A
    /// .txt is looked into only when it is small enough to be one, so that other text files are never taken for projects.
    /// </summary>
    public static bool IsProjectFile(string path)
    {
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return File.Exists(path);
        return path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase) && LegacyProject.IsLegacyFile(path);
    }

    // ----- Between the window's state and the file -----

    private static double[]? Flatten(List<Keyframe>? keys) => keys is { Count: > 0 } ? keys.SelectMany(k => new[] { k.Time, k.Value }).ToArray() : null;

    private static List<Keyframe>? Unflatten(double[]? numbers, int[]? easings, double[]? curves)
    {
        if (numbers is not { Length: >= 2 })
            return null;

        var keys = new List<Keyframe>(numbers.Length / 2);
        for (var i = 0; i + 1 < numbers.Length; i += 2)
        {
            var easing = easings is not null && i / 2 < easings.Length && Enum.IsDefined((EasingType)easings[i / 2]) ? (EasingType)easings[i / 2] : EasingType.Linear;
            var key = new Keyframe(numbers[i], numbers[i + 1], easing);
            if (curves is not null && i * 2 + 3 < curves.Length)
                key = key with { BezierX1 = curves[i * 2], BezierY1 = curves[i * 2 + 1], BezierX2 = curves[i * 2 + 2], BezierY2 = curves[i * 2 + 3] };
            keys.Add(key);
        }

        return keys;
    }

    private static int[]? Easings(List<Keyframe>? keys) =>
        keys is { Count: > 0 } && keys.Any(k => k.Easing != EasingType.Linear) ? keys.Select(k => (int)k.Easing).ToArray() : null;

    private static double[]? Curves(List<Keyframe>? keys) =>
        keys is { Count: > 0 } && keys.Any(k => k.Easing == EasingType.CubicBezier)
            ? keys.SelectMany(k => new[] { k.BezierX1, k.BezierY1, k.BezierX2, k.BezierY2 }).ToArray()
            : null;

    private static LayerState Copy(LayerState state) => JsonSerializer.Deserialize<LayerState>(JsonSerializer.Serialize(state))!;

    private static ProjectClip ToClip(LayerState layer, string trackName, LayerState? look = null) => new()
    {
        Name = layer.Name == trackName ? null : layer.Name,
        File = look is null || string.Equals(look.ImagePath, layer.ImagePath, StringComparison.OrdinalIgnoreCase) ? null : new ProjectClipFile
        {
            Path = layer.ImagePath,
            Width = layer.ImageWidth,
            Height = layer.ImageHeight,
            MediaDuration = layer.MediaDuration,
            HasAudio = layer.HasAudio,
            AudioCodec = layer.AudioCodec,
            AudioChannels = layer.AudioChannels,
        },
        Start = layer.StartTime,
        Duration = layer.Duration,
        Offset = layer.MediaOffset,
        Speed = layer.Speed,
        Hidden = layer.IsHidden,
        Proxy = layer.ProxyChoice,
        AudioOffset = layer.AudioOffset,
        FadeIn = layer.FadeInSeconds,
        FadeOut = layer.FadeOutSeconds,
        FadeInStyle = layer.FadeInStyle,
        FadeOutStyle = layer.FadeOutStyle,
        Transition = layer.TransitionIn,
        Place = layer.LockAspectRatio || layer.SizeHeight <= 0
            ? [layer.PositionX, layer.PositionY, layer.SizeWidth]
            : [layer.PositionX, layer.PositionY, layer.SizeWidth, layer.SizeHeight],
        KeysX = Flatten(layer.KeysX),
        KeysY = Flatten(layer.KeysY),
        KeysScale = Flatten(layer.KeysScale),
        KeysRotation = Flatten(layer.KeysRotation),
        KeysOpacity = Flatten(layer.KeysOpacity),
        EaseX = Easings(layer.KeysX),
        EaseY = Easings(layer.KeysY),
        EaseScale = Easings(layer.KeysScale),
        EaseRotation = Easings(layer.KeysRotation),
        EaseOpacity = Easings(layer.KeysOpacity),
        CurveX = Curves(layer.KeysX),
        CurveY = Curves(layer.KeysY),
        CurveScale = Curves(layer.KeysScale),
        CurveRotation = Curves(layer.KeysRotation),
        CurveOpacity = Curves(layer.KeysOpacity),
    };

    /// <summary>A track's shared look: one of its clips with everything that is a clip's own taken out.</summary>
    private static LayerState ToLook(LayerState layer)
    {
        var look = Copy(layer);
        (look.StartTime, look.Duration, look.MediaOffset, look.IsHidden, look.AudioOffset, look.TrackId, look.Speed) = (0, 0, 0, false, 0, 0, 1);
        (look.FadeInSeconds, look.FadeOutSeconds, look.TransitionIn) = (0, 0, 0);
        (look.FadeInStyle, look.FadeOutStyle) = (TransitionStyle.Dissolve, TransitionStyle.Dissolve);
        (look.PositionX, look.PositionY, look.SizeWidth, look.SizeHeight) = (0, 0, 0, 0);
        (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation, look.KeysOpacity) = (null, null, null, null, null);
        return look;
    }

    private static LayerState ToLayer(LayerState look, ProjectClip clip, int trackId)
    {
        var layer = Copy(look);
        double At(int index) => clip.Place.Length > index ? clip.Place[index] : 0;
        (layer.Name, layer.TrackId) = (clip.Name ?? look.Name, trackId);
        if (clip.File is { } file)
        {
            (layer.ImagePath, layer.ImageWidth, layer.ImageHeight, layer.MediaDuration) = (file.Path, file.Width, file.Height, file.MediaDuration);
            (layer.HasAudio, layer.AudioCodec, layer.AudioChannels) = (file.HasAudio, file.AudioCodec, file.AudioChannels);
        }

        (layer.StartTime, layer.Duration, layer.MediaOffset, layer.IsHidden, layer.AudioOffset) = (clip.Start, clip.Duration, clip.Offset, clip.Hidden, clip.AudioOffset);
        layer.ProxyChoice = clip.Proxy;
        (layer.FadeInSeconds, layer.FadeOutSeconds, layer.TransitionIn) = (clip.FadeIn, clip.FadeOut, clip.Transition);
        (layer.FadeInStyle, layer.FadeOutStyle) = (clip.FadeInStyle, clip.FadeOutStyle);
        layer.Speed = clip.Speed > 0 ? clip.Speed : 1;
        (layer.PositionX, layer.PositionY, layer.SizeWidth, layer.SizeHeight) = (At(0), At(1), At(2), At(3));
        (layer.KeysX, layer.KeysY) = (Unflatten(clip.KeysX, clip.EaseX, clip.CurveX), Unflatten(clip.KeysY, clip.EaseY, clip.CurveY));
        (layer.KeysScale, layer.KeysRotation) = (Unflatten(clip.KeysScale, clip.EaseScale, clip.CurveScale), Unflatten(clip.KeysRotation, clip.EaseRotation, clip.CurveRotation));
        layer.KeysOpacity = Unflatten(clip.KeysOpacity, clip.EaseOpacity, clip.CurveOpacity);
        return layer;
    }

    /// <summary>Everything in a project file that can carry a mask: each track's look, the main video's, and the caption box.</summary>
    private static IEnumerable<LayerState> MaskedLooks(ProjectFile file)
    {
        foreach (var track in file.Sequence.Tracks)
            yield return track.Look;
        yield return file.Sequence.Main.Look;
        if (file.Settings.CaptionLayer is { } captions)
            yield return captions;
    }

    private static ProjectFile ToFile(ProjectState state)
    {
        var settings = state.Settings;
        var main = settings.MainLayer ?? new LayerState { Kind = LayerKind.MainVideo, Name = "Main Video" };

        // Clips of one track lie together in the list, and are written as that track.
        var tracks = new List<ProjectTrack>();
        foreach (var layer in settings.Layers)
        {
            if (tracks.Count > 0 && tracks[^1].Id == layer.TrackId && layer.TrackId > 0 && tracks[^1].Look.Kind == layer.Kind)
                tracks[^1].Clips.Add(ToClip(layer, tracks[^1].Look.Name, tracks[^1].Look));
            else
                tracks.Add(new ProjectTrack { Id = layer.TrackId, Look = ToLook(layer), Clips = { ToClip(layer, layer.Name) } });
        }

        // The settings without what the sequence already holds.
        var rest = JsonSerializer.Deserialize<EncodingPreset>(JsonSerializer.Serialize(settings))!;
        (rest.Name, rest.Layers, rest.MainLayer, rest.MainVideoIndex, rest.BackgroundHidden, rest.AudioTracks) = ("", [], null, 0, false, []);
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
                Cuts = state.Segments.Select(s => s.Skipped ? new[] { s.StartMs, s.EndMs, 1 } : [s.StartMs, s.EndMs]).ToList(),
                Markers = state.Markers,
                SnapToIFrames = state.SnapToIFrames,
                AudioLinked = state.AudioLinked,
                Main = new ProjectTrack
                {
                    Look = ToLook(main),
                    Clips = [ToClip(main, main.Name), .. state.MainPieces.Select(p => new ProjectClip
                    {
                        Start = p.Start, Duration = p.Duration, Offset = p.Offset,
                        FadeIn = p.FadeIn, FadeOut = p.FadeOut, FadeInStyle = p.FadeInStyle, FadeOutStyle = p.FadeOutStyle,
                    })],
                },
                Deleted = state.Deleted,
                MainIndex = settings.MainVideoIndex,
                BackgroundHidden = settings.BackgroundHidden,
                Tracks = tracks,
                Audio = state.AudioTracks,
                Subtitles = state.SubtitleTracks,
                CaptionAudioPath = state.CaptionAudioPath,
                CaptionUseExternalAudio = state.CaptionUseExternalAudio ?? false,
                CaptionAudioTrack = state.CaptionAudioTrackIndex,
            },
            Settings = rest,
        };

        // The looks are the file's own copies: they are pointed at the masks carried with them, not at the disk.
        var masks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var look in MaskedLooks(file))
            MaskStore.Pack(look, masks);
        file.Masks = masks.Count > 0 ? masks : null;

        var seeks = VideoFiles(state).Select(MediaProbe.KnownFacts).OfType<SeekFacts>().Select(f => new ProjectSeekIndex
        {
            Path = f.Path, Length = f.Length, Written = f.WrittenTicks, ContainerStart = f.ContainerStart, VideoStart = f.VideoStart, Duration = f.Duration,
            IFrames = MediaProbe.PackTimes(f.IFrames),
        }).ToList();
        file.SeekIndexes = seeks.Count > 0 ? seeks : null;
        return file;
    }

    /// <summary>The video files a project plays: the main video and every video layer's file, once each.</summary>
    private static IEnumerable<string> VideoFiles(ProjectState state) =>
        new[] { state.LocalMediaPath }.Concat(state.Settings.Layers.Where(l => l.Kind == LayerKind.VideoFile).Select(l => l.ImagePath))
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

    private static ProjectState FromFile(ProjectFile file, bool unpackMasks)
    {
        // The masks the project carries go into the masks folder, and its layers are pointed at them there.
        if (unpackMasks && file.Masks is { Count: > 0 } masks)
        {
            foreach (var look in MaskedLooks(file))
            {
                if (MaskStore.Unpack(look, masks) is { } problem)
                    AppLog.Write(problem);
            }
        }

        var sequence = file.Sequence;
        var settings = file.Settings;
        settings.Name = file.Name;
        settings.Layers = sequence.Tracks.SelectMany(track => track.Clips.Select(clip => ToLayer(track.Look, clip, track.Id))).ToList();
        settings.MainLayer = sequence.Main.Clips.Count > 0 ? ToLayer(sequence.Main.Look, sequence.Main.Clips[0], 0) : sequence.Main.Look;
        (settings.MainVideoIndex, settings.BackgroundHidden, settings.AudioTracks) = (sequence.MainIndex, sequence.BackgroundHidden, sequence.Audio);

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
            Markers = file.Sequence.Markers ?? [],
            Subtitles = file.Output.Subtitles,
            TargetFileSize = file.Output.TargetFileSize,
            ManualCommand = file.Output.ManualCommand,
            Segments = sequence.Cuts.Where(c => c.Length >= 2).Select(c => new SegmentState(c[0], c[1], c.Length > 2 && c[2] != 0)).ToList(),
            SnapToIFrames = sequence.SnapToIFrames,
            AudioLinked = sequence.AudioLinked,
            MainPieces = sequence.Main.Clips.Skip(1).Select(c => new MainPiece(c.Start, c.Duration, c.Offset)
            {
                FadeIn = Math.Clamp(c.FadeIn, 0, 60), FadeOut = Math.Clamp(c.FadeOut, 0, 60), FadeInStyle = c.FadeInStyle, FadeOutStyle = c.FadeOutStyle,
            }).ToList(),
            Deleted = sequence.Deleted,
            Settings = settings,
            AudioTracks = sequence.Audio,
            SubtitleTracks = sequence.Subtitles,
            CaptionAudioPath = sequence.CaptionAudioPath,
            CaptionUseExternalAudio = sequence.CaptionUseExternalAudio,
            CaptionAudioTrackIndex = sequence.CaptionAudioTrack,
        };

        // Opened (not only listed): its video files are made ready to be seeked in now, before the first graph is built.
        if (unpackMasks)
            TakeUpSeekIndexes(file, state);
        return state;
    }

    /// <summary>
    /// Saved projects, most recently saved first: the .hproj files (a newer format's too, which asks before it opens) and
    /// the HandPeg 1.x projects (.txt) among the text files, recognised by their shape. Files that cannot be read are left out.
    /// </summary>
    public static List<ProjectEntry> ListRecent()
    {
        var entries = new List<ProjectEntry>();
        if (!Directory.Exists(Folder))
            return entries;

        var files = new DirectoryInfo(Folder).EnumerateFiles("*" + Extension)
            .Concat(new DirectoryInfo(Folder).EnumerateFiles("*" + LegacyExtension).Where(f => LegacyProject.IsLegacyFile(f.FullName)));
        foreach (var file in files.OrderByDescending(f => f.LastWriteTime))
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
