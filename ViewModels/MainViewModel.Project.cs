using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Capturing and restoring the whole state of the window: projects and queue editing build on this.
public partial class MainViewModel
{
    /// <summary>Player volume, 0 to 100.</summary>
    [ObservableProperty] private int _volume = 100;

    /// <summary>Frame rate of the loaded source, for HH:MM:SS:FF timecodes. 30 when it is not known.</summary>
    public double SourceFrameRate => _mediaInfo?.Video?.FrameRate is > 0 and var rate ? rate : 30;

    /// <summary>The Dimensions, Filters, Video and Audio tabs as a preset.</summary>
    private EncodingPreset CaptureSettings(string name) => new()
    {
        Name = name,
        OutputWidth = OutputWidth,
        OutputHeight = OutputHeight,
        KeepAspectRatio = KeepAspectRatio,
        CropPercent = [CropLeft, CropTop, CropRight, CropBottom],
        PixelAspectRatio = PixelAspectRatio,
        CustomPixelAspectRatio = CustomPixelAspectRatio,
        Deinterlace = Deinterlace,
        Denoise = Denoise,
        VideoEncoder = VideoEncoder.Name,
        EncoderPreset = EncoderPreset,
        Tune = Tune,
        Profile = Profile,
        Level = Level,
        RateControl = RateControl,
        Quality = Crf,
        TargetBitrate = TargetBitrate,
        Framerate = Framerate,
        CustomFramerate = CustomFramerate,
        Colorspace = Colorspace,
        HardwareDecoding = HardwareDecoding,
        ExtraVideoArguments = ExtraVideoArguments,
        NvencTune = NvencTune,
        NvencMultipass = NvencMultipass,
        NvencSpatialAq = NvencSpatialAq,
        NvencTemporalAq = NvencTemporalAq,
        AmfUsage = AmfUsage,
        QsvIcq = QsvIcq,
        Lookahead = Lookahead,
        TenBit = TenBit,
        AudioEncoder = AudioEncoder,
        AudioBitrate = AudioBitrate,
        MergeAudioTracks = MergeAudioTracks,
        NormalizeAudio = NormalizeAudio,
        DuckAudio = DuckAudio,
        DuckAmountDb = DuckAmountDb,
        FrameEngine = FrameEngine,
        CenterZoom = CenterZoom,
        Layers = Layers.Select(e => e.ToState()).ToList(),
        MainLayer = _mainVideoRow.ToState(),
        BackgroundHidden = _backgroundRow.IsHidden,
        MainVideoIndex = MainVideoIndex,
        LayoutSourceAspectRatio = GetLayoutSourceAspect(),
        AutoCaptions = AutoCaptions,
        CaptionStyle = CaptionStyle.Clone(),
        CenterOffsetX = CenterOffsetX,
        CenterOffsetY = CenterOffsetY,
        UseVerticalResolution = UseVerticalResolution,
        BlurRadius = BlurRadius,
        BlurPasses = BlurPasses,
        BackgroundDim = BackgroundDim,
        AudioTracks = CaptureAudioTracks(),
        CaptionLayer = CaptionLayer.ToState(),
        WhisperPrompt = WhisperPrompt,
        WhisperLanguage = WhisperLanguage,
        WhisperTranslate = WhisperTranslate,
        LutPath = LutPath,
        ColorContrast = ColorContrast,
        ColorBrightness = ColorBrightness,
        ColorSaturation = ColorSaturation,
        ColorGamma = ColorGamma,
        ColorHue = ColorHue,
        ColorRed = ColorRed,
        ColorGreen = ColorGreen,
        ColorBlue = ColorBlue,
        SharpenStrength = SharpenStrength,
        FadeIn = FadeIn,
        FadeOut = FadeOut,

        // Only when the user asked for presets to carry it; see the Automation settings.
        TargetFileSize = AppSettings.Current.SaveTargetSizeInPresets ? TargetFileSize : "",
    };

    /// <summary>What is set track by track: action, codec, bitrate, title, gain and processing.</summary>
    private List<AudioTrackState> CaptureAudioTracks() =>
        AudioTracks.Select(t => new AudioTrackState(t.Index, t.Action, t.Codec, t.Bitrate, t.Title, t.GainDb, t.Filters.IsActive ? t.Filters.Clone() : null,
            t.OffsetSeconds, t.Pieces.Count > 0 ? [.. t.Pieces] : null, t.AutoDuck, t.IsVoice)).ToList();

    /// <summary>A snapshot of the source, the cuts and every setting.</summary>
    public ProjectState CaptureState(string name = "") => new()
    {
        Name = name,
        SavedAt = DateTime.Now,
        SourcePath = SourcePath.Trim().Trim('"'),
        LocalMediaPath = LocalMediaPath,
        DestinationPath = DestinationPath,
        DownloadResolution = DownloadResolution,
        DownloadSubtitles = DownloadSubtitles,
        Segments = Segments.Select(s => new SegmentState(s.Start.TotalMilliseconds, s.End.TotalMilliseconds, s.IsSkipped)).ToList(),
        SnapToIFrames = SnapToIFrames,
        Container = Container,
        WebOptimized = WebOptimized,
        ChapterMarkers = ChapterMarkers,
        ChaptersAtCuts = ChaptersAtCuts,
        TargetFileSize = TargetFileSize,
        Settings = CaptureSettings(name),
        AudioTracks = CaptureAudioTracks(),
        SubtitleTracks = SubtitleTracks.Select(t => new SubtitleTrackState(t.Index, t.Action)).ToList(),
        CaptionAudioPath = CaptionAudioPath,
        CaptionUseExternalAudio = CaptionUseExternalAudio,
        CaptionAudioTrackIndex = CaptionAudioTrack?.Index ?? 0,
        AudioLinked = AudioLinked,
        MainPieces = [.. _mainPieces],
        Deleted = RecycleBin.Select(i => i.Clip).ToList(),
        ManualCommand = IsCommandManuallyEdited ? CommandPreview : null,
    };

    /// <summary>Whether a source is loaded, so there is something worth saving or queueing.</summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(LocalMediaPath);

    /// <summary>Loads the state's source and puts every setting back. Runs as a normal busy operation.</summary>
    public Task RestoreStateAsync(ProjectState state, string doneMessage) => RunOperationAsync(async cancellationToken =>
    {
        SourcePath = state.SourcePath;
        DownloadResolution = Pick(DownloadResolutions, state.DownloadResolution, "Best");
        DownloadSubtitles = state.DownloadSubtitles;

        var (loaded, message) = await LoadMediaAsync(state.SourcePath, state.LocalMediaPath, applyAutomation: false, cancellationToken);
        if (!loaded)
            return message;

        SnapToIFrames = state.SnapToIFrames;
        if (Containers.Contains(state.Container))
            Container = state.Container;
        WebOptimized = state.WebOptimized;
        ChapterMarkers = state.ChapterMarkers;
        ChaptersAtCuts = state.ChaptersAtCuts;

        SelectedPreset = null;
        ApplyPreset(state.Settings);

        // What a preset leaves alone, being about one video and not a look: where the main video sits in time, and how it moves.
        _mainVideoRow.ApplyTiming(state.Settings.MainLayer);
        _mainPieces.Clear();
        _mainPieces.AddRange(state.MainPieces ?? []);
        InvalidateMainClips();
        SetBin(state.Deleted);
        AudioLinked = state.AudioLinked;
        TargetFileSize = state.TargetFileSize;
        CaptionAudioPath = state.CaptionAudioPath ?? "";
        CaptionUseExternalAudio = state.CaptionUseExternalAudio ?? CaptionAudioPath.Length > 0;
        CaptionAudioTrack = AudioTracks.FirstOrDefault(t => t.Index == state.CaptionAudioTrackIndex) ?? AudioTracks.FirstOrDefault();

        // Queue jobs and projects saved before presets carried the tracks keep them here.
        ApplyAudioTrackStates(state.AudioTracks);

        foreach (var saved in state.SubtitleTracks)
        {
            if (SubtitleTracks.FirstOrDefault(t => t.Index == saved.Index) is { } track && track.Actions.Contains(saved.Action))
                track.Action = saved.Action;
        }

        foreach (var segment in state.Segments.OrderBy(s => s.StartMs))
            Segments.Add(new CutSegment(TimeSpan.FromMilliseconds(segment.StartMs), TimeSpan.FromMilliseconds(segment.EndMs)) { IsSkipped = segment.Skipped });

        if (!string.IsNullOrWhiteSpace(state.DestinationPath))
            DestinationPath = state.DestinationPath;

        // Last, so that nothing above regenerates over a hand-edited command.
        IsCommandManuallyEdited = false;
        GenerateCommand();
        if (state.ManualCommand is { } manualCommand)
            CommandPreview = manualCommand;

        _undo.Clear();
        _redo.Clear();
        MarkSaved();
        return GetLayoutAspectWarning() is { } warning ? $"{doneMessage}. {warning}" : doneMessage;
    });

    // ----- Encoding presets as files -----

    private static readonly System.Text.Json.JsonSerializerOptions PresetFileOptions = new() { WriteIndented = true };

    /// <summary>Writes the settings as they are now to a file, as one encoding preset that can be imported elsewhere.</summary>
    public void ExportPreset(string path)
    {
        try
        {
            var name = PresetName.Trim().Length > 0 ? PresetName.Trim() : Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(CaptureSettings(name), PresetFileOptions));
            StatusText = $"Exported the encoding preset \"{name}\" to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not write {path}: {ex.Message}";
        }
    }

    /// <summary>Takes an encoding preset from a file into the list (replacing one of the same name), and applies it.</summary>
    public void ImportPreset(string path)
    {
        try
        {
            if (System.Text.Json.JsonSerializer.Deserialize<EncodingPreset>(File.ReadAllText(path)) is not { } preset || string.IsNullOrWhiteSpace(preset.VideoEncoder))
            {
                StatusText = $"Not an encoding preset: {path}";
                return;
            }

            if (string.IsNullOrWhiteSpace(preset.Name))
                preset.Name = Path.GetFileNameWithoutExtension(path);
            var existing = Presets.ToList().FindIndex(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                Presets[existing] = preset;
            else
                Presets.Add(preset);
            PresetStore.Save(Presets);
            SelectedPreset = preset;
            StatusText = $"Imported the encoding preset \"{preset.Name}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText = $"Could not read the preset: {ex.Message}";
        }
    }

    // ----- Projects -----

    /// <summary>The saved projects by name, the most recently saved first: the Swap Project list.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>The project that is open, when it is one of the saved ones.</summary>
    [ObservableProperty] private string? _selectedProject;

    /// <summary>Set while the list is refilled or follows a load: the choice changing then is not the user choosing.</summary>
    public bool IsListingProjects { get; private set; }

    /// <summary>Reads the Projects folder again (names only: no project is opened for it), as the list is opened.</summary>
    public void RefreshRecentProjects(string? current = null)
    {
        current ??= SelectedProject;
        IsListingProjects = true;
        try
        {
            RecentProjects.Clear();
            if (Directory.Exists(ProjectStore.Folder))
            {
                foreach (var file in new DirectoryInfo(ProjectStore.Folder).EnumerateFiles("*" + ProjectStore.Extension).OrderByDescending(f => f.LastWriteTime).Take(25))
                    RecentProjects.Add(Path.GetFileNameWithoutExtension(file.Name));
            }

            SelectedProject = current is not null && RecentProjects.Contains(current) ? current : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            IsListingProjects = false;
        }
    }

    /// <summary>Saves the current state as a project and returns a line for the status bar.</summary>
    public string SaveProject(string name)
    {
        try
        {
            var path = ProjectStore.Save(CaptureState(name.Trim()));
            MarkSaved();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            return StatusText = $"Project saved: {Path.GetFileNameWithoutExtension(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusText = $"Could not save the project: {ex.Message}";
        }
    }

    public Task LoadProjectAsync(string filePath)
    {
        ProjectState state;
        try
        {
            state = ProjectStore.Load(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            StatusText = $"Could not open the project: {ex.Message}";
            return Task.CompletedTask;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);

        // An encode is running: leave the window alone and line the project up behind it instead.
        if (IsBusy)
            return QueueProjectInBackgroundAsync(state, name);

        EditingJob = null;
        RefreshRecentProjects(name);
        return RestoreStateAsync(state, $"Project loaded: {name}");
    }

    // ----- Manual cut entry -----

    /// <summary>The I-frame at or before a time, or null when there is none (or no I-frame index).</summary>
    public double? GetIFrameFloor(double seconds, double tolerance)
    {
        var index = IFrames.FindLastIndex(k => k <= seconds + tolerance);
        return index >= 0 ? IFrames[index] : null;
    }

    /// <summary>The I-frame at or after a time, or null when there is none (or no I-frame index).</summary>
    public double? GetIFrameCeiling(double seconds, double tolerance)
    {
        var index = IFrames.FindIndex(k => k >= seconds - tolerance);
        return index >= 0 ? IFrames[index] : null;
    }

    /// <summary>
    /// Adds a segment typed in by hand. With snapping on it grows to the surrounding I-frames, the same
    /// "prefer longer" rule used when the checkbox is ticked. Returns an error message, or null on success.
    /// </summary>
    public string? AddManualSegment(double startSeconds, double stopSeconds)
    {
        if (SnapToIFrames && IFrames.Count > 0)
        {
            // Half a frame of slack: a timecode cannot name an I-frame's timestamp more exactly than that.
            var tolerance = 0.5 / SourceFrameRate;
            startSeconds = GetIFrameFloor(startSeconds, tolerance) ?? startSeconds;
            stopSeconds = GetIFrameCeiling(stopSeconds, tolerance) ?? stopSeconds;
        }

        var duration = SequenceSeconds;
        if (duration > 0)
            stopSeconds = Math.Min(stopSeconds, duration);
        if (stopSeconds <= startSeconds)
            return "The stop point has to come after the start point.";
        if (duration > 0 && startSeconds >= duration)
            return "The start point is past the end of the video.";

        var segment = new CutSegment(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(stopSeconds));
        Checkpoint("add a segment");
        InsertSegment(segment);
        PendingStartMs = null;
        Log($"Added segment {segment.Display}");
        return null;
    }

    /// <summary>Adds a segment, keeping the list ordered by start time.</summary>
    private void InsertSegment(CutSegment segment)
    {
        var index = 0;
        while (index < Segments.Count && Segments[index].Start <= segment.Start)
            index++;
        Segments.Insert(index, segment);
    }
}
