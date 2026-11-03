using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

/// <summary>
/// What of a masked layer a pointer can land on (5.1-CleanupStudioAndFfmpegPurge, the user's directive: the empty space round a HUD
/// cutout never steals a click). The layer's own mask, made as the player makes it (<see cref="MaskBaker"/>, the same recipe: its cut of
/// the picture, turned inside out, grown, nudged) but small, and kept. Asked on every move of the pointer over a box, so it never
/// reads a file on the interface's thread: until the mask is ready the box takes its whole rectangle, as a box with no mask does.
/// </summary>
public static class LayerHitMask
{
    /// <summary>The longest side it is kept at: enough to tell a widget from the air beside it, 37 KB for 16:9.</summary>
    private const int Longest = 256;

    /// <summary>How much of a pixel must show for a press to land on it: a soft edge's faint fringe does not count.</summary>
    private const byte Solid = 32;

    private const int MostKept = 128;

    private sealed record Grid(byte[] Values, int Width, int Height);

    // Keyed by the recipe's content hash (a mask drawn again is a new file date). A null is one on its way, or one that cannot be read.
    private static readonly Dictionary<long, Grid?> Known = [];

    /// <summary>Whether what the layer's mask leaves of it shows at a point of its box (fractions of it, 0 to 1).</summary>
    /// <param name="wait">Waits for a mask that has to be read: only for a test.</param>
    public static bool Shows(Layer layer, double x, double y, double boxWidth, double boxHeight, bool wait = false)
    {
        if (RecipeOf(layer, boxWidth, boxHeight) is not { } recipe)
            return true;

        var key = recipe.ContentHash();
        Grid? grid;
        var isNew = false;
        lock (Known)
        {
            if (!Known.TryGetValue(key, out grid))
            {
                if (Known.Count >= MostKept)
                    Known.Clear();
                Known[key] = null;
                isNew = true;
            }
        }

        if (isNew)
        {
            if (wait)
                grid = Bake(key, recipe);
            else
                _ = WorkScheduler.Run(new WorkSpec("Reading a mask", WorkLane.Interactive) { Visible = false, Cancellable = false }, _ => Task.Run(() => Bake(key, recipe)));
        }

        if (grid is null)
            return true;

        var column = Math.Clamp((int)(x * grid.Width), 0, grid.Width - 1);
        var row = Math.Clamp((int)(y * grid.Height), 0, grid.Height - 1);
        return grid.Values[row * grid.Width + column] >= Solid;
    }

    private static Grid? Bake(long key, MaskRecipe recipe)
    {
        var read = recipe with { FromAlpha = MaskStore.UsesAlpha(recipe.Path) };
        Grid? grid = null;
        if (MaskBaker.Bake(read) is { } bgra)
        {
            var values = new byte[read.Width * read.Height];
            for (var i = 0; i < values.Length; i++)
                values[i] = bgra[i * 4];
            grid = new Grid(values, read.Width, read.Height);
        }

        lock (Known)
            Known[key] = grid;
        return grid;
    }

    // The player's recipe for this layer (GpuScenePlan's PlaceMasks), small; with no soft edge, which only changes the fringe.
    // Null with no mask on it. Whether the picture is read by its transparency is asked as it is baked: that looks at the file.
    private static MaskRecipe? RecipeOf(Layer layer, double boxWidth, double boxHeight)
    {
        var file = layer.CustomMask && layer.HasMask && layer.FxOn(EffectRegistry.Mask) ? layer.MaskSource : "";
        if (file.Length == 0 || !MaskStore.Exists(file) || boxWidth <= 0 || boxHeight <= 0)
            return null;

        var (width, height) = boxWidth >= boxHeight
            ? (Longest, Math.Max((int)Math.Round(Longest * boxHeight / boxWidth), 1))
            : (Math.Max((int)Math.Round(Longest * boxWidth / boxHeight), 1), Longest);
        (double X, double Y, double Width, double Height) cut = (0, 0, 1, 1);
        if (layer.UsesFrameMask)
        {
            cut = layer.CroppedSource;
        }
        else
        {
            var (left, top, right, bottom) = layer.CropFractions;
            if (left + top + right + bottom > 0)
                cut = (left, top, 1 - left - right, 1 - top - bottom);
        }

        var grow = (int)Math.Round(Math.Clamp(layer.MaskGrow, -Layer.MaskGrowLimit, Layer.MaskGrowLimit) * width / boxWidth);
        var (dx, dy) = ((int)Math.Round(Layer.MaskOffset(layer.MaskOffsetX) / 100.0 * width), (int)Math.Round(Layer.MaskOffset(layer.MaskOffsetY) / 100.0 * height));
        return new MaskRecipe(file, false, width, height, cut, layer.MaskInvert, grow, dx, dy, 0);
    }
}
