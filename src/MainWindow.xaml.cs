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
    private QueueWindow? _queueWindow;
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
            : "Downloads the new version (the progress shows at the bottom left), then closes HandPeg, puts it in and starts it again.";
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

    public MainWindow()
    {
        StartupTrace.Mark("MainWindow: view model made, InitializeComponent");
        var building = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
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

        // Proxies wait while something heavy goes on: a drag that holds the mouse (scrubbing, a clip, an edge, a box, a
        // slider) or a shuttle. Ordinary moves, clicks and keys do not count, or a project being looked around in would
        // never get its proxies.
        PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && Mouse.Captured is not null || _isScrubbing || _shuttle != 0)
                _viewModel.NoteActivity();
        };
        AddHandler(PreviewDragLeaveEvent, new DragEventHandler((_, e) => LeaveDropOverlay(e)), handledEventsToo: true);
        AddHandler(PreviewDropEvent, new DragEventHandler((_, _) => Dispatcher.BeginInvoke(HideDropOverlay)), handledEventsToo: true);
        _scrubPace.Tick += ScrubPace_Tick;

        _dropLeave.Tick += (_, _) => HideDropOverlay();
        TransportRow.Fitting = FitTransport;
        _addMediaHover = new HoverFlyout(AddMediaHost, AddMediaFlyout, AddMediaFlyoutPanel) { HoldsWhileFocused = true, Opening = () => PlaceHeaderFlyout(AddMediaFlyout, AddMediaHost, AddMediaFlyoutPanel.Width) };
        _settingsHover = new HoverFlyout(SettingsHost, SettingsFlyout, SettingsFlyoutPanel)
        {
            Opening = () =>
            {
                _viewModel.RefreshAutosave();
                PlaceHeaderFlyout(SettingsFlyout, SettingsHost, 150 + SettingsHost.ActualWidth);
            },
        };

        // The Layout button, in the header: resting on it opens the Layout Selector (layouts and themes, live); a click
        // opens its menu (Pass 4.1B).
        _layoutHover = new HoverFlyout([LayoutButton], LayoutSelector, LayoutSelectorPanel)
        {
            Opening = OpeningLayoutSelector,
            HoldsWhileFocused = true,
            CanOpen = () => !LayoutFlyout.IsOpen,
        };
        PreviewMouseDown += CloseLayoutMenuOnPress;
        Deactivated += (_, _) => CloseLayoutMenu();
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
        InitializePlayerLayout();
        _viewModel.SessionCleared += OnSessionCleared;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.MediaLoaded += PlayMedia;

        // Unsaved changes count from the user's first touch after a load or a save (4.3E): a key, a click, the wheel or a
        // drop in any of HandPeg's windows (their popups and menus pass theirs up to them).
        RoutedEventHandler touched = (_, _) => _viewModel.NoteUserInput();
        foreach (var input in new[] { PreviewMouseDownEvent, PreviewKeyDownEvent, PreviewMouseWheelEvent, DragDrop.PreviewDropEvent })
            EventManager.RegisterClassHandler(typeof(Window), input, touched, handledEventsToo: true);
        InitializeDevFlight();

        // The media browser: what it opens, and what it puts on the timeline at the playhead.
        MediaBrowser.HasVideo = () => _viewModel.HasSource;
        MediaBrowser.SuggestFolder = () => _viewModel.HasSource ? Path.GetDirectoryName(_viewModel.LocalMediaPath) : null;
        MediaBrowser.LoadRequested += path => LoadDroppedFile(path);
        MediaBrowser.ProjectFiles = _viewModel.MediaFilesInUse;
        _viewModel.Layers.CollectionChanged += (_, _) => MediaBrowser.ProjectChanged();
        EffectsView.Attach(_viewModel);
        EffectsView.EditMaskRequested += layer => OpenLayerTool(layer, LayerTool.Mask);

        // The timeline (4.4): its surface, ruler, toolbar and inspector hooked up to the view model and this window.
        InitializeUnifiedTimeline();
        _viewModel.MediaLoaded += _ => MediaBrowser.ProjectChanged();
        MediaBrowser.AddRequested += (files, sound) =>
        {
            var at = _viewModel.PositionMs / 1000;
            _ = sound ? AddDroppedAudioAsync([.. files], at) : AddDroppedLayersAsync([.. files], at, null);
        };
        _viewModel.PreviewRendered += ShowPreview;
        _viewModel.AskOverwrite = AskOverwrite;
        _viewModel.Confirm = (title, message, confirm) => new ConfirmDialog(title, message, confirm) { Owner = this }.ShowDialog() == true;

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

        _viewModel.LiveFilterInvalidated += () => _liveStateVersion++;
        _viewModel.LiveFilterInvalidated += ScheduleLiveFilter;
        _viewModel.LiveFilterInvalidated += () => _liveSoundRefused = false;

        // The timeline's rows follow the layers, and anything about a layer that changes (the main videos clips are layers: 4.3C).
        _viewModel.LiveFilterInvalidated += ScheduleClipRedraw;
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

        // The Summary cards, in the order they were last dragged into.
        Loaded += (_, _) => ApplySummaryCardOrder();
        ReorderBehavior.Reordered += panel =>
        {
            if (ReferenceEquals(panel, SummaryCards))
                SaveSummaryCardOrder();
        };
        Loaded += (_, _) => UpdateBottomBar();
        _viewModel.ActionLogged += FlashStatus;
        _viewModel.LayerLoaded += RedrawLayout;
        _viewModel.LayerVisibilityChanged += RedrawLayout;

        // Edit layout may already be on when the window opens (it is to start with): the pane opens with it.
        Loaded += (_, _) => UpdateLayoutPane();
        SizeChanged += (_, _) => UpdateBottomBar();

        // A double-click on any slider or number box puts it back: to the style preset's value where there is one.
        NumericInput.ResetValue = _viewModel.GetResetValue;
        NumericInput.RegisterDoubleClickReset();
        VideoView.SizeChanged += (_, _) => ScheduleLiveFilter();
        _liveFilterTimer.Tick += (_, _) => ApplyLiveFilter();
        _shuttleTimer.Tick += (_, _) => ShuttleBack();

        // The player starts once there is a surface for it to draw on, away from the UI thread.
        // And not before the window has been drawn (_shellDrawn): the workspace is on screen first, and the
        // video canvas hooks in after it.
        VideoView.SurfaceReady += () => _ = EnsurePlayerAsync();

        // Once the window has been drawn, so the launch window opens over the program rather than over nothing.
        // (ContentRendered is raised once, when the window has actually been drawn; waiting for the dispatcher
        // to fall idle instead could be put off indefinitely by a player that is busy.)
        ContentRendered += (_, _) =>
        {
            ShowFirstFrame();
            StartupTrace.FirstFrame();

            // The crash sentinel starts once the window is idle (no process before the first frame).
            CrashReporter.FirstFrameShown(Dispatcher);

            // How long the launch took, for the log: from the process starting to the window being on screen.
            var sinceStart = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppLog.Write(FormattableString.Invariant($"Startup: window drawn {sinceStart:0} ms after the process started (window built in {built} ms)."));

            _shellDrawn = true;
            Dispatcher.BeginInvoke(() => _ = EnsurePlayerAsync(), System.Windows.Threading.DispatcherPriority.Loaded);

            StyleLibrary.EnsureBuiltIns();

            // No window opens over the program at launch any more: it starts straight into its workspace. What
            // the launch window offered is offered while a file is dragged in instead, and its targets are made
            // now, once everything else is up and idle.
            Dispatcher.BeginInvoke(PrepareDropTargets, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // A minute in, when nothing else is starting: the caches let go of what was made from files deleted since.
            _ = PurgeCachesLaterAsync();

            // Downloads beyond the cache size, and temp folders of sessions that did not close cleanly: disk work, so not
            // before the first frame (it was in the constructor, on this thread).
            _ = WorkScheduler.Run(new WorkSpec("Tidying old sessions and downloads", WorkLane.Idle) { Visible = false, Cancellable = false }, _ => Task.Run(() =>
            {
                SessionPaths.DeleteAbandonedSessions();
                YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize,
                    AppSettings.Current.KeepDownloadsUsedByProjects ? YtDlpDownloader.FoldersUsedByProjects([ProjectStore.Folder], []) : null);
            }), _windowClosing.Token);
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
                _viewModel.LoadFile(request.Path);
                break;

            case LaunchKind.Project:
                _ = _viewModel.LoadProjectAsync(request.Path);
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

    /// <summary>
    /// Closing with changes that were not saved as a project asks first, when the settings say to: save and
    /// close, close without saving, or go back.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // From here the user is closing HandPeg: ended from outside while the save prompt is up (the taskbar, Task
        // Manager), the crash sentinel takes it for the user's doing, not a crash. Going back to work clears it.
        CrashReporter.MarkUserClosing(true);

        // The side window's place and size are kept with the arrangement. Set before the window's size is remembered,
        // which writes the settings file: the arrangement's own save waits half a second, and the app is gone by then.
        _viewModel.SavePaneLayout(s => s.DockLayout = _dock.Save());
        RememberWindowSize();
        var unsaved = _viewModel.HasUnsavedChanges;
        if (unsaved)
            AppLog.Write($"Closing with unsaved changes in {_viewModel.DescribeUnsavedChanges()}.");
        if (!AppSettings.Current.PromptToSaveOnExit || !unsaved)
        {
            _dock.Shutdown();
            return;
        }

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
                e.Cancel = true;
                break;

            // A project that could not be saved must not be followed by losing what it was meant to keep.
            case ExitChoice.SaveAndClose when !_viewModel.SaveProject(dialog.ProjectName).StartsWith("Project saved", StringComparison.Ordinal):
                e.Cancel = true;
                break;
        }

        if (e.Cancel)
            CrashReporter.MarkUserClosing(false);
        else
            _dock.Shutdown();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        CrashReporter.Note("app", "window closed");

        // Encodes, downloads and probes still running go first, children included, so nothing is left
        // behind in the background and nothing still holds a file in the session folder.
        _windowClosing.Cancel();
        ProcessPipes.KillAll();
        _viewModel.Shutdown();
        FollowPlayerByFrame(false);
        _mpv?.Close();

        // The most recent downloads stay, so the same URL loads without downloading again.
        YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize, _viewModel.PinnedDownloads());
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

        // A layout box chosen with the pointer: the arrow keys nudge it, a pixel of the output (ten with Shift). One undo
        // step for a run of nudges.
        if (focused is Thumb { Tag: LayoutNudge nudge } && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var step = Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1;
            if (Environment.TickCount64 - _nudgedAt > 1000)
                _viewModel.Checkpoint($"nudge {nudge.Name}");
            _nudgedAt = Environment.TickCount64;
            nudge.Move(e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 }, e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 });
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
            Filter = "MP4|*.mp4|Matroska|*.mkv|WebM|*.webm|QuickTime|*.mov|Animated GIF|*.gif|Animated WebP|*.webp|All files|*.*",
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

        if (dialog.NewProjectRequested)
            StartNewProject();
        else if (dialog.ProjectToLoad is { } project)
            await _viewModel.LoadProjectAsync(project);
    }

    /// <summary>+ New Project: unsaved changes are saved or let go first, as the user says; then the session is emptied.</summary>
    private void StartNewProject()
    {
        if (_viewModel.HasUnsavedChanges)
        {
            var name = _viewModel.CurrentProjectPath.Length > 0 ? Path.GetFileNameWithoutExtension(_viewModel.CurrentProjectPath)
                : _viewModel.HasSource ? Path.GetFileNameWithoutExtension(_viewModel.LocalMediaPath) : "";
            var ask = new ConfirmDialog("New Project",
                "What is open has changes that were not saved as a project.\n\nSave them first, or let them go?", "Save, then Start New", "Discard and Start New") { Owner = this };
            if (ask.ShowDialog() != true)
                return;

            // A project that could not be saved must not be followed by losing what it was meant to keep.
            if (!ask.ChoseOther && !_viewModel.SaveProject(name.Length > 0 ? name : $"Project {DateTime.Now:yyyy-MM-dd HH.mm}").StartsWith("Project saved", StringComparison.Ordinal))
                return;
        }

        _viewModel.NewProject();
    }

    // The session emptied (+ New Project): the player lets its file go, and the drop hint is back where the video was.
    private void OnSessionCleared()
    {
        _mpv?.DropFilterGraph();
        _mpv?.Stop();
        UpdateVideoVisibility();
        RedrawLayout();
        RedrawPlayerLayout();
    }

    /// <summary>
    /// Split / Trim by Exact Timecode (a timeline row's right-click menu, and the toolbar's button for the selected clip):
    /// a typed time, with the I-frames of the clip's file either side of it offered; then a split or a trim there.
    /// A clip of the main video is a clip like any other (its I-frames are the main video's, laid along the timeline).
    /// </summary>
    private void SplitTrimByTimecode(Layer clip)
    {
        double start, end;
        var iFrames = new List<double>();
        if (clip.IsMainVideo)
        {
            if (_viewModel.ClipOf(clip) is not { } model)
                return;
            (start, end) = (model.Start, model.End);
            iFrames.AddRange(_viewModel.IFrames.Where(key => key >= start && key <= end));
        }
        else
        {
            var speed = clip.Speed > 0 ? clip.Speed : 1;
            (start, end) = (clip.StartTime, clip.Duration > 0.001 ? clip.StartTime + clip.Duration : _viewModel.SequenceSeconds);
            if (clip.IsVideoFile && MediaProbe.KnownIFrames(clip.ImagePath.Trim().Trim('"')) is { } keys)
                foreach (var key in keys)
                    if (key >= clip.MediaOffset && clip.StartTime + (key - clip.MediaOffset) / speed is var on && on <= end)
                        iFrames.Add(on);
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
        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    // The toolbar's button: the selected clip, or else the main video.
    private void ManualCut_Click(object sender, RoutedEventArgs e) => SplitTrimByTimecode(_selectedClip switch
    {
        Layer layer when _viewModel.Layers.Contains(layer) && layer.HasTiming => layer,
        LayerSound sound when _viewModel.Layers.Contains(sound.Layer) => sound.Layer,
        _ => _viewModel.MainLayer,
    });

    /// <summary>
    /// Remove Dead Air: first how quiet counts as silence (and whether to mark or delete),
    /// then the search, on whatever is selected: a video layer, an audio track, or otherwise the main video.
    /// </summary>
    private void RemoveDeadAir_Click(object sender, RoutedEventArgs e) => RunDeadAir(_selectedClip switch
    {
        Layer { CarriesSound: true } layer => layer,
        LayerSound sound => sound.Layer,
        AudioClip audio => audio.Track,
        _ => null,
    });

    private void DropHint_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => BrowseSource_Click(sender, e);

    // ----- The timeline's checkboxes in the transport row -----

    // The width the three checkboxes need side by side, measured while they are shown.
    private double _timelineOptionsWidth;

    /// <summary>
    /// Shows the checkboxes beside the transport while the row has room for them, and one button that opens
    /// them as a menu while it has not. They are never wrapped onto a second line.
    /// </summary>
    private void TimelineOptionsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TimelineOptionsInline.Visibility == Visibility.Visible && TimelineOptionsInline.ActualWidth > 0)
            _timelineOptionsWidth = Math.Max(_timelineOptionsWidth, TimelineOptionsInline.ActualWidth);
        if (_timelineOptionsWidth <= 0)
        {
            TimelineOptionsInline.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _timelineOptionsWidth = TimelineOptionsInline.DesiredSize.Width;
        }

        // No room: the checkboxes are taken out of the row altogether and offered in the Views menu instead; with
        // room again they come back. They are never wrapped onto a second line.
        var fits = TimelineOptionsHost.ActualWidth >= _timelineOptionsWidth + 4;
        UpdateTransportMinWidth();
        TimelineOptionsInline.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
        ViewsPlaybackOptions.Visibility = fits ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TimelineOptionsBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTransportMinWidth();

    /// <summary>
    /// However narrow the player gets, the Views button keeps its place at the left of the playback bar: with
    /// the checkboxes folded into it, it is the only way to them.
    /// </summary>
    private void UpdateTransportMinWidth() =>
        TransportRow.ColumnDefinitions[0].MinWidth = TimelineOptionsBar.IsVisible
            ? Math.Ceiling((ViewsButton.ActualWidth > 0 ? ViewsButton.ActualWidth : 90) + 22)
            : 0;

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

    // ----- Add Media (the header's): a click picks files; resting on it opens the box for a path or a link -----
    // The Settings button's pattern (HoverFlyout): the flyout opens once the pointer has rested on the button, and
    // leaving the button or the flyout closes it a moment later. While its box has the keyboard (something is being
    // typed or pasted) it stays.

    /// <summary>Picks files to add: with nothing open, the first is opened as the main video (or project); otherwise every one goes onto the timeline at the playhead.</summary>
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
                + $"|Pictures|{Patterns(MediaBrowserItem.ImageExtensions)}|HandPeg projects|*{ProjectStore.Extension}|All files|*.*",
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
    /// pointer, reaching <paramref name="leftward"/> pixels left of the button's right edge (the header sits at the
    /// window's right); beside it, towards the window's middle, while the header stands as a strip at a side.
    /// </summary>
    private void PlaceHeaderFlyout(Popup popup, FrameworkElement host, double leftward)
    {
        if (_headerShape == Docking.PaneShape.Column)
        {
            var window = Window.GetWindow(host) ?? this;
            var onRight = host.TranslatePoint(new Point(host.ActualWidth / 2, 0), window).X > window.ActualWidth / 2;
            popup.Placement = onRight ? PlacementMode.Left : PlacementMode.Right;
            (popup.HorizontalOffset, popup.VerticalOffset) = (onRight ? -6 : 6, 0);
            return;
        }

        popup.Placement = PlacementMode.Bottom;
        (popup.HorizontalOffset, popup.VerticalOffset) = (host.ActualWidth - leftward, 6);
    }

    // ----- The progress chip (bottom left) and its flyout -----
    // The Settings button's pattern: the flyout opens while the pointer rests on the chip, and leaving the chip or the
    // flyout closes it a moment later, so the pointer can cross from one to the other.

    private readonly System.Windows.Threading.DispatcherTimer _workFlyoutClose = new() { Interval = TimeSpan.FromMilliseconds(260) };

    // From the progress chip it opens above it; from the status in the title bar (the status bar hidden), under it.
    private void WorkChip_MouseEnter(object sender, MouseEventArgs e)
    {
        _workFlyoutClose.Stop();
        PauseBackgroundToggle.IsChecked = WorkScheduler.IsPausedByUser(WorkLane.Background);
        PauseIdleToggle.IsChecked = WorkScheduler.IsPausedByUser(WorkLane.Idle);
        var fromTitle = ReferenceEquals(sender, TitleStatus);
        if (!WorkFlyout.IsOpen)
        {
            WorkFlyout.PlacementTarget = fromTitle ? TitleStatus : WorkChip;
            WorkFlyout.Placement = fromTitle ? PlacementMode.Bottom : PlacementMode.Top;
            WorkFlyoutPanel.Margin = fromTitle ? new Thickness(0, 4, 0, 0) : new Thickness(0, 0, 0, 4);
        }

        WorkFlyout.IsOpen = true;
    }

    private void WorkFlyout_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_workFlyoutCloseHooked)
        {
            _workFlyoutClose.Tick += (_, _) =>
            {
                _workFlyoutClose.Stop();
                if (!WorkChip.IsMouseOver && !TitleStatus.IsMouseOver && !WorkFlyoutPanel.IsMouseOver)
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
    private void SaveProject()
    {
        if (!_viewModel.HasSource)
            _viewModel.StatusText = "There is nothing to save yet: load a video first.";
        else if (_viewModel.CurrentProjectPath.Length > 0)
            _viewModel.SaveProjectTo(_viewModel.CurrentProjectPath);
        else
            SaveProjectAs();
    }

    /// <summary>Save As: asks where, with the Windows file dialog. Neither kind of save touches the undo history.</summary>
    private void SaveProjectAs()
    {
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
            Filter = $"HandPeg project (*{ProjectStore.Extension})|*{ProjectStore.Extension}",
            DefaultExt = ProjectStore.Extension,
            AddExtension = true,
            InitialDirectory = current.Length > 0 ? Path.GetDirectoryName(current) : AppPaths.Projects,
            FileName = current.Length > 0 ? Path.GetFileName(current) : Path.GetFileNameWithoutExtension(_viewModel.SourcePath.Trim().Trim('"')),
        };
        if (dialog.ShowDialog(this) == true)
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

    private void OpenQueue_Click(object sender, RoutedEventArgs e)
    {
        // One queue window at a time: a second click brings the open one forward.
        if (_queueWindow is null)
        {
            _queueWindow = new QueueWindow { Owner = this, DataContext = _viewModel };
            _queueWindow.Closed += (_, _) => _queueWindow = null;
            _queueWindow.Show();
        }

        _queueWindow.Activate();
    }

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
        var saved = window.ShowDialog() == true;
        CrashReporter.Note("settings", saved ? "saved" : "closed");
        if (saved)
        {
            _viewModel.OnSettingsSaved();
            _mpv?.SetHardwareAcceleration(AppSettings.Current.PlayerHardwareAcceleration);
            if (_dock.IsRealized(ExportId))
                FillExportSection();
        }

        // Crash reports switched on or off there (or in the first-run window it can open): the sentinel follows.
        CrashReporter.FollowSetting();

        // Check for Updates, in that window, may have found (or brought in) a release: it is offered here as one found at startup is.
        if (AppUpdater.ReadyVersion is { } update)
            ShowUpdateNotice(update, downloaded: true);
        else if (AppUpdater.FoundVersion is { } found)
            ShowUpdateNotice(found, downloaded: false);

        // The player may just have been installed from the dependency list.
        _ = EnsurePlayerAsync();
    }


    // The Source pane's one button, and Enter in its box: with the box empty there is nothing to load, and
    // it browses for a file; with a path or a URL in it, it loads that.
    private void SourceAction_Click(object sender, RoutedEventArgs e)
    {
        var path = (_viewModel.SourcePath ?? "").Trim().Trim('"');
        _addMediaHover.Close();
        if (path.Length == 0)
            BrowseSource_Click(sender, e);
        else if (path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            _ = _viewModel.LoadProjectAsync(path);   // A project file in the Source box opens the project.
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

    // ----- Segment strip above the timeline -----

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.PendingStartMs))
            RedrawSegments();

        if (e.PropertyName is nameof(MainViewModel.SoloTrack) or nameof(MainViewModel.SoloedTracks))
            OnSoloTrackChanged();

        if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.ShowLinkedAudio)
            or nameof(MainViewModel.AudioLinked) or nameof(MainViewModel.TimelineWaveform))
        {
            ScheduleClipRedraw();
        }

        if (e.PropertyName == nameof(MainViewModel.PositionMs))
        {
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

        if (e.PropertyName == nameof(MainViewModel.Volume))
            _mpv?.SetVolume(_viewModel.Volume);

        if (e.PropertyName == nameof(MainViewModel.PlaybackSpeed))
            _mpv?.SetSpeed(_viewModel.PlaybackRate);

        if (e.PropertyName == nameof(MainViewModel.LivePreview))
        {
            ApplyLiveFilter();
            _viewModel.StatusText = _viewModel.LivePreview
                ? "Live Preview on: the player shows the picture with the export's filters applied."
                : "Live Preview off: the player shows the source as it is.";
        }

        if (e.PropertyName is nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer))
            UpdateOverlayMode();

        if (e.PropertyName == nameof(MainViewModel.IsArrangeActive))
            UpdateLayoutPane();

        // Which layers there are to show in the layout pane.
        if (e.PropertyName is nameof(MainViewModel.FrameEngine) or nameof(MainViewModel.AutoCaptions))
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

        // The centre video is moved in place rather than redrawn: a redraw would replace the very
        // box that is being dragged.
        if (e.PropertyName is nameof(MainViewModel.CenterZoom) or nameof(MainViewModel.CenterOffsetX) or nameof(MainViewModel.CenterOffsetY))
            _placeCenter?.Invoke();

        if (e.PropertyName == nameof(MainViewModel.SpriteSheetPath))
            LoadSpriteSheet();

    }

    private OverwriteDecision AskOverwrite(string path)
    {
        var dialog = new FileOverwriteDialog(path) { Owner = this };
        dialog.ShowDialog();
        return dialog.Decision;
    }

    private void SegmentCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawSegments();

    // What playback skips, and the stretch a right-drag is marking.
    private static readonly Brush BlackoutBrush = Frozen(Color.FromArgb(0xB0, 0x2A, 0x16, 0x16));
    private static readonly Brush RangeDragBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0x8C, 0x00));

    // The layout pane's stage: the output frame.
    private static readonly Brush StageBrush = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2A));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void RedrawSegments()
    {
        SegmentCanvas.Children.Clear();
        ShadeCanvas.Children.Clear();

        var duration = _viewModel.DurationMs;
        var width = SegmentCanvas.ActualWidth;
        if (duration <= 0 || width <= 0)
            return;

        // A stretch being marked by a right-drag (split at both its ends when it is let go), the in point set with [, the markers.
        if (_rangeDragStartMs is { } dragStart)
            AddShade(Math.Min(dragStart, _rangeDragEndMs), Math.Max(dragStart, _rangeDragEndMs), RangeDragBrush);

        if (_viewModel.PendingStartMs is { } pendingStart)
            AddStripMark(pendingStart / duration * width, 2, Brushes.OrangeRed);

        DrawTimelineMarkers();
    }

    /// <summary>Shades the timeline between two times, given in milliseconds.</summary>
    private void AddShade(double fromMs, double toMs, Brush fill)
    {
        var (duration, width) = (_viewModel.DurationMs, ShadeCanvas.ActualWidth);
        if (toMs <= fromMs || duration <= 0 || width <= 0)
            return;

        var shade = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max((toMs - fromMs) / duration * width, 1),
            Height = ShadeCanvas.ActualHeight,
            Fill = fill,
        };
        Canvas.SetLeft(shade, fromMs / duration * width);
        ShadeCanvas.Children.Add(shade);
    }

    // ----- Marking a segment by dragging with the right button -----

    private double? _rangeDragStartMs;
    private double _rangeDragEndMs;

    /// <summary>The time, in milliseconds, at a horizontal position on the timeline.</summary>
    private double TimelineMsAt(double x)
    {
        // The track is shorter than the slider by half a thumb at each end.
        const double inset = 5.5;
        var track = TimelineSlider.ActualWidth - 2 * inset;
        return track > 0 ? Math.Clamp((x - inset) / track, 0, 1) * _viewModel.DurationMs : 0;
    }

    /// <summary>The same pull towards I-frames that the thumb feels while it is dragged, when that is switched on.</summary>
    private double PullToIFrame(double positionMs)
    {
        var settings = AppSettings.Current;
        if (!settings.SnapTimelineToIFrames || TimelineSlider.ActualWidth <= 0 || _viewModel.GetNearestIFrameMs(positionMs) is not { } iFrame)
            return positionMs;

        const double pixelsPerStep = 4;
        var reach = Math.Clamp(settings.TimelineMagnetism, 1, 5) * pixelsPerStep / TimelineSlider.ActualWidth * _viewModel.DurationMs;
        return Math.Abs(iFrame - positionMs) <= reach ? iFrame : positionMs;
    }

    private void TimelineSlider_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.DurationMs <= 0 || !TimelineSlider.CaptureMouse())
            return;

        _rangeDragStartMs = _rangeDragEndMs = PullToIFrame(TimelineMsAt(e.GetPosition(TimelineSlider).X));
        PreviewPopup.IsOpen = false;
        e.Handled = true;
        RedrawSegments();
    }

    private void TimelineSlider_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rangeDragStartMs is not { } start)
            return;

        var end = _rangeDragEndMs;
        _rangeDragStartMs = null;
        TimelineSlider.ReleaseMouseCapture();
        e.Handled = true;
        RedrawSegments();

        // A right-click without a drag marks nothing.
        var pixels = Math.Abs(end - start) / _viewModel.DurationMs * TimelineSlider.ActualWidth;
        if (pixels < 3)
        {
            _viewModel.StatusText = "Drag along the timeline with the right mouse button to mark a stretch: every track is split at both its ends.";
            return;
        }

        // The same way in as a typed cut, so Snap cuts to I-frames applies to it in the same way.
        if (_viewModel.AddManualSegment(Math.Min(start, end) / 1000, Math.Max(start, end) / 1000) is { } problem)
            _viewModel.StatusText = problem;
    }

    private void TimelineSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_rangeDragStartMs is null)
            return;

        _rangeDragStartMs = null;
        RedrawSegments();
    }

    private void AddStripMark(double left, double width, Brush fill)
    {
        var mark = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = SegmentCanvas.ActualHeight,
            Fill = fill,
        };
        Canvas.SetLeft(mark, left);
        SegmentCanvas.Children.Add(mark);
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

    // ----- Filter files -----

    private void BrowseLut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select a LUT", Filter = "3D LUT|*.cube|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.LutPath = dialog.FileName;
    }

    private void ClearLut_Click(object sender, RoutedEventArgs e) => _viewModel.LutPath = "";

    private void HudMasks_Click(object sender, RoutedEventArgs e)
    {
        // Offered the way the frame is now: a tall frame gets the tall layout.
        var dialog = new HudMasksDialog { Owner = this, Vertical = _viewModel.FrameHeight > _viewModel.FrameWidth };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.UseOwnMask)
            AddLayersFromMask();
        else if (dialog.Game is { } game)
            _viewModel.AddHudLayers(game, dialog.Vertical);
    }

    // ----- Keyframes: the pane, and the buttons of the bottom bar -----

    /// <summary>The clip the keyframe controls work on: the one selected on a time bar, or failing that the one the view model has.</summary>
    private Layer KeyClip => _selectedClip switch
    {
        Layer { IsPicture: true } layer when _viewModel.Layers.Contains(layer) => layer,
        _ => _viewModel.ActiveKeyLayer,
    };

    private void ToggleKeyframe_Click(object sender, RoutedEventArgs e)
    {
        var clip = KeyClip;
        if (_viewModel.HasKeyframeAtPlayhead(clip))
            _viewModel.RemoveKeyframe(clip);
        else
            _viewModel.AddKeyframe(clip);
    }

    private void PreviousKeyframe_Click(object sender, RoutedEventArgs e) => StepKeyframe(-1);

    private void NextKeyframe_Click(object sender, RoutedEventArgs e) => StepKeyframe(1);

    private void StepKeyframe(int direction)
    {
        if (PlayerIsPlaying)
            PlayerSetPause(true);
        _viewModel.GoToKeyframe(KeyClip, direction);
    }

    private void ClearKeyframes_Click(object sender, RoutedEventArgs e) => _viewModel.ClearKeys(KeyClip);

    private void KeyLanes_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A change of height is the rows' own (the graph shown or hidden); only a new width lays them out again.
        if (e.WidthChanged)
            DrawKeyTimeline();
    }

    private void KeyGraph_Click(object sender, RoutedEventArgs e) => DrawKeyTimeline();

    /// <summary>Zoom to Clip: the time bars, and the Keyframes pane's rows with them, show the selected clip's stretch.</summary>
    private void ZoomToKeyClip_Click(object sender, RoutedEventArgs e)
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0)
            return;

        var clip = _viewModel.ActiveKeyLayer;
        var (from, to) = clip.GetSpan(seconds);
        var margin = Math.Max((to - from) * 0.04, 0.2);
        SetView(Math.Max(from - margin, 0), to - from + 2 * margin);
    }

    /// <summary>
    /// Draws the Keyframes pane's rows: the time bars' stretch of the sequence (their zoom and scroll), the selected
    /// clip's stretch of it shaded, a row for All and for each of its motion properties (and each animated effect value)
    /// with a diamond at each keyframe, the selected row's graph, and the playhead. The rows draw themselves from these;
    /// as the video plays only the playhead is moved.
    /// </summary>
    private void DrawKeyTimeline()
    {
        RefreshMotionPath();
        var seconds = _viewModel.DurationMs / 1000;
        if (!KeyframesPane.IsVisible || seconds <= 0 || KeyLanes.ActualWidth <= 0)
        {
            KeyLanes.Show([], 0, 0, null, null, double.NaN);
            return;
        }

        var clip = _viewModel.ActiveKeyLayer;
        var span = clip.GetSpan(seconds);
        ClampView();
        KeyLanes.ShowGraph = _viewModel.ShowKeyframeGraph;
        KeyLanes.Show(BuildKeyLanes(clip), _viewStart, ViewSpan, span, clip.IsMainVideo ? MainClipBrush : LayerClipBrush, _viewModel.PositionMs / 1000);
        _viewModel.SelectedKeyLane = KeyLanes.Selected?.Target;
    }

    /// <summary>The rows of a clip: All, its motion properties, and every effect value of its stack that has keyframes.</summary>
    private static List<KeyLane> BuildKeyLanes(Layer clip)
    {
        var start = clip.StartTime;
        var lanes = new List<KeyLane> { new() { Name = "All", IsAll = true, Times = [.. clip.KeyTimes.Select(t => t + start)] } };

        void Add(KeyProperty property, string name, Func<double, string> format)
        {
            var keys = clip.GetKeys(property);
            lanes.Add(new KeyLane
            {
                Name = name, Target = property, Format = format,
                Times = [.. keys.Select(k => k.Time + start)], Values = [.. keys.Select(k => k.Value)],
                ValueAt = time => clip.Evaluate(property, time - start),
            });
        }

        static string Percent(double fraction) => (fraction * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
        Add(KeyProperty.X, "X", Percent);
        Add(KeyProperty.Y, "Y", Percent);
        Add(KeyProperty.Scale, "Scale", Percent);
        if (clip.HasFilters)
            Add(KeyProperty.Rotation, "Rotation", value => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "\u00B0");
        if (clip.IsPicture)
            Add(KeyProperty.Opacity, "Opacity", value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%");

        foreach (var card in clip.EffectsBypassed ? [] : clip.Effects)
        {
            foreach (var param in card.Definition?.Params ?? [])
            {
                if (param.Animation == Models.Effects.ParamAnimation.None || param.Bind == nameof(Layer.Opacity) || !card.HasKeys(param.Key))
                    continue;

                var value = new EffectKeyRef(card, param, $"{card.Title}: {param.Label}");
                var keys = card.GetKeys(param.Key);
                lanes.Add(new KeyLane
                {
                    Name = value.Label, Target = value,
                    Times = [.. keys.Select(k => k.Time + start)], Values = [.. keys.Select(k => k.Value)],
                    ValueAt = time => MainViewModel.EffectValueAt(clip, value, time - start),
                    Format = number => number.ToString(param.Decimals == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + param.Unit,
                });
            }
        }

        return lanes;
    }

    // ----- The panes: where each is, and how large -----
    // The player area is two grids. The outer one has the player in one column and the side panes in another,
    // over the overview bar; the inner one has the video in one column and the layout pane in another.
    // Moving a pane is giving it the other row or column of its grid (and that row or column its size);
    // nothing is taken out of the window or put back. The arrangement belongs to the mode in use.

    private static readonly GridLength Star = new(1, GridUnitType.Star);

    /// <summary>Lays the panes out again, as this mode's arrangement has them (see MainWindow.Docking.cs).</summary>
    private void ApplyPaneLayout()
    {
        _dock?.ScheduleRebuild();
        PlaceViewsPopup();
    }

    /// <summary>Puts the Views menu beside the button that opens it, at the left of the transport row, over it.</summary>
    private void PlaceViewsPopup()
    {
        if (!ReferenceEquals(ViewsPopup.PlacementTarget, ViewsButton))
            (ViewsPopup.PlacementTarget, ViewsPopup.Placement, ViewsPopup.VerticalOffset) = (ViewsButton, PlacementMode.Top, -4);
    }

    /// <summary>A side pane was shown or hidden: the panes are laid out again.</summary>
    private void UpdateSidePanes()
    {
        _dock?.ScheduleRebuild();
        Dispatcher.BeginInvoke(DrawKeyTimeline, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Reset Panes Layout (the Views menu), and Reset Layout (the Layout flyout).
    private void ResetPanesLayout_Click(object sender, RoutedEventArgs e)
    {
        CloseLayoutMenu();
        _viewModel.ResetPaneLayout();
        _dock.Reset();
        ViewsButton.IsChecked = false;
    }

    // The border between the player and the overview bar. What it drags is the bar's height setting,
    // the one the slider beside the volume sets: the bar is as tall as that says, not as tall as a grid
    // row happens to be, so the row is put back to "as tall as its content" each time the splitter has sized it.

    // Dragging a pane by its title: let go on the other side of what it is beside, and it moves there.

    // ----- The Audio tab's checkboxes, folded into a button when the row is too narrow -----

    private double _audioOptionsWidth;

    // ----- Tabs that are built when they are first wanted -----

    // The named parts of the Audio tab that this file uses: the tab is made from a template, so these are found in
    // it when it is made. The one asked about from outside the tab (is a file being dropped on it) may not be there
    // yet; the rest are only reached from the tab's own events.
    private DockPanel? AudioPanel;
    private Grid AudioOptionsHost = null!;
    private StackPanel AudioOptionsInline = null!;
    private System.Windows.Controls.Primitives.ToggleButton AudioOptionsButton = null!;

    private void AudioOptionsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AudioPanel is null)
            return;

        if (AudioOptionsInline.Visibility == Visibility.Visible && AudioOptionsInline.ActualWidth > 0)
            _audioOptionsWidth = Math.Max(_audioOptionsWidth, AudioOptionsInline.ActualWidth);
        if (_audioOptionsWidth <= 0)
        {
            AudioOptionsInline.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _audioOptionsWidth = AudioOptionsInline.DesiredSize.Width;
        }

        var fits = AudioOptionsHost.ActualWidth >= _audioOptionsWidth + 4;
        (AudioOptionsInline.Visibility, AudioOptionsButton.Visibility) = fits ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Hidden, Visibility.Visible);
        if (fits)
            AudioOptionsButton.IsChecked = false;
    }

    // ----- The action log -----

    /// <summary>Lights the status bar up for a moment: something was just done, and it says what.</summary>
    private void FlashStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(FlashStatus);
            return;
        }

        StatusFlash.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.6, 0, TimeSpan.FromMilliseconds(1200))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
        });
    }

    // A button that opened a menu would, clicked again to close it, open it straight back up (see
    // TimelineOptionsPopup_Opened). The views menu, the bins and the keyframe menu share the cure: each popup
    // knows its button (its placement target), which is deaf while the popup is open.
    private void ViewsPopup_Opened(object? sender, EventArgs e)
    {
        FillViewsLists();
        ViewsButton.IsHitTestVisible = false;
    }

    private void ViewsPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ViewsButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);

    private void BinPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement button })
            button.IsHitTestVisible = false;
    }

    private void BinPopup_Closed(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement button })
            Dispatcher.BeginInvoke(() => button.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RestoreDeleted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BinItem item })
            _viewModel.RestoreFromBin(item);
        ScheduleClipRedraw();
    }

    private void EmptyBin_Click(object sender, RoutedEventArgs e) => _viewModel.EmptyBin();

    private async void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a video to place on the frame",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.gif|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddVideoLayerAsync(dialog.FileName);
    }

    private async void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a sound to add to the timeline",
            Filter = "Audio and video files|*.mp3;*.wav;*.m4a;*.flac;*.ogg;*.opus;*.aac;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAudioLayerAsync(dialog.FileName, _viewModel.PositionMs / 1000);
    }

    /// <summary>Opens the Windows colour picker for one of the selected text layer's colours: its letters' or its outline's.</summary>
    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which } || _viewModel.SelectedTextLayer is not { } layer)
            return;

        var outline = which == "Outline";
        CaptionPreview.TryParse(outline ? layer.OutlineColor : layer.FontColor, out var current);
        using var picker = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
        };

        // Owned by this window, so that it opens over it and blocks it like any other dialog.
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (picker.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
                return;
        }
        finally
        {
            owner.ReleaseHandle();
        }

        var color = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        if (string.Equals(outline ? layer.OutlineColor : layer.FontColor, color, StringComparison.OrdinalIgnoreCase))
            return;

        // The Windows colour picker's clicks are not seen by the window (NoteUserInput), so its step is taken here (4.4).
        _viewModel.Checkpoint($"change the {(outline ? "outline" : "text")} color of {layer.Name}");
        if (outline)
            layer.OutlineColor = color;
        else
            layer.FontColor = color;
    }

    // ----- Add Layer's kinds: what each entry of the timeline's Add Layer flyout adds, by its Tag -----
    // The flyout itself (its button, its timing, its keys) is the timeline pane's own since 4.4 (Views/UnifiedTimelinePane);
    // it closes itself and hands the entry chosen on here (MainWindow.UnifiedTimeline.cs).

    private void AddLayerMenu_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "Empty": _viewModel.AddEmptyTrackCommand.Execute(null); break;
            case "Video": AddVideo_Click(sender, e); break;
            case "Image": AddImage_Click(sender, e); break;
            case "Text": _viewModel.AddTextLayerCommand.Execute(null); break;
            case "Adjustment": _viewModel.AddAdjustmentLayerCommand.Execute(null); break;
            case "Region": _viewModel.AddLayerCommand.Execute(null); break;
            case "MaskLayers": AddLayersFromMask(); break;
            case "Audio": AddAudio_Click(sender, e); break;
            case "Voiceover": OpenVoiceoverStudio_Click(sender, e); break;
            case "Captions": GenerateCaptionsFrom(null); break;
            case "ImportSubtitles": ImportSubtitles(); break;
            case "EditCaptions": EditCaptions(); break;
            case "CaptionStyle": OpenCaptionStyle(); break;
        }
    }

    /// <summary>
    /// Layers from a Mask: a mask of the whole frame (chosen here when none is given) taken apart into one
    /// layer per HUD element it shows, after the pieces have been looked at and joined as wanted.
    /// </summary>
    /// <param name="owner">The window it opens over: this one, or a dialog open over it.</param>
    public void AddLayersFromMask(string? maskPath = null, Window? owner = null)
    {
        owner ??= this;
        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "Load a video first: the layers are cut out of it.";
            return;
        }

        if (maskPath is null)
        {
            var dialog = new OpenFileDialog { Title = "Select a mask of the whole frame", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
            if (dialog.ShowDialog(owner) != true)
                return;
            maskPath = dialog.FileName;
        }

        // The areas already cut out of the video: pieces of the mask that one of them covers start left out.
        var existing = _viewModel.Layers.Where(l => l.IsVideo && !l.IsDeleted).Select(l => new Rect(l.SourceX, l.SourceY, l.SourceWidth, l.SourceHeight)).ToList();
        if (MaskLayersWindow.Open(_viewModel, maskPath, existing) is not { } window)
        {
            _viewModel.StatusText = "That mask shows nothing to make layers of, or it could not be read: it should be white (or painted, on transparency) where the HUD is.";
            return;
        }

        window.Owner = owner;
        if (window.ShowDialog() == true)
            _viewModel.AddMaskLayers(maskPath, window.Pieces, window.Vertical, window.ReplaceExisting);
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an image to place on the frame", Filter = "Images|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.AddImageLayer(dialog.FileName);
    }

    // ----- Render preview and audio mixer -----

    private void ShowPreview(string path) => new PreviewPlayerWindow(path) { Owner = this }.Show();

    /// <summary>The filters of one audio track, from its row on the timeline or its menu. One step of Undo (4.4: a track's filters are in the step).</summary>
    private void TrackFilters_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case Models.AudioTrack track:
                var forTrack = new TrackAudioFiltersDialog(track) { Owner = this };
                if (forTrack.ShowDialog() == true)
                {
                    _viewModel.Checkpoint($"change the audio filters of {track.Title}");
                    track.Filters = forTrack.Filters;
                    _viewModel.Log($"Changed the audio filters of {track.Title}");
                }

                break;

            // A sound from a file, or the sound of a video layer: the same filters, kept with the clip.
            case Layer layer:
                var forClip = new TrackAudioFiltersDialog($"{layer.SourceFileName}   {layer.Description}", layer.AudioFilters) { Owner = this };
                if (forClip.ShowDialog() == true)
                {
                    _viewModel.Checkpoint($"change the audio filters of {layer.Name}");
                    layer.AudioFilters = forClip.Filters;
                    _viewModel.Log($"Changed the audio filters of {layer.SourceFileName}");
                }

                break;
        }
    }

    /// <summary>The Voiceover Studio: recording, trimming and placing the voiceover that the encode mixes in.</summary>
    private void OpenVoiceoverStudio_Click(object sender, RoutedEventArgs e)
    {
        // Recording goes with watching the video, so the studio does not block the main window.
        if (_voiceoverStudio is { IsLoaded: true })
        {
            _voiceoverStudio.Activate();
            return;
        }

        _voiceoverStudio = new VoiceoverStudioDialog(_viewModel) { Owner = this };
        _voiceoverStudio.Closed += (_, _) => _voiceoverStudio = null;

        // Undo and redo there follow the main window's rule (4.3E): a box's own typing first, the timeline's otherwise. The
        // studio's other keys stay its own; typing in its boxes is noted for that rule.
        _voiceoverStudio.PreviewKeyDown += (studio, e) =>
        {
            if (UndoKey(e) is not null)
                MainWindow_PreviewKeyDown(studio, e);
            else if (Keyboard.FocusedElement is TextBoxBase box && TypesText(e))
                _typedIn = (box, _viewModel.UndoMark);
        };
        _voiceoverStudio.Show();
    }

    private VoiceoverStudioDialog? _voiceoverStudio;

    /// <summary>A layer's Properties dialog: what is changed in it is one step of Undo.</summary>
    private void ShowLayerProperties(Layer layer)
    {
        _viewModel.Checkpoint($"change the look of {layer.Name}");
        var visit = _viewModel.UndoMark;
        new LayerPropertiesDialog { Owner = this, DataContext = layer, ToggleVisibility = _viewModel.ToggleHidden }.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
    }

    private void LiveFramePreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "Load a video first.";
            return;
        }

        // The player would only keep running behind a dialog that shows a still frame.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        new FramePreviewDialog(_viewModel) { Owner = this }.ShowDialog();
    }

    // ----- Layout files -----

    private const string LayoutFileFilter = "HandPeg style preset (*.hpstyle)|*.hpstyle|All files|*.*";


    /// <summary>Where layout files are kept unless the user picks somewhere else. Made on first use, so the dialog can open in it.</summary>
    private static string LayoutsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Styles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The dialog then opens wherever Windows last had it.
        }

        return AppPaths.Styles;
    }

    // ----- The Summary tab's lists: encoding presets, style presets, projects -----

    private const string PresetFileFilter = "HandPeg encoding preset|*.hppreset;*.json|All files|*.*";

    private void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import Encoding Preset", Filter = PresetFileFilter };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ImportPreset(dialog.FileName);
    }

    private void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = _viewModel.PresetName.Trim().Length > 0 ? _viewModel.PresetName.Trim() : "My Preset";
        var dialog = new SaveFileDialog { Title = "Export Encoding Preset", Filter = PresetFileFilter, FileName = name + ".hppreset", DefaultExt = "hppreset" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportPreset(dialog.FileName);
    }

    /// <summary>
    /// The one button of a preset block: a small dialog with what can be done with that kind of preset, each
    /// as a button that does it. For encoding presets that is also where the name to save one under is typed.
    /// </summary>
    /// <param name="nameProperty">The view model's property that holds the name a preset of this kind is saved to the list under.</param>
    /// <returns>"save", "import", "export", or null when the dialog was closed.</returns>
    private string? AskPresetAction(string title, string about, string nameProperty)
    {
        string? chosen = null;
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);

        var panel = new StackPanel { Margin = new Thickness(22), Width = 380 };
        var text = new TextBlock { Text = about, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        panel.Children.Add(text);

        Button Choice(string label, string action, string tip)
        {
            var button = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                chosen = action;
                dialog.Close();
            };
            return button;
        }

        {
            // Saving to the list: the name, and the button that saves under it.
            panel.Children.Add(new TextBlock { Text = "Save the current settings to the list as:", Margin = new Thickness(0, 0, 0, 4) });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var save = Choice("Save", "save", "Saves the current settings to the list under this name. An existing name is replaced.");
            (save.Margin, save.IsDefault) = (new Thickness(6, 0, 0, 0), true);
            DockPanel.SetDock(save, Dock.Right);
            var name = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(name, "Preset Name");
            name.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameProperty) { Source = _viewModel, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
            row.Children.Add(save);
            row.Children.Add(name);
            panel.Children.Add(row);
            dialog.Loaded += (_, _) => name.Focus();
        }

        panel.Children.Add(Choice("Import from a File...", "import", "Brings a preset in from a file."));
        panel.Children.Add(Choice("Export to a File...", "export", "Saves the settings as they are now to a file."));
        var close = new Button { Content = "Close", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        panel.Children.Add(close);
        dialog.Content = panel;
        dialog.ShowDialog();
        return chosen;
    }

    private void ManagePresets_Click(object sender, RoutedEventArgs e)
    {
        switch (AskPresetAction("Encoding Presets", "An encoding preset is the size, filter, video and audio settings. Save the ones set now to the list, or move a preset to or from a file.", nameof(MainViewModel.PresetName)))
        {
            case "save":
                _viewModel.SavePresetCommand.Execute(null);
                break;
            case "import":
                ImportPreset_Click(sender, e);
                break;
            case "export":
                ExportPreset_Click(sender, e);
                break;
        }
    }

    private void ManageStyles_Click(object sender, RoutedEventArgs e)
    {
        switch (AskPresetAction("Style Presets", "A style preset is the layers with their masks, and the color, blur and subtitle settings that go with them, in one .hpstyle file. Save the ones set now to the list, or move a style to or from a file.", nameof(MainViewModel.StyleName)))
        {
            case "save":
                _viewModel.SaveStyle();
                break;
            case "import":
                ImportLayout_Click(sender, e);
                break;
            case "export":
                ExportLayout_Click(sender, e);
                break;
        }
    }

    // The lists show what is in their folders at the moment they are opened.
    private void StylePresets_DropDownOpened(object sender, EventArgs e) => _viewModel.RefreshStylePresets();

    private void RecentProjects_DropDownOpened(object sender, EventArgs e) => _viewModel.RefreshRecentProjects();

    /// <summary>Swap Project: opens the chosen project in place of what is open, asking first when that has changes that were not saved.</summary>
    // ----- The Summary tab's cards: in the order they are dragged into, which is kept -----

    private static readonly string[] SummaryCardIds = ["Output", "EncodingPreset", "StylePreset", "SwapProject"];

    /// <summary>Puts the cards in the order saved (any not named keep their place after those that are).</summary>
    private void ApplySummaryCardOrder()
    {
        var saved = AppSettings.Current.SummaryCardOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var order = saved.Where(SummaryCardIds.Contains).Concat(SummaryCardIds.Where(id => !saved.Contains(id))).ToList();
        var cards = SummaryCards.Children.OfType<FrameworkElement>().ToList();
        SummaryCards.Children.Clear();
        foreach (var card in cards.OrderBy(c => c.Tag is string id && order.IndexOf(id) is var at and >= 0 ? at : int.MaxValue))
            SummaryCards.Children.Add(card);
    }

    private void SaveSummaryCardOrder() =>
        _viewModel.SavePaneLayout(s => s.SummaryCardOrder = string.Join(",", SummaryCards.Children.OfType<FrameworkElement>().Select(c => c.Tag as string).OfType<string>()));

    private void SummaryCardHeader_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } header || SummaryCards.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == id) is not { } card)
            return;

        var at = SummaryCards.Children.IndexOf(card);
        var menu = new ContextMenu { PlacementTarget = header };
        void Add(string text, bool enabled, Action action)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                action();
                SaveSummaryCardOrder();
            };
            menu.Items.Add(item);
        }

        Add("Move Up", at > 0, () =>
        {
            SummaryCards.Children.RemoveAt(at);
            SummaryCards.Children.Insert(at - 1, card);
        });
        Add("Move Down", at < SummaryCards.Children.Count - 1, () =>
        {
            SummaryCards.Children.RemoveAt(at);
            SummaryCards.Children.Insert(at + 1, card);
        });
        menu.Items.Add(new Separator());
        Add("Reset Card Order", true, () =>
        {
            AppSettings.Current.SummaryCardOrder = "";
            ApplySummaryCardOrder();
        });
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void RecentProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.IsListingProjects || sender is not ComboBox { IsDropDownOpen: true } || e.AddedItems is not [string name])
            return;

        var path = Path.Combine(ProjectStore.Folder, name + ProjectStore.Extension);
        var previous = e.RemovedItems is [string before] ? before : null;
        if (!File.Exists(path) || _viewModel.IsBusy
            || (_viewModel.HasUnsavedChanges && !new ConfirmDialog("Swap Project", $"Open \"{name}\" in place of what is open now?\n\nChanges that were not saved as a project will be lost.", "Swap Project") { Owner = this }.ShowDialog().GetValueOrDefault()))
        {
            // Back to what is open: nothing was swapped.
            _viewModel.RefreshRecentProjects(previous);
            if (_viewModel.IsBusy)
                _viewModel.StatusText = "Wait for the running operation to finish, or cancel it, before swapping projects.";
            return;
        }

        await _viewModel.LoadProjectAsync(path);
    }

    // ----- The bottom of the window -----

    // Wide enough for the output row and the status bar to share a line.
    private const double MergedBottomBarWidth = 1250;

    /// <summary>
    /// The bottom of the window turns the output row and the status bar round, so that what was just done is
    /// said where the eye is and the way out is at the far end: the status text and the
    /// progress bar are on the left and Save As with the export buttons on the right, on one row in a window
    /// wide enough for both, and otherwise with the status bar under the output row.
    /// </summary>
    private void UpdateBottomBar()
    {
        // While the Export tab holds the output row (Pass 4.1B), the status bar has the bottom to itself.
        var merged = ActualWidth >= MergedBottomBarWidth && ReferenceEquals(DestinationBar.Parent, BottomBar);

        var columns = BottomBar.ColumnDefinitions;
        (columns[0].Width, columns[1].Width) = merged
            ? (new GridLength(2, GridUnitType.Star), new GridLength(3, GridUnitType.Star))
            : (new GridLength(3, GridUnitType.Star), new GridLength(2, GridUnitType.Star));

        Grid.SetColumn(DestinationBar, merged ? 1 : 0);
        Grid.SetColumnSpan(DestinationBar, merged ? 1 : 2);
        Grid.SetRow(StatusBar, merged ? 0 : 1);
        Grid.SetColumn(StatusBar, 0);
        Grid.SetColumnSpan(StatusBar, merged ? 1 : 2);
        (StatusBar.Margin, StatusBar.VerticalAlignment) = merged ? (new Thickness(2, 10, 14, 0), VerticalAlignment.Center) : (new Thickness(2, 10, 0, 0), VerticalAlignment.Stretch);

        // Inside the status bar the progress chip is first, at the left edge, in both modes; the action log follows it.
    }

    private void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        // First which parts, then where to.
        var choice = new StyleImportDialog(_viewModel.CaptureStyleWithMasks(), fileName: null) { Owner = this };
        if (choice.ShowDialog() != true)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Style Preset", Filter = LayoutFileFilter, FileName = "My Style.hpstyle", DefaultExt = "hpstyle",
            InitialDirectory = LayoutsFolder(),
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportStyle(dialog.FileName, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    private void ImportLayout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import Style Preset", Filter = LayoutFileFilter, InitialDirectory = LayoutsFolder() };
        if (dialog.ShowDialog(this) != true || _viewModel.ReadStyle(dialog.FileName) is not { } layout)
            return;

        // Which parts of the file to take.
        var choice = new StyleImportDialog(layout, Path.GetFileName(dialog.FileName)) { Owner = this };
        if (choice.ShowDialog() == true)
            _viewModel.ApplyStyle(layout, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    // ----- Frame & Layer Engine dialogs -----

    /// <summary>
    /// The blurred background's settings. What is changed in the dialog is one step of Undo (4.4: the blur is in the step; the
    /// dialog's own Ctrl+Z works within the visit), and none when nothing was.
    /// </summary>
    private void BlurSettings_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Checkpoint("change the background blur");
        var visit = _viewModel.UndoMark;
        new BlurSettingsDialog { Owner = this, DataContext = _viewModel }.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
    }

    private void LayerProperties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer element })
            ShowLayerProperties(element);
    }

    // The eye on a layer's row: its whole track is hidden or shown (as Hide / Show on its menu).
    private void LayerVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer { CanHide: true } layer })
            _viewModel.ToggleHidden(layer);
    }

    // ----- Timeline hover previews -----

    private BitmapSource? _spriteSheet;

    // Which load of the thumbnail sheet is the latest: a sheet decoded after a newer one was asked for is not wanted.
    private int _spriteSheetLoads;

    /// <summary>
    /// Loads the thumbnail sheet into memory (so the file itself is not kept open), decoded away from the interface's
    /// thread, and redraws the overview bar's strip with it. With no sheet (a file closed, or one without a sheet yet)
    /// the strip is cleared at once (4.4: it kept the last file's pictures until the next change of length).
    /// </summary>
    private async void LoadSpriteSheet()
    {
        var load = ++_spriteSheetLoads;
        _spriteSheet = null;
        PreviewPopup.IsOpen = false;
        var sheet = _viewModel.SpriteSheetPath is { } path ? await DecodeSpriteSheetAsync(path) : null;
        if (load != _spriteSheetLoads)
            return;

        _spriteSheet = sheet;
        RebuildFilmstrip();
    }

    /// <summary>
    /// The thumbnail sheet, read from a stream (never by its address: WPF's image cache would hand back the sheet that
    /// was at that path before it was made again) and frozen, on a worker; null when it cannot be read, which just means
    /// no previews.
    /// </summary>
    private static async Task<BitmapSource?> DecodeSpriteSheetAsync(string path)
    {
        try
        {
            return await WorkScheduler.Run(new WorkSpec("Timeline thumbnails", WorkLane.Interactive) { Visible = false, Cancellable = false },
                _ => Task.Run<BitmapSource?>(() =>
                {
                    if (!File.Exists(path))
                        return null;

                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
                    {
                        return null;
                    }
                }));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ----- Timeline thumbnails -----

    private void FilmstripList_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildFilmstrip();

    /// <summary>
    /// Fills the strip behind the timeline's waveform with pictures cut from the thumbnail sheet: as many as fit at their
    /// own shape, each showing the moment at the middle of the stretch of timeline it lies over.
    /// </summary>
    private void RebuildFilmstrip()
    {
        var (interval, duration) = (_viewModel.SpriteIntervalSeconds, _viewModel.DurationMs / 1000);
        if (!_viewModel.ShowTimelineThumbnails || _spriteSheet is not { } sheet || interval <= 0 || duration <= 0)
        {
            FilmstripList.ItemsSource = null;
            FilmstripList.Visibility = Visibility.Collapsed;
            return;
        }

        const int grid = FfmpegRunner.SpriteGridSize;
        var (cellWidth, cellHeight) = (sheet.PixelWidth / grid, sheet.PixelHeight / grid);
        if (cellWidth <= 0 || cellHeight <= 0)
            return;

        FilmstripList.Visibility = Visibility.Visible;
        var stripWidth = FilmstripList.ActualWidth > 0 ? FilmstripList.ActualWidth : TimelineSlider.ActualWidth;
        var last = Math.Max(Math.Min(grid * grid, (int)Math.Ceiling(duration / interval)) - 1, 0);
        var stripHeight = FilmstripList.ActualHeight > 0 ? FilmstripList.ActualHeight : Math.Max(_viewModel.TimelineAreaHeight - 6, 8);
        var count = Math.Clamp((int)Math.Round(stripWidth / (stripHeight * cellWidth / cellHeight)), 1, 80);

        var pictures = new List<ImageSource>(count);
        for (var i = 0; i < count; i++)
        {
            var index = Math.Clamp((int)((i + 0.5) / count * duration / interval), 0, last);
            var picture = new CroppedBitmap(sheet, new Int32Rect(index % grid * cellWidth, index / grid * cellHeight, cellWidth, cellHeight));
            picture.Freeze();
            pictures.Add(picture);
        }

        FilmstripList.ItemsSource = pictures;
    }

    private void TimelineSlider_MouseMove(object sender, MouseEventArgs e)
    {
        // A right-drag in progress: the stretch being marked follows the pointer.
        if (_rangeDragStartMs is { } dragStart)
        {
            _rangeDragEndMs = PullToIFrame(TimelineMsAt(e.GetPosition(TimelineSlider).X));
            _viewModel.StatusText = $"New segment: {TimeDisplay.Format(Math.Min(dragStart, _rangeDragEndMs) / 1000)} to {TimeDisplay.Format(Math.Max(dragStart, _rangeDragEndMs) / 1000)}";
            RedrawSegments();
            return;
        }

        var interval = _viewModel.SpriteIntervalSeconds;
        if (_spriteSheet is null || !_viewModel.ShowHoverPreviews || interval <= 0 || _viewModel.DurationMs <= 0 || TimelineSlider.ActualWidth <= 0)
        {
            PreviewPopup.IsOpen = false;
            return;
        }

        // The track is shorter than the slider by half a thumb at each end.
        const double inset = 5.5;
        var x = e.GetPosition(TimelineSlider).X;
        var fraction = Math.Clamp((x - inset) / (TimelineSlider.ActualWidth - 2 * inset), 0, 1);
        var seconds = fraction * _viewModel.DurationMs / 1000;

        // The sheet is a grid read left to right, top to bottom, one thumbnail per interval.
        const int grid = FfmpegRunner.SpriteGridSize;
        var last = Math.Min(grid * grid, (int)Math.Ceiling(_viewModel.DurationMs / 1000 / interval)) - 1;
        var index = Math.Clamp((int)(seconds / interval), 0, Math.Max(last, 0));

        var thumbnailWidth = _spriteSheet.PixelWidth / grid;
        var thumbnailHeight = _spriteSheet.PixelHeight / grid;
        if (thumbnailWidth <= 0 || thumbnailHeight <= 0)
            return;

        PreviewImage.Source = new CroppedBitmap(
            _spriteSheet, new Int32Rect(index % grid * thumbnailWidth, index / grid * thumbnailHeight, thumbnailWidth, thumbnailHeight));
        // Shown larger or smaller than generated, as set in Settings.
        var zoom = AppSettings.Current.HoverPreviewZoom;
        PreviewImage.Width = thumbnailWidth * zoom;
        PreviewImage.Height = thumbnailHeight * zoom;
        PreviewTime.Text = TimeDisplay.Format(seconds);

        // Centred on the pointer, sitting just above the timeline.
        PreviewPopup.HorizontalOffset = x - thumbnailWidth * zoom / 2.0;
        PreviewPopup.VerticalOffset = -(thumbnailHeight * zoom + 40);
        PreviewPopup.IsOpen = true;
    }

    private void TimelineSlider_MouseLeave(object sender, MouseEventArgs e) => PreviewPopup.IsOpen = false;

    private static Point ClampTo(Rect area, Point point) =>
        new(Math.Clamp(point.X, area.Left, area.Right), Math.Clamp(point.Y, area.Top, area.Bottom));

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 0);
}
