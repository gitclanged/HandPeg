using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Main editor window: startup sequencing, close/exit flow, global keys, source loading and view-model change handlers.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    // Heat-lamp controller: created at idle after Frame 1 (null until then).
    private Shell.HeatLampController? _heatLamp;

    // Live Encoded Preview controller (its toggle is created with the bar's first layout).
    private readonly Shell.LoopbackPreviewController _loopback;

    // Player HUD and Fullscreen Preview controller (created on first use, after Frame 1).
    private readonly Shell.FullscreenController _fullscreen;

    // Drag payload classification, drop routing, and the empty-window drop overlay.
    private readonly Shell.DropRouter _drops;
    private bool _isScrubbing;
    private bool _applyingMagnet;
    private bool _updatingFromPlayer;

    /// <summary>
    /// Background update check (Settings: Check for HandPeg Updates on Startup); offers the release in the status bar, to download or
    /// (with background download enabled) already downloaded. Fire-and-forget; failures (offline, no releases) are only logged.
    /// </summary>
    private async Task CheckForHandPegUpdateAsync()
    {
        if (!AppSettings.Current.CheckAppUpdatesOnStartup)
            return;

        if (await AppUpdater.CheckAtStartupAsync() is { } update)
            ShowUpdateNotice(update.Version, update.Downloaded);
    }

    // Offered update: version and whether it is already downloaded (the button then restarts into it).
    private (string Version, bool Downloaded)? _updateOffered;

    private void ShowUpdateNotice(string version, bool downloaded)
    {
        _updateOffered = (version, downloaded);
        UpdateNoticeText.Text = downloaded ? $"HandPeg {version} is ready." : $"HandPeg {version} is available.";
        (UpdateActionButton.Content, UpdateActionButton.IsEnabled, UpdateLaterButton.IsEnabled) = (downloaded ? "Restart to update" : "Update", true, true);
        UpdateActionButton.ToolTip = downloaded
            ? "Closes HandPeg, puts the new version in and starts it again. Without this, the update goes in the next time HandPeg is closed."
            : "Downloads the new version (the progress shows in the title bar), then closes HandPeg, puts it in and starts it again.";
        UpdateLaterButton.ToolTip = downloaded ? "Hide this. The update goes in when HandPeg is closed." : "Hide this. Nothing is downloaded or put in.";
        UpdateNotice.Visibility = Visibility.Visible;
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e) => Faults.Forget(UpdateAction_ClickAsync(sender, e), nameof(UpdateAction_Click));

    private async Task UpdateAction_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_updateOffered is not { } offered)
            return;

        if (offered.Downloaded)
        {
            RestartToUpdate();
            return;
        }

        // Download with chip progress, then restart into it as Restart to Update does.
        (UpdateActionButton.IsEnabled, UpdateLaterButton.IsEnabled) = (false, false);
        UpdateNoticeText.Text = $"Downloading HandPeg {offered.Version}...";
        bool ready;
        try
        {
            ready = await WorkScheduler.Run(new WorkSpec($"Downloading HandPeg {offered.Version}", WorkLane.Interactive) { Key = "app-update" },
                work => AppUpdater.DownloadFoundAsync(fraction => work.Report(fraction, $"{fraction:P0}"), work.Token));
        }
        catch (OperationCanceledException)
        {
            ShowUpdateNotice(offered.Version, downloaded: false);
            _viewModel.StatusText = "The update download was cancelled.";
            return;
        }

        if (!ready)
        {
            ShowUpdateNotice(offered.Version, downloaded: false);
            _viewModel.StatusText = "The update could not be downloaded (see the log). Press Update to try again.";
            return;
        }

        ShowUpdateNotice(offered.Version, downloaded: true);
        RestartToUpdate();
    }

    private void RestartToUpdate()
    {
        if (_viewModel.IsBusy)
        {
            _viewModel.StatusText = "An operation is still running. The update will go in when HandPeg is closed.";
            return;
        }

        // The updater waits for this process to exit, so close through the normal path.
        if (AppUpdater.BeginRestartIntoUpdate())
            Close();
        else
            _viewModel.StatusText = "The update could not be started. It will be tried again when HandPeg is closed.";
    }

    // Deferred: the update is applied when the application exits.
    private void DismissUpdateNotice_Click(object sender, RoutedEventArgs e) => UpdateNotice.Visibility = Visibility.Collapsed;

    public MainWindow()
    {
        StartupTrace.Mark("MainWindow: view model made, InitializeComponent");
        var building = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        _drops = new Shell.DropRouter(_viewModel, this, new Shell.DropOverlayParts(DropOverlay, DropPresetList, DropSquishList, DropPresetTitle,
            DropPresetSection, DropSquishSection, VideoView, ClearDropMark), () => _gpu);

        // Apply the app UI scale before the first layout (native monitor pixels unless following Windows scaling). The window keeps
        // its OS-assigned size; content is laid out at the scale.
        UiScale.SetKeepsItsSize(this, true);
        UiScale.BeforeFirstLayout(this);
        var built = building.ElapsedMilliseconds;
        StartupTrace.Mark("MainWindow: InitializeComponent done");
        DataContext = _viewModel;

        // handledEventsToo: the slider handles presses beside its thumb (seeking there) and would otherwise hide them from the window.
        ApplyStartupWindowSize();
        // handledEventsToo: the window's drag handlers mark these handled (to refuse drops over text boxes), hiding them from the overlay.
        AddHandler(PreviewDragEnterEvent, new DragEventHandler((_, e) =>
        {
            // A drag entered: start the player now so it is ready when the drop lands.
            WarmUpPlayer();
            _drops.ShowDropOverlay(e);
        }), handledEventsToo: true);
        AddHandler(PreviewDragOverEvent, new DragEventHandler((_, e) => _drops.ShowDropOverlay(e)), handledEventsToo: true);

        // Typing a path or URL also pre-starts the player.
        SourcePaneBox.GotKeyboardFocus += (_, _) => WarmUpPlayer();

        AddHandler(PreviewDragLeaveEvent, new DragEventHandler((_, _) => _drops.LeaveDropOverlay()), handledEventsToo: true);
        AddHandler(PreviewDropEvent, new DragEventHandler((_, _) => Dispatcher.BeginInvoke(_drops.HideDropOverlay)), handledEventsToo: true);

        TransportRow.Fitting = FitTransport;
        _addMediaHover = new HoverFlyout(AddMediaHost, AddMediaFlyout, AddMediaFlyoutPanel) { HoldsWhileFocused = true, Opening = () => PlaceHeaderFlyout(AddMediaFlyout, AddMediaHost) };
        _settingsHover = new HoverFlyout(SettingsHost, SettingsFlyout, SettingsFlyoutPanel)
        {
            Opening = () =>
            {
                _viewModel.RefreshAutosave();
                PlaceHeaderFlyout(SettingsFlyout, SettingsHost);
            },
        };

        // Header Layout button: hover or click opens the Layout Selector (live layouts and themes); Escape, pointer leave or window
        // deactivation close it. Hover does nothing during a tab switch (a preview then would be restored over the new tab's layout).
        _layoutHover = new HoverFlyout([LayoutButton], LayoutSelector, LayoutSelectorPanel)
        {
            Opening = OpeningLayoutSelector,
            HoldsWhileFocused = true,
            CanOpen = () => !_switchingTab,
        };
        Deactivated += (_, _) => CloseLayoutSelectorOnDeactivated();
        _autosave.Tick += Autosave_Tick;
        _autosave.Start();

        // Every save of the active project deletes its crash-recovery file immediately (MainWindow.TabCache.cs).
        _viewModel.ProjectSaved += ViewModel_ProjectSaved;

        // Save key bindings (XAML) invoke view-model commands, which call back here for UI.
        _viewModel.SaveRequested += choosePlace =>
        {
            if (choosePlace)
                SaveProjectAs();
            else
                SaveProject();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SyncMasterTimeline))
                UpdateMasterStretch();
        };
        InitializeDocking();
        InitializeTitleBar();
        StartupTrace.Mark("MainWindow: docking laid out");

        // Player surface and drop hint in their Frame 1 state (matching the XAML defaults).
        UpdateVideoVisibility();
        Application.Current.Resources["GlobalScrollBarThickness"] = Math.Clamp(AppSettings.Current.ScrollbarThickness, 4, 16);
        TimelineSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(TimelineSlider_PreviewMouseLeftButtonDown), handledEventsToo: true);

        // The first-run window may have been created first, and WPF makes the first window MainWindow.
        Application.Current.MainWindow = this;

        // Trim preview caches to their size limits (background, a few seconds after start).
        PreviewCache.Timeline.QueueTrim();
        PreviewCache.Browser.QueueTrim();

        _viewModel.MarkersChanged += OnMarkersChanged;
        InitializePlaybackBar();
        _fullscreen = new Shell.FullscreenController(new Shell.FullscreenHost(this, _viewModel, VideoView, VideoBorder, PlayerHud, ExplodedToggle,
            () => CanToggleExplodedView, () => IsScrubHeld, () => _switchingTab, HookSideKeys, UnhookSideKeys, PlayPause_Click, ScrubPlayheadTo,
            ms =>
            {
                EndScrub();
                if (!PlayerAt((long)ms))
                    PlayerSeek((long)ms);
            }, RunGuarded));
        InitializePlayerLayout();
        HookLoupe();
        _loopback = new Shell.LoopbackPreviewController(_viewModel, this, ExportQualityPreviewBarSlot, TransportRow, TransportCenter, ExplodedToggle,
            PlayerOverlay, VideoView, () => _gpu, () => _explodedUi);
        _viewModel.SessionCleared += OnSessionCleared;
        InitializeProjectWorkspace();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        // Release the previous file's Sequence Overview Scrubber images before the new open requests its own.
        _viewModel.MediaLoaded += _ => ClearProgramStrip();
        _viewModel.MediaLoaded += PlayMedia;

        // Unsaved-change tracking starts at the first input after load/save: key, click, wheel or drop in any HandPeg window (popups
        // and menus bubble to their window).
        RoutedEventHandler touched = (_, _) => _viewModel.NoteUserInput();
        foreach (var input in new[] { PreviewMouseDownEvent, PreviewKeyDownEvent, PreviewMouseWheelEvent, DragDrop.PreviewDropEvent })
            EventManager.RegisterClassHandler(typeof(Window), input, touched, handledEventsToo: true);
        InitializeDevFlight();

        // Media browser: open and insert-at-playhead handlers (MainWindow.MediaBrowser.cs).
        InitializeMediaBrowser();
        InitializeClipLibrary();
        EffectsView.Attach(_viewModel);
        EffectsView.EditMaskRequested += layer => OpenLayerTool(layer, LayerTool.Mask);

        // Studio Audio Suite: master meters and fader, audio card dialog, beat grid.
        InitializeAudioSuite();

        // Wire the timeline surface, ruler, toolbar and inspector to the view model and this window.
        InitializeUnifiedTimeline();
        _viewModel.AskOverwrite = AskOverwrite;
        _viewModel.Confirm = (title, message, confirm) => new ConfirmDialog(title, message, confirm) { Owner = this }.ShowDialog() == true;

        // Legacy-format projects opened here are migrated in place (test harnesses open projects read-only).
        _viewModel.SavesConvertedProjects = true;

        // Confirmations with "Remember my choice" persist the remembered answer.
        _viewModel.ConfirmOrRemember = (title, message, confirm, decline) =>
        {
            var dialog = new ConfirmDialog(title, message, confirm) { Owner = this, RememberText = "Remember my choice (don't ask again)", CancelText = decline };
            return (dialog.ShowDialog() == true, dialog.Remember);
        };

        // Missing tools are offered for download on first need.
        _viewModel.DependenciesNeeded = request => Task.FromResult(new DependencyPromptWindow(request) { Owner = this }.ShowDialog() == true);
        _viewModel.Choose = (title, message, first, second) =>
        {
            var dialog = new ConfirmDialog(title, message, first, second) { Owner = this };
            return dialog.ShowDialog() != true ? 0 : dialog.ChoseOther ? 2 : 1;
        };

        // Layer add/remove refreshes the canvas.
        _viewModel.Layers.CollectionChanged += (_, _) => RedrawLayout();

        PreviewKeyDown += MainWindow_PreviewKeyDown;

        // Class handlers across all editor windows: mouse buttons, text-box focus release on press, and held keys (X-Ray, Caps Lock)
        // including PreviewKeyUp (MainWindow.Keybinds.cs).
        InitializeKeybinds();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += (_, _) => _ = CheckForHandPegUpdateAsync();

        // While HandPeg is foreground, toast-notification text goes to the status line instead.
        Notifier.Said += (title, message) => Dispatcher.BeginInvoke(() => _viewModel.StatusText = $"{title}: {message}");

        // Re-render the player on every preview-state change (MainViewModel.InvalidatePreviewState).
        _viewModel.PreviewStateInvalidated += ScheduleGpuState;
        _viewModel.IsTimelineDragHeld = () => TimelineDragHeld;

        // Input probes for the view model's undo grouping (it reads no WPF input): left button held, text box typing.
        _viewModel.IsPointerHeld = () => Dispatcher.CheckAccess() && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
        _viewModel.IsTypingInTextBox = () => Dispatcher.CheckAccess() && System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        _viewModel.IsAltHeld = () => Dispatcher.CheckAccess() && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
        _viewModel.TaskbarProgress = new Shell.WindowTaskbarProgress(this);
        _viewModel.TimelineSelection = SelectedGroup;

        // Timeline rows rebuild on Layers changes, except look-only changes (e.g. a 30 Hz effect drag) that no row displays.
        _viewModel.PreviewStateInvalidated += ScheduleClipRedrawUnlessLook;
        _viewModel.Layers.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.TimelineChanged += ScheduleClipRedrawUnlessLook;
        _viewModel.TimelineChanged += DrawKeyTimeline;

        // Keyframes pane rows: per-property key, move and delete.
        KeyLanes.LaneSelected += lane => _viewModel.SelectedKeyLane = lane.Target;
        KeyLanes.KeyToggleRequested += lane => _viewModel.ToggleLaneKey(_viewModel.ActiveKeyLayer, lane.Target);
        KeyLanes.KeyMoved += (lane, from, to) => _viewModel.MoveLaneKey(_viewModel.ActiveKeyLayer, lane.Target, from, to);
        KeyLanes.DeleteRequested += lane => _viewModel.DeleteLaneKey(_viewModel.ActiveKeyLayer, lane.Target);

        // Multi-keyframe selection: move, delete, copy and paste, each one Undo step.
        KeyLanes.SelectionChanged += keys => _viewModel.SelectedLaneKeys = [.. keys];
        KeyLanes.KeysMoved += (keys, delta) => _viewModel.MoveLaneKeys(_viewModel.ActiveKeyLayer, keys, delta);
        KeyLanes.DeleteSelectedRequested += keys => _viewModel.DeleteLaneKeys(_viewModel.ActiveKeyLayer, keys);
        KeyLanes.CopyRequested += keys => _viewModel.CopyLaneKeys(_viewModel.ActiveKeyLayer, keys);
        KeyLanes.PasteRequested += () => _viewModel.PasteLaneKeys(_viewModel.ActiveKeyLayer);
        KeyLanes.CanPaste = () => _viewModel.HasCopiedKeys;
        KeyLanes.SeekRequested += seconds => _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, Math.Max(_viewModel.DurationMs, 0));
        KeyLanes.PanRequested += PanTimeline;
        KeyLanes.ZoomRequested += ZoomTimeline;
        Loaded += (_, _) => ApplyPaneLayout();
        Loaded += (_, _) => UpdateBottomBar();
        _viewModel.LayerLoaded += RedrawLayout;
        _viewModel.LayerVisibilityChanged += RedrawLayout;

        // Edit layout may be on at open (the default): show the pane silently (the title-bar status stays empty until user input).
        Loaded += (_, _) => UpdateLayoutPane(announce: false);
        SizeChanged += (_, _) => UpdateBottomBar();

        // Double-click on any slider or number box resets it (to the style preset's value when one exists).
        NumericInput.ResetValue = _viewModel.GetResetValue;
        NumericInput.RegisterDoubleClickReset();

        // The player starts off the UI thread once its surface exists and the window has rendered (_shellDrawn), so the workspace
        // appears first and the video canvas attaches after.
        VideoView.SurfaceReady += EnsureGpuEngine;

        // Post-render work runs on ContentRendered (raised once, after the first real render); waiting for dispatcher idle instead
        // could be postponed indefinitely by a busy player.
        ContentRendered += (_, _) =>
        {
            ShowFirstFrame();
            StartupTrace.FirstFrame();

            // Start the crash sentinel at idle (no child process before Frame 1).
            CrashReporter.FirstFrameShown(Dispatcher);

            // Sync Explorer thumbnail/open-command registration with its setting, off the UI thread.
            ExplorerThumbnails.ApplyLater();

            // Open a command-line project/bundle (Explorer double-click via ExplorerThumbnails' open command) once the window is up.
            if (SingleInstance.LaunchFile() is { } launched)
                Dispatcher.BeginInvoke(() => _ = OpenLaunchRequestAsync(new LaunchRequest(LaunchKind.Project, launched)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Later Explorer opens are handed to this instance (SingleInstance.Listen, until the window closes).
            SingleInstance.Listen(path => Dispatcher.InvokeAsync(() => _ = OpenHandedProjectAsync(path)), _windowClosing.Token);

            // Report unreadable settings/presets: they run on defaults and are not written this session.
            if (AppSettings.ReadFailed || PresetStore.ReadFailed)
                _viewModel.StatusText = "Settings file unreadable: changes are not saved this session.";

            // Log launch time: process start to first render.
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var sinceStart = (DateTime.Now - self.StartTime).TotalMilliseconds;
            AppLog.Write(FormattableString.Invariant($"Startup: window drawn {sinceStart:0} ms after the process started (window built in {built} ms)."));

            _shellDrawn = true;
            Dispatcher.BeginInvoke(EnsureGpuEngine, System.Windows.Threading.DispatcherPriority.Loaded);

            // Deferred timeline parts (inspector, Text Settings, tool flyouts) are built just after Frame 1; they start hidden, so
            // nothing shifts. The tool row itself is built with the pane.
            Dispatcher.BeginInvoke(UnifiedTimeline.RealizeTools, System.Windows.Threading.DispatcherPriority.ContextIdle);

            // Heat lamp: pre-warms UI under a resting pointer; attached once everything is up.
            Dispatcher.BeginInvoke(() => _heatLamp ??= new Shell.HeatLampController(_viewModel, EditorHeader, UnifiedTimelinePanel, WarmLayoutCards,
                () => IsScrubHeld || TimelineDragHeld, UnifiedTimeline.RealizeTools, CutoutTarget), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // No launch window: the workspace opens directly; the drag-in overlay offers its targets, built now at idle.
            Dispatcher.BeginInvoke(_drops.PrepareDropTargets, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            HookMediaMemory();
            Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // About a minute in: purge cache entries for deleted source files.
            _ = PurgeCachesLaterAsync();

            // Watched capture folder: pre-bake strips, waveforms and seek indexes at idle after the dwell (Settings: "Prepare the
            // Whole Folder After") so it never competes with start-up.
            if (AppSettings.Current.WatchedCaptureFolder.Trim().Length > 0)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.BrowserIndexDelaySeconds, 5, 3600)), _windowClosing.Token)
                    .ContinueWith(t => t.IsCompletedSuccessfully ? Views.MediaBrowserPane.PrepareWatchedFolderAsync(_windowClosing.Token) : Task.CompletedTask,
                        TaskScheduler.Default).Unwrap();
            }

            // Disk cleanup after Frame 1: downloads over the cache limit, temp folders of unclean sessions, and a legacy libmpv install.
            _ = WorkScheduler.Run(new WorkSpec("Tidying old sessions and downloads", WorkLane.Idle) { Visible = false, Cancellable = false }, _ => Task.Run(() =>
            {
                SessionPaths.DeleteAbandonedSessions();
                DependencyUpdater.DeleteRetiredPlayer();
                YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize,
                    AppSettings.Current.KeepDownloadsUsedByProjects ? YtDlpDownloader.FoldersUsedByProjects([ProjectStore.Folder], []) : null);
            }), _windowClosing.Token);

            // Seed built-in Game Presets into the Styles folder at idle, once per version, never overwriting an existing name.
            if (AppSettings.Current.BuiltInGamePresetsVersion < GamePresetLibrary.Version)
            {
                _ = WorkScheduler.Run(new WorkSpec("Writing the built-in Game Presets", WorkLane.Idle) { Visible = false, Cancellable = false }, async _ =>
                {
                    var written = await GamePresetLibrary.WriteMissingAsync();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        GamePresetLibrary.Seeded();
                        if (written.Count > 0)
                            _viewModel.RefreshStylePresets();
                    });
                }, _windowClosing.Token);
            }

            StartupTrace.Mark("ContentRendered work done");
            Dispatcher.BeginInvoke(() =>
            {
                StartupTrace.Mark("idle after the first frame");
                StartupTrace.Flush();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (StartupTrace.IsOn)
                _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => StartupTrace.Mark("10 s after the first frame (idle memory)"), TaskScheduler.Default);
        };
        Loaded += (_, _) => StartupTrace.Mark("Loaded");
        StartupTrace.Mark("MainWindow: constructor done");
    }

    /// <summary>
    /// Idle-priority purge of cache entries whose source files were deleted or changed (PreviewCache.PurgeMissing), delayed per
    /// Settings. Entries on an absent drive are kept unless Settings treats them as deleted.
    /// </summary>
    private async Task PurgeCachesLaterAsync()
    {
        var settings = AppSettings.Current;
        if (!settings.PurgeMissingAtStartup && !settings.PurgeProxiesOfDeletedFiles)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.PurgeDelaySeconds, 0, 3600)), _windowClosing.Token);
            var (files, bytes) = await WorkScheduler.Run(new WorkSpec("Cleaning up the caches", WorkLane.Idle), work => Task.Run(() =>
            {
                var (count, size) = (0, 0L);
                foreach (var cache in PreviewCache.All)
                {
                    if (cache == PreviewCache.Proxies ? !settings.PurgeProxiesOfDeletedFiles : !settings.PurgeMissingAtStartup)
                        continue;

                    work.Report(cache.Name);
                    var (removed, freed) = cache.PurgeMissing(settings.MissingDrivesCountAsDeleted, work.Token);
                    (count, size) = (count + removed, size + freed);
                }

                return (count, size);
            }, work.Token), _windowClosing.Token);

            if (files > 0)
                AppLog.Write($"Caches: {files} files made from deleted or changed media removed ({PreviewCache.Describe(bytes)}).");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private SplashWindow? _launchWindow;

    /// <summary>Opens the launch window regardless of its startup setting (it also hosts the Social Squisher).</summary>
    private void OpenLaunchWindow(bool startHidden = false)
    {
        if (_launchWindow is not null)
        {
            _launchWindow.Activate();
            return;
        }

        // Modeless soft dialog: a press or drop anywhere on this window dismisses it, like choosing nothing.
        var splash = _launchWindow = new SplashWindow
        {
            Owner = this,
            AvailableEncoders = () => _viewModel.VideoEncoders.Where(encoder => encoder.IsHardware).Select(encoder => encoder.Name).ToList(),
            Opacity = startHidden ? 0 : 1,
        };

        // Not while squishing or showing a result: only its own button closes it then.
        void Dismiss(object? sender, EventArgs e)
        {
            if (!splash.IsSquishing)
                splash.Close();
        }

        PreviewMouseDown += Dismiss;
        PreviewDrop += Dismiss;
        splash.Closed += (_, _) =>
        {
            _launchWindow = null;
            PreviewMouseDown -= Dismiss;
            PreviewDrop -= Dismiss;
            if (splash.Request is { } request)
                _ = OpenLaunchRequestAsync(request);
        };
        splash.Show();
    }

    /// <summary>
    /// SingleInstance hand-off: activates (and restores) the window, waits for any open dialog to close, then opens the project in its
    /// own tab (no prompt, never replacing open work) once nothing else is running.
    /// </summary>
    private async Task OpenHandedProjectAsync(string path)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != 0 && Native.User32.IsIconic(handle))
                _ = Native.User32.ShowWindow(handle, Native.User32.SwRestore);
            Activate();

            // An owned dialog is open (Settings, save prompt): defer until it closes.
            while (System.Windows.Interop.ComponentDispatcher.IsThreadModal && !_windowClosing.IsCancellationRequested)
                await Task.Delay(250);
            for (var waited = 0; _viewModel.IsBusy && waited < 300 && !_windowClosing.IsCancellationRequested; waited++)
                await Task.Delay(100);
            if (_windowClosing.IsCancellationRequested)
                return;

            await OpenProjectInTabAsync(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("A project handed over by another HandPeg could not be opened", ex);
            _viewModel.StatusText = $"{Path.GetFileName(path)} could not be opened: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the launch-window choice once nothing else is running; requested during start-up checks it would otherwise be dropped,
    /// along with its chosen style.
    /// </summary>
    private async Task OpenLaunchRequestAsync(LaunchRequest request)
    {
        for (var waited = 0; _viewModel.IsBusy && waited < 300; waited++)
            await Task.Delay(100);

        switch (request.Kind)
        {
            case LaunchKind.Video when request.StylePath is { } style:
                _ = _viewModel.LoadFileWithStyleAsync(request.Path, style);
                break;

            case LaunchKind.Video:
                // A video handed over from Explorer names the tab before its probe, as a drop does.
                DropLater("a video handed over", () => OpenNamingTabAsync(request.Path, () => _viewModel.LoadFileAsync(request.Path)));
                break;

            case LaunchKind.Project:
                _ = OpenProjectInTabAsync(request.Path);
                break;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        StartupTrace.Mark("SourceInitialized (window handle made)");

        // Dark or light title bar to match the theme.
        ThemeManager.ApplyTitleBar(this);

        // Clamp into the actual monitor's work area before first show (SystemParameters.WorkArea is the primary's, at system DPI).
        KeepInside(Shell.WindowFrame.WorkAreaOf(this, new WindowInteropHelper(this).Handle));

        // Before the first layout: load cached encoder probe results so tabs render with their kept encoder.
        _viewModel.TakeEarlyHardwareEncoders();
        StartupTrace.Mark("hardware encoders as found before: " + (_viewModel.VideoEncoder.Name));

        // DWM-cloak the window until its first frame renders (avoids a white flash); uncloak on ContentRendered or after a timeout.
        if (!_firstFrameShown)
        {
            ThemeManager.SetCloaked(this, true);
            var fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                ShowFirstFrame();
            };
            fallback.Start();
        }
    }

    private bool _firstFrameShown;

    /// <summary>Uncloaks the window (cloaked since handle creation) once its first frame exists.</summary>
    private void ShowFirstFrame()
    {
        if (_firstFrameShown)
            return;

        _firstFrameShown = true;
        ThemeManager.SetCloaked(this, false);
    }

    // True while a close prompt is up: re-entrant close requests (taskbar Close, Alt+F4, double click) wait for it instead of prompting again.
    private bool _closeAsked;

    /// <summary>
    /// Close flow: prompts Save / Don't Save / Cancel for unsaved work when enabled. The window stays open only on explicit choice
    /// (Cancel, or staying after a failed save); no shutdown failure silently keeps it open.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Never close mid tab activation (state split between view model and cache) or mid orphan-tab import.
        if (_closeAsked || RefuseCloseMidSwitch())
        {
            e.Cancel = true;
            return;
        }

        // Mark an intentional exit so the crash sentinel treats an external kill during the prompt (taskbar, Task Manager) as user
        // action, not a crash; cleared if the close is cancelled.
        Guarded("note the close", () => CrashReporter.MarkUserClosing(true));

        // Store side-window bounds with the layout before RememberWindowSize writes settings (the layout's own save is debounced
        // 500 ms and would never run).
        Guarded("keep the arrangement", () => _viewModel.SavePaneLayout(SaveDockInto));
        Guarded("remember the window's size", RememberWindowSize);

        bool close;
        try
        {
            _closeAsked = true;
            close = MayClose();

            // Clean close: apply the exit plan to tab cache files (delete, or keep for next start); the 20 s tick writes no
            // more recovery files.
            if (close)
            {
                _autosave.Stop();
                Guarded("let the project tabs' cache go", ForgetProjectTabs);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Asking whether to save before closing failed", ex);
            close = MayCloseAfterFault(ex);
        }
        finally
        {
            _closeAsked = false;
        }

        if (!close)
        {
            e.Cancel = true;
            Guarded("note the close called off", () => CrashReporter.MarkUserClosing(false));

            // Close cancelled: import orphaned-session tabs deferred during the prompt (at idle, as at start).
            TakeInLeftTabsPutOff();
            return;
        }

        Guarded("empty the side window", _dock.Shutdown);
    }

    /// <summary>
    /// True when the window may close: immediately with nothing unsaved (or the prompt disabled), else per the save prompt's answer.
    /// False only when the user chooses to stay.
    /// </summary>
    private bool MayClose()
    {
        // Inactive tabs with unsaved changes first (MainWindow.ProjectTabs.cs).
        if (!MayCloseProjectTabs())
            return false;

        // The tab exit plan already covered the active tab (keep-tabs, multi-tab dialog, or nothing to ask): no further prompt.
        if (ExitAnsweredByTabs)
            return true;

        // Remaining cases: save prompt disabled (no prompt), or a single active project with unsaved or never-saved work (ExitPlanFor
        // counted it and made its dot exact).
        var unsaved = _exitQuestion == ExitQuestion.Classic;
        if (!unsaved || !AppSettings.Current.PromptToSaveOnExit)
        {
            if (_viewModel.HasUnsavedChanges)
                AppLog.Write($"Closing with unsaved changes in {_viewModel.DescribeUnsavedChanges()}.");
            return true;
        }

        AppLog.Write(_tabs.Active.Dirty ? $"Closing with unsaved changes in {_viewModel.DescribeUnsavedChanges()}." : "Closing with a project never saved: asked whether to save it.");

        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(_viewModel.LocalMediaPath);
        }
        catch (ArgumentException)
        {
            name = "";
        }

        // A project with a file saves to it, as Ctrl+S (and the tabs' dialog) does; the name box is only for never-saved work.
        var projectPath = _viewModel.CurrentProjectPath;
        var dialog = new SaveOnExitDialog(name.Length > 0 ? name : $"Project {DateTime.Now:yyyy-MM-dd HH.mm}", projectPath) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case ExitChoice.Cancel:
                return false;

            case ExitChoice.SaveAndClose:
                // Save failed: report the error and let the user choose to close anyway (never discard silently, never stay silently).
                var saved = projectPath.Length > 0 ? _viewModel.SaveProjectTo(projectPath) : _viewModel.SaveProject(dialog.ProjectName);
                return saved.IsSaved
                    || new ConfirmDialog("Project Not Saved", $"{saved.Line}\n\nClose HandPeg anyway? The changes that were not saved are lost.", "Close Without Saving")
                    {
                        Owner = this, DefaultsToCancel = true, CancelText = "Keep HandPeg Open",
                    }.ShowDialog() == true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Fallback when the save prompt fails: asks via a plain MessageBox whether to close without saving; if that also fails, closes.
    /// </summary>
    private bool MayCloseAfterFault(Exception fault)
    {
        try
        {
            // Never-saved work counts as unsaved too (UnsavedForExit), changed since open or not.
            if (!_viewModel.HasUnsavedChanges && !UnsavedForExit(false, _viewModel.HoldsNothingForTab, _viewModel.CurrentProjectPath))
                return true;
            return MessageBox.Show(this, $"HandPeg could not ask whether to save the project ({fault.Message}).\n\nClose without saving? The changes that were not saved are lost.",
                "HandPeg", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        catch (Exception ex)
        {
            AppLog.Write("Asking whether to close without saving failed; HandPeg closes", ex);
            return true;
        }
    }

    /// <summary>Runs one shutdown step; a failure is logged and does not stop the remaining steps.</summary>
    private static void Guarded(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Closing HandPeg: could not {step}", ex);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        Guarded("note the window closed", () => CrashReporter.Note("app", "window closed"));

        // Kill running encodes, downloads and probes (with child processes) first so none outlive the app or lock session files.
        // Each step is isolated so one failure does not skip the others.
        Guarded("cancel the window's work", _windowClosing.Cancel);
        Guarded("let projects double-clicked from now on start a copy of their own", SingleInstance.Release);
        Guarded("end the tools still running", ProcessPipes.KillAll);
        Guarded("shut the view model down", _viewModel.Shutdown);
        Guarded("stop following the player", () => FollowPlayerByFrame(false));
        Guarded("stop the GPU retry", _gpuRetry.Stop);
        Guarded("close the GPU engine", CloseGpuEngine);
        Guarded("close the Sequence Overview Scrubber's drawer", () => _programStrip?.Dispose());

        // Keep the most recent downloads so a repeated URL loads from cache.
        Guarded("trim the downloads", () => YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize, _viewModel.PinnedDownloads()));

        // Shut down explicitly: ShutdownMode is OnLastWindowClose (unless first-run ran), and stray windows (never shown, unowned)
        // would otherwise keep the process alive.
        Application.Current?.Shutdown();
    }

    // ----- Hotkeys -----

    // Last arrow-key nudge time of a layout box: a run of nudges is one Undo step.
    private long _nudgedAt;

    // Last typed-in text box and the undo mark at that time (MainViewModel.UndoMark): while the timeline history has not moved since,
    // Ctrl+Z / Ctrl+Y in that box undo/redo its typing.
    private (TextBoxBase Box, object? Mark)? _typedIn;

    /// <summary>
    /// False for undo (Ctrl+Z), true for redo (Ctrl+Y, Ctrl+Shift+Z), null otherwise. Fixed regardless of keybinds (KeybindManager
    /// never rebinds them).
    /// </summary>
    private static bool? UndoKey(KeyEventArgs e) =>
        KeybindManager.IsUndo(e) ? false : KeybindManager.IsRedo(e) ? true : (bool?)null;

    /// <summary>
    /// True when Ctrl+Z/Ctrl+Y belongs to the focused text box: it can undo/redo, was the last typed into, and the timeline history
    /// has not moved since (committing a value via Enter or focus loss creates a timeline step, which then owns the undo).
    /// </summary>
    private bool TypingIsNewest(TextBoxBase box, bool redo) =>
        (redo ? box.CanRedo : box.CanUndo) && _typedIn is { } typed && ReferenceEquals(typed.Box, box) && ReferenceEquals(typed.Mark, _viewModel.UndoMark);

    /// <summary>True for keys that edit text-box content (characters, Back, Delete, Enter, Ctrl+V, Ctrl+X), not navigation/selection.</summary>
    private static bool TypesText(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var (control, alt) = (modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Alt));
        if (control && !alt)
            return key is Key.V or Key.X or Key.Back or Key.Delete;

        // Alt alone is menu access; Ctrl+Alt is AltGr, which types characters on many layouts.
        if (alt != control || modifiers.HasFlag(ModifierKeys.Windows))
            return false;

        return key is Key.Back or Key.Delete or Key.Enter or Key.Space or Key.ImeProcessed or Key.DeadCharProcessed
            or (>= Key.D0 and <= Key.Z) or (>= Key.NumPad0 and <= Key.Divide) or (>= Key.Oem1 and <= Key.OemBackslash);
    }

    /// <summary>
    /// Global key dispatch (side windows forward here too), in order: dial/tour Esc and Caps Lock echo; save; undo/redo and text-box
    /// keys; Canvas box nudge; then KeybindManager shortcuts including held navigation keys (actions in MainWindow.Keybinds.cs).
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TakeOverlayKey(e))
            return;

        var chord = KeybindManager.ChordOf(e);
        var bound = KeybindManager.Resolve(chord);

        // Shift + held navigation key = faster variant, unless Shift+key has its own binding.
        if (bound is null && chord.Mods == ModifierKeys.Shift && KeybindManager.Resolve(chord.WithoutShift) is { } faster
            && KeybindManager.Action(faster) is { } held && held.Has(KeyActionFlags.Continuous))
            bound = faster;

        // Save keys run before anything else (text boxes never receive Ctrl+S / Ctrl+Alt+S unless rebound).
        // Save As is Ctrl+Alt+S because AMD's driver software registers Ctrl+Shift+S globally ("save instant replay"), so it never
        // reaches HandPeg; KeybindManager refuses to bind it. With Alt held WPF reports Key.System; KeybindManager.ChordOf unwraps it.
        if (bound is not null && KeybindManager.Action(bound) is { } early && early.Has(KeyActionFlags.WorksInText))
        {
            if (!e.IsRepeat)
                RunAction(bound);
            e.Handled = true;
            return;
        }

        var focused = Keyboard.FocusedElement;

        // Undo/redo everywhere except open drop-downs: the focused text box's while its typing is newest (TypingIsNewest), else the
        // timeline's, so a focused number/search box no longer swallows them. RunUndo cancels an active drag first.
        if (UndoKey(e) is { } redo && focused is not ComboBoxItem)
        {
            RunUndo(redo);
            e.Handled = true;
            return;
        }

        // An editing key in a text box: record it as typing newer than the timeline's last step.
        if (focused is TextBoxBase typedInto && TypesText(e))
            _typedIn = (typedInto, _viewModel.UndoMark);

        // Text boxes and open drop-downs keep their keys (a press elsewhere releases text focus via ReleaseTypingFocus in
        // MainWindow.Keybinds.cs, restoring shortcuts).
        if (focused is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
            return;

        if (EndCropOnEscape(e, focused))
            return;

        // Selected layout box: arrows nudge 1 output pixel (10 with Shift); a run of nudges is one Undo step.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift && TakeNudge(e.Key, focused, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        // All other shortcuts via KeybindManager (preset plus custom bindings), unless the focused control owns the key or the action
        // cannot run now (MainWindow.Keybinds.cs).
        if (bound is not null && TryRunBound(bound, chord, e, sender, focused))
            e.Handled = true;
    }

    // ----- Source / destination -----

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        WarmUpPlayer();
        var dialog = new OpenFileDialog
        {
            Title = "Select a source file",
            Filter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.mp3;*.m4a;*.flac;*.wav|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _viewModel.SourcePath = dialog.FileName;
        _viewModel.LoadSourceCommand.Execute(null);
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save output as",
            // Same order as the view model's Containers (audio-only FLAC, WAV, MP3 last) so FilterIndex maps to the format.
            Filter = "MP4|*.mp4|Matroska|*.mkv|WebM|*.webm|QuickTime|*.mov|Animated GIF|*.gif|Animated WebP|*.webp|FLAC Audio|*.flac|WAV Audio|*.wav|MP3 Audio|*.mp3|All files|*.*",
            FilterIndex = Math.Max(1, _viewModel.Containers.ToList().IndexOf(_viewModel.Container) + 1),
            FileName = Path.GetFileName(_viewModel.DestinationPath),
            DefaultExt = _viewModel.Container,
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.DestinationPath = dialog.FileName;
    }

    private void OpenProjects_Click(object sender, RoutedEventArgs e) => Faults.Forget(OpenProjects_ClickAsync(sender, e), nameof(OpenProjects_Click));

    private async Task OpenProjects_ClickAsync(object sender, RoutedEventArgs e)
    {
        var dialog = new ProjectManagerWindow(_viewModel) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        // New or opened projects go to their own tab, never replacing open work.
        await ActOnProjectManagerAsync(dialog);
    }

    // Session cleared (+ New Project): the player releases its file and the drop hint replaces the video.
    private void OnSessionCleared()
    {
        if (_gpu is { } gpu)
        {
            gpu.Pause();
            gpu.Seek(0);
            _viewModel.IsPlaying = false;
            FollowPlayerByFrame(false);
        }

        UpdateVideoVisibility();
        RedrawLayout();
        RedrawPlayerLayout();
    }

    /// <summary>
    /// Split / Trim by Exact Timecode (row context menu, toolbar for the selected clip): prompts for a time, offering the neighbouring
    /// I-frames of the clip's file, then splits or trims there.
    /// </summary>
    private void SplitTrimByTimecode(Layer clip)
    {
        if (_viewModel.ClipOf(clip) is null)
        {
            _viewModel.StatusText = "Select a clip to split or trim first.";
            return;
        }

        var iFrames = new List<double>();
        var speed = clip.Speed > 0 ? clip.Speed : 1;
        var (start, end) = (clip.StartTime, clip.Duration > 0.001 ? clip.StartTime + clip.Duration : _viewModel.SequenceSeconds);
        if (clip.IsVideoFile && MediaProbe.KnownIFrames(clip.ImagePath.Trim().Trim('"')) is { } keys)
        {
            foreach (var key in keys)
            {
                if (key >= clip.MediaOffset && clip.StartTime + (key - clip.MediaOffset) / speed is var on && on <= end)
                    iFrames.Add(on);
            }
        }

        iFrames.Sort();

        // Initial value: the playhead if inside the clip, else the clip's nearer end.
        var at = Math.Clamp(_viewModel.PositionMs / 1000, start, Math.Max(start, end));
        var dialog = new ManualCutWindow(clip.Name, start, end, _viewModel.SequenceFps, iFrames, at) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _ = dialog.Edit switch
        {
            TimecodeEdit.Split => _viewModel.SplitLayer(clip, dialog.Seconds),
            TimecodeEdit.TrimStart => _viewModel.TrimClip(clip, start: true, dialog.Seconds),
            _ => _viewModel.TrimClip(clip, start: false, dialog.Seconds),
        };
        (_selectedClip, _playheadVideoSelected) = (null, false);
        ScheduleClipRedraw();
    }

    // Toolbar button: the selected clip, else the video at the playhead.
    private void ManualCut_Click(object sender, RoutedEventArgs e) => SplitTrimByTimecode(_selectedClip switch
    {
        Layer layer when _viewModel.Layers.Contains(layer) && layer.HasTiming => layer,
        LayerSound sound when _viewModel.Layers.Contains(sound.Layer) => sound.Layer,
        _ => _viewModel.PlayheadVideo,
    });

    /// <summary>
    /// Remove Dead Air: prompts for the silence threshold and mark/delete mode, then scans the selected audible clip, else
    /// MainViewModel.ListenedSound.
    /// </summary>
    private void RemoveDeadAir_Click(object sender, RoutedEventArgs e) => RunDeadAir(_selectedClip switch
    {
        Layer { CarriesSound: true } layer => layer,
        LayerSound sound => sound.Layer,
        _ => null,
    });

    private void DropHint_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => BrowseSource_Click(sender, e);

    // ----- Header flyouts (Add Media box, Settings queue and squisher, Layout Selector) -----
    // Each opens on hover and stays open while the pointer is over its button or itself (HoverFlyout). Add Layer lives in
    // Views/UnifiedTimelinePane.

    private readonly HoverFlyout _addMediaHover, _settingsHover, _layoutHover;

    // ----- Startup window size -----

    /// <summary>
    /// Initial bounds: clamped to the work area, centred, top never above it (keeps the title bar reachable). Overrides the XAML
    /// minimum size when the work area is smaller (e.g. 1920x1200 at 150% gives 1280x752). A size covering >= 95% opens maximized.
    /// </summary>
    private void ApplyStartupWindowSize()
    {
        var settings = AppSettings.Current;
        var work = SystemParameters.WorkArea;
        (MinWidth, MinHeight) = (Math.Min(MinWidth, work.Width), Math.Min(MinHeight, work.Height));
        var maximized = false;
        switch (settings.StartupWindowSize)
        {
            case AppSettings.MaximizedWindow:
                maximized = true;
                break;
            case AppSettings.RememberWindowSize when settings.LastWindowWidth >= MinWidth && settings.LastWindowHeight >= MinHeight:
                (Width, Height, maximized) = (settings.LastWindowWidth, settings.LastWindowHeight, settings.LastWindowMaximized);
                break;
        }

        if (Width >= work.Width * NearlyFullScreen || Height >= work.Height * NearlyFullScreen)
            maximized = true;
        (Width, Height) = (Math.Min(Width, work.Width), Math.Min(Height, work.Height));
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top) = (work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        KeepInside(work);
        if (maximized)
            WindowState = WindowState.Maximized;
    }

    private const double NearlyFullScreen = 0.95;

    /// <summary>Clamps restored bounds inside a work area (this window's DIPs), top never above the area's top.</summary>
    private void KeepInside(Rect work)
    {
        if (WindowState != WindowState.Normal || work.IsEmpty)
            return;

        (MinWidth, MinHeight) = (Math.Min(MinWidth, work.Width), Math.Min(MinHeight, work.Height));
        (Width, Height) = (Math.Min(Width, work.Width), Math.Min(Height, work.Height));
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    /// <summary>
    /// Records restore size and maximized state for "Remember Last Size", then writes settings (including the exit-time pane layout).
    /// </summary>
    private void RememberWindowSize()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var keep = !bounds.IsEmpty && bounds.Width >= MinWidth && bounds.Height >= MinHeight;
        var maximized = WindowState == WindowState.Maximized;

        try
        {
            AppSettings.Update(settings =>
            {
                if (keep)
                    (settings.LastWindowWidth, settings.LastWindowHeight, settings.LastWindowMaximized) = (Math.Round(bounds.Width), Math.Round(bounds.Height), maximized);
            }, saveNow: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remember Last Size off: skip.
        }
    }

    // ----- Fitting -----

    private void FlyoutSquisher_Click(object sender, RoutedEventArgs e)
    {
        _settingsHover.Close();
        OpenLaunchWindow();
    }

    // Game Preset save/import/export from the Settings flyout, available even with an empty timeline.
    private void FlyoutGamePresets_Click(object sender, RoutedEventArgs e)
    {
        _settingsHover.Close();
        ManageStyles_Click(UnifiedTimeline, e);
    }

    // ----- Header Add Media: click picks files; hover opens the path/URL box -----
    // HoverFlyout: opens after a hover dwell, closes shortly after leaving button and flyout; stays open while its box has focus.

    /// <summary>File picker: with nothing open, opens the first file (or project) onto Track 1; otherwise inserts all at the playhead.</summary>
    private void AddMedia_Click(object sender, RoutedEventArgs e)
    {
        _addMediaHover.Close();
        WarmUpPlayer();
        static string Patterns(IEnumerable<string> extensions) => string.Join(";", extensions.Select(x => "*" + x));
        var all = MediaFiles.VideoExtensions.Concat(MediaFiles.AudioExtensions).Concat(MediaFiles.ImageExtensions);
        var dialog = new OpenFileDialog
        {
            Title = _viewModel.HasSource ? "Add media to the timeline" : "Open a video",
            Multiselect = _viewModel.HasSource,
            Filter = $"Media files|{Patterns(all)}|Videos|{Patterns(MediaFiles.VideoExtensions)}|Sounds|{Patterns(MediaFiles.AudioExtensions)}"
                + $"|Pictures|{Patterns(MediaFiles.ImageExtensions)}|HandPeg projects and bundles|*{ProjectStore.Extension};*{ProjectBundle.Extension}|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames is not { Length: > 0 } files)
            return;

        if (!_viewModel.HasSource)
        {
            LoadDroppedFile(files[0]);
            return;
        }

        AddFilesToTimeline(files, _viewModel.PositionMs / 1000);
    }

    /// <summary>Inserts files at a time: audio as audio clips, video and images as layers (placed by PlaceClip).</summary>
    private void AddFilesToTimeline(string[] files, double at)
    {
        var sounds = files.Where(f => MediaFiles.KindOf(f) == MediaBrowserKind.Audio).ToArray();
        var pictures = files.Except(sounds).ToArray();
        if (pictures.Length > 0)
            DropLater("adding clips", () => AddDroppedLayersAsync(pictures, at, null));
        if (sounds.Length > 0)
            DropLater("adding sound", () => AddDroppedAudioAsync(sounds, at));
    }

    private void AddMediaFlyout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _addMediaHover.Close();
        e.Handled = true;
    }

    // Pre-start the player on open (as on box focus) so a pasted link plays immediately.
    private void AddMediaFlyout_Opened(object? sender, EventArgs e) => WarmUpPlayer();

    /// <summary>
    /// Places a header flyout: below its button (offset so it never opens under the pointer), right-aligned; or beside it toward the
    /// window centre when the header is a side strip. Uses UiScale.PlaceAgainst in device pixels (no touch mirroring; fixed while panes move).
    /// </summary>
    private void PlaceHeaderFlyout(Popup popup, FrameworkElement host, FrameworkElement? within = null, double inset = 0) =>
        UiScale.PlaceAgainst(popup, host, HeaderFlyoutSide(host), HeaderFlyoutGap, within, inset);

    // Header flyout offset from its button, in DIPs.
    private const double HeaderFlyoutGap = 6;

    /// <summary>Flyout side relative to its button: below, or toward the window centre when the header is a side strip.</summary>
    private UiScale.FlyoutSide HeaderFlyoutSide(FrameworkElement host)
    {
        if (_headerShape != Docking.PaneShape.Column)
            return UiScale.FlyoutSide.BelowEnd;

        var window = Window.GetWindow(host) ?? this;
        var onRight = host.TranslatePoint(new Point(host.ActualWidth / 2, 0), window).X > window.ActualWidth / 2;
        return onRight ? UiScale.FlyoutSide.Left : UiScale.FlyoutSide.Right;
    }

    // ----- Progress chip and flyout -----
    // HoverFlyout pattern: opens on chip hover, closes shortly after leaving chip and flyout (so the pointer can cross between them).

    private readonly System.Windows.Threading.DispatcherTimer _workFlyoutClose = new() { Interval = TimeSpan.FromMilliseconds(260) };

    // Anchored below the title-bar status (the single home for status and running work).
    private void TitleStatus_MouseEnter(object sender, MouseEventArgs e)
    {
        _workFlyoutClose.Stop();
        PauseBackgroundToggle.IsChecked = WorkScheduler.IsPausedByUser(WorkLane.Background);
        PauseIdleToggle.IsChecked = WorkScheduler.IsPausedByUser(WorkLane.Idle);
        WorkFlyoutPanel.Margin = new Thickness(0, 4, 0, 0);
        WorkFlyout.IsOpen = true;
    }

    private void WorkFlyout_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_workFlyoutCloseHooked)
        {
            _workFlyoutClose.Tick += (_, _) =>
            {
                _workFlyoutClose.Stop();
                if (!TitleStatus.IsMouseOver && !WorkFlyoutPanel.IsMouseOver)
                    WorkFlyout.IsOpen = false;
            };
            _workFlyoutCloseHooked = true;
        }

        _workFlyoutClose.Stop();
        _workFlyoutClose.Start();
    }

    private bool _workFlyoutCloseHooked;

    private void LanePause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton { Tag: string name } toggle && Enum.TryParse<WorkLane>(name, out var lane))
            WorkScheduler.SetPaused(lane, toggle.IsChecked == true);
    }

    private void WorkItemCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkItem item })
            item.Cancel();
    }

    private void WorkFlyoutQueue_Click(object sender, RoutedEventArgs e)
    {
        WorkFlyout.IsOpen = false;
        OpenQueue_Click(sender, e);
    }

    private void FlyoutQueue_Click(object sender, RoutedEventArgs e)
    {
        _settingsHover.Close();
        OpenQueue_Click(sender, e);
    }

    // ----- Saving: Ctrl+S, Save As, and autosave -----

    /// <summary>Save As / first save: opens the Project Manager with its name box focused (not the system file picker).</summary>
    private void OpenProjectManagerToSave()
    {
        var dialog = new ProjectManagerWindow(_viewModel, focusName: true) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        // Route through the project tabs, as the header's Projects button does.
        _ = ActOnProjectManagerAsync(dialog);
    }

    private void SaveProject()
    {
        if (!_viewModel.HasSource)
            _viewModel.StatusText = "There is nothing to save yet: load a video first.";
        else if (_viewModel.CurrentProjectPath.Length > 0)
            _viewModel.SaveProjectTo(_viewModel.CurrentProjectPath);
        else
            SaveProjectAs();
    }

    /// <summary>
    /// Save As via the Windows file dialog (does not touch undo history). The second file type is a bundle (.hpb: project plus every
    /// referenced file); <paramref name="bundle"/> preselects it.
    /// </summary>
    private void SaveProjectAs(bool bundle = false)
    {
        if (!bundle)
        {
            OpenProjectManagerToSave();
            return;
        }

        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "There is nothing to save yet: load a video first.";
            return;
        }

        var current = _viewModel.CurrentProjectPath;
        Directory.CreateDirectory(AppPaths.Projects);
        var dialog = new SaveFileDialog
        {
            Title = "Save Project As",
            Filter = $"HandPeg project (*{ProjectStore.Extension})|*{ProjectStore.Extension}"
                     + $"|HandPeg project bundle, with its media (*{ProjectBundle.Extension})|*{ProjectBundle.Extension}",
            FilterIndex = bundle ? 2 : 1,
            DefaultExt = bundle ? ProjectBundle.Extension : ProjectStore.Extension,
            AddExtension = true,
            InitialDirectory = current.Length > 0 ? Path.GetDirectoryName(current) : AppPaths.Projects,
            FileName = current.Length > 0 ? Path.GetFileNameWithoutExtension(current) : Path.GetFileNameWithoutExtension(_viewModel.SourcePath.Trim().Trim('"')),
        };
        if (dialog.ShowDialog(this) != true)
            return;
        if (dialog.FilterIndex == 2 || dialog.FileName.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase))
            _ = _viewModel.SaveBundleAsync(Path.ChangeExtension(dialog.FileName, ProjectBundle.Extension));
        else
            _viewModel.SaveProjectTo(dialog.FileName);
    }

    private readonly System.Windows.Threading.DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(20) };
    private DateTime _lastAutosave = DateTime.UtcNow;

    // 20 s tick: writes the active tab's crash-recovery file, then autosaves when enabled, due and changed. Skipped while playing,
    // scrubbing, dragging or switching tabs (full-state capture on the UI thread, Law 3), and for recovery also during a press, modal
    // dialog or close. First deletes a stale recovery file of a clean active tab (O(1), no fingerprint).
    private void Autosave_Tick(object? sender, EventArgs e)
    {
        if (_frontRecovery is { Written: true } && !_viewModel.MayHaveUnsavedChanges && !_switchingTab)
            DropFrontRecovery(_tabs.Active);
        if (_viewModel.IsPlaying || _gpu is { IsPlaying: true } || IsScrubHeld || TimelineDragHeld || _switchingTab)
            return;

        if (!FrontRecoveryHeld)
            KeepFrontRecovery();
        var settings = AppSettings.Current;
        if (!settings.AutosaveEnabled || DateTime.UtcNow - _lastAutosave < TimeSpan.FromMinutes(Math.Clamp(settings.AutosaveMinutes, 1, 120)))
            return;

        _lastAutosave = DateTime.UtcNow;
        _ = _viewModel.AutosaveAsync();
    }

    private void OpenQueue_Click(object sender, RoutedEventArgs e) => OpenRenderQueue();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// Opens Settings (optionally at a named tab, e.g. a pane's "More ... Settings..."), covering the editor at its bounds and maximized
    /// state, with a live stage mirroring the editor panes (StageMirror).
    /// </summary>
    /// <param name="prepare">Extra setup before showing (e.g. open at a specific key).</param>
    private void OpenSettings(string? tab = null, Action<SettingsWindow>? prepare = null)
    {
        CrashReporter.Note("settings", "opened");
        var window = new SettingsWindow(_viewModel) { Owner = this, MirrorSource = StageMirror };
        window.CoverWindow(this);
        if (tab is not null)
            window.ShowTab(tab);
        prepare?.Invoke(window);
        var explorerThumbnails = AppSettings.Current.ShowExplorerThumbnails;
        var saved = window.ShowDialog() == true;
        CrashReporter.Note("settings", saved ? "saved" : "closed");
        if (saved)
        {
            _viewModel.OnSettingsSaved();
            _gpu?.ApplyAudioSettings(AppSettings.Current);
            UiScale.ApplyAll();
            ShowHotbar();

            // Timeline toggles (effect lanes, filmstrips, waveforms, gain lines...) are read during row builds: rebuild.
            ScheduleClipRedraw();

            if (_dock.IsRealized(ExportId))
                FillExportSection();

            // Register or unregister Explorer project previews if toggled.
            if (AppSettings.Current.ShowExplorerThumbnails != explorerThumbnails)
                ExplorerThumbnails.ApplyLater();
        }

        // Crash-report toggle (here or in the first-run window it can open): start/stop the sentinel.
        CrashReporter.FollowSetting();

        // An update found or downloaded from Settings is offered like a startup-check result.
        if (AppUpdater.ReadyVersion is { } update)
            ShowUpdateNotice(update, downloaded: true);
        else if (AppUpdater.FoundVersion is { } found)
            ShowUpdateNotice(found, downloaded: false);

        // FFmpeg may have just been installed: start the player if it was waiting.
        EnsureGpuEngine();
    }


    // Source pane button / Enter: browse when the box is empty, else load its path or URL.
    private void SourceAction_Click(object sender, RoutedEventArgs e)
    {
        var path = (_viewModel.SourcePath ?? "").Trim().Trim('"');
        _addMediaHover.Close();
        if (path.Length == 0)
            BrowseSource_Click(sender, e);
        else if (ProjectStore.IsProjectFile(path))
            _ = OpenProjectInTabAsync(path);   // Project/bundle path: open it in a project tab.
        else
            _viewModel.LoadSourceCommand.Execute(null);
    }

    // Command text regenerates on settings changes only while its box is visible (MainViewModel.CommandPreview).
    private void CommandPreviewBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _viewModel.CommandPreviewWatched = e.NewValue is true;

    private void SourcePaneBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SourceAction_Click(sender, e);
    }

    // ----- View-model change handlers -----

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-lay marker flags on the Sequence Overview Scrubber for the new duration.
        if (e.PropertyName is nameof(MainViewModel.DurationMs))
            DrawTimelineMarkers();

        if (e.PropertyName is nameof(MainViewModel.SoloedTracks))
            OnSoloTrackChanged();

        if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.ShowLinkedAudio)
            or nameof(MainViewModel.AudioLinked) or nameof(MainViewModel.TimelineWaveform))
        {
            ScheduleClipRedraw();
        }

        if (e.PropertyName == nameof(MainViewModel.PositionMs))
        {
            FollowPlayheadInPlayer();

            // Held scrub commits (60 Hz): ScrubPlayheadTo already moved the lines; ReleaseScrubGate moves and repaints once.
            if (!_scrubState.Flushing && !ScrubGate.IsHeld)
            {
                MoveClipPlayheads();
                KeyLanes.MovePlayhead(_viewModel.PositionMs / 1000);

                // Refresh keyframe-at-playhead diamonds (paused only).
                if (!_viewModel.IsPlaying)
                    KeyLanes.InvalidateVisual();
            }
        }

        if (e.PropertyName is nameof(MainViewModel.ShowKeyframesPane) or nameof(MainViewModel.ShowEffectsPane) or nameof(MainViewModel.IsMediaBrowserVisible))
            UpdateSidePanes();


        if (e.PropertyName is nameof(MainViewModel.ActiveKeyLayer) or nameof(MainViewModel.DurationMs))
            DrawKeyTimeline();

        if (e.PropertyName == nameof(MainViewModel.HasSource))
            UpdateVideoVisibility();

        if (e.PropertyName is nameof(MainViewModel.ShowTimelineThumbnails) or nameof(MainViewModel.DurationMs))
            RebuildFilmstrip();
        if (e.PropertyName == nameof(MainViewModel.ShowTimelineThumbnails))
            ProgramStripSettingChanged();
        // Skip mid-switch: the outgoing tab already suspended the engine (GpuPreviewEngine.Suspend keeps live tabs' frames), and an
        // Unload here would clear them all.
        if (e.PropertyName == nameof(MainViewModel.HasSource) && !_viewModel.HasSource && !_switchingTab)
        {
            ClearProgramStrip();
            UnloadGpu();
        }

        if (e.PropertyName == nameof(MainViewModel.Volume))
        {
            if (_gpu is { } gpuVolume)
                gpuVolume.Volume = (float)(_viewModel.Volume / 100.0);
        }

        if (e.PropertyName == nameof(MainViewModel.PlaybackSpeed))
        {
            if (_gpu is { } gpuSpeed && _shuttle == 0)
                gpuSpeed.Rate = _viewModel.PlaybackRate;
        }

        if (e.PropertyName is nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer))
            UpdateOverlayMode();

        // Source view (Interactive Crop, Draw Target) shows the selected clip (MainViewModel.ShownSourceClip).
        if (e.PropertyName == nameof(MainViewModel.KeyLayer) && CurrentOverlayMode != OverlayMode.None)
            ScheduleGpuState();

        if (e.PropertyName == nameof(MainViewModel.IsArrangeActive))
            UpdateLayoutPane();

        // Refresh the layout pane's layer list.
        if (e.PropertyName is nameof(MainViewModel.AutoCaptions))
            RedrawLayout();

        // Overlays and the live crop follow value changes from both dragging and typing.
        if (e.PropertyName is nameof(MainViewModel.CropLeft) or nameof(MainViewModel.CropRight)
            or nameof(MainViewModel.CropTop) or nameof(MainViewModel.CropBottom)
            or nameof(MainViewModel.SourceWidth) or nameof(MainViewModel.SourceHeight)
            or nameof(MainViewModel.FrameWidth) or nameof(MainViewModel.FrameHeight))
        {
            RedrawOverlay();
            RedrawLayout();
        }


        if (e.PropertyName == nameof(MainViewModel.SpriteSheetPath))
            LoadSpriteSheet();

    }

    private OverwriteDecision AskOverwrite(string path)
    {
        var dialog = new FileOverwriteDialog(path) { Owner = this };
        dialog.ShowDialog();
        return dialog.Decision;
    }

    // Re-lay marker flags on the Sequence Overview Scrubber for the new width.
    private void MarkerCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTimelineMarkers();

    // Layout pane stage = the output frame.
    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A));

    /// <summary>Time in milliseconds at an x position on the timeline slider.</summary>
    private double TimelineMsAt(double x)
    {
        // The track is inset by half a thumb width at each end.
        const double inset = 5.5;
        var track = TimelineSlider.ActualWidth - 2 * inset;
        return track > 0 ? Math.Clamp((x - inset) / track, 0, 1) * _viewModel.DurationMs : 0;
    }

    // ----- Advanced playback -----

    private void StepBack_Click(object sender, RoutedEventArgs e) => StepFrames(-1);

    private void StepForward_Click(object sender, RoutedEventArgs e) => StepFrames(1);

    private void PreviousIFrame_Click(object sender, RoutedEventArgs e) => JumpToIFrame(-1);

    private void NextIFrame_Click(object sender, RoutedEventArgs e) => JumpToIFrame(1);

    /// <summary>
    /// Seeks to the previous/next I-frame from the I-frame index; setting the position moves the timeline, which seeks the player exactly.
    /// </summary>
    private void JumpToIFrame(int direction)
    {
        // Like frame stepping, I-frame jumps pause playback.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        _viewModel.SeekToIFrame(direction);
    }

    private void StepFrames(int frames)
    {
        // Frame stepping pauses playback.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        _viewModel.StepFrames(frames);
    }

    private void PositionText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Faults.Forget(PositionText_MouseLeftButtonUpAsync(sender, e), nameof(PositionText_MouseLeftButtonUp));

    private async Task PositionText_MouseLeftButtonUpAsync(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.PositionText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard locked by another process; nothing copied.
            _viewModel.StatusText = "The clipboard is busy. Try again.";
            return;
        }

        _viewModel.StatusText = $"Copied {_viewModel.PositionText}";

        // Brief flash confirms the copy.
        PositionTextBlock.Foreground = (Brush)FindResource("AccentBrush");
        await Task.Delay(300);
        PositionTextBlock.ClearValue(TextBlock.ForegroundProperty);
    }
}
