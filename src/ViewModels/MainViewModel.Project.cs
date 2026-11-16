using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Whole-session state capture and restore, used by projects, tabs and queue editing.
public partial class MainViewModel
{
    /// <summary>Player volume, 0 to 100.</summary>
    [ObservableProperty] private int _volume = 100;

    /// <summary>Frame rate of the last opened file (adopted by an empty Sequence); 30 when unknown.</summary>
    public double SourceFrameRate => _mediaInfo?.Video?.FrameRate is > 0 and var rate ? rate : 30;

    /// <summary>
    /// The edit-snapping rate (<see cref="TimelineDocument.FrameRate"/>), or the last file's while unset; used by frame stepping,
    /// timecode readouts, the Timeline precision step and typed timecodes.
    /// </summary>
    public double SequenceFps => Document.FrameRate.IsValid ? Document.FrameRate.Fps : SourceFrameRate;

        /// <summary>Current encode settings as a preset (Settings: "Use the Current Video and Audio Settings").</summary>
    public EncodingPreset CurrentEncodingSettings() => CaptureSettings("");

    private EncodingPreset CaptureSettings(string name) => new()
    {
        Name = name,
        OutputWidth = OutputWidth,
        OutputHeight = OutputHeight,
        KeepAspectRatio = KeepAspectRatio,
        // Crop and placement of the preset's template clip (TemplateBase); projects keep per-clip values in their timeline.
        CropPercent = SequenceCropPercent(),
        PixelAspectRatio = PixelAspectRatio,
        CustomPixelAspectRatio = CustomPixelAspectRatio,
        Deinterlace = Deinterlace,
        Denoise = Denoise,
        VideoEncoder = VideoEncoder.Name,
        EncoderPreset = EncoderPreset,
        SpeedFirst = _speedFirst,
        Tune = Tune,
        Profile = Profile,
        Level = Level,
        RateControl = RateControl,
        Quality = Crf,
        TargetBitrate = TargetBitrate,
        Framerate = Framerate,
        CustomFramerate = CustomFramerate,
        Colorspace = Colorspace,
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
        AudioSampleRate = AudioSampleRate,
        AudioChannels = AudioChannels == AudioChannelChoice.Stereo ? null : AudioChannels,
        MergeAudioTracks = MergeAudioTracks,
        NormalizeAudio = NormalizeAudio,
        LoudnessTarget = NormalizeLoudness(LoudnessTarget) == -16 ? 0 : NormalizeLoudness(LoudnessTarget),
        DuckAmountDb = DuckAmountDb,
        CenterZoom = TemplatePlace().Zoom,
        // Preset layout only (ProjectStore omits it for projects): the template clip's pieces, look, below-track piece count and
        // placement as legacy Center values.
        Layers = TemplateLayers(),
        MainLayer = TemplateBase()?.ToState(),
        BackgroundHidden = _backgroundRow.IsHidden,
        MainVideoIndex = TemplateMainIndex(),
        LayoutSourceAspectRatio = GetLayoutSourceAspect(),
        AutoCaptions = AutoCaptions,
        CaptionStyle = CaptionStyle.Clone(),
        CenterOffsetX = TemplatePlace().OffsetX,
        CenterOffsetY = TemplatePlace().OffsetY,
        UseVerticalResolution = UseVerticalResolution,
        BlurRadius = BlurRadius,
        BlurPasses = BlurPasses,
        BackgroundDim = BackgroundDim,
        BackgroundZoom = BackgroundZoom,
        BackgroundEffects = CaptureBackgroundEffects(),
        BackgroundEffectsBypassed = _backgroundRow.EffectsBypassed,
        AudioTracks = CaptureAudioTracks(),
        CaptionLayer = CaptionLayer.ToState(),
        WhisperPrompt = WhisperPrompt,
        WhisperLanguage = WhisperLanguage,
        WhisperTranslate = WhisperTranslate,
        LutPath = LutPath,
        SequenceEffects = CaptureSequenceEffects(),
        SequenceEffectsBypassed = SequenceEffectsBypassed,
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

        // Only when Automation settings enable it, and only a typed size (bitrate-derived sizes are just a displayed estimate).
        TargetFileSize = AppSettings.Current.SaveTargetSizeInPresets && _sizeDrivesBitrate ? TargetFileSize : "",

        // Output format: container and Web Optimized, like the built-ins.
        Container = Container,
        WebOptimized = WebOptimized,
    };

    /// <summary>
    /// Template clip for a saved layout: the first top-level video clip on the lowest track, or the selected video clip first when
    /// <paramref name="selected"/> (manual style save). Null when none. Selection-independent otherwise, since presets feed the fingerprint.
    /// </summary>
    private Layer? TemplateBase(bool selected = false) => selected ? TemplateTargets().FirstOrDefault() ?? FirstVideoView() : FirstVideoView();

    /// <summary>The clip's template as saved: its pieces (below-track ones first), each offset from the clip start.</summary>
    private List<TemplatePiece> TemplatePieces(bool selected = false) =>
        TemplateBase(selected) is { } view && ClipOf(view) is { } clip && TemplateFrom(clip) is { } template ? [.. template.Pieces] : [];

    /// <summary>The template clip's own pieces as a layout stores them.</summary>
    private List<LayerState> TemplateLayers(bool selected = false) => TemplatePieces(selected).ConvertAll(SavedPiece);

    /// <summary>Number of layout pieces below the clip's track.</summary>
    private int TemplateMainIndex(bool selected = false) => TemplatePieces(selected).Count(p => p.Under);

    /// <summary>
    /// Template clip placement as legacy Center values: width as zoom, centre offset from frame centre in frame percent;
    /// full-width centred when there is no clip.
    /// </summary>
    private (double Zoom, double OffsetX, double OffsetY) TemplatePlace(bool selected = false)
    {
        if (TemplateBase(selected) is not { } view || FrameWidth <= 0 || FrameHeight <= 0)
            return (1, 0, 0);

        var (x, y, width, height) = view.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        return (view.SizeWidth, ((x + width / 2.0) / FrameWidth - 0.5) * 100, ((y + height / 2.0) / FrameHeight - 0.5) * 100);
    }

    /// <summary>
    /// A piece as saved: offset and duration on its clip (0 = whole clip). Cutouts of the clip's own file are stored as generic
    /// LayerKind.Video cut-outs (crop + box) so the layout applies to any file (FileCutoutOf rebuilds them).
    /// </summary>
    private static LayerState SavedPiece(TemplatePiece piece)
    {
        var state = TimelineTransaction.CopyOf(piece.State);
        (state.StartTime, state.Duration) = (piece.Offset, piece.Length);
        if (piece.FollowsBase && state.Kind == LayerKind.VideoFile)
        {
            var (left, top, right, bottom) = Layer.HeldCrop(state);
            state.Kind = LayerKind.Video;
            (state.SourceX, state.SourceY, state.SourceWidth, state.SourceHeight) = (left, top, 1 - left - right, 1 - top - bottom);
            (state.CropLeft, state.CropTop, state.CropRight, state.CropBottom) = (0, 0, 0, 0);
            (state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaOffset, state.MediaDuration) = ("", 0, 0, 0, 0);

            // A file Cutout's drawn mask is already spread over the whole picture (FileCutoutOf): saved as a frame mask, so applying the
            // preset again does not squeeze it into the region a second time (the shrinking double crop).
            if (state.CustomMask && MaskStore.IsDrawn(MaskStore.SourceOf(state.MaskPath, state.MaskShapes)))
                state.MaskFrame = true;
        }

        return state;
    }

    /// <summary>Per-track output settings: action, codec, bitrate, title (mixing lives on Sub-Lanes and is neutral here).</summary>
    private List<AudioTrackState> CaptureAudioTracks() =>
        AudioTracks.Select(t => new AudioTrackState(t.Index, t.Action, t.Codec, t.Bitrate, t.Title, t.GainDb, t.Filters.IsDefault ? null : t.Filters.Clone(),
            t.OffsetSeconds, t.Pieces.Count > 0 ? [.. t.Pieces] : null, t.AutoDuck, t.IsVoice)).ToList();

    /// <summary>
    /// Snapshot of source, timeline and all settings. The timeline (including looks) is deep-copied so holders (queued jobs,
    /// background autosaves) are isolated from later edits.
    /// </summary>
    public ProjectState CaptureState(string name = "") => CaptureState(name, liveTimeline: false);

    /// <summary>
    /// <see cref="CaptureState(string)"/>; <paramref name="liveTimeline"/> references the live Sequence instead of copying (DetachForTab,
    /// fingerprints); without <paramref name="copyBin"/> the Recycle Bin clips are live references too (read-only use).
    /// </summary>
    private ProjectState CaptureState(string name, bool liveTimeline, bool copyBin = true)
    {
        FlushLooks();
        return new ProjectState
        {
            Name = name,
            SavedAt = DateTime.Now,
            SourcePath = SourcePath.Trim().Trim('"'),
            LocalMediaPath = LocalMediaPath,
            DestinationPath = DestinationPath,
            DownloadResolution = DownloadResolution,
            DownloadSubtitles = DownloadSubtitles,
            Timeline = liveTimeline ? Document : Document.Clone(deep: true),
            SnapToIFrames = SnapToIFrames,
            Container = Container,
            WebOptimized = WebOptimized,
            ChapterMarkers = ChapterMarkers,
            ChaptersAtCuts = ChaptersAtCuts,
            ChaptersAtMarkers = ChaptersAtMarkers,
            Subtitles = CaptureSubtitles(),
            // Typed size only; bitrate-derived sizes are recomputed on open.
            TargetFileSize = _sizeDrivesBitrate ? TargetFileSize : "",
            Settings = CaptureSettings(name),
            AudioTracks = CaptureAudioTracks(),
            SubtitleTracks = SubtitleTracks.Select(t => new SubtitleTrackState(t.Index, t.Action)).ToList(),
            CaptionAudioPath = CaptionAudioPath,
            CaptionUseExternalAudio = CaptionUseExternalAudio,
            CaptionAudioTrackIndex = ResolveCaptionSource() is { Kind: CaptionSourceKind.Clip, Stream: >= 0 } listened && SameFile(listened.Path, OpenedFile) ? listened.Stream : 0,
            Deleted = copyBin ? RecycleBin.Select(i => i.Clip with { Clip = CopyOfClip(i.Clip.Clip) }).ToList() : RecycleBin.Select(i => i.Clip).ToList(),
            ManualCommand = IsCommandManuallyEdited ? CommandPreview : null,
        };
    }

    /// <summary>
    /// Prepares a state's timeline for open: a private copy, source-file clips re-pointed at this session's path (downloads may
    /// move), and for hand-edited files clips sorted by start with unique ids (search invariants). Remaining issues are logged.
    /// </summary>
    private TimelineDocument TimelineToOpen(ProjectState state)
    {
        var timeline = state.Timeline.Clone(deep: true);
        var ids = new HashSet<Guid>();
        foreach (var track in timeline.Tracks)
        {
            track.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var clip in track.Clips)
            {
                if (!ids.Add(clip.Id))
                    clip.Id = Guid.NewGuid();
                if (!string.IsNullOrEmpty(state.LocalMediaPath) && !string.Equals(state.LocalMediaPath, LocalMediaPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(clip.SourcePath, state.LocalMediaPath, StringComparison.OrdinalIgnoreCase))
                {
                    clip.SourcePath = LocalMediaPath;
                }
            }
        }

        // Legacy projects stored one sequence-wide crop in settings: copy it into each clip lacking one before ConvertLegacy.
        // Looks are immutable, so each clip gets a new copy.
        var (left, top, right, bottom) = PresetCropPercent(state.Settings);
        if (left + top + right + bottom > 0)
        {
            var mainClips = timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.ContentKind == LayerKind.MainVideo).ToList();
            if (mainClips.TrueForAll(c => c.State.CropLeft + c.State.CropTop + c.State.CropRight + c.State.CropBottom <= 0))
            {
                var crop = HeldCrop(left / 100, top / 100, right / 100, bottom / 100);
                foreach (var clip in mainClips)
                {
                    var look = clip.State.ShallowCopy();
                    (look.CropLeft, look.CropTop, look.CropRight, look.CropBottom) = crop;
                    clip.State = look;
                }
            }
        }

        // Re-point bin clips to the source file's current path the same way, once.
        if (!string.IsNullOrEmpty(state.LocalMediaPath) && !string.IsNullOrEmpty(LocalMediaPath)
            && !string.Equals(state.LocalMediaPath, LocalMediaPath, StringComparison.OrdinalIgnoreCase)
            && timeline.MediaPool.FindIndex(f => string.Equals(TimelineDocument.PoolPath(f), state.LocalMediaPath, StringComparison.OrdinalIgnoreCase)) is >= 0 and var stale)
        {
            if (timeline.PoolHolds(LocalMediaPath))
                timeline.MediaPool.RemoveAt(stale);
            else
                timeline.MediaPool[stale] = LocalMediaPath;
        }

        // Projects predating Project Media have an empty bin: seed it with clip files in track order, on this copy (opening never edits).
        if (timeline.MediaPool.Count == 0)
        {
            foreach (var clip in timeline.Tracks.SelectMany(t => t.Clips))
            {
                if (TimelineDocument.PoolFileOf(clip) is { } file && !timeline.PoolHolds(file))
                    timeline.MediaPool.Add(file);
            }
        }

        if (timeline.FindProblems() is { Count: > 0 } problems)
            AppLog.Write($"The project's timeline has {problems.Count} problem(s): {string.Join(" ", problems.Take(5))}");
        return timeline;
    }

    /// <summary>Preset crop in percent (left, top, right, bottom): TemplateBase's, else the Crop boxes'.</summary>
    private double[] SequenceCropPercent()
    {
        var (left, top, right, bottom) = TemplateBase() is { } view ? CropOf(view) : BoxCrop();
        return [Math.Round(left * 100, 2), Math.Round(top * 100, 2), Math.Round(right * 100, 2), Math.Round(bottom * 100, 2)];
    }

    /// <summary>
    /// Whether there is anything to save or queue: an opened file or any clip. Re-raised on document changes (NoteDocumentShape).
    /// </summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(LocalMediaPath) || Document.Tracks.Any(t => t.Clips.Count > 0);

    /// <summary>
    /// Description of the legacy shape the last project was converted from ("19 clips of the main video and 57 cut-outs of it":
    /// ProjectStore.OldShapeOf, ConvertLegacy); null for current-format projects. Read by the window (<see cref="SavesConvertedProjects"/>) and harnesses.
    /// </summary>
    public string? ConvertedFrom { get; private set; }

    /// <summary>
    /// Whether a converted legacy project immediately overwrites its file (the legacy file is not kept). Set by the window only;
    /// harnesses (SeekBench, AvSync) open projects read-only.
    /// </summary>
    public bool SavesConvertedProjects { get; set; }

    /// <summary>
    /// One-time legacy main-video conversion on open (LegacyMainVideo.Migrate): main-video clips become file clips in their drawn
    /// boxes, cut-outs become child pieces, stream mixing moves to Sub-Lanes (then neutralised), and the bin is converted too.
    /// Returns whether anything changed (the undo history is then dropped).
    /// </summary>
    private bool ConvertLegacy(TimelineDocument timeline, ProjectState state)
    {
        if (!LegacyMainVideo.Needs(timeline) && !ProjectStore.HoldsOldKinds(timeline, state.Deleted))
            return false;

        var context = new LegacyMainVideoContext
        {
            PrimaryPath = LocalMediaPath,
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            MediaSeconds = MainMediaSeconds,
            SourceHasNoVideo = _mediaInfo is { Video: null },
            BackgroundHidden = state.Settings.BackgroundHidden,
            CenterRect = null,
            Streams = AudioTracks,
            Bin = state.Deleted,
        };

        // Legacy layout frame: old natural size (main video cropped by its first clip) through the project's Resolution & Cropping.
        var (frameWidth, frameHeight) = FrameSizeFor(LegacyMainVideo.NaturalSizeOf(timeline, context));
        LegacyMainVideo.Migrate(timeline, context with { FrameWidth = frameWidth, FrameHeight = frameHeight });
        AppLog.Write($"The project's main video was converted to clips of its file (5.2-AuditB), on a {frameWidth} x {frameHeight} frame");
        return true;
    }

    /// <summary>
    /// Loads the state's source and restores every setting as a busy operation. Legacy states are converted on the way
    /// (<see cref="ConvertLegacy"/>, <see cref="ConvertedFrom"/>); queued jobs and undo steps are always current-format.
    /// </summary>
    /// <param name="fromFile">
    /// State read from disk (project, tab cache, bundle): saved commands and Extra options require confirmation
    /// (<see cref="CommandTrust"/>). False when re-editing this session's queued job.
    /// </param>
    public Task RestoreStateAsync(ProjectState state, string doneMessage, bool fromFile = true) => RestoreStateAsync(state, doneMessage, fromFile, opened: null);

    // As above; <paramref name="opened"/> runs once the project is in place (not when its source failed to load, or it never ran).
    private Task RestoreStateAsync(ProjectState state, string doneMessage, bool fromFile, Action? opened) => RunOperationAsync("Opening a project", WorkLane.Interactive, loadsDocument: true, operation: async cancellationToken =>
    {
        if (fromFile)
        {
            // Built-in Export Preset Extra options (ProRes/DNxHR profiles, all-intra GOP) are trusted: they are not from the file.
            var extras = state.Settings?.ExtraVideoArguments;
            if (extras is not null && ExportPresetCatalog.BuiltIns.Any(p => string.Equals(p.ExtraVideoArguments, extras, StringComparison.Ordinal)))
                extras = null;
            _commandTrust.TookFromFile(state.ManualCommand, extras);
        }
        else
            _commandTrust.Clear();

        // Legacy-shape description, read before anything changes (null for current format).
        var oldShape = ProjectStore.OldShapeOf(state.Timeline, state.Deleted, state.Subtitles);
        ConvertedFrom = null;

        // The project's source and download settings (a URL load reads them); put back if the load fails, so the open project keeps its own.
        var before = (SourcePath, DownloadResolution, DownloadSubtitles);
        SourcePath = state.SourcePath;
        DownloadResolution = Pick(DownloadResolutions, state.DownloadResolution, "Best");
        DownloadSubtitles = state.DownloadSubtitles;

        // Probe the opened file first (its streams define output tracks and feed legacy conversion) without placing it: the project's
        // timeline is authoritative. Projects built only from drops have none.
        if (!string.IsNullOrWhiteSpace(state.SourcePath) || !string.IsNullOrWhiteSpace(state.LocalMediaPath))
        {
            var (loaded, message) = await LoadMediaAsync(state.SourcePath, state.LocalMediaPath, applyAutomation: false, cancellationToken, place: false);
            if (!loaded)
            {
                (SourcePath, DownloadResolution, DownloadSubtitles) = before;
                return message;
            }
        }
        else
        {
            ResetForNewSource();
            LocalMediaPath = "";
            SetMediaInfo(null);
        }

        // Adopt the project's stored thumbnail (queued jobs have none) until a save renders a new one; only once its source loaded.
        TakeUpThumbnail(state.ThumbnailJpeg);

        ApplyProjectSettings(state, legacyMixing: oldShape is not null);
        var timeline = TimelineToOpen(state);
        var converted = ConvertLegacy(timeline, state) || oldShape is not null;
        if (converted)
        {
            ConvertedFrom = oldShape ?? "clips of the main video";
            AppLog.Write($"Opened a project of the shape before 5.2 ({ConvertedFrom}): converted as it was opened");
        }

        InstallProjectTimeline(state, timeline);

        // Adopt the saved undo history (none for queued jobs) so Ctrl+Z continues from the save.
        TakeUpHistory(state, converted);
        MarkSaved();
        opened?.Invoke();
        return GetLayoutAspectWarning() is { } warning ? $"{doneMessage}. {warning}" : doneMessage;
    });

    /// <summary>
    /// Applies project settings on open (RestoreStateAsync) or tab activation (AttachTab): output format and chapters, subtitles,
    /// export settings without layout (the timeline follows via <see cref="InstallProjectTimeline"/>) and output tracks. Both paths
    /// share these steps so an in-memory tab is configured identically to one opened from disk.
    /// </summary>
    /// <param name="legacyMixing">Legacy projects only: main-video stream mixing, the conversion's input.</param>
    private void ApplyProjectSettings(ProjectState state, bool legacyMixing)
    {
        SnapToIFrames = state.SnapToIFrames;
        if (Containers.Contains(state.Container))
            Container = state.Container;
        WebOptimized = state.WebOptimized;
        ChapterMarkers = state.ChapterMarkers;
        ChaptersAtCuts = state.ChaptersAtCuts;
        ChaptersAtMarkers = state.ChaptersAtMarkers;

        // Captions anchored to the legacy main video follow its file's clips.
        LegacyMainVideo.MigrateCaptions(state.Subtitles, LocalMediaPath);
        ApplySubtitles(state.Subtitles);

        // Settings without layout; the timeline (a copy, keeping queued job state intact) installs afterwards with markers and
        // Sequence Output effects.
        SelectedPreset = null;
        ApplyPreset(state.Settings, withLayout: false);
        _backgroundRow.IsHidden = state.Settings.BackgroundHidden;

        // Output tracks (stored here by queue jobs and older projects) plus legacy stream mixing, which the conversion moves onto
        // Sub-Lanes before the timeline installs.
        ApplyAudioTrackStates(state.AudioTracks, legacyMixing);
    }

    /// <summary>
    /// Installs a project timeline and its dependents as one restore (RestoreStateAsync, or AttachTab with the live Sequence): bin,
    /// target size, caption audio, subtitle stream actions, then destination and command.
    /// </summary>
    /// <param name="kept">Views kept by a live tab (reused for its unchanged Sequence); null to build them.</param>
    private void InstallProjectTimeline(ProjectState state, TimelineDocument timeline, KeptViews? kept = null)
    {
        // One restore scope: no undo steps or edits from side effects; captions project once at the end.
        using (BeginRestore())
        {
            PutDocumentInPlace(timeline, kept);
            SetBin(state.Deleted);
            TargetFileSize = state.TargetFileSize;
            CaptionAudioPath = state.CaptionAudioPath ?? "";
            CaptionUseExternalAudio = state.CaptionUseExternalAudio ?? CaptionAudioPath.Length > 0;

            foreach (var saved in state.SubtitleTracks)
            {
                if (SubtitleTracks.FirstOrDefault(t => t.Index == saved.Index) is { } track && track.Actions.Contains(saved.Action))
                    track.Action = saved.Action;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.DestinationPath))
            DestinationPath = state.DestinationPath;

        // Last, so nothing above regenerates over a hand-edited command.
        IsCommandManuallyEdited = false;
        GenerateCommand();
        if (state.ManualCommand is { } manualCommand)
            CommandPreview = manualCommand;
    }

    // ----- Encoding presets as files -----

    private static readonly System.Text.Json.JsonSerializerOptions PresetFileOptions = new() { WriteIndented = true };

    /// <summary>Exports the current settings as an importable encoding preset file.</summary>
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
            Fail($"Could not write {path}: {ex.Message}", $"Export the encoding preset to {path}", ex);
        }
    }

    /// <summary>Imports an encoding preset file (replacing a same-named one) and applies it.</summary>
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

            // Imported presets are always custom, never built-in, and never take a built-in's name.
            preset.Category = null;
            if (ExportPresetCatalog.IsBuiltInName(preset.Name))
                preset.Name = PresetStore.UniqueName(preset.Name, Presets.Where(p => !p.IsBuiltIn));

            var existing = Presets.ToList().FindIndex(p => !p.IsBuiltIn && p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
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
            Fail($"Could not read the preset: {ex.Message}", "Read an encoding preset", ex);
        }
    }

    // ----- Projects -----

    /// <summary>The saved projects by name, the most recently saved first: the Swap Project list.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>The project that is open, when it is one of the saved ones.</summary>
    [ObservableProperty] private string? _selectedProject;

    /// <summary>Set during list refill or load sync, so selection changes are not treated as user choices.</summary>
    public bool IsListingProjects { get; private set; }

    /// <summary>Reads the Projects folder again (names only: no project is opened for it), as the list is opened.</summary>
    public void RefreshRecentProjects(string? current = null)
    {
        current ??= SelectedProject;
        IsListingProjects = true;
        try
        {
            RecentProjects.Clear();
            // Include fully portable projects (one folder per project).
            foreach (var file in ProjectStore.ProjectFiles().OrderByDescending(f => f.LastWriteTime).Take(25))
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                if (!RecentProjects.Contains(name))
                    RecentProjects.Add(name);
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

    /// <summary>Save request (Ctrl+S, or Ctrl+Shift+S when <paramref name="choosePlace"/>); the window handles it since it may prompt.</summary>
    public event Action<bool>? SaveRequested;

    /// <summary>
    /// Raised after the project file is written (Save, Save As, portable save, legacy write-back); never for open, autosave or
    /// bundle. The window then deletes the active tab's crash-recovery file.
    /// </summary>
    public event Action? ProjectSaved;

    [RelayCommand]
    private void SaveProjectNow() => SaveRequested?.Invoke(false);

    [RelayCommand]
    private void SaveProjectAs() => SaveRequested?.Invoke(true);

    /// <summary>Autosave quick toggle in the Settings flyout (same setting as Settings, Behavior).</summary>
    public bool AutosaveEnabled
    {
        get => AppSettings.Current.AutosaveEnabled;
        set
        {
            if (value == AppSettings.Current.AutosaveEnabled)
                return;

            SaveView(settings => settings.AutosaveEnabled = value);
            OnPropertyChanged();
            StatusText = value
                ? $"Autosave on: every {Math.Clamp(AppSettings.Current.AutosaveMinutes, 1, 120)} minutes, to a file named after the project."
                : "Autosave off.";
        }
    }

    /// <summary>Re-raises the setting after a Settings window save.</summary>
    public void RefreshAutosave() => OnPropertyChanged(nameof(AutosaveEnabled));

    /// <summary>Lists too narrow for inline row controls plus a usable time bar: controls move to a per-row flyout.</summary>
    [ObservableProperty] private bool _compactTrackControls;

    /// <summary>Overview Scrubber follows timeline zoom and scroll; off, it always shows the whole sequence.</summary>
    [ObservableProperty] private bool _syncMasterTimeline;

    /// <summary>
    /// Path the project was last saved to or opened from; empty if never saved. Change notification renames the active tab immediately.
    /// </summary>
    public string CurrentProjectPath
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            OnPropertyChanged();
        }
    } = "";

    /// <summary>Raised after the session is cleared for a new project; the window releases the player file and shows the drop hint.</summary>
    public event Action? SessionCleared;

    /// <summary>
    /// + New Project (Project Manager): resets to a fresh-launch session (no media, clips, markers, subtitles or history) with
    /// default encoding settings (Settings, Export &amp; Hardware). The window confirms unsaved changes first; false while busy.
    /// </summary>
    public bool NewProject()
    {
        if (IsBusy || IsRecording)
        {
            StatusText = IsRecording
                ? "Stop the voiceover recording before starting a new project."
                : "Wait for the running operation to finish, or cancel it, before starting a new project.";
            return false;
        }

        using (DeferCommand())
        {
            ResetForNewSource();
            InstallDocument(NewDocument());
            (SourcePath, LocalMediaPath, DestinationPath, CurrentProjectPath, EditingJob) = ("", "", "", "", null);
            (NewProjectName, NewProjectFolder) = ("", null);
            SetMediaInfo(null);
            ApplySubtitles(null);
            KeyLayer = null;
            SelectedLaneKeys = [];
            SelectedPreset = null;
            IsCommandManuallyEdited = false;
            ResetProjectLook();
            ApplyEncodingDefaults();
        }

        ForgetHistory();
        TakeUpThumbnail(null);
        MarkSaved();
        SessionCleared?.Invoke();
        Log("New project: nothing is open. Drop a video on the player, or use Add Media, to start.");
        return true;
    }

    /// <summary>
    /// Resets all non-encoding project state: orientation per DefaultVerticalVideo, Sequence size, neutral blur fill, crop, grade,
    /// LUT and Sequence Output effects, no pending layout, captions off with default style and box, no voiceover or player overlay.
    /// </summary>
    private void ResetProjectLook()
    {
        // Set initial sizes without the orientation toggle swapping them.
        _syncingDimensions = true;
        try
        {
            (UseVerticalResolution, KeepAspectRatio, OutputWidth, OutputHeight) = (AppSettings.Current.DefaultVerticalVideo, true, "", "");
        }
        finally
        {
            _syncingDimensions = false;
        }

        (PixelAspectRatio, CustomPixelAspectRatio) = (MatchProject, "");
        (IsInteractiveCropActive, DrawTargetLayer, IsArrangeActive) = (false, null, false);
        ResetCrop();
        ResetBlurSettings();
        _backgroundRow.IsHidden = false;
        SetBackgroundEffects(null, false);
        _layoutSourceAspect = 0;
        _templateToCome = null;

        ResetColor();
        LutPath = "";
        SetSequenceEffects(null, bypassed: false);
        (FadeIn, FadeOut, Deinterlace, Denoise, TargetFileSize) = (false, false, false, false, "");

        (AutoCaptions, PreviewSubtitles) = (false, false);
        SetCaptionStyle(AppSettings.Current.DefaultCaptionStyle?.Clone() ?? new());
        SetCaptionLayer(null);
        WhisperPrompt = "";
        (CaptionAudioPath, CaptionUseExternalAudio) = ("", false);

        if (HasVoiceover)
            RemoveVoiceover();
        (VoiceoverGainDb, MixVoiceover, VoiceoverStatus) = (0, true, "");

        SetMarkers(null);
        ClearDocumentMarkers();
        AudioLinked = true;
    }

    /// <summary>
    /// Save As: writes to a chosen path, which becomes the Save target. Undo history is preserved and persisted with the file.
    /// </summary>
    public SaveOutcome SaveProjectTo(string path)
    {
        // With fully portable saves on, Ctrl+S writes a media-holding folder; Save As to an explicit file writes just that file.
        if (AppSettings.Current.FullyPortableProjectSave && string.Equals(path, CurrentProjectPath, StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileNameWithoutExtension(path).EndsWith(ProjectStore.AutosaveSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return SavePortable(Path.GetFileNameWithoutExtension(path), path);
        }

        try
        {
            ProjectStore.SaveTo(CaptureStateForSave(Path.GetFileNameWithoutExtension(path)), path);
            CurrentProjectPath = path;
            DevFlightRecorder.Record("project", "path " + path);
            MarkSaved();
            ProjectSaved?.Invoke();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            _ = OfferToKeepDownloadsAsync();
            return SaveSucceeded($"Project saved: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return SaveFailed(path, ex);
        }
    }

    // ----- Project bundles (Services/ProjectBundle.cs) -----

    /// <summary>
    /// Writes the project and all referenced files as one .hpb bundle off the UI thread, with status progress. The Save target
    /// is unchanged (a bundle is a portable copy).
    /// </summary>
    public async Task SaveBundleAsync(string path)
    {
        if (!HasSource)
        {
            StatusText = "There is nothing to save yet: load a video first.";
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        ProjectState state;
        try
        {
            state = CaptureStateForSave(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail($"Could not save the bundle: {ex.Message}", "Save a project bundle", ex);
            return;
        }

        var shown = -1;
        var progress = new Progress<double>(share =>
        {
            var percent = (int)(share * 100);
            if (percent != shown)
                StatusText = $"Saving the bundle {name}: {(shown = percent).ToString(System.Globalization.CultureInfo.InvariantCulture)} %";
        });
        try
        {
            var trim = AppSettings.Current.TrimLongVodsOnBundle;
            var (files, missing, trimmed) = await WorkScheduler.Run(new WorkSpec($"Saving the bundle {name}", WorkLane.Interactive),
                work => Task.Run(() => ProjectBundle.Pack(state, path, progress, work.Token, trim)), _shutdown.Token);
            StatusText = $"Bundle saved: {path} ({files} file{(files == 1 ? "" : "s")} inside"
                         + (trimmed > 0 ? $"; {trimmed} long recording{(trimmed == 1 ? "" : "s")} trimmed to what the Sequence uses, with 5 s to spare" : "")
                         + (missing > 0 ? $"; {missing} the project names {(missing == 1 ? "is" : "are")} not on this PC and stayed out)" : ")");
        }
        catch (OperationCanceledException)
        {
            StatusText = "The bundle was not saved (cancelled).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ArgumentException)
        {
            Fail($"Could not save the bundle: {ex.Message}", "Save a project bundle", ex);
        }
    }

    /// <summary>Unpacks a bundle next to itself (ProjectBundle.Unpack) and opens the contained project.</summary>
    private async Task OpenBundleAsync(string bundlePath)
    {
        var name = Path.GetFileNameWithoutExtension(bundlePath);
        var shown = -1;
        var progress = new Progress<double>(share =>
        {
            var percent = (int)(share * 100);
            if (percent != shown)
                StatusText = $"Unpacking the bundle {name}: {(shown = percent).ToString(System.Globalization.CultureInfo.InvariantCulture)} %";
        });

        string project;
        try
        {
            CrashReporter.KeepOut(bundlePath);
            project = await WorkScheduler.Run(new WorkSpec($"Unpacking the bundle {name}", WorkLane.Interactive) { Urgent = true },
                work => Task.Run(() => ProjectBundle.Unpack(bundlePath, progress, work.Token)), _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "The bundle was not opened (cancelled).";
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or System.Text.Json.JsonException
                                       or ArgumentException)
        {
            Fail($"Could not open the bundle: {ex.Message}", "Open a project bundle", ex);
            return;
        }

        await LoadProjectAsync(project);
    }

    // Fingerprint at the last autosave; nothing is written while unchanged.
    private string _autosavedFingerprint = "";
    private bool _autosaving;

    /// <summary>
    /// Writes "Name.autosave.hproj" when changed: state is captured on the UI thread, serialized and written on the pool. Does not
    /// mark the project saved or touch history. With two or more steps since save/open, the file is written two actions back with
    /// those actions on Redo (MainViewModel.Recovery.cs), so a project broken by its last edits opens before them.
    /// </summary>
    public async Task AutosaveAsync()
    {
        if (_isBackgroundWorker || _autosaving || !HasSource)
            return;

        var fingerprint = Fingerprint();
        if (fingerprint == _autosavedFingerprint || !(_touchedSinceSaved && _savedFingerprint.Length > 0 && fingerprint != _savedFingerprint))
            return;

        var name = CurrentProjectPath.Length > 0 ? Path.GetFileNameWithoutExtension(CurrentProjectPath) : Path.GetFileNameWithoutExtension(SourcePath.Trim().Trim('"'));
        var path = ProjectStore.AutosavePath(CurrentProjectPath, name);

        // Packed undo steps are immutable, so the pool thread writes them directly. The thumbnail capture is started here (the engine
        // plans on the UI thread) and awaited on the pool within the save cap before writing.
        var state = CaptureStateForSave(Path.GetFileNameWithoutExtension(path), moved: null, waitForThumbnail: false);
        var thumbnail = ThumbnailRequest();
        var lag = TakeRecoveryLag();
        _autosaving = true;
        try
        {
            // Build the two-actions-back state off the UI thread from the packed steps and the captured copy.
            await Task.Run(async () =>
            {
                if (thumbnail is not null)
                    state.ThumbnailJpeg = await FinishThumbnailAsync(thumbnail).ConfigureAwait(false) ?? state.ThumbnailJpeg;
                var written = lag is not null && Lagged(state, lag) is { } lagged ? lagged : state;
                ProjectStore.SaveTo(written, path);
                if (!ReferenceEquals(written, state))
                    AppLog.Write("Autosave: written two actions back (N-2), the last two on its Redo (Ctrl+Y gives N-1, then N)");
            });
            _autosavedFingerprint = fingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Write($"Autosave to {path} failed", ex);
        }
        finally
        {
            _autosaving = false;
        }
    }

    /// <summary>
    /// Manual fully portable save: Projects\Name\Name.hproj with referenced files in media\ (hard-linked on the same volume, else
    /// copied in the background) via relative paths, or in place when already a portable folder. Returns a status line.
    /// </summary>
    private SaveOutcome SavePortable(string name, string currentPath)
    {
        var path = currentPath;
        try
        {
            path = PortableProject.IsPortableFolder(currentPath) && File.Exists(currentPath) ? currentPath : PortableProject.PathFor(name.Trim());
            var state = CaptureStateForSave(Path.GetFileNameWithoutExtension(path));
            var outcome = PortableProject.Save(state, path);
            CrashReporter.KeepOut(CrashSummaries.FilesIn(state).Append(path));
            CurrentProjectPath = path;
            DevFlightRecorder.Record("project", "path " + path);
            MarkSaved();
            ProjectSaved?.Invoke();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            _ = OfferToKeepDownloadsAsync();
            if (outcome.Copying > 0)
                _ = TellCopiedAsync(outcome);

            var missing = outcome.Missing > 0 ? $"; {outcome.Missing} named file{(outcome.Missing == 1 ? " is" : "s are")} not on this PC" : "";
            return SaveSucceeded(outcome.Copying > 0
                ? $"Project saved: {Path.GetFileNameWithoutExtension(path)}, a portable folder: {outcome.InPlace} file{(outcome.InPlace == 1 ? "" : "s")} in its media folder, {outcome.Copying} being copied there from another drive{missing}."
                : $"Project saved: {Path.GetFileNameWithoutExtension(path)}, a portable folder with all {outcome.InPlace} of its files in its media folder{missing}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return SaveFailed(path.Length > 0 ? path : name, ex);
        }
    }

    // Background media copies of a portable save; reported on completion.
    private async Task TellCopiedAsync(PortableProject.Outcome outcome)
    {
        try
        {
            await outcome.Copies;
            StatusText = $"{Path.GetFileNameWithoutExtension(outcome.Path)}: its media folder is complete; the project now names every copy there.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Portable project: the background copies failed", ex);
        }
    }

    /// <summary>
    /// Saves the current state as a project; returns a status line. Never overwrites a different existing project without
    /// confirmation (studio projects all start as "New Project"); declined means nothing is written.
    /// </summary>
    public SaveOutcome SaveProject(string name)
    {
        var target = SavedProjectPathFor(name);
        if (File.Exists(target) && !SamePath(target, CurrentProjectPath)
            && Confirm?.Invoke("Replace Project", $"A project named \"{Path.GetFileNameWithoutExtension(target)}\" is already saved there:\n{target}\n\nSaving replaces it with this one.", "Replace") != true)
        {
            return SaveDeclined($"Not saved: a project named \"{Path.GetFileNameWithoutExtension(target)}\" is already saved there. Choose another name.");
        }

        // Fully portable saves: own folder with its media.
        if (AppSettings.Current.FullyPortableProjectSave)
            return SavePortable(name, "");

        // First save of a New Project goes to the folder chosen in the New Project studio.
        if (NewProjectPathFor(name) is { } chosen)
            return SaveProjectTo(chosen);

        try
        {
            var state = CaptureStateForSave(name.Trim());
            var path = ProjectStore.Save(state);
            CrashReporter.KeepOut(CrashSummaries.FilesIn(state).Append(path));
            CrashReporter.Note("project", "saved: " + CrashSummaries.Project(state, ProjectFile.CurrentVersion));
            CurrentProjectPath = path;
            DevFlightRecorder.Record("project", "path " + path);
            MarkSaved();
            ProjectSaved?.Invoke();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            _ = OfferToKeepDownloadsAsync();
            return SaveSucceeded($"Project saved: {Path.GetFileNameWithoutExtension(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveFailed(target, ex);
        }
    }

    // ----- Project thumbnail (Services/ProjectThumbnail.cs) -----
    // Each save embeds a Sequence-at-playhead thumbnail from the GPU engine (CaptureThumbnail). Saves run synchronously on the UI
    // thread, so they wait at most ThumbnailWaitMs for a fresh capture, else use the last one (late captures serve the next save).
    // Capture renders on the engine pump and encodes on the pool, never needing the blocked UI thread.

    // Last thumbnail (or the opened project's) and its TickCount64, the in-flight capture and request time, and a project generation
    // bumped on open so late captures of the previous project are discarded.
    private byte[]? _thumbnail;
    private long _thumbnailTakenAt;
    private Task<byte[]?>? _thumbnailCapture;
    private long _thumbnailAskedAt;
    private int _thumbnailGeneration;

    /// <summary>Max wait for a fresh thumbnail on save (ms); the engine renders one from its frame cache within a few frames.</summary>
    private const int ThumbnailWaitMs = 300;

    /// <summary>Age (ms) below which the last thumbnail is reused as-is (e.g. a re-save right after a save).</summary>
    private const long ThumbnailFreshMs = 1000;

    /// <summary>Timeout (ms) after which a pending capture is abandoned and re-requested (engine stalled).</summary>
    private const long ThumbnailStaleMs = 10_000;

    /// <summary>
    /// Thumbnail for a save (UI thread): fresh if rendered within <see cref="ThumbnailWaitMs"/>, else the last one; none without
    /// <see cref="CaptureThumbnail"/>.
    /// </summary>
    private byte[]? ThumbnailForSave()
    {
        if (ThumbnailRequest() is not { } running)
            return Volatile.Read(ref _thumbnail);

        try
        {
            if (running.Wait(ThumbnailWaitMs) && running.Result is { Length: > 0 } fresh)
                return fresh;
        }
        catch (AggregateException ex)
        {
            AppLog.Write("Project thumbnail: the capture failed", ex.InnerException ?? ex);
        }

        return Volatile.Read(ref _thumbnail);
    }

    /// <summary>
    /// Starts or reuses a capture (UI thread, where the engine plans the frame); null when the last thumbnail is fresh or there is no window.
    /// </summary>
    private Task<byte[]?>? ThumbnailRequest()
    {
        if (CaptureThumbnail is not { } capture || _isBackgroundWorker)
            return null;

        var now = Environment.TickCount64;
        if (Volatile.Read(ref _thumbnail) is not null && now - Volatile.Read(ref _thumbnailTakenAt) < ThumbnailFreshMs)
            return null;

        if (_thumbnailCapture is not { IsCompleted: false } running || now - _thumbnailAskedAt > ThumbnailStaleMs)
        {
            running = _thumbnailCapture = KeepThumbnail(capture(), _thumbnailGeneration);
            _thumbnailAskedAt = now;
        }

        return running;
    }

    /// <summary>Awaits a capture off the UI thread within <see cref="ThumbnailWaitMs"/>; null on timeout or failure.</summary>
    private async Task<byte[]?> FinishThumbnailAsync(Task<byte[]?> running)
    {
        try
        {
            if (await Task.WhenAny(running, Task.Delay(ThumbnailWaitMs)).ConfigureAwait(false) == running && running.Result is { Length: > 0 } fresh)
                return fresh;
        }
        catch (AggregateException ex)
        {
            AppLog.Write("Project thumbnail: the capture failed", ex.InnerException ?? ex);
        }

        return Volatile.Read(ref _thumbnail);
    }

    /// <summary>Stores a completed capture for later saves (on the completing thread) unless the project generation changed.</summary>
    private async Task<byte[]?> KeepThumbnail(Task<byte[]?> capture, int generation)
    {
        var jpeg = await capture.ConfigureAwait(false);
        if (jpeg is { Length: > 0 } && generation == Volatile.Read(ref _thumbnailGeneration))
        {
            Volatile.Write(ref _thumbnail, jpeg);
            Volatile.Write(ref _thumbnailTakenAt, Environment.TickCount64);
        }

        return jpeg;
    }

    /// <summary>On open or new project: adopts its thumbnail (none for new), bumps the generation and drops any pending capture.</summary>
    private void TakeUpThumbnail(byte[]? thumbnail)
    {
        Interlocked.Increment(ref _thumbnailGeneration);
        _thumbnailCapture = null;
        Volatile.Write(ref _thumbnailTakenAt, 0L);
        Volatile.Write(ref _thumbnail, thumbnail is { Length: > 0 } ? thumbnail : null);
    }

    /// <summary>Opens a project, then unpacks its videos' streams in the background (<see cref="UnpackProjectStreamsAsync"/>).</summary>
    public async Task LoadProjectAsync(string filePath)
    {
        await OpenProjectFileAsync(filePath);
        _ = UnpackProjectStreamsAsync();
    }

    private Task OpenProjectFileAsync(string filePath)
    {
        // Bundles are unpacked beside themselves first, then the inner project opens.
        if (filePath.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase))
            return OpenBundleAsync(filePath);

        ProjectState state;
        var opened = "Project loaded";
        try
        {
            CrashReporter.KeepOut(filePath);
            try
            {
                // Legacy main-video projects load as-is and convert during restore (RestoreStateAsync, ConvertLegacy); the window then
                // writes them back over their file (WriteBackConverted).
                state = ProjectStore.Load(filePath);
            }
            catch (NewerProjectFormatException newer)
            {
                // Newer format: warn, and open best-effort only on confirmation.
                if (Confirm is null || !Confirm("Newer Project Format",
                        $"{newer.Message}\n\nOpening it anyway reads what this version knows of it: anything the newer version added is not shown. "
                        + "So that none of that is lost, saving over this file is refused: Save asks for another name.", "Open Anyway"))
                {
                    StatusText = $"Not opened: {newer.Message}";
                    return Task.CompletedTask;
                }

                state = ProjectStore.Load(filePath, allowNewer: true);
                opened = $"Project loaded from a newer format ({newer.WrittenBy ?? $"format {newer.Version}"}; save it under another name)";
            }
            CrashReporter.KeepOut(CrashSummaries.FilesIn(state));
            CrashReporter.Note("project", "opened: " + CrashSummaries.Project(state));
            DevFlightRecorder.Record("project", "opened " + filePath);

            // Autosaves and newer-format projects open as unsaved work: Save prompts for a path, so neither file is overwritten.
            var isNewer = ProjectStore.PeekHeader(filePath) is { Version: > ProjectFile.CurrentVersion };
            CurrentProjectPath = isNewer || Path.GetFileNameWithoutExtension(filePath).EndsWith(ProjectStore.AutosaveSuffix, StringComparison.OrdinalIgnoreCase)
                ? ""
                : filePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            Fail($"Could not open the project: {ex.Message}", $"Open the project {filePath}", ex);
            return Task.CompletedTask;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);

        // Encode running: queue the project instead of replacing the session.
        if (IsBusy)
            return QueueProjectInBackgroundAsync(state, name);

        EditingJob = null;
        RefreshRecentProjects(name);
        return OpenThenWriteBackAsync(state, $"{opened}: {name}", CurrentProjectPath, ProjectStore.PeekHeader(filePath)?.WrittenBy);
    }

    /// <summary>
    /// Opens the project (<see cref="OpenShowingCaptionsAsync"/>); a converted legacy project (<see cref="ConvertedFrom"/>) is then written
    /// back when <see cref="SavesConvertedProjects"/> and <paramref name="path"/> is a real Save target (not autosave or newer format).
    /// </summary>
    /// <param name="writtenBy">Writer string from the file (e.g. "HandPeg x.y.z"), for the status line.</param>
    private async Task OpenThenWriteBackAsync(ProjectState state, string doneMessage, string path, string? writtenBy)
    {
        await OpenShowingCaptionsAsync(state, doneMessage);
        if (state.Workspace is { } workspace && !_isBackgroundWorker)
            WorkspaceOpened?.Invoke(workspace);
        if (ConvertedFrom is { } shape && SavesConvertedProjects && !_isBackgroundWorker && path.Length > 0
            && string.Equals(path, CurrentProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            WriteBackConverted(path, $"{writtenBy ?? "a HandPeg from before 5.2"} ({shape})");
        }
    }

    /// <summary>
    /// Overwrites a converted legacy project's file in the current format (no backup kept), atomically via ProjectStore.SaveTo
    /// (write beside, then replace), so a failure leaves the legacy file intact. Always SaveTo, even with portable saves on.
    /// Returns a status line.
    /// </summary>
    private string WriteBackConverted(string path, string from)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            ProjectStore.SaveTo(CaptureStateForSave(name), path);
            MarkSaved();
            ProjectSaved?.Invoke();
            AppLog.Write($"The converted project was written over its file in 5.2's shape: {path} (was {from})");
            return StatusText = $"Project loaded and converted from {from}; saved over its file in this version's shape: {name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Write($"The converted project could not be written over its file: {path}", ex);
            return StatusText = $"Project loaded and converted from {from}, but it could not be saved over its file ({ex.Message}): Save writes it.";
        }
    }

    /// <summary>
    /// Opens the project, then enables Preview Subtitles when its export burns captions in (the GPU preview draws them for free;
    /// otherwise such projects looked caption-less after load). Runs after the restore operation completes.
    /// </summary>
    private async Task OpenShowingCaptionsAsync(ProjectState state, string doneMessage)
    {
        await RestoreStateAsync(state, doneMessage);
        if (_isBackgroundWorker || PreviewSubtitles || !AutoCaptions || !HasTextCues || IsBusy)
            return;

        var said = StatusText;
        PreviewSubtitles = true;
        StatusText = said;
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
}
