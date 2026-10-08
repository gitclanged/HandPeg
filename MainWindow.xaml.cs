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
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace HandPegApp;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    // One LibVLC for as long as it behaves; Reload Player replaces it. See ReloadPlayer_Click.
    private LibVLC _libVlc;

    // Replaced with a fresh one for every source that is loaded; see PlayMedia.
    private MediaPlayer _mediaPlayer;

    // Set by Reload Player: the new player comes up paused, as the old one was.
    private bool _pauseWhenStarted;

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

        // One LibVLC for the life of the application, as the library intends; players come and go on top of it.
        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = CreatePlayer();

        _viewModel.Segments.CollectionChanged += (_, _) => RedrawSegments();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.MediaLoaded += PlayMedia;
        _viewModel.PreviewRendered += ShowPreview;
        _viewModel.AskOverwrite = AskOverwrite;

        // Elements added or removed while they are being arranged change what is on the canvas.
        _viewModel.UiElements.CollectionChanged += (_, _) => RedrawLayout();

        // The choices of the rule columns on the Automation tab.
        RuleTypeColumn.ItemsSource = SmartRule.Types;
        PresetColumn.ItemsSource = _viewModel.PresetNames;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closed += MainWindow_Closed;
        Loaded += (_, _) => _ = CheckForHandPegUpdateAsync();
        Loaded += (_, _) => OpenLaunchRequest();
    }

    /// <summary>What was chosen in the launch window, if it was shown: a video, a video with a preset, or a project.</summary>
    private void OpenLaunchRequest()
    {
        if (App.LaunchRequest is not { } request)
            return;

        App.LaunchRequest = null;
        switch (request.Kind)
        {
            case LaunchKind.Video when request.PresetName is { } preset:
                _ = _viewModel.LoadFileWithPresetAsync(request.Path, preset, request.Parts);
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
    /// Makes a player wired to the window. LibVLC raises events on its own threads, so every handler
    /// marshals with BeginInvoke (a blocking Invoke can deadlock against player calls made from the UI
    /// thread) and ignores events from a player that has since been replaced.
    /// </summary>
    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer(_libVlc);

        void OnUi(Action action) => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(player, _mediaPlayer))
                action();
        });

        player.LengthChanged += (_, e) => OnUi(() => _viewModel.DurationMs = e.Length);
        player.TimeChanged += (_, e) => OnUi(() => OnPlayerTimeChanged(e.Time));
        player.Playing += (_, _) => OnUi(() =>
        {
            _viewModel.IsPlaying = true;

            // The audio output only exists once playback has started, so volume and speed are (re)applied here.
            player.Volume = _viewModel.Volume;
            player.SetRate(_viewModel.PlaybackRate);

            // And so is the choice of track, which a new player does not know about.
            if (_viewModel.SoloTrack is not null)
                ApplySoloTrack();

            if (_pauseWhenStarted)
            {
                _pauseWhenStarted = false;
                player.SetPause(true);
            }
        });
        player.Paused += (_, _) => OnUi(() => _viewModel.IsPlaying = false);
        player.Stopped += (_, _) => OnUi(() => _viewModel.IsPlaying = false);
        player.EndReached += (_, _) => OnUi(() => _viewModel.IsPlaying = false);
        player.EncounteredError += (_, _) => OnUi(() => _viewModel.StatusText = "The player could not open this source.");
        return player;
    }

    private void VideoView_Loaded(object sender, RoutedEventArgs e) => VideoView.MediaPlayer = _mediaPlayer;

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // Encodes, downloads and probes still running go first, children included, so nothing is left
        // behind in the background and nothing still holds a file in the session folder.
        ProcessPipes.KillAll();
        _viewModel.Shutdown();
        _mediaPlayer.Stop();
        VideoView.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();

        // The most recent downloads stay, so the same URL loads without downloading again.
        YtDlpDownloader.TrimDownloads(AppSettings.Current.DownloadCacheSize);
    }

    // ----- Hotkeys -----

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        // Typing stays typing, and a drop-down keeps its own keys.
        var focused = Keyboard.FocusedElement;
        if (focused is TextBoxBase or ComboBox or ComboBoxItem)
            return;

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

    /// <summary>Remove Dead Air: first how quiet counts as silence, then the search.</summary>
    private void RemoveDeadAir_Click(object sender, RoutedEventArgs e)
    {
        if (new DeadAirDialog { Owner = this }.ShowDialog() == true && _viewModel.RemoveDeadAirCommand.CanExecute(null))
            _viewModel.RemoveDeadAirCommand.Execute(null);
    }

    /// <summary>The Video Combinator: two videos joined into one, saved to a file or sent straight to the editor.</summary>
    private void OpenCombinator_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CombinatorDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SendToEditorPath is { } combined)
            LoadDroppedFile(combined);
    }

    private void DropHint_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => BrowseSource_Click(sender, e);

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

        // The dialog must not open inside the drop itself: Explorer waits, frozen, until the drop handler returns.
        Dispatcher.BeginInvoke(() => LoadDroppedFile(file));
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
            _viewModel.OnSettingsSaved();
    }

    private void SourceTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _viewModel.LoadSourceCommand.Execute(null);
    }

    /// <summary>
    /// Plays a newly loaded source on a fresh player. A player keeps decoder and output state for what it
    /// has played, so reusing one across many files lets memory pile up; the old one is released instead.
    /// </summary>
    private void PlayMedia(string path)
    {
        if (!File.Exists(path))
        {
            _viewModel.StatusText = $"The file to play is missing: {path}";
            return;
        }

        // The old player is stopped while it still owns the video surface. Taking the surface away from
        // a player that is still playing makes LibVLC open a window of its own to carry on in.
        var retired = _mediaPlayer;
        retired.Stop();

        _pauseWhenStarted = false;
        _mediaPlayer = CreatePlayer();
        VideoView.MediaPlayer = _mediaPlayer;
        StartPlayback(path, attemptsLeft: 20);

        // Releasing the old player can wait on LibVLC's own threads, so it happens away from the UI thread.
        Task.Run(retired.Dispose);
    }

    private bool _reloadingPlayer;

    /// <summary>
    /// Reload Player: for when LibVLC has locked up and the timeline no longer responds. The player and the
    /// LibVLC instance under it are replaced with new ones, and the video is opened again where it was.
    /// The old pair is stopped and released away from the UI thread, and is not waited for beyond a moment:
    /// a player that is truly stuck would otherwise take the window down with it.
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

        // The new pair first: from here on, events from the old player are ignored (see CreatePlayer).
        var (retiredPlayer, retiredVlc) = (_mediaPlayer, _libVlc);
        _libVlc = new LibVLC();
        _mediaPlayer = CreatePlayer();
        _isScrubbing = false;

        // The old player is stopped while it still owns the video surface (see PlayMedia), but off this thread.
        var stopping = Task.Run(retiredPlayer.Stop);
        var stopped = await Task.WhenAny(stopping, Task.Delay(2500)) == stopping;
        _ = stopping.ContinueWith(_ =>
        {
            retiredPlayer.Dispose();
            retiredVlc.Dispose();
        }, TaskScheduler.Default);

        _viewModel.IsPlaying = false;
        _pauseWhenStarted = !wasPlaying;
        VideoView.MediaPlayer = _mediaPlayer;
        StartPlayback(path, attemptsLeft: 20, startMs: resumeAt);

        _viewModel.StatusText = stopped
            ? $"Player reloaded at {_viewModel.PositionText}."
            : $"Player reloaded at {_viewModel.PositionText}. The old player did not answer and was left behind.";
        _reloadingPlayer = false;
    }

    /// <summary>
    /// Starts playing only once the player has the window's video surface. Without one, LibVLC would
    /// create its own top-level Direct3D window; so if the surface is not attached yet, this waits for it.
    /// </summary>
    /// <param name="startMs">Where to start playing from, for a reload; 0 for the beginning.</param>
    private void StartPlayback(string path, int attemptsLeft, long startMs = 0)
    {
        var player = _mediaPlayer;
        if (player.Hwnd == IntPtr.Zero)
        {
            // Assigning again is what attaches the surface, once the video control has created it.
            VideoView.MediaPlayer = null;
            VideoView.MediaPlayer = player;
        }

        if (player.Hwnd == IntPtr.Zero)
        {
            if (attemptsLeft <= 0)
            {
                _viewModel.StatusText = "The video surface is not ready, so playback was not started.";
                return;
            }

            // The surface appears with the control's first layout; look again after the next one.
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(player, _mediaPlayer))
                    StartPlayback(path, attemptsLeft - 1, startMs);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        // The player keeps its own reference to the media, so this one can go as soon as playback has started.
        using var media = new Media(_libVlc, path, FromType.FromPath);
        if (startMs > 0)
            media.AddOption(string.Create(System.Globalization.CultureInfo.InvariantCulture, $":start-time={startMs / 1000.0:0.###}"));
        player.Play(media);
    }

    // ----- One audio track on its own -----

    /// <summary>
    /// Has the player play the track chosen on the Audio tab, or the first one when none is. LibVLC numbers
    /// the tracks its own way; they are listed in the order of the file, after an entry for "no audio".
    /// </summary>
    private void ApplySoloTrack()
    {
        var tracks = _mediaPlayer.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => t.Id).ToList();
        var index = _viewModel.SoloTrack?.Index ?? 0;
        if (index < tracks.Count && _mediaPlayer.AudioTrack != tracks[index])
            _mediaPlayer.SetAudioTrack(tracks[index]);
    }

    private void OnSoloTrackChanged()
    {
        if (_mediaPlayer.Media is null)
            return;

        ApplySoloTrack();

        // The button is a play button: choosing a track starts it, and letting go of it pauses.
        if (_viewModel.SoloTrack is not null && !_mediaPlayer.IsPlaying)
            PlayPause_Click(this, new RoutedEventArgs());
        else if (_viewModel.SoloTrack is null && _mediaPlayer.IsPlaying)
            _mediaPlayer.SetPause(true);
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
        if (_mediaPlayer.Media is null)
        {
            _viewModel.LoadSourceCommand.Execute(null);
        }
        else if (_mediaPlayer.State == VLCState.Ended)
        {
            // An ended player has to be stopped before it will play again.
            _mediaPlayer.Stop();
            _mediaPlayer.Play();
        }
        else if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
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
        if (!_mediaPlayer.IsPlaying)
            return;

        var next = _viewModel.GetSegmentSkipTarget(timeMs, out var isPastLast);
        if (next is { } target)
        {
            _mediaPlayer.Time = (long)target;
        }
        else if (isPastLast)
        {
            _mediaPlayer.SetPause(true);
            _viewModel.StatusText = "End of the last cut segment.";
        }
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromPlayer || !_mediaPlayer.IsSeekable)
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

        _mediaPlayer.Time = (long)e.NewValue;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e) => _isScrubbing = false;

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

        if (e.PropertyName == nameof(MainViewModel.HasSource))
            DropHint.Visibility = _viewModel.HasSource ? Visibility.Collapsed : Visibility.Visible;

        if (e.PropertyName is nameof(MainViewModel.ShowTimelineThumbnails) or nameof(MainViewModel.DurationMs))
            RebuildFilmstrip();

        if (e.PropertyName == nameof(MainViewModel.Volume))
            _mediaPlayer.Volume = _viewModel.Volume;

        if (e.PropertyName == nameof(MainViewModel.PlaybackSpeed))
            _mediaPlayer.SetRate(_viewModel.PlaybackRate);

        if (e.PropertyName is nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetElement))
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

        if (e.PropertyName == nameof(MainViewModel.ShowAdvancedFiltersTab)
            && !_viewModel.ShowAdvancedFiltersTab && ReferenceEquals(SettingsTabs.SelectedItem, AdvancedTab))
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
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.SetPause(true);

        _viewModel.SeekToKeyframe(direction);
    }

    private void StepFrames(int frames)
    {
        // Stepping only makes sense on a still picture.
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.SetPause(true);

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

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an image to place on the frame", Filter = "Images|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.AddImageElement(dialog.FileName);
    }

    // ----- Drawing on the video -----
    // One canvas lies over the video and serves three jobs, one at a time:
    //   Crop        drag a rectangle to set the Top/Bottom/Left/Right crop;
    //   DrawTarget  drag a rectangle to mark where an element is in the source.
    // Arranging the layers on the output frame has a pane of its own beside the player; see further down.

    private enum OverlayMode
    {
        None,
        Crop,
        DrawTarget,
        Arrange,
    }

    private readonly List<UIElement> _arrangeVisuals = [];
    private readonly List<(OverlayRegion Element, PropertyChangedEventHandler Handler)> _arrangeSubscriptions = [];
    private readonly List<Adorner> _arrangeAdorners = [];

    // Puts the centre video's box where the view model says it is; set while Arrange is showing.
    private Action? _placeCenter;
    private Point? _dragStart;

    private OverlayMode CurrentOverlayMode =>
        _viewModel.IsInteractiveCropActive ? OverlayMode.Crop
        : _viewModel.DrawTargetElement is not null ? OverlayMode.DrawTarget
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
            (_viewModel.IsInteractiveCropActive, _viewModel.DrawTargetElement) = (false, null);
            return;
        }

        CropCanvas.Visibility = mode == OverlayMode.None ? Visibility.Collapsed : Visibility.Visible;


        // Red for the crop, gold for a UI element, so it is clear what is being drawn.
        var colour = mode == OverlayMode.DrawTarget ? Colors.Gold : Colors.Red;
        CropRectangle.Stroke = new SolidColorBrush(colour);
        CropRectangle.Fill = new SolidColorBrush(Color.FromArgb(0x18, colour.R, colour.G, colour.B));

        _viewModel.StatusText = mode switch
        {
            OverlayMode.Crop => "Drag on the video to draw the crop. Double-click inside the rectangle to centre it.",
            OverlayMode.DrawTarget => $"Drag on the video to mark where {_viewModel.DrawTargetElement!.Name} is.",

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

            case OverlayMode.DrawTarget when _viewModel.DrawTargetElement is { } element:
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

        if (_viewModel.DrawTargetElement is { } element)
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

        // Marking an element is a one-shot job: once the rectangle is drawn, the canvas goes away again.
        if (wasDragging && _viewModel.DrawTargetElement is { } element)
        {
            _viewModel.DrawTargetElement = null;
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
    /// for each layer: the sharp centre video and the elements when the engine is on, and the caption box
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

        // The sharp video first, so that it lies under the elements as it does in the output.
        if (_viewModel.FrameEngine)
        {
            _placeCenter = AddArrangeLayer(
                "Center Video", (Style)FindResource("CenterThumb"), dataContext: null, frame, scale,
                _viewModel.GetCenterRect,
                _viewModel.MoveCenter,
                (deltaX, _) => _viewModel.ResizeCenter(deltaX),
                reset: () => _viewModel.ResetCenterVideoCommand.Execute(null));
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

        foreach (var element in _viewModel.FrameEngine ? _viewModel.UiElements.ToList() : [])
        {
            var place = AddArrangeLayer(
                element.Name, (Style)FindResource("ElementThumb"), element, frame, scale,
                () => element.GetOutputRect(frameWidth, frameHeight, _viewModel.SourceWidth, _viewModel.SourceHeight),
                (deltaX, deltaY) =>
                {
                    // Output pixels to fractions of the frame. Setting them moves the sliders, and the box follows them.
                    element.PositionX = Math.Clamp(element.PositionX + deltaX / frameWidth, 0, 1);
                    element.PositionY = Math.Clamp(element.PositionY + deltaY / frameHeight, 0, 1);
                },
                (deltaX, deltaY) =>
                {
                    element.SizeWidth = Math.Clamp(element.SizeWidth + deltaX / frameWidth, 0.02, 2);

                    // Locked, the height follows the width by itself; unlocked, it is dragged separately.
                    if (!element.LockAspectRatio)
                        element.SizeHeight = Math.Clamp(element.SizeHeight + deltaY / frameHeight, 0.01, 2);
                },
                reset: () =>
                {
                    // An element keeps its size; it goes to the middle of the frame.
                    var (_, _, width, height) = element.GetOutputRect(frameWidth, frameHeight, _viewModel.SourceWidth, _viewModel.SourceHeight);
                    element.PositionX = Math.Clamp((frameWidth - width) / 2.0 / frameWidth, 0, 1);
                    element.PositionY = Math.Clamp((frameHeight - height) / 2.0 / frameHeight, 0, 1);
                });

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
        Func<(int X, int Y, int Width, int Height)> getRect, Action<double, double> move, Action<double, double> resize, Action reset)
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
        }

        _arrangeVisuals.Add(box);
        // The caption box was made first so that its handlers exist, but it belongs on top: elements go under it.
        var captionBox = LayoutCanvas.Children.OfType<Thumb>().FirstOrDefault(t => t.DataContext is OverlayRegion { IsCaptions: true });
        if (captionBox is not null && !ReferenceEquals(dataContext, captionBox.DataContext))
            LayoutCanvas.Children.Insert(LayoutCanvas.Children.IndexOf(captionBox), box);
        else
            LayoutCanvas.Children.Add(box);
        Place();

        // The handle is an adorner on the box: it stays on the corner however the box moves or grows.
        if (AdornerLayer.GetAdornerLayer(box) is { } layer)
        {
            var adorner = new ResizeAdorner(box, (Style)FindResource("ElementGrip"), name);
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
        new ElementStyleDialog { Owner = this, DataContext = _viewModel.CaptionLayer }.ShowDialog();

    private void LiveFramePreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "Load a video first.";
            return;
        }

        // The player would only keep running behind a dialog that shows a still frame.
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.SetPause(true);

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

    private const string LayoutFileFilter = "HandPeg project settings (*.json)|*.json|All files|*.*";

    /// <summary>Where layout files are kept unless the user picks somewhere else. Made on first use, so the dialog can open in it.</summary>
    private static string LayoutsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Layouts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The dialog then opens wherever Windows last had it.
        }

        return AppPaths.Layouts;
    }

    private void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        // First which parts, then where to.
        var choice = new LayoutImportDialog(_viewModel.CaptureProjectSettings(), fileName: null) { Owner = this };
        if (choice.ShowDialog() != true)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Project Settings", Filter = LayoutFileFilter, FileName = "HandPeg project settings.json", DefaultExt = "json",
            InitialDirectory = LayoutsFolder(),
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportLayout(dialog.FileName, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    private void ImportLayout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import Project Settings", Filter = LayoutFileFilter, InitialDirectory = LayoutsFolder() };
        if (dialog.ShowDialog(this) != true || _viewModel.ReadLayout(dialog.FileName) is not { } layout)
            return;

        // Which parts of the file to take.
        var choice = new LayoutImportDialog(layout, Path.GetFileName(dialog.FileName)) { Owner = this };
        if (choice.ShowDialog() == true)
            _viewModel.ApplyLayout(layout, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    // ----- Frame & Layer Engine dialogs -----

    private void BlurSettings_Click(object sender, RoutedEventArgs e) =>
        new BlurSettingsDialog { Owner = this, DataContext = _viewModel }.ShowDialog();

    private void ElementStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayRegion element })
            new ElementStyleDialog { Owner = this, DataContext = element }.ShowDialog();
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
