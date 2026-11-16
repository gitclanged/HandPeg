using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;
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
    private DispatcherTimer? _gradeWipeRecheck;

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
        PlayerOverlay.SizeChanged += (_, _) => PlaceGradeWipe();
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

    /// <summary>
    /// Positions handle and divider on the engine's split line (device-pixel rounded, 2 px wide), only while splitting; hidden in the 3D
    /// view, Live Encoded Preview or a source view, where the engine shows plain Before.
    /// </summary>
    private void PlaceGradeWipe()
    {
        // While split is requested, re-check every 500 ms (timer created lazily): 3D view, Live Encoded Preview and source views toggle
        // independently, and the handle must follow.
        var splitting = GradeCompareNow == GradeCompareMode.Split;
        if (splitting && _gradeWipeRecheck is null)
        {
            _gradeWipeRecheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _gradeWipeRecheck.Tick += (_, _) => PlaceGradeWipe();
        }

        if (_gradeWipeRecheck is { } recheck && recheck.IsEnabled != splitting)
            recheck.IsEnabled = splitting;

        var (viewWidth, viewHeight) = _loopback.LoopbackViewSize();
        var shown = splitting && _gpu is { IsDrawingExploded: false, LoopbackOn: false } && CurrentOverlayMode == OverlayMode.None
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
}
