using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandPegApp;

// ----- The player's HUD and the Fullscreen Preview (5.4.4) -----
// Over the player, in PlayerHud (never zoomed with the picture nor hidden by the 3D view, as PlayerOverlay's children are): a Fullscreen
// button that comes up as the pointer moves over the picture and goes once it rests; and the Fullscreen Preview itself, the player's own
// surface lifted into a borderless window over its monitor (PlayerFullscreenWindow), with a transport bar that comes up the same way. The
// docking is never touched: the pane keeps its place, with a note where the picture was, and the surface goes back into it exactly as it
// left. F11 (view.fullscreen) goes in and out; Esc, the bar's Exit, the window closing, the player hidden and a change of displays come
// back. Nothing of it is made before the first frame: the hooks at the first idle moment after it, the rest at first use.
public partial class MainWindow
{
    private PlayerFullscreenWindow? _fullscreen;
    private Button? _fullscreenButton;
    private FullscreenTransportBar? _fullscreenBar;
    private Border? _fullscreenGuard;
    private TextBlock? _fullscreenNote;
    private DispatcherTimer? _hudTimer, _fullscreenRecheck;

    // Where the pointer last moved over the player (a move WPF makes up after a layout change is at the same point) and when.
    private Point _hudPointer = new(double.NaN, double.NaN);
    private long _hudMovedAt;
    private bool _hudHooked, _hudShown, _fullscreenTimeHooked;

    // The player's HUD hooks, at the first idle moment after the first frame (Law 1).
    partial void InitializePlayerHud() => ContentRendered += PlayerHud_ContentRendered;

    private void PlayerHud_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= PlayerHud_ContentRendered;
        Dispatcher.BeginInvoke(HookPlayerHud, DispatcherPriority.ApplicationIdle);
    }

    // (Also as the preview is first entered: F11 pressed before that idle moment.)
    private void HookPlayerHud()
    {
        if (_hudHooked)
            return;

        _hudHooked = true;
        PlayerHud.PreviewMouseMove += PlayerHud_PreviewMouseMove;
        PlayerHud.MouseLeave += PlayerHud_MouseLeave;

        // After every handler the window had from its start: one of them may keep it open (and the preview with it).
        Closing += (_, e) =>
        {
            if (!e.Cancel)
                LeaveFullscreen(activateOwner: false);
        };
    }

    // ----- Esc -----

    /// <summary>What an Esc is for (5.4.4), first come first served: the Lightning Dial, the tour, a 3D drag under way, the Fullscreen Preview, Interactive Crop, the 3D view.</summary>
    internal enum EscapeTaker { None, Dial, Tour, ExplodedDrag, Fullscreen, Crop, Exploded }

    /// <summary>
    /// The one an Esc goes to (TakeOverlayKey): an open dial, then the tour, whatever has the keyboard; past them nothing while a text box, a
    /// drop-down or a flyout has it (<paramref name="focusKeepsIt"/>); then a 3D drag (let go), the Fullscreen Preview (left), Interactive
    /// Crop (ended) and the 3D view (left). The last two are answered later, after the text box's keys (EndCropOnEscape,
    /// ExplodedWindow_PreviewKeyDown), in that order.
    /// </summary>
    internal static EscapeTaker EscapeTakerFor(bool dialOpen, bool tourRunning, bool focusKeepsIt, bool explodedDrag, bool fullscreen, bool crop, bool exploded) =>
        dialOpen ? EscapeTaker.Dial
        : tourRunning ? EscapeTaker.Tour
        : focusKeepsIt ? EscapeTaker.None
        : explodedDrag ? EscapeTaker.ExplodedDrag
        : fullscreen ? EscapeTaker.Fullscreen
        : crop ? EscapeTaker.Crop
        : exploded ? EscapeTaker.Exploded
        : EscapeTaker.None;

    // ----- In and out -----

    /// <summary>The keybind (F11), the Fullscreen button: into the Fullscreen Preview, or out of it.</summary>
    private void ToggleFullscreen()
    {
        if (_fullscreen is not null)
            ExitFullscreen();
        else
            EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_fullscreen is not null)
            return;
        if (!VideoView.IsVisible || PresentationSource.FromVisual(VideoView) is null)
        {
            _viewModel.StatusText = "Fullscreen Preview needs a picture in the player: open a video first.";
            return;
        }

        HookPlayerHud();

        // The monitor the player is on (the one with most of it), whichever window holds it.
        var corner = VideoView.PointToScreen(new Point(0, 0));
        var far = VideoView.PointToScreen(new Point(VideoView.ActualWidth, VideoView.ActualHeight));
        var fullscreen = new PlayerFullscreenWindow(PlayerFullscreenWindow.MonitorAround(new Rect(corner, far))) { Owner = this };
        fullscreen.ExitAsked += () => Dispatcher.BeginInvoke(ExitFullscreen);
        fullscreen.DisplayChanged += ExitFullscreen;
        fullscreen.Show();
        fullscreen.Activate();
        _fullscreen = fullscreen;

        // The surface into the window (the engine draws on, at the window's size); the pane keeps a note where it was.
        VideoBorder.Child = null;
        fullscreen.Host.Children.Add(VideoView);
        VideoBorder.Child = _fullscreenNote ??= new TextBlock
        {
            Text = "Playing in fullscreen \u00B7 Esc",
            FontSize = 13,
            Foreground = FrozenBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Its keys are the editor's (Esc, F11, Space, the 3D view's own), and so are its mouse buttons.
        fullscreen.PreviewKeyDown += MainWindow_PreviewKeyDown;
        fullscreen.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ExplodedWindow_PreviewKeyDown));
        _hookedSides.Add(fullscreen);
        VideoView.IsVisibleChanged += FullscreenVideo_IsVisibleChanged;
        fullscreen.IsVisibleChanged += FullscreenVideo_IsVisibleChanged;
        Activated += FullscreenOwner_Activated;

        // The dock's hover tab on the player (its top left) is no way to start a dock drag from here.
        if (_fullscreenGuard is null)
        {
            _fullscreenGuard = new Border
            {
                Width = 220, Height = 8, Background = PlayerHitBrush, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            PlayerHud.Children.Add(_fullscreenGuard);
        }

        _fullscreenGuard.Visibility = Visibility.Visible;

        // The Fullscreen button goes; the bar comes up for a moment, so it is seen to be there.
        HideHud(instant: true);
        _hudMovedAt = Environment.TickCount64;
        ShowHud();
    }

    /// <summary>Out of the Fullscreen Preview: the surface back into the pane exactly where it was, the window closed (not kept).</summary>
    private void ExitFullscreen() => LeaveFullscreen(activateOwner: true);

    // (The main window closing is not brought forward.)
    private void LeaveFullscreen(bool activateOwner)
    {
        if (_fullscreen is not { } fullscreen)
            return;

        _fullscreen = null;
        VideoView.IsVisibleChanged -= FullscreenVideo_IsVisibleChanged;
        Activated -= FullscreenOwner_Activated;
        _fullscreenRecheck?.Stop();
        fullscreen.PreviewKeyDown -= MainWindow_PreviewKeyDown;
        _hookedSides.Remove(fullscreen);
        HideHud(instant: true);
        if (_fullscreenGuard is not null)
            _fullscreenGuard.Visibility = Visibility.Collapsed;

        var wasActive = fullscreen.IsActive;
        fullscreen.Host.Children.Remove(VideoView);
        VideoBorder.Child = VideoView;
        if (wasActive && activateOwner)
            Activate();

        // Closed once the surface is settled in its pane again (its window over the video has gone back to its owner).
        fullscreen.LetClose();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, fullscreen.Close);
    }

    // The player hidden while fullscreen (UpdateVideoVisibility: nothing loaded any more, the engine stopped): back, once it is clear it
    // stays so. A project tab being switched hides it for a moment: looked at again once the switch is over. The window minimized (the
    // preview with it, as an owned window) is no reason: looked at again as it comes back.
    private void FullscreenVideo_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(CheckFullscreenVideo, DispatcherPriority.ApplicationIdle);

    private void CheckFullscreenVideo()
    {
        if (_fullscreen is null || VideoView.Visibility == Visibility.Visible)
            return;

        if (_switchingTab)
        {
            if (_fullscreenRecheck is null)
            {
                _fullscreenRecheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _fullscreenRecheck.Tick += (_, _) =>
                {
                    _fullscreenRecheck.Stop();
                    CheckFullscreenVideo();
                };
            }

            _fullscreenRecheck.Start();
            return;
        }

        ExitFullscreen();
    }

    // The main window brought forward on the monitor the preview covers (a dialog closed, Alt+Tab): the preview takes it back, so the
    // taskbar stays down and its keys stay the preview's.
    private void FullscreenOwner_Activated(object? sender, EventArgs e)
    {
        if (_fullscreen is not { IsVisible: true } fullscreen || !fullscreen.SharesMonitorWith(this))
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(_fullscreen, fullscreen) && IsActive)
                fullscreen.Activate();
        });
    }

    // ----- The HUD: the Fullscreen button, the bar -----

    // A move over the player: the HUD comes up and stays while the pointer moves (a time and a compare per move, nothing else).
    private void PlayerHud_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(PlayerHud);
        if (at == _hudPointer)
            return;

        _hudPointer = at;
        _hudMovedAt = Environment.TickCount64;
        if (!_hudShown && e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            ShowHud();
    }

    // Off the docked player: its button goes at once. (Fullscreen, the bar waits for its rest.)
    private void PlayerHud_MouseLeave(object sender, MouseEventArgs e)
    {
        _hudPointer = new Point(double.NaN, double.NaN);
        if (_fullscreen is null && _hudShown)
            HideHud(instant: false);
    }

    private void ShowHud()
    {
        if (_fullscreen is not null)
        {
            var bar = _fullscreenBar ??= MakeFullscreenBar();
            ShowBarTime(bar);
            bar.Exploded.IsEnabled = CanToggleExplodedView;
            HudChrome.Fade(bar, shown: true);
        }
        else if (VideoView.IsVisible)
        {
            HudChrome.Fade(_fullscreenButton ??= MakeFullscreenButton(), shown: true);
        }
        else
        {
            return;
        }

        _hudShown = true;
        if (_hudTimer is null)
        {
            _hudTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _hudTimer.Tick += HudTimer_Tick;
        }

        _hudTimer.Interval = TimeSpan.FromMilliseconds(_fullscreen is not null ? HudChrome.BarRestMs : HudChrome.ButtonRestMs);
        _hudTimer.Start();
    }

    private void HideHud(bool instant)
    {
        _hudShown = false;
        _hudTimer?.Stop();

        // (One already going or gone is left to it, unless it is to go at once.)
        if (_fullscreenButton is { } button && (instant || button.IsHitTestVisible))
            HudChrome.Fade(button, shown: false, instant);
        if (_fullscreenBar is { } bar && (instant || bar.IsHitTestVisible))
            HudChrome.Fade(bar, shown: false, instant);
    }

    // The rest after the last move ran out: the HUD goes, unless the pointer is on it or something of it is held (looked at again shortly).
    private void HudTimer_Tick(object? sender, EventArgs e)
    {
        var since = Environment.TickCount64 - _hudMovedAt;
        bool keep;
        double rest;
        if (_fullscreen is not null && _fullscreenBar is { } bar)
            (keep, rest) = (HudChrome.BarShown(since, bar.IsMouseOver, bar.IsHeld || IsScrubHeld), HudChrome.BarRestMs);
        else
            (keep, rest) = (_fullscreenButton is { } button && PlayerHud.IsMouseOver && HudChrome.ButtonShown(since, button.IsMouseOver), HudChrome.ButtonRestMs);

        if (!keep)
        {
            HideHud(instant: false);
            return;
        }

        _hudTimer!.Interval = TimeSpan.FromMilliseconds(Math.Max(HudChrome.RecheckMs, rest - since));
    }

    private Button MakeFullscreenButton()
    {
        var button = HudChrome.HoverButton();
        button.Click += (_, _) => RunGuarded("Fullscreen Preview", EnterFullscreen);
        PlayerHud.Children.Add(button);
        return button;
    }

    private FullscreenTransportBar MakeFullscreenBar()
    {
        var bar = new FullscreenTransportBar(TryFindResource("PlayPauseButton") as Style)
        {
            DataContext = _viewModel,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        bar.PlayPause.Click += PlayPause_Click;
        bar.Exit.Click += (_, _) => RunGuarded("Fullscreen Preview", ExitFullscreen);
        bar.Exploded.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = ExplodedToggle, Mode = BindingMode.TwoWay });

        // The scrubber as the rulers scrub (MainWindow.Scrub.cs): a drag moves the lines and the player at once, the playhead once a frame.
        bar.SoughtTo += ms =>
        {
            if (Math.Abs(ms - _viewModel.PositionMs) >= 1)
                _viewModel.PositionMs = ms;
        };
        bar.ScrubbedTo += ScrubPlayheadTo;
        bar.ScrubEnded += ms =>
        {
            EndScrub();
            if (!PlayerAt((long)ms))
                PlayerSeek((long)ms);
        };

        // Its time follows the playhead only while it is shown.
        if (!_fullscreenTimeHooked)
        {
            _fullscreenTimeHooked = true;
            _viewModel.PropertyChanged += Fullscreen_ViewModelChanged;
        }

        PlayerHud.Children.Add(bar);
        return bar;
    }

    private void Fullscreen_ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hudShown && _fullscreen is not null && _fullscreenBar is { } bar
            && e.PropertyName is nameof(ViewModels.MainViewModel.PositionText) or nameof(ViewModels.MainViewModel.DurationText))
        {
            ShowBarTime(bar);
        }
    }

    private void ShowBarTime(FullscreenTransportBar bar) =>
        bar.ShowTime(_viewModel.PositionMs, _viewModel.DurationMs, _viewModel.PositionText, _viewModel.DurationText);
}
