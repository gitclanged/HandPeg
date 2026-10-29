using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

// Input (4.4): the shortcuts KeybindManager resolves and the actions they run, the held navigation keys (4.4-BugHunt: W,
// A, S and D zoom and pan the timeline, the Canvas or the player under the pointer while held, no button needed), the
// Lightning Dial, the X-Ray HUD held on Tab, and the Quick-Start Tour. Nothing here is made before the first frame: the
// dial, the HUD and the tour are made when first used, the action table at the first key, and the key map itself from
// the settings in memory.
public partial class MainWindow
{
    // ----- Setup (called from the constructor) -----

    private void InitializeKeybinds()
    {
        // Every window of the editor (this one, the side window, and the window kept over the video) is heard for the mouse
        // buttons a shortcut can use, a press that takes the keyboard from a text box, the right button held over a surface
        // while a navigation key is down, and the release of a held key.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Editor_PreviewMouseDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Editor_PreviewMouseUp), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Editor_PreviewKeyUp), handledEventsToo: true);

        // The video takes the right button, the side buttons and the wheel too (a see-through pixel of the window over it took
        // no click, so they went to the native video window and were lost): a nearly see-through fill, as the boxes have.
        PlayerOverlay.Background = PlayerHitBrush;

        // A file loaded is seen whole again, on the Canvas and in the player (mpv keeps its zoom from file to file).
        _viewModel.MediaLoaded += _ => ResetNavigationViews();
        LayoutCanvas.SizeChanged += (_, _) => ClampCanvasView();
        PlayerOverlay.SizeChanged += (_, _) =>
        {
            if (_playerZoom != 0)
            {
                _navPicture = PictureSize();
                ApplyPlayerView();
            }
        };

        // Another program is brought forward: nothing is left open over it or held.
        Application.Current.Deactivated += (_, _) => DropTransientInput();

        // The tour is offered once, by itself, on any install that has not had it (a new one after its first-run window, one
        // from before 4.4-BugHunt the first time this version starts), when the window is up, idle and free of dialogs.
        if (!AppSettings.Current.QuickStartTourDone)
            ContentRendered += OfferTourOnce;
    }

    private bool IsEditorWindow(object sender) =>
        ReferenceEquals(sender, this) || sender is Window window && (_hookedSides.Contains(window) || ReferenceEquals(window, VideoView.OverlayWindow));

    private void DropTransientInput()
    {
        CloseLightningDial();
        EndNavigation();
        HideXRay();
        _holdTimer?.Stop();
        _holdKey = Key.None;
        _capsToRestore = false;
    }

    /// <summary>
    /// Whether a drag on the timeline is held now (4.4-BugHunt): an action that changes the project waits for it to end (it is
    /// refused: its key is taken and does nothing), so the drag never commits over a document changed under it.
    /// </summary>
    private bool TimelineDragHeld => UnifiedTimeline.Surface.IsDragging;

    private bool RefusedWhileDragging(string id) =>
        TimelineDragHeld && KeybindManager.Action(id) is { } action && action.Has(KeyActionFlags.ChangesProject);

    // ----- Actions -----

    private Dictionary<string, (Func<bool> Can, Action Run)>? _keyActions;

    /// <summary>What every KeybindManager action does here, and when it can (made at the first key press).</summary>
    private Dictionary<string, (Func<bool> Can, Action Run)> KeyActions => _keyActions ??= MakeKeyActions();

    private Dictionary<string, (Func<bool> Can, Action Run)> MakeKeyActions()
    {
        Func<bool> always = static () => true, source = () => _viewModel.HasSource;
        var actions = new Dictionary<string, (Func<bool> Can, Action Run)>
        {
            ["file.save"] = (always, () => _viewModel.SaveProjectNowCommand.Execute(null)),
            ["file.saveAs"] = (always, () => _viewModel.SaveProjectAsCommand.Execute(null)),
            ["edit.undo"] = (always, () => RunUndo(redo: false)),
            ["edit.redo"] = (always, () => RunUndo(redo: true)),
            ["file.addMedia"] = (always, () => AddMedia_Click(AddMediaButton, new RoutedEventArgs())),
            ["ui.settings"] = (always, () => OpenSettings()),

            ["transport.playPause"] = (always, () => PlayPause_Click(this, new RoutedEventArgs())),
            // The transport keys of every editor: J back, K stop, L forward; J or L again goes faster.
            ["transport.shuttleBack"] = (always, () => Shuttle(-1)),
            ["transport.stop"] = (always, () => Shuttle(0)),
            ["transport.shuttleForward"] = (always, () => Shuttle(1)),
            ["transport.stepBack"] = (always, () => StepFrames(-1)),
            ["transport.stepForward"] = (always, () => StepFrames(1)),
            ["transport.start"] = (source, () => _viewModel.PositionMs = 0),
            ["transport.end"] = (source, () => _viewModel.PositionMs = _viewModel.DurationMs),
            ["transport.playInToOut"] = (source, PlayStartToStop),
            ["transport.prevIFrame"] = (source, () => JumpToIFrame(-1)),
            ["transport.nextIFrame"] = (source, () => JumpToIFrame(1)),
            ["transport.goToIn"] = (() => _viewModel.HasSource && _viewModel.PendingStartMs is not null, () => _viewModel.PositionMs = _viewModel.PendingStartMs ?? _viewModel.PositionMs),
            ["transport.prevEdit"] = (source, () => GoToEditPoint(forward: false)),
            ["transport.nextEdit"] = (source, () => GoToEditPoint(forward: true)),

            ["edit.split"] = (source, SplitSelectedClip),
            ["edit.delete"] = (always, DeleteSelectedClip),
            ["edit.rippleDelete"] = (always, RippleDeleteSelected),
            ["edit.trimStart"] = (source, () => TrimSelectedClip(start: true)),
            ["edit.trimEnd"] = (source, () => TrimSelectedClip(start: false)),
            ["clip.toggleLink"] = (source, ToggleSelectedClipLink),
            // On what is selected, as the toolbar's button and the menus do (a video layer, an audio track, else the main video).
            ["edit.deadAir"] = (source, () => RemoveDeadAir_Click(this, new RoutedEventArgs())),
            ["edit.bypassEffects"] = (source, () => _viewModel.SequenceEffectsBypassed = !_viewModel.SequenceEffectsBypassed),

            ["marker.add"] = (source, () => _viewModel.AddMarker()),
            ["marker.next"] = (source, () => _viewModel.GoToMarker(forward: true)),
            ["marker.prev"] = (source, () => _viewModel.GoToMarker(forward: false)),
            ["edit.in"] = (() => _viewModel.AddStartPointCommand.CanExecute(null), () => _viewModel.AddStartPointCommand.Execute(null)),
            ["edit.out"] = (() => _viewModel.AddStopPointCommand.CanExecute(null), () => _viewModel.AddStopPointCommand.Execute(null)),

            // The time bars: = and - zoom about the playhead, \ shows the whole timeline again.
            ["view.zoomIn"] = (source, () => ZoomAboutPlayhead(1.6)),
            ["view.zoomOut"] = (source, () => ZoomAboutPlayhead(1 / 1.6)),
            ["view.fit"] = (source, () => FitTimeline()),
            ["view.toggleSnap"] = (always, ToggleSnap),

            ["key.toggle"] = (source, () => ToggleKeyframe_Click(this, new RoutedEventArgs())),
            ["key.prev"] = (source, () => StepKeyframe(-1)),
            ["key.next"] = (source, () => StepKeyframe(1)),

            ["ui.dial"] = (always, () => OpenLightningDial(held: false, category: null, over: null)),
            ["ui.xray"] = (always, ToggleXRay),
            ["help.tour"] = (always, () => StartQuickStartTour()),
        };

        for (var i = 0; i < KeybindManager.DialCategories; i++)
        {
            var category = i;
            actions["ui.dial" + (i + 1).ToString(CultureInfo.InvariantCulture)] = (always, () => OpenLightningDial(held: false, category, over: null));
        }

        return actions;
    }

    /// <summary>Runs an action by its id (a shortcut, a mouse button, the dial): false when it cannot run now.</summary>
    private bool RunAction(string id)
    {
        if (!KeyActions.TryGetValue(id, out var action) || !action.Can() || RefusedWhileDragging(id))
            return false;

        // A fault in what a key or the dial runs is a status line and a note in the log, never the end of the app.
        DevFlightRecorder.RecordInput("action", id);
        RunGuarded(id, action.Run);
        return true;
    }

    private bool CanRunAction(string id) => KeyActions.TryGetValue(id, out var action) && action.Can();

    /// <summary>Caps Lock (4.4-BugHunt): snapping on or off, as the timeline's Snap button does.</summary>
    private void ToggleSnap()
    {
        _viewModel.SnapClips = !_viewModel.SnapClips;
        _viewModel.StatusText = _viewModel.SnapClips
            ? "Snap is on: a dragged clip's edges catch on the other clips, the markers and the playhead."
            : "Snap is off: clips are dragged freely.";
    }

    /// <summary>
    /// Undo (or redo) from wherever it was asked (a key, a mouse button, the dial), by 4.3E's rules: the focused text box's
    /// own while what was done last is typing in it, the timeline's otherwise; never while a drop-down is open, and not while
    /// a drag is held (what is dragged would go on being moved from where Undo put it): a drag on the timeline, or the left
    /// button held anywhere.
    /// </summary>
    private void RunUndo(bool redo)
    {
        var focused = Keyboard.FocusedElement;
        if (focused is ComboBoxItem || TimelineDragHeld)
            return;

        if (focused is TextBoxBase box && TypingIsNewest(box, redo))
        {
            if (redo)
                box.Redo();
            else
                box.Undo();
            _typedIn = (box, _viewModel.UndoMark);
        }
        else if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            if (redo)
                Redo_Click(this, new RoutedEventArgs());
            else
                Undo_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Ripple Delete (R, Shift+Delete by default): the clips after it closing the gap.</summary>
    private void RippleDeleteSelected()
    {
        if (_selectedClip is Layer { HasTiming: true } selected)
        {
            if (_viewModel.RippleDelete(selected))
                (_selectedClip, _mainClipSelected) = (null, false);
            ScheduleClipRedraw();
        }
        else
        {
            DeleteSelectedClip();
        }
    }

    /// <summary>
    /// The shortcut a key press is bound to, run (MainWindow_PreviewKeyDown, after the text box's keys and the nudge): true
    /// when the key was taken. A control with the keyboard keeps its own uses of the key (a list its arrows, the rules grid
    /// its Delete), and an action that cannot run now leaves the key to it too.
    /// </summary>
    private bool TryRunBound(string id, InputChord chord, KeyEventArgs e, object sender, IInputElement? focused)
    {
        if (KeybindManager.Action(id) is not { } action || KeyBelongsToFocus(chord.Key, focused))
            return false;

        if (action.Has(KeyActionFlags.Hold))
        {
            if (!e.IsRepeat)
                BeginHold(chord.Key);
            return true;
        }

        // A held navigation key (W, A, S, D): its first press starts the zoom or the pan, which goes on every frame while a
        // navigation key is down (NavigationFrame); the key's repeats are swallowed.
        if (action.Has(KeyActionFlags.Continuous))
        {
            if (!e.IsRepeat)
                BeginKeyNavigation();
            return true;
        }

        // While a drag on the timeline is held, nothing changes the project under it: the key is taken and does nothing.
        if (RefusedWhileDragging(id))
            return true;

        if (!CanRunAction(id))
            return false;

        // A held key repeats only what is meant to go on (stepping, zooming); anything else runs once per press.
        if (e.IsRepeat && !action.Has(KeyActionFlags.Repeat))
            return true;

        if (id == "ui.dial")
            OpenLightningDial(held: false, category: null, over: sender as Window);
        else
            RunAction(id);

        // Caps Lock ran a shortcut: Windows switched the capitals as the key went down, and they are switched back as it is
        // let go (Editor_PreviewKeyUp), so snapping on and off never leaves the next typing in capitals.
        if (chord.Key == Key.CapsLock)
            _capsToRestore = true;
        return true;
    }

    private bool KeyBelongsToFocus(Key key, IInputElement? focused) => key switch
    {
        // A slider other than the timeline keeps its arrow keys (volume, zoom, opacity...).
        Key.Left or Key.Right => focused is Slider slider && !ReferenceEquals(slider, TimelineSlider),
        Key.Home or Key.End => focused is ItemsControl or ListBoxItem or Slider,
        // (Down on Add Layer opens its kinds instead: AddLayerButton_PreviewKeyDown.)
        Key.Up or Key.Down => focused is ItemsControl or ListBoxItem or Slider or ScrollViewer or KeyLanesView or FrameworkElement { Name: "AddLayerButton" },
        Key.PageUp or Key.PageDown => focused is ItemsControl or ListBoxItem or ScrollViewer,
        // In the rules grid Delete is the grid's own key; in the Keyframes pane's rows, a keyframe's.
        Key.Delete or Key.Back => focused is DataGridCell or KeyLanesView,
        // A grid moves between its cells with Tab (and commits what was typed in one).
        Key.Tab => focused is DataGridCell or DataGrid,
        // A grid's cell starts editing with the letter or the digit typed into it (the left hand's keys are shortcuts elsewhere).
        >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 => focused is DataGridCell or DataGrid,
        _ => false,
    };

    // ----- Keys before the shortcuts: Caps Lock's echo, and Esc for the dial and the tour -----

    // Caps Lock ran a shortcut and is to be switched back as it is let go; the press HandPeg sends for that, still to come
    // back as a key of its own (and until when it is waited for).
    private bool _capsToRestore;
    private int _capsEchoes;
    private long _capsEchoUntil;

    /// <summary>
    /// The keys the input layers take before any shortcut (MainWindow_PreviewKeyDown's first step): the Caps Lock press
    /// HandPeg sent itself (RestoreCapsLock) is swallowed; any other key hides a toggled X-Ray; Esc closes an open dial or ends
    /// the tour. (4.4-BugHunt: the navigation keys are ordinary shortcuts now, after the text box's keys.)
    /// </summary>
    private bool TakeOverlayKey(KeyEventArgs e)
    {
        var key = KeybindManager.KeyOf(e);
        if (key == Key.CapsLock && _capsEchoes > 0)
        {
            _capsEchoes--;
            if (Environment.TickCount64 <= _capsEchoUntil)
            {
                e.Handled = true;
                return true;
            }
        }

        if (_xrayToggled)
            HideXRay();

        if (key == Key.Escape)
        {
            if (_dialPopup is { IsOpen: true })
            {
                CloseLightningDial();
                e.Handled = true;
                return true;
            }

            if (_tour is { IsRunning: true })
            {
                _tour.Stop(finished: true);
                e.Handled = true;
                return true;
            }
        }

        return false;
    }

    // Windows switched Caps Lock as its key went down; one more press, sent now that the key is up, switches it back. Its
    // echo comes back as a key of its own and is swallowed (TakeOverlayKey), unless it has not come within a second.
    private void RestoreCapsLock()
    {
        (_capsEchoes, _capsEchoUntil) = (1, Environment.TickCount64 + 1000);
        KeybdEvent(CapsLockVirtualKey, CapsLockScanCode, 0, 0);
        KeybdEvent(CapsLockVirtualKey, CapsLockScanCode, KeyEventKeyUp, 0);
    }

    private const byte CapsLockVirtualKey = 0x14, CapsLockScanCode = 0x3A;
    private const uint KeyEventKeyUp = 0x0002;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    private static partial void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    // ----- A press outside a text box -----

    /// <summary>
    /// A press outside the text box holding the keyboard takes the keyboard from it (4.4-BugHunt), so the keys after it are
    /// shortcuts, not typing (what was typed is kept: the box is left as clicking away from it leaves it). The timeline, the
    /// Canvas and the player take no keyboard of their own, so without this a box left holding it ate the left hand's keys.
    /// A press on a text box or a drop-down leaves the keyboard to what was pressed, and a press in a popup (a drop-down's
    /// list, a menu) is the popup's own.
    /// </summary>
    private static void ReleaseTypingFocus(DependencyObject? pressed)
    {
        if (Keyboard.FocusedElement is not (TextBoxBase or PasswordBox) || Keyboard.FocusedElement is not DependencyObject box)
            return;

        // A run of text stands for the element it is in.
        while (pressed is not null and not Visual)
            pressed = LogicalTreeHelper.GetParent(pressed);

        DependencyObject? top = null;
        for (var at = pressed; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, box) || at is TextBoxBase or PasswordBox or ComboBox)
                return;
            top = at;
        }

        if (top is not Window)
            return;

        // Its focus scope forgets it (so the box hears that it lost the focus, and a value bound to it on leaving is handed
        // on), then the keyboard lets it go: the keys go to the window.
        if (FocusManager.GetFocusScope(box) is { } scope)
            FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }

    // ----- Mouse buttons -----

    private MouseButton? _dialButton;

    private void Editor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditorWindow(sender))
            return;

        if (_xrayToggled)
            HideXRay();

        // A press outside the text box holding the keyboard takes it from the box. Not the side buttons': Mouse 4 is Undo,
        // which takes back the box's own typing while that is the newest.
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right or MouseButton.Middle)
            ReleaseTypingFocus(e.OriginalSource as DependencyObject);

        // A press on the video lands on the window kept over it, which never takes the keyboard: the window the player is
        // in is brought forward, as a click on the video always did, so the shortcuts are heard after it.
        if (ReferenceEquals(sender, VideoView.OverlayWindow) && VideoView.OverlayWindow?.Owner is { IsActive: false } owner)
            owner.Activate();

        // A dial left open without the mouse (it could not take it): a press anywhere else closes it.
        if (_dialPopup is { IsOpen: true })
            CloseLightningDial();

        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                return;

            // The right button held over a surface while a navigation key is down steers that surface (and the release is
            // the navigation's then: no menu opens). What is under the pointer is noted; the press goes on as it would.
            case MouseButton.Right:
                BeginNavigationPress(sender, e.OriginalSource);
                return;
        }

        if (KeybindManager.Resolve(KeybindManager.ChordOf(e.ChangedButton)) is not { } id)
            return;

        e.Handled = true;
        if (id == "ui.dial")
        {
            // Not over a drag on the timeline (the dial's actions would change the project under it).
            if (RefusedWhileDragging(id))
                return;

            _dialButton = e.ChangedButton;
            OpenLightningDial(held: true, category: null, over: sender as Window);
        }
        else
        {
            RunAction(id);
        }
    }

    private void Editor_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditorWindow(sender))
            return;

        if (e.ChangedButton == MouseButton.Right)
        {
            // After navigating, the release is the navigation's: no menu opens for it. A navigation key still held goes on
            // steering what it steered; with none, the press's navigation is over.
            if (_navEngaged)
            {
                e.Handled = true;
                ReleaseNavigation();
            }

            if (!_navTicking)
                EndNavigation();
            return;
        }

        // The button holding the dial open, released where the dial could not hear it.
        if (e.ChangedButton == _dialButton && _dial is { IsHeld: true } dial)
        {
            e.Handled = true;
            dial.ReleaseHold();
        }
        else if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2
                 && KeybindManager.Resolve(KeybindManager.ChordOf(e.ChangedButton)) is not null)
        {
            e.Handled = true;
        }
    }

    // ----- Held keys: X-Ray on Tab -----

    private Key _holdKey;
    private DispatcherTimer? _holdTimer;

    /// <summary>How long Tab is held before X-Ray shows; let go sooner, it moves the keyboard on as Tab always did.</summary>
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(250);

    private void BeginHold(Key key)
    {
        _holdKey = key;
        if (_holdTimer is not { } timer)
        {
            timer = _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoldDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ShowXRay(toggled: false);
            };
        }

        timer.Stop();
        timer.Start();
    }

    private void Editor_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = KeybindManager.KeyOf(e);
        if (!IsEditorWindow(sender))
            return;

        // Caps Lock ran a shortcut as it went down: the capitals it switched are switched back now that it is up.
        if (key == Key.CapsLock && _capsToRestore)
        {
            _capsToRestore = false;
            RestoreCapsLock();
            return;
        }

        if (_holdKey == Key.None || key != _holdKey)
            return;

        var tapped = _holdTimer is { IsEnabled: true };
        _holdTimer?.Stop();
        _holdKey = Key.None;
        e.Handled = true;
        if (!tapped)
        {
            HideXRay();
            return;
        }

        // A quick tap of Tab still moves the keyboard to the next control.
        if (key == Key.Tab)
        {
            var request = new TraversalRequest(FocusNavigationDirection.Next);
            if (Keyboard.FocusedElement is UIElement focused)
                focused.MoveFocus(request);
            else
                (sender as UIElement)?.MoveFocus(request);
        }
    }

    // ----- The held navigation keys (W, A, S, D) -----

    private enum NavSurface { None, Timeline, Canvas, Player }

    // Speeds, per second of a key held (three times with Shift): the timeline zooms by a factor and pans by its view's width;
    // the Canvas and the player zoom by a factor (up to 16 times) and pan in DIPs.
    private const double NavTimelineZoomRate = 2.5, NavTimelinePanRate = 0.8, NavZoomRate = 2.2, NavPanSpeed = 700, NavFast = 3, NavMaxZoom = 16;

    private NavSurface _navSurface;
    private bool _navEngaged, _navTicking;
    private Window? _navWindow;
    private UnifiedTimelineControl? _navTimeline;
    private long _navLastTick;
    private Size _navPicture;
    private MatrixTransform? _canvasView, _playerView;
    private double _playerZoom, _playerPanX, _playerPanY;

    /// <summary>
    /// The right button went down: the surface it is on is noted (a navigation key pressed while it is held steers that one,
    /// and its menu then does not open). With a navigation key held already, a press on another surface takes the navigation
    /// over to it, held by the button; a press on no surface leaves it as it is.
    /// </summary>
    private void BeginNavigationPress(object sender, object originalSource)
    {
        var timeline = _navTimeline;
        _navTimeline = null;
        var surface = ReferenceEquals(sender, VideoView.OverlayWindow)
            ? PlayerNavigable ? NavSurface.Player : NavSurface.None
            : SurfaceAt(originalSource as DependencyObject);
        if (_navTicking && surface == NavSurface.None)
        {
            _navTimeline = timeline;
            return;
        }

        ReleaseNavigation();
        (_navWindow, _navSurface) = (sender as Window, surface);
        if (_navTicking)
        {
            if (surface == NavSurface.Player)
                _navPicture = PictureSize();
            EngageNavigation();
        }
    }

    /// <summary>
    /// A navigation key went down (4.4-BugHunt: no button needed). It steers the surface under the pointer (the timeline, its
    /// overview bar or keyframe lanes, the Canvas, the player), else the timeline; while the right button is held after a press
    /// on a surface, that one. A second key held with the first steers what the first does.
    /// </summary>
    private void BeginKeyNavigation()
    {
        if (_navSurface != NavSurface.None && _navWindow is not null && Mouse.RightButton == MouseButtonState.Pressed)
        {
            if (!_navEngaged)
                EngageNavigation();
        }
        else if (!_navTicking)
        {
            ReleaseNavigation();
            _navTimeline = null;
            _navSurface = SurfaceUnderPointer();
            if (_navSurface == NavSurface.Player)
                _navPicture = PictureSize();
        }

        StartNavigationTicker();
    }

    // What the pointer is over: the player (the window kept over the video), the timeline, the Canvas; the timeline otherwise
    // (the keys zoom and pan it wherever the pointer is, as = and - do).
    private NavSurface SurfaceUnderPointer()
    {
        var over = Mouse.DirectlyOver as DependencyObject;
        if (over is not null && VideoView.OverlayWindow is { } overlay && ReferenceEquals(Window.GetWindow(over), overlay))
            return PlayerNavigable ? NavSurface.Player : NavSurface.Timeline;

        return SurfaceAt(over) is var surface and not NavSurface.None ? surface : NavSurface.Timeline;
    }

    private bool PlayerNavigable => _mpv is not null && VideoView.IsVisible;

    // Which surface a press is on: the timeline (its pane, the overview bar or the keyframe lanes), the Canvas or the player.
    private NavSurface SurfaceAt(DependencyObject? source)
    {
        // A run of text stands for the element it is in.
        while (source is not null and not Visual)
            source = LogicalTreeHelper.GetParent(source);

        for (var at = source; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UnifiedTimelineControl control)
                _navTimeline ??= control;
            if (ReferenceEquals(at, UnifiedTimelinePanel) || ReferenceEquals(at, TimelinePane) || ReferenceEquals(at, KeyframesPane))
                return NavSurface.Timeline;
            if (ReferenceEquals(at, LayoutPane))
                return NavSurface.Canvas;
            if (ReferenceEquals(at, PlayerPane))
                return PlayerNavigable ? NavSurface.Player : NavSurface.None;
        }

        return NavSurface.None;
    }

    private void EngageNavigation()
    {
        _navEngaged = true;

        // The timeline's right-click opens no menu: the press was the navigation's (and the release is taken here, not by
        // the timeline).
        _navTimeline?.CancelPendingMenu();

        // The overview bar's right-drag that marks a stretch is let go: the press was the navigation's.
        if (_rangeDragStartMs is not null)
        {
            _rangeDragStartMs = null;
            TimelineSlider.ReleaseMouseCapture();
            RedrawSegments();
        }

        if (_navSurface == NavSurface.Player)
            _navPicture = PictureSize();

        // The window the press was in keeps the mouse until the button is let go, wherever that is.
        if (_navWindow is { } window && Mouse.Captured is null)
            window.CaptureMouse();
    }

    private void EndNavigation()
    {
        StopNavigationTicker();
        ReleaseNavigation();
        (_navSurface, _navTimeline) = (NavSurface.None, null);
    }

    // The right button's hold of the navigation is over: the window it captured lets the mouse go.
    private void ReleaseNavigation()
    {
        if (_navEngaged && _navWindow is { IsMouseCaptured: true } window)
            window.ReleaseMouseCapture();
        (_navEngaged, _navWindow) = (false, null);
    }

    // The navigation moves every frame while a key is held, and stops asking for frames when none is.
    private void StartNavigationTicker()
    {
        if (_navTicking)
            return;

        _navTicking = true;
        _navLastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += NavigationFrame;
    }

    private void StopNavigationTicker()
    {
        if (!_navTicking)
            return;

        _navTicking = false;
        CompositionTarget.Rendering -= NavigationFrame;
    }

    private static int Held(string id) => KeybindManager.IsHeld(id) ? 1 : 0;

    private void NavigationFrame(object? sender, EventArgs e)
    {
        // Held by the right button: over when it is let go. Over too when a text box or a dialog takes the keyboard (the
        // keys are typing there).
        if (_navSurface == NavSurface.None || (_navEngaged && Mouse.RightButton != MouseButtonState.Pressed)
            || Keyboard.FocusedElement is TextBoxBase or PasswordBox || ComponentDispatcher.IsThreadModal)
        {
            EndNavigation();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Min((now - _navLastTick) / (double)Stopwatch.Frequency, 0.05);
        _navLastTick = now;
        var zoom = Held("nav.forward") - Held("nav.back");
        var pan = Held("nav.right") - Held("nav.left");
        if (zoom == 0 && pan == 0)
        {
            // Every key let go: a key's navigation is over; the right button's waits for its next key while it is held.
            if (_navEngaged)
                StopNavigationTicker();
            else
                EndNavigation();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            seconds *= NavFast;

        switch (_navSurface)
        {
            case NavSurface.Timeline:
                NavigateTimeline(zoom, pan, seconds);
                break;
            case NavSurface.Canvas when zoom == 0 && PansTimelineInstead(CanvasView.Matrix.M11):
            case NavSurface.Player when zoom == 0 && PansTimelineInstead(Math.Pow(2, _playerZoom)):
                NavigateTimeline(0, pan, seconds);
                break;
            case NavSurface.Canvas:
                NavigateCanvas(zoom, pan, seconds);
                break;
            case NavSurface.Player:
                NavigatePlayer(zoom, pan, seconds);
                break;
        }
    }

    // A pan over a Canvas or a player shown fitted (nothing to pan there) pans the timeline when the user asked for it (B7).
    private static bool PansTimelineInstead(double scale) => scale <= 1.0001 && AppSettings.Current.PanTimelineWhenNotZoomed;

    // The timeline zooms about the moment under the pointer (the playhead off the timeline's own surface) and pans along.
    private void NavigateTimeline(int zoom, int pan, double seconds)
    {
        if (zoom != 0)
            ZoomTimeline(Math.Pow(NavTimelineZoomRate, zoom * seconds), TimelineAnchor());
        if (pan != 0 && ViewSpan > 0)
            PanTimeline(pan * NavTimelinePanRate * seconds * ViewSpan);
    }

    private double TimelineAnchor()
    {
        var span = ViewSpan;
        if (span <= 0)
            return 0.5;

        var at = _navTimeline is { TimeAreaWidth: > 0 } control ? control.TimeAt(Mouse.GetPosition(control).X) : _viewModel.PositionMs / 1000;
        var fraction = (at - _viewStart) / span;
        return fraction is >= 0 and <= 1 ? fraction : 0.5;
    }

    // The Canvas: a matrix on the canvas alone (its well clips it; the boxes' drags are in its own coordinates, so they
    // stay right), never closer than fitted, always covering the well.
    private MatrixTransform CanvasView
    {
        get
        {
            if (_canvasView is null)
            {
                _canvasView = new MatrixTransform();
                LayoutCanvas.RenderTransform = _canvasView;
            }

            return _canvasView;
        }
    }

    private void NavigateCanvas(int zoom, int pan, double seconds)
    {
        var (width, height) = (LayoutCanvas.ActualWidth, LayoutCanvas.ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        var view = CanvasView;
        var matrix = view.Matrix;
        if (zoom != 0)
        {
            var scale = Math.Clamp(matrix.M11 * Math.Pow(NavZoomRate, zoom * seconds), 1, NavMaxZoom);
            var factor = scale / matrix.M11;
            var at = Mouse.GetPosition(VisualTreeHelper.GetParent(LayoutCanvas) as IInputElement ?? LayoutCanvas);
            matrix.ScaleAt(factor, factor, at.X, at.Y);
        }

        // Panning left shows what is to the left: the picture moves right.
        if (pan != 0)
            matrix.Translate(-pan * NavPanSpeed * seconds, 0);

        view.Matrix = Covering(matrix, width, height);
    }

    private static Matrix Covering(Matrix matrix, double width, double height)
    {
        var scale = Math.Max(matrix.M11, 1);
        return new Matrix(scale, 0, 0, scale, Math.Clamp(matrix.OffsetX, width - width * scale, 0), Math.Clamp(matrix.OffsetY, height - height * scale, 0));
    }

    private void ClampCanvasView()
    {
        if (_canvasView is { } view && LayoutCanvas.ActualWidth > 0 && LayoutCanvas.ActualHeight > 0)
            view.Matrix = Covering(view.Matrix, LayoutCanvas.ActualWidth, LayoutCanvas.ActualHeight);
    }

    // The player: mpv zooms and pans the picture (nothing is filtered again), and the boxes, the crop and the safe margins
    // drawn over it follow by the same matrix on the overlay (their own maths is in its coordinates, so it stays right).
    private MatrixTransform PlayerView
    {
        get
        {
            if (_playerView is null)
            {
                _playerView = new MatrixTransform();
                PlayerOverlay.RenderTransform = _playerView;
            }

            return _playerView;
        }
    }

    private void NavigatePlayer(int zoom, int pan, double seconds)
    {
        var (width, height) = (PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        var picture = _navPicture;
        if (_mpv is null || width <= 0 || height <= 0 || picture.Width <= 0 || picture.Height <= 0)
            return;

        var scale = Math.Pow(2, _playerZoom);
        if (zoom != 0)
        {
            // What is under the pointer stays there: the picture's middle moves away from it (or toward it) by the zoom.
            var next = Math.Clamp(scale * Math.Pow(NavZoomRate, zoom * seconds), 1, NavMaxZoom);
            var at = VideoView.OverlayWindow is { } overlay ? Mouse.GetPosition(overlay) : new Point(width / 2, height / 2);
            var middle = new Point(width / 2 + _playerPanX * picture.Width * scale, height / 2 + _playerPanY * picture.Height * scale);
            var moved = at + (middle - at) * (next / scale);
            scale = next;
            (_playerPanX, _playerPanY) = ((moved.X - width / 2) / (picture.Width * scale), (moved.Y - height / 2) / (picture.Height * scale));
        }

        if (pan != 0)
            _playerPanX -= pan * NavPanSpeed * seconds / (picture.Width * scale);

        // The picture keeps covering what it covered fitted: it is never panned off its own place.
        var reach = (scale - 1) / (2 * scale);
        (_playerPanX, _playerPanY) = (Math.Clamp(_playerPanX, -reach, reach), Math.Clamp(_playerPanY, -reach, reach));
        _playerZoom = Math.Log2(scale);
        ApplyPlayerView();
    }

    private void ApplyPlayerView()
    {
        _mpv?.SetViewZoom(_playerZoom, _playerPanX, _playerPanY);
        var scale = Math.Pow(2, _playerZoom);
        var matrix = Matrix.Identity;
        matrix.ScaleAt(scale, scale, PlayerOverlay.ActualWidth / 2, PlayerOverlay.ActualHeight / 2);
        matrix.Translate(_playerPanX * _navPicture.Width * scale, _playerPanY * _navPicture.Height * scale);
        PlayerView.Matrix = matrix;
    }

    // The picture as mpv fits it into the player: the output frame (the source's size without one), letterboxed.
    private Size PictureSize()
    {
        var (width, height) = (PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        double frameWidth = _viewModel.FrameWidth, frameHeight = _viewModel.FrameHeight;
        if (frameWidth <= 0 || frameHeight <= 0)
            (frameWidth, frameHeight) = (_viewModel.SourceWidth, _viewModel.SourceHeight);
        if (width <= 0 || height <= 0 || frameWidth <= 0 || frameHeight <= 0)
            return new Size(Math.Max(width, 0), Math.Max(height, 0));

        var fit = Math.Min(width / frameWidth, height / frameHeight);
        return new Size(frameWidth * fit, frameHeight * fit);
    }

    private void ResetNavigationViews()
    {
        (_playerZoom, _playerPanX, _playerPanY) = (0, 0, 0);
        if (_playerView is { } player)
            player.Matrix = Matrix.Identity;
        _mpv?.SetViewZoom(0, 0, 0);
        if (_canvasView is { } canvas)
            canvas.Matrix = Matrix.Identity;
    }

    // ----- The Lightning Dial -----

    private Popup? _dialPopup;
    private LightningDialOverlay? _dial;

    /// <summary>
    /// Opens the dial at the pointer (the window's middle when the pointer is not over it): <paramref name="held"/> while the
    /// button that opened it is down, on a category's own level for its key.
    /// </summary>
    private void OpenLightningDial(bool held, int? category, Window? over)
    {
        // Over the video, the dial belongs to the window the player is in.
        var window = over is not null && ReferenceEquals(over, VideoView.OverlayWindow) ? over.Owner ?? this
            : over is not null && _hookedSides.Contains(over) ? over
            : this;
        if (window.Content is not FrameworkElement root || !root.IsVisible)
            return;

        if (_dialPopup is not { } popup || _dial is not { } dial)
        {
            dial = _dial = new LightningDialOverlay { CanRun = CanRunAction };
            dial.Chosen += id =>
            {
                CloseLightningDial();
                Dispatcher.BeginInvoke(() => RunAction(id), DispatcherPriority.Input);
            };
            dial.CloseRequested += CloseLightningDial;
            popup = _dialPopup = new Popup { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, Focusable = false, Child = dial };
        }

        var at = Mouse.GetPosition(root);
        if (at.X < 0 || at.Y < 0 || at.X > root.ActualWidth || at.Y > root.ActualHeight)
            at = new Point(root.ActualWidth / 2, root.ActualHeight / 2);

        CloseLightningDial();
        if (!held)
            _dialButton = null;
        popup.PlacementTarget = root;
        (popup.HorizontalOffset, popup.VerticalOffset) = (at.X - dial.Width / 2, at.Y - dial.Height / 2);
        dial.Open(KeybindManager.Dial, category, held);
        popup.IsOpen = true;
        dial.TakeMouse();
    }

    private void CloseLightningDial()
    {
        if (_dialPopup is not { IsOpen: true } popup)
            return;

        _dial?.Reset();
        popup.IsOpen = false;
    }

    // ----- X-Ray -----

    private Popup? _xrayPopup;
    private XRayOverlay? _xray;
    private DispatcherTimer? _xrayTimer;
    private bool _xrayToggled;
    private TimeSpan _xrayLastFrame;
    private double _xrayFrameSum;
    private int _xrayFrames;

    private static readonly string[] XRayPaneIds = ["Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Presets"];

    /// <summary>From the dial: shown until the next key or click.</summary>
    private void ToggleXRay()
    {
        if (_xrayPopup is { IsOpen: true })
            HideXRay();
        else
            ShowXRay(toggled: true);
    }

    private void ShowXRay(bool toggled)
    {
        if (Content is not FrameworkElement root || !IsVisible || WindowState == WindowState.Minimized)
            return;

        if (_xrayPopup is null)
        {
            _xray = new XRayOverlay();
            _xrayPopup = new Popup { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, Focusable = false, IsHitTestVisible = false, Child = _xray };
            _xrayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _xrayTimer.Tick += (_, _) => RefreshXRay();
        }

        _xrayToggled = toggled;
        if (_xrayPopup.IsOpen)
            return;

        _xrayPopup.PlacementTarget = root;
        (_xrayPopup.HorizontalOffset, _xrayPopup.VerticalOffset) = (0, 0);
        (_xrayLastFrame, _xrayFrameSum, _xrayFrames) = (default, 0, 0);
        RefreshXRay();
        _xrayPopup.IsOpen = true;
        _xray!.MakeClickThrough();
        ShowKeyGlance(root);
        _xrayTimer!.Start();
        CompositionTarget.Rendering += XRayFrame;
    }

    private void HideXRay()
    {
        _xrayToggled = false;
        if (_xrayPopup is not { IsOpen: true } popup)
            return;

        popup.IsOpen = false;
        if (_keyGlancePopup is { } glance)
            glance.IsOpen = false;
        _xrayTimer?.Stop();
        CompositionTarget.Rendering -= XRayFrame;
    }

    // ----- The keys over the player while X-Ray shows (4.4.0) -----
    //
    // The keyboard and mouse map in a popup of its own over the player (the X-Ray drawing lets every click through; this one
    // takes the pointer): the pointer on a key names its action, and a click on a key or the "Edit Keybinds" pill opens
    // Settings at Keybinds & Lightning Dial, at that key. Made again each time from the keys in effect.

    private Popup? _keyGlancePopup;

    private void ShowKeyGlance(FrameworkElement root)
    {
        // Over the player's pane when it is shown and wide enough to read; over the middle of the window otherwise.
        var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        if (PlayerPane is { IsVisible: true, ActualWidth: >= 480, ActualHeight: >= 220 } player && root.IsAncestorOf(player))
            area = player.TransformToAncestor(root).TransformBounds(new Rect(player.RenderSize));

        var width = Math.Min(area.Width - 24, 960);
        if (width < 360)
            return;

        var map = new KeybindMapControl(new KeybindDraft(AppSettings.Current.KeybindPreset, AppSettings.Current.CustomKeybinds)) { Glance = true, Width = width };
        map.EditRequested += chord =>
        {
            HideXRay();
            Dispatcher.BeginInvoke(() => OpenSettings("Keybinds", window => window.ShowKeybind(chord)));
        };
        map.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Min(map.DesiredSize.Height, area.Height - 16);

        _keyGlancePopup ??= new Popup { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, Focusable = false };
        _keyGlancePopup.Child = map;
        map.Height = height;
        _keyGlancePopup.PlacementTarget = root;
        (_keyGlancePopup.HorizontalOffset, _keyGlancePopup.VerticalOffset) = (area.X + (area.Width - width) / 2, area.Y + Math.Max((area.Height - height) / 2, 8));
        _keyGlancePopup.IsOpen = true;
    }

    // The UI's frame time while X-Ray shows: the time between the frames WPF draws.
    private void XRayFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering || rendering.RenderingTime == _xrayLastFrame)
            return;

        if (_xrayLastFrame != default)
        {
            _xrayFrameSum += (rendering.RenderingTime - _xrayLastFrame).TotalMilliseconds;
            _xrayFrames++;
        }

        _xrayLastFrame = rendering.RenderingTime;
    }

    private void RefreshXRay()
    {
        if (_xray is null || Content is not FrameworkElement root)
            return;

        (_xray.Width, _xray.Height) = (root.ActualWidth, root.ActualHeight);

        // Every pane made and shown in this window, outlined with its id.
        var panes = new List<(Rect Bounds, string Id)>();
        foreach (var id in XRayPaneIds.Concat(_tabPanes.Select(t => t.Id)))
        {
            if (!_dock.IsRealized(id) || _dock.Realize(id) is not { IsVisible: true } element || !root.IsAncestorOf(element))
                continue;
            panes.Add((element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)), id));
        }

        var frame = _xrayFrames > 0 ? _xrayFrameSum / _xrayFrames : 0;
        (_xrayFrameSum, _xrayFrames) = (0, 0);

        Span<int> lanes = stackalloc int[4];
        foreach (var item in WorkScheduler.Items)
            if (item.IsActive && (int)item.Lane is >= 0 and < 4)
                lanes[(int)item.Lane]++;

        const double Megabyte = 1024 * 1024;
        var extent = TimelineExtent;
        var span = ViewSpan;
        var lines = new List<string>(9)
        {
            "X-Ray",
            frame > 0 ? FormattableString.Invariant($"UI frame   {frame:0.0} ms ({1000 / frame:0} fps)") : "UI frame   idle",
            FormattableString.Invariant($"Memory     {Environment.WorkingSet / Megabyte:0} MB working set, {GC.GetTotalMemory(false) / Megabyte:0} MB managed"),
            FormattableString.Invariant($"Work       {lanes[0]} foreground, {lanes[1]} interactive, {lanes[2]} background, {lanes[3]} idle"),
            _mpv is { } player
                ? FormattableString.Invariant($"Player     {(player.IsPlaying ? "playing" : "paused")} at {TimeDisplay.Format(player.PositionMs / 1000.0)}{(player.LastError is { Length: > 0 } error ? "; " + error : "")}")
                : "Player     not started",
            FormattableString.Invariant($"Live graph version {_liveStateVersion}"),
            _viewModel.UndoMark is null ? "Undo       nothing to undo" : "Undo       a step to undo",
            extent > 0 && span > 0
                ? FormattableString.Invariant($"Timeline   {TimeDisplay.Format(_viewStart)} to {TimeDisplay.Format(_viewStart + span)}, {extent / span:0.#}x")
                : "Timeline   empty",
            FormattableString.Invariant($"Keys       {KeybindManager.KnownPreset(AppSettings.Current.KeybindPreset)}{(AppSettings.Current.CustomKeybinds is { Count: > 0 } custom ? $", {custom.Count} changed" : "")}"),
        };

        _xray.Show(panes, lines);
    }

    // ----- The Quick-Start Tour -----

    private QuickStartTour? _tour;

    private void OfferTourOnce(object? sender, EventArgs e)
    {
        ContentRendered -= OfferTourOnce;

        // A moment after the first frame, once the window has settled (the inactive tabs are made at idle first).
        var wait = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1.5) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            OfferTourWhenFree();
        };
        wait.Start();
    }

    /// <summary>
    /// Starts the tour offered by itself once HandPeg is free for it: no dialog open (a restored autosave's question, a crash
    /// report, an engine download: it comes after them) and HandPeg in front (another program in front: it waits for HandPeg
    /// to be brought back). It counts as had as soon as it starts, so closing HandPeg half-way never brings it back.
    /// </summary>
    private void OfferTourWhenFree()
    {
        if (AppSettings.Current.QuickStartTourDone || !IsLoaded)
            return;

        if (ComponentDispatcher.IsThreadModal)
        {
            EventHandler? left = null;
            left = (_, _) =>
            {
                ComponentDispatcher.LeaveThreadModal -= left;
                Dispatcher.BeginInvoke(OfferTourWhenFree, DispatcherPriority.ApplicationIdle);
            };
            ComponentDispatcher.LeaveThreadModal += left;
            return;
        }

        if (!IsActive)
        {
            EventHandler? back = null;
            back = (_, _) =>
            {
                Activated -= back;
                Dispatcher.BeginInvoke(OfferTourWhenFree, DispatcherPriority.ApplicationIdle);
            };
            Activated += back;
            return;
        }

        if (TryStartQuickStartTour())
            MarkTourDone();
    }

    /// <summary>
    /// Shows the Quick-Start Tour from its first card (offered once by itself, and from Settings: asked for there, it starts
    /// once the Settings window has closed). Finishing or skipping it marks it done.
    /// </summary>
    public void StartQuickStartTour() => TryStartQuickStartTour();

    // Whether the tour started now (not while the Settings window is open: it starts when that closes, nor while this window
    // is hidden or minimized).
    private bool TryStartQuickStartTour()
    {
        if (Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault(w => w.IsVisible) is { } settings)
        {
            settings.Closed += (_, _) => Dispatcher.BeginInvoke(StartQuickStartTour, DispatcherPriority.ApplicationIdle);
            return false;
        }

        if (!IsVisible || WindowState == WindowState.Minimized)
            return false;

        _tour?.Stop();
        _tour = new QuickStartTour(this, TourSteps());
        _tour.Ended += finished =>
        {
            if (finished)
                MarkTourDone();
        };
        _tour.Start();
        return true;
    }

    private static void MarkTourDone()
    {
        if (AppSettings.Current.QuickStartTourDone)
            return;

        AppSettings.Current.QuickStartTourDone = true;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Quick-Start Tour: the settings could not be saved", ex);
        }
    }

    private List<QuickStartTour.Step> TourSteps()
    {
        static string Keys(string id, string otherwise) => KeybindManager.GestureText(id) is { Length: > 0 } keys ? keys : otherwise;
        var dial = Keys("ui.dial", "");
        var dialText = dial == "Mouse5" ? "the mouse's forward side button (Mouse 5)" : dial.Length > 0 ? dial : "its key (set it in Settings)";
        var (first, last) = (Keys("ui.dial1", ""), Keys("ui.dial7", ""));
        var categories = first.Length > 0 && last.Length > 0 ? $" ({first} to {last} open its categories at once)" : "";
        var xray = Keys("ui.xray", "");

        return
        [
            new("Add Your Media",
                "Click Add Media, or drop videos, pictures and sounds anywhere on HandPeg. The first video opens as the main track; "
                + "what you add after it lands on the timeline at the playhead.",
                () => DropHint.IsVisible ? DropHint : AddMediaHost),
            new("The Timeline",
                $"Every track is here, with the main video's sound and the captions under it. Drag a clip to move it, drag its edges to trim, "
                + $"and press {Keys("edit.split", "the split key")} to split at the playhead. Hold {Keys("nav.forward", "the zoom-in key")} or "
                + $"{Keys("nav.back", "the zoom-out key")} to zoom and {Keys("nav.left", "the pan-left key")} or {Keys("nav.right", "the pan-right key")} "
                + "to pan the timeline, or the Canvas or the player under the pointer; "
                + $"{Keys("transport.stepBack", "Left")} and {Keys("transport.stepForward", "Right")} step a frame.",
                () => UnifiedTimelinePanel),
            new("Arrange and Add Looks",
                "The Canvas places every layer on the output frame; Effects adds looks and sound cleanup. Every one-click preset leaves all of "
                + "its settings here for you to change.",
                () => LayoutPane.IsVisible ? LayoutPane : EffectsPanePanel),
            new("Your Keys and the Lightning Dial",
                $"Settings holds every shortcut and the Lightning Dial: press {dialText} anywhere for a ring of actions under the pointer{categories}."
                + (xray.Length > 0 ? $" {xray} for X-Ray, a look at every pane and what the editor is doing." : "")
                + " You can see this tour again from Settings.",
                () => SettingsHost),
        ];
    }
}
