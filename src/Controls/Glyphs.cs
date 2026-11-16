using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Named Segoe MDL2 Assets glyphs for code-behind, so source never contains raw private-use characters (invisible in editors and
/// diffs). Render them with <see cref="Font"/>.
/// </summary>
internal static class Glyphs
{
    /// <summary>Icon font (Segoe MDL2 Assets, Windows 10 and 11).</summary>
    public static readonly FontFamily Font = new("Segoe MDL2 Assets");

    // Window caption buttons.
    public const string ChromeMinimize = "\uE921", ChromeMaximize = "\uE922", ChromeRestore = "\uE923", ChromeClose = "\uE8BB";

    // Expanders, sort marks and menus.
    public const string ChevronDown = "\uE70D", ChevronUp = "\uE70E", ChevronRight = "\uE76C";

    // Commands and states.
    public const string Cancel = "\uE711", CheckMark = "\uE73E", Switch = "\uE8AB", Stopwatch = "\uE916", Color = "\uE790";
    public const string Play = "\uE768", Pause = "\uE769", Stop = "\uE71A";

    // Visibility: View (eye) for shown and Live Encoded Preview, RedEye for an enabled effect card, Hide for hidden or bypassed.
    public const string View = "\uE890", RedEye = "\uE7B3", Hide = "\uED1A";

    // Row mute, lock and voice toggles.
    public const string Mute = "\uE74F", Volume = "\uE767", Lock = "\uE72E", Unlock = "\uE785", Microphone = "\uE720";

    // Grippers: split-wipe handle (horizontal bars) and dock tab drag handle (vertical bars).
    public const string GripperBarHorizontal = "\uE76F", GripperBarVertical = "\uE784";

    // Media Browser placeholders for entries without a thumbnail.
    public const string HardDrive = "\uEDA2", Folder = "\uE8B7", Audio = "\uE8D6", Photo = "\uEB9F", Video = "\uE714";
}
