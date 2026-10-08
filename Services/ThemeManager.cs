using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HandPegApp.Services;

/// <summary>
/// The application's colours: a dark and a light palette, chosen by the Theme setting or by Windows itself,
/// with the accent (buttons, sliders, the selected tab) on the colour the user has chosen as their Windows
/// accent. Every brush is replaced in the application's resources, which everything refers to dynamically,
/// so a change shows at once in every open window.
/// </summary>
public static class ThemeManager
{
    public const string FollowSystem = "Follow System";
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static IReadOnlyList<string> Themes { get; } = [FollowSystem, Dark, Light];

    // The blue the application used before it followed Windows: the fallback when no accent can be read.
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD7);

    // White text sits on the accent, so an accent brighter than this is darkened until the text reads.
    private const double BrightestUsableAccent = 0.55;

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["WindowBackgroundBrush"] = "#1E1E1E", ["PanelBrush"] = "#252525", ["ControlBrush"] = "#2D2D2D", ["ControlHoverBrush"] = "#3A3A3A",
        ["ControlBorderBrush"] = "#3F3F3F", ["TextBrush"] = "#FFFFFF", ["MutedTextBrush"] = "#A0A0A0", ["DisabledTextBrush"] = "#6A6A6A",
        ["WarningTextBrush"] = "#E0A040", ["InsetBrush"] = "#181818",
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["WindowBackgroundBrush"] = "#F3F3F3", ["PanelBrush"] = "#FFFFFF", ["ControlBrush"] = "#F7F7F7", ["ControlHoverBrush"] = "#E6E6E6",
        ["ControlBorderBrush"] = "#C8C8C8", ["TextBrush"] = "#1A1A1A", ["MutedTextBrush"] = "#5F5F5F", ["DisabledTextBrush"] = "#9A9A9A",
        ["WarningTextBrush"] = "#A85C00", ["InsetBrush"] = "#DADADA",
    };

    private static Application? _application;

    /// <summary>Whether the palette in effect is the dark one.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Sets the colours now, and again whenever Windows reports a change of colours or of its own theme.</summary>
    public static void Follow(Application application)
    {
        _application = application;
        Apply();

        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.WindowGlassColor) or nameof(SystemParameters.WindowGlassBrush))
                application.Dispatcher.BeginInvoke(Apply);
        };

        // Switching Windows between light and dark arrives as a general change of preferences.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                application.Dispatcher.BeginInvoke(Apply);
        };
    }

    /// <summary>Puts the theme named in the settings into effect, in every window.</summary>
    public static void Apply()
    {
        if (_application is not { } application)
            return;

        IsDark = AppSettings.Current.Theme switch
        {
            Dark => true,
            Light => false,
            _ => !WindowsUsesLightApps(),
        };

        var resources = application.Resources;
        foreach (var (key, color) in IsDark ? DarkPalette : LightPalette)
            resources[key] = Brush((Color)ColorConverter.ConvertFromString(color));

        var accent = MakeUsable(ReadAccent());
        resources["AccentBrush"] = Brush(accent);
        resources["AccentHoverBrush"] = Brush(Mix(accent, Colors.White, 0.16));
        resources["AccentPressedBrush"] = Brush(Mix(accent, Colors.Black, 0.25));
        resources["OnAccentBrush"] = Brush(Colors.White);

        foreach (Window window in application.Windows)
            ApplyTitleBar(window);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Gives a window the dark or the light title bar, to match. For a window whose handle exists.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        // DWMWA_USE_IMMERSIVE_DARK_MODE
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private static bool WindowsUsesLightApps()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The accent colour from where Windows keeps it, then the window-frame colour WPF reports, then the fallback.
    /// </summary>
    private static Color ReadAccent()
    {
        try
        {
            // Stored as 0xAABBGGRR.
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int value)
                return Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        {
        }

        var glass = SystemParameters.WindowGlassColor;
        return glass.A > 0 ? Color.FromRgb(glass.R, glass.G, glass.B) : FallbackAccent;
    }

    private static Color MakeUsable(Color color)
    {
        for (var i = 0; i < 12 && Brightness(color) > BrightestUsableAccent; i++)
            color = Mix(color, Colors.Black, 0.12);
        return color;
    }

    private static double Brightness(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    private static Color Mix(Color color, Color with, double amount) => Color.FromRgb(
        (byte)(color.R + (with.R - color.R) * amount), (byte)(color.G + (with.G - color.G) * amount), (byte)(color.B + (with.B - color.B) * amount));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
