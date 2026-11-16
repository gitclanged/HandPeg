using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// The Segoe MDL2 Assets icons the code-behind sets, by name: never a raw private-use character in source (invisible in an editor and
/// in a diff). Show them in <see cref="Font"/>.
/// </summary>
internal static class Glyphs
{
    /// <summary>The icon font (Segoe MDL2 Assets, Windows 10 and 11).</summary>
    public static readonly FontFamily Font = new("Segoe MDL2 Assets");

    // Window caption buttons.
    public const string ChromeMinimize = "\uE921", ChromeMaximize = "\uE922", ChromeRestore = "\uE923", ChromeClose = "\uE8BB";

    // Expanders, sort marks and menus.
    public const string ChevronDown = "\uE70D", ChevronUp = "\uE70E", ChevronRight = "\uE76C";

    // Commands and states.
    public const string Cancel = "\uE711", CheckMark = "\uE73E", Switch = "\uE8AB", Stopwatch = "\uE916", Color = "\uE790";
    public const string Play = "\uE768", Pause = "\uE769", Stop = "\uE71A";

    // Visibility: View (an eye) for shown and the Live Encoded Preview, RedEye for an enabled effect card, Hide for hidden or bypassed.
    public const string View = "\uE890", RedEye = "\uE7B3", Hide = "\uED1A";

    // A row's sound, lock and voice switches.
    public const string Mute = "\uE74F", Volume = "\uE767", Lock = "\uE72E", Unlock = "\uE785", Microphone = "\uE720";

    // Grippers: the split wipe's handle (horizontal bars) and a dock tab's drag dots (vertical bars).
    public const string GripperBarHorizontal = "\uE76F", GripperBarVertical = "\uE784";

    // The Media Browser's entries without a picture.
    public const string HardDrive = "\uEDA2", Folder = "\uE8B7", Audio = "\uE8D6", Photo = "\uEB9F", Video = "\uE714";
}
