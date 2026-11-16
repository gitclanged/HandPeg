using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.Views;

namespace HandPegApp;

// ----- Pass 5.6A: the Color tab (the Color Studio) and the grade's Before/After on the player -----
// The Color Studio is wired to the window as its tab is made (RealizeTabContent); the Effects pane's Color Grade card opens it. The
// Before/After: \ held shows the picture without its grade (Bypass) at once and until the key is let go; the Color Studio's Compare eye
// splits the player at a divider (Before on the left, After on the right) that is dragged by a handle on the player; the dial's and a
// hotbar slot's Before/After switch the Bypass on and off. The engine draws it (GpuPreviewEngine.SetGradeCompare): only the player's frames.
public partial class MainWindow
{
    /// <summary>The Color tab's pane id (its TabItem's header is "Color"; saved "Tab:Filters" become it: ReplacedPanes).</summary>
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
    /// The paused player's frame without one Color Grade card (its id; none: as shown), small, for the Color Studio's histogram. A save's
    /// or a tab's still waiting goes first (as CaptureForScopes): the engine keeps one request.
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
    /// The Effects pane's Open Color Studio: the Color tab shown (un-hidden when it was), brought forward where it is: its side window to
    /// the front, or its group in the main window; when no group on screen holds it, a Pane Docker of its own.
    /// </summary>
    private void OpenColorStudio()
    {
        if (!TabWanted(ColorTabId))
            SetTabShown(ColorTabId, true);

        // After the layout the un-hiding asked for (BringForward's own priority).
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

    // ----- The Before/After -----

    /// <summary>The compare the player shows now: held or switched on, the plain Before; else the Compare eye's split, else none.</summary>
    internal GradeCompareMode GradeCompareNow => _gradeHeld || _gradeToggled ? GradeCompareMode.Bypass : _gradeSplit ? GradeCompareMode.Split : GradeCompareMode.Off;

    /// <summary>\ pressed (KeybindManager's view.compareGrade, a Hold action): the Before at once, until the key is let go.</summary>
    private void BeginGradeHold()
    {
        if (_gradeHeld)
            return;
        _gradeHeld = true;
        ApplyGradeCompare();
    }

    /// <summary>\ let go, or the window let go of the keyboard (DropTransientInput): the picture as it was.</summary>
    private void EndGradeHold()
    {
        if (!_gradeHeld)
            return;
        _gradeHeld = false;
        ApplyGradeCompare();
    }

    /// <summary>The dial's or a hotbar slot's Before/After: the Before switched on, or off again.</summary>
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

    // ----- The divider's handle on the player (the split) -----

    private void EnsureGradeWipe()
    {
        if (_gradeWipeLayer is not null)
            return;

        // A grip on the divider; the divider itself drawn here (the engine splits the picture, it draws no line).
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

    /// <summary>The divider dragged to <paramref name="x"/> (the handle's layer, the player's units): the same span and formula the line is placed by.</summary>
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
    /// The handle and divider over the split's line, only while the player splits (not in the 3D view, the Live Encoded Preview or a source
    /// view, where the engine shows the plain Before): the line where the engine splits, rounded to a device pixel, 2 pixels wide.
    /// </summary>
    private void PlaceGradeWipe()
    {
        // While the split is asked for, looked at again twice a second (made on first use): the 3D view, the Live Encoded Preview and a
        // source view come and go by their own buttons, and the handle follows them.
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
