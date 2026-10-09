namespace HandPegApp.Models;

/// <summary>How auto-captions look and move. Colours are written #RRGGBB.</summary>
public sealed class CaptionStyle
{
    public const string KaraokeSweep = "Karaoke Sweep";
    public const string TikTokPop = "TikTok Pop";

    public static IReadOnlyList<string> Animations { get; } = [KaraokeSweep, TikTokPop];

    public string FontName { get; set; } = "Arial";

    /// <summary>Size on a frame 1080 pixels on its shorter side; other frame sizes scale it to match.</summary>
    public int FontSize { get; set; } = 72;

    /// <summary>The words as they stand.</summary>
    public string PrimaryColor { get; set; } = "#FFFFFF";

    /// <summary>The word being spoken (TikTok Pop), or the words already spoken (Karaoke Sweep).</summary>
    public string HighlightColor { get; set; } = "#FFE600";

    public string OutlineColor { get; set; } = "#000000";

    /// <summary>Width of the outline, on the same 1080 scale as the font size.</summary>
    public int OutlineThickness { get; set; } = 5;

    /// <summary>Words shown at a time: 2 to 5.</summary>
    public int MaxWordsPerLine { get; set; } = 3;

    public string Animation { get; set; } = TikTokPop;

    public CaptionStyle Clone() => (CaptionStyle)MemberwiseClone();
}
