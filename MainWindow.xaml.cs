using System.ComponentModel;
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
        InitializeComponent();
        DataContext = _viewModel;

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

        // The choices of the rule columns on the Automation tab.
        RuleTypeColumn.ItemsSource = SmartRule.Types;
        PresetColumn.ItemsSource = _viewModel.PresetNames;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += (_, _) => _ = CheckForHandPegUpdateAsync();

        _viewModel.LiveFilterInvalidated += ScheduleLiveFilter;

        // The time bars on the Layers tab follow the cuts, the layers, and anything about a layer that changes.
        _viewModel.LiveFilterInvalidated += ScheduleClipRedraw;
        _viewModel.Segments.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.Layers.CollectionChanged += (_, _) => ScheduleClipRedraw();
        _viewModel.TimelineChanged += ScheduleClipRedraw;
        _viewModel.MainLayer.PropertyChanged += (_, _) => ScheduleClipRedraw();
        VideoView.SizeChanged += (_, _) => ScheduleLiveFilter();
        _liveFilterTimer.Tick += (_, _) => ApplyLiveFilter();

        // The player starts once there is a surface for it to draw on, away from the UI thread.
        VideoView.SurfaceReady += () => _ = EnsurePlayerAsync();
        Loaded += (_, _) => _ = EnsurePlayerAsync();

        // Once the window has been drawn, so the launch window opens over the program rather than over nothing.
        // (ContentRendered is raised once, when the window has actually been drawn; waiting for the dispatcher
        // to fall idle instead could be put off indefinitely by a player that is busy.)
        ContentRendered += (_, _) =>
        {
            StyleLibrary.EnsureBuiltIns();
            ShowLaunchWindow();
        };
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

        var splash = new SplashWindow { Owner = this };
        splash.ShowDialog();
        if (splash.Request is not { } request)
            return;

        _ = OpenLaunchRequestAsync(request);
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
    }

    // ----- Hotkeys -----

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

        var fits = TimelineOptionsHost.ActualWidth >= _timelineOptionsWidth + 4;
        (TimelineOptionsInline.Visibility, TimelineOptionsButton.Visibility) = fits ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Hidden, Visibility.Visible);
        if (fits)
            TimelineOptionsButton.IsChecked = false;
    }

    // The button that opened the menu would, clicked again to close it, open it straight back up: the click
    // that closes the menu from outside reaches the button as well. So it is deaf while the menu is open.
    private void TimelineOptionsPopup_Opened(object? sender, EventArgs e) => TimelineOptionsButton.IsHitTestVisible = false;

    private void TimelineOptionsPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => TimelineOptionsButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);

    // ----- Drag and drop -----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;

        e.Handled = true;
        var file = files[0];

        // Dropped on the layers, with a video open: the files become layers of it, at the moment they were dropped on.
        if (_viewModel.HasSource && LayersList.IsVisible)
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
        if (_viewModel.HasSource && AudioPanel.IsVisible)
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

    // ----- The player: libmpv -----
    // mpv draws into the video surface, reports where it is as it plays, and can run an FFmpeg filter graph on
    // the picture (its lavfi-complex property). That graph is Live Preview: with the box ticked it is the one
    // the export would use for the picture, rebuilt whenever a setting changes; unticked, there is none and
    // the source is shown as it is.

    private MpvPlayer? _mpv;
    private bool _startingPlayer;

    // What to open once the player exists: a video loaded before it had started, or the one a reload is bringing back.
    private (string Path, long StartMs, bool Paused)? _pendingMedia;

    // The graph mpv is running, and one it refused: that one is not offered again until the settings change.
    private string _liveGraph = "";
    private string? _failedLiveGraph;

    // Sliders report every step of a drag; the graph is rebuilt once they have been still for a moment.
    private readonly System.Windows.Threading.DispatcherTimer _liveFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private bool PlayerIsPlaying => _mpv is { IsPlaying: true };

    private void PlayerSetPause(bool paused) => _mpv?.SetPause(paused);

    private void PlayerSeek(long positionMs, bool exact = true) => _mpv?.Seek(positionMs, exact);

    /// <summary>
    /// Starts the player when there is none yet. Loading libmpv and starting it take a moment, and happen on
    /// the thread pool: the window is never held up by them. Without libmpv (not downloaded yet) there is
    /// simply no player; everything else works, and this is tried again when the dependencies change.
    /// </summary>
    private async Task EnsurePlayerAsync()
    {
        if (_mpv is not null || _startingPlayer)
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
            player.DurationChanged += length => OnUi(() =>
            {
                if (length > 0)
                    _viewModel.DurationMs = length;
            });
            player.StateChanged += () => OnUi(() => _viewModel.IsPlaying = player.IsPlaying);
            player.FileLoaded += () => OnUi(() =>
            {
                // Opening a file takes the graph off; it is given again, for this file.
                _liveGraph = "";
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
                _pendingMedia = (_viewModel.LocalMediaPath, (long)_viewModel.PositionMs, true);
            if (_pendingMedia is { } media)
            {
                _pendingMedia = null;
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

        OpenInPlayer(path, 0, paused: false);
    }

    private void OpenInPlayer(string path, long startMs, bool paused)
    {
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

        _pendingMedia = (path, resumeAt, !wasPlaying);
        await EnsurePlayerAsync();
        if (_mpv is not null)
            _viewModel.StatusText = $"Player reloaded at {_viewModel.PositionText}.";
        _reloadingPlayer = false;
    }

    // ----- Live Preview -----

    /// <summary>A setting changed: the graph is rebuilt shortly, once the changes have stopped coming.</summary>
    private void ScheduleLiveFilter()
    {
        if (_mpv is null || !_viewModel.LivePreview)
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

        // Built for the player as large as it is on screen, in real pixels.
        var dpi = VisualTreeHelper.GetDpi(this);

        // While a rectangle is being drawn on the video (the crop, or a layer's place in the source) the
        // source itself is shown: what is drawn is a part of it, not of the finished frame.
        var graph = _viewModel.LivePreview && CurrentOverlayMode == OverlayMode.None
            ? _viewModel.BuildLiveFilterGraph(VideoView.ActualWidth * dpi.DpiScaleX, VideoView.ActualHeight * dpi.DpiScaleY)
            : "";
        if (graph == _liveGraph || (graph.Length > 0 && graph == _failedLiveGraph))
            return;

        // Filters work on frames in main memory, so decoding hands them over there while a graph is running.
        if (graph.Length > 0)
            player.SetFiltering(true);

        _liveGraph = graph;
        var problem = player.SetFilterGraph(graph);
        if (graph.Length == 0)
            player.SetFiltering(false);
        if (problem is not null)
            OnPlayerError($"lavfi-complex: {problem}");
    }

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

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
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
        // Don't fight the user for the thumb while they are dragging it.
        if (_isScrubbing)
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

        // Magnetism: while dragging, a thumb that comes close enough to a keyframe is pulled onto it.
        // The drag continues from wherever the thumb is, so it sticks until the pointer has moved clear.
        var settings = AppSettings.Current;
        if (_isScrubbing && !_applyingMagnet && settings.SnapTimelineToKeyframes
            && TimelineSlider.ActualWidth > 0 && _viewModel.DurationMs > 0
            && _viewModel.GetNearestKeyframeMs(e.NewValue) is { } keyframe && keyframe != e.NewValue)
        {
            const double pixelsPerStep = 4;
            var reach = Math.Clamp(settings.TimelineMagnetism, 1, 5) * pixelsPerStep / TimelineSlider.ActualWidth * _viewModel.DurationMs;
            if (Math.Abs(keyframe - e.NewValue) <= reach)
            {
                _applyingMagnet = true;
                TimelineSlider.Value = keyframe;
                _applyingMagnet = false;
                return;
            }
        }

        // While the thumb is being dragged the player jumps from keyframe to keyframe, which it can do as
        // fast as the pointer moves; the exact frame follows when the thumb is let go.
        PlayerSeek((long)e.NewValue, exact: !_isScrubbing);
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isScrubbing = false;
        PlayerSeek((long)TimelineSlider.Value);
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
            MoveClipPlayheads();

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

        if (e.PropertyName == nameof(MainViewModel.IsEditorMode)
            && !_viewModel.IsEditorMode && ReferenceEquals(SettingsTabs.SelectedItem, LayersTab))
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

    /// <summary>The same pull towards keyframes that the thumb feels while it is dragged, when that is switched on.</summary>
    private double PullToKeyframe(double positionMs)
    {
        var settings = AppSettings.Current;
        if (!settings.SnapTimelineToKeyframes || TimelineSlider.ActualWidth <= 0 || _viewModel.GetNearestKeyframeMs(positionMs) is not { } keyframe)
            return positionMs;

        const double pixelsPerStep = 4;
        var reach = Math.Clamp(settings.TimelineMagnetism, 1, 5) * pixelsPerStep / TimelineSlider.ActualWidth * _viewModel.DurationMs;
        return Math.Abs(keyframe - positionMs) <= reach ? keyframe : positionMs;
    }

    private void TimelineSlider_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.DurationMs <= 0 || !TimelineSlider.CaptureMouse())
            return;

        _rangeDragStartMs = _rangeDragEndMs = PullToKeyframe(TimelineMsAt(e.GetPosition(TimelineSlider).X));
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

        // The same way in as a typed cut, so Snap cuts to keyframes applies to it in the same way.
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

    private void PreviousKeyframe_Click(object sender, RoutedEventArgs e) => JumpToKeyframe(-1);

    private void NextKeyframe_Click(object sender, RoutedEventArgs e) => JumpToKeyframe(1);

    /// <summary>
    /// Puts the player on the keyframe before or after the playhead. The time comes from the keyframe
    /// index; setting the position moves the timeline, which seeks the player to exactly that time.
    /// </summary>
    private void JumpToKeyframe(int direction)
    {
        // Like stepping, jumping is for looking at a still picture.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        _viewModel.SeekToKeyframe(direction);
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
    private static readonly Brush LabelShadeBrush = Frozen(Color.FromArgb(0x90, 0x00, 0x00, 0x00));
    private const double ClipEdge = 9;

    /// <summary>An audio track, or one of the parts it was split into, as something that can be selected.</summary>
    private sealed record AudioClip(AudioTrack Track, AudioPiece? Piece);

    /// <summary>A video layer's own sound, as something that can be selected.</summary>
    private sealed record LayerSound(Layer Layer);

    // The bars that are on screen now (the lists make and drop rows as they scroll).
    private readonly List<Canvas> _clipTracks = [];

    // What is selected: a Layer (one clip), a CutSegment, an AudioClip, a LayerSound; null with the flag set is the main video as a whole.
    private object? _selectedClip;
    private bool _mainClipSelected;
    private bool _clipRedrawQueued;

    // The drag in progress: the block and its bar, where the pointer went down, the block as it was then, and what to do when it is let go.
    private (Border Block, Canvas Track, double PointerX, double Left, double Width, bool Resizing, Action<double, double> Commit)? _clipDrag;

    private void ClipTrack_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Canvas track && !_clipTracks.Contains(track))
            _clipTracks.Add(track);
        DrawTrack(sender as Canvas);
    }

    private void ClipTrack_Unloaded(object sender, RoutedEventArgs e) => _clipTracks.Remove((Canvas)sender);

    private void ClipTrack_Changed(object sender, SizeChangedEventArgs e) => DrawTrack(sender as Canvas);

    private void ClipTrack_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => DrawTrack(sender as Canvas);

    // A click on a bar, beside its blocks, moves the playhead to that moment.
    private void ClipTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Canvas { ActualWidth: > 0 } track && _viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1) * _viewModel.DurationMs;
    }

    /// <summary>Redraws the bars once, however many things have just changed.</summary>
    private void ScheduleClipRedraw()
    {
        if (_clipRedrawQueued || _clipDrag is not null)
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
    private void DrawTrack(Canvas? track)
    {
        if (track is null || _clipDrag is { } drag && ReferenceEquals(drag.Track, track))
            return;

        track.Children.Clear();
        var seconds = _viewModel.DurationMs / 1000;
        var isSound = track.Tag as string == "audio";
        var show = seconds > 0 && track.DataContext switch
        {
            AudioTrack => true,

            // A video layer's sound has a bar of its own only while it is unlinked from its picture; linked, it is drawn on the picture's block.
            Layer layerOf when isSound => !_viewModel.AudioLinked && layerOf is { IsVideoFile: true, HasAudio: true },
            Layer { IsMainVideo: true } or Layer { HasTiming: true } => true,
            _ => false,
        };
        track.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || track.ActualWidth <= 0)
            return;

        var (width, scale) = (track.ActualWidth, track.ActualWidth / seconds);
        var perPixel = seconds / width;
        var height = Math.Max(track.ActualHeight - 2, 4);
        switch (track.DataContext)
        {
            // An audio track: one block, or its parts, moved along with the track when it has been slipped.
            case AudioTrack audio:
                var pieces = audio.Pieces.Count > 0 ? audio.Pieces : [new AudioPiece(0, seconds, false)];
                foreach (var piece in pieces)
                {
                    var clip = new AudioClip(audio, audio.Pieces.Count > 0 ? piece : null);
                    AddClip(track, (piece.Start + audio.OffsetSeconds) * scale, (piece.End - piece.Start) * scale, piece.Muted ? "silenced" : "",
                        piece.Muted ? OffClipBrush : AudioClipBrush, clip, canDrag: () => !_viewModel.AudioLinked, resizable: false,
                        commit: (left, _) => _viewModel.SlipAudio(audio, left * perPixel - piece.Start),
                        cannotDrag: "Audio and video are linked. Unlink them (the chain button) to move this track on its own.");
                }

                // The picture of the sound moves with it.
                if (track.Parent is Grid { Children: [Image waveform, ..] })
                    waveform.Margin = new Thickness(audio.OffsetSeconds * scale, 0, -audio.OffsetSeconds * scale, 0);
                break;

            // A video layer's own sound, unlinked: each clip's starts with the clip, or wherever it was slipped to.
            case Layer sound when isSound:
                foreach (var clip in _viewModel.GetTrackClips(sound))
                {
                    var (from, to) = clip.GetSpan(seconds);
                    AddClip(track, (from + clip.AudioOffset) * scale, (to - from) * scale, "sound", clip.IsHidden ? OffClipBrush : LayerAudioBrush, new LayerSound(clip),
                        canDrag: () => !_viewModel.AudioLinked, resizable: false, wave: MediaBrush(clip.Waveform, clip, scale, height),
                        commit: (left, _) =>
                        {
                            _viewModel.Checkpoint($"move the sound of {clip.Name}");
                            clip.AudioOffset = Math.Round(left * perPixel - clip.StartTime, 2);
                        },
                        cannotDrag: "Audio and video are linked: the sound follows its layer. Unlink them (the chain button) to move it on its own.");
                }

                break;

            // The main video: its cut segments, with the waveform of its sound over them when asked for.
            case Layer { IsMainVideo: true } main:
                var mainWave = _viewModel.ShowLinkedAudio && !main.IsHidden ? _viewModel.TimelineWaveform : null;
                var segments = _viewModel.Segments.OrderBy(s => s.Start).ToList();
                if (segments.Count == 0)
                    AddClip(track, 0, width, main.IsHidden ? "The whole video (picture hidden)" : "The whole video", main.IsHidden ? OffClipBrush : MainClipBrush, null, wave: PartBrush(mainWave, 0, width, height));
                foreach (var segment in segments)
                {
                    var left = segment.Start.TotalSeconds * scale;
                    AddClip(track, left, segment.Duration.TotalSeconds * scale, segment.IsSkipped ? "skipped" : "",
                        segment.IsSkipped || main.IsHidden ? OffClipBrush : MainClipBrush, segment, wave: segment.IsSkipped ? null : PartBrush(mainWave, left, width, height));
                }

                break;

            // A track: its clips, each from when it appears to when it goes.
            case Layer layer:
                foreach (var clip in _viewModel.GetTrackClips(layer))
                {
                    var (from, to) = clip.GetSpan(seconds);
                    var blockWidth = (to - from) * scale;
                    var showsSound = clip.IsAudio || (clip is { IsVideoFile: true, HasAudio: true } && _viewModel.ShowLinkedAudio && _viewModel.AudioLinked);
                    AddClip(track, from * scale, blockWidth, clip.IsHidden ? "hidden" : clip.IsAudio ? clip.Name : "",
                        clip.IsHidden ? OffClipBrush : clip.IsAudio ? SoundClipBrush : LayerClipBrush, clip, canDrag: () => true, resizable: true,
                        frames: clip.IsHidden ? null : BuildFrames(clip, blockWidth, height, scale),
                        wave: clip.IsHidden || !showsSound ? null : MediaBrush(clip.Waveform, clip, scale, height),
                        commit: (left, nowWidth) =>
                        {
                            _viewModel.Checkpoint($"move {clip.Name}");
                            (clip.StartTime, clip.Duration) = (Math.Round(left * perPixel, 2), Math.Round(nowWidth * perPixel, 2));
                        });
                }

                break;
        }

        // The playhead, so it can be seen where a split would fall.
        var playhead = new System.Windows.Shapes.Rectangle { Width = 1.5, Height = Math.Max(track.ActualHeight, 4), Fill = Brushes.OrangeRed, IsHitTestVisible = false, Tag = "playhead" };
        Canvas.SetLeft(playhead, _viewModel.PositionMs / 1000 * scale);
        track.Children.Add(playhead);
    }

    /// <summary>
    /// A picture of a clip's whole file (the waveform of its sound) as a brush for the clip's block: laid out
    /// at the scale of the timeline and shifted, so that the block shows the part of the file the clip plays.
    /// </summary>
    private static Brush? MediaBrush(ImageSource? picture, Layer clip, double scale, double height)
    {
        if (picture is null)
            return null;

        return clip.MediaDuration > 0.05
            ? PartBrush(picture, clip.MediaOffset * scale, clip.MediaDuration * scale, height)
            : new ImageBrush(picture) { Stretch = Stretch.Fill };
    }

    /// <summary>A brush that shows one stretch of a long picture: the picture is <paramref name="span"/> pixels wide in all, and the block begins <paramref name="offset"/> pixels into it.</summary>
    private static Brush? PartBrush(ImageSource? picture, double offset, double span, double height) =>
        picture is null || span < 1 ? null : new ImageBrush(picture)
        {
            Stretch = Stretch.Fill, TileMode = TileMode.None, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(-offset, 0, span, height),
        };

    // The single frames of a filmstrip, cut from it once and kept for as long as the strip is.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, CroppedBitmap?[]> StripFrames = [];

    /// <summary>
    /// The frames a video layer's block is drawn with: as many as fit side by side at their own shape, each the
    /// frame of the layer's file that is on screen at that point of the block. The file's strip holds a fixed
    /// number of frames, spread over its length; the nearest one is used, and a video that repeats starts again.
    /// </summary>
    private static Canvas? BuildFrames(Layer clip, double blockWidth, double height, double scale)
    {
        if (!clip.IsVideoFile || clip.Filmstrip is not BitmapSource strip || strip.PixelHeight <= 0 || blockWidth < 6)
            return null;

        var shape = clip is { ImageWidth: > 0, ImageHeight: > 0 } ? (double)clip.ImageWidth / clip.ImageHeight : 16.0 / 9;
        var count = Math.Max((int)Math.Round(strip.PixelWidth / (strip.PixelHeight * shape)), 1);
        var framePixels = strip.PixelWidth / count;
        if (framePixels < 2)
            return null;

        var frames = StripFrames.GetValue(strip, _ => new CroppedBitmap?[count]);
        if (frames.Length != count)
            return null;

        var slot = height * framePixels / strip.PixelHeight;
        var canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true, Opacity = 0.9 };
        for (var k = 0; k * slot < blockWidth && k < 80; k++)
        {
            // What the layer shows in the middle of this slot.
            var into = clip.MediaOffset + (k + 0.5) * slot / scale;
            var index = clip.MediaDuration > 0.05 ? Math.Clamp((int)(into % clip.MediaDuration / clip.MediaDuration * count), 0, count - 1) : 0;
            var frame = frames[index] ??= Cut(strip, index * framePixels, framePixels);
            var picture = new Image { Source = frame, Width = slot, Height = height, Stretch = Stretch.Fill };
            Canvas.SetLeft(picture, k * slot);
            canvas.Children.Add(picture);
        }

        return canvas;

        static CroppedBitmap Cut(BitmapSource strip, int x, int width)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(x, 0, width, strip.PixelHeight));
            frame.Freeze();
            return frame;
        }
    }

    /// <summary>Moves the playhead line of every bar; nothing else about them changes as the video plays.</summary>
    private void MoveClipPlayheads()
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0)
            return;

        foreach (var track in _clipTracks)
        {
            if (track.Children.Count > 0 && track.Children[^1] is System.Windows.Shapes.Rectangle { Tag: "playhead" } playhead)
                Canvas.SetLeft(playhead, _viewModel.PositionMs / 1000 * track.ActualWidth / seconds);
        }
    }

    private bool IsSelected(object? clip) => clip is null ? _mainClipSelected && _selectedClip is null : Equals(clip, _selectedClip);

    private void SelectClip(object? clip, Border block)
    {
        (_selectedClip, _mainClipSelected) = (clip, clip is null);
        foreach (var other in _clipTracks.SelectMany(t => t.Children.OfType<Border>()))
            other.BorderBrush = ReferenceEquals(other, block) ? Brushes.White : Brushes.Transparent;
    }

    /// <param name="canDrag">Whether the block can be moved right now; null for one that never can (a cut segment).</param>
    /// <param name="commit">What letting go of a moved block does, given its new left edge and width in pixels.</param>
    /// <param name="frames">Frames of the block's video, drawn inside it.</param>
    /// <param name="wave">A picture of the block's sound, drawn inside it over the frames.</param>
    private void AddClip(
        Canvas track, double left, double width, string name, Brush fill, object? clip,
        Func<bool>? canDrag = null, bool resizable = false, Action<double, double>? commit = null, UIElement? frames = null, Brush? wave = null, string cannotDrag = "")
    {
        var content = new Grid { IsHitTestVisible = false };
        if (frames is not null)
            content.Children.Add(frames);
        if (wave is not null)
            content.Children.Add(new System.Windows.Shapes.Rectangle { Fill = wave, Opacity = frames is null ? 0.6 : 0.85 });
        if (name.Length > 0)
        {
            // Over frames, the words get a shade of their own to be read against.
            content.Children.Add(new Border
            {
                Background = frames is null ? null : LabelShadeBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(2), Margin = new Thickness(3, 0, 3, 0),
                Child = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 10.5, Margin = new Thickness(2, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis },
            });
        }

        var block = new Border
        {
            Width = Math.Max(width, 3),
            Height = Math.Max(track.ActualHeight - 2, 4),
            Background = fill,
            CornerRadius = new CornerRadius(3),
            BorderBrush = IsSelected(clip) ? Brushes.White : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Cursor = canDrag is null ? Cursors.Hand : Cursors.SizeAll,
            Child = content,
            ClipToBounds = true,
            Tag = "clip",
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, 1);

        // A right-click selects the block as well, so that the menu that opens is about it.
        block.MouseRightButtonDown += (_, _) => SelectClip(clip, block);
        block.MouseLeftButtonDown += (_, e) =>
        {
            SelectClip(clip, block);

            // On an audio track a click is also what moves the playhead, as it was before the track had blocks.
            if (clip is AudioClip && track.ActualWidth > 0)
                _viewModel.PositionMs = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1) * _viewModel.DurationMs;

            if (canDrag is not null && commit is not null)
            {
                if (!canDrag())
                    _viewModel.StatusText = cannotDrag;
                else if (block.CaptureMouse())
                    _clipDrag = (block, track, e.GetPosition(track).X, Canvas.GetLeft(block), block.Width, resizable && e.GetPosition(block).X > block.Width - ClipEdge, commit);
            }

            e.Handled = true;
        };
        block.MouseMove += (_, e) =>
        {
            if (_clipDrag is not { } drag || !ReferenceEquals(drag.Block, block))
            {
                // The right edge is the handle for the length.
                if (resizable)
                    block.Cursor = e.GetPosition(block).X > block.Width - ClipEdge ? Cursors.SizeWE : Cursors.SizeAll;
                return;
            }

            var moved = e.GetPosition(track).X - drag.PointerX;
            if (drag.Resizing)
                block.Width = Math.Clamp(drag.Width + moved, 4, track.ActualWidth - drag.Left);
            else
                Canvas.SetLeft(block, clip is AudioClip or LayerSound ? drag.Left + moved : Math.Clamp(drag.Left + moved, 0, Math.Max(track.ActualWidth - drag.Width, 0)));
        };
        block.MouseLeftButtonUp += (_, _) =>
        {
            if (_clipDrag is not { } drag || !ReferenceEquals(drag.Block, block))
                return;

            _clipDrag = null;
            block.ReleaseMouseCapture();

            // A click that did not move the block changes nothing.
            var (nowLeft, nowWidth) = (Canvas.GetLeft(block), block.Width);
            if (Math.Abs(nowLeft - drag.Left) > 0.5 || Math.Abs(nowWidth - drag.Width) > 0.5)
                drag.Commit(nowLeft, nowWidth);

            ScheduleClipRedraw();
        };
        track.Children.Add(block);
    }

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
            default:
                _viewModel.SplitSegment(_selectedClip as CutSegment, at);
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
        if (_viewModel.TrimClip(_selectedClip is LayerSound sound ? sound.Layer : _selectedClip, start, _viewModel.PositionMs / 1000) && _selectedClip is CutSegment)
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
            Add("Split at Playhead", () => _viewModel.SplitSegment(null, at));
            Add("Reset Position and Zoom", () => _viewModel.ResetCenterVideoCommand.Execute(null));
        }

        if (layer.HasTiming)
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

    // The width the pane opens at. Dragging the splitter beside it changes it from there.
    private const double LayoutPaneWidth = 340;

    /// <summary>Opens or closes the pane beside the player, following Edit Layout.</summary>
    private void UpdateLayoutPane()
    {
        var open = _viewModel.IsArrangeActive;
        LayoutPane.Visibility = LayoutSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        // Closed, the column gives its width back to the video; opened, it starts from its usual width
        // again, or less when the window is too narrow to spare that much.
        var available = ((Grid)LayoutPane.Parent).ActualWidth;
        LayoutColumn.Width = new GridLength(open ? Math.Min(LayoutPaneWidth, Math.Max(available - 530, 160)) : 0);

        if (open)
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
        if (sender is not FrameworkElement { DataContext: Models.AudioTrack track })
            return;

        var dialog = new TrackAudioFiltersDialog(track) { Owner = this };
        if (dialog.ShowDialog() == true)
            track.Filters = dialog.Filters;
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

    // ----- Automation: smart rules -----
    // The rules are saved as they are edited; the tab has no Save button.

    private void AddFolderRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Videos in this folder get the preset" };
        if (dialog.ShowDialog(this) == true)
            SelectRule(_viewModel.AddSmartRule(SmartRule.Folder, dialog.FolderName), edit: false);
    }

    // Added with a word to replace, already in edit so it can be typed over at once.
    private void AddKeywordRule_Click(object sender, RoutedEventArgs e) => SelectRule(_viewModel.AddSmartRule(SmartRule.Keyword, "keyword"), edit: true);

    private void AddExtensionRule_Click(object sender, RoutedEventArgs e) => SelectRule(_viewModel.AddSmartRule(SmartRule.Extension, "*.mkv"), edit: true);

    private void SelectRule(SmartRule? rule, bool edit)
    {
        if (rule is null)
            return;

        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
        if (edit)
        {
            RulesGrid.CurrentCell = new DataGridCellInfo(rule, RulesGrid.Columns[1]);
            RulesGrid.Focus();
            RulesGrid.BeginEdit();
        }
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is SmartRule rule)
            _viewModel.RemoveSmartRule(rule);
    }

    // The edit reaches the rule once this event has returned, so the save waits for that.
    private void RulesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.BeginInvoke(_viewModel.SaveAutomation, System.Windows.Threading.DispatcherPriority.Background);
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
            _rangeDragEndMs = PullToKeyframe(TimelineMsAt(e.GetPosition(TimelineSlider).X));
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
