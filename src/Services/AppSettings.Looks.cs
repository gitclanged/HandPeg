namespace HandPegApp.Services;

// ----- How new clips are placed on the frame (4.4) -----

public sealed partial class AppSettings
{
    /// <summary>What <see cref="NewClipSizing"/> can be: the whole picture shows, or the picture covers the frame.</summary>
    public const string NewClipFit = "fit", NewClipFill = "fill";

    /// <summary>
    /// How a picture or a video added to the timeline is sized: "fit" (the default: the whole picture shows, touching the
    /// frame's edges on one side) or "fill" (it covers the whole frame, what reaches past its edges cut off). Either way its
    /// middle is the frame's. Templates, styles and HUD pieces keep the places they were made with.
    /// </summary>
    public string NewClipSizing { get; set; } = NewClipFit;

    /// <summary>What <see cref="NewClipSizing"/> can be, with the words Settings shows for each.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> NewClipSizings { get; } =
    [
        new(NewClipFit, "Fit (the whole picture shows)"),
        new(NewClipFill, "Fill (cover the frame)"),
    ];

    /// <summary>Whether new clips cover the frame (<see cref="NewClipSizing"/> is "fill"); anything else fits them inside it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NewClipsFill => string.Equals(NewClipSizing, NewClipFill, StringComparison.OrdinalIgnoreCase);
}
