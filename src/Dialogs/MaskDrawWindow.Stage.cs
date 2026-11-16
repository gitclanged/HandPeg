using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandPegApp;

public sealed partial class MaskDrawWindow
{
    /// <summary>
    /// Vertical-frame preview of the Game Preset layout: video centred at zoom, each piece cut from the still by its outlines. Drag to
    /// place, wheel to size; manual placements persist. The move cursor shows only over pieces; the surrounding frame ignores drags.
    /// </summary>
    private sealed class CutoutStage : FrameworkElement
    {
        /// <summary>Stage piece; all values are source or frame fractions.</summary>
        public sealed class Placed
        {
            public required int Id { get; init; }
            public required Rect Source { get; init; }
            public required List<List<Point>> Outlines { get; init; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
        }

        private const double SmallestWidth = 0.05, LargestWidth = 1.5;
        private static readonly Brush Frame = MakeBrush(Color.FromRgb(0x10, 0x10, 0x12));

        private readonly double _sourceWidth, _sourceHeight;
        private List<Placed> _pieces = [];
        private BitmapSource? _still;
        private double _zoom = 1;
        private (Placed Piece, Point Grab)? _moving;

        /// <summary>Raised when a piece is manually moved or sized.</summary>
        public event Action<int>? Moved;

        public CutoutStage(double sourceWidth, double sourceHeight)
        {
            (_sourceWidth, _sourceHeight) = (sourceWidth, sourceHeight);
            ToolTip = "The Game Preset's tall frame. Drag a Cutout to place it, the wheel over a Cutout sizes it. Double-click a Cutout to lay it out again by itself.";
        }

        /// <summary>List-selected piece (drawn outlined).</summary>
        public int Selected { get; set; }

        public void Show(List<Placed> pieces, BitmapSource? still, double zoom)
        {
            (_pieces, _still, _zoom) = (pieces, still, zoom);
            InvalidateVisual();
        }

        private Rect FrameRect => ActualHeight * 9 / 16 <= ActualWidth
            ? new Rect((ActualWidth - ActualHeight * 9 / 16) / 2, 0, ActualHeight * 9 / 16, ActualHeight)
            : new Rect(0, (ActualHeight - ActualWidth * 16 / 9) / 2, ActualWidth, ActualWidth * 16 / 9);

        private double HeightOf(Placed piece) => piece.Width * (piece.Source.Height * _sourceHeight) / Math.Max(piece.Source.Width * _sourceWidth, 1e-6) * (9.0 / 16);

        // Piece rect on the stage.
        private Rect BoxOf(Placed piece, Rect frame) => new(frame.X + piece.X * frame.Width, frame.Y + piece.Y * frame.Height, piece.Width * frame.Width, HeightOf(piece) * frame.Height);

        private Placed? PieceAt(Point at)
        {
            var frame = FrameRect;
            for (var i = _pieces.Count - 1; i >= 0; i--)
            {
                if (BoxOf(_pieces[i], frame).Contains(at))
                    return _pieces[i];
            }

            return null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var at = e.GetPosition(this);
            if (PieceAt(at) is not { } piece)
                return;

            if (e.ClickCount == 2)
            {
                piece.X = double.NaN;
                Moved?.Invoke(piece.Id);
                return;
            }

            var frame = FrameRect;
            _moving = (piece, new Point(at.X - (frame.X + piece.X * frame.Width), at.Y - (frame.Y + piece.Y * frame.Height)));
            CaptureMouse();
            e.Handled = true;
        }

        /// <summary>Cursor at a point: move cursor over a piece or during a move, arrow elsewhere.</summary>
        public Cursor? CursorAt(Point at) => _moving is not null || PieceAt(at) is not null ? Cursors.SizeAll : null;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = CursorAt(e.GetPosition(this));
            if (_moving is not { } moving || e.LeftButton != MouseButtonState.Pressed)
                return;

            var (at, frame) = (e.GetPosition(this), FrameRect);
            moving.Piece.X = Math.Clamp((at.X - moving.Grab.X - frame.X) / frame.Width, -0.5, 1);
            moving.Piece.Y = Math.Clamp((at.Y - moving.Grab.Y - frame.Y) / frame.Height, -0.5, 1);
            Moved?.Invoke(moving.Piece.Id);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            _moving = null;
            ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (PieceAt(e.GetPosition(this)) is not { } piece)
                return;

            // Scale about the centre.
            var (middleX, middleY) = (piece.X + piece.Width / 2, piece.Y + HeightOf(piece) / 2);
            piece.Width = Math.Clamp(piece.Width * Math.Pow(1.001, e.Delta), SmallestWidth, LargestWidth);
            (piece.X, piece.Y) = (middleX - piece.Width / 2, middleY - HeightOf(piece) / 2);
            Moved?.Invoke(piece.Id);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var frame = FrameRect;
            if (frame.Width <= 0 || frame.Height <= 0)
                return;

            dc.PushClip(new RectangleGeometry(frame));
            dc.DrawRectangle(Frame, null, frame);

            // Video band: frame width at 100 %, vertically centred.
            var (wide, tall) = (frame.Width * _zoom, frame.Width * _zoom * _sourceHeight / _sourceWidth);
            var video = new Rect(frame.X + (frame.Width - wide) / 2, frame.Y + (frame.Height - tall) / 2, wide, tall);
            if (_still is not null)
                dc.DrawImage(_still, video);
            else
                dc.DrawRectangle(Brushes.DimGray, null, video);

            foreach (var piece in _pieces)
            {
                var box = BoxOf(piece, frame);
                if (box.Width <= 0 || box.Height <= 0 || double.IsNaN(box.X))
                    continue;

                // Full still positioned so this piece's box lands at its stage rect.
                var perSource = box.Width / Math.Max(piece.Source.Width, 1e-6);
                var perDown = box.Height / Math.Max(piece.Source.Height, 1e-6);
                var picture = new Rect(box.X - piece.Source.X * perSource, box.Y - piece.Source.Y * perDown, perSource, perDown);
                var outline = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var context = outline.Open())
                {
                    foreach (var shape in piece.Outlines)
                    {
                        context.BeginFigure(new Point(picture.X + shape[0].X * picture.Width, picture.Y + shape[0].Y * picture.Height), true, true);
                        context.PolyLineTo(shape.Skip(1).Select(p => new Point(picture.X + p.X * picture.Width, picture.Y + p.Y * picture.Height)).ToList(), true, true);
                    }
                }

                outline.Freeze();
                dc.PushClip(outline);
                if (_still is not null)
                    dc.DrawImage(_still, picture);
                else
                    dc.DrawRectangle(ShapeSurface.LineOf(piece.Id), null, box);
                dc.Pop();
                if (piece.Id == Selected)
                    dc.DrawRectangle(null, new Pen(ShapeSurface.LineOf(piece.Id), 1), box);
            }

            dc.Pop();
            dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), frame);
        }

        private static SolidColorBrush MakeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
