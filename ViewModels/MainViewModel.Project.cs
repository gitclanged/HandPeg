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
        AudioEncoder = AudioEncoder,
        AudioBitrate = AudioBitrate,
        MergeAudioTracks = MergeAudioTracks,
        NormalizeAudio = NormalizeAudio,
        DuckAudio = DuckAudio,
        FrameEngine = FrameEngine,
        CenterZoom = CenterZoom,
        UiElements = UiElements.Select(e => e.ToState()).ToList(),
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
        AudioTracks.Select(t => new AudioTrackState(t.Index, t.Action, t.Codec, t.Bitrate, t.Title, t.GainDb, t.Filters.IsActive ? t.Filters.Clone() : null)).ToList();

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
        Segments = Segments.Select(s => new SegmentState(s.Start.TotalMilliseconds, s.End.TotalMilliseconds)).ToList(),
        SnapToKeyframes = SnapToKeyframes,
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

        SnapToKeyframes = state.SnapToKeyframes;
        if (Containers.Contains(state.Container))
            Container = state.Container;
        WebOptimized = state.WebOptimized;
        ChapterMarkers = state.ChapterMarkers;
        ChaptersAtCuts = state.ChaptersAtCuts;

        SelectedPreset = null;
        ApplyPreset(state.Settings);
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
            Segments.Add(new CutSegment(TimeSpan.FromMilliseconds(segment.StartMs), TimeSpan.FromMilliseconds(segment.EndMs)));

        if (!string.IsNullOrWhiteSpace(state.DestinationPath))
            DestinationPath = state.DestinationPath;

        // Last, so that nothing above regenerates over a hand-edited command.
        IsCommandManuallyEdited = false;
        GenerateCommand();
        if (state.ManualCommand is { } manualCommand)
            CommandPreview = manualCommand;

        return GetLayoutAspectWarning() is { } warning ? $"{doneMessage}. {warning}" : doneMessage;
    });

    // ----- Projects -----

    /// <summary>Saves the current state as a project and returns a line for the status bar.</summary>
    public string SaveProject(string name)
    {
        try
        {
            var path = ProjectStore.Save(CaptureState(name.Trim()));
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
        return RestoreStateAsync(state, $"Project loaded: {name}");
    }

    // ----- Manual cut entry -----

    /// <summary>The keyframe at or before a time, or null when there is none (or no keyframe index).</summary>
    public double? GetKeyframeFloor(double seconds, double tolerance)
    {
        var index = Keyframes.FindLastIndex(k => k <= seconds + tolerance);
        return index >= 0 ? Keyframes[index] : null;
    }

    /// <summary>The keyframe at or after a time, or null when there is none (or no keyframe index).</summary>
    public double? GetKeyframeCeiling(double seconds, double tolerance)
    {
        var index = Keyframes.FindIndex(k => k >= seconds - tolerance);
        return index >= 0 ? Keyframes[index] : null;
    }

    /// <summary>
    /// Adds a segment typed in by hand. With snapping on it grows to the surrounding keyframes, the same
    /// "prefer longer" rule used when the checkbox is ticked. Returns an error message, or null on success.
    /// </summary>
    public string? AddManualSegment(double startSeconds, double stopSeconds)
    {
        if (SnapToKeyframes && Keyframes.Count > 0)
        {
            // Half a frame of slack: a timecode cannot name a keyframe's timestamp more exactly than that.
            var tolerance = 0.5 / SourceFrameRate;
            startSeconds = GetKeyframeFloor(startSeconds, tolerance) ?? startSeconds;
            stopSeconds = GetKeyframeCeiling(stopSeconds, tolerance) ?? stopSeconds;
        }

        var duration = DurationMs / 1000;
        if (duration > 0)
            stopSeconds = Math.Min(stopSeconds, duration);
        if (stopSeconds <= startSeconds)
            return "The stop point has to come after the start point.";
        if (duration > 0 && startSeconds >= duration)
            return "The start point is past the end of the video.";

        var segment = new CutSegment(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(stopSeconds));
        InsertSegment(segment);
        PendingStartMs = null;
        StatusText = $"Added segment {segment.Display}";
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
