using HandPegApp.Models.Timeline;

namespace HandPegApp.Models;

/// <summary>
/// What the New Project studio (5.4A, ProjectManagerWindow) asks the main window to make: a project of this name (and folder, null for
/// the Projects folder) with this output frame (0 x 0 leaves the size to the first clip), rate (null: the first clip's own) and colour
/// space, a Game Preset to start from (null for none) and the files to lay on its Sequence, in order. The dialog never opens anything
/// itself: the window installs a fresh project and hands this to <c>MainWindow.CreateProjectAsync</c>.
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
    /// The output frame of an aspect ("16:9", "9:16" or "1:1"; anything else is 16:9) whose short side is <paramref name="shortSide"/>
    /// pixels: 1080 gives 1920 x 1080, 1080 x 1920 or 1080 x 1080. A custom size (both sides at least 16) is taken as it is instead.
    /// Every side is even (most encoders refuse odd ones) and within 16 to 8192.
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
