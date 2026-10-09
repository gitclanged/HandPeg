using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>The outline a piece of a game's HUD is cut to.</summary>
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

/// <summary>One piece of a game's HUD: what it is, and where it sits on a 16:9 frame, as fractions of the frame.</summary>
public sealed record HudPiece(string Name, double X, double Y, double Width, double Height, HudShape Shape = HudShape.Panel);

/// <summary>A game, and the pieces its HUD is taken apart into.</summary>
public sealed record HudGame(string Name, IReadOnlyList<HudPiece> Pieces)
{
    public string Summary => string.Join(", ", Pieces.Select(p => p.Name));
}

/// <summary>
/// Where the HUD of some popular shooters sits at 16:9 with the default HUD settings, piece by piece, so that
/// each piece can become a layer of its own and be moved about the frame on its own.
///
/// The rectangles are starting points. They were set down from the games' default layouts, not measured
/// against a recording, and a game's HUD moves with its settings (HUD scale, safe area, radar size) and with
/// its updates: each layer made from them is meant to be checked against the video and corrected with Draw Target.
/// </summary>
public static class HudLibrary
{
    public static IReadOnlyList<HudGame> Games { get; } =
    [
        new("Overwatch",
        [
            new("Killfeed", 0.75, 0.05, 0.24, 0.25, HudShape.Soft),
            new("Health & Portrait", 0.02, 0.80, 0.25, 0.18, HudShape.Soft),
            new("Abilities & Weapon", 0.75, 0.80, 0.23, 0.18, HudShape.Soft),
            new("Ultimate", 0.45, 0.85, 0.10, 0.12, HudShape.Ellipse),
        ]),
        new("Counter-Strike 2",
        [
            new("Radar", 0.008, 0.015, 0.150, 0.265, HudShape.Ellipse),
            new("Health & Armor", 0.010, 0.930, 0.220, 0.062),
            new("Ammo", 0.800, 0.930, 0.192, 0.062),
            new("Killfeed", 0.720, 0.050, 0.272, 0.200),
            new("Score & Timer", 0.360, 0.000, 0.280, 0.072),
            new("Weapons", 0.870, 0.560, 0.122, 0.340),
        ]),
        new("Apex Legends",
        [
            new("Minimap", 0.027, 0.045, 0.140, 0.250, HudShape.Panel),
            new("Health & Shield", 0.030, 0.850, 0.245, 0.120, HudShape.Slant),
            new("Abilities", 0.440, 0.880, 0.120, 0.100),
            new("Weapons & Ammo", 0.760, 0.830, 0.220, 0.140, HudShape.Slant),
            new("Killfeed", 0.720, 0.100, 0.272, 0.200),
            new("Squad Status", 0.800, 0.020, 0.190, 0.065),
        ]),
        new("Marvel Rivals",
        [
            new("Health", 0.300, 0.870, 0.225, 0.085, HudShape.Slant),
            new("Ultimate", 0.462, 0.835, 0.076, 0.135, HudShape.Ellipse),
            new("Abilities", 0.700, 0.820, 0.270, 0.150, HudShape.Slant),
            new("Killfeed", 0.740, 0.080, 0.250, 0.200),
            new("Objective", 0.360, 0.020, 0.280, 0.095),
        ]),
    ];

    /// <summary>
    /// A game's HUD as layers: one per piece, each cut out of the video where that piece sits, with a mask for
    /// the pieces that are not plain rectangles.
    /// </summary>
    /// <param name="vertical">
    /// Lays the pieces out for a tall (9:16) frame with the video across its middle: what was in the top half of
    /// the picture goes above the video, the rest below, enlarged to fill the width. Otherwise every piece is
    /// laid back exactly where it was cut from.
    /// </param>
    public static List<LayerState> BuildLayers(HudGame game, bool vertical)
    {
        var states = game.Pieces.Select(piece =>
        {
            var state = new LayerState
            {
                Name = $"{game.Name}: {piece.Name}",
                Kind = LayerKind.Video,
                SourceX = piece.X, SourceY = piece.Y, SourceWidth = piece.Width, SourceHeight = piece.Height,
                PositionX = piece.X, PositionY = piece.Y, SizeWidth = piece.Width,
                CornerRadius = piece.Shape == HudShape.Panel ? 6 : 0,
            };

            try
            {
                if (EnsureMask(piece.Shape) is { } mask)
                    (state.CustomMask, state.MaskPath) = (true, mask);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without its mask the piece is a plain rectangle, which still works.
            }

            return state;
        }).ToList();

        if (vertical)
            LayOutVertically(game, states);
        return states;
    }

    /// <summary>
    /// Places the pieces on a 9:16 frame around a 16:9 video that fills its width: one row above the video and
    /// one below, each as large as the row's width and the room above or below allow, in the left-to-right
    /// order the pieces have on screen.
    /// </summary>
    private static void LayOutVertically(HudGame game, List<LayerState> states)
    {
        // The share of the frame's height the video takes; a piece's height is its width times this, per unit of its own shape.
        const double band = 81.0 / 256, gap = 0.02, margin = 0.03, largest = 3.2;
        var room = (1 - band) / 2 - margin - 0.012;

        foreach (var above in new[] { true, false })
        {
            var row = game.Pieces.Select((piece, index) => (Piece: piece, State: states[index]))
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
