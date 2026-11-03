using System.IO;
using System.Text.Json;
using System.Windows;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// The Game Presets HandPeg comes with (5.1-Release, the user's call): ordinary .hpstyle files written into the Styles folder, made
/// exactly as the Cutout Studio makes one (<c>MainViewModel.SaveCutoutPreset</c>): a tall frame, the main video zoomed across its
/// middle with its blurred background, and each piece of the game's HUD a layer of its own with a mask of its own, drawn from outlines
/// the Studio opens and edits like any it drew. Nothing is locked: a built-in can be changed, renamed, saved over or deleted.
///
/// They are written once per <see cref="Version"/>, only under a name the folder does not have yet: a file there already, the user's
/// or an earlier built-in the user changed, is never written over, and HandPeg never deletes a style.
/// </summary>
public static class GamePresetLibrary
{
    /// <summary>The zoom of the main video a Game Preset starts with: what its edges lose is what the pieces are for.</summary>
    public const double DefaultZoom = 1.7;

    /// <summary>The blurred background a vertical Game Preset starts with.</summary>
    public const double BlurRadius = 20, BackgroundDim = -0.15;

    public const int BlurPasses = 2;

    // Raised when a built-in is added or changed, so that it is written (where the folder has no file of its name).
    // 1: Overwatch 2 (traced from the user's own mask and laid out as their own style), Marvel Rivals, Valorant, CS2, Apex Legends, The Finals.
    public const int Version = 1;

    // The frame the outlines below are written in, in pixels; the masks are drawn at this size too.
    private const double W = 2560, H = 1440;

    /// <summary>Where a piece goes on the tall frame: its left and top edges and its width, as fractions of the frame.</summary>
    public readonly record struct Spot(double X, double Y, double Width);

    /// <summary>One piece of a game's HUD: its outlines as a mask's text (fractions of a 16:9 frame, see <see cref="MaskStore"/>).</summary>
    /// <param name="Source">The box it is cut from; null for the box round its outlines with 8 pixels spare.</param>
    /// <param name="Place">Where it goes; null to be laid out with the others by <see cref="HudLibrary.LayOutVertically"/>.</param>
    public sealed record Piece(string Name, string Shapes, Rect? Source = null, Spot? Place = null, int FeatherRadius = 1, int MaskGrow = 0, int CornerRadius = 0);

    /// <summary>A built-in Game Preset: its name (the file's) and its pieces.</summary>
    public sealed record BuiltIn(string Name, IReadOnlyList<Piece> Pieces)
    {
        public string FileName => Name + StyleFile.Extension;
    }

    private static string Text(params List<Point>[] shapes) => MaskStore.FormatShapes(shapes);

    private static Point P(double x, double y) => new(x / W, y / H);

    /// <summary>A polygon through points given as x, y, x, y... in pixels of the 2560 x 1440 frame.</summary>
    private static List<Point> Poly(params double[] xy) => Enumerable.Range(0, xy.Length / 2).Select(i => P(xy[2 * i], xy[2 * i + 1])).ToList();

    /// <summary>A rectangle from one corner to the other in pixels of the 2560 x 1440 frame, its corners rounded by so many of them.</summary>
    private static List<Point> Box(double left, double top, double right, double bottom, double radius = 6) =>
        MaskStore.RoundedRectangle(left / W, top / H, (right - left) / W, (bottom - top) / H, radius * 1080 / H);

    /// <summary>The built-ins, in the order the lists show them.</summary>
    public static IReadOnlyList<BuiltIn> All { get; } =
    [
        // Traced from the user's own mask (their Photoshop cutout, the one HandPeg 5.0 shipped: 12 islands, within 0.32 % of its
        // pixels), and cut, placed, feathered and grown as in the vertical style the user made by hand in HandPeg.
        new("Overwatch 2 Vertical HUD",
        [
            new("Killfeed",
                "0.72148,0.07222 0.98477,0.07222 0.98477,0.18472 0.72148,0.18472;0.90469,0.02708 0.98359,0.02708 0.98359,0.05764 0.90469,0.05764",
                new Rect(0.65637, 0.01094, 0.33784, 0.26083), new Spot(-0.169, 0.013, 1.15032), FeatherRadius: 16, MaskGrow: 19),
            new("Health & Name",
                "0.04766,0.81528 0.06562,0.81597 0.07227,0.82083 0.07617,0.83125 0.08125,0.86528 0.09844,0.85486 0.09609,0.87986 0.08867,0.90833 "
                + "0.08281,0.91667 0.08281,0.92292 0.06523,0.94167 0.06445,0.94306 0.06445,0.95069 0.05469,0.9625 0.04453,0.95139 0.04297,0.94167 "
                + "0.02578,0.92361 0.02539,0.91667 0.01953,0.90764 0.01211,0.87986 0.01016,0.85694 0.01016,0.85486 0.01133,0.85486 0.02773,0.86389 "
                + "0.03516,0.82431 0.03984,0.81806;"
                + "0.18594,0.81597 0.18867,0.81597 0.18867,0.84236 0.10078,0.85347 0.10078,0.82708;"
                + "0.23008,0.84306 0.23281,0.84306 0.23281,0.87639 0.10078,0.89167 0.10117,0.85972;"
                + "0.22773,0.88333 0.23047,0.88333 0.23047,0.92222 0.10195,0.93819 0.10156,0.89931",
                new Rect(0.00137, 0.77656, 0.25343, 0.20879), new Spot(-0.0103, 0.74339, 0.55911), FeatherRadius: 4, MaskGrow: 2),
            new("Abilities & Ammo",
                "0.75547,0.83819 0.78945,0.84236 0.78867,0.91111 0.75508,0.90694;"
                + "0.79727,0.84306 0.83125,0.84722 0.83047,0.91597 0.79688,0.91181;"
                + "0.83633,0.84792 0.8707,0.85208 0.87031,0.92083 0.83633,0.91667;"
                + "0.87773,0.85903 0.97188,0.87014 0.97188,0.93333 0.87734,0.92153;"
                + "0.91641,0.80278 0.925,0.80347 0.93008,0.81042 0.93203,0.81667 0.93203,0.83194 0.93086,0.83611 0.92578,0.84306 0.91719,0.84306 "
                + "0.91367,0.83958 0.90977,0.82986 0.90938,0.81597 0.91016,0.81181 0.91367,0.80486;"
                + "0.95234,0.80694 0.96094,0.80833 0.96602,0.81597 0.96719,0.82014 0.96797,0.82569 0.96719,0.8375 0.96172,0.84722 0.95234,0.84722 "
                + "0.94805,0.84236 0.94531,0.83472 0.94453,0.82847 0.94531,0.81736 0.94883,0.80972",
                new Rect(0.7278, 0.76598, 0.2722, 0.23402), new Spot(0.46788, 0.74208, 0.58282), FeatherRadius: 2, CornerRadius: 10),
        ]),

        // The rest are HandPeg's own outlines of each game's default HUD at 16:9 (as 5.0 had them; Valorant new), to be redrawn on a
        // recording in the Cutout Studio where a game's HUD settings move them.
        new("Marvel Rivals Vertical HUD",
        [
            new("Killfeed", Text(Box(1880, 110, 2530, 400, 4))),
            new("Hero & Ammo", Text(Poly(52, 1258, 128, 1188, 264, 1188, 264, 1300, 250, 1380, 68, 1380), Box(256, 1312, 482, 1364, 4))),
            new("Abilities & Ultimate", Text(Box(1918, 1230, 2302, 1406),
                Poly(2338, 1260, 2510, 1254, 2470, 1378, 2422, 1378, 2422, 1408, 2382, 1408, 2382, 1378, 2306, 1380))),
        ]),
        new("Valorant Vertical HUD",
        [
            new("Minimap", Text(Box(28, 28, 484, 484, 8))),
            new("Killfeed", Text(Box(1900, 110, 2536, 420, 4))),
            new("Health, Abilities & Ammo", Text(Box(800, 1292, 1772, 1426, 6))),
        ]),
        new("CS2 Vertical HUD",
        [
            new("Radar", Text(Box(20, 22, 404, 404, 8))),
            new("Killfeed", Text(Box(1840, 72, 2540, 360, 4))),
            new("Health & Armor", Text(Box(26, 1340, 590, 1430, 4))),
            new("Ammo", Text(Box(2048, 1340, 2540, 1430, 4))),
        ]),
        new("Apex Legends Vertical HUD",
        [
            new("Killfeed", Text(Box(1840, 140, 2500, 400, 4))),
            new("Health & Items", Text(
                Poly(14, 1248, 38, 1210, 522, 1210, 504, 1268, 548, 1268, 592, 1340, 574, 1378, 118, 1378, 66, 1330, 66, 1286, 40, 1274),
                Poly(628, 1290, 882, 1290, 846, 1376, 600, 1376), Box(668, 1376, 702, 1410, 4), Box(800, 1376, 834, 1410, 4))),
            new("Weapons & Gear", Text(
                Poly(1760, 1294, 1850, 1294, 1810, 1372, 1722, 1372), Poly(1892, 1296, 1982, 1296, 1942, 1372, 1856, 1372),
                Box(1744, 1376, 1794, 1410, 4), Box(1886, 1376, 1920, 1410, 4),
                Poly(2050, 1270, 2482, 1270, 2496, 1292, 2450, 1374, 2006, 1374, 2006, 1356))),
        ]),
        new("The Finals Vertical HUD",
        [
            new("Killfeed", Text(Box(1860, 70, 2530, 330, 4))),
            new("Health & Team", Text(Box(86, 1122, 404, 1176, 4), Box(86, 1208, 500, 1374, 4))),
            new("Weapon & Gadgets", Text(Box(2018, 1148, 2474, 1382))),
        ]),
    ];

    /// <summary>
    /// A built-in as the Cutout Studio saves one: its pieces as masked video layers, each mask its outlines alone (no picture, no
    /// Base64 inside: 5.1-Release). Pure data, on any thread.
    /// </summary>
    public static StylePreset Build(BuiltIn preset)
    {
        var states = preset.Pieces.Select(piece =>
        {
            var source = piece.Source ?? SourceOf(piece.Shapes);
            return new LayerState
            {
                Name = piece.Name,
                Kind = LayerKind.Video,
                SourceX = source.X, SourceY = source.Y, SourceWidth = source.Width, SourceHeight = source.Height,
                PositionX = piece.Place?.X ?? source.X, PositionY = piece.Place?.Y ?? source.Y, SizeWidth = piece.Place?.Width ?? source.Width,
                CustomMask = true, MaskPath = "", MaskFrame = true, MaskShapes = piece.Shapes,
                Feather = true, FeatherRadius = piece.FeatherRadius, MaskGrow = piece.MaskGrow, CornerRadius = piece.CornerRadius,
            };
        }).ToList();

        var laidOut = preset.Pieces.Select((p, i) => (p, i)).Where(x => x.p.Place is null).Select(x => states[x.i]).ToList();
        if (laidOut.Count > 0)
            HudLibrary.LayOutVertically(laidOut, DefaultZoom);

        return new StylePreset
        {
            Layout = new LayoutSection { CenterZoom = DefaultZoom, SourceAspectRatio = 1.7778, Vertical = true, MainVideoIndex = 0, Layers = states },
            Blur = new BlurSection { Radius = BlurRadius, Passes = BlurPasses, Dim = BackgroundDim, Zoom = 1 },
        };
    }

    // The box round a piece's outlines, with about 8 pixels of a 1440-line frame spare for a soft edge.
    private static Rect SourceOf(string shapes)
    {
        var points = MaskStore.ParseShapes(shapes).SelectMany(s => s).ToList();
        const double roomX = 8 / W, roomY = 8 / H;
        var (left, top) = (Math.Max(points.Min(p => p.X) - roomX, 0), Math.Max(points.Min(p => p.Y) - roomY, 0));
        var (right, bottom) = (Math.Min(points.Max(p => p.X) + roomX, 1), Math.Min(points.Max(p => p.Y) + roomY, 1));
        return new Rect(Math.Round(left, 5), Math.Round(top, 5), Math.Round(right - left, 5), Math.Round(bottom - top, 5));
    }

    /// <summary>
    /// Writes the built-ins the Styles folder has no file of, when <see cref="AppSettings.BuiltInGamePresetsVersion"/> is behind, on a
    /// pool thread (call it off the interface's thread). Returns the files written; their dates keep the lists' order (older than
    /// anything the user saves, so the user's own stay on top); empty when nothing was due. The caller records the version on the
    /// interface's thread (<see cref="Seeded"/>).
    /// </summary>
    public static Task<IReadOnlyList<string>> WriteMissingAsync()
    {
        if (AppSettings.Current.BuiltInGamePresetsVersion >= Version)
            return Task.FromResult<IReadOnlyList<string>>([]);

        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var written = new List<string>();
            try
            {
                Directory.CreateDirectory(AppPaths.Styles);
                var options = new JsonSerializerOptions { WriteIndented = true };

                // A fixed date long past, one minute apart: the first built-in is the newest of them.
                var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
                for (var i = 0; i < All.Count; i++)
                {
                    var path = Path.Combine(AppPaths.Styles, All[i].FileName);
                    if (File.Exists(path))
                        continue;

                    var part = path + ".part";
                    File.WriteAllText(part, JsonSerializer.Serialize(Build(All[i]), options));
                    File.Move(part, path, overwrite: false);
                    File.SetLastWriteTime(path, stamp.AddMinutes(-i));
                    written.Add(All[i].FileName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("The built-in Game Presets could not be written", ex);
            }

            return written;
        });
    }

    /// <summary>
    /// Records that the built-ins are written, on the interface's thread. An empty drop-overlay list takes every built-in; a list the user
    /// made gets the first built-in (Overwatch 2) after their own, once per version, so that it is on the overlay without taking a place
    /// of theirs. Asked of the folder, not of what this run wrote: a version not yet recorded still lists what an earlier run wrote.
    /// </summary>
    public static void Seeded()
    {
        var settings = AppSettings.Current;
        if (settings.BuiltInGamePresetsVersion >= Version)
            return;

        var all = All.Select(b => b.FileName).Where(f => File.Exists(Path.Combine(AppPaths.Styles, f))).ToList();
        if (settings.SplashStylePresets.Count == 0)
        {
            settings.SplashStylePresets = all;
            settings.SplashPresetCount = Math.Max(settings.SplashPresetCount, 1);
        }
        else if (all.Contains(All[0].FileName) && !settings.SplashStylePresets.Contains(All[0].FileName, StringComparer.OrdinalIgnoreCase))
        {
            settings.SplashStylePresets = [.. settings.SplashStylePresets, All[0].FileName];
        }

        settings.BuiltInGamePresetsVersion = Version;
        settings.SaveAsCurrent();
    }
}
