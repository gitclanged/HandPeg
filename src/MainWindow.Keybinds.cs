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

// Input: KeybindManager shortcuts and their actions, held navigation keys (WASD zoom/pan the timeline, Canvas or player under the
// pointer, no button needed), Lightning Dial, Tab-held X-Ray HUD, and the Quick-Start Tour. Nothing is built before Frame 1: dial,
// HUD and tour on first use, the action table on the first key, the key map from in-memory settings.
public partial class MainWindow
{
    // ----- Setup (called from the constructor) -----

    private void InitializeKeybinds()
    {
        // All editor windows (main, side, video overlay) route shortcut mouse buttons, text-box focus release on press, right-button
        // navigation holds, and held-key release here.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Editor_PreviewMouseDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Editor_PreviewMouseUp), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Editor_PreviewKeyUp), handledEventsToo: true);

        // Near-transparent overlay fill so right/side buttons and wheel hit-test (fully transparent pixels passed them to the native
        // video HWND, where they were lost).
        PlayerOverlay.Background = PlayerHitBrush;

        // Reset Canvas and player zoom on file load (the engine would otherwise keep it across files).
        _viewModel.MediaLoaded += _ => ResetNavigationViews();
        LayoutCanvas.SizeChanged += (_, _) => ClampCanvasView();
        HookCanvasBand();
        PlayerOverlay.SizeChanged += (_, _) =>
        {
            if (_playerZoom != 0)
            {
                _navPicture = PictureSize();
                ApplyPlayerView();
            }
        };

        // App deactivated: close overlays and release held inputs.
        Application.Current.Deactivated += (_, _) => DropTransientInput();

        // Auto-offer the tour once per install that has not seen it (after first-run, or on upgrade), when idle with no dialogs.
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
        EndGradeHold();
        _holdTimer?.Stop();
        _holdKey = Key.None;
        _capsToRestore = false;
    }

    /// <summary>
    /// True while a timeline drag is active; project-changing actions are refused (key consumed, no-op) so a drag never commits over
    /// a changed document.
    /// </summary>
    private bool TimelineDragHeld => UnifiedTimeline.Surface.IsDragging;

    // Project-changing keys are refused during a timeline drag or a tab switch.
    private bool RefusedWhileDragging(string id) =>
        (TimelineDragHeld || _switchingTab) && KeybindManager.Action(id) is { } action && action.Has(KeyActionFlags.ChangesProject);

    // ----- Actions -----

    private Dictionary<string, (Func<bool> Can, Action Run)>? _keyActions;

    /// <summary>Action table (handler and CanRun per KeybindManager id), built on the first key press.</summary>
    private Dictionary<string, (Func<bool> Can, Action Run)> KeyActions => _keyActions ??= MakeKeyActions();

    private Dictionary<string, (Func<bool> Can, Action Run)> MakeKeyActions()
    {
        Func<bool> always = static () => true, source = () => _viewModel.HasSource;
        var actions = new Dictionary<string, (Func<bool> Can, Action Run)>
        {
            ["file.save"] = (always, () => _viewModel.SaveProjectNowCommand.Execute(null)),
            ["file.saveAs"] = (always, () => _viewModel.SaveProjectAsCommand.Execute(null)),
            ["file.saveBundle"] = (always, () => SaveProjectAs(bundle: true)),
            ["edit.undo"] = (always, () => RunUndo(redo: false)),
            ["edit.redo"] = (always, () => RunUndo(redo: true)),
            ["file.addMedia"] = (always, () => AddMedia_Click(AddMediaButton, new RoutedEventArgs())),
            ["ui.settings"] = (always, () => OpenSettings()),
            // Project tab actions (MainWindow.ProjectTabs.cs); each reports refusals in the status bar.
            ["tab.new"] = (always, () => _ = NewProjectTabAsync()),
            ["tab.close"] = (always, () => _ = CloseProjectTabAsync(null)),
            ["tab.next"] = (always, () => _ = CycleProjectTabAsync(1)),
            ["tab.prev"] = (always, () => _ = CycleProjectTabAsync(-1)),
            ["captions.generate"] = (source, () => GenerateCaptionsFrom(null)),

            ["transport.playPause"] = (always, () => PlayPause_Click(this, new RoutedEventArgs())),
            // JKL transport: J reverse, K stop, L forward; repeated J/L speeds up.
            ["transport.shuttleBack"] = (always, () => Shuttle(-1)),
            ["transport.stop"] = (always, () => Shuttle(0)),
            ["transport.shuttleForward"] = (always, () => Shuttle(1)),
            ["transport.stepBack"] = (always, () => StepFrames(-1)),
            ["transport.stepForward"] = (always, () => StepFrames(1)),
            ["transport.start"] = (source, () => _viewModel.PositionMs = 0),
            ["transport.end"] = (source, () => _viewModel.PositionMs = _viewModel.DurationMs),
            ["transport.prevIFrame"] = (source, () => JumpToIFrame(-1)),
            ["transport.nextIFrame"] = (source, () => JumpToIFrame(1)),
            ["transport.prevEdit"] = (source, () => GoToEditPoint(forward: false)),
            ["transport.nextEdit"] = (source, () => GoToEditPoint(forward: true)),

            ["edit.split"] = (source, SplitSelectedClip),
            ["edit.delete"] = (always, DeleteSelectedClip),
            ["edit.rippleDelete"] = (always, RippleDeleteSelected),
            ["edit.trimStart"] = (source, () => TrimSelectedClip(start: true)),
            ["edit.trimEnd"] = (source, () => TrimSelectedClip(start: false)),
            ["clip.toggleLink"] = (source, ToggleSelectedClipLink),
            // Target the selection like the toolbar/menus (video clip, audio track, else voice or video at the playhead).
            ["edit.deadAir"] = (source, () => RemoveDeadAir_Click(this, new RoutedEventArgs())),
            ["edit.bypassEffects"] = (source, () => _viewModel.SequenceEffectsBypassed = !_viewModel.SequenceEffectsBypassed),
            ["edit.copy"] = (source, CopySelectedClips),
            ["edit.cut"] = (source, CutSelectedClips),
            ["edit.paste"] = (source, () => _viewModel.PasteClips(_viewModel.PositionMs / 1000)),
            ["edit.duplicate"] = (source, DuplicateSelectedClips),
            ["edit.pasteAttributes"] = (source, PasteAttributesToSelection),
            // Alt+Up/Down only when a selected clip can move; otherwise the key goes to the focused control.
            ["clip.newTrackAbove"] = (CanMoveSelectedToNewTrack, () => MoveSelectedToNewTrack(above: true, ontoNeighbour: true)),
            ["clip.newTrackBelow"] = (CanMoveSelectedToNewTrack, () => MoveSelectedToNewTrack(above: false, ontoNeighbour: true)),

            ["marker.add"] = (source, () => _viewModel.AddMarker()),
            ["marker.ping"] = (source, () => _viewModel.PingToMark()),
            ["marker.next"] = (source, () => _viewModel.GoToMarker(forward: true)),
            ["marker.prev"] = (source, () => _viewModel.GoToMarker(forward: false)),

            // Timeline zoom: = and - about the playhead; Shift+\ fits the whole timeline (\ alone is grade Before/After).
            ["view.zoomIn"] = (source, () => ZoomAboutPlayhead(1.6)),
            ["view.zoomOut"] = (source, () => ZoomAboutPlayhead(1 / 1.6)),
            ["view.fit"] = (source, () => FitTimeline()),
            ["view.toggleSnap"] = (always, ToggleSnap),
            ["view.exploded3d"] = (() => CanToggleExplodedView, ToggleExplodedView),
            // Fullscreen Preview toggle; entering requires a picture in the player.
            ["view.fullscreen"] = (() => _fullscreen.IsOpen || VideoView.IsVisible, _fullscreen.Toggle),
            // Grade Before/After: holding \ shows the ungraded picture; dial/hotbar invocations toggle it.
            ["view.compareGrade"] = (() => _gpu is not null, ToggleGradeBypass),

            ["key.toggle"] = (source, () => ToggleKeyframe_Click(this, new RoutedEventArgs())),
            ["key.prev"] = (source, () => StepKeyframe(-1)),
            ["key.next"] = (source, () => StepKeyframe(1)),

            ["ui.dial"] = (always, () => OpenLightningDial(held: false, category: null, over: null)),
            ["ui.xray"] = (always, ToggleXRay),
            ["help.tour"] = (always, () => StartQuickStartTour()),
        };

        for (var i = 0; i < KeybindManager.DialSlices; i++)
        {
            var slice = i;
            actions["ui.dial" + (i + 1).ToString(CultureInfo.InvariantCulture)] = (() => CanRunDialSlice(slice), () => RunDialSlice(slice));
        }

        return actions;
    }

    /// <summary>Action id of a dial slice; null for a category or empty slice.</summary>
    private static string? DialSliceAction(int slice) =>
        KeybindManager.Dial is var dial && slice < dial.Count && dial[slice].IsAction ? dial[slice].Action : null;

    private bool CanRunDialSlice(int slice) => DialSliceAction(slice) is not { } id || CanRunAction(id);

    /// <summary>
    /// Number-key hotbar (eight slots): an action slice runs immediately; a category slice opens the dial on it.
    /// </summary>
    private void RunDialSlice(int slice)
    {
        if (DialSliceAction(slice) is { } id)
            RunAction(id);
        else
            OpenLightningDial(held: false, slice, over: null);
    }

    /// <summary>Runs an action by id (shortcut, mouse button, dial); returns false if it cannot run now.</summary>
    private bool RunAction(string id)
    {
        if (!KeyActions.TryGetValue(id, out var action) || !action.Can() || RefusedWhileDragging(id))
            return false;

        // Action faults become a status line and a log entry, never a crash.
        DevFlightRecorder.RecordInput("action", id);
        RunGuarded(id, action.Run);
        return true;
    }

    private bool CanRunAction(string id) => KeyActions.TryGetValue(id, out var action) && action.Can();

    /// <summary>Caps Lock toggles snapping, like the timeline Snap button.</summary>
    private void ToggleSnap()
    {
        _viewModel.SnapClips = !_viewModel.SnapClips;
        _viewModel.StatusText = _viewModel.SnapClips
            ? "Snap is on: a dragged clip's edges catch on the other clips, the markers and the playhead."
            : "Snap is off: clips are dragged freely.";
    }

    /// <summary>
    /// Undo/redo from any source (key, mouse button, dial): the focused text box's while its typing is newest, else the timeline's.
    /// Never while a drop-down is open or a drag is active (timeline drag or left button held anywhere), since the drag would
    /// continue from the undone state.
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

    /// <summary>Ripple Delete (default R, Shift+Delete): removes and closes the gap.</summary>
    private void RippleDeleteSelected()
    {
        // Multi-selection: ripple-delete all of them in one Undo step.
        if (GroupHolds)
        {
            LetGoOfDrag();
            if (_viewModel.DeleteClips(_viewModel.SelectedClips, ripple: true))
                ClearClipSelection();
            ScheduleClipRedraw();
            return;
        }

        if (_selectedClip is Layer { HasTiming: true } selected)
        {
            if (_viewModel.RippleDelete(selected))
                ClearClipSelection();
            ScheduleClipRedraw();
        }
        else
        {
            DeleteSelectedClip();
        }
    }

    /// <summary>
    /// Runs the shortcut bound to a key (from MainWindow_PreviewKeyDown, after text-box keys and nudge); true when consumed. The focused
    /// control keeps its own keys (list arrows, rules-grid Delete), and keys whose action cannot run fall through to it.
    /// </summary>
    private bool TryRunBound(string id, InputChord chord, KeyEventArgs e, object sender, IInputElement? focused)
    {
        if (KeybindManager.Action(id) is not { } action || KeyBelongsToFocus(chord, focused))
            return false;

        // Focused Keyframes rows handle their own Ctrl+C / Ctrl+V / Ctrl+A.
        if (focused is KeyLanesView && chord.Mods == ModifierKeys.Control && chord.Key is Key.C or Key.V or Key.A)
            return false;

        if (action.Has(KeyActionFlags.Hold))
        {
            if (!e.IsRepeat)
                BeginHold(chord.Key, id);
            return true;
        }

        // WASD: first press starts zoom/pan, continued per frame while held (NavigationFrame); auto-repeats are swallowed.
        if (action.Has(KeyActionFlags.Continuous))
        {
            if (!e.IsRepeat)
                BeginKeyNavigation();
            return true;
        }

        // During a timeline drag, project-changing keys are consumed as no-ops.
        if (RefusedWhileDragging(id))
            return true;

        if (!CanRunAction(id))
            return false;

        // Auto-repeat only for repeatable actions (stepping, zooming); others run once per press.
        if (e.IsRepeat && !action.Has(KeyActionFlags.Repeat))
            return true;

        if (id == "ui.dial")
            OpenLightningDial(held: false, category: null, over: sender as Window);
        else
            RunAction(id);

        // Caps Lock shortcut: Windows toggled caps on key-down; restore it on key-up (Editor_PreviewKeyUp) so typing is unaffected.
        if (chord.Key == Key.CapsLock)
            _capsToRestore = true;
        return true;
    }

    private bool KeyBelongsToFocus(InputChord chord, IInputElement? focused) => OwnerOfKeys(focused)?.OwnsKey(chord.Key, chord.Mods) == true || chord.Key switch
    {
        // Non-timeline sliders keep their arrow keys (volume, zoom, opacity...).
        Key.Left or Key.Right => focused is Slider slider && !ReferenceEquals(slider, TimelineSlider),
        Key.Home or Key.End => focused is ItemsControl or ListBoxItem or Slider,
        Key.Up or Key.Down when (chord.Mods & ModifierKeys.Alt) != 0 => AltArrowBelongsToFocus(chord.Key, focused),
        // Down on Add Layer opens its kinds instead (AddLayerButton_PreviewKeyDown).
        Key.Up or Key.Down => focused is ItemsControl or ListBoxItem or Slider or ScrollViewer or KeyLanesView or FrameworkElement { Name: "AddLayerButton" },
        Key.PageUp or Key.PageDown => focused is ItemsControl or ListBoxItem or ScrollViewer,
        // Delete belongs to the rules grid, or to keyframes in focused Keyframes rows.
        Key.Delete or Key.Back => focused is DataGridCell or KeyLanesView,
        // Grids use Tab for cell navigation (committing edits).
        Key.Tab => focused is DataGridCell or DataGrid,
        // Grid cells start editing on a typed letter/digit (these are shortcuts elsewhere).
        >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 => focused is DataGridCell or DataGrid,
        _ => false,
    };

    /// <summary>
    /// Nearest focused <see cref="IOwnsKeys"/> (self or ancestor; e.g. the filmstrip cutter, Subtitle Studio) whose keys take precedence
    /// over shortcuts.
    /// </summary>
    internal static IOwnsKeys? OwnerOfKeys(IInputElement? focused)
    {
        for (var node = focused as DependencyObject; node is not null;
             node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is IOwnsKeys owner)
                return owner;
        return null;
    }

    /// <summary>
    /// True when Alt+Up/Down belongs to the focused control: combo boxes keep both; Quick FX, Add Track and the Effects pane's Add keep
    /// Down (their PreviewKeyDown runs after the window's); lists, scrollers and other buttons yield them to clip moves.
    /// </summary>
    internal static bool AltArrowBelongsToFocus(Key key, IInputElement? focused) =>
        focused is ComboBox || (key == Key.Down && focused is ButtonBase { Name: "QuickFxButton" or "AddLayerButton" or "AddButton" });

    // ----- Pre-shortcut keys: Caps Lock echo, Esc routing -----

    /// <summary>Esc targets in priority order: Lightning Dial, tour, active 3D drag, Fullscreen Preview, Interactive Crop, 3D view.</summary>
    internal enum EscapeTaker { None, Dial, Tour, ExplodedDrag, Fullscreen, Crop, Exploded }

    /// <summary>
    /// Pure Esc router (TakeOverlayKey): dial, then tour regardless of focus; then nothing if a text box, drop-down or flyout has focus
    /// (<paramref name="focusKeepsIt"/>); then cancel 3D drag, exit Fullscreen Preview, end Interactive Crop, exit 3D. The last two are
    /// handled later, after text-box keys (EndCropOnEscape, ExplodedWindow_PreviewKeyDown), in that order.
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

    // Caps Lock restore pending on key-up; the synthetic press HandPeg sent, awaiting its echo (with deadline).
    private bool _capsToRestore;
    private int _capsEchoes;
    private long _capsEchoUntil;

    /// <summary>
    /// First step of MainWindow_PreviewKeyDown: swallows HandPeg's own Caps Lock echo (RestoreCapsLock), hides a toggled X-Ray on any key,
    /// and routes Esc to the dial or tour (Interactive Crop's Esc follows text-box keys: EndCropOnEscape). WASD are ordinary shortcuts.
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
            // An active Color Match Reference drag takes Esc first (cancel), before 3D view or Interactive Crop.
            if (Mouse.Captured is ScreenEyedropper { IsDragging: true } dropper)
            {
                dropper.Cancel();
                e.Handled = true;
                return true;
            }

            // Dial, tour, then (unless text box/drop-down/flyout focused) 3D drag and Fullscreen Preview; Interactive Crop and 3D view
            // are handled after text-box keys (EndCropOnEscape, ExplodedWindow_PreviewKeyDown).
            var focused = Keyboard.FocusedElement;
            var focusKeepsIt = focused is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem
                || (focused is DependencyObject element && PresentationSource.FromDependencyObject(element)?.RootVisual is { } root && root is not Window);
            switch (EscapeTakerFor(_dialPopup is { IsOpen: true }, _tour is { IsRunning: true }, focusKeepsIt, _explodedDrag != ExplodedDragKind.None,
                        _fullscreen.IsOpen, _viewModel.IsInteractiveCropActive, _explodedUi))
            {
                case EscapeTaker.Dial:
                    CloseLightningDial();
                    break;
                case EscapeTaker.Tour:
                    _tour?.Stop(finished: true);
                    break;
                case EscapeTaker.ExplodedDrag:
                    EndExplodedDrag(cancelled: true);
                    break;
                case EscapeTaker.Fullscreen:
                    _fullscreen.Exit();
                    break;
                default:
                    return false;
            }

            e.Handled = true;
            return true;
        }

        return TakeSeamKey(e, key);
    }

    // ----- Lane seams: EasyCut 1-9 and Z spring-zoom (Pass 5.6.5 contract) -----
    // Optional partial methods: one MainWindow part implements each; until then the call compiles away. They run from TakeOverlayKey,
    // ahead of bound shortcuts (Z is Previous Edit Point; digits may be bound), for unmodified keys only (never while typing, nor a key the
    // focused control owns), and set handled only when they consume the key (otherwise it continues to its shortcut).

    /// <summary>EasyCut (live multi-angle switcher): angle 1-9 pressed (top row or keypad); consumed only while EasyCut is armed.</summary>
    partial void TakeEasyCutKey(int angle, bool repeat, ref bool handled);

    /// <summary>
    /// Spring-zoom: Z pressed; consumed only during a drag that spring-zooms (clip move, trim, fade, keyframe, ducking), as the 300 ms
    /// dwell would.
    /// </summary>
    partial void TakeSpringZoomKey(bool repeat, ref bool handled);

    /// <summary>An EasyCut angle: digits 1-9 on the top row or the keypad; null for any other key.</summary>
    internal static int? EasyCutAngleOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    /// <summary>Offers an unmodified EasyCut digit or Z to its seam (TakeOverlayKey); true when consumed.</summary>
    private bool TakeSeamKey(KeyEventArgs e, Key key)
    {
        var focused = Keyboard.FocusedElement;
        if (Keyboard.Modifiers != ModifierKeys.None || focused is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem
            || KeyBelongsToFocus(KeybindManager.ChordOf(e), focused))
            return false;

        var handled = false;
        if (EasyCutAngleOf(key) is { } angle)
            TakeEasyCutKey(angle, e.IsRepeat, ref handled);
        else if (key == Key.Z)
            TakeSpringZoomKey(e.IsRepeat, ref handled);
        if (handled)
            e.Handled = true;
        return handled;
    }

    /// <summary>
    /// Unclaimed Esc ends Interactive Crop (it has no toggle besides the clip menu), unless a text box, drop-down or flyout Popup has focus.
    /// </summary>
    private bool EndCropOnEscape(KeyEventArgs e, IInputElement? focused)
    {
        if (KeybindManager.KeyOf(e) != Key.Escape || Keyboard.Modifiers != ModifierKeys.None || !_viewModel.IsInteractiveCropActive)
            return false;
        if (focused is DependencyObject element && PresentationSource.FromDependencyObject(element) is { } source
            && !ReferenceEquals(source, PresentationSource.FromVisual(this)))
            return false;
        _viewModel.IsInteractiveCropActive = false;
        e.Handled = true;
        return true;
    }

    // Sends a synthetic Caps Lock press after key-up to undo Windows' toggle; its echo is swallowed by TakeOverlayKey (1 s deadline).
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

    // ----- Text-box focus release -----

    /// <summary>
    /// A press outside the focused text box clears its focus (edits commit as on normal blur) so subsequent keys are shortcuts; the
    /// timeline, Canvas and player never take focus, so a lingering text box would otherwise swallow them. Presses on text boxes,
    /// drop-downs or inside popups are left alone.
    /// </summary>
    private static void ReleaseTypingFocus(DependencyObject? pressed)
    {
        if (Keyboard.FocusedElement is not (TextBoxBase or PasswordBox) || Keyboard.FocusedElement is not DependencyObject box)
            return;

        // Map a Run to its containing element.
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

        // Clear logical focus in its scope (fires LostFocus, committing LostFocus bindings), then keyboard focus, so keys go to the window.
        if (FocusManager.GetFocusScope(box) is { } scope)
            FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }

    /// <summary>
    /// Same release for a focused <see cref="IOwnsKeys"/> pane (cutter, Project Media list, Subtitle Studio) on a press outside it;
    /// otherwise Space, J/K/L, I/O, arrows, Enter and Delete stay with the pane after clicking the timeline or player. Presses inside the
    /// pane or a popup are ignored.
    /// </summary>
    internal static void ReleaseOwnedKeys(DependencyObject? pressed)
    {
        if (Keyboard.FocusedElement is not DependencyObject focused || OwnerOfKeys(Keyboard.FocusedElement) is not DependencyObject owner)
            return;

        while (pressed is not null and not Visual)
            pressed = LogicalTreeHelper.GetParent(pressed);

        DependencyObject? top = null;
        for (var at = pressed; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, owner))
                return;
            top = at;
        }

        if (top is not Window)
            return;

        // Clear scope focus too, or the next window activation would restore focus to it.
        if (FocusManager.GetFocusScope(focused) is { } scope)
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

        // Release text-box focus on press, except side buttons (Mouse 4 = Undo, which must still undo the box's newest typing).
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right or MouseButton.Middle)
        {
            ReleaseTypingFocus(e.OriginalSource as DependencyObject);
            ReleaseOwnedKeys(e.OriginalSource as DependencyObject);
        }

        // Presses on the video hit the non-activating overlay: activate the player's host window so shortcuts reach it.
        if (ReferenceEquals(sender, VideoView.OverlayWindow) && VideoView.OverlayWindow?.Owner is { IsActive: false } owner)
            owner.Activate();

        // A dial open without mouse capture closes on any outside press.
        if (_dialPopup is { IsOpen: true })
            CloseLightningDial();

        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                return;

            // Right button held over a surface with a navigation key down steers that surface (release opens no menu). Record the
            // surface; the press proceeds normally.
            case MouseButton.Right:
                BeginNavigationPress(sender, e.OriginalSource);
                return;
        }

        if (KeybindManager.Resolve(KeybindManager.ChordOf(e.ChangedButton)) is not { } id)
            return;

        e.Handled = true;
        if (id == "ui.dial")
        {
            // Not during a timeline drag (dial actions could change the project under it).
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
            // Release after navigating opens no menu. A still-held key keeps steering its surface; otherwise the navigation ends.
            if (_navEngaged)
            {
                e.Handled = true;
                ReleaseNavigation();
            }

            if (!_navTicking)
                EndNavigation();
            return;
        }

        // The dial's hold button released outside the dial's hearing.
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

    // ----- Held keys: X-Ray (Tab) and Before/After -----

    private Key _holdKey;
    private string? _holdAction;
    private DispatcherTimer? _holdTimer;

    /// <summary>Tab hold time before X-Ray shows; a shorter tap performs normal Tab navigation.</summary>
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(250);

    private void BeginHold(Key key, string id)
    {
        // End the other hold first (X-Ray vs Before/After) so neither is left stuck on.
        if (_holdKey != Key.None && _holdKey != key)
        {
            if (_holdAction == "view.compareGrade")
                EndGradeHold();
            else
                HideXRay();
        }

        (_holdKey, _holdAction) = (key, id);

        // Grade Before/After: immediate on press, off on release (no tap/hold distinction).
        if (id == "view.compareGrade")
        {
            _holdTimer?.Stop();
            BeginGradeHold();
            return;
        }

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

        // Caps Lock ran a shortcut on key-down: restore the caps state now.
        if (key == Key.CapsLock && _capsToRestore)
        {
            _capsToRestore = false;
            RestoreCapsLock();
            return;
        }

        if (_holdKey == Key.None || key != _holdKey)
            return;

        if (_holdAction == "view.compareGrade")
        {
            (_holdKey, _holdAction) = (Key.None, null);
            e.Handled = true;
            EndGradeHold();
            return;
        }

        var tapped = _holdTimer is { IsEnabled: true };
        _holdTimer?.Stop();
        _holdKey = Key.None;
        e.Handled = true;
        if (!tapped)
        {
            HideXRay();
            return;
        }

        // Quick Tab tap: normal focus navigation.
        if (key == Key.Tab)
        {
            var request = new TraversalRequest(FocusNavigationDirection.Next);
            if (Keyboard.FocusedElement is UIElement focused)
                focused.MoveFocus(request);
            else
                (sender as UIElement)?.MoveFocus(request);
        }
    }

    // ----- Held navigation keys (WASD) -----

    private enum NavSurface { None, Timeline, Canvas, Player }

    // Per-second rates (x3 with Shift): timeline zoom factor and pan in view widths; Canvas/player zoom factor (max 16x) and pan in DIPs.
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
    /// Right-button press: records the surface so a navigation key during the hold steers it (and suppresses its menu). With a key
    /// already held, a press on another surface transfers navigation to it; a press on no surface changes nothing.
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
    /// Navigation key down: steers the surface under the pointer (timeline, Scrubber, keyframe lanes, Canvas, player; default timeline),
    /// or the right-button-held surface. Additional keys steer the same surface as the first.
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

    // Surface under the pointer: player (video overlay), timeline or Canvas; default timeline (like = and -).
    private NavSurface SurfaceUnderPointer()
    {
        var over = Mouse.DirectlyOver as DependencyObject;
        if (over is not null && VideoView.OverlayWindow is { } overlay && ReferenceEquals(Window.GetWindow(over), overlay))
            return PlayerNavigable ? NavSurface.Player : NavSurface.Timeline;

        return SurfaceAt(over) is var surface and not NavSurface.None ? surface : NavSurface.Timeline;
    }

    private bool PlayerNavigable => _gpu is not null && VideoView.IsVisible;

    // Surface hit by a press: timeline (pane, Scrubber, keyframe lanes), Canvas or player.
    private NavSurface SurfaceAt(DependencyObject? source)
    {
        // Map a Run to its containing element.
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

        // Suppress the timeline's context menu: the press belongs to navigation (release handled here).
        _navTimeline?.CancelPendingMenu();

        if (_navSurface == NavSurface.Player)
            _navPicture = PictureSize();

        // Capture the mouse to the press's window until release.
        if (_navWindow is { } window && Mouse.Captured is null)
            window.CaptureMouse();
    }

    private void EndNavigation()
    {
        StopNavigationTicker();
        ReleaseNavigation();
        (_navSurface, _navTimeline) = (NavSurface.None, null);
    }

    // End the right-button navigation hold and release its capture.
    private void ReleaseNavigation()
    {
        if (_navEngaged && _navWindow is { IsMouseCaptured: true } window)
            window.ReleaseMouseCapture();
        (_navEngaged, _navWindow) = (false, null);
    }

    // Per-frame navigation while any key is held; unsubscribes when none is.
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
        // A right-button hold ends on release; any navigation ends when a text box or dialog takes focus (keys become typing).
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
            // All keys released: key navigation ends; a right-button hold waits for the next key.
            if (_navEngaged)
                StopNavigationTicker();
            else
                EndNavigation();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            seconds *= NavFast;

        // In 3D over the player: W/S dolly, A/D orbit.
        if (_navSurface == NavSurface.Player && ExplodedNavigates(zoom, pan, seconds))
            return;

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

    // Pan over a fitted Canvas/player (nothing to pan) falls through to the timeline when that setting is on.
    private static bool PansTimelineInstead(double scale) => scale <= 1.0001 && AppSettings.Current.PanTimelineWhenNotZoomed;

    // Timeline navigation: zoom about the pointer time (the playhead when off the surface) and pan.
    private void NavigateTimeline(int zoom, int pan, double seconds)
    {
        // Key navigation cancels any running coast.
        StopTimelineMomentum();
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

    // Canvas navigation: a RenderTransform on the canvas only (clipped by its well; box drags use canvas coordinates, so stay
    // correct), never below fit, always covering the well.
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

        // Pan left reveals content to the left: translate the picture right.
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

    // Player navigation: the engine zooms/pans the picture (no scene replan); overlay boxes, crop and safe margins follow via the same
    // matrix on the overlay (their math stays in overlay coordinates).
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
        if (_gpu is null || width <= 0 || height <= 0 || picture.Width <= 0 || picture.Height <= 0)
            return;

        var scale = Math.Pow(2, _playerZoom);
        if (zoom != 0)
        {
            // Zoom about the pointer: the centre moves away from (or toward) it by the zoom factor.
            var next = Math.Clamp(scale * Math.Pow(NavZoomRate, zoom * seconds), 1, NavMaxZoom);
            var at = VideoView.OverlayWindow is { } overlay ? Mouse.GetPosition(overlay) : new Point(width / 2, height / 2);
            var middle = new Point(width / 2 + _playerPanX * picture.Width * scale, height / 2 + _playerPanY * picture.Height * scale);
            var moved = at + (middle - at) * (next / scale);
            scale = next;
            (_playerPanX, _playerPanY) = ((moved.X - width / 2) / (picture.Width * scale), (moved.Y - height / 2) / (picture.Height * scale));
        }

        if (pan != 0)
            _playerPanX -= pan * NavPanSpeed * seconds / (picture.Width * scale);

        // Clamp the pan so the picture always covers its fitted rectangle.
        var reach = (scale - 1) / (2 * scale);
        (_playerPanX, _playerPanY) = (Math.Clamp(_playerPanX, -reach, reach), Math.Clamp(_playerPanY, -reach, reach));
        _playerZoom = Math.Log2(scale);
        ApplyPlayerView();
    }

    private void ApplyPlayerView()
    {
        _gpu?.SetView(_playerZoom, _playerPanX, _playerPanY);
        var scale = Math.Pow(2, _playerZoom);
        var matrix = Matrix.Identity;
        matrix.ScaleAt(scale, scale, PlayerOverlay.ActualWidth / 2, PlayerOverlay.ActualHeight / 2);
        matrix.Translate(_playerPanX * _navPicture.Width * scale, _playerPanY * _navPicture.Height * scale);
        PlayerView.Matrix = matrix;
        _loopback.PlaceLoopbackWipe();
        PlaceGradeWipe();
    }

    // Fitted picture rect in the player: output frame (source size if none), letterboxed.
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
        _gpu?.SetView(0, 0, 0);
        _loopback.PlaceLoopbackWipe();
        PlaceGradeWipe();
        if (_canvasView is { } canvas)
            canvas.Matrix = Matrix.Identity;
    }

    // ----- The Lightning Dial -----

    private Popup? _dialPopup;
    private LightningDialOverlay? _dial;

    /// <summary>
    /// Opens the dial at the pointer (window centre if outside); <paramref name="held"/> while the opening button is down; optionally
    /// at a category's level for its key.
    /// </summary>
    private void OpenLightningDial(bool held, int? category, Window? over)
    {
        // Over the video, host the dial in the player's window.
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
            popup = _dialPopup = new Popup { AllowsTransparency = true, StaysOpen = true, Focusable = false, Child = dial };
            UiScale.Track(popup);
        }

        var at = Mouse.GetPosition(root);
        if (at.X < 0 || at.Y < 0 || at.X > root.ActualWidth || at.Y > root.ActualHeight)
            at = new Point(root.ActualWidth / 2, root.ActualHeight / 2);

        CloseLightningDial();
        if (!held)
            _dialButton = null;
        UiScale.PlaceRelative(popup, root, at.X - dial.Width / 2, at.Y - dial.Height / 2);
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

    /// <summary>Toggled from the dial: shown until the next key or click.</summary>
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
            _xrayPopup = new Popup { AllowsTransparency = true, StaysOpen = true, Focusable = false, IsHitTestVisible = false, Child = _xray };
            UiScale.Track(_xrayPopup);
            _xrayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _xrayTimer.Tick += (_, _) => RefreshXRay();
        }

        _xrayToggled = toggled;
        if (_xrayPopup.IsOpen)
            return;

        UiScale.PlaceRelative(_xrayPopup, root, 0, 0);
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

    // ----- X-Ray key map over the player -----
    //
    // Keyboard/mouse map in its own hit-testable popup (the X-Ray drawing itself passes clicks through): hovering a key names its
    // action; clicking a key or "Edit Keybinds" opens Settings at Keybinds & Lightning Dial on that key. Rebuilt from current bindings.

    private Popup? _keyGlancePopup;

    private void ShowKeyGlance(FrameworkElement root)
    {
        // Over the player pane if visible and wide enough; else window centre.
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

        if (_keyGlancePopup is null)
        {
            _keyGlancePopup = new Popup { AllowsTransparency = true, StaysOpen = true, Focusable = false };
            UiScale.Track(_keyGlancePopup);
        }

        _keyGlancePopup.Child = map;
        map.Height = height;
        UiScale.PlaceRelative(_keyGlancePopup, root, area.X + (area.Width - width) / 2, area.Y + Math.Max((area.Height - height) / 2, 8));
        _keyGlancePopup.IsOpen = true;
    }

    // UI frame time shown in X-Ray: interval between WPF render ticks.
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

        // Outline every realized, visible pane in this window with its id.
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
        var lines = new List<string>(8)
        {
            "X-Ray",
            frame > 0 ? FormattableString.Invariant($"UI frame   {frame:0.0} ms ({1000 / frame:0} fps)") : "UI frame   idle",
            FormattableString.Invariant($"Memory     {Environment.WorkingSet / Megabyte:0} MB working set, {GC.GetTotalMemory(false) / Megabyte:0} MB managed"),
            FormattableString.Invariant($"Work       {lanes[0]} foreground, {lanes[1]} interactive, {lanes[2]} background, {lanes[3]} idle"),
            _gpu is { } gpu
                ? FormattableString.Invariant($"Player     GPU engine {(gpu.IsPlaying ? "playing" : "paused")} at {TimeDisplay.Format(gpu.Position)}; cache {gpu.CacheStats.Frames} frames, {gpu.CacheStats.Bytes / Megabyte:0} of {gpu.CacheStats.BudgetBytes / Megabyte:0} MB, {gpu.CacheStats.Hits} hits, {gpu.CacheStats.Misses} misses, last exact {gpu.CacheStats.LastExactLatencyMs:0.0} ms")
                : "Player     not started",
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

        // Shortly after Frame 1, once settled (inactive panes realize at idle first).
        var wait = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1.5) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            OfferTourWhenFree();
        };
        wait.Start();
    }

    /// <summary>
    /// Auto-starts the tour once no dialog is open (autosave restore, crash report, engine download first) and HandPeg is foreground
    /// (waits for reactivation). Marked seen on start, so quitting mid-tour never re-offers it.
    /// </summary>
    private void OfferTourWhenFree()
    {
        if (AppSettings.Current.QuickStartTourDone || !IsLoaded)
            return;

        // Requires FFmpeg: wait for its install (first-run window or Settings), then apply the same offer rules.
        if (!DependencyUpdater.EnginesInstalled)
        {
            OfferTourOnceEnginesAreIn();
            return;
        }

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

    // One-shot wait for engine install completion; cancelled when the window closes.
    private bool _tourWaitsForEngines;

    private void OfferTourOnceEnginesAreIn()
    {
        if (_tourWaitsForEngines)
            return;

        _tourWaitsForEngines = true;
        void Installed(string tool) => Dispatcher.BeginInvoke(() =>
        {
            if (!_tourWaitsForEngines || !DependencyUpdater.EnginesInstalled)
                return;

            Stop();
            Dispatcher.BeginInvoke(OfferTourWhenFree, DispatcherPriority.ApplicationIdle);
        });
        void Closing(object? sender, EventArgs e) => Stop();
        void Stop()
        {
            _tourWaitsForEngines = false;
            DependencyUpdater.Installed -= Installed;
            Closed -= Closing;
        }

        DependencyUpdater.Installed += Installed;
        Closed += Closing;
    }

    /// <summary>
    /// Shows the Quick-Start Tour from the first card (auto-offered once, or from Settings, starting after Settings closes). Finish or
    /// Skip marks it done.
    /// </summary>
    public void StartQuickStartTour() => TryStartQuickStartTour();

    // True if the tour started now (deferred while Settings is open, or the window is hidden or minimized).
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

        AppSettings.Update(settings => settings.QuickStartTourDone = true);
    }

    private List<QuickStartTour.Step> TourSteps()
    {
        static string Keys(string id, string otherwise) => KeybindManager.GestureText(id) is { Length: > 0 } keys ? keys : otherwise;
        var dial = Keys("ui.dial", "");
        var dialText = dial == "Mouse5" ? "the mouse's forward side button (Mouse 5)" : dial.Length > 0 ? dial : "its key (set it in Settings)";
        var (first, last) = (Keys("ui.dial1", ""), Keys("ui.dial8", ""));
        var slices = first.Length > 0 && last.Length > 0 ? $" ({first} to {last} run its eight slices at once)" : "";
        var xray = Keys("ui.xray", "");

        return
        [
            new("Add Your Media",
                "Click Add Media, or drop videos, pictures and sounds anywhere on HandPeg: each one lands on the timeline at the playhead, "
                + "ready to cut, stack and arrange.",
                () => DropHint.IsVisible ? DropHint : AddMediaHost),
            new("The Timeline",
                $"Every track is here, with each clip's sound on its Sub-Lanes and the captions under it. Drag a clip to move it, drag its edges to trim, "
                + $"and press {Keys("edit.split", "the split key")} to split at the playhead. Hold {Keys("nav.forward", "the zoom-in key")} or "
                + $"{Keys("nav.back", "the zoom-out key")} to zoom and {Keys("nav.left", "the pan-left key")} or {Keys("nav.right", "the pan-right key")} "
                + "to pan the timeline, or the Canvas or the player under the pointer; "
                + $"{Keys("transport.stepBack", "Left")} and {Keys("transport.stepForward", "Right")} step a frame.",
                () => UnifiedTimelinePanel),
            new("Arrange and Add Looks",
                "The Canvas places every clip on the output frame; Effects adds looks and sound cleanup. Every one-click preset leaves all of "
                + "its settings here for you to change.",
                () => LayoutPane.IsVisible ? LayoutPane : EffectsPanePanel),
            new("Your Keys and the Lightning Dial",
                $"Settings holds every shortcut and the Lightning Dial: press {dialText} anywhere for a ring of actions under the pointer{slices}."
                + (xray.Length > 0 ? $" {xray} for X-Ray, a look at every pane and what the editor is doing." : "")
                + " You can see this tour again from Settings.",
                () => SettingsHost),
        ];
    }
}
