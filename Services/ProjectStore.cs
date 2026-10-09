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

// ----- The project file (.hproj), format 2 -----
// A project is a sequence: cuts, the main video's clip, and tracks of clips. The file is laid out that way.
// What a track's clips share (what they show, their look, their filters) is written once, on the track; a clip
// carries only what is its own: when it is on, where it sits, and its keyframes. Nothing is written that is
// still at its default, and times and keyframes are plain arrays of numbers.

/// <summary>A project as it is written to disk.</summary>
public sealed class ProjectFile
{
    public const string FormatName = "handpeg-project";
    public const int CurrentVersion = 2;

    // No defaults here: a file that does not say what it is must not pass for one that does.
    public string? Format { get; set; }
    public int Version { get; set; }

    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public ProjectSource Source { get; set; } = new();
    public ProjectOutput Output { get; set; } = new();
    public ProjectSequence Sequence { get; set; } = new();

    /// <summary>Everything that is not on the timeline: the frame, the encoders, the filters, the caption style.</summary>
    public EncodingPreset Settings { get; set; } = new();
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
    public string TargetFileSize { get; set; } = "";
    public string? ManualCommand { get; set; }
}

public sealed class ProjectSequence
{
    /// <summary>The kept stretches of the timeline, each as [start, end] in milliseconds; a third number of 1 marks one that is skipped.</summary>
    public List<double[]> Cuts { get; set; } = [];

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

public sealed class ProjectClip
{
    public string? Name { get; set; }
    public double Start { get; set; }
    public double Duration { get; set; }
    public double Offset { get; set; }
    public bool Hidden { get; set; }
    public double AudioOffset { get; set; }

    /// <summary>Where it sits on the frame: left, top, width, and (for one not held to its shape) height, as fractions of the frame.</summary>
    public double[] Place { get; set; } = [];

    // Keyframes, each property as time, value, time, value...
    public double[]? KeysX { get; set; }
    public double[]? KeysY { get; set; }
    public double[]? KeysScale { get; set; }
    public double[]? KeysRotation { get; set; }
}

/// <summary>
/// Project files live in the Projects folder (see <see cref="AppPaths"/>), as .hproj: JSON in the layout above.
/// Projects saved by versions before 2.0 (.txt, in another layout) are not read.
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".hproj";

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
        }
        catch (Exception ex) when (ex is MissingMethodException or System.Reflection.TargetInvocationException)
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

    /// <summary>Saves the state under its name, replacing a project of the same name.</summary>
    public static string Save(ProjectState state)
    {
        Directory.CreateDirectory(Folder);

        var fileName = string.Concat(state.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (fileName.Length == 0)
            fileName = "Project";

        var path = Path.Combine(Folder, fileName + Extension);
        File.WriteAllText(path, Serialize(state));
        return path;
    }

    public static string Serialize(ProjectState state) => JsonSerializer.Serialize(ToFile(state), JsonOptions);

    public static ProjectState Load(string filePath) => Deserialize(File.ReadAllText(filePath));

    /// <summary>Reads a project from its text. Anything that is not a format 2 project is refused, with the reason.</summary>
    public static ProjectState Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions) ?? throw new InvalidDataException("The project file is empty.");
        if (file.Format != ProjectFile.FormatName)
            throw new InvalidDataException("This is not a HandPeg 2.0 project. Projects saved by earlier versions cannot be opened.");
        if (file.Version != ProjectFile.CurrentVersion)
            throw new InvalidDataException($"This project is in format {file.Version}; this version of HandPeg reads format {ProjectFile.CurrentVersion}.");
        return FromFile(file);
    }

    /// <summary>Whether a piece of JSON says it is a project of the format this version reads.</summary>
    public static bool IsProject(System.Text.Json.Nodes.JsonObject node) =>
        node["format"] is System.Text.Json.Nodes.JsonValue format && format.TryGetValue<string>(out var name) && name == ProjectFile.FormatName
        && node["version"] is System.Text.Json.Nodes.JsonValue version && version.TryGetValue<int>(out var number) && number == ProjectFile.CurrentVersion;

    // ----- Between the window's state and the file -----

    private static double[]? Flatten(List<Keyframe>? keys) => keys is { Count: > 0 } ? keys.SelectMany(k => new[] { k.Time, k.Value }).ToArray() : null;

    private static List<Keyframe>? Unflatten(double[]? numbers)
    {
        if (numbers is not { Length: >= 2 })
            return null;

        var keys = new List<Keyframe>(numbers.Length / 2);
        for (var i = 0; i + 1 < numbers.Length; i += 2)
            keys.Add(new Keyframe(numbers[i], numbers[i + 1]));
        return keys;
    }

    private static LayerState Copy(LayerState state) => JsonSerializer.Deserialize<LayerState>(JsonSerializer.Serialize(state))!;

    private static ProjectClip ToClip(LayerState layer, string trackName) => new()
    {
        Name = layer.Name == trackName ? null : layer.Name,
        Start = layer.StartTime,
        Duration = layer.Duration,
        Offset = layer.MediaOffset,
        Hidden = layer.IsHidden,
        AudioOffset = layer.AudioOffset,
        Place = layer.LockAspectRatio || layer.SizeHeight <= 0
            ? [layer.PositionX, layer.PositionY, layer.SizeWidth]
            : [layer.PositionX, layer.PositionY, layer.SizeWidth, layer.SizeHeight],
        KeysX = Flatten(layer.KeysX),
        KeysY = Flatten(layer.KeysY),
        KeysScale = Flatten(layer.KeysScale),
        KeysRotation = Flatten(layer.KeysRotation),
    };

    /// <summary>A track's shared look: one of its clips with everything that is a clip's own taken out.</summary>
    private static LayerState ToLook(LayerState layer)
    {
        var look = Copy(layer);
        (look.StartTime, look.Duration, look.MediaOffset, look.IsHidden, look.AudioOffset, look.TrackId) = (0, 0, 0, false, 0, 0);
        (look.PositionX, look.PositionY, look.SizeWidth, look.SizeHeight) = (0, 0, 0, 0);
        (look.KeysX, look.KeysY, look.KeysScale, look.KeysRotation) = (null, null, null, null);
        return look;
    }

    private static LayerState ToLayer(LayerState look, ProjectClip clip, int trackId)
    {
        var layer = Copy(look);
        double At(int index) => clip.Place.Length > index ? clip.Place[index] : 0;
        (layer.Name, layer.TrackId) = (clip.Name ?? look.Name, trackId);
        (layer.StartTime, layer.Duration, layer.MediaOffset, layer.IsHidden, layer.AudioOffset) = (clip.Start, clip.Duration, clip.Offset, clip.Hidden, clip.AudioOffset);
        (layer.PositionX, layer.PositionY, layer.SizeWidth, layer.SizeHeight) = (At(0), At(1), At(2), At(3));
        (layer.KeysX, layer.KeysY, layer.KeysScale, layer.KeysRotation) = (Unflatten(clip.KeysX), Unflatten(clip.KeysY), Unflatten(clip.KeysScale), Unflatten(clip.KeysRotation));
        return layer;
    }

    private static ProjectFile ToFile(ProjectState state)
    {
        var settings = state.Settings;
        var main = settings.MainLayer ?? new LayerState { Kind = LayerKind.MainVideo, Name = "Main Video" };

        // Clips of one track lie together in the list, and are written as that track.
        var tracks = new List<ProjectTrack>();
        foreach (var layer in settings.Layers)
        {
            if (tracks.Count > 0 && tracks[^1].Id == layer.TrackId && layer.TrackId > 0 && tracks[^1].Look.Kind == layer.Kind && tracks[^1].Look.ImagePath == layer.ImagePath)
                tracks[^1].Clips.Add(ToClip(layer, tracks[^1].Look.Name));
            else
                tracks.Add(new ProjectTrack { Id = layer.TrackId, Look = ToLook(layer), Clips = { ToClip(layer, layer.Name) } });
        }

        // The settings without what the sequence already holds.
        var rest = JsonSerializer.Deserialize<EncodingPreset>(JsonSerializer.Serialize(settings))!;
        (rest.Name, rest.Layers, rest.MainLayer, rest.MainVideoIndex, rest.BackgroundHidden, rest.AudioTracks) = ("", [], null, 0, false, []);

        return new ProjectFile
        {
            Format = ProjectFile.FormatName,
            Version = ProjectFile.CurrentVersion,
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
                TargetFileSize = state.TargetFileSize,
                ManualCommand = state.ManualCommand,
            },
            Sequence = new ProjectSequence
            {
                Cuts = state.Segments.Select(s => s.Skipped ? new[] { s.StartMs, s.EndMs, 1 } : [s.StartMs, s.EndMs]).ToList(),
                SnapToIFrames = state.SnapToIFrames,
                AudioLinked = state.AudioLinked,
                Main = new ProjectTrack
                {
                    Look = ToLook(main),
                    Clips = [ToClip(main, main.Name), .. state.MainPieces.Select(p => new ProjectClip { Start = p.Start, Duration = p.Duration, Offset = p.Offset })],
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
    }

    private static ProjectState FromFile(ProjectFile file)
    {
        var sequence = file.Sequence;
        var settings = file.Settings;
        settings.Name = file.Name;
        settings.Layers = sequence.Tracks.SelectMany(track => track.Clips.Select(clip => ToLayer(track.Look, clip, track.Id))).ToList();
        settings.MainLayer = sequence.Main.Clips.Count > 0 ? ToLayer(sequence.Main.Look, sequence.Main.Clips[0], 0) : sequence.Main.Look;
        (settings.MainVideoIndex, settings.BackgroundHidden, settings.AudioTracks) = (sequence.MainIndex, sequence.BackgroundHidden, sequence.Audio);

        return new ProjectState
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
            TargetFileSize = file.Output.TargetFileSize,
            ManualCommand = file.Output.ManualCommand,
            Segments = sequence.Cuts.Where(c => c.Length >= 2).Select(c => new SegmentState(c[0], c[1], c.Length > 2 && c[2] != 0)).ToList(),
            SnapToIFrames = sequence.SnapToIFrames,
            AudioLinked = sequence.AudioLinked,
            MainPieces = sequence.Main.Clips.Skip(1).Select(c => new MainPiece(c.Start, c.Duration, c.Offset)).ToList(),
            Deleted = sequence.Deleted,
            Settings = settings,
            AudioTracks = sequence.Audio,
            SubtitleTracks = sequence.Subtitles,
            CaptionAudioPath = sequence.CaptionAudioPath,
            CaptionUseExternalAudio = sequence.CaptionUseExternalAudio,
            CaptionAudioTrackIndex = sequence.CaptionAudioTrack,
        };
    }

    /// <summary>Saved projects, most recently saved first. Files that cannot be read are left out.</summary>
    public static List<ProjectEntry> ListRecent()
    {
        var entries = new List<ProjectEntry>();
        if (!Directory.Exists(Folder))
            return entries;

        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTime))
        {
            try
            {
                var state = Load(file.FullName);
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
