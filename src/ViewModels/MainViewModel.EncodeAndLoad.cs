using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.ViewModels;

// The hardware encoder probe, loading a source (files, links, media), settings saved, shutdown, and the encode runner.
public partial class MainViewModel
{
    // Whether hardware encoders were listed before Frame 1 (TakeEarlyHardwareEncoders).
    private bool _hardwareEncodersEarly;

    /// <summary>
    /// Lists hardware encoders from a saved probe for this FFmpeg and drivers (loaded off-thread during window construction) before
    /// Frame 1, so the Export tab opens on its saved encoder instead of briefly showing Copy. Otherwise the post-Frame-1 probe fills it.
    /// </summary>
    public void TakeEarlyHardwareEncoders()
    {
        if (_isBackgroundWorker || EncoderProber.TakeEarlyResult(TimeSpan.FromMilliseconds(100)) is not { } saved)
            return;

        ++_hardwareProbeRun;
        _hardwareEncodersEarly = true;
        ApplyHardwareEncoders(saved.Supported, saved.Problems);
    }

    /// <summary>
    /// Early encoders matched on FFmpeg only (EncoderProber.LoadSavedResultEarly); now verifies GPUs and drivers off-thread and
    /// re-probes on any change.
    /// </summary>
    private async Task RecheckEarlyEncodersAsync()
    {
        // GPUs are enumerated there too (off-thread, after Frame 1), so problems for absent vendors (e.g. "h264_nvenc: Cannot load
        // nvcuda.dll" on an AMD-only PC from an old encoder-probe.json) are suppressed.
        var same = await Task.Run(() =>
        {
            var still = EncoderProber.CardsStillMatch();
            _ = GpuAdapters.All;
            return still;
        });
        NameEncodersForCards();
        if (!same)
            await ProbeHardwareEncodersAsync();
        else
            ShowHardwareEncoderStatus();
    }

    /// <summary>
    /// Relabels hardware encoders with their GPU once enumerated after Frame 1 (e.g. an MF encoder shows "(Qualcomm Adreno)");
    /// the selection is preserved.
    /// </summary>
    private void NameEncodersForCards()
    {
        if (!GpuAdapters.IsListed || !GpuAdapters.HasQualcommAdreno)
            return;

        _resolvingEncoder = true;
        try
        {
            for (var i = 0; i < VideoEncoders.Count; i++)
            {
                var option = VideoEncoders[i];
                if (option.Family != EncoderFamily.MediaFoundation || EncoderOption.FromHardwareName(option.Name, qualcommAdreno: true) is not { } named || named == option)
                    continue;
                var selected = VideoEncoder == option;
                VideoEncoders[i] = named;
                if (selected)
                    VideoEncoder = named;
            }
        }
        finally
        {
            _resolvingEncoder = false;
        }
    }

    /// <summary>Tests the GPU encoders in the background and offers the working ones in the Export tab.</summary>
    private async Task ProbeHardwareEncodersAsync()
    {
        // A newer probe (install or settings change) supersedes older results.
        var run = ++_hardwareProbeRun;

        if (!File.Exists(DependencyUpdater.FfmpegPath))
        {
            HardwareEncoderStatusText = "Install FFmpeg (in Settings) to detect hardware encoders";
            return;
        }

        HardwareEncoderStatusText = "Checking hardware encoders...";
        List<string> supported;
        List<string> problems;
        try
        {
            // Cached result for this FFmpeg and driver set: skip testing (EncoderProber.LoadSavedResult).
            (supported, problems) = await Task.Run(EncoderProber.LoadSavedResult) is { } saved
                ? saved
                : await WorkScheduler.Run(new WorkSpec("Testing hardware encoders", WorkLane.Idle), work => EncoderProber.ProbeAsync(work.Token), _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (run == _hardwareProbeRun)
                AppLog.Write("Hardware encoder check failed", ex);
            HardwareEncoderStatusText = $"Hardware encoder check failed: {ex.Message}";
            return;
        }

        if (run != _hardwareProbeRun)
            return;

        ApplyHardwareEncoders(supported, problems);
    }

    /// <summary>Puts the hardware encoders found into the list, keeping the selection where it still exists.</summary>
    private void ApplyHardwareEncoders(List<string> supported, List<string> problems)
    {
        // Rebuild the hardware entries, keeping the selection if still present; on first fill select the default hardware
        // encoder unless the user picked another meanwhile.
        var untouched = VideoEncoder.Family is EncoderFamily.Copy || VideoEncoder == DefaultVideoEncoder;
        var selected = _pendingDefaultEncoder is { } pending && untouched ? pending : VideoEncoder.Name;
        _pendingDefaultEncoder = null;
        foreach (var stale in VideoEncoders.Where(e => e.IsHardware).ToList())
            VideoEncoders.Remove(stale);
        foreach (var name in supported)
            VideoEncoders.Add(EncoderOption.FromHardwareName(name, GpuAdapters.IsListed && GpuAdapters.HasQualcommAdreno));
        _hardwareEncodersListed = true;
        _resolvingEncoder = true;
        try
        {
            VideoEncoder = VideoEncoders.FirstOrDefault(e => e.Name == selected) ?? DefaultVideoEncoder;

            // A built-in applied before the probe fell back to software: switch once to its codec's hardware encoder at the slowest
            // step (UpdateEncoderPresets' hardware default).
            if (_pendingPreferredCodec is { } wanted && !VideoEncoder.IsHardware
                && ResolveEncoder(new EncodingPreset { PreferredCodec = wanted, VideoEncoder = VideoEncoder.Name }) is ({ IsHardware: true } hardware, _))
            {
                VideoEncoder = hardware;
                selected = hardware.Name;
            }
        }
        finally
        {
            _resolvingEncoder = false;
            _pendingPreferredCodec = null;
        }

        // Apply the hardware encoder's default step now that its steps exist; a speed-first preset (Fast Proxy) applied before the
        // probe then re-applies its fastest step.
        _placingStep = true;
        try
        {
            if (selected == AppSettings.Current.DefaultVideoEncoder && EncoderPresets.Contains(AppSettings.Current.DefaultEncoderPreset))
                EncoderPreset = AppSettings.Current.DefaultEncoderPreset;
            if (_speedFirst && FastestStepOf(VideoEncoder) is { } fastest && EncoderPresets.Contains(fastest))
                EncoderPreset = fastest;
        }
        finally
        {
            _placingStep = false;
        }

        (_hardwareEncoderCount, _hardwareProblems) = (supported.Count, problems);
        ShowHardwareEncoderStatus();

        // GPU capabilities are now known for the selected encoder too.
        OnPropertyChanged(nameof(CanNvencTemporalAq));
        OnPropertyChanged(nameof(CanTenBit));
        GenerateCommand();

        // Defer the older-FFmpeg offer until after Frame 1 (this may run before it).
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (EncoderProber.NvencNeedsOlderFfmpeg)
                OfferFfmpegForOlderNvidiaDrivers();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Last probe result: working hardware encoder count and failure reasons for the rest.
    private int _hardwareEncoderCount;
    private List<string> _hardwareProblems = [];

    /// <summary>
    /// Export tab encoder status: working count and encoders that failed to start (usually outdated drivers). Absent-vendor problems
    /// are filtered once GPUs are enumerated (EncoderProber.ProblemsForCardsHere; not before Frame 1, rechecked by RecheckEarlyEncodersAsync).
    /// </summary>
    private void ShowHardwareEncoderStatus()
    {
        var text = _hardwareEncoderCount switch
        {
            0 => "No hardware encoders available on this system",
            1 => "1 hardware encoder available",
            _ => $"{_hardwareEncoderCount} hardware encoders available",
        };
        var problems = EncoderProber.ProblemsForCardsHere(_hardwareProblems);
        HardwareEncoderStatusText = problems.Count > 0 ? $"{text}. Not usable: {string.Join("; ", problems)}" : text;
    }

    /// <summary>
    /// NVENC failed because this FFmpeg requires a newer NVIDIA driver. Supported GPUs need a driver update; legacy ones
    /// (GTX 10 series and older) need the older FFmpeg build, which the next FFmpeg update installs.
    /// </summary>
    private void OfferFfmpegForOlderNvidiaDrivers()
    {
        if (DependencyUpdater.IsFfmpegOverridden)
        {
            HardwareEncoderStatusText += ". NVENC needs a newer NVIDIA driver, or an FFmpeg 8.1 build in place of the one set in Settings.";
            return;
        }

        if (AppSettings.Set(s => s.UseFfmpegForOlderNvidiaDrivers, (s, v) => s.UseFfmpegForOlderNvidiaDrivers = v, true))
        {
            AppLog.Write("NVENC needs a newer NVIDIA driver than is installed, so FFmpeg updates now install the build for older drivers.");
            _ = CheckDependencyUpdatesAsync();
        }

        HardwareEncoderStatusText += ". NVENC needs a newer NVIDIA driver, or the FFmpeg build for older drivers: update FFmpeg in Settings to install it.";
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task LoadSourceAsync()
    {
        // Project files (.hproj, or legacy projects detected by shape) open as projects.
        if (ProjectStore.IsProjectFile(SourcePath.Trim().Trim('"')))
        {
            await LoadProjectAsync(SourcePath.Trim().Trim('"'));
            return;
        }

        // URLs are fetched with yt-dlp (downloaded on first use).
        if (IsWebAddress(SourcePath) && !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return;

        await LoadSourceNowAsync();
    }

    /// <summary>Whether a source is a web address (http or https), which yt-dlp downloads, rather than a file.</summary>
    public static bool IsWebAddress(string source) =>
        Uri.TryCreate(source.Trim().Trim('"'), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Resolves a dropped URL to a local file via yt-dlp (or a previous download) for the timeline or a Squisher target; null on failure.
    /// </summary>
    public async Task<string?> DownloadLinkAsync(string url)
    {
        if (IsBusy || !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return null;

        string? path = null;
        await RunOperationAsync("Downloading a link", WorkLane.Interactive, loadsDocument: true, operation: async cancellationToken =>
        {
            IsProgressIndeterminate = true;
            StatusText = "Starting yt-dlp...";
            var progress = new Progress<string>(ReportStatus);
            var (resolution, subtitles, (sponsorBlock, markSponsorBlock), folder) = (DownloadResolution, DownloadSubtitles, AppSettings.Current.SponsorBlockRequest, AppSettings.Current.DownloadFolder);
            path = await Task.Run(() => YtDlpDownloader.DownloadAsync(url, resolution, subtitles, sponsorBlock, markSponsorBlock, folder, progress, cancellationToken), cancellationToken);
            return $"Downloaded {Path.GetFileName(path)}";
        });

        return path is not null && File.Exists(path) ? path : null;
    }

    private Task LoadSourceNowAsync() => RunOperationAsync("Loading the source", WorkLane.Interactive, loadsDocument: true, operation: async cancellationToken =>
    {
        var source = SourcePath.Trim().Trim('"');
        if (source.Length == 0)
            return null;

        // Loading something by hand ends the editing of a queued job.
        EditingJob = null;
        var (opened, message) = await LoadMediaAsync(source, knownLocalPath: null, applyAutomation: true, cancellationToken);

        // Default-vertical setting applies unless the New Project studio set an explicit frame.
        if (opened && AppSettings.Current.DefaultVerticalVideo && !UseVerticalResolution && !Document.ExplicitFrame)
            UseVerticalResolution = true;
        ForgetHistory();
        MarkSaved();
        return message;
    });

    /// <summary>Loads a file chosen outside the source box (dropped on the window, for instance).</summary>
    public void LoadFile(string path)
    {
        SourcePath = path;
        LoadSourceCommand.Execute(null);
    }

    /// <summary>As <see cref="LoadFile"/>, for what follows once it is open.</summary>
    public Task LoadFileAsync(string path)
    {
        SourcePath = path;
        return LoadSourceCommand.ExecuteAsync(null);
    }

    /// <summary>Loads a file and applies a Game Preset (drop onto a preset in the launch window).</summary>
    public async Task LoadFileWithStyleAsync(string path, string stylePath)
    {
        // URLs are downloaded first via yt-dlp (fetched itself if missing).
        if (IsWebAddress(path) && !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return;

        await LoadFileWithStyleNowAsync(path, stylePath);
    }

    private Task LoadFileWithStyleNowAsync(string path, string stylePath) => RunOperationAsync("Loading with a Game Preset", WorkLane.Interactive, loadsDocument: true, operation: async cancellationToken =>
    {
        SourcePath = path;
        EditingJob = null;
        var (loaded, message) = await LoadMediaAsync(path, knownLocalPath: null, applyAutomation: true, cancellationToken);
        if (!loaded)
            return message;

        if (ReadStyle(stylePath) is not { } style)
            return $"{message}. {StatusText}";

        ApplyStyle(style, layout: true, color: true, blur: true, subtitles: true);
        MarkSaved();
        return $"{message}. Game Preset \"{Path.GetFileNameWithoutExtension(stylePath)}\" applied.";
    });


    /// <summary>Sets the current source: downloads URLs, probes and starts playback.</summary>
    /// <param name="knownLocalPath">An existing download for this source, used instead of downloading again.</param>
    /// <param name="applyAutomation">Whether smart rules and the default preset apply.</param>
    /// <param name="place">Whether to place a clip (PlaceOpenedFile); false for projects, whose timeline is authoritative.</param>
    private async Task<(bool Loaded, string Message)> LoadMediaAsync(
        string source, string? knownLocalPath, bool applyAutomation, CancellationToken cancellationToken, bool place = true)
    {
        string localPath;
        if (File.Exists(source))
        {
            localPath = Path.GetFullPath(source);
        }
        else if (!string.IsNullOrWhiteSpace(knownLocalPath) && File.Exists(knownLocalPath))
        {
            localPath = knownLocalPath;
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (DependencyUpdater.GetMissing() is { Count: > 0 } missing)
                return (false, $"Downloading needs {string.Join(", ", missing)}. Install them from Settings (the gear button) first.");

            IsProgressIndeterminate = true;
            StatusText = "Starting yt-dlp...";
            // Create Progress on the UI thread so reports marshal back; the download and yt-dlp output parsing run on the pool.
            var progress = new Progress<string>(ReportStatus);
            var (resolution, subtitles, (sponsorBlock, markSponsorBlock), folder) = (DownloadResolution, DownloadSubtitles, AppSettings.Current.SponsorBlockRequest, AppSettings.Current.DownloadFolder);
            localPath = await Task.Run(
                () => YtDlpDownloader.DownloadAsync(source, resolution, subtitles, sponsorBlock, markSponsorBlock, folder, progress, cancellationToken),
                cancellationToken);
        }
        else
        {
            return (false, $"Source not found: {source}");
        }

        // Keep the file name out of crash reports (a probe error message may contain it).
        CrashReporter.KeepOut(localPath);
        // User-blocking probe: never queued behind Media Browser posters and strips.
        var mediaInfo = await MediaProbe.ProbeAsync(localPath, cancellationToken, urgent: true);
        var previous = LocalMediaPath;
        ResetForNewSource();
        LocalMediaPath = localPath;
        SetMediaInfo(mediaInfo);
        NoteHevcDecoder(mediaInfo?.Video);

        // Place a whole-file clip like a drop: at 0 on an empty Sequence, or replacing the previous file's clips (others stay).
        (_openedOver, _openedClip) = (null, null);
        if (place)
            PlaceOpenedFile(previous);

        // Crash report context: container, codecs, size, rate, duration; never the file identity.
        var mediaSummary = CrashSummaries.Media(localPath, mediaInfo, Layers.Count);
        CrashReporter.SetContext("media", mediaSummary);
        CrashReporter.Note("media", "opened: " + mediaSummary);
        DevFlightRecorder.Record("media", $"opened {localPath}: {mediaSummary}");

        // Fresh default output name per source so encodes never overwrite each other.
        DestinationPath = Path.Combine(
            GetDefaultOutputFolder(localPath), $"{Path.GetFileNameWithoutExtension(localPath)}_HandPeg.{Container}");

        var message = $"Loaded {localPath}";
        if (applyAutomation && AppSettings.Current.FindPresetFor(localPath, source) is { } presetName)
        {
            if (Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)) is { } preset)
            {
                // Apply the rule's full preset (including layout; the selector applies export settings only), then show it selected.
                // Targets the just-opened file's clip, not the Timeline selection.
                _templateTargetOnly = _openedClip;
                try
                {
                    ApplyPreset(preset);
                    _selectingAppliedPreset = true;
                    SelectedPreset = preset;
                }
                finally
                {
                    (_templateTargetOnly, _selectingAppliedPreset) = (null, false);
                }

                message += $". Preset \"{preset.Name}\" applied automatically.";
            }
            else
            {
                message += $". The automatic preset \"{presetName}\" no longer exists.";
            }
        }

        // Auto-mark a microphone/voice-named track as Voice unless the preset marked one; only for fresh loads (projects and queued
        // jobs restore without automation).
        if (applyAutomation && MarkVoiceTrack() is { } voice)
            message = $"{message.TrimEnd('.')}. {voice}";

        // Layers marked on a different-aspect video no longer align with this one.
        if (GetLayoutAspectWarning() is { } warning)
            message += $". {warning}";

        // Playback, I-frame index and hover previews are window-only; background instances skip them.
        if (!_isBackgroundWorker)
        {
            MediaLoaded?.Invoke(localPath);
            _ = ScanIFramesAsync(localPath);
            _ = GenerateSpriteSheetAsync(localPath);
            _ = GenerateWaveformsAsync(localPath);
        }
        return (true, message);
    }

    /// <summary>The source's own folder, or the Videos library when that folder is not a sensible place to write.</summary>
    private static string GetDefaultOutputFolder(string sourcePath)
    {
        var folder = Path.GetDirectoryName(sourcePath);
        return folder is not null && IsUserWritableFolder(folder)
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    private static bool IsUserWritableFolder(string folder)
    {
        string[] systemRoots =
        [
            Path.GetTempPath(),

            // Never default output into HandPeg's own cache/work folders.
            AppPaths.Temp,
            AppPaths.DataRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var root in systemRoots.Where(r => r.Length > 0))
        {
            var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // The only reliable test for write access is to try it.
        try
        {
            using (File.Create(Path.Combine(folder, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Indexes the source's I-frames without holding up playback or other operations.</summary>
    private async Task ScanIFramesAsync(string path)
    {
        _iFrameScanCancellation?.Cancel();
        var cancellation = _iFrameScanCancellation = new CancellationTokenSource();

        SetSourceIFrames([]);
        IFrameStatusText = "Indexing I-frames...";
        try
        {
            // Pool thread: ffprobe emits one line per I-frame (thousands), too much for the UI thread.
            var iFrames = await Task.Run(() => FfmpegRunner.GetIFramesAsync(path, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            SetSourceIFrames(iFrames);
            IFrameStatusText = iFrames.Count > 0
                ? $"{iFrames.Count} I-frames indexed"
                : "No I-frame index: cut points will not snap";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("I-frame indexing failed", ex);
            IFrameStatusText = $"I-frame indexing failed: {ex.Message}";
        }
        finally
        {
            // Dispose the CTS here, after its last token use, not at the cancel site.
            if (ReferenceEquals(_iFrameScanCancellation, cancellation))
                _iFrameScanCancellation = null;
            cancellation.Dispose();
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked; temp-folder cleanup will retry later.
        }
    }

    /// <summary>Picks up new tool paths after the settings window was saved.</summary>
    public void OnSettingsSaved()
    {
        OnPropertyChanged(nameof(EditLayersOnPlayer));
        OnPropertyChanged(nameof(SponsorBlockMode));

        // Sync Timeline toggles shared with Settings so the Snap button, Timeline Settings menu and blocks update (raised on change only).
        var saved = AppSettings.Current;
        ShowClipKeyframes = saved.ShowClipKeyframes;
        SnapClips = saved.SnapClips;
        SnapToBeats = saved.SnapToBeats;
        ShowBeatGrid = saved.ShowBeatGrid;

        // I-frame lines too, without the manual-toggle side effect (AutoToggleTimelineSnap): the saved settings already hold the snap.
        _resyncingShowIFrames = true;
        try
        {
            ShowIFrames = saved.ShowIFrames;
        }
        finally
        {
            _resyncingShowIFrames = false;
        }

        // Proxy settings may change height, format or location: re-resolve proxies.
        _proxies.Clear();
        PreviewCache.Proxies.QueueTrim();

        ApplyDisplaySettings();
        LoadAutomation();
        GenerateCommand();
        _ = ProbeHardwareEncodersAsync();
        _ = CheckDependencyUpdatesAsync();

        // Trim the cache to a reduced (or zero, when disabled) size.
        PreviewCache.Timeline.QueueTrim();
        WorkScheduler.ApplySettings();
        PreviewCache.Browser.QueueTrim();

        var missing = DependencyUpdater.GetMissing();
        StatusText = missing.Count > 0
            ? $"Settings saved. Missing {string.Join(", ", missing)}."
            : "Settings saved.";
    }

    /// <summary>Stops whatever is still running when the application closes.</summary>
    public void Shutdown()
    {
        ForgetProjectProxies(now: true);
        _operationCancellation?.Cancel();
        _iFrameScanCancellation?.Cancel();
        _shutdown.Cancel();
        _spriteCancellation?.Cancel();
        _waveformCancellation?.Cancel();

        // Background instances own only their work folder; shared folders belong to the window.
        if (_isBackgroundWorker)
        {
            DeleteFolder(_workFolder);
            return;
        }

        // Delete all session temp output (cut lists, thumbnail sheets, previews, queue files).
        SessionPaths.DeleteSession();
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task StartEncodeAsync()
    {
        // Ensure FFmpeg (and whisper plus model when captions need transcription) are installed.
        if (await RequireForCommandAsync("Start Encode"))
            await RunEncodeAsync();
    }

    private Task RunEncodeAsync() => RunOperationAsync("Exporting", WorkLane.Foreground, async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(CommandPreview))
            return "There is no command to run.";

        // Empty project: the command would reference a placeholder source FFmpeg cannot open.
        if (!HasSource && !(SequenceSeconds > 0) && !IsCommandManuallyEdited)
            return "There is nothing to export: open a video or a project first.";

        // Refuse impossible Copy exports before any prompt or work starts.
        if (CopyRefusal() is { } refused)
            return refused;

        // Warn again before a Copy export that would skip edits.
        if (CopyBypassWarning.Length > 0 && !IsCommandManuallyEdited && Confirm is not null
            && !Confirm("Stream Copy", $"{CopyBypassWarning}\n\nThe video is copied as it is: no clip's effects, crop or place, nothing set on the Color tab, and no resize reaches the output. To apply them, choose a video codec on the Export tab.", "Encode Anyway"))
        {
            return "Encode cancelled.";
        }

        if (!ResolveOverwrite())
            return "Encode cancelled: the existing file was left untouched.";

        // Read after the overwrite prompt, which may rename the target.
        var command = CommandPreview;

        // Commands or Extra options loaded from a project file (not edited this session) require confirmation.
        if (!ConfirmFileCommand(command))
            return "Not exported: the project's own command was not confirmed.";

        // Auto-captions: transcribe and write the caption file for this export.
        if (await PrepareCaptionsForEncodeAsync(command, cancellationToken) is { } captionProblem)
            return captionProblem;

        // Progress is measured against what the output should contain (see GetOutputDuration).
        var expected = TimeSpan.FromSeconds(Math.Max(GetOutputDuration(), 0.001));

        IsProgressIndeterminate = true;
        StatusText = "Starting FFmpeg...";
        SetTaskbarProgress(TaskbarItemProgressState.Normal);

        var progress = new Progress<FfmpegProgress>(report =>
        {
            if (!_acceptProgressReports)
                return;

            if (expected <= TimeSpan.Zero && report.InputDuration is { } inputDuration)
                expected = inputDuration;

            if (report.Position is { } position && expected > TimeSpan.Zero)
            {
                IsProgressIndeterminate = false;
                ProgressValue = Math.Clamp(position / expected * 100, 0, 100);
                SetTaskbarProgress(TaskbarItemProgressState.Normal, ProgressValue / 100);
            }

            if (report.Line.Length > 0)
                StatusText = report.Line;
        });

        try
        {
            // Crash report context: encoders, output kind and duration; never file names.
            CrashReporter.KeepOut(DestinationPath);
            var exportSummary = CrashSummaries.Command(command, expected);
            CrashReporter.SetContext("export", "running: " + exportSummary);
            CrashReporter.Note("export", "started: " + exportSummary);
            await RunExportAsync(command, ExportStateForGpu(), progress, cancellationToken);
            CrashReporter.Note("export", "finished");
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return _exportLeftOut.Length > 0 ? $"Encode complete. {_exportLeftOut}" : "Encode complete.";
        }
        catch (InvalidOperationException ex) when (
            ex is not FfmpegCommandException
            && AppSettings.Current.AutoFallbackToSoftware
            && HardwareFallback.TryRewrite(command, out _, out _, out _))
        {
            // Hardware encoder failed: retry once with the same codec's software encoder.
            HardwareFallback.TryRewrite(command, out var retry, out var hardware, out var software);
            AppLog.Write($"{hardware} failed and {software} is tried instead", ex);
            StatusText = $"Warning: {hardware} failed. Retrying with {software}...";
            Notifier.Show("Hardware encoder failed", $"{hardware} could not encode this video. HandPeg is trying again with {software}.");

            // Reflect the switch in the Export tab where that is possible; a hand-edited command is patched in place.
            if (!IsCommandManuallyEdited && VideoEncoders.FirstOrDefault(e => e.Name == software) is { } replacement)
            {
                PlaceSoftwareRetry(replacement, command);
                retry = CommandPreview;
            }
            else
            {
                CommandPreview = retry;
            }

            ProgressValue = 0;
            IsProgressIndeterminate = true;
            SetTaskbarProgress(TaskbarItemProgressState.Normal);
            CrashReporter.Note("export", $"{hardware} failed, retried with {software}");
            // Settings-driven retries stay on the GPU export path; patched hand-edited commands run via FFmpeg.
            await RunExportAsync(retry, ExportStateForGpu(), progress, cancellationToken);
            CrashReporter.Note("export", "finished");
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return $"Encode complete on {software}: the hardware encoder {hardware} failed, so the software encoder was used.";
        }
        finally
        {
            // Clear taskbar progress on completion, failure or cancel.
            SetTaskbarProgress(TaskbarItemProgressState.None);
            CrashReporter.SetContext("export", null);
        }
    });

    /// <summary>
    /// Applies a software fallback encoder on the Export tab as a programmatic pick (speed-first state preserved): fastest step when the
    /// export was speed-first or the failed command used its vendor's fastest step (HardwareFallback.HadFastestStep), else the slowest
    /// default. Mirrors HardwareFallback.TryRewrite so retries match whether or not the command was hand-edited.
    /// </summary>
    private void PlaceSoftwareRetry(EncoderOption replacement, string failedCommand)
    {
        _placingStep = true;
        try
        {
            VideoEncoder = replacement;
            if ((_speedFirst || HardwareFallback.HadFastestStep(failedCommand)) && FastestStepOf(replacement) is { } fastest && EncoderPresets.Contains(fastest))
                EncoderPreset = fastest;
        }
        finally
        {
            _placingStep = false;
        }
    }

    /// <summary>Prompts when the target exists; false on cancel. A rename retargets Save As and the command.</summary>
    private bool ResolveOverwrite()
    {
        var destination = DestinationPath.Trim().Trim('"');
        if (destination.Length == 0 || AskOverwrite is null || !File.Exists(destination)
            || !CommandPreview.Contains(destination, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var decision = AskOverwrite(destination);
        if (decision.Choice == OverwriteChoice.Cancel)
            return false;

        if (decision.Choice == OverwriteChoice.Rename)
        {
            // Generated commands follow Save As automatically; hand-edited ones are patched.
            var manualCommand = IsCommandManuallyEdited ? CommandPreview : null;
            DestinationPath = decision.NewPath;
            if (manualCommand is not null)
                CommandPreview = manualCommand.Replace(destination, decision.NewPath, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    /// <summary>
    /// Runs a single exclusive operation and maps its outcome (message, cancel, fault) to status text. Registered as WorkScheduler
    /// lane work: the progress chip shows its progress/status and its flyout cancels like the Cancel button.
    /// </summary>
    /// <param name="title">Progress chip label.</param>
    /// <param name="loadsDocument">True when the operation replaces the Sequence wholesale (open, load, Remove Dead Air), deferring the
    /// crash-recovery copy (<see cref="BusyWithDocument"/>); false for export, queue and captions.</param>
    private async Task RunOperationAsync(string title, WorkLane lane, Func<CancellationToken, Task<string?>> operation, bool loadsDocument = false)
    {
        if (IsBusy)
            return;

        _operationLoadsDocument = loadsDocument;

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _acceptProgressReports = true;
        IsBusy = true;
        CrashReporter.Note("op", "started: " + title);
        var ended = "done";

        string? outcome;
        Task<string?>? run = null;
        try
        {
            run = WorkScheduler.Run(new WorkSpec(title, lane), async work =>
            {
                // Mirror the operation's progress and status onto its chip.
                void Mirror(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName is nameof(ProgressValue) or nameof(IsProgressIndeterminate))
                        work.Report(IsProgressIndeterminate ? -1 : ProgressValue / 100);
                    else if (e.PropertyName == nameof(StatusText) && _acceptProgressReports)
                        work.Report(StatusText);
                }

                using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, work.Token);
                PropertyChanged += Mirror;
                try
                {
                    return await operation(either.Token);
                }
                finally
                {
                    PropertyChanged -= Mirror;
                }
            }, cancellation.Token);
            outcome = await run;
        }
        catch (OperationCanceledException) when (run is { IsCanceled: true } || cancellation.IsCancellationRequested)
        {
            outcome = "Cancelled.";
            ended = "cancelled";
        }
        catch (OperationCanceledException ex)
        {
            // Unrequested cancellation: an internal timeout (HTTP, tool) ended the operation.
            AppLog.Write($"{title} failed", ex);
            outcome = ex.InnerException is TimeoutException ? $"{title} timed out." : $"Error: {ex.Message}";
            ended = "failed: " + ex.GetType().Name;
        }
        catch (Exception ex)
        {
            AppLog.Write($"{title} failed", ex);
            outcome = $"Error: {ex.Message}";
            ended = "failed: " + ex.GetType().Name;
        }

        CrashReporter.Note("op", title + ": " + ended);

        // Progress callbacks are posted and may arrive after completion.
        _acceptProgressReports = false;
        _operationCancellation = null;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        IsBusy = false;
        _operationLoadsDocument = false;
        if (outcome is not null)
            StatusText = outcome;
    }

    // RunOperationAsync's loadsDocument flag for the current operation.
    private bool _operationLoadsDocument;

    /// <summary>
    /// True while an operation replaces the Sequence wholesale (a recovery copy now could be half-done); false for export, queue,
    /// captions or model downloads, during which the Sequence stays editable.
    /// </summary>
    public bool BusyWithDocument => IsBusy && _operationLoadsDocument;

    private void ReportStatus(string message)
    {
        if (_acceptProgressReports && !string.IsNullOrWhiteSpace(message))
            StatusText = message.Trim();
    }

    /// <summary>Clears per-source state when a different source is loaded.</summary>
    private void ResetForNewSource()
    {
        ForgetProjectProxies();
        PositionMs = 0;
        DurationMs = 0;

        // The opened file is placed whole at the timeline start once probed (PlaceOpenedFile); other clips stay, while bin and
        // markers belonged to the old file.
        SetBin(null);
        SetMarkers(null);
        ClearDocumentMarkers();
        _previewState = null;
    }
}
