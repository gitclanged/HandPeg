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
    // Whether the hardware encoders were put in the list before the window's first frame (TakeEarlyHardwareEncoders).
    private bool _hardwareEncodersEarly;

    /// <summary>
    /// The hardware encoders as an earlier probe found them for this FFmpeg and these drivers (read on another thread
    /// while the window was being made), put in the list before the window's first frame: the Export tab then opens with
    /// the encoder it keeps, where a hardware default used to show as Copy, with Copy's warning, until the probe
    /// answered. Not there yet, or none saved: the probe after the first frame does it, as before.
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
    /// The encoders taken before the first frame were matched on FFmpeg alone (EncoderProber.LoadSavedResultEarly): the
    /// cards and drivers are compared now, away from this thread, and a change since the probe that found them probes again.
    /// </summary>
    private async Task RecheckEarlyEncodersAsync()
    {
        // The cards are listed there too (off this thread, after the first frame): a probe problem of a vendor whose card is not in this
        // PC ("h264_nvenc: Cannot load nvcuda.dll" on an AMD machine, from an encoder-probe.json of before 5.4B) is then left unsaid.
        var same = await Task.Run(() =>
        {
            var still = EncoderProber.CardsStillMatch();
            _ = GpuAdapters.All;
            return still;
        });
        if (!same)
            await ProbeHardwareEncodersAsync();
        else
            ShowHardwareEncoderStatus();
    }

    /// <summary>Tests the GPU encoders in the background and offers the working ones in the Export tab.</summary>
    private async Task ProbeHardwareEncodersAsync()
    {
        // A newer probe (after an install or a settings change) makes the result of an older one stale.
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
            // Found before for this FFmpeg and these drivers: not tested again (see EncoderProber.LoadSavedResult).
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
        // Rebuild the hardware part of the list, keeping the selection where it still exists. The first time
        // round, a hardware encoder set as the default is selected, unless another was chosen in the meantime.
        var untouched = VideoEncoder.Family is EncoderFamily.Copy || VideoEncoder == DefaultVideoEncoder;
        var selected = _pendingDefaultEncoder is { } pending && untouched ? pending : VideoEncoder.Name;
        _pendingDefaultEncoder = null;
        foreach (var stale in VideoEncoders.Where(e => e.IsHardware).ToList())
            VideoEncoders.Remove(stale);
        foreach (var name in supported)
            VideoEncoders.Add(EncoderOption.FromHardwareName(name));
        _hardwareEncodersListed = true;
        _resolvingEncoder = true;
        try
        {
            VideoEncoder = VideoEncoders.FirstOrDefault(e => e.Name == selected) ?? DefaultVideoEncoder;

            // A built-in applied before this list was there fell back to software (5.4C): its codec's hardware encoder now, once, at its
            // slowest step (UpdateEncoderPresets starts a hardware family there).
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

        // The default encoder preset is one of the hardware encoder's own steps, which exist now that it does. A speed-first preset
        // (5.4D, Fast Proxy) applied before the probe keeps its fastest step on the encoder it resolves to now, after that default.
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

        // What the card can do is known now, for the encoder that is selected as for the others.
        OnPropertyChanged(nameof(CanNvencTemporalAq));
        OnPropertyChanged(nameof(CanTenBit));
        GenerateCommand();

        // Offering an older FFmpeg is not part of putting the list up: it waits for the window to be drawn (this may run
        // before its first frame).
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (EncoderProber.NvencNeedsOlderFfmpeg)
                OfferFfmpegForOlderNvidiaDrivers();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // What the last probe (or the result saved by one) found: how many hardware encoders work, and why the others did not start.
    private int _hardwareEncoderCount;
    private List<string> _hardwareProblems = [];

    /// <summary>
    /// The detected encoders' line on the Export tab: how many work, and an encoder FFmpeg has but could not start (usually a driver
    /// that needs updating). A problem of a vendor whose card is not in this PC is left out once the cards are listed (5.4B,
    /// EncoderProber.ProblemsForCardsHere: never listed for it before the first frame, Law 1; RecheckEarlyEncodersAsync says it again after).
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
    /// NVENC did not start because this FFmpeg asks for a newer NVIDIA driver than the one installed. A card that
    /// still gets drivers needs a driver update; one that does not (the GTX 10 series and older) needs the older
    /// FFmpeg build, which the next FFmpeg update then installs.
    /// </summary>
    private void OfferFfmpegForOlderNvidiaDrivers()
    {
        if (DependencyUpdater.IsFfmpegOverridden)
        {
            HardwareEncoderStatusText += ". NVENC needs a newer NVIDIA driver, or an FFmpeg 8.1 build in place of the one set in Settings.";
            return;
        }

        var settings = AppSettings.Current;
        if (!settings.UseFfmpegForOlderNvidiaDrivers)
        {
            settings.UseFfmpegForOlderNvidiaDrivers = true;
            try
            {
                settings.SaveAsCurrent();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The choice still holds for this session.
            }

            AppLog.Write("NVENC needs a newer NVIDIA driver than is installed, so FFmpeg updates now install the build for older drivers.");
            _ = CheckDependencyUpdatesAsync();
        }

        HardwareEncoderStatusText += ". NVENC needs a newer NVIDIA driver, or the FFmpeg build for older drivers: update FFmpeg in Settings to install it.";
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task LoadSourceAsync()
    {
        // A project file (a .hproj, or a HandPeg 1.x project recognised by its shape), typed in or dropped, opens the project.
        if (ProjectStore.IsProjectFile(SourcePath.Trim().Trim('"')))
        {
            await LoadProjectAsync(SourcePath.Trim().Trim('"'));
            return;
        }

        // A web address is fetched by yt-dlp, which is downloaded the first time one is loaded.
        if (IsWebAddress(SourcePath) && !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return;

        await LoadSourceNowAsync();
    }

    /// <summary>Whether a source is a web address (http or https), which yt-dlp downloads, rather than a file.</summary>
    public static bool IsWebAddress(string source) =>
        Uri.TryCreate(source.Trim().Trim('"'), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Downloads a web address with yt-dlp (or finds the file downloaded for it before), for a link dropped where a
    /// file is wanted: on the timeline, or on a Squisher target. Returns the file; null when nothing came of it.
    /// </summary>
    public async Task<string?> DownloadLinkAsync(string url)
    {
        if (IsBusy || !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return null;

        string? path = null;
        await RunOperationAsync("Downloading a link", WorkLane.Interactive, async cancellationToken =>
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

    private Task LoadSourceNowAsync() => RunOperationAsync("Loading the source", WorkLane.Interactive, async cancellationToken =>
    {
        var source = SourcePath.Trim().Trim('"');
        if (source.Length == 0)
            return null;

        // Loading something by hand ends the editing of a queued job.
        EditingJob = null;
        var (opened, message) = await LoadMediaAsync(source, knownLocalPath: null, applyAutomation: true, cancellationToken);

        // Settings: every video starts as a vertical one (5.4A: unless the New Project studio chose the frame on purpose).
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

    /// <summary>
    /// Loads a file and gives it a Game Preset: what dropping a video on a Game Preset in the launch window does.
    /// </summary>
    public async Task LoadFileWithStyleAsync(string path, string stylePath)
    {
        // A link dropped on a Game Preset is downloaded first, with yt-dlp, which may itself have to be downloaded.
        if (IsWebAddress(path) && !await RequireAsync("Downloading a video", [DependencyUpdater.YtDlp, DependencyUpdater.Ffmpeg], []))
            return;

        await LoadFileWithStyleNowAsync(path, stylePath);
    }

    private Task LoadFileWithStyleNowAsync(string path, string stylePath) => RunOperationAsync("Loading with a Game Preset", WorkLane.Interactive, async cancellationToken =>
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


    /// <summary>
    /// Makes a source the current one: downloads it if it is a URL, inspects it and starts playback.
    /// </summary>
    /// <param name="knownLocalPath">A file already downloaded for this source, used instead of downloading again.</param>
    /// <param name="applyAutomation">Whether the smart rules and the default preset from the settings apply.</param>
    /// <param name="place">Whether a clip of it goes on the timeline (PlaceOpenedFile); not for a project, whose timeline is its own.</param>
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
            // The progress object is made here, on the UI thread, so its reports come back to it;
            // the download itself, and the handling of yt-dlp's output, run on the thread pool.
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

        // A crash report never names the file, not even on its own (a failed probe's message may).
        CrashReporter.KeepOut(localPath);
        // What the user is waiting on: never queued behind the media browser's posters and strips (4.3E).
        var mediaInfo = await MediaProbe.ProbeAsync(localPath, cancellationToken, urgent: true);
        var previous = LocalMediaPath;
        ResetForNewSource();
        LocalMediaPath = localPath;
        SetMediaInfo(mediaInfo);
        NoteHevcDecoder(mediaInfo?.Video);

        // A clip of the whole file on the timeline, as a dropped file is made (5.2-AuditB): on an empty Sequence at its start, or
        // where the clips of the file opened before were (the other clips stay).
        (_openedOver, _openedClip) = (null, null);
        if (place)
            PlaceOpenedFile(previous);

        // For a crash report: what kind of file this is (container, codecs, size, rate, length), never which.
        var mediaSummary = CrashSummaries.Media(localPath, mediaInfo, Layers.Count);
        CrashReporter.SetContext("media", mediaSummary);
        CrashReporter.Note("media", "opened: " + mediaSummary);
        DevFlightRecorder.Record("media", $"opened {localPath}: {mediaSummary}");

        // Every new source gets its own default name, so one encode never lands on top of the previous one.
        DestinationPath = Path.Combine(
            GetDefaultOutputFolder(localPath), $"{Path.GetFileNameWithoutExtension(localPath)}_HandPeg.{Container}");

        var message = $"Loaded {localPath}";
        if (applyAutomation && AppSettings.Current.FindPresetFor(localPath, source) is { } presetName)
        {
            if (Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)) is { } preset)
            {
                // Applied whole (5.4B: the rule's preset keeps everything it holds, its layout too; the selector applies an Export
                // Preset's export settings only), then shown as the one selected. It goes on the clip of the file just opened, not on
                // what is selected on the Timeline.
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

        // A track named for a microphone or a voice is the Voice track (4.3E), unless the preset above marked one: only for a
        // file loaded anew (a project or a queued job is restored without automation, and keeps its tracks as saved).
        if (applyAutomation && MarkVoiceTrack() is { } voice)
            message = $"{message.TrimEnd('.')}. {voice}";

        // Layers marked on a video of another shape will not sit on the same things in this one.
        if (GetLayoutAspectWarning() is { } warning)
            message += $". {warning}";

        // Playback, the I-frame index and hover previews are for the window; a background instance needs none of them.
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

            // HandPeg's own working folders: a video downloaded into the cache is not saved back into it.
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
            // On the thread pool: ffprobe prints a line per I-frame, and reading thousands of them
            // should not happen on the UI thread.
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
            IFrameStatusText = $"I-frame indexing failed: {ex.Message}";
        }
        finally
        {
            // Released here, once nothing uses its token any more, rather than by whoever cancels it.
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
            // Still in use; it is in the temp folder and will be cleaned up another time.
        }
    }

    /// <summary>Picks up new tool paths after the settings window was saved.</summary>
    public void OnSettingsSaved()
    {
        OnPropertyChanged(nameof(EditLayersOnPlayer));
        OnPropertyChanged(nameof(SponsorBlockMode));

        // The Timeline's switches the Settings window has as well (5.3): this view model's copies of them follow what was saved, so the
        // Snap button, the Timeline Settings menu and the blocks show it (each raised only when it changed).
        var saved = AppSettings.Current;
        ShowClipKeyframes = saved.ShowClipKeyframes;
        SnapClips = saved.SnapClips;
        SnapToBeats = saved.SnapToBeats;
        ShowBeatGrid = saved.ShowBeatGrid;

        // The I-frame lines as well, without what switching them by hand does (AutoToggleTimelineSnap turning the timeline thumb's snap
        // to I-frames with them): what was saved already holds the snap the user left in the window.
        _resyncingShowIFrames = true;
        try
        {
            ShowIFrames = saved.ShowIFrames;
        }
        finally
        {
            _resyncingShowIFrames = false;
        }

        // The proxy settings may name another height, format or place: proxies are looked for again under them.
        _proxies.Clear();
        PreviewCache.Proxies.QueueTrim();

        ApplyDisplaySettings();
        LoadAutomation();
        GenerateCommand();
        _ = ProbeHardwareEncodersAsync();
        _ = CheckDependencyUpdatesAsync();

        // A cache made smaller, or switched off (which empties it), is brought to its new size.
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

        // A background instance only owns its own working folder; the shared ones belong to the window.
        if (_isBackgroundWorker)
        {
            DeleteFolder(_workFolder);
            return;
        }

        // Cut lists, thumbnail sheets, previews, queue files: everything this session wrote, in one go.
        SessionPaths.DeleteSession();
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task StartEncodeAsync()
    {
        // FFmpeg, and for captions that are still to be heard whisper and its model: fetched now if they never were.
        if (await RequireForCommandAsync("Start Encode"))
            await RunEncodeAsync();
    }

    private Task RunEncodeAsync() => RunOperationAsync("Exporting", WorkLane.Foreground, async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(CommandPreview))
            return "There is no command to run.";

        // Nothing open and nothing on the timeline: the command names a stand-in for a source, which FFmpeg cannot open.
        if (!HasSource && !(SequenceSeconds > 0) && !IsCommandManuallyEdited)
            return "There is nothing to export: open a video or a project first.";

        // A Copy export that cannot cut is refused here, before anything is asked or started (5.2-Arm64AndNpu, the user's call).
        if (CopyRefusal() is { } refused)
            return refused;

        // Copy keeps the picture as it is: said once more before the time is spent, when there is work it would skip.
        if (CopyBypassWarning.Length > 0 && !IsCommandManuallyEdited && Confirm is not null
            && !Confirm("Stream Copy", $"{CopyBypassWarning}\n\nThe video is copied as it is: no clip's effects, crop or place, nothing set on the Filters tab, and no resize reaches the output. To apply them, choose a video codec on the Export tab.", "Encode Anyway"))
        {
            return "Encode cancelled.";
        }

        if (!ResolveOverwrite())
            return "Encode cancelled: the existing file was left untouched.";

        // Read after the overwrite question: renaming the target changes the command.
        var command = CommandPreview;

        // Auto-captions: listen, transcribe and write the subtitle file the command is about to draw.
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
            // For a crash report: the encoders, the output's kind and length, never the files (the output's name included).
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
            // The hardware encoder let us down: one more attempt on the software encoder for the same codec.
            HardwareFallback.TryRewrite(command, out var retry, out var hardware, out var software);
            AppLog.Write($"{hardware} failed and {software} is tried instead: {ex.Message}");
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
            // The retry is the GPU's too where the settings make it (the encoder switched on the Export tab); a patched command is FFmpeg's.
            await RunExportAsync(retry, ExportStateForGpu(), progress, cancellationToken);
            CrashReporter.Note("export", "finished");
            NotifyFinished("Encode complete", Path.GetFileName(DestinationPath.Trim().Trim('"')));
            return $"Encode complete on {software}: the hardware encoder {hardware} failed, so the software encoder was used.";
        }
        finally
        {
            // Finished, failed or cancelled: the taskbar button goes back to plain.
            SetTaskbarProgress(TaskbarItemProgressState.None);
            CrashReporter.SetContext("export", null);
        }
    });

    /// <summary>
    /// A hardware failure's software retry on the Export tab (5.4D): <paramref name="replacement"/> put in place as HandPeg's own pick, not
    /// one by hand (a speed-first state holds), at its fastest step when the export was speed-first or the failed command carried its
    /// vendor's fastest step (HardwareFallback.HadFastestStep: a step picked by hand too), else at its own default, the slowest. The same
    /// rule as HardwareFallback.TryRewrite (5.4D review): the same failure retries at the same step, the command hand-edited or not.
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

    /// <summary>
    /// Asks what to do when the target file already exists. Returns false when the user cancelled;
    /// after a rename, Save As and the command point at the new name.
    /// </summary>
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
            // A generated command follows Save As by itself; a hand-edited one has to be patched.
            var manualCommand = IsCommandManuallyEdited ? CommandPreview : null;
            DestinationPath = decision.NewPath;
            if (manualCommand is not null)
                CommandPreview = manualCommand.Replace(destination, decision.NewPath, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    /// <summary>
    /// Runs one operation at a time, and turns its outcome (returned message, cancellation or failure) into status text.
    /// It is a piece of work in its lane (see WorkScheduler): shown by the progress chip with the progress and the
    /// status it reports here, and cancelled from the chip's flyout as by the Cancel button.
    /// </summary>
    /// <param name="title">What the progress chip calls it.</param>
    private async Task RunOperationAsync(string title, WorkLane lane, Func<CancellationToken, Task<string?>> operation)
    {
        if (IsBusy)
            return;

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _acceptProgressReports = true;
        IsBusy = true;
        CrashReporter.Note("op", "started: " + title);
        var ended = "done";

        string? outcome;
        try
        {
            outcome = await WorkScheduler.Run(new WorkSpec(title, lane), async work =>
            {
                // What the operation says of itself (the progress bar's value, the status line) is what its chip says.
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
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled.";
            ended = "cancelled";
        }
        catch (Exception ex)
        {
            outcome = $"Error: {ex.Message}";
            ended = "failed: " + ex.GetType().Name;
        }

        CrashReporter.Note("op", title + ": " + ended);

        // Progress callbacks are posted, so some can still arrive after the operation has ended.
        _acceptProgressReports = false;
        _operationCancellation = null;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        IsBusy = false;
        if (outcome is not null)
            StatusText = outcome;
    }

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

        // A file opened goes on the timeline where any does: at the beginning of the timeline, whole (PlaceOpenedFile, once it has
        // been looked at). The other clips stay; the bin and the markers were the old file's.
        SetBin(null);
        SetMarkers(null);
        Document.Markers = [];
        _previewState = null;
    }
}
