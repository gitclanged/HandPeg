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
/// <param name="Thumbnail">Embedded JPEG thumbnail; null when the file has none.</param>
public sealed record ProjectEntry(string FilePath, string Name, string SourceName, string SourcePath, DateTime SavedAt, byte[]? Thumbnail = null)
{
    public string SavedText => $"Saved {SavedAt:g}";
}

// ----- Project file (.hproj), formats 6 and 7 -----
// JSON layout: header (format, version, writer) first so PeekHeader reads it from the first few KB; then thumbnail, source, output,
// sequence extras (audio stream map, recycle bin), settings, masks, seek facts, undo/redo history, and the timeline
// (TimelineDocument) last. Default values are never written.
//
// Formats 1-5 are refused, not converted. A newer format opens only on explicit request and is never overwritten. Format 6 has the
// background as its own layer (sequence.background) and stores anchored captions as anchors in source time (cues are regenerated).
//
// Optional sections, omitted when empty: thumbnailJpeg (Base64 JPEG right after the header for Explorer's preview handler) and
// undoFormat/undoHistory/redoHistory (just before the timeline; read leniently: a malformed history is dropped, never the project).
// A bundle (.hpb, ProjectBundle) is a project plus its media in one archive.
//
// Legacy main-video projects are detected by content (OldShapeOf: MainVideo or Video clips, main-file captions), loaded as-is and
// converted once on open by the view model (LegacyMainVideo), which then overwrites the file. Their sequence.audio stream mixing is
// conversion input only. Source.Path/LocalPath remain the last-opened file (Info, Streams, Download panes, default output name).
//
// Tethers: a tethered clip writes "tethered": true; the timeline writes "tethersMarked": true only when it holds a loose child
// (TimelineDocument.TethersMarked). Without the mark, tethers are inferred on read (TimelineDocument.OnDeserialized).

/// <summary>A project as it is written to disk.</summary>
public sealed class ProjectFile
{
    public const string FormatName = "handpeg-project";

    /// <summary>Newest format read and written (newer formats open read-only on explicit request).</summary>
    public const int CurrentVersion = 7;

    /// <summary>
    /// Format written when no format-7 feature is used, so older releases still open it. Projects using a newer feature
    /// (<see cref="ProjectStore.FormatFor"/>) are written as 7, which older releases refuse to overwrite.
    /// </summary>
    public const int BaseVersion = 6;

    /// <summary>Oldest format opened; formats 1-5 are refused, not converted.</summary>
    public const int OldestReadable = 6;

    // No defaults: a file without a header must not pass as valid. Declared first so they serialize first, where PeekHeader reads them.
    public string? Format { get; set; }
    public int Version { get; set; }

    /// <summary>Writer name and version ("HandPeg &lt;version&gt;"), shown when an older version refuses the file.</summary>
    public string? WrittenBy { get; set; }

    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// Base64 JPEG of the playhead frame at save time for the Project Manager, launch window and Explorer preview
    /// (ProjectThumbnail, HandPeg.ShellThumbnails). Serialized right after the header so it sits in the first ~100 KB. Optional.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThumbnailJpeg { get; set; }

    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public ProjectSource Source { get; set; } = new();
    public ProjectOutput Output { get; set; } = new();

    /// <summary>Sequence data outside the timeline: audio stream map, recycle bin, caption audio source.</summary>
    public ProjectSequence Sequence { get; set; } = new();

    /// <summary>Everything that is not on the timeline: the frame, the encoders, the filters, the caption style.</summary>
    public EncodingPreset Settings { get; set; } = new();

    /// <summary>
    /// Embedded mask images (Base64) keyed by the name their looks reference, so the file is self-contained. Optional.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Masks { get; set; }

    /// <summary>
    /// Cached seek indexes for the project's video files at save time; reused on open for unchanged files so seeking works before
    /// probing. Optional.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectSeekIndex>? SeekIndexes { get; set; }

    /// <summary>
    /// Undo step schema version (packed TimelineSnapshot JSON, UndoSteps). Bump on incompatible change; mismatched histories are dropped on open.
    /// </summary>
    public const int CurrentUndoFormat = 1;

    /// <summary>The most steps of each history a file carries (Undo keeps twenty).</summary>
    public const int MaximumUndoSteps = 20;

    /// <summary>Undo schema of the stored history (<see cref="CurrentUndoFormat"/> at write); 0 and omitted without history.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), JsonConverter(typeof(LenientIntConverter))]
    public int UndoFormat { get; set; }

    /// <summary>
    /// Undo history at save, oldest first, max 20 steps (label + packed Base64 timeline). Optional; read leniently (<see cref="UndoStepsConverter"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(UndoStepsConverter))]
    public List<ProjectUndoStep>? UndoHistory { get; set; }

    /// <summary>Redo history at save, same encoding as <see cref="UndoHistory"/>; the next Redo step is last.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(UndoStepsConverter))]
    public List<ProjectUndoStep>? RedoHistory { get; set; }

    /// <summary>Workspace (window state) at save; excluded from the dirty fingerprint. Absent when not saved from a window.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectWorkspace? Workspace { get; set; }

    /// <summary>
    /// Timeline (tracks, clips, markers, Sequence Output effects). Serialized last; required.
    /// </summary>
    public TimelineDocument? Timeline { get; set; }
}

/// <summary>Serialized undo step: label (e.g. "delete Logo") and packed Base64 timeline.</summary>
public sealed class ProjectUndoStep
{
    public string What { get; set; } = "";
    public string State { get; set; } = "";

    /// <summary>
    /// Packed bytes for writing: streamed as the Base64 <see cref="State"/> in small chunks without materializing the string
    /// (large steps reach hundreds of KB).
    /// </summary>
    internal byte[]? Packed { get; init; }
}

/// <summary>
/// Lenient undo-history converter: never fails the project. Non-arrays read as empty; entries without string "what"/"state" are
/// skipped. Writes a JSON array of steps.
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
            if (step.Packed is { } packed)
                WriteBase64(writer, "state", packed);
            else
                writer.WriteString("state", step.State);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // The input of one piece: a multiple of 3 bytes, so the pieces' Base64 joins up with no padding until the last.
    private const int PieceBytes = 3 * 1024;

    /// <summary>
    /// Writes bytes as the exact JSON string Convert.ToBase64String would produce (with the writer's escaping of any
    /// text: a "+" as \u002B), from a 4 KB buffer on the stack instead of a string of the whole.
    /// </summary>
    private static void WriteBase64(Utf8JsonWriter writer, string name, byte[] bytes)
    {
        writer.WritePropertyName(name);
        Span<byte> piece = stackalloc byte[PieceBytes / 3 * 4];
        var at = 0;
        do
        {
            var length = Math.Min(PieceBytes, bytes.Length - at);
            System.Buffers.Text.Base64.EncodeToUtf8(bytes.AsSpan(at, length), piece, out _, out var written);
            at += length;
            writer.WriteStringValueSegment(piece[..written], isFinalSegment: at >= bytes.Length);
        }
        while (at < bytes.Length);
    }
}

/// <summary>Lenient integer converter: any non-number reads as 0 without failing the file.</summary>
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

/// <summary>Cached seek facts for one video (<see cref="SeekFacts"/>): size, last write, and I-frame times.</summary>
public sealed class ProjectSeekIndex
{
    public string Path { get; set; } = "";
    public long Length { get; set; }

    /// <summary>When the file was last written, in UTC ticks.</summary>
    public long Written { get; set; }

    public double ContainerStart { get; set; }
    public double VideoStart { get; set; }
    public double Duration { get; set; }

    /// <summary>I-frame times in file seconds, Base64 little-endian doubles (MediaProbe.PackTimes).</summary>
    public string IFrames { get; set; } = "";
}

public sealed class ProjectSource
{
    public string Path { get; set; } = "";

    /// <summary>The file actually read, when it is not the path itself: the download behind a web address.</summary>
    public string LocalPath { get; set; } = "";

    public string DownloadResolution { get; set; } = "Best";
    public bool DownloadSubtitles { get; set; }

    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class ProjectOutput
{
    public string Path { get; set; } = "";
    public string Container { get; set; } = "mp4";
    public bool WebOptimized { get; set; }
    public bool ChapterMarkers { get; set; } = true;

    /// <summary>Writes a chapter at each primary clip start.</summary>
    public bool ChaptersAtCuts { get; set; }

    public bool ChaptersAtMarkers { get; set; }
    public string TargetFileSize { get; set; } = "";
    public string? ManualCommand { get; set; }

    /// <summary>Subtitle track (cues, offset, source); omitted when absent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubtitleTrackData? Subtitles { get; set; }

    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>What of the sequence is not on its timeline.</summary>
public sealed class ProjectSequence
{
    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>Splits and the playhead keep to the main video's I-frames.</summary>
    public bool SnapToIFrames { get; set; }

    /// <summary>Blur-fill background layer; written only where it differs from defaults.</summary>
    public ProjectBackground Background { get; set; } = new();

    /// <summary>
    /// Output audio tracks by stream position (copy, re-encode or drop; codec, bitrate, title). Legacy projects also store per-stream
    /// mixing here as input to the main-video conversion; preserved as written.
    /// </summary>
    public List<AudioTrackState> Audio { get; set; } = [];

    public List<SubtitleTrackState> Subtitles { get; set; } = [];

    /// <summary>The recycle bin: clips deleted from the timeline, as they were, with the track each was on.</summary>
    public List<DeletedClip> Deleted { get; set; } = [];

    /// <summary>
    /// Recycle-bin counterpart of TimelineDocument.TethersMarked: written only when the bin holds a loose child, so other files are unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BinTethersMarked { get; set; }

    public string CaptionAudioPath { get; set; } = "";
    public bool CaptionUseExternalAudio { get; set; }
    public int CaptionAudioTrack { get; set; }
}

/// <summary>
/// Serialized blur-fill background: enabled, blur radius (0-64 frame px), passes, darkening, centre zoom (1 = cover) and effect
/// stack. Live values sit in EncodingPreset; the file stores them here and omits them from settings. Defaults match a new project.
/// </summary>
public sealed class ProjectBackground
{
    public bool Hidden { get; set; }
    public double Radius { get; set; } = 20;
    public int Passes { get; set; } = 2;
    public double Dim { get; set; } = -0.15;
    public double Zoom { get; set; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Models.Effects.EffectState>? Effects { get; set; }

    public bool EffectsBypassed { get; set; }

    /// <summary>Fields this version does not know (a newer HandPeg's), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// Project of a newer format. Can be opened on request (known fields only) but is never overwritten, to avoid losing newer data.
/// Derives from IOException so open/list paths report it as unreadable.
/// </summary>
public sealed class NewerProjectFormatException(int version, string? writtenBy)
    : IOException($"This project was saved by {writtenBy ?? "a newer version of HandPeg"} in project format {version}; this version of HandPeg "
                           + $"({ProjectStore.WriterName}) reads format {ProjectFile.CurrentVersion}.")
{
    public int Version { get; } = version;
    public string? WrittenBy { get; } = writtenBy;
}

/// <summary>Reads and writes .hproj project files (JSON, layout above) in the Projects folder (<see cref="AppPaths"/>).</summary>
public static class ProjectStore
{
    public const string Extension = ".hproj";

    /// <summary>Writer string stored in each project ("HandPeg &lt;version&gt;").</summary>
    public static string WriterName { get; } = typeof(ProjectStore).Assembly.GetName().Version is { } version
        ? $"HandPeg {version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}"
        : "HandPeg";

    internal static readonly JsonSerializerOptions JsonOptions = new()
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
    /// Omits properties equal to a fresh instance's value (zero, false, empty, or initializer such as opacity 100, contrast 1);
    /// deserialization restores the same values.
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
    /// Deserialization constructor whose parameters all have defaults; its no-argument result defines omitted values (e.g. keyframe
    /// easing defaults to Ease In-Out, so Ease In-Out is omitted and Linear is written).
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
        WriteProject(state, path, durable: true);
        return path;
    }

    /// <summary>What an autosave's file name ends in, before the extension: "My Project.autosave.hproj".</summary>
    public const string AutosaveSuffix = ".autosave";

    /// <summary>Saves the state to an arbitrary path.</summary>
    /// <param name="durable">
    /// Flush to disk before replacing (<see cref="AtomicFile"/>): true for saves and autosaves; false for the tab cache, which only
    /// needs to survive a process crash.
    /// </param>
    public static string SaveTo(ProjectState state, string path, bool durable = true)
    {
        RefuseNewer(path);
        WriteProject(state, path, durable);
        return path;
    }

    /// <summary>
    /// Streams the same bytes as <see cref="Serialize"/> (UTF-8, no BOM) directly to the file in ~16 KB chunks, never holding the full
    /// text (tab switches write these on the pool). Undo steps stream their Base64 incrementally. Atomic (<see cref="AtomicFile.Write"/>):
    /// failure leaves the old file intact.
    /// </summary>
    private static void WriteProject(ProjectState state, string path, bool durable)
    {
        var file = ToFile(state);
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, file, JsonOptions), durable);
    }

    /// <summary>
    /// Throws (IOException) instead of overwriting a newer-format project, so every save path reports a failed save.
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
    /// Reads the header (format, version, writer) from the first few KB without loading the whole file. Null when missing or not a project.
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
    /// Scans top-level scalars for format, version and writer, skipping complete nested values in <paramref name="bytes"/>. Stops at
    /// a truncated value or once all three are found. Null when the text does not start as a project object.
    /// </summary>
    /// <param name="isFinalBlock">True when <paramref name="bytes"/> is the entire text.</param>
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

    /// <summary>Autosave path: beside the project file, else in the Projects folder by name.</summary>
    public static string AutosavePath(string projectPath, string name)
    {
        if (projectPath.Length > 0 && Path.GetDirectoryName(projectPath) is { Length: > 0 } folder)
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(projectPath) + AutosaveSuffix + Extension);

        var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(Folder, (fileName.Length > 0 ? fileName : "Untitled") + AutosaveSuffix + Extension);
    }

    /// <summary>
    /// Atomic write (<see cref="AtomicFile"/>) without flush, for caches and side files that only need to survive a process crash.
    /// Project saves use <see cref="SaveTo"/>, which flushes.
    /// </summary>
    public static void WriteWhole(string path, string content, bool durable = false) => AtomicFile.WriteAllText(path, content, durable);

    /// <summary>Temp path used before replacing <paramref name="path"/> (<see cref="AtomicFile.PartialPath"/>).</summary>
    internal static string PartialPath(string path) => AtomicFile.PartialPath(path);

    /// <summary>
    /// Streaming <see cref="WriteWhole(string, string, bool)"/>: <paramref name="write"/> fills the temp file, which then replaces the
    /// target; failure leaves the target intact.
    /// </summary>
    public static void WriteWhole(string path, Action<Stream> write, bool durable = false) => AtomicFile.Write(path, write, durable);

    /// <summary>
    /// Write format: <see cref="ProjectFile.BaseVersion"/> (6) unless a format-7 feature is used, then <see cref="ProjectFile.CurrentVersion"/>
    /// (7) so older releases refuse to overwrite instead of dropping data. Format-7 features: speed above 10x, Keep Pitch off, Smooth
    /// Slow-Mo, Spectral Audio. Extend this test with every such field (<c>project-format-7</c> golden).
    /// </summary>
    internal static int FormatFor(TimelineDocument timeline, IEnumerable<DeletedClip> deleted)
    {
        const double FastestBefore56 = 10;
        static bool Needs7(TimelineClip clip) =>
            clip.Speed > FastestBefore56
            || clip.State is { KeepPitch: false } or { SmoothSlowMo: true } or { SpectralAudio: true };

        var needs7 = timeline.Tracks.Any(track => track.Clips.Any(Needs7) || track.TrackLook is { KeepPitch: false } or { SmoothSlowMo: true } or { SpectralAudio: true })
                     || deleted.Any(d => d?.Clip is { } clip && Needs7(clip));
        return needs7 ? ProjectFile.CurrentVersion : ProjectFile.BaseVersion;
    }

    /// <summary>Serializes the state; the timeline is written from a copy, leaving the state unchanged.</summary>
    public static string Serialize(ProjectState state) => JsonSerializer.Serialize(ToFile(state), JsonOptions);

    /// <param name="unpackMasks">On open (not listing): extract embedded masks to the masks folder and relink looks, import seek indexes, and read undo history.</param>
    /// <param name="allowNewer">Read a newer format (known fields only) instead of refusing it, after explicit confirmation.</param>
    public static ProjectState Load(string filePath, bool unpackMasks = true, bool allowNewer = false) =>
        Deserialize(PortableProject.Resolve(File.ReadAllText(filePath), filePath), unpackMasks, allowNewer);

    /// <summary>
    /// Project path by name (Swap Project list): Projects\Name.hproj or portable Projects\Name\Name.hproj; the former when neither exists.
    /// </summary>
    public static string PathOfName(string name)
    {
        var flat = Path.Combine(Folder, name + Extension);
        var portable = PortableProject.PathFor(name);
        return !File.Exists(flat) && File.Exists(portable) ? portable : flat;
    }

    /// <summary>Projects in the Projects folder: top-level .hproj files plus portable project folders.</summary>
    public static IEnumerable<FileInfo> ProjectFiles()
    {
        if (!Directory.Exists(Folder))
            return [];

        var folder = new DirectoryInfo(Folder);
        var portable = folder.EnumerateDirectories().Select(d => new FileInfo(Path.Combine(d.FullName, d.Name + Extension))).Where(f => f.Exists);
        return folder.EnumerateFiles("*" + Extension).Concat(portable);
    }

    /// <summary>
    /// Parses a project including <see cref="ProjectState.Timeline"/>. The header is validated first: non-projects and formats 1-5
    /// throw <see cref="InvalidDataException"/>; newer formats throw <see cref="NewerProjectFormatException"/> unless
    /// <paramref name="allowNewer"/>. A missing timeline is refused. Legacy shapes (<see cref="OldShapeOf"/>) load as-is for conversion.
    /// </summary>
    public static ProjectState Deserialize(string json, bool unpackMasks = false, bool allowNewer = false)
    {
        // Validate the header first so a wrong format is reported as such, not as a downstream parse error.
        if (HeaderOf(json) is not { Format: ProjectFile.FormatName } header)
            throw new InvalidDataException("This is not a HandPeg project.");
        if (header.Version > ProjectFile.CurrentVersion && !allowNewer)
            throw new NewerProjectFormatException(header.Version, header.WrittenBy);
        if (header.Version < 1)
            throw new InvalidDataException("This HandPeg project does not say which format it is in.");
        if (header.Version < ProjectFile.OldestReadable)
        {
            var writer = header.WrittenBy is { Length: > 0 } by ? $" (saved by {by})" : "";
            throw new InvalidDataException($"This project is in format {header.Version}{writer}. This version of HandPeg opens format {ProjectFile.OldestReadable} "
                                           + $"to {ProjectFile.CurrentVersion} projects only: projects of formats 1 to 5 (HandPeg 4.4 and earlier) are not converted.");
        }

        var file = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions) ?? throw new InvalidDataException("The project file is empty.");
        if (file.Timeline is not { } timeline)
            throw new InvalidDataException("This project has no timeline: a format 6 project keeps its tracks and clips there.");

        return FromFile(file, timeline, unpackMasks);
    }

    // How much of a project's text its header is looked for in first: HandPeg writes it at the very start.
    private const int HeaderChars = 4096;

    /// <summary>
    /// Header from text, parsed from the first few KB when complete there (avoids encoding megabytes to UTF-8 per tab resume); falls
    /// back to the whole text for hand-written files with a later header.
    /// </summary>
    private static (string? Format, int Version, string? WrittenBy)? HeaderOf(string json)
    {
        if (json.Length > HeaderChars
            && ReadHeader(Encoding.UTF8.GetBytes(json, 0, HeaderChars), isFinalBlock: false) is { Format: not null, Version: not 0, WrittenBy: not null } found)
        {
            return found;
        }

        return ReadHeader(Encoding.UTF8.GetBytes(json), isFinalBlock: true);
    }

    // ----- Legacy main-video projects (converted on open: MainViewModel.ConvertLegacy) -----

    /// <summary>True for legacy clip kinds: MainVideo or its cut-out (Video).</summary>
    public static bool IsOldKind(TimelineClip? clip) => clip?.ContentKind is LayerKind.MainVideo or LayerKind.Video;

    /// <summary>
    /// True when the timeline or recycle bin holds a legacy-kind clip (<see cref="IsOldKind"/>) on a track, in a fold or in the bin.
    /// Current projects never do: preset Cutouts are placed as clips of the source file.
    /// </summary>
    public static bool HoldsOldKinds(TimelineDocument timeline, IEnumerable<DeletedClip?>? bin)
    {
        static bool Holds(DeletedClip? deleted) => deleted is not null && (IsOldKind(deleted.Clip) || (deleted.Children ?? []).Any(Holds));
        return (timeline.Tracks ?? []).Exists(t => (t?.Clips ?? []).Exists(IsOldKind))
               || (timeline.Folds ?? []).Exists(f => (f?.Clips ?? []).Any(c => IsOldKind(c?.Clip)))
               || (bin ?? []).Any(Holds);
    }

    /// <summary>
    /// Human-readable reason a project has the legacy shape (for log/status during conversion): legacy-kind clips
    /// (<see cref="HoldsOldKinds"/>) or <see cref="CaptionSourceKind.MainFile"/> captions. Null for current projects. Stream mixing in
    /// sequence.audio is not a signal (current projects may carry stale preset ducking there).
    /// </summary>
    public static string? OldShapeOf(TimelineDocument timeline, IEnumerable<DeletedClip?>? bin, SubtitleTrackData? subtitles)
    {
        var clips = new List<TimelineClip>();
        foreach (var track in timeline.Tracks ?? [])
            clips.AddRange((track?.Clips ?? []).Where(IsOldKind));
        foreach (var fold in timeline.Folds ?? [])
            clips.AddRange((fold?.Clips ?? []).Select(c => c?.Clip).Where(IsOldKind).OfType<TimelineClip>());

        if (clips.Count == 0 && HoldsOldKinds(timeline, bin))
            return "clips of the main video in its recycle bin";
        if (clips.Count > 0)
        {
            var (main, cut) = (clips.Count(c => c.ContentKind == LayerKind.MainVideo), clips.Count(c => c.ContentKind == LayerKind.Video));
            return string.Join(" and ", new[]
            {
                main > 0 ? $"{main} clip{(main == 1 ? "" : "s")} of the main video" : null,
                cut > 0 ? $"{cut} cut-out{(cut == 1 ? "" : "s")} of it" : null,
            }.OfType<string>());
        }

        return subtitles?.Source is { Kind: CaptionSourceKind.MainFile } ? "its captions are heard from the main video's file" : null;
    }

    /// <summary>A timeline as a format 6 file writes it: camel case, enums by name, nothing at its default.</summary>
    public static System.Text.Json.Nodes.JsonNode? TimelineNode(TimelineDocument document) => JsonSerializer.SerializeToNode(document, JsonOptions);

    /// <summary>True when the JSON declares a supported .hproj format (current, or newer with confirmation).</summary>
    public static bool IsProject(System.Text.Json.Nodes.JsonObject node) =>
        node["format"] is System.Text.Json.Nodes.JsonValue format && format.TryGetValue<string>(out var name) && name == ProjectFile.FormatName
        && node["version"] is System.Text.Json.Nodes.JsonValue version && version.TryGetValue<int>(out var number) && number >= ProjectFile.OldestReadable;

    /// <summary>True for an existing .hproj or .hpb file.</summary>
    public static bool IsProjectFile(string path) =>
        (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase)) && File.Exists(path);

    // ----- Between the window's state and the file -----

    /// <summary>Deep clip copy (including look) so mask relinking for the file never mutates live state.</summary>
    private static TimelineClip CopyOf(TimelineClip clip)
    {
        var copy = clip.Snapshot();
        copy.State = TimelineTransaction.CopyOf(clip.State);
        return copy;
    }

    /// <summary>
    /// All mask-bearing looks: track looks, clip looks (timeline and recycle bin), and the caption box.
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

        // Settings minus data owned by the timeline/sequence (layers, primary look, background, streams, output effects): no duplication.
        var rest = JsonSerializer.Deserialize<EncodingPreset>(JsonSerializer.Serialize(settings))!;
        (rest.Name, rest.Layers, rest.MainLayer, rest.MainVideoIndex, rest.BackgroundHidden, rest.AudioTracks) = ("", [], null, 0, false, []);
        var fresh = new EncodingPreset();
        (rest.BlurRadius, rest.BlurPasses, rest.BackgroundDim, rest.BackgroundZoom, rest.BackgroundEffects, rest.BackgroundEffectsBypassed) =
            (fresh.BlurRadius, fresh.BlurPasses, fresh.BackgroundDim, fresh.BackgroundZoom, null, false);
        (rest.SequenceEffects, rest.SequenceEffectsBypassed) = (null, false);

        // Private timeline copy whose looks are relinked to embedded masks; the live state keeps its on-disk mask paths.
        var timeline = state.Timeline.Clone(deep: true);
        var extras = state.Timeline.CarriedProjectFields;
        rest.Extra ??= extras?.Settings;
        var file = new ProjectFile
        {
            Format = ProjectFile.FormatName,
            Version = FormatFor(timeline, state.Deleted),
            WrittenBy = WriterName,
            Extra = extras?.File,
            ThumbnailJpeg = state.ThumbnailJpeg is { Length: > 0 } thumbnail ? Convert.ToBase64String(thumbnail) : null,
            Name = state.Name,
            SavedAt = state.SavedAt,
            Source = new ProjectSource
            {
                Path = state.SourcePath,
                LocalPath = state.LocalMediaPath == state.SourcePath ? "" : state.LocalMediaPath,
                DownloadResolution = state.DownloadResolution,
                DownloadSubtitles = state.DownloadSubtitles,
                Extra = extras?.Source,
            },
            Output = new ProjectOutput
            {
                Path = state.DestinationPath,
                Container = state.Container,
                WebOptimized = state.WebOptimized,
                ChapterMarkers = state.ChapterMarkers,
                ChaptersAtCuts = state.ChaptersAtCuts,
                ChaptersAtMarkers = state.ChaptersAtMarkers,
                Subtitles = WrittenOnce(state.Subtitles),
                TargetFileSize = state.TargetFileSize,
                ManualCommand = state.ManualCommand,
                Extra = extras?.Output,
            },
            Sequence = new ProjectSequence
            {
                SnapToIFrames = state.SnapToIFrames,
                Background = new ProjectBackground
                {
                    Hidden = settings.BackgroundHidden, Radius = settings.BlurRadius, Passes = settings.BlurPasses, Dim = settings.BackgroundDim,
                    Zoom = settings.BackgroundZoom, Effects = settings.BackgroundEffects is { Count: > 0 } effects ? effects : null,
                    EffectsBypassed = settings.BackgroundEffectsBypassed, Extra = extras?.Background,
                },
                Audio = state.AudioTracks,
                Subtitles = state.SubtitleTracks,

                // The bin's clips are the file's own copies too, as the timeline is.
                Deleted = [.. state.Deleted.Where(d => d?.Clip is not null).Select(d => d with { Clip = CopyOf(d.Clip) })],
                BinTethersMarked = state.Timeline.LooseIn(state.Deleted).Exists(c => !c.Tethered),
                CaptionAudioPath = state.CaptionAudioPath,
                CaptionUseExternalAudio = state.CaptionUseExternalAudio ?? false,
                CaptionAudioTrack = state.CaptionAudioTrackIndex,
                Extra = extras?.Sequence,
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

        // Undo history with its schema version, only when the state carries one.
        if (state.UndoHistory is { Count: > 0 } || state.RedoHistory is { Count: > 0 })
        {
            file.UndoFormat = ProjectFile.CurrentUndoFormat;
            (file.UndoHistory, file.RedoHistory) = (StepsOf(state.UndoHistory), StepsOf(state.RedoHistory));
        }

        file.Workspace = state.Workspace;
        return file;
    }

    /// <summary>
    /// Subtitle track for writing: anchored captions as anchors only; projected cues are omitted and regenerated on open
    /// (CaptionProjection). Free cues (imported, typed, detached) are written as-is.
    /// </summary>
    private static SubtitleTrackData? WrittenOnce(SubtitleTrackData? track) =>
        track is { IsImage: false, Source.IsAnchored: true, Anchors.Count: > 0, Cues: { } cues } && cues.Any(c => c.Projected)
            ? new SubtitleTrackData
            {
                SourcePath = track.SourcePath, IsImage = track.IsImage, OffsetMs = track.OffsetMs, Cues = [.. cues.Where(c => !c.Projected)],
                Source = track.Source, Anchors = track.Anchors,
            }
            : track;

    /// <summary>
    /// History for writing: newest 20 steps, oldest first; null when empty. Packed steps are shared (immutable) and Base64-encoded
    /// lazily during write (<see cref="ProjectUndoStep.Packed"/>).
    /// </summary>
    private static List<ProjectUndoStep>? StepsOf(List<UndoStepState>? steps) =>
        steps is { Count: > 0 }
            ? [.. steps.Where(s => s is { State.Length: > 0 }).TakeLast(ProjectFile.MaximumUndoSteps).Select(s => new ProjectUndoStep { What = s.What, Packed = s.State })]
            : null;

    /// <summary>
    /// Reads stored undo/redo history: dropped (and logged) on schema mismatch; undecodable steps skipped; newest 20 of each. Never
    /// fails the open; steps that prove unreadable are discarded when reached (MainViewModel.Step).
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

    /// <summary>
    /// Distinct video files referenced by clips (the last-opened source counts only if a clip uses it). Unconverted legacy projects
    /// still report their main video via legacy-kind clips.
    /// </summary>
    private static IEnumerable<string> VideoFiles(ProjectState state) =>
        state.Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.ContentKind is LayerKind.VideoFile or LayerKind.MainVideo).Select(c => c.SourcePath)
            .Select(p => (p ?? "").Trim().Trim('"')).Where(p => p.Length > 0 && !p.Contains("://", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Primes seek indexes on open, before any graph is built: from embedded facts for unchanged files, else the Timeline cache.
    /// Logs counts by source.
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

        // Restore sequence-held settings; layers, primary look and output effects belong to the timeline and stay empty here.
        var sequence = file.Sequence;
        var audio = sequence.Audio ?? [];
        var settings = file.Settings;
        settings.Name = file.Name;
        var background = sequence.Background ?? new ProjectBackground();

        // Unknown fields of objects rebuilt on save travel with the timeline so they are written back.
        timeline.CarriedProjectFields = ProjectExtras.Of(file.Extra, file.Source.Extra, file.Output.Extra, sequence.Extra, background.Extra, settings.Extra);
        (settings.BackgroundHidden, settings.AudioTracks) = (background.Hidden, audio);
        (settings.BlurRadius, settings.BlurPasses, settings.BackgroundDim, settings.BackgroundZoom, settings.BackgroundEffects, settings.BackgroundEffectsBypassed) =
            (background.Radius, background.Passes, background.Dim, background.Zoom, background.Effects, background.EffectsBypassed);

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

            // Legacy-shaped entry (hand-translated project) with no clip to restore.
            Deleted = [.. (sequence.Deleted ?? []).Where(d => d?.Clip is not null)],
            Settings = settings,
            AudioTracks = audio,
            SubtitleTracks = sequence.Subtitles ?? [],
            CaptionAudioPath = sequence.CaptionAudioPath,
            CaptionUseExternalAudio = sequence.CaptionUseExternalAudio,
            CaptionAudioTrackIndex = sequence.CaptionAudioTrack,
            ThumbnailJpeg = ThumbnailOf(file.ThumbnailJpeg),
            Workspace = file.Workspace,
        };

        // Infer tethers in an unmarked recycle bin, as for the timeline.
        if (!sequence.BinTethersMarked)
        {
            foreach (var clip in timeline.LooseIn(state.Deleted))
                clip.Tethered = true;
        }

        // On open (not listing): prime seek indexes before the first graph and read the undo history.
        if (unpackMasks)
        {
            TakeUpSeekIndexes(file, state);
            (state.UndoHistory, state.RedoHistory) = HistoryOf(file);
        }

        return state;
    }

    /// <summary>Decodes the embedded Base64 JPEG; null when invalid (lenient: never fails the project).</summary>
    private static byte[]? ThumbnailOf(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saved .hproj projects, newest first, including newer formats (opened with confirmation). Unreadable files and formats 2-5 are excluded.
    /// </summary>
    public static List<ProjectEntry> ListRecent()
    {
        var entries = new List<ProjectEntry>();
        if (!Directory.Exists(Folder))
            return entries;

        foreach (var file in ProjectFiles().OrderByDescending(f => f.LastWriteTime))
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
                    file.LastWriteTime,
                    state.ThumbnailJpeg));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
            }
        }

        return entries;
    }
}
