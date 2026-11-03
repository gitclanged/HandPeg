using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>The ready-made shapes a layer can be cut to, stretched over the layer: see <see cref="HudLibrary.EnsureMask"/>.</summary>
public enum HudShape
{
    /// <summary>A plain rectangle with slightly rounded corners.</summary>
    Panel,

    /// <summary>A circle or oval: a round radar, an ultimate meter.</summary>
    Ellipse,

    /// <summary>A bar leaning to the right, as slanted health bars do.</summary>
    Slant,

    /// <summary>A rectangle whose edges fade out, so the piece sits on the picture without a hard border.</summary>
    Soft,
}

/// <summary>
/// The shapes layers are cut to, and the vertical layout of pieces cut from the video. A game's HUD is a Game Preset: a built-in one
/// (<see cref="GamePresetLibrary"/>) or the user's own, drawn in the Cutout Studio (MaskDrawWindow) and saved as a style; an ML cutout
/// generator is bookmarked for v6.0. HandPeg never deletes a style.
/// </summary>
public static class HudLibrary
{
    /// <summary>
    /// Places the pieces on a 9:16 frame around a 16:9 video that fills its width at <paramref name="zoom"/>: one row above the
    /// video and one below, each as large as the row's width and the room above or below allow, in the left-to-right order
    /// the pieces have on screen.
    /// </summary>
    public static void LayOutVertically(List<LayerState> states, double zoom = 1)
    {
        // The share of the frame's height a video that fills its width takes; a piece's height is its width times this, per unit of its own shape.
        const double band = 81.0 / 256, gap = 0.02, margin = 0.03, largest = 3.2;
        var room = (1 - band * Math.Max(zoom, 1)) / 2 - margin - 0.012;

        foreach (var above in new[] { true, false })
        {
            var row = states.Select(state => (Piece: (X: state.SourceX, Y: state.SourceY, Width: state.SourceWidth, Height: state.SourceHeight), State: state))
                .Where(p => (p.Piece.Y + p.Piece.Height / 2 < 0.5) == above).OrderBy(p => p.Piece.X).ToList();
            if (row.Count == 0)
                continue;

            var widths = row.Sum(p => p.Piece.Width);
            var scale = Math.Min(Math.Min((1 - 2 * margin - gap * (row.Count - 1)) / widths, room / (band * row.Max(p => p.Piece.Height))), largest);
            var x = (1 - (widths * scale + gap * (row.Count - 1))) / 2;
            var top = above ? margin : 1 - margin - room;
            foreach (var (piece, state) in row)
            {
                state.SizeWidth = Math.Round(piece.Width * scale, 4);
                state.PositionX = Math.Round(x, 4);
                state.PositionY = Math.Round(top + (room - piece.Height * scale * band) / 2, 4);
                x += piece.Width * scale + gap;
            }
        }
    }

    /// <summary>
    /// The mask picture for a shape: white where the layer shows, black where it does not, with a soft edge.
    /// Drawn once and kept in the masks folder, where the layers refer to it. Null for a plain panel, which
    /// has no need of a picture: its rounded corners are the layer's own.
    /// </summary>
    public static string? EnsureMask(HudShape shape)
    {
        if (shape == HudShape.Panel)
            return null;

        var path = Path.Combine(AppPaths.Masks, $"hud_{shape.ToString().ToLowerInvariant()}_soft.png");
        if (File.Exists(path))
            return path;

        // On a unit square; the mask is stretched to whatever shape the layer has. The shape is drawn a little
        // in from the edges and then blurred, so that it fades out to black all the way round: a feathered edge.
        const int size = 512;
        const double inset = 34, feather = 22;
        Geometry outline = shape switch
        {
            HudShape.Ellipse => new EllipseGeometry(new Point(size / 2.0, size / 2.0), size / 2.0 - inset, size / 2.0 - inset),
            HudShape.Slant => new PathGeometry([new PathFigure(new Point(size * 0.10 + inset, inset),
                [new PolyLineSegment([new Point(size - inset, inset), new Point(size * 0.90 - inset, size - inset), new Point(inset, size - inset)], true)], true)]),
            _ => new RectangleGeometry(new Rect(inset, inset, size - 2 * inset, size - 2 * inset), 18, 18),
        };

        var shapeVisual = new DrawingVisual { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = feather, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
        using (var context = shapeVisual.RenderOpen())
            context.DrawGeometry(Brushes.White, null, outline);

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, size, size));
        drawing.Children.Add(shapeVisual);

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(AppPaths.Masks);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }
}
