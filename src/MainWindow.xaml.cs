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

namespace HandPegApp;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _isScrubbing;
    private bool _applyingMagnet;
    private bool _updatingFromPlayer;

    /// <summary>
    /// Looks for a newer HandPeg release in the background (Settings: Check for HandPeg Updates on Startup) and offers it
    /// in the status bar: found, for the Update button to download; or downloaded already, when Settings say to download
    /// in the background. Nothing waits on this, and a failed check (offline, no releases yet) only goes to the log.
    /// </summary>
    private async Task CheckForHandPegUpdateAsync()
    {
        if (!AppSettings.Current.CheckAppUpdatesOnStartup)
            return;

        if (await AppUpdater.CheckAtStartupAsync() is { } update)
            ShowUpdateNotice(update.Version, update.Downloaded);
    }

    // The update offered: its version, and whether it is downloaded already (then the button restarts into it).
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

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateOffered is not { } offered)
            return;

        if (offered.Downloaded)
        {
            RestartToUpdate();
            return;
        }

        // Download first, on the chip, then restart into it as Restart to Update does.
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

        // The updater waits for this process to end, so the window closes the way it always does.
        if (AppUpdater.BeginRestartIntoUpdate())
            Close();
        else
            _viewModel.StatusText = "The update could not be started. It will be tried again when HandPeg is closed.";
    }

    // Left for later: the update goes in when the application closes.
    private void DismissUpdateNotice_Click(object sender, RoutedEventArgs e) => UpdateNotice.Visibility = Visibility.Collapsed;

    // The player's HUD (5.4.4, MainWindow.Fullscreen.cs): the hover Fullscreen button and the fullscreen preview. Called by the constructor;
    // everything of it is made at first use, after the first frame.
    partial void InitializePlayerHud();

    public MainWindow()
    {
        StartupTrace.Mark("MainWindow: view model made, InitializeComponent");
        var building = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();

        // HandPeg's own UI scale before the first layout (5.1-Polish): the monitor's own pixels, unless Windows' scaling is
        // followed. The window keeps the size Windows gives it; its content is laid out at the scale.
        UiScale.SetKeepsItsSize(this, true);
        UiScale.BeforeFirstLayout(this);
        var built = building.ElapsedMilliseconds;
        StartupTrace.Mark("MainWindow: InitializeComponent done");
        DataContext = _viewModel;

        // Asked for even when already handled: the slider itself answers a press beside its playhead (it moves
        // the playhead there) and marks it handled, which would keep it from this window altogether.
        ApplyStartupWindowSize();
        // Asked for even when already handled: the window's own drag handlers mark these events handled
        // (that is how they refuse a drop over a text box), which would keep them from the overlay.
        AddHandler(PreviewDragEnterEvent, new DragEventHandler((_, e) =>
        {
            // Something is about to be dropped: the player starts now, before the drop, so that it is ready for it.
            WarmUpPlayer();
            ShowDropOverlay(e);
        }), handledEventsToo: true);
        AddHandler(PreviewDragOverEvent, new DragEventHandler((_, e) => ShowDropOverlay(e)), handledEventsToo: true);

        // A path or a link being typed is about to be loaded too.
        SourcePaneBox.GotKeyboardFocus += (_, _) => WarmUpPlayer();

        AddHandler(PreviewDragLeaveEvent, new DragEventHandler((_, e) => LeaveDropOverlay(e)), handledEventsToo: true);
        AddHandler(PreviewDropEvent, new DragEventHandler((_, _) => Dispatcher.BeginInvoke(HideDropOverlay)), handledEventsToo: true);

        _dropLeave.Tick += (_, _) => HideDropOverlay();
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

        // The Layout button, in the header: resting on it opens the Layout Selector (layouts and themes, live; Pass 4.1B), and so does a
        // click (5.4D: the one Layout UI, the click's menu retired). Escape, the pointer leaving it and the window deactivated close it. Resting
        // on the button opens nothing while a project tab is being switched: a layout previewed then would be put back over the new tab's.
        _layoutHover = new HoverFlyout([LayoutButton], LayoutSelector, LayoutSelectorPanel)
        {
            Opening = OpeningLayoutSelector,
            HoldsWhileFocused = true,
            CanOpen = () => !_switchingTab,
        };
        Deactivated += (_, _) => CloseLayoutSelectorOnDeactivated();
        _autosave.Tick += Autosave_Tick;
        _autosave.Start();

        // Ctrl+S and Ctrl+Shift+S are key bindings of the window (see the XAML) on commands of the view model, which ask here.
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

        // The player's surface and the drop hint as they are to be in the first frame (the XAML starts them so too).
        UpdateVideoVisibility();
        Application.Current.Resources["GlobalScrollBarThickness"] = Math.Clamp(AppSettings.Current.ScrollbarThickness, 4, 16);
        TimelineSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(TimelineSlider_PreviewMouseLeftButtonDown), handledEventsToo: true);

        // The first-run window may have been shown before this one, and WPF takes the first window made
        // as the main one.
        Application.Current.MainWindow = this;

        // The preview caches, kept to their sizes (in the background, a few seconds from now).
        PreviewCache.Timeline.QueueTrim();
        PreviewCache.Browser.QueueTrim();

        _viewModel.MarkersChanged += OnMarkersChanged;
        InitializePlaybackBar();
        InitializePlayerHud();
        InitializePlayerLayout();
        HookLoupe();
        HookLoopback();
        _viewModel.SessionCleared += OnSessionCleared;
        InitializeProjectWorkspace();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        // (The last file's Sequence Overview Scrubber pictures go before the new one is opened, which asks for its own.)
        _viewModel.MediaLoaded += _ => ClearProgramStrip();
        _viewModel.MediaLoaded += PlayMedia;

        // Unsaved changes count from the user's first touch after a load or a save (4.3E): a key, a click, the wheel or a
        // drop in any of HandPeg's windows (their popups and menus pass theirs up to them).
        RoutedEventHandler touched = (_, _) => _viewModel.NoteUserInput();
        foreach (var input in new[] { PreviewMouseDownEvent, PreviewKeyDownEvent, PreviewMouseWheelEvent, DragDrop.PreviewDropEvent })
            EventManager.RegisterClassHandler(typeof(Window), input, touched, handledEventsToo: true);
        InitializeDevFlight();

        // The media browser: what it opens, and what it puts on the timeline at the playhead (MainWindow.MediaBrowser.cs).
        InitializeMediaBrowser();
        InitializeClipLibrary();
        EffectsView.Attach(_viewModel);
        EffectsView.EditMaskRequested += layer => OpenLayerTool(layer, LayerTool.Mask);

        // The master meters and fader, the sound's cards' full dialog, the beat grid (5.2B, the Studio Audio Suite).
        InitializeAudioSuite();

        // The timeline (4.4): its surface, ruler, toolbar and inspector hooked up to the view model and this window.
        InitializeUnifiedTimeline();
        _viewModel.AskOverwrite = AskOverwrite;
        _viewModel.Confirm = (title, message, confirm) => new ConfirmDialog(title, message, confirm) { Owner = this }.ShowDialog() == true;

        // A 5.1 project opened here is converted and written over its own file (the harnesses open projects only to read them).
        _viewModel.SavesConvertedProjects = true;

        // A question that can be answered once for all (4.4): its "Remember my choice" box is kept with the answer.
        _viewModel.ConfirmOrRemember = (title, message, confirm, decline) =>
        {
            var dialog = new ConfirmDialog(title, message, confirm) { Owner = this, RememberText = "Remember my choice (don't ask again)", CancelText = decline };
            return (dialog.ShowDialog() == true, dialog.Remember);
        };

        // A tool that was never downloaded is offered at the moment something needs it.
        _viewModel.DependenciesNeeded = request => Task.FromResult(new DependencyPromptWindow(request) { Owner = this }.ShowDialog() == true);
        _viewModel.Choose = (title, message, first, second) =>
        {
            var dialog = new ConfirmDialog(title, message, first, second) { Owner = this };
            return dialog.ShowDialog() != true ? 0 : dialog.ChoseOther ? 2 : 1;
        };

        // Layers added or removed while they are being arranged change what is on the canvas.
        _viewModel.Layers.CollectionChanged += (_, _) => RedrawLayout();

        PreviewKeyDown += MainWindow_PreviewKeyDown;

        // The mouse buttons, the press that takes the keyboard from a text box and the keys held (X-Ray, Caps Lock), heard in
        // every window of the editor; their release (PreviewKeyUp) included (MainWindow.Keybinds.cs).
        InitializeKeybinds();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += (_, _) => _ = CheckForHandPegUpdateAsync();

        // What a Windows notification would have said while HandPeg was in front goes on the status line instead (5.0-Release).
        Notifier.Said += (title, message) => Dispatcher.BeginInvoke(() => _viewModel.StatusText = $"{title}: {message}");

        // The player's picture is made again after every change it is made from (the preview state, MainViewModel.InvalidatePreviewState).
        _viewModel.PreviewStateInvalidated += ScheduleGpuState;
        _viewModel.IsTimelineDragHeld = () => TimelineDragHeld;
        _viewModel.TimelineSelection = SelectedGroup;

        // The timeline's rows follow the clip views (Layers), and anything about one that changes.
        _viewModel.PreviewStateInvalidated += ScheduleClipRedraw;
        _viewModel.Layers.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.TimelineChanged += ScheduleClipRedraw;
        _viewModel.TimelineChanged += DrawKeyTimeline;

        // The Keyframes pane's rows: each property keyed, moved and deleted on its own.
        KeyLanes.LaneSelected += lane => _viewModel.SelectedKeyLane = lane.Target;
        KeyLanes.KeyToggleRequested += lane => _viewModel.ToggleLaneKey(_viewModel.ActiveKeyLayer, lane.Target);
        KeyLanes.KeyMoved += (lane, from, to) => _viewModel.MoveLaneKey(_viewModel.ActiveKeyLayer, lane.Target, from, to);
        KeyLanes.DeleteRequested += lane => _viewModel.DeleteLaneKey(_viewModel.ActiveKeyLayer, lane.Target);

        // Several keyframes at once (4.1.1-B): the pane's selection, moved, deleted, copied and pasted as one step each.
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

        // Edit layout may already be on when the window opens (it is to start with): the pane opens with it, saying nothing
        // (the title bar's middle stays empty until the user does something: 5.3-Stage3, the user's call).
        Loaded += (_, _) => UpdateLayoutPane(announce: false);
        SizeChanged += (_, _) => UpdateBottomBar();

        // A double-click on any slider or number box puts it back: to the style preset's value where there is one.
        NumericInput.ResetValue = _viewModel.GetResetValue;
        NumericInput.RegisterDoubleClickReset();

        // The player starts once there is a surface for it to draw on, away from the UI thread.
        // And not before the window has been drawn (_shellDrawn): the workspace is on screen first, and the
        // video canvas hooks in after it.
        VideoView.SurfaceReady += EnsureGpuEngine;

        // Once the window has been drawn, so the launch window opens over the program rather than over nothing.
        // (ContentRendered is raised once, when the window has actually been drawn; waiting for the dispatcher
        // to fall idle instead could be put off indefinitely by a player that is busy.)
        ContentRendered += (_, _) =>
        {
            ShowFirstFrame();
            StartupTrace.FirstFrame();

            // The crash sentinel starts once the window is idle (no process before the first frame).
            CrashReporter.FirstFrameShown(Dispatcher);

            // Explorer's project previews and open command follow their setting, off this thread (Pass 5.2D, 5.2-Release).
            ExplorerThumbnails.ApplyLater();

            // A project or bundle HandPeg was started with (a double-click in Explorer: ExplorerThumbnails' open command, 5.2-Release):
            // opened once the window is up, as a choice in the launch window is.
            if (SingleInstance.LaunchFile() is { } launched)
                Dispatcher.BeginInvoke(() => _ = OpenLaunchRequestAsync(new LaunchRequest(LaunchKind.Project, launched)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // And one double-clicked from now on, while this copy is open, is handed to it (5.2.0's re-cut, the user's answer).
            SingleInstance.Listen(path => Dispatcher.InvokeAsync(() => _ = OpenHandedProjectAsync(path)), _windowClosing.Token);

            // How long the launch took, for the log: from the process starting to the window being on screen.
            var sinceStart = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppLog.Write(FormattableString.Invariant($"Startup: window drawn {sinceStart:0} ms after the process started (window built in {built} ms)."));

            _shellDrawn = true;
            Dispatcher.BeginInvoke(EnsureGpuEngine, System.Windows.Threading.DispatcherPriority.Loaded);

            // What the timeline hides at the first frame (5.0, the user's Q5): its inspector, Text Settings and the tools'
            // flyouts, made just after the first frame (they start hidden, so nothing moves when they come). The row of tools
            // itself is made with the pane.
            Dispatcher.BeginInvoke(UnifiedTimeline.RealizeTools, System.Windows.Threading.DispatcherPriority.ContextIdle);

            // The heat lamp (5.4.3): what the pointer is about to open made ready while it rests near it, hooked once everything is up.
            Dispatcher.BeginInvoke(InitializeHeatLamp, System.Windows.Threading.DispatcherPriority.ApplicationIdle);


            // No window opens over the program at launch any more: it starts straight into its workspace. What
            // the launch window offered is offered while a file is dragged in instead, and its targets are made
            // now, once everything else is up and idle.
            Dispatcher.BeginInvoke(PrepareDropTargets, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // A minute in, when nothing else is starting: the caches let go of what was made from files deleted since.
            _ = PurgeCachesLaterAsync();

            // The watched capture folder (5.4.1-M5): its recordings' strips, waveforms and seek indexes made in quiet time, after the
            // index dwell (Settings' "Prepare the Whole Folder After"), so nothing of it competes with the start-up.
            if (AppSettings.Current.WatchedCaptureFolder.Trim().Length > 0)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.BrowserIndexDelaySeconds, 5, 3600)), _windowClosing.Token)
                    .ContinueWith(t => t.IsCompletedSuccessfully ? Views.MediaBrowserPane.PrepareWatchedFolderAsync(_windowClosing.Token) : Task.CompletedTask,
                        TaskScheduler.Default).Unwrap();
            }

            // Downloads beyond the cache size, temp folders of sessions that did not close cleanly, and the libmpv player an
            // earlier version installed (5.0-Verify): disk work, so not before the first frame (it was in the constructor, on
            // this thread).
            _ = WorkScheduler.Run(new WorkSpec("Tidying old sessions and downloads", WorkLane.Idle) { Visible = false, Cancellable = false }, _ => Task.Run(() =>
            {
                SessionPaths.DeleteAbandonedSessions();
                DependencyUpdater.DeleteRetiredPlayer();
                YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize,
                    AppSettings.Current.KeepDownloadsUsedByProjects ? YtDlpDownloader.FoldersUsedByProjects([ProjectStore.Folder], []) : null);
            }), _windowClosing.Token);

            // The built-in Game Presets (5.1-Release, the user's call): written at idle where the Styles folder has none of their
            // names, once per version, never over a file. Lists that read the folder show them from then on.
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
    /// Deletes from the caches what was made from files that have since been deleted or changed (see
    /// PreviewCache.PurgeMissing): a while after startup (Settings), as Idle work. Files on a drive that is not there
    /// now are kept, unless Settings says they count as deleted.
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

    /// <summary>Opens the launch window, whatever the settings say about showing it at startup: it is also where the Social Squisher is.</summary>
    private void OpenLaunchWindow(bool startHidden = false)
    {
        if (_launchWindow is not null)
        {
            _launchWindow.Activate();
            return;
        }

        // A soft dialog: it lies over this window without holding it, and a press anywhere on this window
        // (or a file dropped on it) puts it away, as choosing nothing in it would.
        var splash = _launchWindow = new SplashWindow
        {
            Owner = this,
            AvailableEncoders = () => _viewModel.VideoEncoders.Where(encoder => encoder.IsHardware).Select(encoder => encoder.Name).ToList(),
            Opacity = startHidden ? 0 : 1,
        };

        // Not while it is squishing a video, or holding the result: that is only closed with its own button.
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
    /// A project double-clicked while this copy was open (SingleInstance): the window comes to the front (restored from the taskbar), any
    /// dialog open is answered first, then it opens in a project tab of its own (5.4A: never in place of the open work, never a prompt),
    /// once nothing else is running.
    /// </summary>
    private async Task OpenHandedProjectAsync(string path)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != 0 && IsIconic(handle))
                _ = ShowWindow(handle, ShowWindowRestore);
            Activate();

            // A dialog of this window's up (Settings, a save prompt): the project waits for it to be closed.
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

    private const int ShowWindowRestore = 9;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    /// <summary>
    /// Opens what was chosen in the launch window, once nothing else is running: an operation asked for while
    /// the start-up checks are still busy would otherwise be dropped, and with it the style that was chosen.
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
                // 5.4D-TabPerf: a video handed over from Explorer names the tab before its probe, as a drop does.
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

        // A dark or a light title bar, to match the theme.
        ThemeManager.ApplyTitleBar(this);

        // The working area of the monitor the window is really on (SystemParameters.WorkArea is the main one's, at
        // the system's DPI): the window is kept inside it before it is first shown.
        KeepInside(WorkAreaOf(new WindowInteropHelper(this).Handle));

        // Before the first layout: the encoders an earlier launch found, so the tabs are drawn with the one they keep.
        _viewModel.TakeEarlyHardwareEncoders();
        StartupTrace.Mark("hardware encoders as found before: " + (_viewModel.VideoEncoder.Name));

        // Not on screen until its first frame is drawn: Windows would otherwise show the window empty (white) for the
        // moment WPF takes to draw it. Shown when it has been drawn (ContentRendered), or after a while whatever happens.
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

    /// <summary>The window, cloaked since its handle was made, is shown: its first frame is there to be seen.</summary>
    private void ShowFirstFrame()
    {
        if (_firstFrameShown)
            return;

        _firstFrameShown = true;
        ThemeManager.SetCloaked(this, false);
    }

    // Whether the save prompt of a close is up: a close asked for again meanwhile (the taskbar's Close, Alt+F4, a second
    // click on Close before the prompt came up) waits for its answer instead of asking a second time.
    private bool _closeAsked;

    /// <summary>
    /// Closing with changes that were not saved as a project asks first, when the settings say to: save and
    /// close, close without saving, or go back. The window stays only when the user says so (5.2-AuditB, the user's bug:
    /// the Close button did not always close HandPeg): Cancel in that prompt, or keeping it open when the project could not
    /// be saved. Nothing that fails on the way out keeps it open with no word of it.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // A project tab coming to the front (5.4A) is half in the view model and half in its cache file, and the tabs a dead session left are moved in at idle: never closed under either.
        if (_closeAsked || RefuseCloseMidSwitch())
        {
            e.Cancel = true;
            return;
        }

        // From here the user is closing HandPeg: ended from outside while the save prompt is up (the taskbar, Task
        // Manager), the crash sentinel takes it for the user's doing, not a crash. Going back to work clears it.
        Guarded("note the close", () => CrashReporter.MarkUserClosing(true));

        // The side window's place and size are kept with the arrangement. Set before the window's size is remembered,
        // which writes the settings file: the arrangement's own save waits half a second, and the app is gone by then.
        Guarded("keep the arrangement", () => _viewModel.SavePaneLayout(SaveDockInto));
        Guarded("remember the window's size", RememberWindowSize);

        bool close;
        try
        {
            _closeAsked = true;
            close = MayClose();

            // A clean close (5.4A): the project tabs' cache files go, so nothing is offered back at the next start.
            if (close)
                Guarded("let the project tabs' cache go", ForgetProjectTabs);
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
            return;
        }

        Guarded("empty the side window", _dock.Shutdown);
    }

    /// <summary>
    /// Whether the window may close: at once with nothing unsaved (or the prompt switched off in the settings); otherwise as the
    /// save prompt is answered. False only when the user chose to stay.
    /// </summary>
    private bool MayClose()
    {
        // Every project tab behind the one in front with unsaved changes first (5.4A, MainWindow.ProjectTabs.cs).
        if (!MayCloseProjectTabs())
            return false;

        var unsaved = _viewModel.HasUnsavedChanges;
        if (unsaved)
            AppLog.Write($"Closing with unsaved changes in {_viewModel.DescribeUnsavedChanges()}.");
        if (!AppSettings.Current.PromptToSaveOnExit || !unsaved)
            return true;

        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(_viewModel.LocalMediaPath);
        }
        catch (ArgumentException)
        {
            name = "";
        }

        var dialog = new SaveOnExitDialog(name.Length > 0 ? name : $"Project {DateTime.Now:yyyy-MM-dd HH.mm}") { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case ExitChoice.Cancel:
                return false;

            case ExitChoice.SaveAndClose:
                // A project that could not be saved must not be followed by losing what it was meant to keep without a word:
                // what went wrong is said, and closing all the same is the user's call (never a window that silently stays).
                var saved = _viewModel.SaveProject(dialog.ProjectName);
                return saved.StartsWith("Project saved", StringComparison.Ordinal)
                    || new ConfirmDialog("Project Not Saved", $"{saved}\n\nClose HandPeg anyway? The changes that were not saved are lost.", "Close Without Saving")
                    {
                        Owner = this, DefaultsToCancel = true, CancelText = "Keep HandPeg Open",
                    }.ShowDialog() == true;

            default:
                return true;
        }
    }

    /// <summary>
    /// The save prompt itself failed: asked once more, plainly (a Windows message box), whether to close without saving;
    /// when even that cannot be asked, HandPeg closes, as the user asked.
    /// </summary>
    private bool MayCloseAfterFault(Exception fault)
    {
        try
        {
            if (!_viewModel.HasUnsavedChanges)
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

    /// <summary>One step of closing, which may fail without stopping the rest: what failed is written to the log.</summary>
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

        // Encodes, downloads and probes still running go first, children included, so nothing is left
        // behind in the background and nothing still holds a file in the session folder. Each step on its own: one that
        // fails leaves the others to run.
        Guarded("cancel the window's work", _windowClosing.Cancel);
        Guarded("let projects double-clicked from now on start a copy of their own", SingleInstance.Release);
        Guarded("end the tools still running", ProcessPipes.KillAll);
        Guarded("shut the view model down", _viewModel.Shutdown);
        Guarded("stop following the player", () => FollowPlayerByFrame(false));
        Guarded("stop the GPU retry", _gpuRetry.Stop);
        Guarded("close the GPU engine", CloseGpuEngine);
        Guarded("close the Sequence Overview Scrubber's drawer", () => _programStrip?.Dispose());

        // The most recent downloads stay, so the same URL loads without downloading again.
        Guarded("trim the downloads", () => YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize, _viewModel.PinnedDownloads()));

        // The main window gone, HandPeg is gone, whatever other window is still about (one never shown, one not owned by this
        // one): the application's ShutdownMode is OnLastWindowClose unless the first-run window ran.
        Application.Current?.Shutdown();
    }

    // ----- Hotkeys -----

    // When a layout box was last nudged with the arrow keys: a run of nudges is one undo step.
    private long _nudgedAt;

    // The text box last typed into, and the undo history as it stood then (MainViewModel.UndoMark): while no step of the
    // timeline's has been taken, undone or redone since, Ctrl+Z and Ctrl+Y there take back and put back that typing.
    private (TextBoxBase Box, object? Mark)? _typedIn;

    /// <summary>
    /// Null for any other key; false for undo (Ctrl+Z), true for redo (Ctrl+Y, or Ctrl+Shift+Z). These stay fixed whatever
    /// the keybinds (KeybindManager never gives them to anything else).
    /// </summary>
    private static bool? UndoKey(KeyEventArgs e) =>
        KeybindManager.IsUndo(e) ? false : KeybindManager.IsRedo(e) ? true : (bool?)null;

    /// <summary>
    /// Whether Ctrl+Z (or Ctrl+Y) in the focused text box is the box's own: while it has typing to take back (or put back),
    /// it was the box last typed into, and the timeline has taken no step since (a value typed and handed on with Enter, or
    /// by leaving the box, takes one when it changes the timeline: that change is then the timeline's to take back).
    /// </summary>
    private bool TypingIsNewest(TextBoxBase box, bool redo) =>
        (redo ? box.CanRedo : box.CanUndo) && _typedIn is { } typed && ReferenceEquals(typed.Box, box) && ReferenceEquals(typed.Mark, _viewModel.UndoMark);

    /// <summary>Whether a key pressed in a text box types into it (a character, Back, Delete, Enter, Ctrl+V, Ctrl+X), not only moves about in it or selects.</summary>
    private static bool TypesText(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var (control, alt) = (modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Alt));
        if (control && !alt)
            return key is Key.V or Key.X or Key.Back or Key.Delete;

        // Alt alone is a menu's key; Ctrl+Alt together is AltGr, which types a character on many keyboards.
        if (alt != control || modifiers.HasFlag(ModifierKeys.Windows))
            return false;

        return key is Key.Back or Key.Delete or Key.Enter or Key.Space or Key.ImeProcessed or Key.DeadCharProcessed
            or (>= Key.D0 and <= Key.Z) or (>= Key.NumPad0 and <= Key.Divide) or (>= Key.Oem1 and <= Key.OemBackslash);
    }

    /// <summary>
    /// Every global key (4.4): the dial's and the tour's Esc (and Caps Lock's echo) first, then saving, undo and the text
    /// box's keys, the nudge of a Canvas box, and every other shortcut through KeybindManager, the held navigation keys among
    /// them (MainWindow.Keybinds.cs holds what each action does). The side window hands its keys here too.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TakeOverlayKey(e))
            return;

        var chord = KeybindManager.ChordOf(e);
        var bound = KeybindManager.Resolve(chord);

        // Shift with a held navigation key is that key going faster, unless Shift and the key are bound themselves.
        if (bound is null && chord.Mods == ModifierKeys.Shift && KeybindManager.Resolve(chord.WithoutShift) is { } faster
            && KeybindManager.Action(faster) is { } held && held.Has(KeyActionFlags.Continuous))
            bound = faster;

        // Saving, before anything else sees the keys: a text box, which has uses of its own for some key
        // combinations, never gets these (Ctrl+S and Ctrl+Alt+S unless they were rebound).
        //
        // Save As is Ctrl+Alt+S, not Ctrl+Shift+S: that one is taken, on computers with AMD's graphics
        // software, by its "save instant replay" hotkey, which is registered with Windows for every program,
        // so the keys are never given to HandPeg at all; KeybindManager refuses to bind it. (With Alt held, WPF
        // reports the key as a system key: KeybindManager.ChordOf reads the key itself.)
        if (bound is not null && KeybindManager.Action(bound) is { } early && early.Has(KeyActionFlags.WorksInText))
        {
            if (!e.IsRepeat)
                RunAction(bound);
            e.Handled = true;
            return;
        }

        var focused = Keyboard.FocusedElement;

        // Undo (Ctrl+Z) and redo (Ctrl+Y or Ctrl+Shift+Z), wherever the keyboard is but in an open drop-down (4.3E): the
        // focused text box's own while what was done last is typing in it (TypingIsNewest), the timeline's otherwise, so a
        // number box or a search box left holding the keyboard no longer swallows them. Not while a drag is held: what is
        // dragged would go on being moved from where Undo put it (RunUndo).
        if (UndoKey(e) is { } redo && focused is not ComboBoxItem)
        {
            RunUndo(redo);
            e.Handled = true;
            return;
        }

        // A key that types into a text box: the box holds typing newer than the timeline's last step.
        if (focused is TextBoxBase typedInto && TypesText(e))
            _typedIn = (typedInto, _viewModel.UndoMark);

        // Typing stays typing, and a drop-down keeps its own keys. (A press anywhere else takes the keyboard from a text box,
        // MainWindow.Keybinds.cs ReleaseTypingFocus, so the keys after it are shortcuts again.)
        if (focused is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
            return;

        if (EndCropOnEscape(e, focused))
            return;

        // A layout box chosen with the pointer: the arrow keys nudge it, a pixel of the output (ten with Shift). One undo
        // step for a run of nudges.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift && TakeNudge(e.Key, focused, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        // Every other shortcut (KeybindManager's map: the preset and the user's own keys), unless the control with the
        // keyboard keeps that key or the action cannot run now (MainWindow.Keybinds.cs).
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
            // In the order of the view model's Containers (5.4C: the audio-only FLAC, WAV and MP3 last), so FilterIndex finds the format.
            Filter = "MP4|*.mp4|Matroska|*.mkv|WebM|*.webm|QuickTime|*.mov|Animated GIF|*.gif|Animated WebP|*.webp|FLAC Audio|*.flac|WAV Audio|*.wav|MP3 Audio|*.mp3|All files|*.*",
            FilterIndex = Math.Max(1, _viewModel.Containers.ToList().IndexOf(_viewModel.Container) + 1),
            FileName = Path.GetFileName(_viewModel.DestinationPath),
            DefaultExt = _viewModel.Container,
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.DestinationPath = dialog.FileName;
    }

    private async void OpenProjects_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProjectManagerWindow(_viewModel) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        // In the project tabs (5.4A): a New Project or a project opened in a tab of its own, never in place of the open work.
        await ActOnProjectManagerAsync(dialog);
    }

    // The session emptied (+ New Project): the player lets its file go, and the drop hint is back where the video was.
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
    /// Split / Trim by Exact Timecode (a timeline row's right-click menu, and the toolbar's button for the selected clip):
    /// a typed time, with the I-frames of the clip's file either side of it offered; then a split or a trim there.
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

        // The box starts at the playhead when it is on the clip, else at the clip's nearer end (Pass 4.2C).
        var at = Math.Clamp(_viewModel.PositionMs / 1000, start, Math.Max(start, end));
        var dialog = new ManualCutWindow(clip.Name, start, end, _viewModel.SourceFrameRate, iFrames, at) { Owner = this };
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

    // The toolbar's button: the selected clip, or else the video at the playhead.
    private void ManualCut_Click(object sender, RoutedEventArgs e) => SplitTrimByTimecode(_selectedClip switch
    {
        Layer layer when _viewModel.Layers.Contains(layer) && layer.HasTiming => layer,
        LayerSound sound when _viewModel.Layers.Contains(sound.Layer) => sound.Layer,
        _ => _viewModel.PlayheadVideo,
    });

    /// <summary>
    /// Remove Dead Air: first how quiet counts as silence (and whether to mark or delete),
    /// then the search, on whatever is selected: a clip with sound, or otherwise the sound listened to (MainViewModel.ListenedSound).
    /// </summary>
    private void RemoveDeadAir_Click(object sender, RoutedEventArgs e) => RunDeadAir(_selectedClip switch
    {
        Layer { CarriesSound: true } layer => layer,
        LayerSound sound => sound.Layer,
        _ => null,
    });

    private void DropHint_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => BrowseSource_Click(sender, e);

    // ----- The header's flyouts (Add Media's box, the Settings button's queue and squisher, the Layout Selector) -----
    // Each opens while the pointer rests on its button and stays while it is on the button or on it (see HoverFlyout).
    // (Add Layer's kinds are the timeline's own flyout since 4.4: Views/UnifiedTimelinePane.)

    private readonly HoverFlyout _addMediaHover, _settingsHover, _layoutHover;

    // ----- The window's size at startup -----

    /// <summary>
    /// The size and place the window opens at (Pass 4.2B-2): never larger than the working area beside the taskbar,
    /// centred on it and never above its top, so the title bar (the only way to move the window) is always on screen.
    /// A 16" 1920 x 1200 laptop at 150 % has 1280 x 752 of it, less than the window's least size in the XAML: the
    /// least size gives way. A size that fills nearly all of it (95 %) opens maximized, which is what it was for.
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

    /// <summary>Not maximized, the window wholly inside a working area (in this window's DIPs): its top never above the area's.</summary>
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
    /// Notes how the window was left, for "Remember Last Size": its size while not maximized, and whether it was. Then
    /// writes the settings, with whatever was set on the way out (the arrangement of the panes).
    /// </summary>
    private void RememberWindowSize()
    {
        var settings = AppSettings.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width >= MinWidth && bounds.Height >= MinHeight)
            (settings.LastWindowWidth, settings.LastWindowHeight, settings.LastWindowMaximized) = (Math.Round(bounds.Width), Math.Round(bounds.Height), WindowState == WindowState.Maximized);

        try
        {
            settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered this time.
        }
    }

    // ----- Fitting -----

    private void FlyoutSquisher_Click(object sender, RoutedEventArgs e)
    {
        _settingsHover.Close();
        OpenLaunchWindow();
    }

    // 5.4D (Q12 b): the Game Presets' save, import and export, reachable from the Settings flyout even with nothing on the timeline.
    private void FlyoutGamePresets_Click(object sender, RoutedEventArgs e)
    {
        _settingsHover.Close();
        ManageStyles_Click(UnifiedTimeline, e);
    }

    // ----- Add Media (the header's): a click picks files; resting on it opens the box for a path or a link -----
    // The Settings button's pattern (HoverFlyout): the flyout opens once the pointer has rested on the button, and
    // leaving the button or the flyout closes it a moment later. While its box has the keyboard (something is being
    // typed or pasted) it stays.

    /// <summary>Picks files to add: with nothing open, the first is opened (or the project), onto Track 1; otherwise every one goes onto the timeline at the playhead.</summary>
    private void AddMedia_Click(object sender, RoutedEventArgs e)
    {
        _addMediaHover.Close();
        WarmUpPlayer();
        static string Patterns(IEnumerable<string> extensions) => string.Join(";", extensions.Select(x => "*" + x));
        var all = MediaBrowserItem.VideoExtensions.Concat(MediaBrowserItem.AudioExtensions).Concat(MediaBrowserItem.ImageExtensions);
        var dialog = new OpenFileDialog
        {
            Title = _viewModel.HasSource ? "Add media to the timeline" : "Open a video",
            Multiselect = _viewModel.HasSource,
            Filter = $"Media files|{Patterns(all)}|Videos|{Patterns(MediaBrowserItem.VideoExtensions)}|Sounds|{Patterns(MediaBrowserItem.AudioExtensions)}"
                + $"|Pictures|{Patterns(MediaBrowserItem.ImageExtensions)}|HandPeg projects and bundles|*{ProjectStore.Extension};*{ProjectBundle.Extension}|All files|*.*",
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

    /// <summary>Files put onto the timeline at a moment: sounds as audio clips, videos and pictures as layers (placed by PlaceClip).</summary>
    private void AddFilesToTimeline(string[] files, double at)
    {
        var sounds = files.Where(f => MediaBrowserItem.KindOf(f) == MediaBrowserKind.Audio).ToArray();
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

    // Opened: the player is started, as it is when the box gets the keyboard, so a link pasted in plays at once.
    private void AddMediaFlyout_Opened(object? sender, EventArgs e) => WarmUpPlayer();

    /// <summary>
    /// Where a header flyout opens: under its button, a few pixels down so that its window never comes up under the
    /// pointer, its right edge at the button's (the header sits at the window's right); beside it, towards the window's
    /// middle, while the header stands as a strip at a side. One placement in device pixels for all of them
    /// (UiScale.PlaceAgainst, 5.3-Stage3): never mirrored on a touch PC, and kept where it opened while the panes move.
    /// </summary>
    private void PlaceHeaderFlyout(Popup popup, FrameworkElement host, FrameworkElement? within = null, double inset = 0) =>
        UiScale.PlaceAgainst(popup, host, HeaderFlyoutSide(host), HeaderFlyoutGap, within, inset);

    // How far from its button a header flyout opens, in DIPs.
    private const double HeaderFlyoutGap = 6;

    /// <summary>A header flyout's side of its button: under it, or beside it towards the window's middle while the header is a strip.</summary>
    private UiScale.FlyoutSide HeaderFlyoutSide(FrameworkElement host)
    {
        if (_headerShape != Docking.PaneShape.Column)
            return UiScale.FlyoutSide.BelowEnd;

        var window = Window.GetWindow(host) ?? this;
        var onRight = host.TranslatePoint(new Point(host.ActualWidth / 2, 0), window).X > window.ActualWidth / 2;
        return onRight ? UiScale.FlyoutSide.Left : UiScale.FlyoutSide.Right;
    }

    // ----- The progress chip (bottom left) and its flyout -----
    // The Settings button's pattern: the flyout opens while the pointer rests on the chip, and leaving the chip or the
    // flyout closes it a moment later, so the pointer can cross from one to the other.

    private readonly System.Windows.Threading.DispatcherTimer _workFlyoutClose = new() { Interval = TimeSpan.FromMilliseconds(260) };

    // From the status in the title bar (the one home of the status and the work under way: the transport row's chip went in 5.4.2A), under it.
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

    // ----- Saving: Ctrl+S, Ctrl+Shift+S, and the autosave -----

    /// <summary>Save: to the file the project already has; without one, Save As.</summary>
    /// <summary>Save As and a first save: HandPeg's own Project Manager, its name box in hand (the user's directive), not the system's file picker.</summary>
    private void OpenProjectManagerToSave()
    {
        var dialog = new ProjectManagerWindow(_viewModel, focusName: true) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        // In the project tabs (5.4A), as from the Header's Projects button.
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
    /// Save As: asks where, with the Windows file dialog. Neither kind of save touches the undo history. Its second file type
    /// is the project bundle (5.0-Release: the project with every file it reads, in one .hpb), which <paramref name="bundle"/>
    /// picks first.
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

    // Looked at every twenty seconds: when autosave is on and its interval has passed, the project is written (if it has changed).
    private void Autosave_Tick(object? sender, EventArgs e)
    {
        var settings = AppSettings.Current;
        if (!settings.AutosaveEnabled || DateTime.UtcNow - _lastAutosave < TimeSpan.FromMinutes(Math.Clamp(settings.AutosaveMinutes, 1, 120)))
            return;

        _lastAutosave = DateTime.UtcNow;
        _ = _viewModel.AutosaveAsync();
    }

    private void OpenQueue_Click(object sender, RoutedEventArgs e) => OpenRenderQueue();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// The Settings window, at one of its tabs when named (a pane's "More ... Settings...", 4.2B-2): a full-window screen
    /// over the editor, in its place on the screen and maximized when it is (4.3-0B), its stage mirroring the editor's
    /// panes live (StageMirror, 4.4).
    /// </summary>
    /// <param name="prepare">Anything else to do to the window before it is shown (open it at a key, 4.4.0).</param>
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

            // The Timeline's switches there (5.3: its effect lanes, filmstrips, waveforms, gain lines...) are read as its rows are made.
            ScheduleClipRedraw();

            if (_dock.IsRealized(ExportId))
                FillExportSection();

            // Explorer's project previews registered or taken out when that was switched (Pass 5.2D).
            if (AppSettings.Current.ShowExplorerThumbnails != explorerThumbnails)
                ExplorerThumbnails.ApplyLater();
        }

        // Crash reports switched on or off there (or in the first-run window it can open): the sentinel follows.
        CrashReporter.FollowSetting();

        // Check for Updates, in that window, may have found (or brought in) a release: it is offered here as one found at startup is.
        if (AppUpdater.ReadyVersion is { } update)
            ShowUpdateNotice(update, downloaded: true);
        else if (AppUpdater.FoundVersion is { } found)
            ShowUpdateNotice(found, downloaded: false);

        // FFmpeg may just have been installed from the dependency list: the player starts if it had been waiting for something to show.
        EnsureGpuEngine();
    }


    // The Source pane's one button, and Enter in its box: with the box empty there is nothing to load, and
    // it browses for a file; with a path or a URL in it, it loads that.
    private void SourceAction_Click(object sender, RoutedEventArgs e)
    {
        var path = (_viewModel.SourcePath ?? "").Trim().Trim('"');
        _addMediaHover.Close();
        if (path.Length == 0)
            BrowseSource_Click(sender, e);
        else if (ProjectStore.IsProjectFile(path))
            _ = OpenProjectInTabAsync(path);   // A project file (or bundle) in the Source box opens the project (5.4A: in a project tab).
        else
            _viewModel.LoadSourceCommand.Execute(null);
    }

    // The command text is written as the settings change only while its box is on screen (see MainViewModel.CommandPreview).
    private void CommandPreviewBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _viewModel.CommandPreviewWatched = e.NewValue is true;

    private void SourcePaneBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SourceAction_Click(sender, e);
    }

    // ----- What the view model's changes redraw -----

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The markers' flags along the Sequence Overview Scrubber, at its new length.
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
            MoveClipPlayheads();
            KeyLanes.MovePlayhead(_viewModel.PositionMs / 1000);

            // Which rows have a keyframe at the playhead (their names' diamonds) is read again, while not playing.
            if (!_viewModel.IsPlaying)
                KeyLanes.InvalidateVisual();
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
        // Never mid-switch (5.4D-TabPerf review, P12): the tab put behind has let the engine go already (GpuPreviewEngine.Suspend, which
        // keeps the frames of every tab kept live), and an Unload would clear them all.
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

        // The source view (Interactive Crop or a Draw Target) shows the selected clip's picture (MainViewModel.ShownSourceClip).
        if (e.PropertyName == nameof(MainViewModel.KeyLayer) && CurrentOverlayMode != OverlayMode.None)
            ScheduleGpuState();

        if (e.PropertyName == nameof(MainViewModel.IsArrangeActive))
            UpdateLayoutPane();

        // Which layers there are to show in the layout pane.
        if (e.PropertyName is nameof(MainViewModel.AutoCaptions))
            RedrawLayout();

        // What is drawn over the video, and the live crop of the video itself, follow the numbers
        // whether they came from dragging or from typing.
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

    // The markers' flags along the Sequence Overview Scrubber are laid out again at its new width.
    private void MarkerCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTimelineMarkers();

    // The layout pane's stage: the output frame.
    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The time, in milliseconds, at a horizontal position on the timeline.</summary>
    private double TimelineMsAt(double x)
    {
        // The track is shorter than the slider by half a thumb at each end.
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
    /// Puts the player on the I-frame before or after the playhead. The time comes from the I-frame
    /// index; setting the position moves the timeline, which seeks the player to exactly that time.
    /// </summary>
    private void JumpToIFrame(int direction)
    {
        // Like stepping, jumping is for looking at a still picture.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        _viewModel.SeekToIFrame(direction);
    }

    private void StepFrames(int frames)
    {
        // Stepping only makes sense on a still picture.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        _viewModel.StepFrames(frames);
    }

    private async void PositionText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.PositionText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program is holding the clipboard open; nothing was copied.
            _viewModel.StatusText = "The clipboard is busy. Try again.";
            return;
        }

        _viewModel.StatusText = $"Copied {_viewModel.PositionText}";

        // A brief flash confirms the copy.
        PositionTextBlock.Foreground = (Brush)FindResource("AccentBrush");
        await Task.Delay(300);
        PositionTextBlock.ClearValue(TextBlock.ForegroundProperty);
    }
}
