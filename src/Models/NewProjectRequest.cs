using HandPegApp.Models.Timeline;

namespace HandPegApp.Models;

/// <summary>
/// New Project dialog request (ProjectManagerWindow): name, folder (null = Projects), output frame (0 x 0 = first clip), rate (null = first
/// clip), colour space, optional Game Preset and ordered files for the Sequence. The window installs a fresh project and passes this to
/// <c>MainWindow.CreateProjectAsync</c>.
/// </summary>
public sealed record NewProjectRequest(string Name, string? Folder, int FrameWidth, int FrameHeight, FrameRate? FrameRate,
    ProjectColorSpace ColorSpace, string? StylePath, IReadOnlyList<string> Files)
{
    /// <summary>The aspects the studio offers, as its cards name them.</summary>
    public const string Wide = "16:9", Vertical = "9:16", Square = "1:1";

    /// <summary>The smallest side a custom frame may have, in pixels (as Resolution &amp; Cropping takes a size).</summary>
    public const int SmallestSide = 16;

    /// <summary>The largest side a custom frame may have, in pixels.</summary>
    public const int LargestSide = 8192;

    /// <summary>
    /// Frame size for an aspect ("16:9", "9:16", "1:1"; default 16:9) with short side <paramref name="shortSide"/>, or a custom size (both
    /// sides &gt;= 16). Sides are even and clamped to 16-8192.
    /// </summary>
    public static (int Width, int Height) FrameFor(string aspect, int shortSide, int customW = 0, int customH = 0)
    {
        if (customW >= SmallestSide && customH >= SmallestSide)
            return (Even(customW), Even(customH));

        var side = Even(shortSide <= 0 ? 1080 : shortSide);
        var longSide = Even(side * 16 / 9.0);
        return aspect switch
        {
            Vertical => (side, longSide),
            Square => (side, side),
            _ => (longSide, side),
        };
    }

    private static int Even(double value) => Math.Clamp((int)Math.Round(value / 2) * 2, SmallestSide, LargestSide);
}
