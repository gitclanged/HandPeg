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

    /// <summary>Frame rate of the loaded source, for HH:MM:SS:FF timecodes. 30 when it is not known.</summary>
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
        // The sequence's crop and place, its main video's first clip's (4.4: each clip has its own, which a project keeps in
        // its timeline; a preset, and HandPeg 4.3, take these).
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
        DuckAudio = DuckAudio,
        DuckAmountDb = DuckAmountDb,
        FrameEngine = FrameEngine,
        CenterZoom = SequencePlace().Zoom,
        // The layout, for a preset (a project's are its timeline, and ProjectStore leaves these out): every clip but the
        // main video's as a template, the main video's look, and how many pictures lie under it.
        Layers = TemplateLayers(),
        MainLayer = MainLayer.ToState(),
        BackgroundHidden = _backgroundRow.IsHidden,
        MainVideoIndex = TemplateMainIndex(),
        LayoutSourceAspectRatio = GetLayoutSourceAspect(),
        AutoCaptions = AutoCaptions,
        CaptionStyle = CaptionStyle.Clone(),
        CenterOffsetX = SequencePlace().OffsetX,
        CenterOffsetY = SequencePlace().OffsetY,
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

    /// <summary>The timeline's clips but the main video's, each with its track's number and its timing, as a preset carries a layout.</summary>
    private List<LayerState> TemplateLayers() =>
        Layers.Where(l => !l.IsMainVideo && !l.IsEmptyTrack && ClipOf(l) is not null).Select(l => l.ToState()).ToList();

    /// <summary>How many of a preset's layout's pictures lie under the main video (all of them when it has no clip).</summary>
    private int TemplateMainIndex()
    {
        var pictures = Layers.Where(l => !l.IsAudio && !l.IsEmptyTrack && ClipOf(l) is not null).ToList();
        var main = pictures.FindIndex(l => l.IsMainVideo);
        return main < 0 ? pictures.Count : pictures.Take(main).Count(l => !l.IsMainVideo);
    }

    /// <summary>What is set track by track: action, codec, bitrate, title, gain and processing.</summary>
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
            CaptionAudioTrackIndex = CaptionAudioTrack?.Index ?? 0,
            Deleted = RecycleBin.Select(i => i.Clip with { Clip = CopyOfClip(i.Clip.Clip) }).ToList(),
            ManualCommand = IsCommandManuallyEdited ? CommandPreview : null,
        };
    }

    /// <summary>
    /// A state's timeline as it is opened: a copy of its own; the main video's clips playing the file this session opened
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
                if (clip.ContentKind == LayerKind.MainVideo && !string.Equals(state.LocalMediaPath, LocalMediaPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(clip.SourcePath, state.LocalMediaPath, StringComparison.OrdinalIgnoreCase))
                {
                    clip.SourcePath = LocalMediaPath;
                }
            }
        }

        // A project from before each clip of the main video had a crop of its own (4.3) kept one for the whole sequence in its
        // settings: its clips take it, when none of them has one (4.4). A look is never changed in place: each gets a copy.
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

    /// <summary>The sequence's crop as percentages (left, top, right, bottom): its main video's first clip's (GetCropFractions).</summary>
    private double[] SequenceCropPercent()
    {
        var (left, top, right, bottom) = GetCropFractions();
        return [Math.Round(left * 100, 2), Math.Round(top * 100, 2), Math.Round(right * 100, 2), Math.Round(bottom * 100, 2)];
    }

    /// <summary>The sequence's place of its main video (zoom and offsets): its first clip's (GetCenterRect's), the Center controls' while that is the clip they stand for.</summary>
    private (double Zoom, double OffsetX, double OffsetY) SequencePlace() =>
        AnchorMainView() is { } anchor && !ReferenceEquals(anchor, CenterTarget) ? KeptPlaceOf(anchor) : (CenterZoom, CenterOffsetX, CenterOffsetY);

    /// <summary>Whether a source is loaded, so there is something worth saving or queueing.</summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(LocalMediaPath);

    /// <summary>Loads the state's source and puts every setting back. Runs as a normal busy operation.</summary>
    public Task RestoreStateAsync(ProjectState state, string doneMessage) => RunOperationAsync("Opening a project", WorkLane.Interactive, async cancellationToken =>
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
        ChaptersAtMarkers = state.ChaptersAtMarkers;
        ApplySubtitles(state.Subtitles);

        // The project's settings, without a layout of their own: its timeline is its own, put in place after them (a
        // copy, so a queued job's state stays as it was queued), with its markers and the Sequence Output's effects.
        SelectedPreset = null;
        ApplyPreset(state.Settings, withLayout: false);
        _backgroundRow.IsHidden = state.Settings.BackgroundHidden;

        // The timeline and what goes with it are put in place as one restore (4.4-BugHunt): nothing it sets going takes an
        // undo step or makes an edit, and the captions are projected once, at its end, through the project's own timeline.
        using (BeginRestore())
        {
            InstallDocument(TimelineToOpen(state));
            TakeMainFile(_mediaInfo);
            SetBin(state.Deleted);
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
        }

        if (!string.IsNullOrWhiteSpace(state.DestinationPath))
            DestinationPath = state.DestinationPath;

        // Last, so that nothing above regenerates over a hand-edited command.
        IsCommandManuallyEdited = false;
        GenerateCommand();
        if (state.ManualCommand is { } manualCommand)
            CommandPreview = manualCommand;

        // The undo history the project carries (4.4-BugHunt; none for a queued job): Ctrl+Z goes on where its save left it.
        TakeUpHistory(state);
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

    /// <summary>The master timeline is stretched and scrolled with the layer and audio time bars when they are zoomed; off, it always shows the whole sequence.</summary>
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
        MarkSaved();
        SessionCleared?.Invoke();
        Log("New project: nothing is open. Drop a video on the player, or use Add Media, to start.");
        return true;
    }

    /// <summary>
    /// + New Project: everything a project carries that is not an encoding setting, back as HandPeg starts. The frame
    /// tall or wide as Settings says every video starts (DefaultVerticalVideo) and the source's size, the sharp video centred, the blur, crop, grade, LUT and Sequence Output effects
    /// neutral, the main video's own look and place in the stack, captions off in the default style and box, no
    /// voiceover, nothing soloed, no overlay drawing on the player.
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

        // Tall, the engine is what fits the picture into the frame (as the Vertical Video switch does).
        if (UseVerticalResolution)
            FrameEngine = true;

        (PixelAspectRatio, CustomPixelAspectRatio) = (SameAsSource, "");
        (IsInteractiveCropActive, DrawTargetLayer, IsArrangeActive) = (false, null, false);
        ResetCrop();
        ResetCenterVideo();
        ResetBlurSettings();
        _backgroundRow.IsHidden = false;
        _layoutSourceAspect = 0;
        _mainLookToCome = null;

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
        _soloed.Clear();
        ApplySolo();
        AudioLinked = true;
    }

    /// <summary>
    /// Saves the project to a file of the user's choosing (Save As), which is from then on where Save puts it.
    /// Like every save, it leaves the undo history alone: what was done before the save can still be undone after it, and
    /// (4.4-BugHunt) after the project is opened again, the history going into the file with it.
    /// </summary>
    public string SaveProjectTo(string path)
    {
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

    /// <summary>Saves the current state as a project and returns a line for the status bar.</summary>
    public string SaveProject(string name)
    {
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

    public Task LoadProjectAsync(string filePath)
    {
        ProjectState state;
        var opened = "Project loaded";
        try
        {
            CrashReporter.KeepOut(filePath);
            try
            {
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
        return RestoreStateAsync(state, $"{opened}: {name}");
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
    /// A stretch marked by hand (a right-drag along the timeline, or typed in): every unlocked track is split at its start
    /// and its end (4.3C: what 4.2.0 made a cut segment of), so it can be deleted, moved or kept on its own. With snapping
    /// on it grows to the surrounding I-frames ("prefer longer"). Returns an error message, or null on success.
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

        if (!SplitAt(startSeconds, stopSeconds))
            return StatusText;
        PendingStartMs = null;
        return null;
    }
}
