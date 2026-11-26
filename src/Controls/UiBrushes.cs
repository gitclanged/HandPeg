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

    // ----- Adaptive icon contrast (Pass 5.6.5) -----
    // WCAG 2 relative luminance and contrast ratio; icons are non-text content, so 3:1 against what is behind them (1.4.11).

    /// <summary>Minimum contrast an adaptive icon keeps against the surface behind it.</summary>
    public const double IconContrast = 3.0;

    /// <summary>The surface assumed behind an element with no opaque background up its tree (the dark theme's card surface).</summary>
    public static readonly Color DefaultSurface = Color.FromRgb(0x1E, 0x1E, 0x22);

    /// <summary>WCAG relative luminance (0 black .. 1 white) of an opaque colour.</summary>
    public static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    /// <summary>WCAG contrast ratio (1..21) of two opaque colours.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="top"/> composited over the opaque <paramref name="under"/> (alpha blend; the result is opaque).</summary>
    public static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    /// <summary>
    /// Pure adaptive icon colour: <paramref name="icon"/> (composited over the surface) when it already reaches <paramref name="minimum"/>
    /// contrast against <paramref name="surface"/>; otherwise the least mix of it toward white (dark surface) or black (light surface)
    /// that does, in 5% steps (white or black at worst). Hue is kept as far as contrast allows; alpha becomes opaque.
    /// </summary>
    public static Color ContrastSafe(Color icon, Color surface, double minimum = IconContrast)
    {
        surface = Over(surface, DefaultSurface);
        var seen = Over(icon, surface);
        if (ContrastRatio(seen, surface) >= minimum)
            return seen;

        // The pole with the better reach: white over dark surfaces, black over light ones.
        var pole = ContrastRatio(Colors.White, surface) >= ContrastRatio(Colors.Black, surface) ? Colors.White : Colors.Black;
        for (var step = 1; step <= 20; step++)
        {
            var mixed = Over(Color.FromArgb((byte)Math.Round(255 * step / 20.0), pole.R, pole.G, pole.B), seen);
            if (ContrastRatio(mixed, surface) >= minimum)
                return mixed;
        }

        return pole;
    }

    /// <summary>
    /// The opaque colour behind <paramref name="element"/>: the solid backgrounds (Panel, Border, Control, ButtonChrome) of it and its
    /// visual ancestors composited top-down until opaque, over <see cref="DefaultSurface"/> (a popup's root or no background).
    /// </summary>
    public static Color SurfaceBehind(DependencyObject? element)
    {
        var layers = new List<Color>();
        for (var at = element; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : null)
        {
            var brush = at switch
            {
                ButtonChrome chrome => chrome.Background,
                Panel panel => panel.Background,
                Border border => border.Background,
                Control control => control.Background,
                _ => null,
            };
            if (brush is not SolidColorBrush { Color.A: > 0 } solid || solid.Opacity <= 0)
                continue;

            var color = solid.Color;
            color.A = (byte)Math.Round(color.A * Math.Clamp(solid.Opacity, 0, 1));
            layers.Add(color);
            if (color.A == 255)
                break;
        }

        var surface = DefaultSurface;
        for (var i = layers.Count - 1; i >= 0; i--)
            surface = Over(layers[i], surface);
        return surface;
    }
}
