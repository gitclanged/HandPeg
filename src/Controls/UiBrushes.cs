using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Frozen brushes (Law 2: every non-animated Freezable is frozen) and the chrome drawn over the player's picture: one helper instead
/// of a private <c>Frozen</c> copy per file, and one split-wipe handle for the Live Encoded Preview and the Color Studio's Before/After.
/// </summary>
internal static class UiBrushes
{
    /// <summary>A frozen solid brush of <paramref name="color"/>.</summary>
    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>A frozen opaque solid brush.</summary>
    public static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(Color.FromRgb(r, g, b));

    /// <summary>A frozen solid brush with alpha.</summary>
    public static SolidColorBrush Frozen(byte a, byte r, byte g, byte b) => Frozen(Color.FromArgb(a, r, g, b));

    /// <summary>Freezes <paramref name="freezable"/> when it can be (a gradient, a drawing) and returns it.</summary>
    public static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }

    // ----- Split wipes over the picture -----

    /// <summary>The ink of chrome drawn over the picture: a split wipe's divider line and its grip's glyph.</summary>
    public static readonly SolidColorBrush PictureInk = Frozen(0xF2, 0xF2, 0xF2);

    private static readonly SolidColorBrush GripFill = Frozen(0xD8, 0x1E, 0x1E, 0x22), GripEdge = Frozen(0xC0, 0xF2, 0xF2, 0xF2);

    // Alpha 1: the handle's column takes the pointer while drawing nothing visible.
    private static readonly SolidColorBrush HitOnly = Frozen(0x01, 0, 0, 0);

    /// <summary>The grip in the middle of a split wipe's divider: 18 x 34 DIPs, dark and rounded, with a gripper glyph.</summary>
    public static Border WipeGrip() => new()
    {
        Width = 18,
        Height = 34,
        CornerRadius = new CornerRadius(4),
        Background = GripFill,
        BorderBrush = GripEdge,
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = Glyphs.GripperBarHorizontal, FontFamily = Glyphs.Font, FontSize = 10, Foreground = PictureInk,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>
    /// A split wipe's handle: an 18-DIP column the player's height that takes the pointer, with the resize cursor and
    /// <see cref="WipeGrip"/> in its middle. The caller places it, sets its height and handles the drag.
    /// </summary>
    public static Border WipeHandle(string toolTip, string automationName)
    {
        var handle = new Border { Width = 18, Background = HitOnly, Cursor = Cursors.SizeWE, Child = WipeGrip(), ToolTip = toolTip };
        AutomationProperties.SetName(handle, automationName);
        return handle;
    }

    /// <summary>A split wipe's divider line (sized and placed by the caller): never takes the pointer.</summary>
    public static Border WipeDivider() => new() { IsHitTestVisible = false, Background = PictureInk };
}
