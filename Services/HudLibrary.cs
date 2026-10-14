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
/// One piece of a game's HUD: what it is, and the outlines of what it shows, as fractions of a 16:9 frame. The
/// area the piece is cut from is worked out from them: the box round its outlines, with a little room spare.
/// </summary>
/// <param name="Optional">In the mask, but not made a layer unless asked for (Add More Pieces of This Mask).</param>
public sealed record HudPiece(string Name, IReadOnlyList<List<Point>> Shapes, bool Optional = false)
{
    // Room left round the outlines, so that a soft edge has somewhere to fade: about 8 pixels of a 1440-line frame.
    private const double MarginX = 8 / 2560.0, MarginY = 8 / 1440.0;

    public double X => Math.Max(Shapes.SelectMany(s => s).Min(p => p.X) - MarginX, 0);
    public double Y => Math.Max(Shapes.SelectMany(s => s).Min(p => p.Y) - MarginY, 0);
    public double Width => Math.Min(Shapes.SelectMany(s => s).Max(p => p.X) + MarginX, 1) - X;
    public double Height => Math.Min(Shapes.SelectMany(s => s).Max(p => p.Y) + MarginY, 1) - Y;
}

/// <summary>A game, and the pieces its HUD is taken apart into.</summary>
public sealed record HudGame(string Name, IReadOnlyList<HudPiece> Pieces)
{
    public string Summary => string.Join(", ", Pieces.Where(p => !p.Optional).Select(p => p.Name))
        + (Pieces.Any(p => p.Optional) ? $"  (also in the mask: {string.Join(", ", Pieces.Where(p => p.Optional).Select(p => p.Name))})" : "");

    /// <summary>The pieces that become layers.</summary>
    public IEnumerable<HudPiece> MainPieces => Pieces.Where(p => !p.Optional);

    /// <summary>Every piece's outlines together, as a mask's text: the one mask of the whole frame all the game's layers share.</summary>
    public string MaskShapes => MaskStore.FormatShapes(Pieces.SelectMany(p => p.Shapes));
}

/// <summary>
/// Where the HUD of some popular shooters sits at 16:9 with the default HUD settings, piece by piece, so that
/// each piece can become a layer of its own and be moved about the frame on its own.
///
/// Each piece is its outlines, traced on a 2560 x 1440 screenshot of the game and written here in that frame's
/// pixels. All of a game's outlines together make one mask of the whole frame, which every piece's layer shares
/// and cuts its own part out of. A game's HUD still moves with its settings (HUD scale, minimap size, safe
/// area) and its updates: the areas can be redrawn on the video, and the outlines opened and changed with Draw
/// Custom Shape. A killfeed that the screenshot could not show is placed where the game puts it.
/// </summary>
public static class HudLibrary
{
    // The frame the outlines are written in.
    private const double W = 2560, H = 1440;

    private static Point P(double x, double y) => new(Math.Round(x / W, 5), Math.Round(y / H, 5));

    /// <summary>A polygon through points given as x, y, x, y... in pixels of the 2560 x 1440 frame.</summary>
    private static List<Point> Poly(params double[] xy) => Enumerable.Range(0, xy.Length / 2).Select(i => P(xy[2 * i], xy[2 * i + 1])).ToList();

    /// <summary>A rectangle from one corner to the other, with its corners rounded by so many pixels.</summary>
    private static List<Point> Box(double left, double top, double right, double bottom, double radius = 6) =>
        MaskStore.RoundedRectangle(left / W, top / H, (right - left) / W, (bottom - top) / H, radius * 1080 / H);

    /// <summary>A circle about a centre.</summary>
    private static List<Point> Round(double x, double y, double radius) =>
        MaskStore.Ellipse((x - radius) / W, (y - radius) / H, 2 * radius / W, 2 * radius / H);

    private static HudPiece Piece(string name, params List<Point>[] shapes) => new(name, shapes);

    /// <summary>A piece that is in the game's mask but is not made a layer unless asked for.</summary>
    private static HudPiece Extra(string name, params List<Point>[] shapes) => new(name, shapes, Optional: true);

    // A game's few main pieces become layers; the rest are in its mask all the same, as extras, to be made
    // into layers by hand (Add More Pieces of This Mask). A piece is one section of the HUD: a row of ability
    // icons and the ammo beside them are one layer, not one each.
    public static IReadOnlyList<HudGame> Games { get; } =
    [
        new("Overwatch",
        [
            // Room for three kills under the team kill count.
            Piece("Killfeed", Box(2310, 34, 2520, 86), Box(1826, 98, 2524, 292)),
            // The health bar and the line under it (the player's name, or the perk prompt and its progress), as one piece.
            Piece("Health & Name", Box(248, 1178, 606, 1356, 6)),
            Piece("Abilities & Ammo", Box(1930, 1204, 2234, 1370), Box(2240, 1228, 2472, 1330), Round(2445, 1191, 34)),
            // The meter alone: not the frame a weapon skin may draw round it.
            Extra("Ultimate", Round(1280, 1240, 80)),
            Extra("Portrait", Poly(28, 1232, 76, 1196, 140, 1186, 204, 1196, 252, 1232, 232, 1312, 140, 1390, 48, 1312)),
            Extra("Objective", Box(1016, 64, 1544, 152), Round(1280, 104, 58)),
        ]),
        // As HandPeg 2.5 had it, not yet traced from a screenshot.
        new("Counter-Strike 2",
        [
            Piece("Radar", Box(20, 22, 404, 404, 8)),
            Piece("Killfeed", Box(1840, 72, 2540, 360, 4)),
            Piece("Health & Armor", Box(26, 1340, 590, 1430, 4)),
            Piece("Ammo", Box(2048, 1340, 2540, 1430, 4)),
            Extra("Score & Timer", Box(920, 0, 1640, 104, 4)),
        ]),
        new("Apex Legends",
        [
            Piece("Killfeed", Box(1840, 140, 2500, 400, 4)),
            Piece("Health & Items",
                Poly(14, 1248, 38, 1210, 522, 1210, 504, 1268, 548, 1268, 592, 1340, 574, 1378, 118, 1378, 66, 1330, 66, 1286, 40, 1274),
                Poly(628, 1290, 882, 1290, 846, 1376, 600, 1376), Box(668, 1376, 702, 1410, 4), Box(800, 1376, 834, 1410, 4)),
            Piece("Weapons & Gear",
                Poly(1760, 1294, 1850, 1294, 1810, 1372, 1722, 1372), Poly(1892, 1296, 1982, 1296, 1942, 1372, 1856, 1372),
                Box(1744, 1376, 1794, 1410, 4), Box(1886, 1376, 1920, 1410, 4),
                Poly(2050, 1270, 2482, 1270, 2496, 1292, 2450, 1374, 2006, 1374, 2006, 1356)),
            // In the middle of the picture already, and the minimap and squad box were not on the screenshot.
            Extra("Tactical", Poly(1250, 1204, 1302, 1204, 1372, 1330, 1356, 1370, 1206, 1370, 1188, 1330), Box(1262, 1374, 1300, 1410, 4)),
            Extra("Minimap", Box(69, 65, 427, 425, 6)),
            Extra("Squad Status", Box(2000, 26, 2500, 120, 4)),
        ]),
        new("Marvel Rivals",
        [
            Piece("Killfeed", Box(1880, 110, 2530, 400, 4)),
            Piece("Hero & Ammo", Poly(52, 1258, 128, 1188, 264, 1188, 264, 1300, 250, 1380, 68, 1380), Box(256, 1312, 482, 1364, 4)),
            Piece("Abilities & Ultimate", Box(1918, 1230, 2302, 1406),
                Poly(2338, 1260, 2510, 1254, 2470, 1378, 2422, 1378, 2422, 1408, 2382, 1408, 2382, 1378, 2306, 1380)),
            // The health bar is in the middle of the picture, where the video already shows it.
            Extra("Health", Poly(1210, 1292, 1350, 1292, 1350, 1334, 1532, 1334, 1532, 1382, 1028, 1382, 1028, 1334, 1210, 1334)),
            Extra("Objective", Box(920, 20, 1640, 150)),
        ]),
        new("The Finals",
        [
            Piece("Killfeed", Box(1860, 70, 2530, 330, 4)),
            Piece("Health & Team", Box(86, 1122, 404, 1176, 4), Box(86, 1208, 500, 1374, 4)),
            Piece("Weapon & Gadgets", Box(2018, 1148, 2474, 1382)),
            Extra("Compass & Timer", Box(1000, 6, 1560, 66, 4), Box(1232, 60, 1328, 114, 4)),
        ]),
    ];

    /// <summary>
    /// The mask of a game's whole frame: every piece's outlines, white on black. Drawn into the masks folder
    /// the first time it is asked for. Needs the UI thread.
    /// </summary>
    public static string? EnsureFrameMask(HudGame game) => MaskStore.Render(game.MaskShapes, 2560, 1440);

    /// <summary>
    /// A game's HUD as layers: one per piece, each cut out of the video where that piece sits, all sharing the
    /// game's mask of the whole frame (each takes its own part of it), with a slightly softened edge.
    /// </summary>
    /// <param name="vertical">
    /// Lays the pieces out for a tall (9:16) frame with the video across its middle: what was in the top half of
    /// the picture goes above the video, the rest below, enlarged to fill the width. Otherwise every piece is
    /// laid back exactly where it was cut from.
    /// </param>
    public static List<LayerState> BuildLayers(HudGame game, bool vertical)
    {
        string? mask = null;
        try
        {
            mask = EnsureFrameMask(game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without their mask the pieces are plain rectangles, which still works.
        }

        var shapes = game.MaskShapes;
        var states = game.MainPieces.Select(piece => new LayerState
        {
            Name = $"{game.Name}: {piece.Name}",
            Kind = LayerKind.Video,
            SourceX = piece.X, SourceY = piece.Y, SourceWidth = piece.Width, SourceHeight = piece.Height,
            PositionX = piece.X, PositionY = piece.Y, SizeWidth = piece.Width,
            CustomMask = mask is not null, MaskPath = mask ?? "", MaskFrame = mask is not null, MaskShapes = mask is null ? null : shapes,
            Feather = mask is not null, FeatherRadius = 4,
        }).ToList();

        if (vertical)
            LayOutVertically(states);
        return states;
    }

    /// <summary>
    /// Places the pieces on a 9:16 frame around a 16:9 video that fills its width: one row above the video and
    /// one below, each as large as the row's width and the room above or below allow, in the left-to-right
    /// order the pieces have on screen.
    /// </summary>
    public static void LayOutVertically(List<LayerState> states)
    {
        // The share of the frame's height the video takes; a piece's height is its width times this, per unit of its own shape.
        const double band = 81.0 / 256, gap = 0.02, margin = 0.03, largest = 3.2;
        var room = (1 - band) / 2 - margin - 0.012;

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
