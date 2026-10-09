using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        new("Overwatch 2",
        [
            new("Health", 0.035, 0.845, 0.225, 0.115, HudShape.Slant),
            new("Ultimate", 0.455, 0.835, 0.090, 0.150, HudShape.Ellipse),
            new("Abilities & Weapon", 0.745, 0.845, 0.225, 0.120, HudShape.Slant),
            new("Killfeed", 0.725, 0.035, 0.265, 0.190),
            new("Objective", 0.380, 0.020, 0.240, 0.105),
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
    /// The mask picture for a shape: white where the layer shows, black where it does not, with a soft edge.
    /// Drawn once and kept in the masks folder, where the layers refer to it. Null for a plain panel, which
    /// has no need of a picture: its rounded corners are the layer's own.
    /// </summary>
    public static string? EnsureMask(HudShape shape)
    {
        if (shape == HudShape.Panel)
            return null;

        var path = Path.Combine(AppPaths.Masks, shape == HudShape.Ellipse ? "hud_ellipse.png" : "hud_slant.png");
        if (File.Exists(path))
            return path;

        // On a unit square; the mask is stretched to whatever shape the layer has.
        const int size = 512;
        Geometry outline = shape == HudShape.Ellipse
            ? new EllipseGeometry(new Point(size / 2.0, size / 2.0), size / 2.0 - 6, size / 2.0 - 6)
            : new PathGeometry([new PathFigure(new Point(size * 0.10, 6), [new PolyLineSegment([new Point(size - 6, 6), new Point(size * 0.90, size - 6), new Point(6, size - 6)], true)], true)]);

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, size, size));

            // A soft edge: the outline is drawn in white with a blurred pen-width of grey around it.
            context.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 6), outline);
        }

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
