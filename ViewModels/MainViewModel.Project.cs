using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        Subtitles = CaptureSubtitles(),
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
        ApplySubtitles(state.Subtitles);

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

        // Its captions are the ones that were saved with it, words, times and word timings: nothing is transcribed.
        _ = SyncCaptionsAfterLoadAsync();

        // A project brings its videos with it. The timeline proxy first, when it is rendered by itself; then the
        // main video and every video layer get their own.
        if (AppSettings.Current.AutoRenderTimelineProxy && IsEditorMode)
        {
            _timelinePending = true;
            _ = RenderTimelineProxyWhenIdleAsync();
        }

        RefreshProxies();
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

    /// <summary>The Layers and Audio lists are too narrow for a row to hold its controls and a time bar of any use: the controls are in a flyout from each row instead. Editor Mode only.</summary>
    [ObservableProperty] private bool _compactTrackControls;

    // ----- Proxies -----
    // Every video of the editor's session (the main one, and each video layer) has an original, which is what
    // is exported, and may have a proxy, which is what the player and its graph read. Proxies are made one at
    // a time, in the background, whenever a video comes into the session: opened, added as a layer, or loaded
    // with a project.

    // Original file to its proxy, for the proxies that are ready.
    private readonly Dictionary<string, string> _proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _proxyQueue = new();
    private readonly HashSet<string> _proxyQueued = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _proxyWork;
    private bool _proxyWorking;
    private int _proxyDone, _proxyTotal;

    private bool ProxiesOn => !_isBackgroundWorker && IsEditorMode && AppSettings.Current.EnableEditorProxies && AppSettings.Current.PreviewQuality != AppSettings.HighPreview;

    // The Low tier's proxies are smaller ones, kept beside the Medium tier's.
    private static bool LowPreview => AppSettings.Current.PreviewQuality == AppSettings.LowPreview;

    public IReadOnlyList<string> PreviewQualityChoices { get; } =
        ["High (Preview Render Quality)", "Medium (Editor Mode Quality)", "Low (Performance Quality)"];

    /// <summary>
    /// The preview's quality tier, for the Views menu. High shows the original files; Medium, 720-line proxies;
    /// Low, 360-line ones. Changing it changes what the player and its graph read at once: the proxies of the
    /// new tier are taken from the cache or made, and until they are there the originals are shown.
    /// </summary>
    public string PreviewQualityChoice
    {
        get => PreviewQualityChoices.First(c => c.StartsWith(AppSettings.Current.PreviewQuality, StringComparison.Ordinal));
        set
        {
            var tier = value?.Split(' ')[0];
            if (tier is not (AppSettings.HighPreview or AppSettings.MediumPreview or AppSettings.LowPreview) || tier == AppSettings.Current.PreviewQuality)
                return;

            SaveView(settings => settings.PreviewQuality = tier);
            OnPropertyChanged();
            AppLog.Write($"Preview quality: {tier}.");

            // The proxies in use were another tier's.
            StopProxies();
            _proxies.Clear();
            RefreshProxies();
            ProxiesChanged();
            StatusText = tier == AppSettings.HighPreview ? "Preview quality High: the player shows the original files." : $"Preview quality {tier}: the player shows {(tier == AppSettings.LowPreview ? "360" : "720")}-line proxies.";
        }
    }

    /// <summary>What the player is given for the open video: its proxy in Editor Mode, when proxies are on and it has one; the file itself otherwise.</summary>
    public string PreviewMediaPath => ProxiesOn && _proxies.TryGetValue(LocalMediaPath, out var proxy) && File.Exists(proxy) ? proxy : LocalMediaPath;

    /// <summary>The layers' proxies, for the player's graph. Empty outside Editor Mode, and for a background encode.</summary>
    private Dictionary<string, string> GetLayerProxies()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!ProxiesOn)
            return found;

        foreach (var layer in Layers.Where(l => l.IsVideoFile && !l.IsLoading))
        {
            if (_proxies.TryGetValue(layer.ImagePath, out var proxy) && File.Exists(proxy))
                found[layer.ImagePath] = proxy;
        }

        return found;
    }

    /// <summary>What the player should have open has changed (a proxy is ready, or the mode changed): the window has it look again.</summary>
    public event Action? PlayerSourceChanged;

    /// <summary>
    /// Looks over the session's videos and sees to a proxy for each that has none: one already in the cache
    /// is taken at once, the rest are queued to be made. Called whenever a video comes in. Does nothing in
    /// Encoder Mode, or with proxies switched off.
    /// </summary>
    private void RefreshProxies()
    {
        if (!ProxiesOn)
            return;

        var videos = Layers.Where(l => l.IsVideoFile).Select(l => l.ImagePath).ToList();
        if (HasSource && SourceWidth > 0)
            videos.Insert(0, LocalMediaPath);

        var took = false;
        foreach (var media in videos.Where(m => m.Length > 0 && File.Exists(m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_proxies.ContainsKey(media) || !_proxyQueued.Add(media))
                continue;

            if (ProxyCache.Find(media, LowPreview) is { } ready)
            {
                _proxies[media] = ready;
                _proxyQueued.Remove(media);
                took = true;
                AppLog.Write($"Proxy: found in the cache for {Path.GetFileName(media)}, nothing is generated; the player shows it.");
                continue;
            }

            _proxyQueue.Enqueue(media);
            _proxyTotal++;
        }

        if (took)
            ProxiesChanged();
        if (!_proxyWorking && _proxyQueue.Count > 0)
            _ = MakeProxiesAsync();
    }

    // A proxy came into use: the player goes over to it, and its graph is built again to read it.
    private void ProxiesChanged()
    {
        PlayerSourceChanged?.Invoke();
        LiveFilterInvalidated?.Invoke();
    }

    /// <summary>
    /// Makes the queued proxies, one after another, off the UI thread. How far along the one in hand is goes
    /// to the progress bar at the bottom of the window (when no other operation has it), and what is being
    /// done to the status text beside it; when the last is done, the status text says so.
    /// </summary>
    private async Task MakeProxiesAsync()
    {
        _proxyWorking = true;
        var work = _proxyWork = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var encoders = SocialSquisher.ChooseEncoders(VideoEncoders.Where(e => e.IsHardware).Select(e => e.Name).ToList());
        var low = LowPreview;
        try
        {
            while (_proxyQueue.Count > 0 && !work.IsCancellationRequested && ProxiesOn)
            {
                // The timeline proxy goes first: one file of everything is worth more to the player than each video's own.
                while ((_timelineRendering || _timelinePending) && !work.IsCancellationRequested)
                    await Task.Delay(300);
                if (work.IsCancellationRequested)
                    break;

                var media = _proxyQueue.Dequeue();
                var name = Path.GetFileName(media);
                var which = _proxyTotal > 1 ? $" ({_proxyDone + 1} of {_proxyTotal})" : "";
                AppLog.Write($"Proxy: generating one for {name}.");
                if (!IsBusy)
                    StatusText = $"Generating Proxy{which}: {name}...";
                var progress = new Progress<double>(percent =>
                {
                    // The bar and the status line are the running operation's while there is one; otherwise they show the proxy.
                    if (work.IsCancellationRequested || IsBusy)
                        return;

                    (IsProgressIndeterminate, ProgressValue) = (false, percent);
                    StatusText = $"Generating Proxy{which}: {name}... {percent:0}%";
                });

                string? made = null;
                try
                {
                    made = await Task.Run(() => ProxyCache.GenerateAsync(
                        media, low, Math.Clamp(AppSettings.Current.ProxyCacheLimitMb, 512, 2_000_000) / 1024.0, encoders, progress, work.Token), work.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Stopped while it was being made (another video or project was opened): the queue and the
                // counts belong to the next session by now, and are left alone.
                if (work.IsCancellationRequested)
                    break;

                _proxyQueued.Remove(media);
                _proxyDone++;
                if (made is not null && !work.IsCancellationRequested)
                {
                    _proxies[media] = made;
                    AppLog.Write($"Proxy: ready for {name}; the player shows it from now on.");
                    ProxiesChanged();
                }
            }
        }
        finally
        {
            // Only while this is still the run in hand: one that was stopped has been replaced, or will be.
            if (ReferenceEquals(_proxyWork, work))
            {
                _proxyWorking = false;
                if (!work.IsCancellationRequested && _proxyQueue.Count == 0)
                {
                    var made = _proxyDone;
                    (_proxyDone, _proxyTotal) = (0, 0);
                    AppLog.Write("Proxy: all proxies for the session are generated.");
                    _ = AnnounceProxiesAsync(made);
                }
            }
        }
    }

    // Said on the status line once nothing else is using it: an operation that is running has it until it is done.
    private async Task AnnounceProxiesAsync(int made)
    {
        for (var waited = 0; IsBusy && waited < 600; waited++)
            await Task.Delay(100);
        if (IsBusy || _proxyWorking || made == 0)
            return;

        ProgressValue = 0;
        StatusText = made == 1 ? "Proxies Generated: 1 video, ready for smooth scrubbing." : $"Proxies Generated: {made} videos, ready for smooth scrubbing.";
    }

    /// <summary>Stops making proxies and forgets what was queued: Encoder Mode has no use for them.</summary>
    private void StopProxies()
    {
        // Cancelling ends the FFmpeg that is encoding one, at once. The run it belonged to is let go of here
        // (it finds that out and touches nothing more), so that another can start straight away.
        var wasWorking = _proxyWorking;
        _proxyWork?.Cancel();
        (_proxyWork, _proxyWorking) = (null, false);
        _proxyQueue.Clear();
        _proxyQueued.Clear();
        (_proxyDone, _proxyTotal) = (0, 0);
        if (wasWorking)
        {
            AppLog.Write("Proxy: generation stopped, and its FFmpeg ended.");
            if (!IsBusy)
                ProgressValue = 0;
        }
    }

    /// <summary>
    /// Another video or project is being opened: whatever was being made for the preview of the one before
    /// is stopped, and its FFmpeg processes ended at once. Proxies that were finished stay in the cache, the
    /// timeline proxy among them, for the next time that video or project is opened.
    /// </summary>
    private void StopPreviewWork()
    {
        if (_isBackgroundWorker)
            return;

        StopProxies();
        _proxies.Clear();

        _timelineIdle?.Cancel();
        _timelinePending = false;
        if (_timelineRendering)
        {
            _timelineRender?.Cancel();
            (_timelineRender, _timelineRendering) = (null, false);
            AppLog.Write("Timeline proxy: rendering stopped, another video or project is being opened.");
            if (!IsBusy)
                ProgressValue = 0;
        }

        if (_timelineProxy.Length > 0)
        {
            AppLog.Write("Timeline proxy: left in the cache for the next time this timeline is opened.");
            (_timelineProxy, _timelineProxyPrint) = ("", "");
        }
    }

    /// <summary>
    /// Where the timeline proxy of a timeline is kept: named after the timeline itself (everything a project
    /// saves), the preview quality, and the size and date of each file on it. The same timeline, opened
    /// again, finds its proxy; anything else about it, and it is another file.
    /// </summary>
    private string TimelineProxyPath(string print)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var text = new System.Text.StringBuilder(print).Append('|').Append(AppSettings.Current.PreviewQuality);
        foreach (var file in Layers.Select(l => l.ImagePath).Prepend(LocalMediaPath).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(file.Trim().Trim('"'));
                if (info.Exists)
                    text.Append('|').Append(info.Length.ToString(invariant)).Append(':').Append(info.LastWriteTimeUtc.Ticks.ToString(invariant));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Not a file that can be asked: the path, which is in the project, has to do.
            }
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Path.Combine(ProxyCache.Folder, $"timeline_{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}.mp4");
    }

    private async Task SyncCaptionsAfterLoadAsync()
    {
        for (var waited = 0; IsBusy && waited < 600; waited++)
            await Task.Delay(100);
        SyncEditorCaptions();
    }

    // ----- The timeline proxy -----
    // The whole picture of the timeline, every layer composed, rendered once into a single silent file in the
    // proxy cache. While it stands, the player plays that file and composes nothing; only the sound is still
    // mixed live. It is a picture of the timeline as it was when it was rendered, so the moment the timeline
    // is anything else it is deleted and the player composes live again, at the preview quality chosen.

    private string _timelineProxy = "", _timelineProxyPrint = "", _timelineRenderPrint = "";
    private CancellationTokenSource? _timelineRender, _timelineIdle;
    private bool _timelineRendering, _timelinePending;

    /// <summary>There is a timeline proxy and it is the timeline as it now is: the player plays it. Editor Mode only.</summary>
    public bool IsTimelineProxyActive => !_isBackgroundWorker && IsEditorMode && _timelineProxy.Length > 0 && File.Exists(_timelineProxy);

    [RelayCommand]
    private void RenderTimelineProxy() => _ = RenderTimelineProxyAsync(asked: true);

    /// <summary>
    /// Called whenever anything that goes into the output has changed (it is where the command is generated
    /// again). A timeline proxy that no longer shows the timeline is deleted on the spot, one that is being
    /// rendered of the old timeline is stopped, and the wait for the timeline to be left alone starts over.
    /// </summary>
    private void TimelineEdited()
    {
        if (_isBackgroundWorker)
            return;

        if (_timelineProxy.Length > 0 || _timelineRendering)
        {
            var print = Fingerprint();
            if (_timelineProxy.Length > 0 && print != _timelineProxyPrint)
                DropTimelineProxy();
            if (_timelineRendering && print != _timelineRenderPrint)
                _timelineRender?.Cancel();
        }

        RestartTimelineIdle();
    }

    private void DropTimelineProxy()
    {
        var file = _timelineProxy;
        (_timelineProxy, _timelineProxyPrint) = ("", "");
        AppLog.Write("Timeline proxy: invalidated by an edit, and deleted. The player composes live again.");

        // The player lets go of the file first (it goes back to the videos themselves), and then the file goes.
        ProxiesChanged();
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(250);
                }
            }
        });
    }

    // The wait for the timeline to be left alone: started over by every edit.
    private void RestartTimelineIdle()
    {
        _timelineIdle?.Cancel();
        var settings = AppSettings.Current;
        if (!IsEditorMode || !settings.AutoRenderTimelineProxy || !HasSource)
            return;

        var wait = _timelineIdle = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _ = WaitThenRenderAsync(TimeSpan.FromSeconds(Math.Clamp(settings.TimelineProxyIdleSeconds, 2, 86400)), wait.Token);
    }

    private async Task WaitThenRenderAsync(TimeSpan idle, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(idle, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsTimelineProxyActive && !_timelineRendering)
            await RenderTimelineProxyAsync(asked: false);
    }

    // After a project is opened: as soon as the opening is over.
    private async Task RenderTimelineProxyWhenIdleAsync()
    {
        for (var waited = 0; IsBusy && waited < 1200; waited++)
            await Task.Delay(100);
        _timelinePending = false;
        if (!IsBusy)
            await RenderTimelineProxyAsync(asked: false);
    }

    /// <summary>
    /// Renders the timeline proxy, in the background: the same graph an export compiles (from the original
    /// files), at the size of the preview quality in use, with no sound. How far along it is goes to the
    /// progress bar and the status line, as a video's own proxy does. An edit made meanwhile stops it.
    /// </summary>
    private async Task RenderTimelineProxyAsync(bool asked)
    {
        if (_isBackgroundWorker || !IsEditorMode || !HasSource || _timelineRendering)
            return;

        var seconds = SequenceSeconds;
        if (IsBusy || seconds < 0.2)
        {
            if (asked)
                StatusText = IsBusy ? "Wait for the running operation to finish, then render the timeline proxy." : "There is nothing on the timeline to render.";
            else if (IsBusy)
                RestartTimelineIdle();
            return;
        }

        var print = Fingerprint();
        if (IsTimelineProxyActive && print == _timelineProxyPrint)
        {
            if (asked)
                StatusText = "The timeline proxy is up to date: the player is already playing it.";
            return;
        }

        // Rendered before, of this very timeline (in this session or an earlier one): it is there to be played.
        var silent = TimelineProxyPath(print);
        if (ProxyCache.Touch(silent))
        {
            (_timelineProxy, _timelineProxyPrint) = (silent, print);
            AppLog.Write($"Timeline proxy: found in the cache ({new FileInfo(silent).Length / 1048576.0:0.0} MB), nothing is rendered; the player plays it.");
            ProxiesChanged();
            if (!IsBusy)
                StatusText = "Timeline Proxy Ready (from the cache): the player is playing the flattened timeline. Any edit discards it.";
            return;
        }

        (_timelineRendering, _timelineRenderPrint) = (true, print);
        var work = _timelineRender = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        Directory.CreateDirectory(ProxyCache.Folder);
        var rendered = Path.Combine(ProxyCache.Folder, $"timeline_{Guid.NewGuid():N}.render.mp4");
        var done = false;
        try
        {
            // As tall as the preview quality's proxies are: 720 lines, 360 for Low, the frame itself for High.
            var (_, frameHeight) = GetOutputSize() ?? (DefaultSourceWidth, DefaultSourceHeight);
            var lines = AppSettings.Current.PreviewQuality switch { AppSettings.LowPreview => 360, AppSettings.HighPreview => frameHeight, _ => 720 };
            var percent = (int)Math.Clamp(Math.Round(lines * 100.0 / Math.Max(frameHeight, 2)), 10, 100);
            var command = BuildPreviewCommand(rendered, [(0, seconds)], percent, null);

            AppLog.Write($"Timeline proxy: rendering {seconds:0.#} s of timeline at {percent}% ({AppSettings.Current.PreviewQuality} quality).");
            if (!IsBusy)
                StatusText = "Rendering Timeline Proxy...";
            var progress = new Progress<FfmpegProgress>(report =>
            {
                if (work.IsCancellationRequested || IsBusy || report.Position is not { } position)
                    return;

                var percentDone = Math.Clamp(position.TotalSeconds / seconds * 100, 0, 100);
                (IsProgressIndeterminate, ProgressValue) = (false, percentDone);
                StatusText = $"Rendering Timeline Proxy... {percentDone:0}%";
            });

            await Task.Run(() => FfmpegRunner.RunAsync(command, progress, work.Token), work.Token);

            // Its sound is taken off: the proxy is a picture only. At High the picture is kept as it was rendered
            // (copied, which takes a moment). At Medium and Low it is brought under the tier's bitrate cap, with
            // an I-frame every 15 frames like the videos' own proxies: lighter to decode and quick to seek.
            var finish = AppSettings.Current.PreviewQuality switch
            {
                AppSettings.HighPreview => "-c copy",
                AppSettings.LowPreview => "-c:v libx264 -preset veryfast -tune fastdecode -crf 30 -maxrate 2M -bufsize 4M -g 15 -pix_fmt yuv420p",
                _ => "-c:v libx264 -preset veryfast -tune fastdecode -crf 28 -maxrate 4M -bufsize 8M -g 15 -pix_fmt yuv420p",
            };
            await Task.Run(() => FfmpegRunner.RunAsync(
                $"ffmpeg -hide_banner -y -i \"{rendered}\" -map 0:v:0 {finish} -an -movflags +faststart \"{silent}\"", new Progress<FfmpegProgress>(), work.Token), work.Token);
            done = !work.IsCancellationRequested && File.Exists(silent);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_timelineRender, work))
                AppLog.Write("Timeline proxy: rendering stopped, the timeline was edited.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Timeline proxy: it could not be rendered", ex);
            if (!IsBusy)
                StatusText = "The timeline proxy could not be rendered (see HandPeg.log). The player composes live.";
        }
        finally
        {
            // A render that was stopped for another video or project has been let go of already.
            var current = ReferenceEquals(_timelineRender, work);
            if (current)
                _timelineRendering = false;
            done &= current;
            foreach (var leftover in done ? new[] { rendered } : [rendered, silent])
            {
                try
                {
                    if (File.Exists(leftover))
                        File.Delete(leftover);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the cache's next trimming.
                }
            }
        }

        if (!done)
            return;

        // Edited while the last of it was being written: it is already out of date.
        if (Fingerprint() != print)
        {
            try
            {
                File.Delete(silent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            RestartTimelineIdle();
            return;
        }

        // It counts towards the limit of the cache like any proxy; the oldest go to make room for it.
        var limit = Math.Clamp(AppSettings.Current.ProxyCacheLimitMb, 512, 2_000_000) / 1024.0;
        _ = Task.Run(() => ProxyCache.Trim(limit, silent));

        (_timelineProxy, _timelineProxyPrint) = (silent, print);
        AppLog.Write($"Timeline proxy: ready ({new FileInfo(silent).Length / 1048576.0:0.0} MB); the player plays it and composes nothing.");
        ProxiesChanged();
        if (!IsBusy)
        {
            ProgressValue = 0;
            StatusText = "Timeline Proxy Ready: the player is playing the flattened timeline. Any edit discards it.";
        }
    }

    // ----- Encoder Mode stands apart from the editor -----

    // Whether Live Preview was on in Editor Mode, to put back on return: Encoder Mode plays the source as it is.
    private bool? _editorLivePreview;

    /// <summary>
    /// Entering Encoder Mode: the player shows the source file itself, not the editor's composition (Live
    /// Preview goes off, and its buttons are not there), and the layout pane closes. Back in Editor Mode, Live
    /// Preview is as it was left.
    /// </summary>
    private void ApplyModePreview()
    {
        if (IsEncoderMode)
        {
            _editorLivePreview ??= LivePreview;
            LivePreview = false;
            IsArrangeActive = false;
        }
        else if (_editorLivePreview is { } was)
        {
            _editorLivePreview = null;
            LivePreview = was;
        }

        // The proxy is the editor's: Encoder Mode plays the file itself, and no proxy is made there.
        if (IsEncoderMode)
            StopProxies();
        else
            RefreshProxies();

        OnPropertyChanged(nameof(CaptionOptionsEnabled));
        SyncEditorCaptions();

        ProxiesChanged();
    }

    public ObservableCollection<string> RecentEditorFiles { get; } = new(AppSettings.Current.RecentEditorFiles.Where(File.Exists));

    /// <summary>Notes a file that was opened or put on the timeline in Editor Mode. The dozen most recent are kept.</summary>
    private void NoteEditorFile(string? path)
    {
        if (!IsEditorMode || _isBackgroundWorker || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        foreach (var same in RecentEditorFiles.Where(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList())
            RecentEditorFiles.Remove(same);
        RecentEditorFiles.Insert(0, path);
        while (RecentEditorFiles.Count > 12)
            RecentEditorFiles.RemoveAt(RecentEditorFiles.Count - 1);
        SaveView(settings => settings.RecentEditorFiles = [.. RecentEditorFiles]);
    }

    /// <summary>For the Recent Editor Files box: choosing a file opens it as the source. Nothing stays chosen.</summary>
    public string? SelectedRecentEditorFile
    {
        get => null;
        set
        {
            if (value is { Length: > 0 } && File.Exists(value) && !IsBusy)
            {
                SourcePath = value;
                LoadSourceCommand.Execute(null);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>The master timeline is stretched and scrolled with the layer and audio time bars when they are zoomed; off, it always shows the whole sequence.</summary>
    [ObservableProperty] private bool _syncMasterTimeline;

    /// <summary>The file the open project was last saved to or opened from; empty for work that has never been saved.</summary>
    public string CurrentProjectPath { get; private set; } = "";

    /// <summary>
    /// Saves the project to a file of the user's choosing (Save As), which is from then on where Save puts it.
    /// Like every save, it leaves the undo history alone: what was done before the save can still be undone after it.
    /// </summary>
    public string SaveProjectTo(string path)
    {
        try
        {
            ProjectStore.SaveTo(CaptureState(Path.GetFileNameWithoutExtension(path)), path);
            CurrentProjectPath = path;
            MarkSaved();
            RefreshRecentProjects(Path.GetFileNameWithoutExtension(path));
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
        var state = CaptureState(Path.GetFileNameWithoutExtension(path));
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
            var path = ProjectStore.Save(CaptureState(name.Trim()));
            CurrentProjectPath = path;
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

            // The project that was open is being left: what was being made for its preview stops now.
            StopPreviewWork();

            // An autosave is opened as work that has no file yet: Save then asks where, and the autosave is not overwritten by hand.
            CurrentProjectPath = Path.GetFileNameWithoutExtension(filePath).EndsWith(ProjectStore.AutosaveSuffix, StringComparison.OrdinalIgnoreCase) ? "" : filePath;
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
