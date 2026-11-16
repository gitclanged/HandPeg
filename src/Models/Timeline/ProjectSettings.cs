namespace HandPegApp.Models.Timeline;

// ----- Project Settings -----
// Explicit per-project frame, orientation, frame rate, colour space, pixel aspect and sample rate. Set by the first video on an empty
// Sequence (or the New Project dialog); Export's "Match Project Settings" follows them. Stored flat on TimelineDocument and omitted at
// defaults so older projects round-trip byte-identically.

/// <summary>Project working colour space; export writes BT.709 SDR (tone-mapped from HDR).</summary>
public enum ProjectColorSpace
{
    Sdr709 = 0,
    HdrPq = 1,
    Hlg = 2,
}

/// <summary>Pixel aspect: square, or anamorphic 4:3/16:9 (stretched by the playback device).</summary>
public enum ProjectPixelAspect
{
    Square = 0,
    FourThree = 1,
    SixteenNine = 2,
}

/// <summary>
/// Read-only snapshot of project settings (<see cref="TimelineDocument.ProjectSettings"/>, not serialized): natural frame before
/// <paramref name="Vertical"/> rotation, frame rate, colour space, pixel aspect and sample rate (Hz).
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

    /// <summary>Maps a probed transfer characteristic to a colour space: PQ, HLG, else BT.709 SDR.</summary>
    public static ProjectColorSpace ColorSpaceOf(string? transfer) => transfer switch
    {
        "smpte2084" => ProjectColorSpace.HdrPq,
        "arib-std-b67" => ProjectColorSpace.Hlg,
        _ => ProjectColorSpace.Sdr709,
    };

    /// <summary>Whether a colour space is one of the HDR ones.</summary>
    public static bool IsHdr(ProjectColorSpace colorSpace) => colorSpace != ProjectColorSpace.Sdr709;
}
