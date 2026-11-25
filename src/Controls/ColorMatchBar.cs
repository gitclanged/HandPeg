using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// What the standalone <see cref="ColorMatchBar"/> needs from the window that hosts the player (Pass 5.6.5): the view model, the Color
/// Studio, the picture state and the player's pick surfaces. Implemented by <c>MainWindow</c> (MainWindow.ColorMatch.cs); the bar never
/// touches the window's private state.
/// </summary>
internal interface IColorMatchHost
{
    /// <summary>The one view model (Sequence, playhead, grade cards).</summary>
    MainViewModel ViewModel { get; }

    /// <summary>The Color Studio the bar sits in, once attached; its value writer carries Match Strength.</summary>
    ColorStudioPane? Studio { get; }

    /// <summary>True while the 3D Exploded View is on (picks refuse).</summary>
    bool IsExploded { get; }

    /// <summary>True while a GPU preview engine runs (a frame can be captured).</summary>
    bool HasEngine { get; }

    /// <summary>True while the player plays.</summary>
    bool IsPlaying { get; }

    /// <summary>Pauses the player (no readback during playback, Law 3).</summary>
    void Pause();

    /// <summary>One canvas-size export-picture frame (BGRA), or null (no engine, or a newer capture request took the slot).</summary>
    Task<(byte[] Bgra, int Width, int Height)?> CaptureFrameAsync();

    /// <summary>Picture fraction (0..1 both axes) under a <see cref="PickSurface"/> point; null outside the picture.</summary>
    Point? PictureFocalAt(Point surfacePoint);

    /// <summary>The player overlay: takes the crosshair cursor and the pick's pointer input.</summary>
    FrameworkElement PickSurface { get; }

    /// <summary>The bar's player layer (topmost over the picture, not hit-testable): holds the loupe while picking.</summary>
    Panel PickLayer { get; }

    /// <summary>Receives Esc while picking.</summary>
    UIElement KeyScope { get; }
}

// ----- Color Match: target picked on the player, reference picked anywhere on screen, grade solved -----
// The Color Studio's Color Match row (a standalone control since Pass 5.6.5; its host is IColorMatchHost). Pick Target pauses playback,
// captures ONE canvas-size frame (export picture; never while playing, Law 3), and turns the player pointer into a crosshair with a
// CPU-drawn loupe from that frame (no GPU/decoder work per move) on the host's pick layer, counter-zoomed so the player zoom never scales
// it. Click samples the bilateral mean of 15 x 15 pixels (ColorMatch.BilateralMean); Esc or a right click cancels; 3D and source views
// refuse. Reference uses ScreenEyedropper: the same two-click pick (TwoClickPick) anywhere on any monitor, a loupe with a live RGB/Luma
// readout following the pointer. Match Strength (0-100 %, reset to 100 % by a new match or a double-click) blends live from the grade
// before the match to the solved one (ColorMatchBounds.Blend, written through the Color Studio's writer, one Undo step per drag). With
// both colours, Color Grade values are solved (ColorMatch.Solve against ColorGradeMath.Apply, target ungraded first) and applied as one
// Undo step (MainViewModel.ApplyColorGrade, adding the card if missing). Colours are display-referred SDR (HDR frames read as 8-bit). The
// target colour is ungraded once at click time under the frame's grade (ColorMatch.SolveFrom starts from it); card curves are modelled. A
// mixed-in LUT, keyframed value or a target off screen at the playhead refuse; an edit, Undo, 3D view or source view ends the pick.

/// <summary>Color Match bar: two pickers with swatches, a status line, Match Strength, and the player pick mode while active.</summary>
internal sealed class ColorMatchBar : StackPanel
{
    // Loupe size over the player and its offset from the pointer (DIPs).
    private const double LoupeSide = 96, LoupeGap = 18, LoupeReadoutHeight = 30;

    private static readonly Geometry TargetIcon = Icon("M 2,6 A 4,4 0 1 0 10,6 A 4,4 0 1 0 2,6 M 6,0 V 3.5 M 6,8.5 V 12 M 0,6 H 3.5 M 8.5,6 H 12");
    private static readonly Geometry EyedropperIcon = Icon(
        "M 10.6,1.4 C 9.9,0.7 8.8,0.7 8.1,1.4 L 6.6,2.9 L 9.1,5.4 L 10.6,3.9 C 11.3,3.2 11.3,2.1 10.6,1.4 Z M 5.9,3.6 L 8.4,6.1 M 7.2,4.8 L 2.4,9.6 L 1.6,10.4 L 1.6,11.2 L 2.4,11.2 L 3.2,10.4 L 8,5.6");

    private static readonly Brush LoupeFrame = Frozen(Color.FromRgb(0x2A, 0x2A, 0x30)), CentreMark = Frozen(Colors.White);

    private readonly IColorMatchHost _host;
    private readonly Func<ColorGradeTarget> _target;
    private readonly Button _pick;
    private readonly ScreenEyedropper _reference;
    private readonly TextBlock _status;

    // Match Strength: the slider (0..100 %), its value text, the grade before the last match and the solved one it blends between.
    private readonly Slider _strength;
    private readonly TextBlock _strengthValue;
    private ColorGradeValues? _before, _solved;
    private ColorGradeTarget _solvedFor;
    private bool _settingStrength, _strengthDragging;

    // The card the last match wrote into and what it held after the match's (or Match Strength's) last write: the slider writes only
    // while both still stand (a manual edit, Undo or another card ends it).
    private Guid _matchedCard;
    private ColorGradeValues? _written;

    // The card a Match Strength drag is bound to (its target is _solvedFor): its queued writes land only there; another card or target,
    // or an edit from elsewhere, ends the drag.
    private EffectInstance? _dragCard;
    private TextBlock? _loupeReadout;

    // Swatch brushes, intentionally unfrozen: colours are updated in place on each pick.
    private readonly SolidColorBrush _targetBrush = new(Colors.Transparent), _referenceBrush = new(Colors.Transparent);
    private readonly Border _targetSwatch, _referenceSwatch;

    // Colours in sRGB 0..1: target ungraded once under the capture-time grade (so later Undo or grade edits never stale it), the
    // reference, and the target it was picked for.
    private (double R, double G, double B)? _targetUngraded, _referenceColor;
    private ColorGradeTarget _pickedFor;

    // Pick mode state: captured frame (released on end) and its grade shot, read-in-progress flag, the loupe (a counter-zoomed canvas on
    // the host's pick layer, built once) with its single bitmap, patch buffer and swatch brush.
    private (byte[] Bgra, int Width, int Height)? _frame;
    private GradeShot _shot;
    private bool _reading;
    private Canvas? _loupeLayer;
    private FrameworkElement? _loupe;
    private readonly MatrixTransform _loupeUnzoom = new();
    private WriteableBitmap? _loupePicture;
    private readonly byte[] _loupePatch = new byte[ScreenEyedropper.PatchSide * ScreenEyedropper.PatchSide * 4];
    private readonly SolidColorBrush _loupeSwatch = new(Colors.Black);
    private object? _overlayCursor;
    private bool _overlayForced;
    private double _pickedAtMs;

    // Pick Target capture in flight: its generation (a tab switch, seek, playback, preview change or unload bumps it, so the late frame is
    // dropped) and the playhead it was asked at.
    private int _captureGeneration;
    private double _readingAtMs;

    public ColorMatchBar(IColorMatchHost host, Func<ColorGradeTarget> target)
    {
        (_host, _target) = (host, target);
        _pick = new Button { Content = Labelled(TargetIcon, "Pick Target"), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Click a colour on the player [Esc cancels]" };
        AutomationProperties.SetName(_pick, "Pick Target");
        _pick.Click += (_, _) => _ = PickTargetAsync();

        _reference = new ScreenEyedropper { Content = Labelled(EyedropperIcon, "Reference"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "Click, then click a colour anywhere [Esc cancels]" };
        AutomationProperties.SetName(_reference, "Reference");
        _reference.DraggingChanged += dragging =>
        {
            if (dragging)
            {
                EndPick();
                Status("Click a colour anywhere");
            }
            else
            {
                // Drag ended: restore the swatch to the kept reference (Picked sets a new one and matches); a click or Esc without a
                // pick shows a usage hint.
                _referenceBrush.Color = _referenceColor is { } kept ? ToColor(kept) : Colors.Transparent;
                Status(Next() is { Length: > 0 } next ? next : _referenceColor is null ? "Click Reference, then a colour" : "");
            }
        };
        _reference.Sampling += colour => _referenceBrush.Color = ToColor(colour);
        _reference.Picked += colour =>
        {
            (_referenceColor, _referenceBrush.Color, _referenceSwatch!.ToolTip) = (colour, ToColor(colour), $"Reference {Hex(colour)}");
            if (_targetUngraded is null)
                Status("Now pick the target");
            else
                Match();
        };

        _targetSwatch = Swatch(_targetBrush, "Target colour");
        _referenceSwatch = Swatch(_referenceBrush, "Reference colour");
        _status = new TextBlock { FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(_status, "Color Match Status");

        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in new FrameworkElement[] { _pick, _targetSwatch, _reference, _referenceSwatch, _status })
            row.Children.Add(part);
        AutomationProperties.SetName(row, "Color Match");

        // Match Strength row: label (double-click resets), slider, value.
        (_strength, _strengthValue) = StrengthRow(out var strengthRow);
        Children.Add(row);
        Children.Add(strengthRow);

        // Unloaded (Color tab moved or hidden): end pick mode and any Match Strength drag (its card subscription released).
        Unloaded += (_, _) =>
        {
            InvalidateCapture("");
            EndPick();
            if (_strengthDragging)
                CancelStrengthDrag();
        };
    }

    /// <summary>The Match Strength slider (goldens).</summary>
    internal Slider Strength => _strength;

    /// <summary>True while Pick Target holds a captured frame and the player pointer picks (goldens, diagnostics).</summary>
    internal bool IsPicking => _frame is not null;

    /// <summary>The status line's text (goldens).</summary>
    internal string StatusText => _status.Text;

    /// <summary>Ends Pick Target if active (the host's view changed under it); the status line shows the next step.</summary>
    public void CancelPick()
    {
        if (_frame is null)
        {
            // Still reading (tab switch during the capture): the frame arriving later is dropped.
            InvalidateCapture(Next());
            return;
        }
        EndPick();
        Status(Next());
    }

    // Match Strength: 0..100 %, 100 % after every match; a drag is one Undo step, a click or key one step each.
    private (Slider Slider, TextBlock Value) StrengthRow(out FrameworkElement row)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var label = new TextBlock { Text = "Match Strength", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Blend toward the match (double-click: 100%)" };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = 100, SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true, IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), ToolTip = "Match Strength (double-click: 100%)",
        };
        AutomationProperties.SetName(slider, "Match Strength");
        var value = new TextBlock { Text = ColorMatchBounds.StrengthText(1), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        AutomationProperties.SetName(value, "Match Strength Value");
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(value);

        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            // The grade changed since the match (edited, undone, another card or target): Match Strength ends instead of writing.
            if (_host.Studio is not { } pane || !StillMatched())
            {
                EndStrength();
                return;
            }

            // Bound to this target and card for the whole drag: an edit from elsewhere (Undo included) ends it (DragCard_ValueChanged).
            UnbindDrag();
            (_strengthDragging, _dragCard) = (true, _host.ViewModel.FindColorGrade(_solvedFor));
            if (_dragCard is not null)
                _dragCard.ValueChanged += DragCard_ValueChanged;
            pane.BeginValuesGesture();
        }));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            if (!_strengthDragging)
                return;
            UnbindDrag();
            _strengthDragging = false;
            if (_host.Studio is { } pane)
            {
                pane.EndValuesGesture();
                pane.FlushWrites();
            }

            Remember();
        }));
        slider.ValueChanged += (_, e) =>
        {
            _strengthValue!.Text = ColorMatchBounds.StrengthText(e.NewValue / 100);
            if (!_settingStrength)
                ApplyStrength(e.NewValue / 100);
        };
        slider.MouseDoubleClick += (_, _) => slider.Value = 100;

        // Pointer over the slider: a match edited, undone or replaced since shows as ended before anything is written.
        slider.MouseEnter += (_, _) =>
        {
            if (_before is not null && !_strengthDragging && !StillMatched())
                EndStrength();
        };
        label.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                slider.Value = 100;
        };
        row = grid;
        return (slider, value);
    }

    // Writes the blend of the grade before the last match and the solved one, only over the grade the match (or this slider) left: a
    // changed target, card or value since then ends Match Strength instead; a drag stays bound to the target and card it started on.
    private void ApplyStrength(double strength)
    {
        if (_before is not { } before || _solved is not { } solved || _host.Studio is not { } pane)
            return;
        if (!_strengthDragging && !StillMatched())
        {
            EndStrength();
            return;
        }

        var blend = ColorMatchBounds.Blend(before, solved, strength);
        if (_strengthDragging)
        {
            // Another target or card since the drag began (Undo puts new cards in): it ends unwritten.
            if (_target() != _solvedFor || _dragCard is null || !ReferenceEquals(_host.ViewModel.FindColorGrade(_solvedFor), _dragCard)
                || (pane.Card is { } shown && !ReferenceEquals(shown, _dragCard)))
            {
                CancelStrengthDrag();
                return;
            }

            pane.WriteValues(blend, _dragCard);
            return;
        }

        pane.BeginValuesGesture();
        pane.WriteValues(blend);
        pane.EndValuesGesture();
        pane.FlushWrites();
        Remember();
    }

    // The dragged card changed outside the pane's own writes (Undo, Redo, an Effects pane edit): the drag's pending writes are dropped
    // and Match Strength ends, so no queued blend lands over that change.
    private void DragCard_ValueChanged(EffectInstance card, string key)
    {
        if (_strengthDragging && _host.Studio is not { IsWriting: true })
            CancelStrengthDrag();
    }

    // Ends an invalidated drag: its pending writes dropped (never flushed into the current card), the slider off until the next match.
    private void CancelStrengthDrag()
    {
        UnbindDrag();
        _strengthDragging = false;
        _host.Studio?.CancelValuesGesture();
        EndStrength();
    }

    // Releases the drag's card subscription (drag ended or cancelled).
    private void UnbindDrag()
    {
        if (_dragCard is not null)
            _dragCard.ValueChanged -= DragCard_ValueChanged;
        _dragCard = null;
    }

    // A new match: the slider back at 100 % (without writing) and enabled for this target and the card the match wrote.
    private void ResetStrength(in ColorGradeValues before, in ColorGradeValues solved, ColorGradeTarget target)
    {
        (_before, _solved, _solvedFor) = (before, solved, target);
        _matchedCard = _host.ViewModel.FindColorGrade(target)?.Id ?? Guid.Empty;
        Remember();
        SetStrengthQuietly(100);
        _strength.IsEnabled = _written is not null;
    }

    // True while the target, its card and the card's values are still what the match or Match Strength last left.
    private bool StillMatched() =>
        _written is { } written && _target() == _solvedFor && _host.ViewModel.FindColorGrade(_solvedFor) is { } card && card.Id == _matchedCard
        && ColorGradeValues.From(card) == written;

    // Records what the matched card holds now: the baseline StillMatched compares against (null when the card is gone).
    private void Remember() =>
        _written = _host.ViewModel.FindColorGrade(_solvedFor) is { } card && card.Id == _matchedCard ? ColorGradeValues.From(card) : null;

    // The match no longer stands: the slider back at 100 % (without writing) and off until the next match.
    private void EndStrength()
    {
        (_before, _solved, _written) = (null, null, null);
        SetStrengthQuietly(100);
        _strength.IsEnabled = false;
    }

    // The slider's value set without writing (a new match, or the match ended).
    private void SetStrengthQuietly(double value)
    {
        _settingStrength = true;
        try
        {
            _strength.Value = value;
        }
        finally
        {
            _settingStrength = false;
        }
    }

    // Next-step hint for the status line: the missing colour, or empty when both are set.
    private string Next() => _targetUngraded is null ? _referenceColor is null ? "" : "Now pick the target" : _referenceColor is null ? "Now pick the reference" : "";

    // ----- Pick Target: single frame capture, crosshair + loupe over the player, click samples -----

    private async Task PickTargetAsync()
    {
        if (_frame is not null)
        {
            // Pressed again while picking: cancel.
            CancelPick();
            return;
        }

        if (_reading)
            return;
        if (Refusal() is { } refused)
        {
            Status(refused);
            return;
        }

        // No readback during playback (Law 3): pause first.
        if (_host.IsPlaying)
            _host.Pause();

        // Grade at capture time (used to ungrade the target colour on click).
        var shot = ShotOf(_target());
        var capture = ++_captureGeneration;
        var viewModel = _host.ViewModel;
        (_reading, _readingAtMs) = (true, viewModel.PositionMs);
        Status("Reading the frame");
        (byte[] Bgra, int Width, int Height)? frame;
        viewModel.PropertyChanged += Reading_PropertyChanged;
        viewModel.PreviewStateInvalidated += Reading_Invalidated;
        try
        {
            // The capture slot is shared with Export/Copy Frame (a newer request cancels an older one with null): retry once on null,
            // unless the capture was invalidated meanwhile.
            frame = await _host.CaptureFrameAsync();
            if (frame is null && capture == _captureGeneration)
                frame = await _host.CaptureFrameAsync();
        }
        finally
        {
            _reading = false;
            viewModel.PropertyChanged -= Reading_PropertyChanged;
            viewModel.PreviewStateInvalidated -= Reading_Invalidated;
        }

        // Invalidated during the read (tab switch, seek, playback, preview change, unload): the status already says so.
        if (capture != _captureGeneration)
            return;

        if (frame is not { Width: > 0, Height: > 0 } read || read.Bgra.Length < read.Width * read.Height * 4)
        {
            Status("No frame read");
            return;
        }

        // Abort if unloaded, the view became refused, or the target/grade changed during capture (frame may reflect either).
        var late = IsLoaded ? Refusal() : "";
        if (late is not null)
        {
            Status(late);
            return;
        }

        if (ShotOf(_target()) != shot)
        {
            Status("Pick the target again");
            return;
        }

        BeginPick(read, shot);
    }

    // Refusal reason (3D view, no picture, source view, nothing to grade, target off screen, grade not drawn, card LUT or keys), or null.
    // Allocation-free: re-evaluated on every move while picking.
    private string? Refusal()
    {
        var viewModel = _host.ViewModel;
        if (_host.IsExploded)
            return "Not in 3D view";
        if (!_host.HasEngine || !viewModel.HasSource)
            return "No picture yet";
        if (viewModel.IsInteractiveCropActive || viewModel.DrawTargetLayer is not null)
            return "Leave the source view";
        var target = _target();
        if (target.IsNone || !viewModel.CanColorGrade(target))
            return "Nothing to grade";
        if (target.Layer is { } layer && !ColorMatch.ShownAt(layer, viewModel.PositionMs / 1000))
            return "Target not shown";
        // Disabled card or bypassed stack draws no grade ("Grade is off"); checked before LUT/keys.
        var card = viewModel.FindColorGrade(target);
        return ColorMatch.DrawnRefusal(card, Bypassed(target)) ?? ColorMatch.CardRefusal(card);
    }

    // True when the target's effects are bypassed (clip or blur fill Bypass All, or Sequence Output).
    private bool Bypassed(ColorGradeTarget target) => target.Layer?.EffectsBypassed ?? _host.ViewModel.SequenceEffectsBypassed;

    // Snapshot of the target's Color Grade card: values and curves (LUT or keys refuse via Refusal).
    private readonly record struct GradeShot(ColorGradeTarget For, ColorGradeValues Values, string Master, string Red, string Green, string Blue)
    {
        public ColorCurveStage? Curves => ColorCurveStage.Of(Master, Red, Green, Blue);
    }

    // Curves are included only while the card owns the layer's curve table (an earlier Curves card takes it: OwnsCurveTable), so a
    // table ownership change during capture yields a different shot.
    private GradeShot ShotOf(ColorGradeTarget target)
    {
        var viewModel = _host.ViewModel;
        if (viewModel.FindColorGrade(target) is not { } card)
            return new(target, ColorGradeValues.Neutral, "", "", "", "");
        if (viewModel.ColorGradeStack(target) is { } stack && !ColorMatch.OwnsCurveTable(stack, card))
            return new(target, ColorGradeValues.From(card), "", "", "", "");
        return new(target, ColorGradeValues.From(card), card.GetText(ColorGradeKeys.CurveMaster), card.GetText(ColorGradeKeys.CurveRed),
            card.GetText(ColorGradeKeys.CurveGreen), card.GetText(ColorGradeKeys.CurveBlue));
    }

    private void BeginPick((byte[] Bgra, int Width, int Height) frame, GradeShot shot)
    {
        (_frame, _shot) = (frame, shot);
        var surface = _host.PickSurface;
        (_overlayCursor, _overlayForced, _pickedAtMs) = (surface.ReadLocalValue(FrameworkElement.CursorProperty), surface.ForceCursor, _host.ViewModel.PositionMs);
        (surface.Cursor, surface.ForceCursor) = (Cursors.Cross, true);
        surface.PreviewMouseLeftButtonDown += Overlay_PreviewMouseLeftButtonDown;
        surface.PreviewMouseRightButtonDown += Overlay_PreviewMouseRightButtonDown;
        surface.PreviewMouseMove += Overlay_PreviewMouseMove;
        surface.MouseLeave += Overlay_MouseLeave;
        _host.KeyScope.PreviewKeyDown += KeyScope_PreviewKeyDown;
        _host.ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _host.ViewModel.PreviewStateInvalidated += Picture_Invalidated;
        Status("Click the target");
    }

    private void EndPick()
    {
        if (_frame is null)
            return;

        _frame = null;
        var surface = _host.PickSurface;
        surface.PreviewMouseLeftButtonDown -= Overlay_PreviewMouseLeftButtonDown;
        surface.PreviewMouseRightButtonDown -= Overlay_PreviewMouseRightButtonDown;
        surface.PreviewMouseMove -= Overlay_PreviewMouseMove;
        surface.MouseLeave -= Overlay_MouseLeave;
        _host.KeyScope.PreviewKeyDown -= KeyScope_PreviewKeyDown;
        _host.ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _host.ViewModel.PreviewStateInvalidated -= Picture_Invalidated;
        if (_overlayCursor == DependencyProperty.UnsetValue)
            surface.ClearValue(FrameworkElement.CursorProperty);
        else
            surface.Cursor = _overlayCursor as Cursor;
        surface.ForceCursor = _overlayForced;
        HideLoupe();

        // Leave the host's pick layer as found: the loupe layer removed, the layer collapsed once empty.
        if (_loupeLayer is { Parent: Panel layer } loupeLayer)
        {
            layer.Children.Remove(loupeLayer);
            if (layer.Children.Count == 0)
                layer.Visibility = Visibility.Collapsed;
        }
    }

    // End the pick when playback starts, the source changes, the playhead moves, or Interactive Crop/Draw Target begins (the frame no
    // longer matches what is shown).
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (EndsFrame(e.PropertyName, _pickedAtMs) && _frame is not null)
        {
            EndPick();
            Status("");
        }
    }

    // True when a view-model change makes a frame taken at playhead atMs no longer match what is shown.
    private bool EndsFrame(string? property, double atMs)
    {
        var viewModel = _host.ViewModel;
        return property switch
        {
            nameof(MainViewModel.IsPlaying) => viewModel.IsPlaying,
            nameof(MainViewModel.HasSource) or nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer) => true,
            nameof(MainViewModel.PositionMs) => Math.Abs(viewModel.PositionMs - atMs) > 1,
            _ => false,
        };
    }

    // While Pick Target reads its frame: the same changes that end a pick drop the frame being read.
    private void Reading_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (EndsFrame(e.PropertyName, _readingAtMs))
            InvalidateCapture("Pick the target again");
    }

    private void Reading_Invalidated() => InvalidateCapture("Pick the target again");

    // Drops the capture in flight (no-op when none): its frame is ignored on arrival and not retried.
    private void InvalidateCapture(string status)
    {
        if (!_reading)
            return;
        _captureGeneration++;
        Status(status);
    }

    // Preview inputs changed (edit, Undo/Redo, grade or setting): the held frame is stale, so end the pick (a spurious invalidation
    // only costs a re-pick).
    private void Picture_Invalidated()
    {
        if (_frame is null)
            return;
        EndPick();
        Status("Pick the target again");
    }

    // A right click while picking cancels (the two-click pick's other way out besides Esc); the player's context menu never opens.
    private void Overlay_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CancelPick();
    }

    private void KeyScope_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        CancelPick();
    }

    // Frame pixel under a pick-surface point; null outside the picture.
    private (int X, int Y)? PixelAt(Point at)
    {
        if (_frame is not { } frame || _host.PictureFocalAt(at) is not { } focal)
            return null;
        return (Math.Max(0, Math.Min((int)(focal.X * frame.Width), frame.Width - 1)), Math.Max(0, Math.Min((int)(focal.Y * frame.Height), frame.Height - 1)));
    }

    // If refused since capture (3D view, source view, target off screen, LUT or keys), end the pick and leave the input to the player;
    // returns true when ended.
    private bool EndedByRefusal()
    {
        if (Refusal() is not { } refused)
            return false;
        EndPick();
        Status(refused);
        return true;
    }

    private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EndedByRefusal())
            return;

        // Consume the click so clip boxes, gizmos or crop handles underneath never receive it.
        e.Handled = true;
        if (_frame is not { } frame || PixelAt(e.GetPosition(_host.PickSurface)) is not { } pixel)
        {
            Status("Click the picture");
            return;
        }

        // Target or grade changed since capture: ungrading would be wrong, so re-pick.
        var shot = _shot;
        if (ShotOf(_target()) != shot)
        {
            EndPick();
            Status("Pick the target again");
            return;
        }

        var colour = ColorMatch.BilateralMean(frame.Bgra, frame.Width, frame.Height, pixel.X, pixel.Y);
        EndPick();
        if (colour is not { } picked)
        {
            Status("No colour read");
            return;
        }

        // Ungrade once under the capture-time grade; the swatch shows the colour as picked.
        var ungraded = ColorMatch.Ungrade(picked, shot.Values, shot.Curves);
        (_targetUngraded, _pickedFor, _targetBrush.Color, _targetSwatch.ToolTip) = (ungraded, shot.For, ToColor(picked), $"Target {Hex(picked)}");
        if (_referenceColor is null)
            Status("Now pick the reference");
        else
            Match();
    }

    private void Overlay_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_frame is not null && EndedByRefusal())
            return;
        if (_frame is not { } frame || PixelAt(e.GetPosition(_host.PickSurface)) is not { } pixel)
        {
            HideLoupe();
            return;
        }

        // Copy the 15 x 15 patch around the pointer into the loupe bitmap (dark outside the frame) and update its swatch.
        const int side = ScreenEyedropper.PatchSide, radius = side / 2;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var (fx, fy) = (pixel.X + x - radius, pixel.Y + y - radius);
                var to = (y * side + x) * 4;
                if (fx < 0 || fy < 0 || fx >= frame.Width || fy >= frame.Height)
                {
                    (_loupePatch[to], _loupePatch[to + 1], _loupePatch[to + 2]) = (0x18, 0x18, 0x18);
                    continue;
                }

                var from = (fy * frame.Width + fx) * 4;
                (_loupePatch[to], _loupePatch[to + 1], _loupePatch[to + 2]) = (frame.Bgra[from], frame.Bgra[from + 1], frame.Bgra[from + 2]);
            }
        }

        var loupeLayer = ShowLoupeLayer();
        _loupePicture!.WritePixels(new Int32Rect(0, 0, side, side), _loupePatch, side * 4, 0);
        if (ColorMatch.BilateralMean(frame.Bgra, frame.Width, frame.Height, pixel.X, pixel.Y) is { } colour)
        {
            _loupeSwatch.Color = ToColor(colour);
            if (_loupeReadout is not null)
                _loupeReadout.Text = TwoClickPick.ReadoutLines(colour);
        }

        // Place beside the pointer on whichever side has room. The loupe layer is counter-zoomed against the surface's view transform, so
        // its coordinates are the unzoomed player's (the picture zoom never scales the loupe) and the room is the surface's visible size.
        var at = e.GetPosition(loupeLayer);
        var (roomWidth, roomHeight) = (_host.PickSurface.ActualWidth, _host.PickSurface.ActualHeight);
        var height = LoupeSide + 14 + LoupeReadoutHeight;
        Canvas.SetLeft(_loupe!, at.X + LoupeGap + LoupeSide <= roomWidth ? at.X + LoupeGap : at.X - LoupeGap - LoupeSide);
        Canvas.SetTop(_loupe!, at.Y + LoupeGap + height <= roomHeight ? at.Y + LoupeGap : at.Y - LoupeGap - height);
        _loupe!.Visibility = Visibility.Visible;
    }

    private void Overlay_MouseLeave(object sender, MouseEventArgs e) => HideLoupe();

    private void HideLoupe()
    {
        if (_loupe is not null)
            _loupe.Visibility = Visibility.Collapsed;
    }

    // The loupe layer on the host's pick layer (added and the pick layer shown on first use in a pick), its counter-zoom refreshed from the
    // surface's current view transform (the player may be zoomed while picking).
    private Canvas ShowLoupeLayer()
    {
        var loupeLayer = _loupeLayer ??= MakeLoupeLayer();
        var layer = _host.PickLayer;
        if (!ReferenceEquals(loupeLayer.Parent, layer))
        {
            (loupeLayer.Parent as Panel)?.Children.Remove(loupeLayer);
            layer.Children.Add(loupeLayer);
        }

        if (layer.Visibility != Visibility.Visible)
            layer.Visibility = Visibility.Visible;
        var view = _host.PickSurface.RenderTransform?.Value ?? Matrix.Identity;
        if (view.HasInverse)
            view.Invert();
        else
            view = Matrix.Identity;
        _loupeUnzoom.Matrix = view;
        return loupeLayer;
    }

    // Loupe: the patch scaled up nearest-neighbour with a centre mark, the swatch and a two-line readout, on a counter-zoomed canvas.
    private Canvas MakeLoupeLayer()
    {
        const int side = ScreenEyedropper.PatchSide;
        _loupePicture = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgr32, null);
        var image = new Image { Source = _loupePicture, Width = LoupeSide, Height = LoupeSide, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var cell = LoupeSide / side;
        var centre = new Rectangle { Width = cell + 2, Height = cell + 2, Stroke = CentreMark, StrokeThickness = 1, IsHitTestVisible = false };
        Canvas.SetLeft(centre, cell * (side / 2) - 1);
        Canvas.SetTop(centre, cell * (side / 2) - 1);
        var marks = new Canvas { Width = LoupeSide, Height = LoupeSide, IsHitTestVisible = false };
        marks.Children.Add(centre);
        var picture = new Grid();
        picture.Children.Add(image);
        picture.Children.Add(marks);
        var body = new StackPanel();
        body.Children.Add(picture);
        body.Children.Add(new Border { Height = 14, Background = _loupeSwatch });
        // Two lines (TwoClickPick.ReadoutLines), scaled down only if a large UI font would still overflow: never an ellipsis.
        _loupeReadout = new TextBlock { FontSize = 10, Foreground = CentreMark, Padding = new Thickness(3, 1, 3, 1), LineHeight = 13, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        AutomationProperties.SetName(_loupeReadout, "Loupe Readout");
        body.Children.Add(new Border
        {
            Background = LoupeFrame, Width = LoupeSide, Height = LoupeReadoutHeight,
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Child = _loupeReadout },
        });
        _loupe = new Border { Child = body, BorderBrush = LoupeFrame, BorderThickness = new Thickness(1), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_loupe, "Color Match Loupe");
        var layer = new Canvas { IsHitTestVisible = false, RenderTransform = _loupeUnzoom };
        layer.Children.Add(_loupe);
        return layer;
    }

    // ----- Match -----

    // With both colours: solve the card values against its model and apply as one Undo step.
    private void Match()
    {
        var viewModel = _host.ViewModel;
        var target = _target();
        if (_targetUngraded is not { } ungraded || _referenceColor is not { } reference)
            return;
        if (target.IsNone || !viewModel.CanColorGrade(target))
        {
            Status("Nothing to grade");
            return;
        }

        if (target != _pickedFor)
        {
            // Grade target changed since the pick: discard the target colour.
            (_targetUngraded, _targetBrush.Color, _targetSwatch.ToolTip) = (null, Colors.Transparent, null);
            Status("Pick the target again");
            return;
        }

        // Re-check (the card may have changed): refuse if the grade is not drawn (disabled card or bypassed stack), a LUT is mixed in,
        // or a value is keyframed. Curves are modelled only while the card owns the curve table, so "Matched" never comes from an
        // incomplete model or curves the GPU skips.
        var card = viewModel.FindColorGrade(target);
        if ((ColorMatch.DrawnRefusal(card, Bypassed(target)) ?? ColorMatch.CardRefusal(card)) is { } refused)
        {
            Status(refused);
            return;
        }

        var current = card is null ? ColorGradeValues.Neutral : ColorGradeValues.From(card);
        var curves = card is not null && viewModel.ColorGradeStack(target) is { } stack && ColorMatch.OwnsCurveTable(stack, card) ? ColorCurveStage.Of(card) : null;
        var solution = ColorMatch.SolveFrom(ungraded, reference, current, curves);
        if (!viewModel.ApplyColorGrade(target, solution.Values, "Color Match"))
        {
            Status("Nothing to grade");
            return;
        }

        ResetStrength(current, solution.Values, target);

        // Swatch shows the target under the new grade; the ungraded colour is kept so a later match (after Undo or manual edits)
        // restarts from it.
        _targetBrush.Color = ToColor(ColorMatch.Graded(ungraded, solution.Values, curves));
        Status(solution.Converged ? "Matched" : "Closest match");
        _status.ToolTip = string.Create(CultureInfo.InvariantCulture, $"Left: dE {solution.DeltaE:0.000} (OKLab)");
    }

    private void Status(string text)
    {
        _status.Text = text;
        _status.ToolTip = null;
    }

    // ----- UI helpers -----

    private static StackPanel Labelled(Geometry icon, string text)
    {
        var glyph = new Path { Data = icon, Width = 12, Height = 12, StrokeThickness = 1.2, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(glyph);
        label.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return label;
    }

    private static Border Swatch(SolidColorBrush brush, string tip)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 0, 0), Background = brush, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        AutomationProperties.SetName(swatch, tip);
        return swatch;
    }

    private static Geometry Icon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Color ToColor((double R, double G, double B) c) => Color.FromRgb(ToByte(c.R), ToByte(c.G), ToByte(c.B));

    private static byte ToByte(double c) => (byte)Math.Round(Math.Max(0, Math.Min(c, 1)) * 255);

    private static string Hex((double R, double G, double B) c) => string.Create(CultureInfo.InvariantCulture, $"#{ToByte(c.R):X2}{ToByte(c.G):X2}{ToByte(c.B):X2}");
}
