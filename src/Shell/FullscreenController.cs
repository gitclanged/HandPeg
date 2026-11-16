using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Controls;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>What the Fullscreen Preview needs of the window that owns the player.</summary>
/// <param name="Owner">The main window (its keys, activation, closing and first frame).</param>
/// <param name="ViewModel">Its view model (the playhead and the transport's texts).</param>
/// <param name="Video">The player's surface, lifted into the preview's window and put back.</param>
/// <param name="VideoPane">The pane's decorator the surface lives in (VideoBorder).</param>
/// <param name="Hud">The layer over the player that holds the HUD (PlayerHud: never zoomed with the picture nor hidden by the 3D view).</param>
/// <param name="ExplodedToggle">The 3D view's toggle (the bar's button follows it both ways).</param>
/// <param name="CanToggleExploded">Whether the 3D view can be toggled now.</param>
/// <param name="ScrubHeld">Whether a scrub is held (the bar stays while one is).</param>
/// <param name="SwitchingTab">Whether a project tab is being switched (the player hides only for a moment then).</param>
/// <param name="HookKeys">Gives a side window the editor's keys (Esc, F11, Space, the 3D view's own).</param>
/// <param name="UnhookKeys">Takes them away again.</param>
/// <param name="PlayPause">The play/pause button's click.</param>
/// <param name="ScrubTo">A scrub step to a playhead time, ms.</param>
/// <param name="ScrubEnded">The scrub let go at a time, ms.</param>
/// <param name="RunGuarded">Runs a command so a fault is a status line and a log entry, never a crash.</param>
internal sealed record FullscreenHost(
    Window Owner, MainViewModel ViewModel, VideoSurface Video, Decorator VideoPane, Grid Hud, ToggleButton ExplodedToggle,
    Func<bool> CanToggleExploded, Func<bool> ScrubHeld, Func<bool> SwitchingTab, Action<Window> HookKeys, Action<Window> UnhookKeys,
    RoutedEventHandler PlayPause, Action<double> ScrubTo, Action<double> ScrubEnded, Action<string, Action> RunGuarded);

/// <summary>
/// The player's HUD and the Fullscreen Preview. Over the player (in the HUD layer): a Fullscreen button that comes up as the pointer
/// moves over the picture and goes once it rests; and the Fullscreen Preview, the player's own surface lifted into a borderless window
/// over its monitor (<see cref="PlayerFullscreenWindow"/>) with a transport bar that comes up the same way.
/// </summary>
/// <remarks>
/// The docking is never touched: the pane keeps its place, with a note where the picture was, and the surface goes back into it exactly
/// as it left. F11 toggles; Esc, the bar's Exit, the window closing, the player hidden and a change of displays leave. Nothing is made
/// before the first frame: the hooks at the first idle moment after it, the rest at first use. The window's thread only.
/// </remarks>
internal sealed class FullscreenController
{
    private static readonly SolidColorBrush NoteInk = UiBrushes.Frozen(0x90, 0xFF, 0xFF, 0xFF);

    // Alpha 1: the guard strip takes the pointer while drawing nothing visible.
    private static readonly SolidColorBrush HitOnly = UiBrushes.Frozen(0x01, 0, 0, 0);

    private readonly FullscreenHost _host;
    private readonly TimerScope _timers = new();
    private PlayerFullscreenWindow? _fullscreen;
    private Button? _fullscreenButton;
    private FullscreenTransportBar? _fullscreenBar;
    private Border? _fullscreenGuard;
    private TextBlock? _fullscreenNote;
    private DispatcherTimer? _hudTimer;
    private Debounce? _fullscreenRecheck;

    // Where the pointer last moved over the player (a move WPF makes up after a layout change is at the same point) and when.
    private Point _hudPointer = new(double.NaN, double.NaN);
    private long _hudMovedAt;
    private bool _hudHooked, _hudShown, _fullscreenTimeHooked;

    /// <summary>Hooks only the owner's first frame (Law 1); the HUD's own hooks follow at the first idle moment after it.</summary>
    public FullscreenController(FullscreenHost host)
    {
        _host = host;
        host.Owner.ContentRendered += Owner_ContentRendered;
    }

    /// <summary>Whether the Fullscreen Preview is open.</summary>
    public bool IsOpen => _fullscreen is not null;

    /// <summary>The keybind (F11), the player menu: into the Fullscreen Preview, or out of it.</summary>
    public void Toggle()
    {
        if (_fullscreen is not null)
            Exit();
        else
            Enter();
    }

    /// <summary>Out of the Fullscreen Preview: the surface back into the pane exactly where it was, the window closed (not kept).</summary>
    public void Exit() => Leave(activateOwner: true);

    private void Owner_ContentRendered(object? sender, EventArgs e)
    {
        _host.Owner.ContentRendered -= Owner_ContentRendered;
        _host.Owner.Dispatcher.BeginInvoke(HookPlayerHud, DispatcherPriority.ApplicationIdle);
    }

    // (Also as the preview is first entered: F11 pressed before that idle moment.)
    private void HookPlayerHud()
    {
        if (_hudHooked)
            return;

        _hudHooked = true;
        _host.Hud.PreviewMouseMove += PlayerHud_PreviewMouseMove;
        _host.Hud.MouseLeave += PlayerHud_MouseLeave;

        // After every handler the window had from its start: one of them may keep it open (and the preview with it).
        _host.Owner.Closing += (_, e) =>
        {
            if (!e.Cancel)
                Leave(activateOwner: false);
        };
    }

    // ----- In and out -----

    private void Enter()
    {
        var (owner, video) = (_host.Owner, _host.Video);
        if (_fullscreen is not null)
            return;
        if (!video.IsVisible || PresentationSource.FromVisual(video) is null)
        {
            _host.ViewModel.StatusText = "Fullscreen Preview needs a picture in the player: open a video first.";
            return;
        }

        HookPlayerHud();

        // The monitor the player is on (the one with most of it), whichever window holds it.
        var corner = video.PointToScreen(new Point(0, 0));
        var far = video.PointToScreen(new Point(video.ActualWidth, video.ActualHeight));
        var fullscreen = new PlayerFullscreenWindow(PlayerFullscreenWindow.MonitorAround(new Rect(corner, far))) { Owner = owner };
        fullscreen.ExitAsked += () => owner.Dispatcher.BeginInvoke(Exit);
        fullscreen.DisplayChanged += Exit;
        fullscreen.Show();
        fullscreen.Activate();
        _fullscreen = fullscreen;

        // The surface into the window (the engine draws on, at the window's size); the pane keeps a note where it was.
        _host.VideoPane.Child = null;
        fullscreen.Host.Children.Add(video);
        _host.VideoPane.Child = _fullscreenNote ??= new TextBlock
        {
            Text = "Playing in fullscreen \u00B7 Esc",
            FontSize = 13,
            Foreground = NoteInk,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Its keys are the editor's (Esc, F11, Space, the 3D view's own), and so are its mouse buttons.
        _host.HookKeys(fullscreen);
        video.IsVisibleChanged += FullscreenVideo_IsVisibleChanged;
        fullscreen.IsVisibleChanged += FullscreenVideo_IsVisibleChanged;
        owner.Activated += FullscreenOwner_Activated;

        // The dock's hover tab on the player (its top left) is no way to start a dock drag from here.
        if (_fullscreenGuard is null)
        {
            _fullscreenGuard = new Border
            {
                Width = 220, Height = 8, Background = HitOnly, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            _host.Hud.Children.Add(_fullscreenGuard);
        }

        _fullscreenGuard.Visibility = Visibility.Visible;

        // The Fullscreen button goes; the bar comes up for a moment, so it is seen to be there.
        HideHud(instant: true);
        _hudMovedAt = Environment.TickCount64;
        ShowHud();
    }

    // (The main window closing is not brought forward.)
    private void Leave(bool activateOwner)
    {
        if (_fullscreen is not { } fullscreen)
            return;

        var (owner, video) = (_host.Owner, _host.Video);
        _fullscreen = null;
        video.IsVisibleChanged -= FullscreenVideo_IsVisibleChanged;
        owner.Activated -= FullscreenOwner_Activated;
        _fullscreenRecheck?.Cancel();
        _host.UnhookKeys(fullscreen);
        HideHud(instant: true);
        if (_fullscreenGuard is not null)
            _fullscreenGuard.Visibility = Visibility.Collapsed;

        var wasActive = fullscreen.IsActive;
        fullscreen.Host.Children.Remove(video);
        _host.VideoPane.Child = video;
        if (wasActive && activateOwner)
            owner.Activate();

        // Closed once the surface is settled in its pane again (its window over the video has gone back to its owner).
        fullscreen.LetClose();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, fullscreen.Close);
    }

    // The player hidden while fullscreen (UpdateVideoVisibility: nothing loaded any more, the engine stopped): back, once it is clear it
    // stays so. A project tab being switched hides it for a moment: looked at again once the switch is over. The window minimized (the
    // preview with it, as an owned window) is no reason: looked at again as it comes back.
    private void FullscreenVideo_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _host.Owner.Dispatcher.BeginInvoke(CheckFullscreenVideo, DispatcherPriority.ApplicationIdle);

    private void CheckFullscreenVideo()
    {
        if (_fullscreen is null || _host.Video.Visibility == Visibility.Visible)
            return;

        if (_host.SwitchingTab())
        {
            (_fullscreenRecheck ??= new Debounce(TimeSpan.FromMilliseconds(250), CheckFullscreenVideo, DispatcherPriority.Background, _timers)).Poke();
            return;
        }

        Exit();
    }

    // The main window brought forward on the monitor the preview covers (a dialog closed, Alt+Tab): the preview takes it back, so the
    // taskbar stays down and its keys stay the preview's.
    private void FullscreenOwner_Activated(object? sender, EventArgs e)
    {
        var owner = _host.Owner;
        if (_fullscreen is not { IsVisible: true } fullscreen || !fullscreen.SharesMonitorWith(owner))
            return;

        owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(_fullscreen, fullscreen) && owner.IsActive)
                fullscreen.Activate();
        });
    }

    // ----- The HUD: the Fullscreen button, the bar -----

    // A move over the player: the HUD comes up and stays while the pointer moves (a time and a compare per move, nothing else).
    private void PlayerHud_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(_host.Hud);
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
            bar.Exploded.IsEnabled = _host.CanToggleExploded();
            HudChrome.Fade(bar, shown: true);
        }
        else if (_host.Video.IsVisible)
        {
            HudChrome.Fade(_fullscreenButton ??= MakeFullscreenButton(), shown: true);
        }
        else
        {
            return;
        }

        _hudShown = true;
        _hudTimer ??= _timers.Make(TimeSpan.FromMilliseconds(HudChrome.ButtonRestMs), DispatcherPriority.Normal, HudTimer_Tick);
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
            (keep, rest) = (HudChrome.BarShown(since, bar.IsMouseOver, bar.IsHeld || _host.ScrubHeld()), HudChrome.BarRestMs);
        else
            (keep, rest) = (_fullscreenButton is { } button && _host.Hud.IsMouseOver && HudChrome.ButtonShown(since, button.IsMouseOver), HudChrome.ButtonRestMs);

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
        button.Click += (_, _) => _host.RunGuarded("Fullscreen Preview", Enter);
        _host.Hud.Children.Add(button);
        return button;
    }

    private FullscreenTransportBar MakeFullscreenBar()
    {
        var viewModel = _host.ViewModel;
        var bar = new FullscreenTransportBar(_host.Owner.TryFindResource("PlayPauseButton") as Style)
        {
            DataContext = viewModel,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        bar.PlayPause.Click += _host.PlayPause;
        bar.Exit.Click += (_, _) => _host.RunGuarded("Fullscreen Preview", Exit);
        bar.Exploded.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = _host.ExplodedToggle, Mode = BindingMode.TwoWay });

        // The scrubber as the rulers scrub (MainWindow.Scrub.cs): a drag moves the lines and the player at once, the playhead once a frame.
        bar.SoughtTo += ms =>
        {
            if (Math.Abs(ms - viewModel.PositionMs) >= 1)
                viewModel.PositionMs = ms;
        };
        bar.ScrubbedTo += ms => _host.ScrubTo(ms);
        bar.ScrubEnded += ms => _host.ScrubEnded(ms);

        // Its time follows the playhead only while it is shown.
        if (!_fullscreenTimeHooked)
        {
            _fullscreenTimeHooked = true;
            viewModel.PropertyChanged += Fullscreen_ViewModelChanged;
        }

        _host.Hud.Children.Add(bar);
        return bar;
    }

    private void Fullscreen_ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hudShown && _fullscreen is not null && _fullscreenBar is { } bar
            && e.PropertyName is nameof(MainViewModel.PositionText) or nameof(MainViewModel.DurationText))
        {
            ShowBarTime(bar);
        }
    }

    private void ShowBarTime(FullscreenTransportBar bar)
    {
        var viewModel = _host.ViewModel;
        bar.ShowTime(viewModel.PositionMs, viewModel.DurationMs, viewModel.PositionText, viewModel.DurationText);
    }
}
