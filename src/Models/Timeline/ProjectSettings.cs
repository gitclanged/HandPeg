namespace HandPegApp.Models.Timeline;

// ----- Project Settings (5.4A) -----
// Every project has explicit settings: its natural frame, whether that frame stands on its side, its frame rate, colour
// space, pixel aspect and sample rate. The first video opened onto an empty Sequence sets them (or the New Project studio
// does, before any file), and Export's "Match Project Settings" follows them. The values live flat on TimelineDocument,
// each left out of the file while it keeps its default, so a project made before 5.4A reads and writes byte-identical.

/// <summary>The colour space of a project (5.4A): what its pictures are made in. Export still writes BT.709 SDR, tone-mapped from an HDR project.</summary>
public enum ProjectColorSpace
{
    Sdr709 = 0,
    HdrPq = 1,
    Hlg = 2,
}

/// <summary>The shape of a project's pixels (5.4A): square, or stretched to 4:3 or 16:9 by the player that shows the export.</summary>
public enum ProjectPixelAspect
{
    Square = 0,
    FourThree = 1,
    SixteenNine = 2,
}

/// <summary>
/// A read-only view of a project's settings (5.4A, <see cref="TimelineDocument.ProjectSettings"/>), never itself written to a
/// file: the natural frame before <paramref name="Vertical"/> turns it on its side, the rate, the colour space, the pixel
/// aspect and the sample rate in hertz.
/// </summary>
public sealed record ProjectSettings(int NaturalWidth, int NaturalHeight, bool Vertical, FrameRate FrameRate,
    ProjectColorSpace ColorSpace, ProjectPixelAspect PixelAspect, int SampleRate)
{
    /// <summary>The sample rates a project can have, in hertz.</summary>
    public static IReadOnlyList<int> SampleRates { get; } = [48000, 44100, 96000];

    /// <summary>A colour space as the user reads it.</summary>
    public static string Label(ProjectColorSpace colorSpace) => colorSpace switch
    {
        ProjectColorSpace.HdrPq => "HDR10 PQ",
        ProjectColorSpace.Hlg => "HLG",
        _ => "BT.709 SDR",
    };

    /// <summary>A pixel aspect as the user reads it.</summary>
    public static string Label(ProjectPixelAspect pixelAspect) => pixelAspect switch
    {
        ProjectPixelAspect.FourThree => "4:3",
        ProjectPixelAspect.SixteenNine => "16:9",
        _ => "Square Pixels (1:1)",
    };

    /// <summary>The sample aspect an export asks for (<c>-aspect</c>), or null for square pixels, which need none.</summary>
    public static string? SampleAspectOf(ProjectPixelAspect pixelAspect) => pixelAspect switch
    {
        ProjectPixelAspect.FourThree => "4/3",
        ProjectPixelAspect.SixteenNine => "16/9",
        _ => null,
    };

    /// <summary>The colour space a picture's transfer characteristic (as the probe reads it) means: PQ, HLG, else BT.709 SDR.</summary>
    public static ProjectColorSpace ColorSpaceOf(string? transfer) => transfer switch
    {
        "smpte2084" => ProjectColorSpace.HdrPq,
        "arib-std-b67" => ProjectColorSpace.Hlg,
        _ => ProjectColorSpace.Sdr709,
    };

    /// <summary>Whether a colour space is one of the HDR ones.</summary>
    public static bool IsHdr(ProjectColorSpace colorSpace) => colorSpace != ProjectColorSpace.Sdr709;
}
