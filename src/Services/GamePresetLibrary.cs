using System.IO;
using System.Text.Json;
using System.Windows;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// Built-in Game Presets: ordinary .hpstyle files seeded into the Styles folder, identical to Cutout Studio output
/// (<c>MainViewModel.SaveCutoutPreset</c>): vertical frame, centre-zoomed video with blur fill, and one masked Cutout per HUD element
/// using editable outlines. Fully editable: rename, overwrite or delete.
///
/// Written once per <see cref="Version"/>, only for names not already present: existing files (custom or modified built-ins) are
/// never overwritten, and Game Presets are never deleted.
/// </summary>
public static class GamePresetLibrary
{
    /// <summary>Initial video zoom; the cropped edges are what the Cutouts restore.</summary>
    public const double DefaultZoom = 1.7;

    /// <summary>The blurred background a vertical Game Preset starts with.</summary>
    public const double BlurRadius = 20, BackgroundDim = -0.15;

    public const int BlurPasses = 2;

    // Bumped when built-ins are added or changed so missing files are written.
    // 1: Overwatch 2, Marvel Rivals, Valorant, CS2, Apex Legends, The Finals.
    // 2: "Overwatch 2 Vertical HUD" renamed "Overwatch Vertical" (untouched old files renamed, see WriteMissingAsync); "Fortnite Vertical" added.
    public const int Version = 2;

    /// <summary>Legacy name of the built-in Overwatch Game Preset.</summary>
    public const string OldOverwatchFile = "Overwatch 2 Vertical HUD" + StyleFile.Extension;

    // Write date WriteMissingAsync assigns to built-ins (a saved-over file has a different date).
    private static readonly DateTime FirstStamp = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    // The frame the outlines below are written in, in pixels; the masks are drawn at this size too.
    private const double W = 2560, H = 1440;

    /// <summary>Placement on the vertical frame: left, top and width as frame fractions.</summary>
    public readonly record struct Spot(double X, double Y, double Width);

    /// <summary>One HUD element: outlines as mask text (16:9 frame fractions, see <see cref="MaskStore"/>).</summary>
    /// <param name="Source">Source crop; null for the outline bounds plus 8 px padding.</param>
    /// <param name="Place">Placement; null for automatic layout via <see cref="HudLibrary.LayOutVertically"/>.</param>
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

    /// <summary>Rounded rectangle outline from corner pixels on a 2560 x 1440 frame, with the given corner radius.</summary>
    private static List<Point> Box(double left, double top, double right, double bottom, double radius = 6) =>
        MaskStore.RoundedRectangle(left / W, top / H, (right - left) / W, (bottom - top) / H, radius * 1080 / H);

    /// <summary>The built-ins, in the order the lists show them.</summary>
    public static IReadOnlyList<BuiltIn> All { get; } =
    [
        // Traced from a reference bitmap mask (12 islands, within 0.32 % of its pixels) with crops, placement, feather and grow matching
        // a hand-built vertical preset (mask within 1.1 % at soft edges; boxes and placement exact). Listed first: the drop overlay's
        // first target and top of the lists.
        new("Overwatch Vertical",
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

        // From a Cutout Studio preset authored on a 16:9 recording: outlines, crops and placement as drawn.
        new("Fortnite Vertical",
        [
            new("Inventory",
                "0.73347,0.87717 0.73356,0.94161 0.77055,0.94161 0.77055,0.87713;0.77534,0.87713 0.77534,0.94161 0.81233,0.94161 0.81233,0.87713;"
                + "0.81773,0.94168 0.81781,0.87713 0.85477,0.87691 0.85481,0.94177;0.85959,0.87713 0.85959,0.94161 0.89726,0.94161 0.89726,0.87713;"
                + "0.90205,0.87713 0.90205,0.94161 0.93904,0.94161 0.93904,0.87713;0.94384,0.94161 0.94377,0.87692 0.98089,0.87704 0.98082,0.94199;"
                + "0.93973,0.76764 0.93973,0.84307 0.98014,0.84307 0.98014,0.76521",
                new Rect(0.73347, 0.76521, 0.24742, 0.17678), new Spot(0.5363, 0.8265, 0.4337)),
            new("Health",
                "0.29784,0.8749 0.03544,0.87371 0.03571,0.85624 0.02143,0.85552 0.0204,0.95537 0.29781,0.95476",
                new Rect(0.0204, 0.85552, 0.27745, 0.09984), new Spot(0.03, 0.8478, 0.4863)),
            new("Minimap",
                "0.85137,0.0292 0.85146,0.26284 0.98353,0.26386 0.98424,0.02761;0.84384,0.03041 0.79525,0.0304 0.79521,0.12895 0.8435,0.12997",
                new Rect(0.79521, 0.02761, 0.18903, 0.23625), new Spot(0.261, 0.03, 0.4781)),
        ]),

        // Remaining presets: generic outlines of each game's default 16:9 HUD; redraw in Cutout Studio when HUD settings differ.
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
    /// Builds a built-in as Cutout Studio would save it: masked Cutouts with outline-only masks (no embedded bitmaps). Pure; any thread.
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
    /// Writes missing built-ins when <see cref="AppSettings.BuiltInGamePresetsVersion"/> is behind (pool thread; call off the UI thread).
    /// Returns written files, dated to preserve list order and sort below custom presets; empty if nothing was due. The caller records
    /// the version on the UI thread (<see cref="Seeded"/>).
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

                // An untouched legacy Overwatch built-in (original write date) is renamed rather than duplicated; a modified one keeps
                // its name and the new built-in is written beside it.
                var old = Path.Combine(AppPaths.Styles, OldOverwatchFile);
                var renamed = Path.Combine(AppPaths.Styles, All[0].FileName);
                if (File.Exists(old) && !File.Exists(renamed) && File.GetLastWriteTime(old) == FirstStamp)
                {
                    File.Move(old, renamed, overwrite: false);
                    written.Add(All[0].FileName);
                }

                // A fixed date long past, one minute apart: the first built-in is the newest of them.
                var stamp = FirstStamp;
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
    /// Records seeding on the UI thread. An empty drop-overlay list gets every built-in; a custom list gets Overwatch Vertical once per
    /// version if missing, placed first (left-most target), replacing a renamed legacy Overwatch entry (WriteMissingAsync). Queries the
    /// folder, so files from an earlier unrecorded run still count.
    /// </summary>
    public static void Seeded()
    {
        if (AppSettings.Current.BuiltInGamePresetsVersion >= Version)
            return;

        var present = Directory.Exists(AppPaths.Styles)
            ? Directory.EnumerateFiles(AppPaths.Styles).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        AppSettings.UpdateIf(settings =>
        {
            if (settings.BuiltInGamePresetsVersion >= Version)
                return false;
            if (settings.SplashStylePresets.Count == 0)
                settings.SplashPresetCount = Math.Max(settings.SplashPresetCount, 1);
            settings.SplashStylePresets = SeededList(settings.SplashStylePresets, present.Contains);
            settings.BuiltInGamePresetsVersion = Version;
            return true;
        });
    }

    /// <summary>
    /// Drop overlay list after seeding (pure; golden built-in-game-presets): empty lists get all present built-ins in order; custom lists
    /// get Overwatch Vertical first (replacing a stale legacy Overwatch entry), then their existing order.
    /// </summary>
    public static List<string> SeededList(IReadOnlyList<string> list, Func<string, bool> exists)
    {
        var first = All[0].FileName;
        if (list.Count == 0)
            return All.Select(b => b.FileName).Where(exists).ToList();
        if (!exists(first))
            return [.. list];

        var renamed = list.Select(f => string.Equals(f, OldOverwatchFile, StringComparison.OrdinalIgnoreCase) && !exists(OldOverwatchFile) ? first : f);
        var mine = renamed.Where(f => !string.Equals(f, first, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        return [first, .. mine];
    }
}
