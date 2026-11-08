using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp;

public sealed partial class MaskDrawWindow
{
    /// <summary>
    /// The Source Navigator (5.3.1): the whole frame the Cutouts are cut from, small, with the shapes drawn on it and a bright box
    /// round the part the drawing view shows. A press or a drag in it moves the drawing view there, at the zoom it has.
    /// </summary>
    private sealed class SourceNavigator : FrameworkElement
    {
        // The tallest it grows in a wide column, for a tall source.
        private const double MostHeight = 220;

        private static readonly Brush Back = Frozen(Color.FromRgb(0x10, 0x10, 0x12));
        private static readonly Brush Outside = Frozen(Color.FromArgb(0x90, 0, 0, 0));
        private static readonly Pen SeenPen = FrozenPen(Color.FromRgb(0xFF, 0xD0, 0x40), 1.5);
        private static readonly Pen EdgePen = FrozenPen(Color.FromRgb(0x60, 0x60, 0x66), 1);

        private readonly double _sourceWidth, _sourceHeight;
        private readonly ShapeSurface _surface;
        private BitmapSource? _still;
        private Rect _seen;

        /// <summary>A press or drag asks for the drawing view to be centred on this point of the source (in its pixels).</summary>
        public event Action<Point>? Navigate;

        public SourceNavigator(double sourceWidth, double sourceHeight, ShapeSurface surface)
        {
            (_sourceWidth, _sourceHeight, _surface) = (sourceWidth, sourceHeight, surface);
            Cursor = Cursors.Hand;
            ToolTip = "The whole frame. The bright box is what the drawing view shows: click or drag here to move the view there.";
            surface.Changed += InvalidateVisual;
        }

        public void Show(BitmapSource? still)
        {
            _still = still;
            InvalidateVisual();
        }

        /// <summary>The part of the source the drawing view shows, in its pixels.</summary>
        public Rect Seen
        {
            get => _seen;
            set
            {
                if (_seen == value)
                    return;
                _seen = value;
                InvalidateVisual();
            }
        }

        // The frame, kept to its shape in the room it has.
        private Rect Picture
        {
            get
            {
                var scale = Math.Min(ActualWidth / _sourceWidth, ActualHeight / _sourceHeight);
                var (wide, tall) = (_sourceWidth * scale, _sourceHeight * scale);
                return new Rect((ActualWidth - wide) / 2, (ActualHeight - tall) / 2, wide, tall);
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var width = double.IsInfinity(constraint.Width) ? 270 : constraint.Width;
            return new Size(width, Math.Min(width * _sourceHeight / _sourceWidth, Math.Min(MostHeight, constraint.Height)));
        }

        private Point SourceAt(Point at)
        {
            var picture = Picture;
            return new Point(
                Math.Clamp((at.X - picture.X) / picture.Width, 0, 1) * _sourceWidth,
                Math.Clamp((at.Y - picture.Y) / picture.Height, 0, 1) * _sourceHeight);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            CaptureMouse();
            Navigate?.Invoke(SourceAt(e.GetPosition(this)));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
                Navigate?.Invoke(SourceAt(e.GetPosition(this)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

        protected override void OnRender(DrawingContext dc)
        {
            var picture = Picture;
            if (picture.Width <= 0 || picture.Height <= 0)
                return;

            dc.DrawRectangle(Back, null, new Rect(RenderSize));
            if (_still is not null)
                dc.DrawImage(_still, picture);
            else
                dc.DrawRectangle(Brushes.DimGray, null, picture);

            // The shapes, each in its piece's colour.
            var (perX, perY) = (picture.Width / _sourceWidth, picture.Height / _sourceHeight);
            Point On(Point p) => new(picture.X + p.X * perX, picture.Y + p.Y * perY);
            for (var s = 0; s < _surface.Finished.Count; s++)
            {
                var shape = _surface.Finished[s];
                if (shape.Count < 2)
                    continue;

                var outline = new StreamGeometry();
                using (var context = outline.Open())
                {
                    context.BeginFigure(On(shape[0]), true, true);
                    context.PolyLineTo(shape.Skip(1).Select(On).ToList(), true, false);
                }

                outline.Freeze();
                dc.DrawGeometry(null, ShapeSurface.PenOf(_surface.Owners[s]), outline);
            }

            // What the drawing view shows: the rest dimmed, the box bright. The whole frame in view leaves it undimmed, edged only.
            var seen = Rect.Intersect(new Rect(On(_seen.TopLeft), On(_seen.BottomRight)), picture);
            if (!seen.IsEmpty && (seen.Width < picture.Width - 0.5 || seen.Height < picture.Height - 0.5))
            {
                var rest = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(picture), new RectangleGeometry(seen));
                rest.Freeze();
                dc.DrawGeometry(Outside, null, rest);
                dc.DrawRectangle(null, SeenPen, seen);
            }
            else
            {
                dc.DrawRectangle(null, EdgePen, picture);
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Color color, double thickness)
        {
            var pen = new Pen(Frozen(color), thickness);
            pen.Freeze();
            return pen;
        }
    }
}
