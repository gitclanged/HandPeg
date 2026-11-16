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

/// <summary>Host services the Fullscreen Preview needs from the player's owner window.</summary>
/// <param name="Owner">Main window (keys, activation, closing, Frame 1).</param>
/// <param name="ViewModel">Its view model (playhead, transport text).</param>
/// <param name="Video">Player surface, re-parented into the preview window and back.</param>
/// <param name="VideoPane">Pane decorator hosting the surface (VideoBorder).</param>
/// <param name="Hud">HUD layer over the player (PlayerHud: never zoomed with the picture or hidden by 3D view).</param>
/// <param name="ExplodedToggle">3D view toggle (two-way synced with the bar button).</param>
/// <param name="CanToggleExploded">Whether the 3D view can toggle now.</param>
/// <param name="ScrubHeld">Whether a scrub is held (keeps the bar visible).</param>
/// <param name="SwitchingTab">Whether a project tab switch is in progress (transient player hide).</param>
/// <param name="HookKeys">Routes editor keys (Esc, F11, Space, 3D view keys) to a side window.</param>
/// <param name="UnhookKeys">Removes that routing.</param>
/// <param name="PlayPause">Play/pause click handler.</param>
/// <param name="ScrubTo">Scrub step to a playhead time (ms).</param>
/// <param name="ScrubEnded">Scrub release at a time (ms).</param>
/// <param name="RunGuarded">Runs a command with faults reported to status bar and log, never crashing.</param>
internal sealed record FullscreenHost(
    Window Owner, MainViewModel ViewModel, VideoSurface Video, Decorator VideoPane, Grid Hud, ToggleButton ExplodedToggle,
    Func<bool> CanToggleExploded, Func<bool> ScrubHeld, Func<bool> SwitchingTab, Action<Window> HookKeys, Action<Window> UnhookKeys,
    RoutedEventHandler PlayPause, Action<double> ScrubTo, Action<double> ScrubEnded, Action<string, Action> RunGuarded);

/// <summary>
/// Player HUD and Fullscreen Preview: a Fullscreen button in the HUD layer that appears on pointer motion and hides at rest, and a
/// borderless monitor-covering window (<see cref="PlayerFullscreenWindow"/>) hosting the player surface with an auto-hiding transport bar.
/// </summary>
/// <remarks>
/// Docking is untouched: the pane keeps its slot with a placeholder and the surface returns exactly as it left. F11 toggles; Esc, Exit,
/// window close, player hide and display changes exit. Nothing is built before Frame 1: hooks at first idle after it, the rest on first
/// use. UI thread only.
/// </remarks>
internal sealed class FullscreenController
{
    private static readonly SolidColorBrush NoteInk = UiBrushes.Frozen(0x90, 0xFF, 0xFF, 0xFF);

    // Alpha-1 guard strip: hit-testable but visually invisible.
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

    // Last pointer position and time over the player (WPF's synthetic post-layout moves repeat the same point).
    private Point _hudPointer = new(double.NaN, double.NaN);
    private long _hudMovedAt;
    private bool _hudHooked, _hudShown, _fullscreenTimeHooked;

    /// <summary>Hooks only the owner's Frame 1 (Law 1); HUD hooks attach at the first idle after it.</summary>
    public FullscreenController(FullscreenHost host)
    {
        _host = host;
        host.Owner.ContentRendered += Owner_ContentRendered;
    }

    /// <summary>Whether the Fullscreen Preview is open.</summary>
    public bool IsOpen => _fullscreen is not null;

    /// <summary>Toggles the Fullscreen Preview (F11, player menu).</summary>
    public void Toggle()
    {
        if (_fullscreen is not null)
            Exit();
        else
            Enter();
    }

    /// <summary>Exits the Fullscreen Preview: restores the surface to its pane and closes (does not cache) the window.</summary>
    public void Exit() => Leave(activateOwner: true);

    private void Owner_ContentRendered(object? sender, EventArgs e)
    {
        _host.Owner.ContentRendered -= Owner_ContentRendered;
        _host.Owner.Dispatcher.BeginInvoke(HookPlayerHud, DispatcherPriority.ApplicationIdle);
    }

    // Also called on first entry if F11 arrives before that idle.
    private void HookPlayerHud()
    {
        if (_hudHooked)
            return;

        _hudHooked = true;
        _host.Hud.PreviewMouseMove += PlayerHud_PreviewMouseMove;
        _host.Hud.MouseLeave += PlayerHud_MouseLeave;

        // Subscribed after the startup handlers so e.Cancel reflects them (a cancelled close keeps the preview open).
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

        // Monitor containing most of the player, whichever window hosts it.
        var corner = video.PointToScreen(new Point(0, 0));
        var far = video.PointToScreen(new Point(video.ActualWidth, video.ActualHeight));
        var fullscreen = new PlayerFullscreenWindow(PlayerFullscreenWindow.MonitorAround(new Rect(corner, far))) { Owner = owner };
        fullscreen.ExitAsked += () => owner.Dispatcher.BeginInvoke(Exit);
        fullscreen.DisplayChanged += Exit;
        fullscreen.Show();
        fullscreen.Activate();
        _fullscreen = fullscreen;

        // Move the surface into the window (rendering continues at the new size); the pane shows a placeholder.
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

        // Route editor keys (Esc, F11, Space, 3D view) and mouse buttons.
        _host.HookKeys(fullscreen);
        video.IsVisibleChanged += FullscreenVideo_IsVisibleChanged;
        fullscreen.IsVisibleChanged += FullscreenVideo_IsVisibleChanged;
        owner.Activated += FullscreenOwner_Activated;

        // Suppress the dock hover tab (top-left) so no dock drag can start from fullscreen.
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

        // Hide the Fullscreen button; flash the bar briefly for discoverability.
        HideHud(instant: true);
        _hudMovedAt = Environment.TickCount64;
        ShowHud();
    }

    // A closing main window is not re-activated.
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

        // Close only after the surface is re-seated in its pane (its video overlay window re-owned).
        fullscreen.LetClose();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, fullscreen.Close);
    }

    // Exit when the player is persistently hidden (UpdateVideoVisibility: nothing loaded, engine stopped). Tab switches hide it
    // transiently (recheck after the switch); minimizing (owned preview minimizes too) is ignored and rechecked on restore.
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

    // When the main window activates on the preview's monitor (dialog closed, Alt+Tab), re-activate the preview so the taskbar stays
    // hidden and keys stay routed to it.
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

    // Pointer move over the player shows the HUD and keeps it up (one timestamp and compare per move).
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

    // Leaving the docked player hides its button immediately (fullscreen bar waits for the rest timeout).
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

        // Leave an in-progress or completed fade alone unless an immediate hide is requested.
        if (_fullscreenButton is { } button && (instant || button.IsHitTestVisible))
            HudChrome.Fade(button, shown: false, instant);
        if (_fullscreenBar is { } bar && (instant || bar.IsHitTestVisible))
            HudChrome.Fade(bar, shown: false, instant);
    }

    // Rest timeout elapsed: hide the HUD unless hovered or a control is held (then recheck shortly).
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

        // Scrubs like the rulers (MainWindow.Scrub.cs): drag updates lines and player immediately, the playhead once per frame.
        bar.SoughtTo += ms =>
        {
            if (Math.Abs(ms - viewModel.PositionMs) >= 1)
                viewModel.PositionMs = ms;
        };
        bar.ScrubbedTo += ms => _host.ScrubTo(ms);
        bar.ScrubEnded += ms => _host.ScrubEnded(ms);

        // Time readout tracks the playhead only while visible.
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
