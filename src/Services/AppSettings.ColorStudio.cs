namespace HandPegApp.Services;

// ----- Color Studio and Video Scopes histogram -----

public sealed partial class AppSettings
{
    /// <summary>
    /// The histogram overlay's channels, by pill label ("YRGB", "RGB" or "Luma"; <see cref="YrgbHistogram.Parse"/>): the Color Studio
    /// curves backdrop and the Video Scopes Histogram share it, and a pill click in either saves it. Internal UI state, no Settings row.
    /// </summary>
    public string ColorHistogramMode { get; set; } = "YRGB";
}
