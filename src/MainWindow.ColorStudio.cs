using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegApp;

// ----- Color tab (Color Studio) and grade Before/After on the player -----
// The Color Studio is wired when its tab is realized (RealizeTabContent); the Effects pane's Color Grade card opens it. Before/After:
// holding \ bypasses the grade immediately until release; the Compare eye splits the player at a draggable divider (Before left, After
// right); the dial's and hotbar slot's Before/After toggle Bypass. The engine renders it (GpuPreviewEngine.SetGradeCompare) on player
// frames only.
public partial class MainWindow
{
    /// <summary>Color tab pane id (TabItem header "Color"; legacy saved "Tab:Filters" maps to it via ReplacedPanes).</summary>
    internal const string ColorTabId = "Tab:Color";

    private ColorStudioPane? _colorStudio;
    private bool _gradeSplit, _gradeHeld, _gradeToggled;
    private double _gradeWipe = 0.5;
    private Canvas? _gradeWipeLayer;
    private Border? _gradeWipeHandle, _gradeDivider;
    private readonly MatrixTransform _gradeWipeUnzoom = new();

    // Event-driven placement (Pass 5.6.5, replaces a 500 ms re-check timer): hooked once, the first time a split is requested; every
    // trigger coalesces into one queued PlaceGradeWipe.
    private bool _gradeWipeHooked, _gradeWipeQueued;
    private HwndSource? _gradeWipeSource;

    private void AttachColorStudio(ColorStudioPane pane)
    {
        _colorStudio = pane;
        pane.Attach(_viewModel, CaptureForColorStudio, target => CreateColorMatchBar(target));
        pane.CompareToggled += on =>
        {
            _gradeSplit = on;
            ApplyGradeCompare();
        };
        pane.ShowCompare(_gradeSplit);
        HookCurveSampling();
    }

    /// <summary>
    /// Small paused-player frame with the given Color Grade card excluded (Guid.Empty: as displayed), for the Color Studio histogram.
    /// A pending save or tab still goes first (as in CaptureForScopes), since the engine holds one request.
    /// </summary>
    private async Task<(byte[] Bgra, int Width, int Height)?> CaptureForColorStudio(Guid card)
    {
        for (var waited = 0; _gpu is { IsThumbnailWaiting: true } && waited < 1000; waited += 50)
            await Task.Delay(50);
        if (_gpu is not { IsThumbnailWaiting: false } engine)
            return null;
        return card == Guid.Empty ? await engine.CaptureThumbnailAsync(256) : await engine.CaptureInputThumbnailAsync(card, 256);
    }

    /// <summary>
    /// Effects pane's Open Color Studio: shows the Color tab (un-hiding it if needed) and activates it where it lives: its side window, its
    /// main-window group, or, when no visible group holds it, a new Pane Docker window.
    /// </summary>
    private void OpenColorStudio()
    {
        if (!TabWanted(ColorTabId))
            SetTabShown(ColorTabId, true);

        // Run after the un-hide layout pass (at BringForward's Loaded priority).
        Dispatcher.BeginInvoke(() =>
        {
            if (_dock.SideIdOf(ColorTabId) is { } side)
            {
                _dock.BringSideWindowForward(side);
                _dock.Activate(ColorTabId);
            }
            else if (_dock.IsInShownGroup(ColorTabId))
            {
                _dock.BringForward(ColorTabId);
            }
            else
            {
                _dock.MoveToWindow(ColorTabId, side: true);
            }
        }, DispatcherPriority.Loaded);
    }

    // ----- Before/After -----

    /// <summary>Current compare mode: Bypass when held or toggled, else Split when the Compare eye is on, else Off.</summary>
    internal GradeCompareMode GradeCompareNow => _gradeHeld || _gradeToggled ? GradeCompareMode.Bypass : _gradeSplit ? GradeCompareMode.Split : GradeCompareMode.Off;

    /// <summary>\ pressed (view.compareGrade Hold action): bypass the grade until release.</summary>
    private void BeginGradeHold()
    {
        if (_gradeHeld)
            return;
        _gradeHeld = true;
        ApplyGradeCompare();
    }

    /// <summary>\ released, or keyboard focus lost (DropTransientInput): restore the previous compare state.</summary>
    private void EndGradeHold()
    {
        if (!_gradeHeld)
            return;
        _gradeHeld = false;
        ApplyGradeCompare();
    }

    /// <summary>Dial or hotbar Before/After: toggles grade Bypass.</summary>
    private void ToggleGradeBypass()
    {
        _gradeToggled = !_gradeToggled;
        ApplyGradeCompare();
        _viewModel.StatusText = _gradeToggled ? "Before: the grade is off." : "After: the grade is on.";
    }

    private void ApplyGradeCompare()
    {
        _gpu?.SetGradeCompare(GradeCompareNow, _gradeWipe);
        _colorStudio?.ShowCompare(_gradeSplit);
        PlaceGradeWipe();
    }

    // ----- Split divider handle on the player -----

    private void EnsureGradeWipe()
    {
        if (_gradeWipeLayer is not null)
            return;

        // Grip and divider line drawn in WPF (the engine splits the picture but draws no line).
        var handle = _gradeWipeHandle = UiBrushes.WipeHandle("Drag: Before on the left, After on the right", "Before/After Divider");
        handle.MouseLeftButtonDown += (_, e) =>
        {
            handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (handle.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _gradeWipeLayer is { } layer)
                DragGradeWipe(e.GetPosition(layer).X);
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            handle.ReleaseMouseCapture();
            e.Handled = true;
        };
        handle.TouchDown += (_, e) =>
        {
            handle.CaptureTouch(e.TouchDevice);
            e.Handled = true;
        };
        handle.TouchMove += (_, e) =>
        {
            if (handle.AreAnyTouchesCaptured && _gradeWipeLayer is { } layer)
            {
                DragGradeWipe(e.GetTouchPoint(layer).Position.X);
                e.Handled = true;
            }
        };
        handle.TouchUp += (_, e) =>
        {
            handle.ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        };

        _gradeDivider = UiBrushes.WipeDivider();
        _gradeWipeLayer = new Canvas { ClipToBounds = true, Visibility = Visibility.Collapsed, RenderTransform = _gradeWipeUnzoom };
        _gradeWipeLayer.Children.Add(_gradeDivider);
        _gradeWipeLayer.Children.Add(handle);
        PlayerOverlay.Children.Add(_gradeWipeLayer);
    }

    /// <summary>Moves the divider to <paramref name="x"/> (handle layer, player units) using the same span and formula as line placement.</summary>
    private void DragGradeWipe(double x)
    {
        var (viewWidth, viewHeight) = _loopback.LoopbackViewSize();
        if (_gpu is not { } engine || viewWidth < 1)
            return;
        var span = engine.PictureSpan(viewWidth, viewHeight);
        if (span.Width <= 1)
            return;
        _gradeWipe = GpuPreviewEngine.WipeAt(x, span);
        ApplyGradeCompare();
    }

    // ----- Grade wipe triggers: layout, size, view transform, DPI, 3D, overlay modes, Live Encoded Preview, frame size, selection -----
    // PlaceGradeWipe's inputs change independently of the compare state: the player's size (SizeChanged), zoom and pan (ApplyPlayerView and
    // ResetNavigationViews call it), DPI (the overlay's HwndSource, re-hooked when the player moves to another window), 3D Exploded View (the
    // exploded layer turns hit-testable on entry and back once the engine draws flat again), Interactive Crop / Draw Target, Live Encoded
    // Preview and the output frame size (view-model properties), the canvas the engine letterboxes (each scene-plan push) and the selection.
    // Each trigger only queues one placement, and only while a split is requested or the handle is still showing.

    private void HookGradeWipeTriggers()
    {
        if (_gradeWipeHooked)
            return;
        _gradeWipeHooked = true;
        PlayerOverlay.SizeChanged += (_, _) => QueueGradeWipe();
        ExplodedLayer.IsHitTestVisibleChanged += (_, _) => QueueGradeWipe();
        _viewModel.PropertyChanged += GradeWipe_ViewModelChanged;
        _viewModel.ClipSelectionChanged += QueueGradeWipe;
        _gpuStateTimer.Tick += (_, _) => QueueGradeWipe();
        PresentationSource.AddSourceChangedHandler(PlayerOverlay, (_, e) =>
        {
            HookGradeWipeDpi(e.NewSource as HwndSource);
            QueueGradeWipe();
        });
        HookGradeWipeDpi(PresentationSource.FromVisual(PlayerOverlay) as HwndSource);
    }

    // The player overlay's window: its DPI change re-rounds the handle and divider to the new device pixels.
    private void HookGradeWipeDpi(HwndSource? source)
    {
        if (ReferenceEquals(source, _gradeWipeSource))
            return;
        if (_gradeWipeSource is { } old)
            old.DpiChanged -= GradeWipe_DpiChanged;
        _gradeWipeSource = source;
        if (source is not null)
            source.DpiChanged += GradeWipe_DpiChanged;
    }

    private void GradeWipe_DpiChanged(object? sender, HwndDpiChangedEventArgs e) => QueueGradeWipe();

    private void GradeWipe_ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsExportQualityPreview):
            case nameof(MainViewModel.IsInteractiveCropActive):
            case nameof(MainViewModel.DrawTargetLayer):
            case nameof(MainViewModel.FrameWidth):
            case nameof(MainViewModel.FrameHeight):
            case nameof(MainViewModel.HasSource):
                QueueGradeWipe();
                break;
        }
    }

    /// <summary>
    /// Queues one PlaceGradeWipe after the current dispatcher work (other handlers of the same change, e.g. Live Encoded Preview's engine
    /// switch, run first); idle unless a split is requested or the handle still shows.
    /// </summary>
    private void QueueGradeWipe()
    {
        if (_gradeWipeQueued || (GradeCompareNow != GradeCompareMode.Split && _gradeWipeLayer is not { Visibility: Visibility.Visible }))
            return;
        _gradeWipeQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _gradeWipeQueued = false;
            PlaceGradeWipe();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Positions handle and divider on the engine's split line (device-pixel rounded, 2 px wide), only while splitting; hidden in the 3D
    /// view, Live Encoded Preview or a source view, where the engine shows plain Before. Called directly on compare, zoom and pan changes,
    /// and queued by the triggers above.
    /// </summary>
    private void PlaceGradeWipe()
    {
        var splitting = GradeCompareNow == GradeCompareMode.Split;
        if (splitting)
            HookGradeWipeTriggers();

        var (viewWidth, viewHeight) = _loopback.LoopbackViewSize();
        // 3D: from entry until the engine draws flat again (the exploded layer is hit-testable exactly that long, and its change is a trigger).
        var shown = splitting && !_explodedUi && !ExplodedLayer.IsHitTestVisible && _gpu is { LoopbackOn: false } && CurrentOverlayMode == OverlayMode.None
            && viewWidth > 1 && viewHeight > 1;
        if (!shown)
        {
            if (_gradeWipeLayer is not null)
                _gradeWipeLayer.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureGradeWipe();
        _gradeWipeLayer!.Visibility = Visibility.Visible;
        var view = PlayerOverlay.RenderTransform?.Value ?? Matrix.Identity;
        if (view.HasInverse)
            view.Invert();
        else
            view = Matrix.Identity;
        _gradeWipeUnzoom.Matrix = view;

        var x = GpuPreviewEngine.WipeLineX(_gradeWipe, _gpu!.PictureSpan(viewWidth, viewHeight));
        var scale = VisualTreeHelper.GetDpi(PlayerOverlay).DpiScaleX;
        scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        _gradeWipeHandle!.Height = PlayerOverlay.ActualHeight;
        Canvas.SetLeft(_gradeWipeHandle, Math.Round((x - _gradeWipeHandle.Width / 2) * scale) / scale);
        Canvas.SetTop(_gradeWipeHandle, 0);

        var (_, top, _, height) = _gpu.PictureRect(viewWidth, viewHeight);
        var (from, to) = (Math.Max(top, 0), Math.Min(top + height, viewHeight));
        _gradeDivider!.Width = 2 / scale;
        _gradeDivider.Height = Math.Max(to - from, 0);
        Canvas.SetLeft(_gradeDivider, (Math.Round(x * scale) - 1) / scale);
        Canvas.SetTop(_gradeDivider, from);
    }

    // ----- Ctrl + Hover / Ctrl + Click on the player: tonal pips and curve points in the Color Studio (Pass 5.6.4B) -----
    // While the Color tab is shown, Ctrl + Hover over the picture shows the pixel's Y/R/G/B pips on the curves (ColorStudioPane.SampleAt: the
    // pre-grade still graded through the card's other stages, i.e. the value entering the curves); Ctrl + Click drops a point on the active
    // curve (luma on Master, its own channel on Red/Green/Blue) and Ctrl + Shift + Click matched points on all four, one Undo step either way
    // (user decisions). The picture is read from the Color Studio's last paused still: no capture, no GPU or decoder work per move (Law 3).

    /// <summary>
    /// Which curves a click feeds: null without Ctrl (the player keeps the click), false (Ctrl) the active curve, true (Ctrl + Shift) all
    /// four. Pure; golden-tested.
    /// </summary>
    internal static bool? CurveClickScope(ModifierKeys modifiers) => modifiers switch
    {
        ModifierKeys.Control => false,
        ModifierKeys.Control | ModifierKeys.Shift => true,
        _ => null,
    };

    /// <summary>True while the player's Ctrl gestures belong to the Color Studio curves: its tab shown, no 3D, crop or source view.</summary>
    private bool CurveSamplingLive => _colorStudio is { IsVisible: true } && !_explodedUi && !_viewModel.IsInteractiveCropActive && _viewModel.DrawTargetLayer is null;

    /// <summary>Hooks the player overlay (AttachColorStudio): named handlers removed first, so hooking again never doubles them.</summary>
    private void HookCurveSampling()
    {
        PlayerOverlay.PreviewMouseMove -= CurveSampling_MouseMove;
        PlayerOverlay.PreviewMouseMove += CurveSampling_MouseMove;
        PlayerOverlay.MouseLeave -= CurveSampling_MouseLeave;
        PlayerOverlay.MouseLeave += CurveSampling_MouseLeave;
        PlayerOverlay.PreviewMouseLeftButtonDown -= CurveSampling_MouseDown;
        PlayerOverlay.PreviewMouseLeftButtonDown += CurveSampling_MouseDown;
    }

    private void CurveSampling_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_colorStudio is not { } pane)
            return;
        var sample = CurveSamplingLive && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.LeftButton != MouseButtonState.Pressed
            && PictureFocalAt(e.GetPosition(PlayerOverlay)) is { } focal ? pane.SampleAt(focal) : null;
        pane.ShowTonalPips(sample);
    }

    private void CurveSampling_MouseLeave(object? sender, MouseEventArgs e) => _colorStudio?.ShowTonalPips(null);

    private void CurveSampling_MouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (_colorStudio is not { } pane || e.ClickCount != 1 || CurveClickScope(Keyboard.Modifiers) is not { } allChannels || !CurveSamplingLive)
            return;
        if (PictureFocalAt(e.GetPosition(PlayerOverlay)) is { } focal && pane.SampleAt(focal) is { } sample && pane.AddCurvePoints(sample, allChannels))
            e.Handled = true;
    }
}
