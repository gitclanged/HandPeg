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

// ----- Pass 5.6A (lane B): Color Match: a target picked on the player, a reference picked anywhere on screen, the grade solved -----
// The Color Studio puts the bar this makes in its Color Match row. Pick Target pauses the player when it plays, reads ONE frame at the
// canvas's own size (GpuPreviewEngine.CaptureFrameAsync, the export's picture: never while playing, Law 3), and turns the pointer over the
// player into a crosshair with a loupe beside it, drawn on the processor from that one frame (no GPU or decoder turn per move); a click
// on the picture takes the bilateral mean of the 15 x 15 pixels about it (ColorMatch.BilateralMean); Esc cancels; the 3D view and a
// source view refuse. Reference is the screen eyedropper (ScreenEyedropper): pressed and dragged anywhere, on any monitor. With both
// colours, the Color Grade card's values are solved (ColorMatch.Solve, against ColorGradeMath.Apply, the target ungraded first) and
// applied as one step of Undo (MainViewModel.ApplyColorGrade, the card put on when missing). The colours are display-referred SDR: an
// HDR project's frame is read as its 8-bit picture. 5.6A review: the target's colour is ungraded once, on the click, under the grade its
// frame was read with (ColorMatch.SolveFrom then starts from it); the card's curves are in the model; a LUT mixed in, a keyframed value
// or a target off screen at the playhead refuse; an edit, Undo, the 3D view or a source view end the pick.
public partial class MainWindow
{
    /// <summary>
    /// The Color Studio's Color Match bar, for the target <paramref name="target"/> gives at the moment of use (null: none).
    /// </summary>
    internal FrameworkElement? CreateColorMatchBar(Func<ColorGradeTarget> target) => new ColorMatchBar(this, target).Root;

    /// <summary>One Color Match bar: its two pickers, their swatches, its status, and the player's pick mode while it is on.</summary>
    private sealed class ColorMatchBar
    {
        // The loupe's side over the player (DIPs), and its distance from the pointer.
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

        // The swatches' brushes: their colours follow the picks (mutable by design: each is changed in place, never made again).
        private readonly SolidColorBrush _targetBrush = new(Colors.Transparent), _referenceBrush = new(Colors.Transparent);
        private readonly Border _targetSwatch, _referenceSwatch;

        // The two colours (sRGB 0..1): the target's before its card (5.6A review: ungraded once, under the grade its frame was read with, so
        // an Undo or a grade moved since never makes it stale) and the reference; and the target the first was picked for.
        private (double R, double G, double B)? _targetUngraded, _referenceColor;
        private ColorGradeTarget _pickedFor;

        // The pick mode: the frame read for it (let go when it ends) and the grade it was read under, whether it is being read, the loupe
        // and its one bitmap, patch and brush.
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
                    // Ended: the swatch back to the reference kept (a pick that follows sets the new one, and matches), the status with it
                    // (5.6A review: a plain click, or Esc, with nothing picked says how Reference works).
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

            // Out of the window (the Color tab moved or hidden): the pick mode ends with it.
            row.Unloaded += (_, _) => EndPick();
        }

        public FrameworkElement Root { get; }

        // What to do next, in the status line: the colour still missing; nothing once both are there.
        private string Next() => _targetUngraded is null ? _referenceColor is null ? "" : "Now pick the target" : _referenceColor is null ? "Now drag Reference" : "";

        // ----- Pick Target: one frame read, the pointer over the player a crosshair with a loupe, a click takes the colour -----

        private async Task PickTargetAsync()
        {
            if (_frame is not null)
            {
                // Pressed again while picking: cancelled.
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

            // Never a readback while the player plays (Law 3): paused first.
            if (_window.PlayerIsPlaying)
                _window.PlayerSetPause(true);

            // 5.6A review: the grade the frame is read under (the target's colour is ungraded with it on the click).
            var shot = ShotOf(_target());
            _reading = true;
            Status("Reading the frame");
            (byte[] Bgra, int Width, int Height)? frame;
            try
            {
                // One slot serves Export Frame and Copy Frame too (a newer request ends an older one with nothing): asked once more on nothing.
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

            // Closed, or the view changed while the frame was read: no pick. 5.6A review: nor when the target or its grade changed meanwhile
            // (the frame may have been drawn under either).
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

        // Why picking is refused now (the 3D view, no picture, a source view, nothing to grade; 5.6A review: the target off screen at the
        // playhead, its grade not drawn, its card's LUT or keys); null when it may go ahead. Allocates nothing (asked again on every move
        // while picking).
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
            // 5.6A review: a card switched off or a bypassed stack draws no grade ("Grade is off"), checked before its LUT or keys.
            var card = viewModel.FindColorGrade(target);
            return ColorMatch.DrawnRefusal(card, Bypassed(target)) ?? ColorMatch.CardRefusal(card);
        }

        // 5.6A review: whether the target's effects are bypassed (a clip's or the blur fill's Bypass All, or the Sequence Output's).
        private bool Bypassed(ColorGradeTarget target) => target.Layer?.EffectsBypassed ?? _window._viewModel.SequenceEffectsBypassed;

        // The target's Color Grade card as it is now (5.6A review): its values and its curves (a LUT or keys refuse the match: Refusal).
        private readonly record struct GradeShot(ColorGradeTarget For, ColorGradeValues Values, string Master, string Red, string Green, string Blue)
        {
            public ColorCurveStage? Curves => ColorCurveStage.Of(Master, Red, Green, Blue);
        }

        // 5.6A review: the curves only while the card holds the layer's curve table (an earlier Curves card that takes it leaves them
        // undrawn: OwnsCurveTable), so the table changing hands while the frame is read is a different shot too.
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

        // The player starting to play, another source, the playhead moved (5.6A review: Interactive Crop or a Draw target begun): the pick
        // ends (its frame would no longer be what is shown, or where).
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

        // 5.6A review: something the picture is made from changed (an edit, Undo or Redo, a grade moved, a setting): the frame held is no
        // longer what is shown, so the pick ends (a rare extra invalidation only asks for the pick again).
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

        // The frame's pixel under a point of the player (its overlay's), null off the picture.
        private (int X, int Y)? PixelAt(Point at)
        {
            if (_frame is not { } frame || _window.PictureFocalAt(at) is not { } focal)
                return null;
            return (Math.Max(0, Math.Min((int)(focal.X * frame.Width), frame.Width - 1)), Math.Max(0, Math.Min((int)(focal.Y * frame.Height), frame.Height - 1)));
        }

        // 5.6A review: the view or the target refused since the frame was read (the 3D view, a source view, the target off screen, its
        // card's LUT or keys): the pick ends and the press or move is left to the player (the 3D view takes its own); true when it did.
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

            // The click is the pick's: no clip box, gizmo or crop under it takes it.
            e.Handled = true;
            if (_frame is not { } frame || PixelAt(e.GetPosition(_window.PlayerOverlay)) is not { } pixel)
            {
                Status("Click the picture");
                return;
            }

            // 5.6A review: another target, or its grade changed, since the frame was read: its colour would be ungraded wrongly.
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

            // The colour before the card, worked out once under the grade the frame was read with (the swatch shows it as picked).
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

            // The 15 x 15 pixels about the pointer into the loupe's one bitmap (off the frame: dark), and their colour into its swatch.
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

            // Beside the pointer, on the side of the player with room (the player's HUD: never zoomed with the picture).
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

        // The loupe: a popup over the player's HUD (UiScale-tracked), placed in its device pixels at _loupeAt (one placement array, reused).
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

        // ----- The match -----

        // Both colours: the card's values solved against its own model and applied, one step of Undo.
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
                // Another clip (or the Sequence Output) is graded now: the target colour was not its.
                (_targetUngraded, _targetBrush.Color, _targetSwatch.ToolTip) = (null, Colors.Transparent, null);
                Status("Pick the target again");
                return;
            }

            // 5.6A review: checked here too (the card may have changed since the pick): a grade not drawn (a card switched off, a bypassed
            // stack: a match would change nothing on screen), a LUT mixed in or a keyframed value refuses; the card's curves are part of
            // the model it is solved and checked against while it holds the curve table (never "Matched" from a model short of a stage,
            // nor through curves the GPU leaves out).
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

            // The swatch: the target as the new grade makes it. Its colour before the card is kept as it is (a second match, after an
            // Undo or a grade moved by hand, starts from it again).
            _targetBrush.Color = ToColor(ColorMatch.Graded(ungraded, solution.Values, curves));
            Status(solution.Converged ? "Matched" : "Closest match");
            _status.ToolTip = string.Create(CultureInfo.InvariantCulture, $"Left: dE {solution.DeltaE:0.000} (OKLab)");
        }

        private void Status(string text)
        {
            _status.Text = text;
            _status.ToolTip = null;
        }

        // ----- Parts -----

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
