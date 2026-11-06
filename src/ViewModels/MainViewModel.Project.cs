using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Capturing and restoring the whole state of the window: projects and queue editing build on this.
public partial class MainViewModel
{
    /// <summary>Player volume, 0 to 100.</summary>
    [ObservableProperty] private int _volume = 100;

    /// <summary>Frame rate of the file last opened (the rate an empty Sequence takes from it). 30 when it is not known.</summary>
    public double SourceFrameRate => _mediaInfo?.Video?.FrameRate is > 0 and var rate ? rate : 30;

    /// <summary>The Dimensions, Filters, Video and Audio tabs as a preset.</summary>
    /// <summary>The encode settings as they are now, as a preset (for Settings: "Use the Current Video and Audio Settings").</summary>
    public EncodingPreset CurrentEncodingSettings() => CaptureSettings("");

    private EncodingPreset CaptureSettings(string name) => new()
    {
        Name = name,
        OutputWidth = OutputWidth,
        OutputHeight = OutputHeight,
        KeepAspectRatio = KeepAspectRatio,
        // The crop and place of the clip a preset takes its layout from (TemplateBase; 4.4: each clip has its own, which a project
        // keeps in its timeline; a preset, and HandPeg before 5.2, take these).
        CropPercent = SequenceCropPercent(),
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
        AudioSampleRate = AudioSampleRate,
        AudioChannels = AudioChannels == AudioChannelChoice.Stereo ? null : AudioChannels,
        MergeAudioTracks = MergeAudioTracks,
        NormalizeAudio = NormalizeAudio,
        DuckAmountDb = DuckAmountDb,
        CenterZoom = TemplatePlace().Zoom,
        // The layout, for a preset (a project's are its timeline, and ProjectStore leaves these out): the first video clip's
        // template (5.2-AuditB: its pieces, its look, how many pieces lie under it, its place as the old Center values).
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

        // Only when the user asked for presets to carry it; see the Automation settings.
        TargetFileSize = AppSettings.Current.SaveTargetSizeInPresets ? TargetFileSize : "",
    };

    /// <summary>
    /// The clip a saved layout is taken from (5.2-AuditB: a preset's or a style's template is one clip's): the first top-level video
    /// clip, the lowest track's; with <paramref name="selected"/> (a style saved by hand), the selected video clip first. Null while
    /// there is none. (A preset is part of a project's fingerprint: what it is taken from never follows the selection.)
    /// </summary>
    private Layer? TemplateBase(bool selected = false) => selected ? TemplateTargets().FirstOrDefault() ?? FirstVideoView() : FirstVideoView();

    /// <summary>The clip's template as a layout saves it: its pieces, those under its track first, each from where it starts on the clip.</summary>
    private List<TemplatePiece> TemplatePieces(bool selected = false) =>
        TemplateBase(selected) is { } view && ClipOf(view) is { } clip && TemplateFrom(clip) is { } template ? [.. template.Pieces] : [];

    /// <summary>The template's pieces as a layout carries them (5.2-AuditB: the clip's, not every clip of the timeline).</summary>
    private List<LayerState> TemplateLayers(bool selected = false) => TemplatePieces(selected).ConvertAll(SavedPiece);

    /// <summary>How many of a layout's pieces lie under its clip's track.</summary>
    private int TemplateMainIndex(bool selected = false) => TemplatePieces(selected).Count(p => p.Under);

    /// <summary>
    /// The place of the clip a layout is taken from as the old Center values (5.2-AuditB): its width as the zoom, its middle's distance
    /// from the frame's as the offsets (percentages of the frame); a whole-width middle while there is none.
    /// </summary>
    private (double Zoom, double OffsetX, double OffsetY) TemplatePlace(bool selected = false)
    {
        if (TemplateBase(selected) is not { } view || FrameWidth <= 0 || FrameHeight <= 0)
            return (1, 0, 0);

        var (x, y, width, height) = view.GetOutputRect(FrameWidth, FrameHeight, SourceWidth, SourceHeight);
        return (view.SizeWidth, ((x + width / 2.0) / FrameWidth - 0.5) * 100, ((y + height / 2.0) / FrameHeight - 0.5) * 100);
    }

    /// <summary>
    /// A clip's piece as a layout saves it: from where it starts on its clip, for how long (0: the whole clip); a piece cut from the
    /// clip's own file as a cut-out of "the video" (LayerKind.Video: the part of the picture its crop keeps, its box as it is), so a
    /// layout puts it on a clip of any file (FileCutoutOf makes it again).
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
        }

        return state;
    }

    /// <summary>What is set track by track: action, codec, bitrate and title (5.2-AuditB: the mixing is the Sub-Lanes', neutral here).</summary>
    private List<AudioTrackState> CaptureAudioTracks() =>
        AudioTracks.Select(t => new AudioTrackState(t.Index, t.Action, t.Codec, t.Bitrate, t.Title, t.GainDb, t.Filters.IsActive ? t.Filters.Clone() : null,
            t.OffsetSeconds, t.Pieces.Count > 0 ? [.. t.Pieces] : null, t.AutoDuck, t.IsVoice)).ToList();

    /// <summary>
    /// A snapshot of the source, the timeline and every setting. The timeline is a copy of its own (its looks too), so
    /// what holds the state (a queued job, an autosave being written away from the interface) is never reached by later edits.
    /// </summary>
    public ProjectState CaptureState(string name = "")
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
            Timeline = Document.Clone(deep: true),
            SnapToIFrames = SnapToIFrames,
            Container = Container,
            WebOptimized = WebOptimized,
            ChapterMarkers = ChapterMarkers,
            ChaptersAtCuts = ChaptersAtCuts,
            ChaptersAtMarkers = ChaptersAtMarkers,
            Subtitles = CaptureSubtitles(),
            TargetFileSize = TargetFileSize,
            Settings = CaptureSettings(name),
            AudioTracks = CaptureAudioTracks(),
            SubtitleTracks = SubtitleTracks.Select(t => new SubtitleTrackState(t.Index, t.Action)).ToList(),
            CaptionAudioPath = CaptionAudioPath,
            CaptionUseExternalAudio = CaptionUseExternalAudio,
            CaptionAudioTrackIndex = ResolveCaptionSource() is { Kind: CaptionSourceKind.Clip, Stream: >= 0 } listened && SameFile(listened.Path, OpenedFile) ? listened.Stream : 0,
            Deleted = RecycleBin.Select(i => i.Clip with { Clip = CopyOfClip(i.Clip.Clip) }).ToList(),
            ManualCommand = IsCommandManuallyEdited ? CommandPreview : null,
        };
    }

    /// <summary>
    /// A state's timeline as it is opened: a copy of its own; the clips of the file it opened playing the file this session opened
    /// (a link's download may lie at another path now); and, for a file put together by hand, its tracks' clips in order of
    /// their starts and each id once (what the timeline's searches rely on). What is still wrong with it is written to the log.
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

        // A project from before each clip of the main video had a crop of its own (4.3) kept one for the whole sequence in its
        // settings: its clips take it, when none of them has one (4.4), before they are converted (ConvertLegacy). A look is
        // never changed in place: each gets a copy.
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

        if (timeline.FindProblems() is { Count: > 0 } problems)
            AppLog.Write($"The project's timeline has {problems.Count} problem(s): {string.Join(" ", problems.Take(5))}");
        return timeline;
    }

    /// <summary>The crop a preset carries, as percentages (left, top, right, bottom): the clip its layout is taken from's (TemplateBase), the Crop boxes' while there is none.</summary>
    private double[] SequenceCropPercent()
    {
        var (left, top, right, bottom) = TemplateBase() is { } view ? CropOf(view) : BoxCrop();
        return [Math.Round(left * 100, 2), Math.Round(top * 100, 2), Math.Round(right * 100, 2), Math.Round(bottom * 100, 2)];
    }

    /// <summary>
    /// Whether there is something worth saving or queueing: a file opened, or any clip on the timeline (5.2-AuditB: a Sequence of
    /// dropped files is a project too). Told again whenever the document changes (NoteDocumentShape).
    /// </summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(LocalMediaPath) || Document.Tracks.Any(t => t.Clips.Count > 0);

    /// <summary>
    /// What made the project opened last one of 5.1's shape, which was converted as it was opened ("19 clips of the main video and 57
    /// cut-outs of it": ProjectStore.OldShapeOf, ConvertLegacy), or null when it was of the current shape. Read by the window (which then
    /// writes it over its file, <see cref="SavesConvertedProjects"/>) and by the harnesses, which say so.
    /// </summary>
    public string? ConvertedFrom { get; private set; }

    /// <summary>
    /// Whether a project converted as it is opened is written over its own file at once, under the same name (5.2E, the
    /// user's answer: the 5.1 file is not kept). Set by the window only: the harnesses (SeekBench, AvSync) open the same projects to read them,
    /// and leave every file as it was.
    /// </summary>
    public bool SavesConvertedProjects { get; set; }

    /// <summary>
    /// A project's timeline from before 5.2-AuditB converted, once, as it is opened (LegacyMainVideo.Migrate): its main video's
    /// clips made clips of their file in the boxes they were drawn in, its cut-outs of it pieces of them on their clocks, its
    /// streams' mixing (the live output tracks', taken from the project just before) put on Sub-Lanes and then set back to neutral,
    /// its bin with it. Whether anything was converted (its undo history is then not taken up).
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

        // The frame the old boxes were laid in: the old natural size (the main video's, cut by its first clip's crop) through the
        // project's own Resolution & Cropping, as the old view model made it.
        var (frameWidth, frameHeight) = FrameSizeFor(LegacyMainVideo.NaturalSizeOf(timeline, context));
        LegacyMainVideo.Migrate(timeline, context with { FrameWidth = frameWidth, FrameHeight = frameHeight });
        AppLog.Write($"The project's main video was converted to clips of its file (5.2-AuditB), on a {frameWidth} x {frameHeight} frame");
        return true;
    }

    /// <summary>
    /// Loads the state's source and puts every setting back. Runs as a normal busy operation. A state of the shape before
    /// 5.2-AuditB (a 5.1 project) is converted on the way (<see cref="ConvertLegacy"/>, <see cref="ConvertedFrom"/>); a queued job or
    /// an undo step is of this version's own making.
    /// </summary>
    public Task RestoreStateAsync(ProjectState state, string doneMessage) => RunOperationAsync("Opening a project", WorkLane.Interactive, async cancellationToken =>
    {
        // What makes it a project of 5.1's shape, read before anything of it changes (null for one of this version's).
        var oldShape = ProjectStore.OldShapeOf(state.Timeline, state.Deleted, state.Subtitles);
        ConvertedFrom = null;

        // Its own thumbnail is the last one taken (a project's file carries one; a queued job none), until a save draws a new one.
        TakeUpThumbnail(state.ThumbnailJpeg);
        SourcePath = state.SourcePath;
        DownloadResolution = Pick(DownloadResolutions, state.DownloadResolution, "Best");
        DownloadSubtitles = state.DownloadSubtitles;

        // The file it opened, probed first (its streams are the output tracks, and what converting a project of 5.1 reads), and not
        // put on the timeline: the project's timeline is its own. A project of dropped files alone opened none.
        if (!string.IsNullOrWhiteSpace(state.SourcePath) || !string.IsNullOrWhiteSpace(state.LocalMediaPath))
        {
            var (loaded, message) = await LoadMediaAsync(state.SourcePath, state.LocalMediaPath, applyAutomation: false, cancellationToken, place: false);
            if (!loaded)
                return message;
        }
        else
        {
            ResetForNewSource();
            LocalMediaPath = "";
            SetMediaInfo(null);
        }

        SnapToIFrames = state.SnapToIFrames;
        if (Containers.Contains(state.Container))
            Container = state.Container;
        WebOptimized = state.WebOptimized;
        ChapterMarkers = state.ChapterMarkers;
        ChaptersAtCuts = state.ChaptersAtCuts;
        ChaptersAtMarkers = state.ChaptersAtMarkers;

        // (Captions anchored to the old main video follow its file's clips.)
        LegacyMainVideo.MigrateCaptions(state.Subtitles, LocalMediaPath);
        ApplySubtitles(state.Subtitles);

        // The project's settings, without a layout of their own: its timeline is its own, put in place after them (a
        // copy, so a queued job's state stays as it was queued), with its markers and the Sequence Output's effects.
        SelectedPreset = null;
        ApplyPreset(state.Settings, withLayout: false);
        _backgroundRow.IsHidden = state.Settings.BackgroundHidden;

        // Its output tracks (queue jobs and projects saved before presets carried the tracks keep them here), with what a project
        // of 5.1 mixed its main video's streams with: the conversion takes that onto Sub-Lanes, before the timeline is put in place.
        ApplyAudioTrackStates(state.AudioTracks, legacyMixing: oldShape is not null);
        var timeline = TimelineToOpen(state);
        var converted = ConvertLegacy(timeline, state) || oldShape is not null;
        if (converted)
        {
            ConvertedFrom = oldShape ?? "clips of the main video";
            AppLog.Write($"Opened a project of the shape before 5.2 ({ConvertedFrom}): converted as it was opened");
        }

        // The timeline and what goes with it are put in place as one restore (4.4-BugHunt): nothing it sets going takes an
        // undo step or makes an edit, and the captions are projected once, at its end, through the project's own timeline.
        using (BeginRestore())
        {
            InstallDocument(timeline);
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

        // Last, so that nothing above regenerates over a hand-edited command.
        IsCommandManuallyEdited = false;
        GenerateCommand();
        if (state.ManualCommand is { } manualCommand)
            CommandPreview = manualCommand;

        // The undo history the project carries (4.4-BugHunt; none for a queued job): Ctrl+Z goes on where its save left it.
        TakeUpHistory(state, converted);
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
            // Fully portable projects (5.2A) too, each in a folder of its own name.
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

    /// <summary>Ctrl+S, or Ctrl+Shift+S when <paramref name="choosePlace"/>: the window does the saving, since it may have to ask where.</summary>
    public event Action<bool>? SaveRequested;

    [RelayCommand]
    private void SaveProjectNow() => SaveRequested?.Invoke(false);

    [RelayCommand]
    private void SaveProjectAs() => SaveRequested?.Invoke(true);

    /// <summary>Autosave, for the quick switch in the Settings button's flyout: the same setting as in Settings, Behavior.</summary>
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

    /// <summary>The setting may have been changed in the Settings window: whatever shows it is told to look again.</summary>
    public void RefreshAutosave() => OnPropertyChanged(nameof(AutosaveEnabled));

    /// <summary>The Layers and Audio lists are too narrow for a row to hold its controls and a time bar of any use: the controls are in a flyout from each row instead.</summary>
    [ObservableProperty] private bool _compactTrackControls;

    /// <summary>The Sequence Overview Scrubber is stretched and scrolled with the timeline's time bars when they are zoomed; off, it always shows the whole sequence.</summary>
    [ObservableProperty] private bool _syncMasterTimeline;

    /// <summary>The file the open project was last saved to or opened from; empty for work that has never been saved.</summary>
    public string CurrentProjectPath { get; private set; } = "";

    /// <summary>Raised when the session has been emptied for a new project: the window lets the player's file go and shows the drop hint.</summary>
    public event Action? SessionCleared;

    /// <summary>
    /// + New Project (the Project Manager, 4.1.1-B): the session emptied as HandPeg starts, with nothing open: no main
    /// video, no clips, cuts, markers or subtitles, nothing to undo, and the encoding settings back to the defaults
    /// (Settings, Export &amp; Hardware). The window asks about unsaved changes first. False while an operation runs.
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
    /// + New Project: everything a project carries that is not an encoding setting, back as HandPeg starts. The frame
    /// tall or wide as Settings says every video starts (DefaultVerticalVideo) and the Sequence's own size, the blur fill, crop, grade,
    /// LUT and Sequence Output effects neutral, no layout waiting for a video, captions off in the default style and box, no voiceover,
    /// no overlay drawing on the player.
    /// </summary>
    private void ResetProjectLook()
    {
        // The sizes as they start, without the turn to tall or wide swapping them on the way.
        _syncingDimensions = true;
        try
        {
            (UseVerticalResolution, KeepAspectRatio, OutputWidth, OutputHeight) = (AppSettings.Current.DefaultVerticalVideo, true, "", "");
        }
        finally
        {
            _syncingDimensions = false;
        }

        (PixelAspectRatio, CustomPixelAspectRatio) = (SameAsSource, "");
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
        Document.Markers = [];
        AudioLinked = true;
    }

    /// <summary>
    /// Saves the project to a file of the user's choosing (Save As), which is from then on where Save puts it.
    /// Like every save, it leaves the undo history alone: what was done before the save can still be undone after it, and
    /// (4.4-BugHunt) after the project is opened again, the history going into the file with it.
    /// </summary>
    public string SaveProjectTo(string path)
    {
        // Ctrl+S on the open project, with fully portable saves on (5.2A): a folder that holds its media. Save As to a file chosen by hand
        // writes that file as it is.
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
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            _ = OfferToKeepDownloadsAsync();
            return StatusText = $"Project saved: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return StatusText = $"Could not save the project: {ex.Message}";
        }
    }

    // ----- Project bundles (5.0-Release, Services/ProjectBundle.cs) -----

    /// <summary>
    /// Saves the project with every file it reads as one bundle (.hpb), away from the interface's thread, the status line
    /// counting. The project open stays where it was saved before (a bundle is a copy to carry, not where Save writes).
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
            StatusText = $"Could not save the bundle: {ex.Message}";
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
            StatusText = $"Could not save the bundle: {ex.Message}";
        }
    }

    /// <summary>Unpacks a bundle beside it (ProjectBundle.Unpack) and opens the project in it.</summary>
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
            StatusText = $"Could not open the bundle: {ex.Message}";
            return;
        }

        await LoadProjectAsync(project);
    }

    // What the project looked like when it was last autosaved: nothing is written while nothing has changed.
    private string _autosavedFingerprint = "";
    private bool _autosaving;

    /// <summary>
    /// Writes the project to its autosave file ("Name.autosave.hproj") if it has changed since the last time.
    /// The state is gathered here, on the UI thread, which takes a moment; making JSON of it and writing the
    /// file happen on the thread pool, so the window is never held up by the disk. The project itself is not
    /// touched, it does not count as saved, and the undo history stays as it is.
    /// </summary>
    public async Task AutosaveAsync()
    {
        if (_isBackgroundWorker || _autosaving || !HasSource)
            return;

        var fingerprint = Fingerprint();
        if (fingerprint == _autosavedFingerprint || !HasUnsavedChanges)
            return;

        var name = CurrentProjectPath.Length > 0 ? Path.GetFileNameWithoutExtension(CurrentProjectPath) : Path.GetFileNameWithoutExtension(SourcePath.Trim().Trim('"'));
        var path = ProjectStore.AutosavePath(CurrentProjectPath, name);

        // With the undo history (4.4-BugHunt): its steps are never changed once packed, so the file is written from them away
        // from the interface's thread as they are.
        var state = CaptureStateForSave(Path.GetFileNameWithoutExtension(path));
        _autosaving = true;
        try
        {
            await Task.Run(() => ProjectStore.SaveTo(state, path));
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
    /// A manual save as a fully portable project (5.2A, Settings: Fully portable project saves): Projects\Name\Name.hproj with every file it
    /// reads in media\ beside it (hard-linked on the same drive, copied in the background from another), named there by relative paths; or,
    /// when the open project is a portable folder already (carried to a USB drive, say), there. A line for the status bar.
    /// </summary>
    private string SavePortable(string name, string currentPath)
    {
        try
        {
            var path = PortableProject.IsPortableFolder(currentPath) && File.Exists(currentPath) ? currentPath : PortableProject.PathFor(name.Trim());
            var state = CaptureStateForSave(Path.GetFileNameWithoutExtension(path));
            var outcome = PortableProject.Save(state, path);
            CrashReporter.KeepOut(CrashSummaries.FilesIn(state).Append(path));
            CurrentProjectPath = path;
            DevFlightRecorder.Record("project", "path " + path);
            MarkSaved();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            _ = OfferToKeepDownloadsAsync();
            if (outcome.Copying > 0)
                _ = TellCopiedAsync(outcome);

            var missing = outcome.Missing > 0 ? $"; {outcome.Missing} named file{(outcome.Missing == 1 ? " is" : "s are")} not on this PC" : "";
            return StatusText = outcome.Copying > 0
                ? $"Project saved: {Path.GetFileNameWithoutExtension(path)}, a portable folder: {outcome.InPlace} file{(outcome.InPlace == 1 ? "" : "s")} in its media folder, {outcome.Copying} being copied there from another drive{missing}."
                : $"Project saved: {Path.GetFileNameWithoutExtension(path)}, a portable folder with all {outcome.InPlace} of its files in its media folder{missing}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return StatusText = $"Could not save the project: {ex.Message}";
        }
    }

    // The background copies of a portable save, told when they are done.
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

    /// <summary>Saves the current state as a project and returns a line for the status bar.</summary>
    public string SaveProject(string name)
    {
        // Fully portable saves (5.2A): a folder of its own, its media with it.
        if (AppSettings.Current.FullyPortableProjectSave)
            return SavePortable(name, "");

        try
        {
            var state = CaptureStateForSave(name.Trim());
            var path = ProjectStore.Save(state);
            CrashReporter.KeepOut(CrashSummaries.FilesIn(state).Append(path));
            CrashReporter.Note("project", "saved: " + CrashSummaries.Project(state, ProjectFile.CurrentVersion));
            CurrentProjectPath = path;
            DevFlightRecorder.Record("project", "path " + path);
            MarkSaved();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
            _ = OfferToKeepDownloadsAsync();
            return StatusText = $"Project saved: {Path.GetFileNameWithoutExtension(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusText = $"Could not save the project: {ex.Message}";
        }
    }

    // ----- The project's thumbnail (Pass 5.2D, Services/ProjectThumbnail.cs) -----
    // Every save carries a small picture of the Sequence at the playhead, drawn by the window's GPU engine (CaptureThumbnail). The saves
    // are made on the window's thread and most of them at once (they return a status line), so a save waits for a fresh picture a moment
    // at most (ThumbnailWaitMs) and otherwise takes the last one taken; a picture finished after that is kept for the next save. The
    // capture is drawn on the engine's pump and encoded on the thread pool: nothing of it needs the window's thread the save is holding.

    // The last thumbnail taken (or the opened project's own), when it was taken (Environment.TickCount64), the capture being drawn and
    // when it was asked for, and which project they are of: bumped as another is opened, so a late picture of the last one is let go.
    private byte[]? _thumbnail;
    private long _thumbnailTakenAt;
    private Task<byte[]?>? _thumbnailCapture;
    private long _thumbnailAskedAt;
    private int _thumbnailGeneration;

    /// <summary>How long a save waits for a fresh thumbnail, ms: the engine draws one from its frame cache in a few frames.</summary>
    private const int ThumbnailWaitMs = 300;

    /// <summary>A thumbnail this young is a save's as it is (the downloads' re-save right after a save), ms.</summary>
    private const long ThumbnailFreshMs = 1000;

    /// <summary>A capture still not done after this long is given up on and asked for again (the engine stopped with it), ms.</summary>
    private const long ThumbnailStaleMs = 10_000;

    /// <summary>
    /// The thumbnail a save writes (window's thread): a fresh one when the engine draws it within <see cref="ThumbnailWaitMs"/>, else
    /// the last one taken. None for a view model with no window (no <see cref="CaptureThumbnail"/>).
    /// </summary>
    private byte[]? ThumbnailForSave()
    {
        if (CaptureThumbnail is not { } capture || _isBackgroundWorker)
            return null;

        var last = Volatile.Read(ref _thumbnail);
        var now = Environment.TickCount64;
        if (last is not null && now - Volatile.Read(ref _thumbnailTakenAt) < ThumbnailFreshMs)
            return last;

        if (_thumbnailCapture is not { IsCompleted: false } running || now - _thumbnailAskedAt > ThumbnailStaleMs)
        {
            running = _thumbnailCapture = KeepThumbnail(capture(), _thumbnailGeneration);
            _thumbnailAskedAt = now;
        }

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

    /// <summary>A capture, its picture kept for the saves after it as it comes (on the thread it comes on), unless another project was opened meanwhile.</summary>
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

    /// <summary>Another project opened, or a new one started: its own thumbnail (none for a new one) is the last one taken, and a capture of the one before is let go.</summary>
    private void TakeUpThumbnail(byte[]? thumbnail)
    {
        Interlocked.Increment(ref _thumbnailGeneration);
        _thumbnailCapture = null;
        Volatile.Write(ref _thumbnailTakenAt, 0L);
        Volatile.Write(ref _thumbnail, thumbnail is { Length: > 0 } ? thumbnail : null);
    }

    /// <summary>A project opened, its videos' streams shown in the background (<see cref="UnpackProjectStreamsAsync"/>).</summary>
    public async Task LoadProjectAsync(string filePath)
    {
        await OpenProjectFileAsync(filePath);
        _ = UnpackProjectStreamsAsync();
    }

    private Task OpenProjectFileAsync(string filePath)
    {
        // A bundle is unpacked beside itself first, and the project in it opened (5.0-Release).
        if (filePath.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase))
            return OpenBundleAsync(filePath);

        ProjectState state;
        var opened = "Project loaded";
        try
        {
            CrashReporter.KeepOut(filePath);
            try
            {
                // A project of the shape before 5.2-AuditB (a main video, HandPeg 5.1) is read as it is and converted as it is put in
                // place (RestoreStateAsync, ConvertLegacy); the window then writes it over its file (WriteBackConverted).
                state = ProjectStore.Load(filePath);
            }
            catch (NewerProjectFormatException newer)
            {
                // Saved by a newer HandPeg: said plainly, and opened only when asked, as far as this version knows it.
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

            // An autosave is opened as work that has no file yet: Save then asks where, and the autosave is not overwritten by
            // hand. So is a project of a newer format (never written over).
            var isNewer = ProjectStore.PeekHeader(filePath) is { Version: > ProjectFile.CurrentVersion };
            CurrentProjectPath = isNewer || Path.GetFileNameWithoutExtension(filePath).EndsWith(ProjectStore.AutosaveSuffix, StringComparison.OrdinalIgnoreCase)
                ? ""
                : filePath;
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
        return OpenThenWriteBackAsync(state, $"{opened}: {name}", CurrentProjectPath, ProjectStore.PeekHeader(filePath)?.WrittenBy);
    }

    /// <summary>
    /// The project put in place (<see cref="OpenShowingCaptionsAsync"/>); one converted from 5.1's shape as it was (<see cref="ConvertedFrom"/>)
    /// is then written over its own file, when the window asks for that (<see cref="SavesConvertedProjects"/>) and the project has a file
    /// Save writes to (<paramref name="path"/>: not an autosave, not a newer format's).
    /// </summary>
    /// <param name="writtenBy">The HandPeg the file says wrote it ("HandPeg 5.1.0"), for the status line.</param>
    private async Task OpenThenWriteBackAsync(ProjectState state, string doneMessage, string path, string? writtenBy)
    {
        await OpenShowingCaptionsAsync(state, doneMessage);
        if (ConvertedFrom is { } shape && SavesConvertedProjects && !_isBackgroundWorker && path.Length > 0
            && string.Equals(path, CurrentProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            WriteBackConverted(path, $"{writtenBy ?? "a HandPeg from before 5.2"} ({shape})");
        }
    }

    /// <summary>
    /// A project converted from 5.1's shape as it was opened, written over its own file in this version's shape, under the same name
    /// (5.2E, the user's answer: the old file is not kept beside it). Written whole beside it and moved over it
    /// (ProjectStore.SaveTo), so the file is either the 5.1 project or the converted one, never half of each; a failure leaves the
    /// 5.1 file as it was, and Save writes the converted project later. Always ProjectStore.SaveTo, even with fully portable saves on:
    /// the converted project replaces that very file. A line for the status bar.
    /// </summary>
    private string WriteBackConverted(string path, string from)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            ProjectStore.SaveTo(CaptureStateForSave(name), path);
            MarkSaved();
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
    /// The project put in place, then its captions shown in the player when its export burns them in (5.0-Release: the GPU
    /// preview draws them at no cost, and a project opened with Auto-Captions on and words to show looked as if it had none,
    /// Preview Subtitles being off after every load). After the restore, which is a running operation the preview would wait for.
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

    // (A stretch marked by a right-drag along the Sequence Overview Scrubber, every track split at both its ends, went with the
    // cut list's last remnant in 5.2-Arm64AndNpu: a clip is split by the razor, or at a timecode typed in.)
}
