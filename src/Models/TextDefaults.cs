namespace HandPegApp.Models;

/// <summary>
/// The defaults and limits of a text layer's lettering: what a saved look falls back to and is held to. Plain constants in the model, so a
/// look is read without the text layout service; TextLayout re-exports them.
/// </summary>
public static class TextDefaults
{
    public const string Font = "Segoe UI";
    public const double SmallestSize = 8, LargestSize = 400, Size = 48;
    public const int LargestOutline = 20;
}
