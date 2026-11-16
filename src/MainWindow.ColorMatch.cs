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
using static HandPegApp.UiBrushes;

namespace HandPegApp;

// ----- Color Match: target picked on the player, reference picked anywhere on screen, grade solved -----
// Builds the Color Studio's Color Match row. Pick Target pauses playback, captures ONE canvas-size frame (GpuPreviewEngine.CaptureFrameAsync,
// export picture; never while playing, Law 3), and turns the player pointer into a crosshair with a CPU-drawn loupe from that frame (no
// GPU/decoder work per move). Click samples the bilateral mean of 15 x 15 pixels (ColorMatch.BilateralMean); Esc cancels; 3D and source
// views refuse. Reference uses ScreenEyedropper (press and drag anywhere, any monitor). With both colours, Color Grade values are solved
// (ColorMatch.Solve against ColorGradeMath.Apply, target ungraded first) and applied as one Undo step (MainViewModel.ApplyColorGrade,
// adding the card if missing). Colours are display-referred SDR (HDR frames read as 8-bit). The target colour is ungraded once at click
// time under the frame's grade (ColorMatch.SolveFrom starts from it); card curves are modelled. A mixed-in LUT, keyframed value or a
// target off screen at the playhead refuse; an edit, Undo, 3D view or source view ends the pick.
public partial class MainWindow
{
    /// <summary>
    /// Creates the Color Studio's Color Match bar; <paramref name="target"/> is evaluated at use time.
    /// </summary>
    internal FrameworkElement? CreateColorMatchBar(Func<ColorGradeTarget> target) => new ColorMatchBar(this, target).Root;

    /// <summary>Color Match bar: two pickers with swatches, a status line, and the player pick mode while active.</summary>
    private sealed class ColorMatchBar
    {
        // Loupe size over the player and its offset from the pointer (DIPs).
        private const double LoupeSide = 96, LoupeGap = 18;

        private static readonly Geometry TargetIcon = Icon("M 2,6 A 4,4 0 1 0 10,6 A 4,4 0 1 0 2,6 M 6,0 V 3.5 M 6,8.5 V 12 M 0,6 H 3.5 M 8.5,6 H 12");
        private static readonly Geometry EyedropperIcon = Icon(
            "M 10.6,1.4 C 9.9,0.7 8.8,0.7 8.1,1.4 L 6.6,2.9 L 9.1,5.4 L 10.6,3.9 C 11.3,3.2 11.3,2.1 10.6,1.4 Z M 5.9,3.6 L 8.4,6.1 M 7.2,4.8 L 2.4,9.6 L 1.6,10.4 L 1.6,11.2 L 2.4,11.2 L 3.2,10.4 L 8,5.6");

        private static readonly Brush LoupeFrame = Frozen(Color.FromRgb(0x2A, 0x2A, 0x30)), CentreMark = Frozen(Colors.White);

        private readonly MainWindow _window;
        private readonly Func<ColorGradeTarget> _target;
        private readonly Button _pick;
        private readonly ScreenEyedropper _reference;
        private readonly TextBlock _status;

        // Swatch brushes, intentionally unfrozen: colours are updated in place on each pick.
        private readonly SolidColorBrush _targetBrush = new(Colors.Transparent), _referenceBrush = new(Colors.Transparent);
        private readonly Border _targetSwatch, _referenceSwatch;

        // Colours in sRGB 0..1: target ungraded once under the capture-time grade (so later Undo or grade edits never stale it), the
        // reference, and the target it was picked for.
        private (double R, double G, double B)? _targetUngraded, _referenceColor;
        private ColorGradeTarget _pickedFor;

        // Pick mode state: captured frame (released on end) and its grade shot, read-in-progress flag, loupe popup with its single bitmap,
        // patch buffer and swatch brush.
        private (byte[] Bgra, int Width, int Height)? _frame;
        private GradeShot _shot;
        private bool _reading;
        private Popup? _loupe;
        private WriteableBitmap? _loupePicture;
        private readonly byte[] _loupePatch = new byte[ScreenEyedropper.PatchSide * ScreenEyedropper.PatchSide * 4];
        private readonly SolidColorBrush _loupeSwatch = new(Colors.Black);
        private readonly CustomPopupPlacement[] _placement = new CustomPopupPlacement[1];
        private Point _loupeAt;
        private object? _overlayCursor;
        private bool _overlayForced;
        private double _pickedAtMs;

        public ColorMatchBar(MainWindow window, Func<ColorGradeTarget> target)
        {
            (_window, _target) = (window, target);
            _pick = new Button { Content = Labelled(TargetIcon, "Pick Target"), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Click a colour on the player [Esc cancels]" };
            AutomationProperties.SetName(_pick, "Pick Target");
            _pick.Click += (_, _) => _ = PickTargetAsync();

            _reference = new ScreenEyedropper { Content = Labelled(EyedropperIcon, "Reference"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "Drag anywhere on screen, release on the colour [Esc cancels]" };
            AutomationProperties.SetName(_reference, "Reference");
            _reference.DraggingChanged += dragging =>
            {
                if (dragging)
                {
                    EndPick();
                    Status("Release to match");
                }
                else
                {
                    // Drag ended: restore the swatch to the kept reference (Picked sets a new one and matches); a click or Esc without a
                    // pick shows a usage hint.
                    _referenceBrush.Color = _referenceColor is { } kept ? ToColor(kept) : Colors.Transparent;
                    Status(Next() is { Length: > 0 } next ? next : _referenceColor is null ? "Drag to a colour" : "");
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
            Root = row;

            // Unloaded (Color tab moved or hidden): end pick mode.
            row.Unloaded += (_, _) => EndPick();
        }

        public FrameworkElement Root { get; }

        // Next-step hint for the status line: the missing colour, or empty when both are set.
        private string Next() => _targetUngraded is null ? _referenceColor is null ? "" : "Now pick the target" : _referenceColor is null ? "Now drag Reference" : "";

        // ----- Pick Target: single frame capture, crosshair + loupe over the player, click samples -----

        private async Task PickTargetAsync()
        {
            if (_frame is not null)
            {
                // Pressed again while picking: cancel.
                EndPick();
                Status(Next());
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
            if (_window.PlayerIsPlaying)
                _window.PlayerSetPause(true);

            // Grade at capture time (used to ungrade the target colour on click).
            var shot = ShotOf(_target());
            _reading = true;
            Status("Reading the frame");
            (byte[] Bgra, int Width, int Height)? frame;
            try
            {
                // The capture slot is shared with Export/Copy Frame (a newer request cancels an older one with null): retry once on null.
                var engine = _window._gpu!;
                frame = await engine.CaptureFrameAsync() ?? await engine.CaptureFrameAsync();
            }
            finally
            {
                _reading = false;
            }

            if (frame is not { Width: > 0, Height: > 0 } read || read.Bgra.Length < read.Width * read.Height * 4)
            {
                Status("No frame read");
                return;
            }

            // Abort if unloaded, the view became refused, or the target/grade changed during capture (frame may reflect either).
            var late = Root.IsLoaded ? Refusal() : "";
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
            var viewModel = _window._viewModel;
            if (_window._explodedUi)
                return "Not in 3D view";
            if (_window._gpu is null || !viewModel.HasSource)
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
        private bool Bypassed(ColorGradeTarget target) => target.Layer?.EffectsBypassed ?? _window._viewModel.SequenceEffectsBypassed;

        // Snapshot of the target's Color Grade card: values and curves (LUT or keys refuse via Refusal).
        private readonly record struct GradeShot(ColorGradeTarget For, ColorGradeValues Values, string Master, string Red, string Green, string Blue)
        {
            public ColorCurveStage? Curves => ColorCurveStage.Of(Master, Red, Green, Blue);
        }

        // Curves are included only while the card owns the layer's curve table (an earlier Curves card takes it: OwnsCurveTable), so a
        // table ownership change during capture yields a different shot.
        private GradeShot ShotOf(ColorGradeTarget target)
        {
            var viewModel = _window._viewModel;
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
            var overlay = _window.PlayerOverlay;
            (_overlayCursor, _overlayForced, _pickedAtMs) = (overlay.ReadLocalValue(FrameworkElement.CursorProperty), overlay.ForceCursor, _window._viewModel.PositionMs);
            (overlay.Cursor, overlay.ForceCursor) = (Cursors.Cross, true);
            overlay.PreviewMouseLeftButtonDown += Overlay_PreviewMouseLeftButtonDown;
            overlay.PreviewMouseMove += Overlay_PreviewMouseMove;
            overlay.MouseLeave += Overlay_MouseLeave;
            _window.PreviewKeyDown += Window_PreviewKeyDown;
            _window._viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _window._viewModel.PreviewStateInvalidated += Picture_Invalidated;
            Status("Click the target");
        }

        private void EndPick()
        {
            if (_frame is null)
                return;

            _frame = null;
            var overlay = _window.PlayerOverlay;
            overlay.PreviewMouseLeftButtonDown -= Overlay_PreviewMouseLeftButtonDown;
            overlay.PreviewMouseMove -= Overlay_PreviewMouseMove;
            overlay.MouseLeave -= Overlay_MouseLeave;
            _window.PreviewKeyDown -= Window_PreviewKeyDown;
            _window._viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _window._viewModel.PreviewStateInvalidated -= Picture_Invalidated;
            if (_overlayCursor == DependencyProperty.UnsetValue)
                overlay.ClearValue(FrameworkElement.CursorProperty);
            else
                overlay.Cursor = _overlayCursor as Cursor;
            overlay.ForceCursor = _overlayForced;
            if (_loupe is not null)
                _loupe.IsOpen = false;
        }

        // End the pick when playback starts, the source changes, the playhead moves, or Interactive Crop/Draw Target begins (the frame no
        // longer matches what is shown).
        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var viewModel = _window._viewModel;
            var ends = e.PropertyName switch
            {
                nameof(MainViewModel.IsPlaying) => viewModel.IsPlaying,
                nameof(MainViewModel.HasSource) or nameof(MainViewModel.IsInteractiveCropActive) or nameof(MainViewModel.DrawTargetLayer) => true,
                nameof(MainViewModel.PositionMs) => Math.Abs(viewModel.PositionMs - _pickedAtMs) > 1,
                _ => false,
            };
            if (ends && _frame is not null)
            {
                EndPick();
                Status("");
            }
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

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            EndPick();
            Status(Next());
        }

        // Frame pixel under an overlay point; null outside the picture.
        private (int X, int Y)? PixelAt(Point at)
        {
            if (_frame is not { } frame || _window.PictureFocalAt(at) is not { } focal)
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
            if (_frame is not { } frame || PixelAt(e.GetPosition(_window.PlayerOverlay)) is not { } pixel)
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
                Status("Now drag Reference");
            else
                Match();
        }

        private void Overlay_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_frame is not null && EndedByRefusal())
                return;
            if (_frame is not { } frame || PixelAt(e.GetPosition(_window.PlayerOverlay)) is not { } pixel)
            {
                if (_loupe is not null)
                    _loupe.IsOpen = false;
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

            var loupe = _loupe ??= MakeLoupe();
            _loupePicture!.WritePixels(new Int32Rect(0, 0, side, side), _loupePatch, side * 4, 0);
            if (ColorMatch.BilateralMean(frame.Bgra, frame.Width, frame.Height, pixel.X, pixel.Y) is { } colour)
                _loupeSwatch.Color = ToColor(colour);

            // Place beside the pointer on whichever side has room, in HUD coordinates (unaffected by picture zoom).
            var hud = _window.PlayerHud;
            var at = e.GetPosition(hud);
            var height = LoupeSide + 14;
            _loupeAt = new Point(at.X + LoupeGap + LoupeSide <= hud.ActualWidth ? at.X + LoupeGap : at.X - LoupeGap - LoupeSide,
                at.Y + LoupeGap + height <= hud.ActualHeight ? at.Y + LoupeGap : at.Y - LoupeGap - height);
            if (!loupe.IsOpen)
                loupe.IsOpen = true;
            else
                loupe.HorizontalOffset = loupe.HorizontalOffset == 0 ? 0.01 : 0;
        }

        private void Overlay_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_loupe is not null)
                _loupe.IsOpen = false;
        }

        // Loupe popup over the player HUD (UiScale-tracked), custom-placed in device pixels at _loupeAt (reused placement array).
        private Popup MakeLoupe()
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
            var frame = new Border { Child = body, BorderBrush = LoupeFrame, BorderThickness = new Thickness(1), IsHitTestVisible = false };

            var hud = _window.PlayerHud;
            var popup = new Popup { Child = frame, PlacementTarget = hud, Placement = PlacementMode.Custom, AllowsTransparency = false, IsHitTestVisible = false, StaysOpen = true, Focusable = false };
            popup.CustomPopupPlacementCallback = (_, targetSize, _) =>
            {
                var perDip = hud.ActualWidth > 0 ? targetSize.Width / hud.ActualWidth : 1;
                _placement[0] = new CustomPopupPlacement(new Point(_loupeAt.X * perDip, _loupeAt.Y * perDip), PopupPrimaryAxis.None);
                return _placement;
            };
            UiScale.Track(popup);
            return popup;
        }

        // ----- Match -----

        // With both colours: solve the card values against its model and apply as one Undo step.
        private void Match()
        {
            var viewModel = _window._viewModel;
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
}
