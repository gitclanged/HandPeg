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
    /// Looks for a newer HandPeg release in the background and, once it has been downloaded, offers it in the
    /// status bar. Nothing waits on this, and a failed check (offline, no releases yet) only goes to the log.
    /// </summary>
    private async Task CheckForHandPegUpdateAsync()
    {
        if (await AppUpdater.CheckAndDownloadAsync() is not { } version)
            return;

        UpdateNoticeText.Text = $"HandPeg {version} is ready.";
        UpdateNotice.Visibility = Visibility.Visible;
    }

    private void RestartToUpdate_Click(object sender, RoutedEventArgs e)
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
        var building = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        var built = building.ElapsedMilliseconds;
        DataContext = _viewModel;

        // Asked for even when already handled: the slider itself answers a press beside its playhead (it moves
        // the playhead there) and marks it handled, which would keep it from this window altogether.
        ApplyStartupWindowSize();
        // Asked for even when already handled: the window's own drag handlers mark these events handled
        // (that is how they refuse a drop over a text box), which would keep them from the overlay.
        AddHandler(PreviewDragEnterEvent, new DragEventHandler((_, e) => ShowDropOverlay(e)), handledEventsToo: true);
        AddHandler(PreviewDragOverEvent, new DragEventHandler((_, e) => ShowDropOverlay(e)), handledEventsToo: true);
        AddHandler(PreviewDragLeaveEvent, new DragEventHandler((_, e) => LeaveDropOverlay(e)), handledEventsToo: true);
        AddHandler(PreviewDropEvent, new DragEventHandler((_, _) => Dispatcher.BeginInvoke(HideDropOverlay)), handledEventsToo: true);
        _scrubPace.Tick += ScrubPace_Tick;

        // A proxy became ready, or the mode changed: the player goes over to what it should now have open.
        _viewModel.PlayerSourceChanged += () => Dispatcher.BeginInvoke(() => SyncPlayerSource());
        _dropLeave.Tick += (_, _) => HideDropOverlay();
        _settingsFlyoutClose.Tick += SettingsFlyoutClose_Tick;
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
        SidePanes.ClipToBounds = true;
        Application.Current.Resources["GlobalScrollBarThickness"] = Math.Clamp(AppSettings.Current.ScrollbarThickness, 4, 16);
        TimelineSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(TimelineSlider_PreviewMouseLeftButtonDown), handledEventsToo: true);

        // The first-run window may have been shown before this one, and WPF takes the first window made
        // as the main one.
        Application.Current.MainWindow = this;

        // Downloads beyond the cache size, and temp folders of sessions that did not close cleanly.
        YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize);
        SessionPaths.DeleteAbandonedSessions();

        _viewModel.Segments.CollectionChanged += (_, _) => RedrawSegments();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.MediaLoaded += PlayMedia;
        _viewModel.PreviewRendered += ShowPreview;
        _viewModel.AskOverwrite = AskOverwrite;
        _viewModel.Confirm = (title, message, confirm) => new ConfirmDialog(title, message, confirm) { Owner = this }.ShowDialog() == true;
        _viewModel.Choose = (title, message, first, second) =>
        {
            var dialog = new ConfirmDialog(title, message, first, second) { Owner = this };
            return dialog.ShowDialog() != true ? 0 : dialog.ChoseOther ? 2 : 1;
        };

        // Layers added or removed while they are being arranged change what is on the canvas.
        _viewModel.Layers.CollectionChanged += (_, _) => RedrawLayout();

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += (_, _) => _ = CheckForHandPegUpdateAsync();

        _viewModel.LiveFilterInvalidated += ScheduleLiveFilter;
        _viewModel.LiveFilterInvalidated += () => _liveSoundRefused = false;

        // The time bars on the Layers tab follow the cuts, the layers, and anything about a layer that changes.
        _viewModel.LiveFilterInvalidated += ScheduleClipRedraw;
        _viewModel.Segments.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.Layers.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.TimelineChanged += ScheduleClipRedraw;
        _viewModel.TimelineChanged += DrawKeyTimeline;
        _viewModel.MainLayer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == Layer.KeysChanged)
                DrawKeyTimeline();
        };
        Loaded += (_, _) => ApplyPaneLayout();
        Loaded += (_, _) => UpdateBottomBar();
        _viewModel.ActionLogged += FlashStatus;
        _viewModel.LayerLoaded += RedrawLayout;

        // Edit layout may already be on when the window opens (it is, in Editor Mode): the pane opens with it.
        Loaded += (_, _) => UpdateLayoutPane();
        SizeChanged += (_, _) => UpdateBottomBar();

        // A double-click on any slider or number box puts it back: to the style preset's value where there is one.
        NumericInput.ResetValue = _viewModel.GetResetValue;
        NumericInput.RegisterDoubleClickReset();
        _viewModel.MainLayer.PropertyChanged += (_, _) => ScheduleClipRedraw();
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
            // How long the launch took, for the log: from the process starting to the window being on screen.
            var sinceStart = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppLog.Write(FormattableString.Invariant($"Startup: window drawn {sinceStart:0} ms after the process started (window built in {built} ms)."));

            _shellDrawn = true;
            Dispatcher.BeginInvoke(() => _ = EnsurePlayerAsync(), System.Windows.Threading.DispatcherPriority.Loaded);

            StyleLibrary.EnsureBuiltIns();

            // Off this thread and not waited for: last week's proxies and last session's leftovers go.
            ProxyCache.SweepInBackground();

            // No window opens over the program at launch any more: it starts straight into its workspace. What
            // the launch window offered is offered while a file is dragged in instead, and its targets are made
            // now, once everything else is up and idle.
            Dispatcher.BeginInvoke(BuildDropTargets, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };

        // A tab other than the first may be the one that is open.
        RealizeTab(SettingsTabs.SelectedItem);
    }

    /// <summary>
    /// Shows the launch window over this one, when it is switched on, and opens what was chosen in it:
    /// a video, a video with a preset, or a project. Closing it without choosing is a blank project.
    /// </summary>
    private void ShowLaunchWindow()
    {
        // An Editor Mode thing: Encoder Mode goes straight to work.
        if (AppSettings.Current is not { FirstRunComplete: true, SplashPresetCount: > 0, UiMode: AppSettings.EditorMode })
            return;

        OpenLaunchWindow();
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

        // A dark or a light title bar, to match the theme.
        ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>
    /// Closing with changes that were not saved as a project asks first, when the settings say to: save and
    /// close, close without saving, or go back.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        RememberWindowSize();
        if (!AppSettings.Current.PromptToSaveOnExit || !_viewModel.HasUnsavedChanges)
            return;

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
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // Encodes, downloads and probes still running go first, children included, so nothing is left
        // behind in the background and nothing still holds a file in the session folder.
        _windowClosing.Cancel();
        ProcessPipes.KillAll();
        _viewModel.Shutdown();
        _mpv?.Close();

        // The most recent downloads stay, so the same URL loads without downloading again.
        YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize);

        // Handed to a process of its own, which is not waited for: closing takes no longer for it.
        if (AppSettings.Current.ClearProxyCacheOnExit)
            ProxyCache.ClearInBackground();
    }

    // ----- Hotkeys -----

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Saving, before anything else sees the keys: a text box, which has uses of its own for some key
        // combinations, never gets these two. The window's key bindings (see the XAML) are the same commands,
        // for the keys that reach it by the ordinary road.
        //
        // Save As is Ctrl+Alt+S, not Ctrl+Shift+S: that one is taken, on computers with AMD's graphics
        // software, by its "save instant replay" hotkey, which is registered with Windows for every program,
        // so the keys are never given to HandPeg at all. (With Alt held, WPF reports the key as a system key.)
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressed == Key.S && Keyboard.Modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                _viewModel.SaveProjectAsCommand.Execute(null);
            else
                _viewModel.SaveProjectNowCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Typing stays typing (a text box has an undo of its own), and a drop-down keeps its own keys.
        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase or ComboBox or ComboBoxItem)
            return;

        // Undo and redo, for the timeline.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y)
        {
            if (e.Key == Key.Z)
                Undo_Click(this, new RoutedEventArgs());
            else
                Redo_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        // Delete removes what is selected on a time bar. In the rules grid it is the grid's own key.
        if (e.Key == Key.Delete && _viewModel.IsEditorMode && focused is not DataGridCell)
        {
            DeleteSelectedClip();
            e.Handled = true;
            return;
        }

        // A slider other than the timeline keeps its arrow keys (volume, zoom, opacity...).
        var arrowsBelongToSlider = focused is Slider slider && !ReferenceEquals(slider, TimelineSlider);

        switch (e.Key)
        {
            case Key.Space:
                PlayPause_Click(this, new RoutedEventArgs());
                break;

            case Key.Left when !arrowsBelongToSlider:
                StepFrames(-1);
                break;

            case Key.Right when !arrowsBelongToSlider:
                StepFrames(1);
                break;

            case Key.I when _viewModel.AddStartPointCommand.CanExecute(null):
                _viewModel.AddStartPointCommand.Execute(null);
                break;

            case Key.S when _viewModel.IsEditorMode && _viewModel.HasSource:
                SplitSelectedClip();
                break;

            // The transport keys of every editor: J back, K stop, L forward; J or L again goes faster.
            case Key.J:
                Shuttle(-1);
                break;

            case Key.K:
                Shuttle(0);
                break;

            case Key.L:
                Shuttle(1);
                break;

            case Key.O when _viewModel.AddStopPointCommand.CanExecute(null):
                _viewModel.AddStopPointCommand.Execute(null);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    // ----- Source / destination -----

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
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
        if (dialog.ShowDialog() == true && dialog.ProjectToLoad is { } project)
            await _viewModel.LoadProjectAsync(project);
    }

    private void ManualCut_Click(object sender, RoutedEventArgs e) =>
        new ManualCutWindow(_viewModel) { Owner = this }.ShowDialog();

    /// <summary>
    /// Remove Dead Air: first how quiet counts as silence (and, in Editor Mode, whether to mark or delete),
    /// then the search, on whatever is selected: a video layer, an audio track, or otherwise the main video.
    /// </summary>
    private void RemoveDeadAir_Click(object sender, RoutedEventArgs e) => RunDeadAir(_selectedClip switch
    {
        Layer { CarriesSound: true } layer => layer,
        LayerSound sound => sound.Layer,
        AudioClip audio => audio.Track,
        _ => null,
    });

    /// <summary>The Video Combinator: two videos joined into one, saved to a file or sent straight to the editor.</summary>
    private void OpenCombinator_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CombinatorDialog(_viewModel) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SendToEditorPath is { } combined)
            LoadDroppedFile(combined);
    }

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
        ViewsPlaybackOptions.Visibility = fits || !_viewModel.IsEditorMode ? Visibility.Collapsed : Visibility.Visible;
    }

    // ----- A track's controls: in its row while there is room, in a flyout from its row when there is not -----

    // What a row needs beside its controls: the name block, and a time bar wide enough to be of use.
    private const double NarrowestTrackList = 232 + 250 + 700;

    private void TrackList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { ActualWidth: > 0 } list)
            _viewModel.CompactTrackControls = _viewModel.IsEditorMode && list.ActualWidth < NarrowestTrackList;
    }

    // The button that opened a flyout would, clicked again to close it, open it straight back up; so it is deaf while the flyout is open.
    private void TrackControlsPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement button })
            button.IsHitTestVisible = false;
    }

    private void TrackControlsPopup_Closed(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement button })
            Dispatcher.BeginInvoke(() => button.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void TimelineOptionsBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTransportMinWidth();

    /// <summary>
    /// However narrow the player gets, the Views button keeps its place at the left of the playback bar: with
    /// the checkboxes folded into it, it is the only way to them. In Encoder Mode there is nothing there to keep room for.
    /// </summary>
    private void UpdateTransportMinWidth() =>
        TransportRow.ColumnDefinitions[0].MinWidth = TimelineOptionsBar.IsVisible
            ? Math.Ceiling((EditorViewsButton.ActualWidth > 0 ? EditorViewsButton.ActualWidth : 90) + 22)
            : 0;

    // ----- The Settings button's flyout: the queue and the combinator, while the pointer is on the button or on it -----

    private readonly System.Windows.Threading.DispatcherTimer _settingsFlyoutClose = new() { Interval = TimeSpan.FromMilliseconds(260) };

    // ----- The window's size at startup -----

    private void ApplyStartupWindowSize()
    {
        var settings = AppSettings.Current;
        switch (settings.StartupWindowSize)
        {
            case AppSettings.MaximizedWindow:
                WindowState = WindowState.Maximized;
                break;
            case AppSettings.RememberWindowSize when settings.LastWindowWidth >= MinWidth && settings.LastWindowHeight >= MinHeight:
                // No larger than the screen it opens on, should that be a smaller one than it was left on.
                Width = Math.Min(settings.LastWindowWidth, SystemParameters.VirtualScreenWidth);
                Height = Math.Min(settings.LastWindowHeight, SystemParameters.VirtualScreenHeight);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                if (settings.LastWindowMaximized)
                    WindowState = WindowState.Maximized;
                break;
        }
    }

    /// <summary>Notes how the window was left, for "Remember Last Size": its size while not maximized, and whether it was.</summary>
    private void RememberWindowSize()
    {
        var settings = AppSettings.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < MinWidth || bounds.Height < MinHeight)
            return;

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

    // ----- The drop overlay: targets for a file that is being dragged in -----

    private bool _dropTargetsBuilt;
    private string _dropTargetsMode = "";

    /// <summary>
    /// Makes the overlay's targets for the mode in use: style presets in Editor Mode, encoding presets in
    /// Encoder Mode, and the Social Squisher's presets in both. Their background is the accent colour,
    /// toned down, so that a whole screen of them does not shout.
    /// </summary>
    private void BuildDropTargets()
    {
        var settings = AppSettings.Current;
        var count = Math.Clamp(settings.SplashPresetCount, 0, 8);
        var presets = _viewModel.IsEditorMode
            ? StyleFile.ForLaunch().Take(count).Select(style => new DropTargetItem(style.Name, DropTargetItem.StyleIcon, style)).ToList()
            : _viewModel.Presets.Take(count).Select(preset => new DropTargetItem(preset.Name, DropTargetItem.EncodeIcon, preset)).ToList();
        var squish = settings.ShowSquisher
            ? settings.SquisherPresets.Where(p => p.TargetSizeMb > 0).Take(Math.Clamp(settings.SquisherPresetCount, 1, 12)).Select(p => new DropTargetItem(p.Name, p.IconData, p)).ToList()
            : [];
        (DropPresetList.ItemsSource, DropSquishList.ItemsSource) = (presets, squish);
        DropPresetTitle.Text = _viewModel.IsEditorMode ? "Open with a style preset" : "Open with an encoder preset";
        DropPresetSection.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DropSquishSection.Visibility = squish.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        var grey = (byte)((accent.R + accent.G + accent.B) / 3);
        byte Mute(byte channel) => (byte)(channel * 0.55 + grey * 0.30 + 20);
        var muted = new SolidColorBrush(Color.FromRgb(Mute(accent.R), Mute(accent.G), Mute(accent.B)));
        muted.Freeze();
        Resources["DropTargetBrush"] = muted;
        (_dropTargetsBuilt, _dropTargetsMode) = (true, _viewModel.IsEditorMode ? "editor" : "encoder");
    }

    // A drag "leaves" every element it passes out of, on its way to the next one inside the window, and where
    // the pointer is said to be at that moment cannot be relied on at the window's edge. So leaving only
    // starts a short wait: the drag is still in the window if it is heard from again before the wait is out.
    private readonly System.Windows.Threading.DispatcherTimer _dropLeave = new() { Interval = TimeSpan.FromMilliseconds(140) };

    private void ShowDropOverlay(DragEventArgs e)
    {
        _dropLeave.Stop();
        if (DropOverlay.Visibility == Visibility.Visible || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        // Only into an empty window. With a video or a project open, a file dragged in is on its way to the
        // timeline, the layers or the audio tab, and nothing is put in its way.
        if (_viewModel.HasSource || _viewModel.IsBusy)
            return;

        // The mode may have changed, or the presets: they are made again, which is seven buttons' work.
        BuildDropTargets();
        DropOverlay.Visibility = Visibility.Visible;

        // The player is a native window, which WPF cannot draw over: it would stay bright in the middle of the
        // dimmed window. It is hidden for as long as the overlay is up.
        VideoView.Visibility = Visibility.Hidden;
    }

    // A file that leaves the window takes the overlay with it, a moment later (see above).
    private void LeaveDropOverlay(DragEventArgs e)
    {
        _dropLeave.Stop();
        _dropLeave.Start();
    }

    private void HideDropOverlay()
    {
        _dropLeave.Stop();
        DropOverlay.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
    }

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 0.7;
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    /// <summary>A file let go on a target: opened with that style, opened and given that encoding preset, or squished for that preset.</summary>
    private void DropTarget_Drop(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: DropTargetItem item })
            RouteDrop(item, e);
    }

    private void RouteDrop(DropTargetItem item, DragEventArgs e)
    {
        HideDropOverlay();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files || !File.Exists(files[0]))
            return;

        var file = files[0];
        AppLog.Write($"Dropped {Path.GetFileName(file)} on the overlay target {item.Name}.");
        switch (item.Target)
        {
            case StyleFile style:
                _ = _viewModel.LoadFileWithStyleAsync(file, style.Path);
                break;
            case EncodingPreset preset:
                _ = LoadWithPresetAsync(file, preset);
                break;
            case SquishPreset squish:
                // Straight to the processing square: the window is not shown opening and then shrinking.
                OpenLaunchWindow(startHidden: true);
                var window = _launchWindow;
                Dispatcher.BeginInvoke(() => window?.StartSquish(file, squish, instant: true), System.Windows.Threading.DispatcherPriority.Loaded);
                break;
        }
    }

    // Opens the file as the Source pane would, and once it is open applies the encoding preset to it.
    private async Task LoadWithPresetAsync(string file, EncodingPreset preset)
    {
        _viewModel.SourcePath = file;
        _viewModel.LoadSourceCommand.Execute(null);
        await Task.Delay(300);
        for (var waited = 0; _viewModel.IsBusy && waited < 600; waited++)
            await Task.Delay(100);
        if (_viewModel.Presets.Contains(preset))
            _viewModel.SelectedPreset = preset;
    }

    // ----- Fitting -----

    private void PlayerArea_SizeChanged(object sender, SizeChangedEventArgs e) => ClampSideColumn();

    /// <summary>
    /// Keeps the side column inside the window. It has a width of its own (the one it was dragged to), which
    /// a narrower window has no room for: it then takes what is left beside the player's least width, down to
    /// its own least, and is given its width back when there is room again. It never lies over the player.
    /// </summary>
    private void ClampSideColumn()
    {
        if (SidePanes.Visibility != Visibility.Visible || PlayerArea.ActualWidth <= 0)
            return;

        var settings = AppSettings.Current;
        var wanted = settings.SidePanesWidth > 0 ? settings.SidePanesWidth : DefaultSidePanesWidth;
        var room = PlayerArea.ActualWidth - PlayerColumn.MinWidth - 10;
        var width = Math.Max(Math.Min(wanted, room), SideColumn.MinWidth);
        if (Math.Abs(SideColumn.Width.Value - width) > 0.5 || !SideColumn.Width.IsAbsolute)
            SideColumn.Width = new GridLength(width);
    }

    // ----- The time bars' view: zoomed and moved along together -----
    // Every layer and audio time bar shows the same stretch of the sequence. Ctrl + wheel over any of them
    // zooms all of them about the pointer; Shift + wheel, or a drag with the middle button, moves them along.
    // The master timeline follows when "Sync Master Timeline Stretch" is on in the Views menu.

    private double _timelineZoom = 1, _timelineOffsetSeconds;

    private void ClipTrack_ZoomRequested(TimelineTrack track, double factor, double x)
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0 || track.ActualWidth <= 0)
            return;

        // The moment under the pointer stays under it.
        var under = _timelineOffsetSeconds + x / (track.ActualWidth * _timelineZoom) * seconds;
        _timelineZoom = Math.Clamp(_timelineZoom * factor, 1, 400);
        _timelineOffsetSeconds = under - x / (track.ActualWidth * _timelineZoom) * seconds;
        ApplyTimelineView();
    }

    private void ClipTrack_PanRequested(TimelineTrack track, double pixels)
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0 || track.ActualWidth <= 0)
            return;

        _timelineOffsetSeconds += pixels / (track.ActualWidth * _timelineZoom) * seconds;
        ApplyTimelineView();
    }

    private void ApplyTimelineView()
    {
        var seconds = _viewModel.DurationMs / 1000;
        _timelineOffsetSeconds = Math.Clamp(_timelineOffsetSeconds, 0, Math.Max(seconds * (1 - 1 / _timelineZoom), 0));
        foreach (var track in _clipTracks.ToList())
            DrawTrack(track);
        UpdateMasterStretch();
        _viewModel.StatusText = _timelineZoom > 1.001
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Time bars zoomed {_timelineZoom:0.#}x (Ctrl + wheel zooms, Shift + wheel or a middle-button drag moves along).")
            : "Time bars show the whole sequence.";
    }

    private void MasterTimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMasterStretch();

    /// <summary>Stretches the master timeline to the time bars' zoom and slides it to their place, while that is switched on; otherwise it fills its place as it always did.</summary>
    private void UpdateMasterStretch()
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (!_viewModel.SyncMasterTimeline || _timelineZoom <= 1.001 || seconds <= 0 || MasterTimelineViewport.ActualWidth <= 0)
        {
            (MasterTimelineContent.Width, MasterTimelineContent.HorizontalAlignment, MasterTimelineContent.Margin) = (double.NaN, HorizontalAlignment.Stretch, new Thickness(0));
            return;
        }

        var width = MasterTimelineViewport.ActualWidth * _timelineZoom;
        MasterTimelineContent.HorizontalAlignment = HorizontalAlignment.Left;
        MasterTimelineContent.Width = width;
        MasterTimelineContent.Margin = new Thickness(-_timelineOffsetSeconds / seconds * width, 0, 0, 0);
    }

    private void FlyoutSquisher_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.IsOpen = false;
        OpenLaunchWindow();
    }

    private void SettingsHost_MouseEnter(object sender, MouseEventArgs e)
    {
        _viewModel.RefreshAutosave();
        _settingsFlyoutClose.Stop();
        SettingsFlyout.IsOpen = true;
    }

    // Leaving the button or the flyout does not close it at once: the pointer is given a moment to cross from one to the other.
    private void SettingsFlyout_MouseLeave(object sender, MouseEventArgs e)
    {
        _settingsFlyoutClose.Stop();
        _settingsFlyoutClose.Start();
    }

    private void SettingsFlyoutClose_Tick(object? sender, EventArgs e)
    {
        _settingsFlyoutClose.Stop();
        if (!SettingsHost.IsMouseOver && !SettingsFlyoutPanel.IsMouseOver)
            SettingsFlyout.IsOpen = false;
    }

    private void FlyoutQueue_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.IsOpen = false;
        OpenQueue_Click(sender, e);
    }

    private void FlyoutCombinator_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.IsOpen = false;
        OpenCombinator_Click(sender, e);
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

    // ----- Drag and drop -----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Files from Explorer are answered on the way down to whatever they are over, not on the way back up.
    // The rows of the Layers and Audio tabs are full of number boxes, and a text box answers a drag itself
    // ("no" to files) before the window is asked: over those, a drop was refused. Seen first, here, a file is
    // accepted wherever on the window it is, and the boxes keep their own dragging of text.

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        // Let go on one of the overlay's targets: that target has it. (This handler takes every file dropped on
        // the window before any element sees it, which is why the targets' own Drop was never reached.)
        if (DropOverlay.Visibility == Visibility.Visible && (e.OriginalSource as FrameworkElement)?.DataContext is DropTargetItem item)
        {
            e.Handled = true;
            RouteDrop(item, e);
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            Window_Drop(sender, e);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;

        e.Handled = true;
        var file = files[0];

        // Dropped on the layers, with a video open: the files become layers of it, at the moment they were dropped on.
        if (_viewModel.HasSource && LayersList is { IsVisible: true })
        {
            var at = e.GetPosition(LayersList);
            if (at.X >= 0 && at.Y >= 0 && at.X <= LayersList.ActualWidth && at.Y <= LayersList.ActualHeight)
            {
                var seconds = DropTime(e);
                Dispatcher.BeginInvoke(() => _ = AddDroppedLayersAsync(files, seconds));
                return;
            }
        }

        // Dropped on the Audio tab, with a video open: the files' sound goes onto the timeline, at the moment it was dropped on.
        if (_viewModel.HasSource && AudioPanel is { IsVisible: true })
        {
            var at = e.GetPosition(AudioPanel);
            if (at.X >= 0 && at.Y >= 0 && at.X <= AudioPanel.ActualWidth && at.Y <= AudioPanel.ActualHeight)
            {
                var seconds = DropTime(e);
                Dispatcher.BeginInvoke(() => _ = AddDroppedAudioAsync(files, seconds));
                return;
            }
        }

        // The dialog must not open inside the drop itself: Explorer waits, frozen, until the drop handler returns.
        Dispatcher.BeginInvoke(() => LoadDroppedFile(file));
    }

    /// <summary>Adds the sound of dropped files to the timeline: audio files, or the audio of videos.</summary>
    private async Task AddDroppedAudioAsync(string[] files, double seconds)
    {
        Activate();
        var added = 0;
        foreach (var file in files.Where(File.Exists))
        {
            if (await _viewModel.AddAudioLayerAsync(file, seconds))
                added++;
        }

        if (added > 1)
            _viewModel.StatusText = $"Added {added} audio clips. Each is mixed into the first audio track; drag a block to move it.";
        ScheduleClipRedraw();
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>The moment a drop on the layers points at: where it fell along the time bars, or 0 beside them.</summary>
    private double DropTime(DragEventArgs e)
    {
        if (_clipTracks.FirstOrDefault(t => t is { IsVisible: true, ActualWidth: > 0 }) is not { } track)
            return 0;

        var x = e.GetPosition(track).X;
        return x > 0 && x <= track.ActualWidth ? x / track.ActualWidth * _viewModel.DurationMs / 1000 : 0;
    }

    /// <summary>Adds dropped files as layers: pictures as image layers, anything else as video layers.</summary>
    private async Task AddDroppedLayersAsync(string[] files, double seconds)
    {
        Activate();
        if (!_viewModel.IsVideoReencoded)
        {
            _viewModel.StatusText = "Layers need the video to be encoded: choose a video encoder other than Copy on the Video tab, then drop the file again.";
            return;
        }

        var added = 0;
        foreach (var file in files.Where(File.Exists))
        {
            if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) ? _viewModel.AddImageLayer(file, seconds) : await _viewModel.AddVideoLayerAsync(file, seconds))
                added++;
        }

        if (added > 1)
            _viewModel.StatusText = $"Added {added} layers{(seconds > 0.01 ? $" at {TimeSpan.FromSeconds(seconds):g}" : "")}.";
        ScheduleClipRedraw();
    }

    private void LoadDroppedFile(string file)
    {
        Activate();

        if (_viewModel.IsBusy)
        {
            _viewModel.StatusText = "Wait for the running operation to finish, or cancel it, before dropping another video.";
            return;
        }

        if (_viewModel.HasSource)
        {
            // Save what is open as a project first, load over it, or leave things as they are.
            var dialog = new ReplaceVideoDialog(_viewModel.SourcePath, file) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Choice == ReplaceVideoChoice.Cancel)
                return;

            // A project that could not be saved must not be followed by losing what it was meant to keep.
            if (dialog.Choice == ReplaceVideoChoice.SaveAndLoad
                && !_viewModel.SaveProject(dialog.ProjectName).StartsWith("Project saved", StringComparison.Ordinal))
            {
                return;
            }
        }

        _viewModel.LoadFile(file);
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

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_viewModel) { Owner = this }.ShowDialog() == true)
        {
            _viewModel.OnSettingsSaved();
            _mpv?.SetHardwareAcceleration(AppSettings.Current.PlayerHardwareAcceleration);
        }

        // The player may just have been installed from the dependency list.
        _ = EnsurePlayerAsync();
    }

    private void SourceTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _viewModel.LoadSourceCommand.Execute(null);
    }

    // The Source pane's one button, and Enter in its box: with the box empty there is nothing to load, and
    // it browses for a file; with a path or a URL in it, it loads that.
    private void SourceAction_Click(object sender, RoutedEventArgs e)
    {
        var path = (_viewModel.SourcePath ?? "").Trim().Trim('"');
        if (path.Length == 0)
            BrowseSource_Click(sender, e);
        else if (path.EndsWith(ProjectStore.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            _ = _viewModel.LoadProjectAsync(path);   // A project file in the Source box opens the project.
        else
            _viewModel.LoadSourceCommand.Execute(null);
    }

    private void SourcePaneBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SourceAction_Click(sender, e);
    }

    // ----- The player: libmpv -----
    // mpv draws into the video surface, reports where it is as it plays, and can run an FFmpeg filter graph on
    // the picture (its lavfi-complex property). That graph is Live Preview: with the box ticked it is the one
    // the export would use for the picture, rebuilt whenever a setting changes; unticked, there is none and
    // the source is shown as it is.

    private MpvPlayer? _mpv;
    private bool _startingPlayer;
    private bool _shellDrawn;

    // What to open once the player exists: a video loaded before it had started, or the one a reload is bringing back.
    private (string Path, long StartMs, bool Paused)? _pendingMedia;

    // The graph mpv is running, and one it refused: that one is not offered again until the settings change.
    private string _liveGraph = "";
    private string? _failedLiveGraph;

    // Sliders report every step of a drag; the graph is rebuilt once they have been still for a moment.
    private readonly System.Windows.Threading.DispatcherTimer _liveFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private bool PlayerIsPlaying => _mpv is { IsPlaying: true };

    private void PlayerSetPause(bool paused) => _mpv?.SetPause(paused);

    private void PlayerSeek(long positionMs, bool exact = true)
    {
        _mpv?.Seek(positionMs, exact);

        // The layers' sound is laid out in the graph from one moment on (see ApplyLiveFilter): after a jump it
        // is laid out again, from where the jump lands.
        if (LiveGraphHasSound)
        {
            _liveAudioSeekMs = positionMs;
            ScheduleLiveFilter();
        }
    }

    /// <summary>
    /// Starts the player when there is none yet. Loading libmpv and starting it take a moment, and happen on
    /// the thread pool: the window is never held up by them. Without libmpv (not downloaded yet) there is
    /// simply no player; everything else works, and this is tried again when the dependencies change.
    /// </summary>
    private async Task EnsurePlayerAsync()
    {
        if (_mpv is not null || _startingPlayer || !_shellDrawn)
            return;

        var surface = VideoView.SurfaceHandle;
        if (surface == IntPtr.Zero)
            return;

        // Not there yet: it is fetched by itself, with a notice in the empty player saying so.
        if (!MpvPlayer.IsInstalled)
        {
            _ = FetchPlayerAsync();
            return;
        }

        EngineNotice.Visibility = Visibility.Collapsed;

        _startingPlayer = true;
        try
        {
            var acceleration = AppSettings.Current.PlayerHardwareAcceleration;
            var player = await Task.Run(() => MpvPlayer.Create(surface, acceleration));

            // mpv raises its events on a thread of its own; they are passed on only while this is still the player.
            void OnUi(Action action) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(player, _mpv))
                    action();
            });

            player.TimeChanged += time => OnUi(() => OnPlayerTimeChanged(time));

            // The timeline's length is the sequence's, which the view model works out; the player's word is
            // only needed for a file that could not be inspected.
            player.DurationChanged += length => OnUi(() => _viewModel.ReportPlayerDuration(length / 1000.0, _playerSource == _viewModel.LocalMediaPath));
            player.StateChanged += () => OnUi(() =>
            {
                var wasPlaying = _viewModel.IsPlaying;
                _viewModel.IsPlaying = player.IsPlaying;

                // The layers' sound is in the graph only while it plays: it comes in as playback starts, from
                // where it starts, and goes again when it stops.
                if (wasPlaying != player.IsPlaying && _viewModel.LivePreview && (player.IsPlaying || LiveGraphHasSound))
                    ApplyLiveFilter();
            });
            player.FileLoaded += () => OnUi(() =>
            {
                // Opening a file takes the graph off; it is given again, for this file.
                _liveGraph = "";
                player.SetAudioFilter(_viewModel.PlayerAudioFilter);
                if (!SyncPlayerSource())
                    ApplyLiveFilter();
                if (_viewModel.SoloTrack is not null)
                    ApplySoloTrack();
            });
            player.ErrorLogged += text => OnUi(() => OnPlayerError(text));

            _mpv = player;
            (_liveGraph, _failedLiveGraph) = ("", null);
            player.SetVolume(_viewModel.Volume);
            player.SetSpeed(_viewModel.PlaybackRate);

            // A video may have been loaded while the player was still starting.
            if (_pendingMedia is null && _viewModel.HasSource && File.Exists(_viewModel.LocalMediaPath))
                _pendingMedia = (_viewModel.PlayerSource, (long)_viewModel.PositionMs, true);
            if (_pendingMedia is { } media)
            {
                _pendingMedia = null;
                _playerSource = media.Path;
                player.Open(media.Path, media.StartMs, media.Paused);
            }
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Write("The player could not be started", ex);
            _viewModel.StatusText = $"The video player could not be started: {ex.Message}";
        }
        finally
        {
            _startingPlayer = false;
        }
    }

    private readonly CancellationTokenSource _windowClosing = new();
    private bool _fetchingPlayer;
    private bool _playerFetchFailed;

    /// <summary>
    /// Downloads libmpv, the video engine, when it is missing: by itself, in the background, with a notice
    /// over the empty player so that it is clear why there is no picture yet. Tried once per session; when
    /// it fails the notice says so, and the dependency list in the settings is the way to try again.
    /// </summary>
    private async Task FetchPlayerAsync()
    {
        if (_fetchingPlayer || _playerFetchFailed)
            return;

        _fetchingPlayer = true;
        (EngineNotice.Visibility, EngineProgress.Visibility, EngineProgress.IsIndeterminate) = (Visibility.Visible, Visibility.Visible, true);
        EngineNoticeTitle.Text = "Downloading Video Engine...";
        EngineNoticeText.Text = "The player (libmpv, about 31 MB) is fetched once. Everything else can be used meanwhile.";
        try
        {
            var token = _windowClosing.Token;
            var latest = await DependencyUpdater.GetLatestAsync(DependencyUpdater.Mpv, token);
            var progress = new Progress<InstallProgress>(report =>
            {
                EngineProgress.IsIndeterminate = report.Fraction < 0;
                EngineProgress.Value = Math.Clamp(report.Fraction, 0, 1);
                EngineNoticeText.Text = report.Text;
            });
            await Task.Run(() => DependencyUpdater.InstallToolAsync(DependencyUpdater.Mpv, latest, progress, token), token);
            _viewModel.OnDependenciesChanged();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex))
        {
            AppLog.Write("The video engine could not be downloaded", ex);
            _playerFetchFailed = true;
            EngineProgress.Visibility = Visibility.Collapsed;
            EngineNoticeTitle.Text = "Video Engine Not Installed";
            EngineNoticeText.Text = $"It could not be downloaded ({ex.Message}). Check the connection, then open Settings (the gear button) and press Install / Update All Dependencies.";
            return;
        }
        finally
        {
            _fetchingPlayer = false;
        }

        await EnsurePlayerAsync();
    }

    /// <summary>Plays a newly loaded source.</summary>
    private void PlayMedia(string path)
    {
        if (!File.Exists(path))
        {
            _viewModel.StatusText = $"The file to play is missing: {path}";
            return;
        }

        StopShuttle();
        OpenInPlayer(_viewModel.PlayerSource, 0, paused: false);
    }

    // What the player has open: the main video's file, or the sequence written out for it (see PlayerSource).
    private string _playerSource = "";

    /// <summary>
    /// Has the player open the sequence as it is now, when that is no longer what it has open: the main video
    /// was moved or trimmed, or a clip now reaches past its end. It carries on from where it was. Returns
    /// whether it had to.
    /// </summary>
    private bool SyncPlayerSource()
    {
        if (_mpv is not { IsLoading: false } player || !_viewModel.HasSource || _reloadingPlayer)
            return false;

        var source = _viewModel.PlayerSource;
        if (source == _playerSource || !File.Exists(_viewModel.LocalMediaPath))
            return false;

        OpenInPlayer(source, (long)_viewModel.PositionMs, paused: !player.IsPlaying);
        return true;
    }

    private void OpenInPlayer(string path, long startMs, bool paused)
    {
        _playerSource = path;
        if (_mpv is not { } player)
        {
            _pendingMedia = (path, startMs, paused);
            _ = EnsurePlayerAsync();
            return;
        }

        // The graph belongs to the file that was playing; the new one gets its own once it is open.
        _liveFilterTimer.Stop();
        (_liveGraph, _failedLiveGraph) = ("", null);
        player.DropFilterGraph();
        player.SetFiltering(false);
        player.Open(path, startMs, paused);
    }

    private bool _reloadingPlayer;

    /// <summary>
    /// Reload Player: for when the player has locked up and the timeline no longer responds. The player is
    /// replaced with a new one, and the video is opened again where it was. The old one is told to quit and
    /// is not waited for: a player that is truly stuck would otherwise take the window down with it.
    /// </summary>
    private async void ReloadPlayer_Click(object sender, RoutedEventArgs e)
    {
        var path = _viewModel.LocalMediaPath;
        if (_reloadingPlayer || !_viewModel.HasSource || !File.Exists(path))
        {
            if (!_reloadingPlayer)
                _viewModel.StatusText = "There is no video loaded to reload.";
            return;
        }

        _reloadingPlayer = true;
        var (resumeAt, wasPlaying) = ((long)_viewModel.PositionMs, _viewModel.IsPlaying);
        _viewModel.StatusText = "Reloading the player...";

        var retired = _mpv;
        _mpv = null;
        _isScrubbing = false;
        _viewModel.IsPlaying = false;
        retired?.Close();

        StopShuttle();
        _pendingMedia = (_viewModel.PlayerSource, resumeAt, !wasPlaying);
        await EnsurePlayerAsync();
        if (_mpv is not null)
            _viewModel.StatusText = $"Player reloaded at {_viewModel.PositionText}.";
        _reloadingPlayer = false;
    }

    // ----- Live Preview -----

    /// <summary>A setting changed: the graph is rebuilt shortly, once the changes have stopped coming.</summary>
    private void ScheduleLiveFilter()
    {
        // Also with Live Preview off: what the player has open follows the sequence either way.
        if (_mpv is null)
            return;

        _liveFilterTimer.Stop();
        _liveFilterTimer.Start();
    }

    private void ClearLiveFilter(MpvPlayer player)
    {
        _liveFilterTimer.Stop();
        _failedLiveGraph = null;
        if (_liveGraph.Length == 0)
            return;

        _liveGraph = "";
        player.SetFilterGraph("");
        player.SetFiltering(false);
    }

    /// <summary>
    /// Hands mpv the filter graph for the settings as they are now, when it is not the one already running:
    /// the export's graph while Live Preview is ticked, none while it is not.
    /// </summary>
    private void ApplyLiveFilter()
    {
        _liveFilterTimer.Stop();

        // A file still being opened gets its graph when it is ready (see FileLoaded), not before.
        if (_mpv is not { IsLoading: false } player || !_viewModel.HasSource)
            return;

        // The sequence has changed shape: it is opened afresh, and gets its graph when that is done.
        if (SyncPlayerSource())
            return;

        // Preview Subtitles: the player draws the captions itself, with Live Preview on or off.
        player.SetSubtitleFile(_viewModel.LiveCaptionsPath);

        // Built for the player as large as it is on screen, in real pixels.
        var dpi = VisualTreeHelper.GetDpi(this);

        // While a rectangle is being drawn on the video (the crop, or a layer's place in the source) the
        // source itself is shown: what is drawn is a part of it, not of the finished frame.
        var live = _viewModel.LivePreview && CurrentOverlayMode == OverlayMode.None;
        string Build(long? audioFromMs) => live
            ? _viewModel.BuildLiveFilterGraph(VideoView.ActualWidth * dpi.DpiScaleX, VideoView.ActualHeight * dpi.DpiScaleY, audioFromMs)
            : "";

        // The sound of the layers is part of the graph only while the video plays forwards: paused, dragged
        // or wound back, nothing is heard anyway, and a graph without sound in it jumps about far faster.
        // It is laid out from one moment on. While nothing calls for another, that moment is kept, so that the
        // graph compares equal to the one running; a jump, or any change to the graph, lays it out afresh
        // from where playback is.
        // Not while one track is played on its own either, nor after mpv has turned such a graph down.
        var wantsSound = player.IsPlaying && !_isScrubbing && _shuttle >= 0 && _viewModel.SoloTrack is null && !_liveSoundRefused;
        var (jumpedTo, now) = (_liveAudioSeekMs, (long)_viewModel.PositionMs);
        _liveAudioSeekMs = null;
        long? kept = wantsSound && jumpedTo is null && LiveGraphHasSound ? _liveAudioFromMs : null;
        long? from = wantsSound ? kept ?? jumpedTo ?? now : null;
        var graph = Build(from);

        // With the layers' sound in the graph, the silence where the main video is not there is part of the
        // graph too; as a filter of the player's it would silence the layers with it.
        var hasSound = graph.Contains(FilterGraphBuilder.LiveAudioOutput, StringComparison.Ordinal);
        player.SetAudioFilter(hasSound ? "" : _viewModel.PlayerAudioFilter);
        if (graph == _liveGraph || (graph.Length > 0 && graph == _failedLiveGraph))
            return;

        if (hasSound && kept is not null && kept != now)
        {
            from = now;
            graph = Build(from);
        }

        // Filters work on frames in main memory, so decoding hands them over there while a graph is running.
        if (graph.Length > 0)
            player.SetFiltering(true);

        (_liveGraph, _liveAudioFromMs) = (graph, hasSound ? from : null);
        var problem = player.SetFilterGraph(graph);
        if (graph.Length == 0)
            player.SetFiltering(false);
        if (problem is not null)
        {
            OnPlayerError($"lavfi-complex: {problem}");
            return;
        }

        // Playback is started again at the moment the sound was laid out from, so that the main video's own
        // sound and the layers' set off together.
        if (hasSound && from is { } start)
            player.Seek(start, exact: true);
    }

    // Whether the graph that is running mixes the layers' sound in, the moment it was laid out from, and a
    // jump made since that it has yet to be laid out for.
    private bool LiveGraphHasSound => _liveGraph.Contains(FilterGraphBuilder.LiveAudioOutput, StringComparison.Ordinal);
    private long? _liveAudioFromMs;
    private long? _liveAudioSeekMs;

    // mpv turned down a graph with the layers' sound in it: the picture's graph is run without, until a setting changes.
    private bool _liveSoundRefused;

    /// <summary>
    /// mpv logged an error. When it is about the filters, the graph is taken off again, so the source keeps
    /// playing, and is not tried a second time until the settings have changed.
    /// </summary>
    private void OnPlayerError(string text)
    {
        AppLog.Write($"Player: {text}");
        if (_mpv is not { } player || _liveGraph.Length == 0
            || !(text.Contains("lavfi", StringComparison.OrdinalIgnoreCase) || text.Contains("filter", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // A graph that mixes the layers' sound in is tried again without it first: the picture may be fine.
        if (LiveGraphHasSound)
        {
            _liveSoundRefused = true;
            _viewModel.StatusText = $"Live Preview could not mix the layers' sound in, and plays the main video's sound alone ({text}).";
            ApplyLiveFilter();
            return;
        }

        var refused = _liveGraph;
        ClearLiveFilter(player);
        _failedLiveGraph = refused;
        _viewModel.StatusText = $"Live Preview could not run the filters as they are set, and shows the source instead ({text}).";
    }

    // ----- One audio track on its own -----

    /// <summary>Has the player play the track chosen on the Audio tab, or the first one when none is.</summary>
    private void ApplySoloTrack() => _mpv?.SetAudioTrack(_viewModel.SoloTrack?.Index ?? 0);

    private void OnSoloTrackChanged()
    {
        if (_mpv is null || !_viewModel.HasSource)
            return;

        ApplySoloTrack();

        // A track played on its own is played without the layers' sound.
        ScheduleLiveFilter();

        // The button is a play button: choosing a track starts it, and letting go of it pauses.
        if (_viewModel.SoloTrack is not null && !PlayerIsPlaying)
            PlayPause_Click(this, new RoutedEventArgs());
        else if (_viewModel.SoloTrack is null && PlayerIsPlaying)
            PlayerSetPause(true);
    }

    // ----- Seeking from a waveform -----
    // A track's waveform on the Audio tab covers the whole video from edge to edge, so a point on it is a time.

    private void Waveform_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement waveform || _viewModel.DurationMs <= 0 || !waveform.CaptureMouse())
            return;

        // Handled, so the list the track's row is in does not take the mouse for itself and end the drag.
        e.Handled = true;
        SeekToWaveformPoint(waveform, e);
    }

    private void Waveform_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } waveform)
            SeekToWaveformPoint(waveform, e);
    }

    private void Waveform_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => (sender as UIElement)?.ReleaseMouseCapture();

    private void SeekToWaveformPoint(FrameworkElement waveform, MouseEventArgs e)
    {
        // Setting the position moves the timeline, which seeks the player.
        if (waveform.ActualWidth > 0)
            _viewModel.PositionMs = Math.Clamp(e.GetPosition(waveform).X / waveform.ActualWidth, 0, 1) * _viewModel.DurationMs;
    }

    // ----- Playback / scrubbing -----

    // ----- J, K, L -----
    // L plays; pressed again it plays twice as fast, then four and eight times. J does the same backwards.
    // K stops. mpv does not play a filtered picture backwards, so going back is done here: the player is
    // paused and sent back along the timeline, step by step, as fast as was asked.

    // 0 while not shuttling; otherwise the speed, negative for backwards.
    private int _shuttle;
    private double _shuttlePositionMs;
    private readonly System.Windows.Threading.DispatcherTimer _shuttleTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void Shuttle(int direction)
    {
        if (!_viewModel.HasSource || _mpv is not { } player)
            return;

        if (direction == 0)
        {
            StopShuttle();
            player.SetPause(true);
            _viewModel.StatusText = "Paused.";
            return;
        }

        // The same key again doubles the speed; the other key turns round, at normal speed. L while it is
        // already playing is the second press.
        var speed = Math.Sign(_shuttle) == direction ? Math.Min(Math.Abs(_shuttle) * 2, 8)
            : direction > 0 && _shuttle == 0 && player.IsPlaying ? 2
            : 1;
        _shuttle = direction * speed;

        if (direction > 0)
        {
            _shuttleTimer.Stop();
            if (player.IsEnded)
                player.Seek(0, exact: true);
            player.SetSpeed(speed);
            player.SetPause(false);
        }
        else
        {
            player.SetPause(true);
            _shuttlePositionMs = _viewModel.PositionMs;
            _shuttleTimer.Start();
        }

        _viewModel.StatusText = $"{(direction > 0 ? "Forward" : "Reverse")} {speed}x. K stops; {(direction > 0 ? "L" : "J")} again goes faster.";
    }

    private void ShuttleBack()
    {
        if (_shuttle >= 0 || _mpv is null)
        {
            _shuttleTimer.Stop();
            return;
        }

        _shuttlePositionMs = Math.Max(_shuttlePositionMs + _shuttle * _shuttleTimer.Interval.TotalMilliseconds, 0);
        _updatingFromPlayer = true;
        _viewModel.PositionMs = _shuttlePositionMs;
        _updatingFromPlayer = false;

        // At speed the player keeps up by landing on I-frames; slowly, it shows every step exactly.
        PlayerSeek((long)_shuttlePositionMs, exact: _shuttle >= -2);
        if (_shuttlePositionMs <= 0)
            StopShuttle();
    }

    /// <summary>Ends J/K/L shuttling, and puts the playback speed back to the one chosen in the speed box.</summary>
    private void StopShuttle()
    {
        _shuttleTimer.Stop();
        if (_shuttle == 0)
            return;

        _shuttle = 0;
        _mpv?.SetSpeed(_viewModel.PlaybackRate);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        StopShuttle();
        if (!_viewModel.HasSource)
        {
            _viewModel.LoadSourceCommand.Execute(null);
        }
        else if (_mpv is not { } player)
        {
            _ = EnsurePlayerAsync();
        }
        else if (player.IsEnded)
        {
            // Held on the last frame: play starts again from the beginning.
            player.Seek(0, exact: true);
            player.SetPause(false);
        }
        else
        {
            player.SetPause(!player.IsPaused);
        }
    }

    private void OnPlayerTimeChanged(long timeMs)
    {
        // Don't fight the user for the thumb while they are dragging it, or the shuttle while it is winding back.
        if (_isScrubbing || _shuttle < 0)
            return;

        _updatingFromPlayer = true;
        _viewModel.PositionMs = timeMs;
        _updatingFromPlayer = false;

        // Smart playback: the moment the player is outside every cut segment, it is sent on to the start of
        // the next one, so only what will be kept is played. After the last segment there is nothing to play.
        if (!PlayerIsPlaying)
            return;

        var next = _viewModel.GetSegmentSkipTarget(timeMs, out var isPastLast);
        if (next is { } target)
        {
            PlayerSeek((long)target);
        }
        else if (isPastLast)
        {
            PlayerSetPause(true);
            _viewModel.StatusText = "End of the last cut segment.";
        }
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromPlayer || _mpv is null)
            return;

        // Magnetism: while dragging, a thumb that comes close enough to an I-frame is pulled onto it.
        // The drag continues from wherever the thumb is, so it sticks until the pointer has moved clear.
        var settings = AppSettings.Current;
        if (_isScrubbing && !_applyingMagnet && settings.SnapTimelineToIFrames
            && TimelineSlider.ActualWidth > 0 && _viewModel.DurationMs > 0
            && _viewModel.GetNearestIFrameMs(e.NewValue) is { } iFrame && iFrame != e.NewValue)
        {
            const double pixelsPerStep = 4;
            var reach = Math.Clamp(settings.TimelineMagnetism, 1, 5) * pixelsPerStep / TimelineSlider.ActualWidth * _viewModel.DurationMs;
            if (Math.Abs(iFrame - e.NewValue) <= reach)
            {
                _applyingMagnet = true;
                TimelineSlider.Value = iFrame;
                _applyingMagnet = false;
                return;
            }
        }

        // While the thumb is being dragged the player jumps from I-frame to I-frame, and is asked to no more
        // than thirty times a second; the exact frame follows when the thumb is let go.
        if (_isScrubbing)
            QueueScrubSeek((long)e.NewValue);
        else
            PlayerSeek((long)e.NewValue, exact: true);
    }


    // ----- Scrubbing: fast seeks, paced -----
    // A drag of the playhead is a stream of positions, far more of them than a player with several videos to
    // decode can answer. Each is a fast seek (to the nearest I-frame, never frame-exact), and they are paced:
    // the first goes at once, and after it at most one every 33 ms, always to the newest position. Whatever
    // came in between is never sent. Letting go sends one exact seek to where the playhead was left.

    private readonly System.Windows.Threading.DispatcherTimer _scrubPace = new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
    private long _scrubWanted = -1, _scrubSent = -1;
    private int _scrubSeeks, _scrubAsked;
    private DateTime _scrubBegan;

    private void QueueScrubSeek(long positionMs)
    {
        _scrubWanted = positionMs;
        _scrubAsked++;
        if (_scrubPace.IsEnabled)
            return;

        SendScrubSeek();
        _scrubPace.Start();
    }

    private void ScrubPace_Tick(object? sender, EventArgs e)
    {
        if (!_isScrubbing || _scrubWanted == _scrubSent)
            _scrubPace.Stop();
        else
            SendScrubSeek();
    }

    private void SendScrubSeek()
    {
        _scrubSent = _scrubWanted;
        _scrubSeeks++;
        PlayerSeek(_scrubSent, exact: false);
    }

    /// <summary>The end of a scrub: the pacing stops, and what it came to is noted in the log.</summary>
    private void EndScrubPacing()
    {
        _scrubPace.Stop();
        if (_scrubAsked > 0)
            AppLog.Write($"Scrub: {_scrubAsked} positions over {(DateTime.UtcNow - _scrubBegan).TotalMilliseconds:0} ms, {_scrubSeeks} fast seeks sent, then one exact seek.");
        (_scrubAsked, _scrubSeeks, _scrubWanted, _scrubSent) = (0, 0, -1, -1);
    }

    /// <summary>
    /// A press anywhere on the master timeline, not only on the playhead: the playhead goes to where the
    /// pointer is, and the press is then handed to it, so that it is held from that moment exactly as if it
    /// had been pressed itself. Holding and dragging scrubs; letting go ends it, by the same steps as any
    /// drag of the playhead (DragStarted, DragCompleted). The slider has usually moved the playhead to the
    /// pointer already by the time this runs; what it does not do by itself is take hold of it.
    /// </summary>
    private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineSlider.Template?.FindName("PART_Track", TimelineSlider) is not System.Windows.Controls.Primitives.Track { Thumb: { } thumb } track
            || thumb.IsMouseOver || thumb.IsDragging || TimelineSlider.Maximum <= TimelineSlider.Minimum)
        {
            return;
        }

        // The track works out a value from where its playhead was last laid out. The slider may just have moved
        // the playhead (it does, for a press beside it), so the track is laid out first: asked before that, it
        // would add the distance to the pointer a second time.
        TimelineSlider.UpdateLayout();
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(value))
            return;

        TimelineSlider.Value = Math.Clamp(value, TimelineSlider.Minimum, TimelineSlider.Maximum);

        // The playhead is laid out where it now is before it is pressed: it measures the drag from there.
        TimelineSlider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent, Source = thumb });
        e.Handled = true;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isScrubbing = true;
        (_scrubBegan, _scrubAsked, _scrubSeeks) = (DateTime.UtcNow, 0, 0);

        // Dragged while it plays: the layers' sound comes out of the graph for as long as the drag lasts.
        if (LiveGraphHasSound)
            ApplyLiveFilter();
    }

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isScrubbing = false;
        EndScrubPacing();

        // Let go (and a click on the timeline is a press let go at once): the exact frame, straight away.
        PlayerSeek((long)TimelineSlider.Value);

        // And goes back in, from where the thumb was let go, if it is still playing.
        if (PlayerIsPlaying && _viewModel.LivePreview)
        {
            _liveAudioSeekMs = (long)TimelineSlider.Value;
            ScheduleLiveFilter();
        }
    }

    // Selecting a segment already seeks; this covers clicking the one that is selected.
    private void SegmentItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var clickedButton = e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null;
        if (!clickedButton && sender is ListBoxItem { DataContext: CutSegment segment })
            _viewModel.SeekToSegment(segment);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    // ----- Segment strip above the timeline -----

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.PendingStartMs) or nameof(MainViewModel.PlayOnlySegments))
            RedrawSegments();

        if (e.PropertyName == nameof(MainViewModel.SoloTrack))
            OnSoloTrackChanged();

        if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.IsEditorMode) or nameof(MainViewModel.ShowLinkedAudio)
            or nameof(MainViewModel.AudioLinked) or nameof(MainViewModel.TimelineWaveform))
        {
            ScheduleClipRedraw();
        }

        if (e.PropertyName == nameof(MainViewModel.PositionMs))
        {
            MoveClipPlayheads();
            if (_viewModel.DurationMs > 0 && !double.IsNaN(KeyTimeline.PlayheadX))
                KeyTimeline.PlayheadX = _viewModel.PositionMs / _viewModel.DurationMs * KeyTimeline.ActualWidth;
        }

        if (e.PropertyName is nameof(MainViewModel.ShowCutSegmentsPane) or nameof(MainViewModel.ShowKeyframesPane))
            UpdateSidePanes();

        // Each mode keeps its own arrangement of the panes, and its own bottom bar.
        if (e.PropertyName == nameof(MainViewModel.IsEditorMode))
        {
            ApplyPaneLayout();
            UpdateBottomBar();
        }

        if (e.PropertyName is nameof(MainViewModel.ActiveKeyLayer) or nameof(MainViewModel.DurationMs))
            DrawKeyTimeline();

        if (e.PropertyName == nameof(MainViewModel.HasSource))
            DropHint.Visibility = _viewModel.HasSource ? Visibility.Collapsed : Visibility.Visible;

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

        // Do not leave a tab showing that has just been hidden.
        if (e.PropertyName == nameof(MainViewModel.ShowCommandPreviewTab)
            && !_viewModel.ShowCommandPreviewTab && ReferenceEquals(SettingsTabs.SelectedItem, CommandPreviewTab))
        {
            SettingsTabs.SelectedIndex = 0;
        }

        if (e.PropertyName == nameof(MainViewModel.IsEditorMode))
            UpdateBottomBar();

        // Layers is Editor Mode's tab and Chapters is Encoder Mode's.
        if (e.PropertyName == nameof(MainViewModel.IsEditorMode)
            && ReferenceEquals(SettingsTabs.SelectedItem, _viewModel.IsEditorMode ? ChaptersTab : LayersTab))
        {
            SettingsTabs.SelectedIndex = 0;
        }
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

        // The blackouts: everything outside the cut segments, shaded while playback is set to skip it.
        if (_viewModel.PlayOnlySegments && _viewModel.Segments.Count > 0)
        {
            var reached = 0.0;
            foreach (var segment in _viewModel.Segments.OrderBy(s => s.Start))
            {
                AddShade(reached, segment.Start.TotalMilliseconds, BlackoutBrush);
                reached = Math.Max(reached, segment.End.TotalMilliseconds);
            }

            AddShade(reached, duration, BlackoutBrush);
        }

        if (_rangeDragStartMs is { } dragStart)
            AddShade(Math.Min(dragStart, _rangeDragEndMs), Math.Max(dragStart, _rangeDragEndMs), RangeDragBrush);

        foreach (var segment in _viewModel.Segments)
        {
            AddStripMark(
                segment.Start.TotalMilliseconds / duration * width,
                Math.Max(2, segment.Duration.TotalMilliseconds / duration * width),
                (Brush)FindResource("AccentBrush"));
        }

        if (_viewModel.PendingStartMs is { } pendingStart)
            AddStripMark(pendingStart / duration * width, 2, Brushes.OrangeRed);
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
            _viewModel.StatusText = "Drag along the timeline with the right mouse button to mark a cut segment.";
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
        if (dialog.ShowDialog() == true && dialog.Game is { } game)
            _viewModel.AddHudLayers(game, dialog.Vertical);
    }

    // ----- Blocks in time -----
    // Every row of the Layers tab, and every track of the Audio tab, has a time bar as long as the video, with
    // what it stands for as blocks: a track's clips, each from when it appears to when it goes; the main
    // video's cut segments; a video layer's own sound; an audio track, whole or in the parts it was split into;
    // a sound added from a file. Everything is placed by one sum: seconds / length * width. While a block is
    // being dragged only that block is moved; what it stands for is changed when it is let go, which is also
    // when everything else (the command, Live Preview, Undo) hears of it.
    //
    // A bar is drawn once and left alone: as the video plays only its playhead line moves. The pictures in a
    // block (frames of a video layer, a waveform) are brushes and crops of pictures already in memory.

    private static readonly Brush MainClipBrush = Frozen(Color.FromArgb(0xB0, 0x2F, 0x6F, 0xB5));
    private static readonly Brush LayerClipBrush = Frozen(Color.FromArgb(0xD0, 0x8E, 0x5A, 0xB8));
    private static readonly Brush AudioClipBrush = Frozen(Color.FromArgb(0x38, 0x4C, 0xAF, 0x50));
    private static readonly Brush LayerAudioBrush = Frozen(Color.FromArgb(0xD0, 0x3C, 0x9A, 0x5F));
    private static readonly Brush SoundClipBrush = Frozen(Color.FromArgb(0xD0, 0x2B, 0x7A, 0x6B));
    private static readonly Brush OffClipBrush = Frozen(Color.FromArgb(0xC0, 0x55, 0x55, 0x55));
    private static readonly Brush SubtitleClipBrush = Frozen(Color.FromArgb(0xD0, 0x2E, 0x7D, 0x6B));
    private static readonly Brush MainUnderBrush = Frozen(Color.FromArgb(0x48, 0x2F, 0x6F, 0xB5));
    private static readonly Brush SegmentMarkBrush = Frozen(Color.FromArgb(0xD0, 0x1F, 0x4E, 0x82));

    /// <summary>One clip of the main video, as something that can be selected.</summary>
    private sealed record MainClipRef(int Index);

    /// <summary>Where a clip's keyframes fall along its block, in pixels; nothing while their diamonds are switched off.</summary>
    /// <param name="shift">How far the moment the keyframes are counted from lies from the start of the block, in seconds.</param>
    private IEnumerable<double>? ClipKeys(Layer clip, double shift, double scale) =>
        _viewModel.ShowClipKeyframes && clip.IsAnimated ? clip.KeyTimes.Select(t => (t + shift) * scale) : null;

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

    private void KeyTimeline_SizeChanged(object sender, SizeChangedEventArgs e) => DrawKeyTimeline();

    /// <summary>
    /// Draws the Keyframes pane's timeline: the same span as the master timeline, the selected clip's stretch
    /// of it shaded, a diamond at each of its keyframes, and the playhead. The bar draws itself from these
    /// figures; as the video plays only its playhead is moved.
    /// </summary>
    private void DrawKeyTimeline()
    {
        var (seconds, width) = (_viewModel.DurationMs / 1000, KeyTimeline.ActualWidth);
        if (!KeyframesPane.IsVisible || seconds <= 0 || width <= 0)
        {
            KeyTimeline.Show(0, 0, null, []);
            KeyTimeline.PlayheadX = double.NaN;
            return;
        }

        var clip = _viewModel.ActiveKeyLayer;
        var scale = width / seconds;
        var (from, to) = clip.IsMainVideo ? _viewModel.GetMainSpan() : clip.GetSpan(seconds);
        KeyTimeline.Show(from * scale, (to - from) * scale, clip.IsMainVideo ? MainClipBrush : LayerClipBrush, clip.KeyTimes.Select(time => (clip.StartTime + time) * scale).ToList());
        KeyTimeline.PlayheadX = _viewModel.PositionMs / 1000 * scale;
    }

    private void KeyTimeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.DurationMs > 0 && KeyTimeline.CaptureMouse())
            SeekKeyTimeline(e);
    }

    private void KeyTimeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (KeyTimeline.IsMouseCaptured)
            SeekKeyTimeline(e);
    }

    private void KeyTimeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => KeyTimeline.ReleaseMouseCapture();

    private void SeekKeyTimeline(MouseEventArgs e)
    {
        if (KeyTimeline.ActualWidth <= 0)
            return;

        // A click on a diamond goes to that keyframe exactly; anywhere else, to where the pointer is.
        var x = e.GetPosition(KeyTimeline).X;
        if (e is MouseButtonEventArgs && KeyTimeline.KeyNear(x) is { } key)
            x = key;
        _viewModel.PositionMs = Math.Clamp(x / KeyTimeline.ActualWidth, 0, 1) * _viewModel.DurationMs;
    }

    // ----- The panes: where each is, and how large -----
    // The player area is two grids. The outer one has the player in one column and the side panes in another,
    // over the master timeline; the inner one has the video in one column and the layout pane in another.
    // Moving a pane is giving it the other row or column of its grid (and that row or column its size);
    // nothing is taken out of the window or put back. The arrangement belongs to the mode in use.

    private const double DefaultSidePanesWidth = 400;
    private const double DefaultLayoutPaneWidth = 340;

    private static readonly GridLength Star = new(1, GridUnitType.Star);

    // The outer grid's columns: whichever of the first and the last the side panes are in, and the player's.
    private ColumnDefinition SideColumn => PlayerArea.ColumnDefinitions[AppSettings.Current.SidePanesOnLeft ? 0 : 2];
    private ColumnDefinition PlayerColumn => PlayerArea.ColumnDefinitions[AppSettings.Current.SidePanesOnLeft ? 2 : 0];

    // The inner grid's: the layout pane's, and the video's.
    private ColumnDefinition LayoutColumn => PlayerCell.ColumnDefinitions[AppSettings.Current.LayoutPaneOnLeft ? 0 : 2];
    private ColumnDefinition VideoColumn => PlayerCell.ColumnDefinitions[AppSettings.Current.LayoutPaneOnLeft ? 2 : 0];

    /// <summary>Puts every pane in the row and column the settings have it in, at the size they have it at.</summary>
    private void ApplyPaneLayout()
    {
        var settings = AppSettings.Current;

        // The player and the side panes, side by side either way round.
        var (sideAt, playerAt) = settings.SidePanesOnLeft ? (0, 2) : (2, 0);
        Grid.SetColumn(SidePanes, sideAt);
        Grid.SetColumn(PlayerCell, playerAt);
        (PlayerColumn.Width, PlayerColumn.MinWidth) = (Star, 520);

        // The master timeline under them, or over them.
        ApplyTimelineRows();
        var playerRow = settings.TimelineOnTop ? 2 : 0;
        Grid.SetRow(PlayerCell, playerRow);
        Grid.SetRow(SideSplitter, playerRow);
        Grid.SetRow(SidePanes, playerRow);
        Grid.SetRow(TimelinePane, settings.TimelineOnTop ? 0 : 2);

        // The video and the layout pane, side by side either way round; the transport row stays under the video.
        var (layoutAt, videoAt) = settings.LayoutPaneOnLeft ? (0, 2) : (2, 0);
        Grid.SetColumn(LayoutPane, layoutAt);
        Grid.SetColumn(VideoBorder, videoAt);
        Grid.SetColumn(TransportRow, videoAt);
        (VideoColumn.Width, VideoColumn.MinWidth) = (Star, 520);
        LayoutColumn.MinWidth = 0;

        UpdateSidePanes();
        UpdateLayoutPane(announce: false);
        UpdatePaneButtons();
        PlaceViewsPopup();
    }

    /// <summary>
    /// Puts the Views menu beside the button that opens it in the mode in use: over the transport row's in
    /// Encoder Mode, under the one in the side panes' header in Editor Mode.
    /// </summary>
    private void PlaceViewsPopup()
    {
        var (button, placement, offset) = _viewModel.IsEditorMode ? (EditorViewsButton, PlacementMode.Top, -4) : (ViewsButton, PlacementMode.Top, -4);
        if (ReferenceEquals(ViewsPopup.PlacementTarget, button))
            return;

        // Open beside the other mode's button, it closes first.
        ViewsButton.IsChecked = false;
        (ViewsPopup.PlacementTarget, ViewsPopup.Placement, ViewsPopup.VerticalOffset) = (button, placement, offset);
    }

    /// <summary>The rows of the player area: the player takes what height there is, the timeline what it needs.</summary>
    private void ApplyTimelineRows()
    {
        var top = AppSettings.Current.TimelineOnTop;
        var rows = PlayerArea.RowDefinitions;
        (rows[0].Height, rows[1].Height, rows[2].Height) = top ? (GridLength.Auto, GridLength.Auto, Star) : (Star, GridLength.Auto, GridLength.Auto);
    }

    /// <summary>
    /// Opens and closes the column of side panes with the panes in it: with neither the Keyframes pane nor the
    /// Cut Segments pane showing, the column and its splitter go, and the player has the width. Not in Editor
    /// Mode, where the column starts with the header and is always there.
    /// </summary>
    private void UpdateSidePanes()
    {
        var settings = AppSettings.Current;
        var (keys, cuts) = (_viewModel.ShowKeyframesPane, _viewModel.ShowCutSegmentsPane);
        var any = keys || cuts || _viewModel.IsEditorMode;

        SidePanes.Visibility = SideSplitter.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        var width = settings.SidePanesWidth > 0 ? settings.SidePanesWidth : DefaultSidePanesWidth;
        SideColumn.Width = any ? new GridLength(width) : new GridLength(0);
        UpdateSideColumnMinWidth();

        // The two panes over each other, either way round, with a border between them to drag. The Cut
        // Segments pane takes the height that is left; the Keyframes pane is as tall as what is in it until
        // it is dragged to a height of its own. A pane that is alone has the first row.
        var both = keys && cuts;
        var keysRow = both && settings.CutSegmentsOnTop ? 2 : 0;
        var cutsRow = both && !settings.CutSegmentsOnTop ? 2 : 0;
        Grid.SetRow(KeyframesPane, keysRow);
        Grid.SetRow(CutSegmentsPane, cutsRow);
        SidePaneSplitter.Visibility = both ? Visibility.Visible : Visibility.Collapsed;

        var rows = SidePaneStack.RowDefinitions;
        var keysHeight = both && settings.KeyframesPaneHeight > 0 ? new GridLength(settings.KeyframesPaneHeight) : GridLength.Auto;
        foreach (var row in new[] { 0, 2 })
        {
            var holdsKeys = keys && row == keysRow;
            var holdsCuts = cuts && row == cutsRow;
            (rows[row].Height, rows[row].MinHeight) = holdsCuts ? (Star, both ? 70 : 0)
                : holdsKeys ? (keysHeight, both ? 110 : 0)
                : (keys && !cuts ? Star : GridLength.Auto, 0);
        }

        LimitKeyframesPane();
        Dispatcher.BeginInvoke(DrawKeyTimeline, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // The header's buttons have changed width (the queue's count grew a digit), or have just come or gone with the mode.
    private void EditorHeader_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSideColumnMinWidth();

    /// <summary>
    /// How narrow the side column can be dragged. In Editor Mode it is never narrower than the header's row
    /// of buttons, so that all of them stay on the one row and none is cut off; the border beside the player
    /// stops there. The width is asked of the row itself: what the buttons need with nothing holding them in.
    /// </summary>
    private void UpdateSideColumnMinWidth()
    {
        const double least = 170;
        if (SidePanes.Visibility != Visibility.Visible)
        {
            SideColumn.MinWidth = 0;
            return;
        }

        var header = 0.0;
        if (_viewModel.IsEditorMode && EditorHeader.Visibility == Visibility.Visible)
        {
            EditorHeader.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            header = Math.Ceiling(EditorHeader.DesiredSize.Width);
        }

        SideColumn.MinWidth = Math.Max(least, header);
    }

    private void SidePaneStack_SizeChanged(object sender, SizeChangedEventArgs e) => LimitKeyframesPane();

    /// <summary>
    /// Keeps the Keyframes pane inside the column while it shares it with the Cut Segments pane. It is as tall
    /// as what is in it, and a row of that kind is given its height whether there is room or not: in a short
    /// column its lower controls would be cut off. Held to the room there is, it scrolls instead.
    /// </summary>
    private void LimitKeyframesPane()
    {
        const double cutSegmentsLeast = 70, splitter = 10;
        var both = _viewModel.ShowKeyframesPane && _viewModel.ShowCutSegmentsPane;
        KeyframesPane.MaxHeight = both && SidePaneStack.ActualHeight > 0
            ? Math.Max(SidePaneStack.ActualHeight - cutSegmentsLeast - splitter, 110)
            : double.PositiveInfinity;
    }

    /// <summary>What the Arrange buttons of the Views menu say: where each pane would go.</summary>
    private void UpdatePaneButtons()
    {
        var settings = AppSettings.Current;
        MoveLayoutPaneButton.Content = settings.LayoutPaneOnLeft ? "Move Layout Pane to the Right of the Video" : "Move Layout Pane to the Left of the Video";
        MoveSidePanesButton.Content = settings.SidePanesOnLeft ? "Move Side Panes to the Right" : "Move Side Panes to the Left";
        SwapSidePanesButton.Content = settings.CutSegmentsOnTop ? "Put Keyframes above Cut Segments" : "Put Cut Segments above Keyframes";
        MoveTimelineButton.Content = settings.TimelineOnTop ? "Move Master Timeline under the Player" : "Move Master Timeline above the Player";
    }

    /// <summary>Moves a pane to the other side, remembers it for this mode, and lays the panes out again.</summary>
    /// <param name="pane">Layout, Side, Order (the two side panes over each other) or Timeline.</param>
    /// <param name="to">Where to put it: true for left or top. Null for the other side from where it is.</param>
    private void MovePane(string pane, bool? to = null)
    {
        var settings = AppSettings.Current;
        var was = pane switch
        {
            "Layout" => settings.LayoutPaneOnLeft,
            "Side" => settings.SidePanesOnLeft,
            "Order" => settings.CutSegmentsOnTop,
            _ => settings.TimelineOnTop,
        };
        var now = to ?? !was;
        if (now == was)
            return;

        // The column a pane leaves is the video's or the player's from now on, and its width goes with the pane.
        RememberPaneSizes();
        _viewModel.SavePaneLayout(s =>
        {
            switch (pane)
            {
                case "Layout": s.LayoutPaneOnLeft = now; break;
                case "Side": s.SidePanesOnLeft = now; break;
                case "Order": s.CutSegmentsOnTop = now; break;
                default: s.TimelineOnTop = now; break;
            }
        });

        ApplyPaneLayout();
        _viewModel.Log(pane switch
        {
            "Layout" => $"Moved the Layout pane to the {(now ? "left" : "right")} of the video",
            "Side" => $"Moved the side panes to the {(now ? "left" : "right")} of the player",
            "Order" => now ? "Moved Cut Segments above Keyframes" : "Moved Keyframes above Cut Segments",
            _ => $"Moved the master timeline {(now ? "above" : "under")} the player",
        });
    }

    private void MovePane_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string pane })
            MovePane(pane);
    }

    private void ResetPanesLayout_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetPaneLayout();

        // The border between the player and the settings tabs goes back as well.
        if (PlayerArea.Parent is Grid { RowDefinitions.Count: > 3 } root)
            (root.RowDefinitions[1].Height, root.RowDefinitions[3].Height) = (new GridLength(46, GridUnitType.Star), new GridLength(38, GridUnitType.Star));

        ApplyPaneLayout();
        ViewsButton.IsChecked = false;
    }

    /// <summary>Takes the sizes the panes have on screen into the settings: they were dragged to them.</summary>
    private void RememberPaneSizes()
    {
        var (side, layout) = (SidePanes.Visibility == Visibility.Visible ? SideColumn.ActualWidth : 0, LayoutPane.Visibility == Visibility.Visible ? LayoutColumn.ActualWidth : 0);
        var keys = SidePaneSplitter.Visibility == Visibility.Visible && SidePaneStack.RowDefinitions[Grid.GetRow(KeyframesPane)].Height.IsAbsolute
            ? SidePaneStack.RowDefinitions[Grid.GetRow(KeyframesPane)].ActualHeight
            : 0;
        _viewModel.SavePaneLayout(s =>
        {
            if (side > 0)
                s.SidePanesWidth = Math.Round(side);
            if (layout > 0)
                s.LayoutPaneWidth = Math.Round(layout);
            if (keys > 0)
                s.KeyframesPaneHeight = Math.Round(keys);
        });
    }

    // A border between two panes was dragged: the sizes it left them at are this mode's from now on.
    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        RememberPaneSizes();
        RedrawLayout();
        DrawKeyTimeline();
    }

    // The border between the player and the master timeline. What it drags is the timeline's height setting,
    // the one the slider beside the volume sets: the timeline is as tall as that says, not as tall as a grid
    // row happens to be, so the row is put back to "as tall as its content" each time the splitter has sized it.

    private double _timelineDragHeight;
    private double _timelineDragStart;

    private void TimelineSplitter_DragStarted(object sender, DragStartedEventArgs e) =>
        (_timelineDragHeight, _timelineDragStart) = (_viewModel.MasterTimelineHeight, Mouse.GetPosition(this).Y);

    private void TimelineSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Towards the player makes the timeline taller: up when it is under the player, down when it is over it.
        var moved = Mouse.GetPosition(this).Y - _timelineDragStart;
        var steps = (AppSettings.Current.TimelineOnTop ? moved : -moved) / 4;
        _viewModel.MasterTimelineHeight = Math.Clamp(_timelineDragHeight + steps, 1, 50);
        ApplyTimelineRows();
    }

    private void TimelineSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ApplyTimelineRows();
        _viewModel.Log(string.Create(CultureInfo.InvariantCulture, $"Timeline height {_viewModel.MasterTimelineHeight:0} of 50"));
    }

    // With the keyboard: the arrow keys change the same setting, a step at a time.
    private void TimelineSplitter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down))
            return;

        var towardsPlayer = (e.Key == Key.Up) != AppSettings.Current.TimelineOnTop;
        _viewModel.MasterTimelineHeight = Math.Clamp(_viewModel.MasterTimelineHeight + (towardsPlayer ? 1 : -1), 1, 50);
        e.Handled = true;
    }

    // Dragging a pane by its title: let go on the other side of what it is beside, and it moves there.

    private Point? _paneDragFrom;

    private void PaneTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement title || e.ClickCount > 1)
            return;

        _paneDragFrom = e.GetPosition(this);
        title.CaptureMouse();
        e.Handled = true;
    }

    private void PaneTitle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_paneDragFrom is { } from && sender is FrameworkElement { IsMouseCaptured: true, Tag: string pane } && (e.GetPosition(this) - from).Length > 8)
        {
            _viewModel.StatusText = pane == "Layout"
                ? "Let go on the other side of the video to move the Layout pane there."
                : "Let go above or below the other side pane to swap them, or on the other side of the player to move both there.";
        }
    }

    private void PaneTitle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true, Tag: string pane } title || _paneDragFrom is not { } from)
            return;

        title.ReleaseMouseCapture();
        _paneDragFrom = null;
        if ((e.GetPosition(this) - from).Length <= 8)
            return;

        if (pane == "Layout")
        {
            // Whichever half of the video-and-layout cell it was let go over.
            MovePane("Layout", to: e.GetPosition(PlayerCell).X < PlayerCell.ActualWidth / 2);
            return;
        }

        var inSide = e.GetPosition(SidePanes);
        if (inSide.X >= 0 && inSide.X <= SidePanes.ActualWidth)
        {
            // Inside the side column: above or below the middle of its panes is where this pane goes.
            var above = e.GetPosition(SidePaneStack).Y < SidePaneStack.ActualHeight / 2;
            MovePane("Order", to: (pane == "CutSegments") == above);
        }
        else
        {
            // Outside it: the half of the player area it was let go over is the side the panes go to.
            MovePane("Side", to: e.GetPosition(PlayerArea).X < PlayerArea.ActualWidth / 2);
        }
    }

    // ----- The Audio tab's checkboxes, folded into a button when the row is too narrow -----

    private double _audioOptionsWidth;

    // ----- Tabs that are built when they are first wanted -----

    // The named parts of the Audio and Layers tabs that this file uses: the tabs are made from templates, so
    // these are found in them when they are made. The two that are asked about from outside their tab (is a
    // file being dropped on it, what is selected in it) may not be there yet; the rest are only reached from
    // their own tab's events.
    private DockPanel? AudioPanel;
    private Grid AudioOptionsHost = null!;
    private StackPanel AudioOptionsInline = null!;
    private System.Windows.Controls.Primitives.ToggleButton AudioOptionsButton = null!;
    private ListBox? LayersList;
    private WrapPanel LayerAddBar = null!;
    private StackPanel KeyBarInline = null!;
    private System.Windows.Controls.Primitives.ToggleButton AddLayerButton = null!, KeyBarButton = null!;

    /// <summary>
    /// Makes what is in a tab whose content was left as a template, which is every tab but Summary: once, and
    /// it then stays as any tab's content does. Only the Summary tab is made with the window.
    /// </summary>
    private void RealizeTab(object? item)
    {
        if (item is not TabItem { Tag: DataTemplate template } tab)
            return;

        tab.Tag = null;
        var content = template.LoadContent();
        if (content is FrameworkElement root && root.FindName(nameof(AudioOptionsHost)) is Grid host)
        {
            AudioOptionsHost = host;
            AudioOptionsInline = (StackPanel)root.FindName(nameof(AudioOptionsInline));
            AudioOptionsButton = (System.Windows.Controls.Primitives.ToggleButton)root.FindName(nameof(AudioOptionsButton));
            AudioPanel = (DockPanel)root.FindName(nameof(AudioPanel));
        }
        else if (content is FrameworkElement layers && layers.FindName(nameof(LayerAddBar)) is WrapPanel bar)
        {
            LayerAddBar = bar;
            KeyBarInline = (StackPanel)layers.FindName(nameof(KeyBarInline));
            KeyBarButton = (System.Windows.Controls.Primitives.ToggleButton)layers.FindName(nameof(KeyBarButton));
            AddLayerButton = (System.Windows.Controls.Primitives.ToggleButton)layers.FindName(nameof(AddLayerButton));
            LayersList = (ListBox)layers.FindName(nameof(LayersList));
        }

        tab.Content = content;
    }

    // Raised by every list and box inside the tabs as well: only the tabs' own selection is of interest.
    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsTabs))
            RealizeTab(SettingsTabs.SelectedItem);
    }

    /// <summary>
    /// Makes the tabs nobody has opened yet, one at a time and only while nothing else is wanted of this
    /// thread, so that the first click on any of them finds it ready.
    /// </summary>
    private void RealizeTabsWhenIdle()
    {
        var next = SettingsTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Tag is DataTemplate);
        if (next is null)
            return;

        RealizeTab(next);
        Dispatcher.BeginInvoke(RealizeTabsWhenIdle, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

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
    private void ViewsPopup_Opened(object? sender, EventArgs e) => ViewsButton.IsHitTestVisible = EditorViewsButton.IsHitTestVisible = false;

    private void ViewsPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ViewsButton.IsHitTestVisible = EditorViewsButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);

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

    // Wide enough for the keyframe buttons to sit in the bar beside everything else in it.
    private const double KeyBarInlineWidth = 1500;

    /// <summary>Shows the keyframe controls in the Layers tab's bar while it is wide enough for them, and behind one button while it is not.</summary>
    private void LayerAddBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var inline = LayerAddBar.ActualWidth >= KeyBarInlineWidth;
        if (inline == (KeyBarInline.Visibility == Visibility.Visible))
            return;

        (KeyBarInline.Visibility, KeyBarButton.Visibility) = inline ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Collapsed, Visibility.Visible);
        if (inline)
            KeyBarButton.IsChecked = false;
    }

    /// <summary>An audio track, or one of the parts it was split into, as something that can be selected.</summary>
    private sealed record AudioClip(AudioTrack Track, AudioPiece? Piece);

    /// <summary>A video layer's own sound, as something that can be selected.</summary>
    private sealed record LayerSound(Layer Layer);

    // The bars that are on screen now (the lists make and drop rows as they scroll).
    private readonly List<TimelineTrack> _clipTracks = [];

    // What is selected: a Layer (one clip), a MainClipRef (a clip of the main video), a CutSegment, an AudioClip, a LayerSound.
    private object? _selectedClip;
    private bool _mainClipSelected;
    private bool _clipRedrawQueued;

    private void ClipTrack_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TimelineTrack track && !_clipTracks.Contains(track))
        {
            _clipTracks.Add(track);
            track.IsSelected = IsSelected;
            track.BlockPressed += ClipTrack_BlockPressed;
            track.DragRefused += ClipTrack_DragRefused;
            track.DragEnded += ScheduleClipRedraw;
            track.ZoomRequested += ClipTrack_ZoomRequested;
            track.PanRequested += ClipTrack_PanRequested;
        }

        DrawTrack(sender as TimelineTrack);
    }

    private void ClipTrack_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TimelineTrack track && _clipTracks.Remove(track))
        {
            track.BlockPressed -= ClipTrack_BlockPressed;
            track.DragRefused -= ClipTrack_DragRefused;
            track.DragEnded -= ScheduleClipRedraw;
            track.ZoomRequested -= ClipTrack_ZoomRequested;
            track.PanRequested -= ClipTrack_PanRequested;
        }
    }

    private void ClipTrack_Changed(object sender, SizeChangedEventArgs e) => DrawTrack(sender as TimelineTrack);

    private void ClipTrack_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => DrawTrack(sender as TimelineTrack);

    // A block was pressed, with either button: it is what is selected now (a right-click selects it too, so that the menu that opens is about it).
    private void ClipTrack_BlockPressed(TimelineBlock block, MouseButtonEventArgs e)
    {
        // A subtitle on the caption layer's bar is not a clip: it is edited in the Subtitle Editor.
        if (block.Clip is SubtitleRef)
            return;

        SelectClip(block.Clip);

        // On an audio track a click is also what moves the playhead, as it was before the track had blocks.
        if (block.Clip is AudioClip && e.ChangedButton == MouseButton.Left && e.Source is TimelineTrack { ActualWidth: > 0 } track)
            _viewModel.PositionMs = Math.Clamp((e.GetPosition(track).X + track.ViewOffsetX) / (track.ActualWidth * _timelineZoom), 0, 1) * _viewModel.DurationMs;
    }

    private void ClipTrack_DragRefused(string why) => _viewModel.StatusText = why;

    /// <summary>A subtitle drawn on the caption layer's bar.</summary>
    private sealed record SubtitleRef(double Start);

    // A click on a bar, beside its blocks, moves the playhead to that moment.
    private void ClipTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TimelineTrack { ActualWidth: > 0 } track && _viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp((e.GetPosition(track).X + track.ViewOffsetX) / (track.ActualWidth * _timelineZoom), 0, 1) * _viewModel.DurationMs;
    }

    /// <summary>Redraws the bars once, however many things have just changed.</summary>
    private void ScheduleClipRedraw()
    {
        if (_clipRedrawQueued || _clipTracks.Exists(t => t.IsDragging))
            return;

        _clipRedrawQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _clipRedrawQueued = false;
            foreach (var track in _clipTracks.ToList())
                DrawTrack(track);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Draws one bar: its block or blocks, and the playhead.</summary>
    private void DrawTrack(TimelineTrack? track)
    {
        if (track is null || track.IsDragging)
            return;

        track.Blocks.Clear();
        track.PlayheadX = double.NaN;
        track.InvalidateVisual();
        var seconds = _viewModel.DurationMs / 1000;
        // A video layer's sound is drawn on the bar under its picture on the Layers tab ("audio"), and on
        // the layer's row of the Audio tab ("row"), where it is listed with the other sounds.
        var onAudioTab = track.Tag as string == "row";
        var isSound = track.Tag as string == "audio" || (onAudioTab && track.DataContext is Layer { IsVideoFile: true });
        var show = seconds > 0 && track.DataContext switch
        {
            AudioTrack => true,

            // A video layer's sound has a bar of its own only while it is unlinked from its picture; linked, it is drawn on the picture's block.
            // On the Audio tab its row always shows it.
            Layer layerOf when isSound => (onAudioTab || !_viewModel.AudioLinked) && layerOf is { IsVideoFile: true, HasAudio: true },
            Layer { IsCaptions: true } => _viewModel.GetSubtitleSegments().Count > 0,
            Layer { IsMainVideo: true } or Layer { HasTiming: true } => true,
            _ => false,
        };
        track.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || track.ActualWidth <= 0)
            return;

        // The bar is laid out as wide as the zoom makes it, and the part of it that is seen is slid into view.
        var width = track.ActualWidth * _timelineZoom;
        var scale = width / seconds;
        track.ViewOffsetX = _timelineOffsetSeconds * scale;
        var perPixel = seconds / width;
        var height = Math.Max(track.ActualHeight - 2, 4);
        switch (track.DataContext)
        {
            // An audio track: one block, or its parts, moved along with the track when it has been slipped.
            case AudioTrack audio:
                // The track is the main video's: it lies along the timeline where the main video's file does.
                var (fileAt, fileLength) = (_viewModel.MainLayer.StartTime - _viewModel.MainLayer.MediaOffset, _viewModel.MainLayer.MediaDuration > 0 ? _viewModel.MainLayer.MediaDuration : seconds);
                var pieces = audio.Pieces.Count > 0 ? audio.Pieces : [new AudioPiece(0, fileLength, false)];
                foreach (var piece in pieces)
                {
                    var clip = new AudioClip(audio, audio.Pieces.Count > 0 ? piece : (AudioPiece?)null);
                    AddClip(track, (piece.Start + audio.OffsetSeconds + fileAt) * scale, (piece.End - piece.Start) * scale, piece.Muted ? "silenced" : "",
                        piece.Muted ? OffClipBrush : AudioClipBrush, clip, canDrag: () => !_viewModel.AudioLinked, resizable: false,
                        commit: (left, _) => _viewModel.SlipAudio(audio, left * perPixel - piece.Start - fileAt),
                        cannotDrag: "Audio and video are linked. Unlink them (the chain button) to move this track on its own.");
                }

                // The picture of the sound is of the whole file, and lies where the file does.
                if (track.Parent is Grid { Children: [Image waveform, ..] })
                {
                    var waveLeft = (audio.OffsetSeconds + fileAt) * scale;
                    waveform.Margin = new Thickness(waveLeft - track.ViewOffsetX, 0, track.ActualWidth - (waveLeft - track.ViewOffsetX) - fileLength * scale, 0);
                }

                break;

            // A video layer's own sound, unlinked: each clip's starts with the clip, or wherever it was slipped to.
            case Layer sound when isSound:
                foreach (var clip in _viewModel.GetTrackClips(sound))
                {
                    var (from, to) = clip.GetSpan(seconds);
                    AddClip(track, (from + clip.AudioOffset) * scale, (to - from) * scale, "sound", clip.IsHidden ? OffClipBrush : LayerAudioBrush, new LayerSound(clip),
                        canDrag: () => !_viewModel.AudioLinked, resizable: false, peaks: MediaPeaks(clip.Waveform, clip, scale),
                        commit: (left, _) =>
                        {
                            _viewModel.Checkpoint($"move the sound of {clip.Name}");
                            clip.AudioOffset = Math.Round(left * perPixel - clip.StartTime, 2);
                        },
                        cannotDrag: "Audio and video are linked: the sound follows its layer. Unlink them (the chain button) to move it on its own.");
                }

                break;

            // The main video: clips like any other. One to begin with; a split makes two, and each is dragged
            // along the timeline, trimmed by its right edge and deleted on its own. Cut segments, where there
            // are any, are marked over them.
            case Layer { IsMainVideo: true } main:
                var mainWave = _viewModel.ShowLinkedAudio && !main.IsHidden ? _viewModel.TimelineWaveform : null;
                var mainClips = _viewModel.GetMainClips();
                foreach (var piece in mainClips)
                {
                    var (pieceLeft, pieceWidth) = (piece.Start * scale, (piece.End - piece.Start) * scale);

                    // The waveform is of the whole file: a block shows the part of it that its clip plays.
                    var filePixels = main.MediaDuration > 0 ? main.MediaDuration * scale : width;
                    AddClip(track, pieceLeft, pieceWidth, mainClips.Count > 1 ? "" : main.IsHidden ? $"{main.Name} (picture hidden)" : main.Name,
                        main.IsHidden ? OffClipBrush : MainClipBrush, new MainClipRef(piece.Index), canDrag: () => true, resizable: true,
                        frames: main.IsHidden ? null : BuildFrames(main.Filmstrip, _viewModel.SourceWidth, _viewModel.SourceHeight, piece.Offset, main.MediaDuration, 1, pieceWidth, height, scale),
                        peaks: WaveOf(mainWave, piece.Offset * scale, filePixels),
                        keys: ClipKeys(main, main.StartTime - piece.Start, scale),
                        commit: (left, nowWidth) =>
                        {
                            if (Math.Abs(nowWidth - pieceWidth) > 0.5)
                                _viewModel.SetMainLength(piece.Index, nowWidth * perPixel);
                            else
                                _viewModel.MoveMain(piece.Index, (left - pieceLeft) * perPixel);
                        });
                }

                foreach (var segment in _viewModel.Segments)
                {
                    AddClip(track, segment.Start.TotalSeconds * scale, segment.Duration.TotalSeconds * scale, segment.IsSkipped ? "skipped" : "cut",
                        segment.IsSkipped ? OffClipBrush : SegmentMarkBrush, segment, top: height * 0.6);
                }

                break;

            // The caption layer: the subtitles, each from when it is shown to when it goes. Not dragged here:
            // they are retimed and reworded in the Subtitle Editor (right-click, Manual Edit).
            case Layer { IsCaptions: true }:
                foreach (var (from, to, text, _) in _viewModel.GetSubtitleSegments())
                {
                    // Off the bar altogether: not worth a block.
                    if (to * scale < 0 || from * scale > width)
                        continue;
                    AddClip(track, from * scale, (to - from) * scale, text, SubtitleClipBrush, new SubtitleRef(from));
                }

                break;

            // A track: its clips, each from when it appears to when it goes.
            case Layer layer:
                foreach (var clip in _viewModel.GetTrackClips(layer))
                {
                    var (from, to) = clip.GetSpan(seconds);
                    var blockWidth = (to - from) * scale;
                    var showsSound = clip.IsAudio || (clip is { IsVideoFile: true, HasAudio: true } && _viewModel.ShowLinkedAudio && _viewModel.AudioLinked);
                    AddClip(track, from * scale, blockWidth, clip.IsLoading ? "Loading..." : clip.IsHidden ? "hidden" : clip.IsAudio ? clip.Name : "",
                        clip.IsLoading || clip.IsHidden ? OffClipBrush : clip.IsAudio ? SoundClipBrush : LayerClipBrush, clip, canDrag: () => true, resizable: true,
                        frames: clip.IsHidden || !clip.IsVideoFile ? null
                            : BuildFrames(clip.Filmstrip, clip.ImageWidth, clip.ImageHeight, clip.MediaOffset, clip.MediaDuration, ClipSpeed(clip), blockWidth, height, scale),
                        peaks: clip.IsHidden || !showsSound ? null : MediaPeaks(clip.Waveform, clip, scale),
                        keys: ClipKeys(clip, 0, scale),
                        commit: (left, nowWidth) =>
                        {
                            _viewModel.Checkpoint($"move {clip.Name}");
                            (clip.StartTime, clip.Duration) = (Math.Round(left * perPixel, 2), Math.Round(nowWidth * perPixel, 2));
                        });
                }

                break;
        }

        // The playhead, so it can be seen where a split would fall.
        track.PlayheadX = _viewModel.PositionMs / 1000 * scale - track.ViewOffsetX;
    }

    /// <summary>
    /// The part of a clip's sound that its block shows: its file's waveform, laid out at the scale of the
    /// timeline and shifted to where the clip begins in it. The bar draws it from the peaks, at the block's own size.
    /// </summary>
    private static WaveSpan? MediaPeaks(ImageSource? picture, Layer clip, double scale)
    {
        // A clip that runs faster covers its file in less of the timeline, and its sound is that much narrower.
        var speed = ClipSpeed(clip);
        return clip.MediaDuration > 0.05
            ? WaveOf(picture, clip.MediaOffset * scale / speed, clip.MediaDuration * scale / speed)
            : WaveOf(picture, 0, 0);
    }

    /// <summary>One stretch of a sound, for a block: the sound is <paramref name="span"/> pixels wide in all, and the block begins <paramref name="offset"/> pixels into it.</summary>
    private static WaveSpan? WaveOf(ImageSource? picture, double offset, double span) =>
        Waveforms.DataOf(picture) is { } data ? new WaveSpan(data, offset, span) : null;

    /// <summary>How many times as fast as recorded a clip runs; 1 for a clip that has no speed of its own.</summary>
    private static double ClipSpeed(Layer clip) => clip.HasSpeed ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;

    /// <summary>A brush that will not change again, frozen: WPF then neither watches it nor ties it to this thread.</summary>
    private static ImageBrush Frozen(ImageBrush brush)
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    // The single frames of a filmstrip, cut from it once and kept for as long as the strip is.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, CroppedBitmap?[]> StripFrames = [];

    /// <summary>
    /// The frames a video's block is drawn with: as many as fit side by side at their own shape, each the
    /// frame of the file that is on screen at that point of the block. The file's strip holds a fixed
    /// number of frames, spread over its length; the nearest one is used, and a video that repeats starts again.
    /// </summary>
    /// <param name="filmstrip">The strip of frames made of the file, or null while there is none (yet).</param>
    /// <param name="pixelWidth">The video's own size, which gives a frame of the strip its shape.</param>
    /// <param name="mediaOffset">How far into the file the block begins, in seconds.</param>
    /// <param name="speed">How many times as fast as recorded the block runs through the file.</param>
    private static List<(ImageSource Picture, double Left, double Width)>? BuildFrames(ImageSource? filmstrip, int pixelWidth, int pixelHeight, double mediaOffset, double mediaDuration, double speed, double blockWidth, double height, double scale)
    {
        if (filmstrip is not BitmapSource strip || strip.PixelHeight <= 0 || blockWidth < 6)
            return null;

        var shape = pixelWidth > 0 && pixelHeight > 0 ? (double)pixelWidth / pixelHeight : 16.0 / 9;
        var count = Math.Max((int)Math.Round(strip.PixelWidth / (strip.PixelHeight * shape)), 1);
        var framePixels = strip.PixelWidth / count;
        if (framePixels < 2)
            return null;

        var frames = StripFrames.GetValue(strip, _ => new CroppedBitmap?[count]);
        if (frames.Length != count)
            return null;

        var slot = height * framePixels / strip.PixelHeight;
        var pictures = new List<(ImageSource Picture, double Left, double Width)>();
        for (var k = 0; k * slot < blockWidth && k < 80; k++)
        {
            // What the layer shows in the middle of this slot.
            var into = mediaOffset + (k + 0.5) * slot / scale * speed;
            var index = mediaDuration > 0.05 ? Math.Clamp((int)(into % mediaDuration / mediaDuration * count), 0, count - 1) : 0;
            var frame = frames[index] ??= Cut(strip, index * framePixels, framePixels);
            pictures.Add((frame, k * slot, slot));
        }

        return pictures;

        static CroppedBitmap Cut(BitmapSource strip, int x, int width)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(x, 0, width, strip.PixelHeight));
            frame.Freeze();
            return frame;
        }
    }

    /// <summary>Moves the playhead line of every bar; nothing else about them changes as the video plays, and nothing else is drawn again.</summary>
    private void MoveClipPlayheads()
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0)
            return;

        foreach (var track in _clipTracks)
        {
            if (!double.IsNaN(track.PlayheadX))
                track.PlayheadX = _viewModel.PositionMs / 1000 * track.ActualWidth * _timelineZoom / seconds - track.ViewOffsetX;
        }
    }

    private bool IsSelected(object? clip) => clip is null ? _mainClipSelected && _selectedClip is null : Equals(clip, _selectedClip);

    private void SelectClip(object? clip)
    {
        (_selectedClip, _mainClipSelected) = (clip, clip is null);
        _viewModel.KeyLayer = clip switch { Layer { IsPicture: true } layer => layer, MainClipRef => _viewModel.MainLayer, _ => _viewModel.KeyLayer };

        // The outline is drawn by each bar from what is selected: they are asked to draw themselves again.
        foreach (var track in _clipTracks)
            track.InvalidateVisual();
    }

    /// <summary>Puts a block on a bar. The bar draws it, and moves or stretches it when it is dragged.</summary>
    /// <param name="canDrag">Whether the block can be moved right now; null for one that never can (a cut segment).</param>
    /// <param name="commit">What letting go of a moved block does, given its new left edge and width in pixels.</param>
    /// <param name="frames">Frames of the block's video, drawn inside it.</param>
    /// <param name="peaks">The block's sound, drawn inside it over the frames.</param>
    private static void AddClip(
        TimelineTrack track, double left, double width, string name, Brush fill, object? clip,
        Func<bool>? canDrag = null, bool resizable = false, Action<double, double>? commit = null,
        IReadOnlyList<(ImageSource Picture, double Left, double Width)>? frames = null, WaveSpan? peaks = null, string cannotDrag = "",
        IEnumerable<double>? keys = null, double top = 0) =>
        track.Blocks.Add(new TimelineBlock
        {
            Left = left,
            Top = 1 + top,
            Width = Math.Max(width, 3),
            Height = Math.Max(track.ActualHeight - 2 - top, 4),
            Fill = fill,
            Name = name,
            Clip = clip,
            Frames = frames,
            Peaks = peaks,
            Keys = keys?.ToList(),
            CanDrag = canDrag,
            Resizable = resizable,
            Commit = commit,
            CannotDrag = cannotDrag,
            MayStartBeforeZero = clip is AudioClip or LayerSound,
        });

    // ----- The editing tools: each works on whatever is selected -----

    private void SplitClip_Click(object sender, RoutedEventArgs e) => SplitSelectedClip();

    /// <summary>The razor: cuts the selected block in two at the playhead. Nothing selected means the main video.</summary>
    private void SplitSelectedClip()
    {
        var at = _viewModel.PositionMs / 1000;
        switch (_selectedClip)
        {
            case Layer layer when _viewModel.Layers.Contains(layer):
                _viewModel.SplitLayer(layer, at);
                break;
            case AudioClip audio:
                _viewModel.SplitAudio(audio.Track, at);
                break;
            case LayerSound sound when _viewModel.Layers.Contains(sound.Layer):
                _viewModel.SplitLayer(sound.Layer, at);
                break;
            case CutSegment segment:
                _viewModel.SplitSegment(segment, at);
                break;
            default:
                // Nothing selected, or a clip of the main video: the main video is what is split.
                _viewModel.SplitMain(at);
                break;
        }

        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void TrimStart_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: true);

    private void TrimEnd_Click(object sender, RoutedEventArgs e) => TrimSelectedClip(start: false);

    private void TrimSelectedClip(bool start)
    {
        // A trimmed segment is a new one; the selection follows nothing, so it is let go.
        // The main video's own block, selected as a whole, is trimmed as the clip it is.
        var clip = _selectedClip is LayerSound sound ? sound.Layer : _selectedClip is MainClipRef ? _viewModel.MainLayer : _selectedClip;
        if (_viewModel.TrimClip(clip, start, _viewModel.PositionMs / 1000) && _selectedClip is CutSegment)
            (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    /// <summary>Delete: a layer or a cut segment goes; a part of an audio track is silenced (and, deleted again, plays again).</summary>
    private void DeleteSelectedClip()
    {
        switch (_selectedClip)
        {
            case AudioClip { Piece: { } piece } audio:
                _viewModel.ToggleAudioPiece(audio.Track, piece);
                break;
            case AudioClip audio:
                _viewModel.StatusText = $"{audio.Track.Title} is still whole. Split it at the playhead (S) first, then delete the part to silence; or set the track to Ignore / Drop to leave it out altogether.";
                return;
            case LayerSound sound:
                _viewModel.StatusText = $"The sound belongs to {sound.Layer.Name}: delete or hide the layer to take it out.";
                return;
            case MainClipRef piece:
                if (!_viewModel.DeleteMainClip(piece.Index))
                    return;
                break;
            default:
                if (!_viewModel.DeleteClip(_selectedClip))
                    return;
                break;
        }

        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Undo();
        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Redo();
        (_selectedClip, _mainClipSelected) = (null, false);
        ScheduleClipRedraw();
    }

    // Raised when either chain button is switched, by a click, a key or a screen reader; and, to no effect,
    // when a button merely follows the other one.
    private void LinkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not System.Windows.Controls.Primitives.ToggleButton button || (button.IsChecked == true) == _viewModel.AudioLinked)
            return;

        _viewModel.SetAudioLinked(button.IsChecked == true);

        // The button shows what is so, not what was clicked: the question may have been answered with no.
        if ((button.IsChecked == true) != _viewModel.AudioLinked)
            button.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _viewModel.AudioLinked);
        ScheduleClipRedraw();
    }

    // ----- Right-click on a track -----

    /// <summary>
    /// Fills a track's menu as it opens, with what can be done to it as it is now: to the track as a whole
    /// (its filters, its place, its mask, its look), and to the clip that was clicked (split, trim, delete).
    /// </summary>
    private void LayerRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Layer layer, ContextMenu: { } menu } row)
            return;

        // The clip the menu is about: the one selected on this track's bar, or the track's first.
        var clips = _viewModel.GetTrackClips(layer);
        var clip = _selectedClip switch
        {
            Layer selected when clips.Contains(selected) => selected,
            LayerSound sound when clips.Contains(sound.Layer) => sound.Layer,
            _ => layer,
        };

        menu.Items.Clear();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                action();
                ScheduleClipRedraw();
            };
            menu.Items.Add(item);
        }

        void Separate()
        {
            if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
                menu.Items.Add(new Separator());
        }

        var at = _viewModel.PositionMs / 1000;

        // The caption layer: how the subtitles look, and the editor for what they say and when.
        if (layer.IsCaptions)
        {
            Add("Caption Style Settings", () => CaptionStyle_Click(row, new RoutedEventArgs()));
            Add("Manual Edit", () => _ = OpenSubtitleEditorAsync());
            Separate();
        }

        // The track's own modifiers.
        if (layer.HasFilters)
            Add("Filters...", () => OpenLayerTool(layer, LayerTool.Filters));
        if (layer.HasFilters)
            Add("Position & Transform...", () => OpenLayerTool(layer, LayerTool.Transform));
        if (layer.HasMask)
            Add(layer is { IsVideo: true, CustomMask: true } ? "Redraw / Modify Mask..." : "Custom Mask...", () => OpenLayerTool(layer, LayerTool.Mask));
        if (layer.HasStyle)
            Add("Properties...", () => new LayerPropertiesDialog { Owner = this, DataContext = layer }.ShowDialog());
        if (layer.IsBackground)
            Add("Blur Settings...", () => BlurSettings_Click(row, new RoutedEventArgs()));
        if (layer.IsVideo)
            Add("Draw Target", () => _viewModel.DrawTargetCommand.Execute(layer));

        if (layer.IsMainVideo)
        {
            Separate();
            Add("Split at Playhead", () => _viewModel.SplitMain(at));
            if (_selectedClip is MainClipRef selectedPiece)
                Add("Delete This Clip", () => _viewModel.DeleteMainClip(selectedPiece.Index), _viewModel.GetMainClips().Count > 1);
            Add("Trim Start to Playhead", () => _viewModel.TrimClip(layer, start: true, at));
            Add("Trim End to Playhead", () => _viewModel.TrimClip(layer, start: false, at));
            Add("Back to the Start of the Timeline, Whole", () => _viewModel.ResetMainTiming(),
                layer.StartTime > 0.001 || layer.MediaOffset > 0.001 || layer.Duration > 0.001);
            Add("Reset Position and Zoom", () => _viewModel.ResetCenterVideoCommand.Execute(null));
        }
        else if (layer.HasTiming)
        {
            Separate();
            Add("Split at Playhead", () => _viewModel.SplitLayer(clip, at));
            Add("Trim Start to Playhead", () => _viewModel.TrimClip(clip, start: true, at));
            Add("Trim End to Playhead", () => _viewModel.TrimClip(clip, start: false, at));
            Add(clip.IsAudio ? "Play from Its Start to Its End" : "Show for the Whole Video", () =>
            {
                _viewModel.Checkpoint($"show {clip.Name} throughout");
                (clip.StartTime, clip.Duration, clip.MediaOffset) = (0, 0, 0);
            }, clip.StartTime > 0.001 || clip.Duration > 0.001);
        }

        if (clip.IsAnimated)
        {
            Separate();
            Add("Clear Keyframes", () => _viewModel.ClearKeys(clip));
        }

        // How fast the clip runs: a few usual speeds, the one it has ticked. Anything between is typed into its Speed box.
        if (clip.HasSpeed)
        {
            Separate();
            var speeds = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Speed ({clip.Speed:0.##}x)") };
            foreach (var speed in new[] { 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4, 10 })
            {
                var item = new MenuItem
                {
                    Header = string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x{(speed == 1 ? "  (as recorded)" : "")}"),
                    IsCheckable = true, IsChecked = Math.Abs(clip.Speed - speed) < 0.0005,
                };
                item.Click += (_, _) =>
                {
                    _viewModel.SetClipSpeed(clip, speed);
                    ScheduleClipRedraw();
                };
                speeds.Items.Add(item);
            }

            menu.Items.Add(speeds);
        }

        // What its sound does when someone is speaking.
        if (layer.CarriesSound)
        {
            Separate();
            AddDuckingItems(menu, layer, layer.AutoDuck, layer.IsVoice);
        }

        if (layer.CanReorder && !layer.IsAudio)
        {
            Separate();
            Add("Move Up (towards the front)", () => _viewModel.MoveLayerUpCommand.Execute(layer));
            Add("Move Down (towards the back)", () => _viewModel.MoveLayerDownCommand.Execute(layer));
        }

        Separate();
        if (layer.IsRemovable || layer.IsMainVideo || layer.IsBackground)
            Add(layer.IsHidden ? "Show" : layer.IsAudio ? "Mute" : "Hide", () => _viewModel.ToggleHidden(layer));
        if (layer.IsRemovable)
        {
            Add("Duplicate", () => _viewModel.DuplicateLayer(layer));
            if (layer.CarriesSound)
            {
                Add("Use Its Sound for Auto-Captions", () => _viewModel.UseLayerAudioForCaptions(layer));
                Add("Remove Dead Air...", () => RunDeadAir(clip));
            }

            Separate();
            if (clips.Count > 1)
                Add("Delete This Clip", () => _viewModel.DeleteClip(clip));
            Add(clips.Count > 1 ? "Delete the Whole Track" : "Delete", () => _viewModel.RemoveTrack(layer));
        }

        if (menu.Items.Count > 0 && menu.Items[^1] is Separator)
            menu.Items.RemoveAt(menu.Items.Count - 1);
        e.Handled = menu.Items.Count == 0;
    }

    /// <summary>The two ducking boxes of a sound's right-click menu: turned down under a voice, or the voice itself.</summary>
    private void AddDuckingItems(ContextMenu menu, object sound, bool ducks, bool isVoice)
    {
        foreach (var (header, isOn, voice, tip) in new[]
                 {
                     ("Auto-Duck against Voice", ducks, false, "Turns this sound down, by the Duck by (dB) amount on the Audio tab, whenever a sound marked as a voice is speaking."),
                     ("Voiceover / Dialogue", isVoice, true, "This sound is a voice: the sounds set to Auto-Duck make room for it."),
                 })
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn, ToolTip = tip };
            item.Click += (_, _) => _viewModel.ToggleDucking(sound, voice);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// The right-click menu of a row of the Audio tab. The rows are one template; what the menu offers is
    /// what the sound is: a clip from a file has a clip's menu (split, trim, speed, delete), and one of the
    /// video's own tracks has its ducking.
    /// </summary>
    private void AudioRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer layer, ContextMenu: { } clipMenu })
        {
            LayerRow_ContextMenuOpening(sender, e);
            if (layer.IsAudio)
                AddRenameItem(clipMenu, layer.Title, name => layer.Title = name);
            e.Handled = clipMenu.Items.Count == 0;
            return;
        }

        if (sender is not FrameworkElement { DataContext: AudioTrack track, ContextMenu: { } menu })
            return;

        menu.Items.Clear();
        AddDuckingItems(menu, track, track.AutoDuck, track.IsVoice);
        AddRenameItem(menu, track.Title, name => track.Title = name);
    }

    /// <summary>
    /// Puts a box for the sound's name at the top of its menu. The heading of a row is the file the sound comes
    /// from; the name is the title a track of the video is saved under, or what a clip is called.
    /// </summary>
    private static void AddRenameItem(ContextMenu menu, string name, Action<string> rename)
    {
        var box = new TextBox { Text = name, MinWidth = 190, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
                rename(box.Text.Trim());
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
                menu.IsOpen = false;
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(box);
        menu.Items.Insert(0, new MenuItem { Header = line, StaysOpenOnClick = true, ToolTip = "The title a track of the video is saved under in the output file, or what a clip is called." });
        if (menu.Items.Count > 1)
            menu.Items.Insert(1, new Separator());
    }

    /// <summary>Opens one of a track's modifier dialogs. What it changes can be undone as one step.</summary>
    private void OpenLayerTool(Layer layer, LayerTool tool)
    {
        _viewModel.Checkpoint(tool switch
        {
            LayerTool.Filters => $"change the filters of {layer.Name}",
            LayerTool.Transform => $"move or turn {layer.Name}",
            _ => $"change the mask of {layer.Name}",
        });

        var dialog = new LayerToolDialog(layer, _viewModel, tool) { Owner = this };
        dialog.ShowDialog();
        if (dialog.RedrawRequested)
            _viewModel.DrawTargetCommand.Execute(layer);
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer layer })
            _viewModel.RemoveTrack(layer);
    }

    private void SegmentSkip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CutSegment segment })
            _viewModel.ToggleSkip(segment);
        ScheduleClipRedraw();
    }

    private void SegmentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CutSegment segment })
            _viewModel.DeleteClip(segment);
    }

    /// <summary>Remove Dead Air on what is given: a video layer, an audio track, or (null) the main video.</summary>
    private void RunDeadAir(object? target)
    {
        var name = target switch { Layer layer => layer.Name, AudioTrack track => $"the video, listening to {track.Title}", _ => "the video" };
        var dialog = new DeadAirDialog(_viewModel.IsEditorMode, name) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.RunDeadAir(dialog.Mode, target);
    }

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

    // A layer's row picked on the Layers tab: it is the one the Keyframes pane and the Text Settings work on,
    // as its block picked on the timeline is.
    private void LayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayersList?.SelectedItem is Layer { IsPicture: true, IsCaptions: false } layer)
            _viewModel.KeyLayer = layer;
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
        if (outline)
            layer.OutlineColor = color;
        else
            layer.FontColor = color;
    }

    // ----- Add Layer: one button, and a menu of what kind -----

    // While the menu is open the button does not answer the mouse: the click that closes the menu would otherwise open it again.
    private void AddLayerPopup_Opened(object sender, EventArgs e) => AddLayerButton.IsHitTestVisible = false;

    private void AddLayerPopup_Closed(object sender, EventArgs e) => AddLayerButton.IsHitTestVisible = true;

    private void AddLayerMenu_Click(object sender, RoutedEventArgs e)
    {
        AddLayerButton.IsChecked = false;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "Video": AddVideo_Click(sender, e); break;
            case "Image": AddImage_Click(sender, e); break;
            case "Text": _viewModel.AddTextLayerCommand.Execute(null); break;
            case "Region": _viewModel.AddLayerCommand.Execute(null); break;
        }
    }

    // ----- Subtitles: importing a track, and the editor -----

    private void ImportSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select subtitles to import", Filter = SubtitleFiles.ImportFilter };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ImportSubtitles(dialog.FileName);
    }

    private async void EditSubtitles_Click(object sender, RoutedEventArgs e) => await OpenSubtitleEditorAsync();

    /// <summary>
    /// Opens the Subtitle Editor on the subtitle track. With no track yet and auto-captions on, the track is
    /// first made from them, so that what was transcribed is what is edited. While the editor is open the
    /// main player draws no subtitles: the editor's own player does.
    /// </summary>
    private async Task OpenSubtitleEditorAsync()
    {
        if (_viewModel.IsSubtitleEditorOpen)
            return;

        if (!_viewModel.HasSubtitleCues && _viewModel.AutoCaptions && _viewModel.HasSource)
            await _viewModel.ConvertCaptionsToCuesAsync();

        if (PlayerIsPlaying)
            PlayerSetPause(true);
        _viewModel.IsSubtitleEditorOpen = true;
        try
        {
            new Views.SubtitleEditorWindow(_viewModel) { Owner = this }.ShowDialog();
        }
        finally
        {
            _viewModel.IsSubtitleEditorOpen = false;
            ScheduleClipRedraw();
        }
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an image to place on the frame", Filter = "Images|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.AddImageLayer(dialog.FileName);
    }

    // ----- Drawing on the video -----
    // One canvas lies over the video and serves three jobs, one at a time:
    //   Crop        drag a rectangle to set the Top/Bottom/Left/Right crop;
    //   DrawTarget  drag a rectangle to mark where a layer is in the source.
    // Arranging the layers on the output frame has a pane of its own beside the player; see further down.

    private enum OverlayMode
    {
        None,
        Crop,
        DrawTarget,
        Arrange,
    }

    private readonly List<UIElement> _arrangeVisuals = [];
    private readonly List<(Layer Layer, PropertyChangedEventHandler Handler)> _arrangeSubscriptions = [];
    private readonly List<Adorner> _arrangeAdorners = [];

    // Puts the centre video's box where the view model says it is; set while Arrange is showing.
    private Action? _placeCenter;
    private Point? _dragStart;

    private OverlayMode CurrentOverlayMode =>
        _viewModel.IsInteractiveCropActive ? OverlayMode.Crop
        : _viewModel.DrawTargetLayer is not null ? OverlayMode.DrawTarget
        : OverlayMode.None;

    /// <summary>Where the picture actually is inside the player: the video is letterboxed to keep its shape.</summary>
    private Rect GetDisplayedVideoArea(out double scale)
    {
        scale = 0;
        double sourceWidth = _viewModel.SourceWidth, sourceHeight = _viewModel.SourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0 || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0)
            return Rect.Empty;

        scale = Math.Min(CropCanvas.ActualWidth / sourceWidth, CropCanvas.ActualHeight / sourceHeight);
        var size = new Size(sourceWidth * scale, sourceHeight * scale);
        return new Rect(new Point((CropCanvas.ActualWidth - size.Width) / 2, (CropCanvas.ActualHeight - size.Height) / 2), size);
    }

    /// <summary>Shows or hides the canvas for the mode the view model is now in.</summary>
    private void UpdateOverlayMode()
    {
        var mode = CurrentOverlayMode;
        if (mode != OverlayMode.None && _viewModel.SourceWidth <= 0)
        {
            _viewModel.StatusText = "Load a video before drawing on it.";
            (_viewModel.IsInteractiveCropActive, _viewModel.DrawTargetLayer) = (false, null);
            return;
        }

        CropCanvas.Visibility = mode == OverlayMode.None ? Visibility.Collapsed : Visibility.Visible;
        ApplyLiveFilter();


        // Red for the crop, gold for a UI layer, so it is clear what is being drawn.
        var colour = mode == OverlayMode.DrawTarget ? Colors.Gold : Colors.Red;
        CropRectangle.Stroke = new SolidColorBrush(colour);
        CropRectangle.Fill = new SolidColorBrush(Color.FromArgb(0x18, colour.R, colour.G, colour.B));

        _viewModel.StatusText = mode switch
        {
            OverlayMode.Crop => "Drag on the video to draw the crop. Double-click inside the rectangle to centre it.",
            OverlayMode.DrawTarget => $"Drag on the video to mark where {_viewModel.DrawTargetLayer!.Name} is.",

            _ => _viewModel.StatusText,
        };

        // Let the canvas take its size before anything is placed on it.
        Dispatcher.BeginInvoke(RedrawOverlay, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawOverlay();

    private void RedrawOverlay()
    {
        switch (CurrentOverlayMode)
        {
            case OverlayMode.Crop:
                var crop = _viewModel.GetCropRect();
                ShowSourceRectangle(crop.Left, crop.Top, crop.Width, crop.Height);
                break;

            case OverlayMode.DrawTarget when _viewModel.DrawTargetLayer is { } element:
                var (x, y, width, height) = element.GetSourceRect(_viewModel.SourceWidth, _viewModel.SourceHeight);
                ShowSourceRectangle(x, y, width, height);
                break;


            default:
                CropRectangle.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>Draws a rectangle given in source pixels at the matching place on the displayed video.</summary>
    private void ShowSourceRectangle(int left, int top, int width, int height)
    {
        var area = GetDisplayedVideoArea(out var scale);
        if (area.IsEmpty || width <= 0 || height <= 0)
        {
            CropRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        CropRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRectangle, area.X + Math.Max(left, 0) * scale);
        Canvas.SetTop(CropRectangle, area.Y + Math.Max(top, 0) * scale);
        CropRectangle.Width = width * scale;
        CropRectangle.Height = height * scale;
    }

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var mode = CurrentOverlayMode;
        var area = GetDisplayedVideoArea(out _);
        if (area.IsEmpty || mode == OverlayMode.None)
            return;

        var point = e.GetPosition(CropCanvas);

        // Double-click inside the crop rectangle: keep its size, centre it on the frame.
        if (e.ClickCount == 2 && mode == OverlayMode.Crop)
        {
            var current = new Rect(Canvas.GetLeft(CropRectangle), Canvas.GetTop(CropRectangle), CropRectangle.Width, CropRectangle.Height);
            if (CropRectangle.Visibility == Visibility.Visible && current.Contains(point))
            {
                // The crop is in percent of the source, so centring is sharing each pair out equally.
                var horizontal = _viewModel.CropLeft + _viewModel.CropRight;
                var vertical = _viewModel.CropTop + _viewModel.CropBottom;
                (_viewModel.CropLeft, _viewModel.CropRight) = (horizontal / 2, horizontal / 2);
                (_viewModel.CropTop, _viewModel.CropBottom) = (vertical / 2, vertical / 2);
            }

            _dragStart = null;
            return;
        }

        _dragStart = ClampTo(area, point);
        CropCanvas.CaptureMouse();
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var area = GetDisplayedVideoArea(out var scale);
        if (area.IsEmpty)
            return;

        var drawn = new Rect(start, ClampTo(area, e.GetPosition(CropCanvas)));

        // A click with a trembling hand is not a rectangle.
        if (drawn.Width < 4 || drawn.Height < 4)
            return;

        // Even numbers of pixels: most encoders need even frame sizes.
        var left = Even((drawn.Left - area.Left) / scale);
        var top = Even((drawn.Top - area.Top) / scale);

        if (_viewModel.DrawTargetLayer is { } element)
        {
            // Kept as fractions of the frame, which is all the drawn rectangle really says.
            (element.SourceX, element.SourceY) = ((drawn.Left - area.Left) / area.Width, (drawn.Top - area.Top) / area.Height);
            (element.SourceWidth, element.SourceHeight) = (drawn.Width / area.Width, drawn.Height / area.Height);
            RedrawOverlay();
        }
        else
        {
            // Source pixels here; the view model keeps them as percentages of the source.
            _viewModel.SetCropFromPixels(left, top, Even((area.Right - drawn.Right) / scale), Even((area.Bottom - drawn.Bottom) / scale));
        }
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragStart is not null;
        _dragStart = null;
        CropCanvas.ReleaseMouseCapture();

        // Marking a layer is a one-shot job: once the rectangle is drawn, the canvas goes away again.
        if (wasDragging && _viewModel.DrawTargetLayer is { } element)
        {
            _viewModel.DrawTargetLayer = null;
            var (x, y, width, height) = element.GetSourceRect(_viewModel.SourceWidth, _viewModel.SourceHeight);
            _viewModel.StatusText = $"{element.Name}: {width} x {height} at {x}, {y} in the source.";
        }
    }

    // ----- The layout pane: arranging layers on the output frame -----

    /// <summary>Opens or closes the pane beside the video, following the Layout Pane box.</summary>
    /// <param name="announce">Whether to say how the pane is used: when it has just been opened, not when the panes are laid out again.</param>
    private void UpdateLayoutPane(bool announce = true)
    {
        var open = _viewModel.IsArrangeActive;
        var opening = open && LayoutPane.Visibility != Visibility.Visible;
        LayoutPane.Visibility = LayoutSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        // Closed, the column gives its width back to the video; opened, it has the width it was last dragged
        // to (or the one it starts with), or less when the window is too narrow to spare that much.
        var wanted = AppSettings.Current.LayoutPaneWidth > 0 ? AppSettings.Current.LayoutPaneWidth : DefaultLayoutPaneWidth;
        LayoutColumn.Width = new GridLength(open ? Math.Min(wanted, Math.Max(PlayerCell.ActualWidth - 530, 160)) : 0);

        if (opening && announce)
            _viewModel.StatusText = "Drag the layers into place in the layout pane. Drag a corner handle to resize; double-click a layer to center it.";

        // Let the canvas take its size before anything is placed on it.
        Dispatcher.BeginInvoke(RedrawLayout, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawLayout();

    private void RedrawLayout()
    {
        ClearArrangeVisuals();
        if (_viewModel.IsArrangeActive)
            BuildArrangeVisuals();
    }

    /// <summary>
    /// Draws the output frame, in whatever shape Resolution &amp; Cropping on the Video tab gives it, with a box
    /// for each layer: the sharp centre video and the layers when the engine is on, and the caption box
    /// when auto-captions are. Every box is dragged to move its layer, and carries a resize handle on its
    /// corner. The boxes and the sliders show the same numbers: moving either moves the other.
    /// </summary>
    private void BuildArrangeVisuals()
    {
        if (LayoutCanvas.ActualWidth <= 0 || LayoutCanvas.ActualHeight <= 0)
            return;

        var (frameWidth, frameHeight) = (_viewModel.FrameWidth, _viewModel.FrameHeight);
        var scale = Math.Min(LayoutCanvas.ActualWidth / frameWidth, LayoutCanvas.ActualHeight / frameHeight);
        var frame = new Rect(
            (LayoutCanvas.ActualWidth - frameWidth * scale) / 2,
            (LayoutCanvas.ActualHeight - frameHeight * scale) / 2,
            frameWidth * scale,
            frameHeight * scale);

        // The stage: the output frame itself.
        var stage = new System.Windows.Shapes.Rectangle
        {
            Width = frame.Width,
            Height = frame.Height,
            Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(stage, frame.X);
        Canvas.SetTop(stage, frame.Y);
        _arrangeVisuals.Add(stage);
        LayoutCanvas.Children.Add(stage);

        // The sharp video first, so that it lies under the layers as it does in the output.
        if (_viewModel.FrameEngine)
        {
            _placeCenter = AddArrangeLayer(
                "Center Video", (Style)FindResource("CenterThumb"), dataContext: null, frame, scale,
                _viewModel.GetCenterRect,
                _viewModel.MoveCenter,
                (deltaX, _) => _viewModel.ResizeCenter(deltaX),
                reset: () => _viewModel.ResetCenterVideoCommand.Execute(null),
                rotation: () => _viewModel.MainLayer.Rotation);

            // Turned in its dialog, the box turns with it.
            PropertyChangedEventHandler mainHandler = (_, _) => _placeCenter?.Invoke();
            _viewModel.MainLayer.PropertyChanged += mainHandler;
            _arrangeSubscriptions.Add((_viewModel.MainLayer, mainHandler));
        }

        // Last, so that it lies on top of everything, as the captions do in the output.
        if (_viewModel.AutoCaptions)
        {
            var captions = _viewModel.CaptionLayer;
            var placeCaptions = AddArrangeLayer(
                "Subtitles", (Style)FindResource("CaptionThumb"), captions, frame, scale,
                () => captions.GetOutputRect(frameWidth, frameHeight, 0, 0),
                (deltaX, deltaY) =>
                {
                    captions.PositionX = Math.Clamp(captions.PositionX + deltaX / frameWidth, 0, 1);
                    captions.PositionY = Math.Clamp(captions.PositionY + deltaY / frameHeight, 0, 1);
                },
                (deltaX, deltaY) =>
                {
                    captions.SizeWidth = Math.Clamp(captions.SizeWidth + deltaX / frameWidth, 0.05, 1);
                    captions.SizeHeight = Math.Clamp(captions.SizeHeight + deltaY / frameHeight, 0.03, 1);
                },
                reset: () =>
                {
                    captions.PositionX = Math.Clamp((1 - captions.SizeWidth) / 2, 0, 1);
                    captions.PositionY = Math.Clamp((1 - captions.SizeHeight) / 2, 0, 1);
                });

            PropertyChangedEventHandler captionHandler = (_, _) => placeCaptions();
            captions.PropertyChanged += captionHandler;
            _arrangeSubscriptions.Add((captions, captionHandler));
        }

        // One box per track: its clips share a place, a size and a look.
        foreach (var element in _viewModel.FrameEngine ? _viewModel.Layers.Where(l => !l.IsAudio).GroupBy(l => l.TrackId).Select(t => t.First()).ToList() : [])
        {
            var place = AddArrangeLayer(
                element.Name, (Style)FindResource("LayerThumb"), element, frame, scale,
                () => element.GetOutputRect(frameWidth, frameHeight, _viewModel.SourceWidth, _viewModel.SourceHeight),
                (deltaX, deltaY) =>
                {
                    // Output pixels to fractions of the frame. Setting them moves the sliders, and the box follows them.
                    // A layer may be dragged off the frame: what is outside is cut off, and the frame stays as it is.
                    element.PositionX = Math.Clamp(element.PositionX + deltaX / frameWidth, -2, 2);
                    element.PositionY = Math.Clamp(element.PositionY + deltaY / frameHeight, -2, 2);
                },
                (deltaX, deltaY) =>
                {
                    // Dragged by its corner, the opposite corner is what stays put.
                    element.FromCorner(() =>
                    {
                        element.SizeWidth = Math.Clamp(element.SizeWidth + deltaX / frameWidth, 0.02, 4);

                        // Locked, the height follows the width by itself; unlocked, it is dragged separately.
                        if (!element.LockAspectRatio)
                            element.SizeHeight = Math.Clamp(element.SizeHeight + deltaY / frameHeight, 0.01, 4);
                    });
                },
                reset: () =>
                {
                    // A layer keeps its size; it goes to the middle of the frame.
                    var (_, _, width, height) = element.GetOutputRect(frameWidth, frameHeight, _viewModel.SourceWidth, _viewModel.SourceHeight);
                    element.PositionX = (frameWidth - width) / 2.0 / frameWidth;
                    element.PositionY = (frameHeight - height) / 2.0 / frameHeight;
                },
                rotation: () => element.Rotation);

            PropertyChangedEventHandler handler = (_, _) => place();
            element.PropertyChanged += handler;
            _arrangeSubscriptions.Add((element, handler));
        }
    }

    /// <summary>
    /// Adds one draggable, resizable layer to the canvas: the same for the centre video, a piece of the
    /// video and an image. Returns the action that puts its box where the layer now is.
    /// </summary>
    /// <param name="getRect">The layer's place on the output frame, in output pixels.</param>
    /// <param name="move">Moves the layer by a distance in output pixels.</param>
    /// <param name="resize">Grows the layer by a distance in output pixels.</param>
    /// <param name="reset">What a double-click does: puts the layer back in the middle of the frame.</param>
    private Action AddArrangeLayer(
        string name, Style style, object? dataContext, Rect frame, double scale,
        Func<(int X, int Y, int Width, int Height)> getRect, Action<double, double> move, Action<double, double> resize, Action reset, Func<double>? rotation = null)
    {
        var box = new Thumb
        {
            Style = style,
            DataContext = dataContext,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move. Drag the corner handle to resize. Double-click to center.",
        };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        box.DragDelta += (_, e) => move(e.HorizontalChange / scale, e.VerticalChange / scale);
        box.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            // The second press of a double-click also starts a drag. It is called off first: the box is
            // about to jump out from under the pointer, and a drag would carry it straight back.
            box.CancelDrag();
            reset();
            e.Handled = true;
        };

        void Place()
        {
            var (x, y, width, height) = getRect();
            box.Width = Math.Max(width * scale, 6);
            box.Height = Math.Max(height * scale, 6);
            Canvas.SetLeft(box, frame.X + x * scale);
            Canvas.SetTop(box, frame.Y + y * scale);

            // Shown turned as the layer is. (It is still dragged along the frame's own axes.)
            var angle = rotation?.Invoke() ?? 0;
            (box.RenderTransformOrigin, box.RenderTransform) = (new Point(0.5, 0.5), Math.Abs(angle % 360) < 0.05 ? Transform.Identity : new RotateTransform(angle));
        }

        _arrangeVisuals.Add(box);
        // The caption box was made first so that its handlers exist, but it belongs on top: layers go under it.
        var captionBox = LayoutCanvas.Children.OfType<Thumb>().FirstOrDefault(t => t.DataContext is Layer { IsCaptions: true });
        if (captionBox is not null && !ReferenceEquals(dataContext, captionBox.DataContext))
            LayoutCanvas.Children.Insert(LayoutCanvas.Children.IndexOf(captionBox), box);
        else
            LayoutCanvas.Children.Add(box);
        Place();

        // The handle is an adorner on the box: it stays on the corner however the box moves or grows.
        if (AdornerLayer.GetAdornerLayer(box) is { } layer)
        {
            var adorner = new ResizeAdorner(box, (Style)FindResource("LayerGrip"), name);
            adorner.ResizeDelta += (_, e) => resize(e.HorizontalChange / scale, e.VerticalChange / scale);
            layer.Add(adorner);
            _arrangeAdorners.Add(adorner);
        }

        return Place;
    }

    private void ClearArrangeVisuals()
    {
        _placeCenter = null;

        foreach (var (element, handler) in _arrangeSubscriptions)
            element.PropertyChanged -= handler;
        _arrangeSubscriptions.Clear();

        foreach (var adorner in _arrangeAdorners)
            AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
        _arrangeAdorners.Clear();

        foreach (var visual in _arrangeVisuals)
            LayoutCanvas.Children.Remove(visual);
        _arrangeVisuals.Clear();
    }
    // ----- Render preview and audio mixer -----

    private void ShowPreview(string path) => new PreviewPlayerWindow(path) { Owner = this }.Show();

    /// <summary>The filters of one audio track, from its row on the Audio tab.</summary>
    private void TrackFilters_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case Models.AudioTrack track:
                var forTrack = new TrackAudioFiltersDialog(track) { Owner = this };
                if (forTrack.ShowDialog() == true)
                    track.Filters = forTrack.Filters;
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
        _voiceoverStudio.Show();
    }

    private VoiceoverStudioDialog? _voiceoverStudio;
    /// <summary>The caption box as a layer: its opacity.</summary>
    private void CaptionLayerStyle_Click(object sender, RoutedEventArgs e) =>
        new LayerPropertiesDialog { Owner = this, DataContext = _viewModel.CaptionLayer }.ShowDialog();

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

    // ----- Auto-captions -----

    private void BrowseCaptionAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the audio to transcribe",
            Filter = "Audio and video files|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.mov;*.webm|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            (_viewModel.CaptionAudioPath, _viewModel.CaptionUseExternalAudio) = (dialog.FileName, true);
    }

    private void ClearCaptionAudio_Click(object sender, RoutedEventArgs e) => _viewModel.CaptionAudioPath = "";

    private void CaptionStyle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CaptionStyleDialog(_viewModel.CaptionStyle) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.SetCaptionStyle(dialog.EditedStyle);
    }

    // ----- Layout files -----

    private const string LayoutFileFilter = "HandPeg style preset (*.hpstyle)|*.hpstyle|All files|*.*";

    private void ToggleMode_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleMode();

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
    /// Encoder Mode: the output row, and the status bar under it. Editor Mode turns that round, so that what
    /// was just done is said where the eye is and the way out is at the far end: the status text and the
    /// progress bar are on the left and Save As with the export buttons on the right, on one row in a window
    /// wide enough for both, and otherwise with the status bar under the output row.
    /// </summary>
    private void UpdateBottomBar()
    {
        var editor = _viewModel.IsEditorMode;
        var merged = editor && ActualWidth >= MergedBottomBarWidth;

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
        StatusProgress.Width = merged ? 140 : 240;

        // Inside the status bar, Editor Mode has the progress bar first, at the left edge, and the text after it.
        var inside = StatusBar.ColumnDefinitions;
        (inside[0].Width, inside[1].Width, inside[2].Width) = editor ? (GridLength.Auto, Star, GridLength.Auto) : (Star, GridLength.Auto, GridLength.Auto);
        Grid.SetColumn(StatusProgressHost, editor ? 0 : 2);
        Grid.SetColumn(StatusTextHost, editor ? 1 : 0);
        Grid.SetColumn(UpdateNotice, editor ? 2 : 1);
        StatusProgressHost.Margin = editor ? new Thickness(-10, 0, 12, 0) : new Thickness(0);
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

    private void BlurSettings_Click(object sender, RoutedEventArgs e) =>
        new BlurSettingsDialog { Owner = this, DataContext = _viewModel }.ShowDialog();

    private void LayerProperties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer element })
            new LayerPropertiesDialog { Owner = this, DataContext = element }.ShowDialog();
    }

    // ----- Timeline hover previews -----

    private BitmapSource? _spriteSheet;

    /// <summary>Loads the thumbnail sheet into memory, so the file itself is not kept open.</summary>
    private void LoadSpriteSheet()
    {
        _spriteSheet = null;
        PreviewPopup.IsOpen = false;
        if (_viewModel.SpriteSheetPath is not { } path || !File.Exists(path))
            return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            _spriteSheet = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            // A sheet that cannot be read just means no previews.
        }

        RebuildFilmstrip();
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

/// <summary>One target of the drop overlay: a preset to drop a file on, with the icon it is drawn with.</summary>
public sealed record DropTargetItem(string Name, string IconData, object Target)
{
    /// <summary>A card with a header: a style.</summary>
    public const string StyleIcon = "M 3,5 H 23 V 21 H 3 Z M 3,10 H 23 M 7,14 H 15 M 7,17 H 12";

    /// <summary>Sliders: an encoding preset.</summary>
    public const string EncodeIcon = "M 3,7 H 23 M 3,13 H 23 M 3,19 H 23 M 8,5 V 9 M 17,11 V 15 M 11,17 V 21";
}
