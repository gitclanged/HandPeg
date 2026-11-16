using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Shared frozen-brush helpers (Law 2: every non-animated Freezable is frozen), replacing per-file <c>Frozen</c> copies, plus the
/// split-wipe chrome drawn over the player for the Live Encoded Preview and the Color Studio Before/After.
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

    /// <summary>Freezes <paramref name="freezable"/> if possible (e.g. a gradient or drawing) and returns it.</summary>
    public static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }

    // ----- Split-wipe chrome over the picture -----

    /// <summary>Foreground for over-picture chrome: the wipe divider line and the grip glyph.</summary>
    public static readonly SolidColorBrush PictureInk = Frozen(0xF2, 0xF2, 0xF2);

    private static readonly SolidColorBrush GripFill = Frozen(0xD8, 0x1E, 0x1E, 0x22), GripEdge = Frozen(0xC0, 0xF2, 0xF2, 0xF2);

    // Alpha 1: makes the handle column hit-testable while staying visually invisible.
    private static readonly SolidColorBrush HitOnly = Frozen(0x01, 0, 0, 0);

    /// <summary>Divider grip: 18 x 34 DIPs, dark rounded rectangle with a gripper glyph.</summary>
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
    /// Split-wipe handle: an 18-DIP hit-testable column with a horizontal resize cursor and a centred <see cref="WipeGrip"/>. The caller
    /// positions it, sets its height to the player's and handles the drag.
    /// </summary>
    public static Border WipeHandle(string toolTip, string automationName)
    {
        var handle = new Border { Width = 18, Background = HitOnly, Cursor = Cursors.SizeWE, Child = WipeGrip(), ToolTip = toolTip };
        AutomationProperties.SetName(handle, automationName);
        return handle;
    }

    /// <summary>Non-hit-testable divider line; the caller sizes and positions it.</summary>
    public static Border WipeDivider() => new() { IsHitTestVisible = false, Background = PictureInk };
}
