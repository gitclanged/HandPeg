using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HandPegApp.Services;

/// <summary>
/// The application's colours: five themes (Pass 4.1B), chosen in Settings or on the Layout selector's Themes row.
/// Windows System follows Windows' light or dark mode and its accent; the others are fixed palettes with an accent of
/// their own (OLED Black keeps Windows' accent on true black). Every brush is replaced in the application's resources,
/// which everything refers to dynamically, so a change shows at once in every open window. Beside the colours each theme
/// sets its look (Pass 4.1C): the button faces, bevels and borders, hover and chosen fills, the active tab's trace and
/// the corner rounding, read by the one set of control templates in App.xaml. Since Pass 4.1C.2 a palette may give an
/// edge lit from the top left as "upper/lower" (<see cref="EdgeTokens"/>): raised controls and sunken wells, drawn by the
/// one element each control already has (ButtonChrome, the time bars), with no element added.
/// </summary>
public static partial class ThemeManager
{
    public const string WindowsSystem = "Windows System";
    public const string OledBlack = "OLED Black";
    public const string Leonardo = "Leonardo";
    public const string Reno = "Reno";
    public const string RenoDark = "Reno Dark";

    /// <summary>The default: Windows' own light or dark mode, and its accent.</summary>
    public const string FollowSystem = WindowsSystem;

    public static IReadOnlyList<string> Themes { get; } = [WindowsSystem, OledBlack, Leonardo, Reno, RenoDark];

    // The blue the application used before it followed Windows: the fallback when no accent can be read.
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD7);

    // White text sits on Windows' accent, so an accent brighter than this is darkened until the text reads.
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

    // True black for OLED panels: the window and the insets are #000000, panels a step above, borders kept crisp.
    private static readonly Dictionary<string, string> OledPalette = new()
    {
        ["WindowBackgroundBrush"] = "#000000", ["PanelBrush"] = "#0A0A0D", ["ControlBrush"] = "#141419", ["ControlHoverBrush"] = "#222228",
        ["ControlBorderBrush"] = "#2C2C33", ["TextBrush"] = "#FFFFFF", ["MutedTextBrush"] = "#9C9C9C", ["DisabledTextBrush"] = "#5C5C5C",
        ["WarningTextBrush"] = "#F0A840", ["InsetBrush"] = "#000000",
    };

    // The look of a dark grading studio (Pass 4.1C): near-black bars, slate panels and cards, a dark viewer well, quiet
    // flat buttons, and one coral-red that traces what is active (the tab shown, the card or row chosen).
    private static readonly Dictionary<string, string> LeonardoPalette = new()
    {
        ["WindowBackgroundBrush"] = "#161619", ["PanelBrush"] = "#212126", ["ControlBrush"] = "#28282E", ["ControlHoverBrush"] = "#35353E",
        ["ControlBorderBrush"] = "#32323A", ["TextBrush"] = "#C2C3C8", ["MutedTextBrush"] = "#787A82", ["DisabledTextBrush"] = "#55565D",
        ["WarningTextBrush"] = "#E8A33A", ["InsetBrush"] = "#111113", ["CardBrush"] = "#28282E",
        ["ButtonFaceBrush"] = "#2B2B32", ["ButtonTextBrush"] = "#C2C3C8", ["ButtonBorderBrush"] = "#3D3D46", ["ButtonBevelBrush"] = "Transparent",
        ["ButtonHoverBrush"] = "#35353E", ["ButtonPressedBrush"] = "#24242A", ["NeutralButtonBrush"] = "#2B2B32", ["NeutralButtonBorderBrush"] = "#3D3D46",
        ["HoverFillBrush"] = "#35353E", ["HoverBorderBrush"] = "#55555F",
        ["SelectedFillBrush"] = "#3A2C2E", ["SelectedBorderBrush"] = "#E64B35", ["SelectedTextBrush"] = "#E6E7EB", ["ActiveTabTraceBrush"] = "#E64B35",
    };

    // A classic studio light (Pass 4.1C, warmed in 4.1C.2): warm tan and putty panels over darker taupe grooves, darker
    // stone for the tabs not shown and the cards, recessed sand wells (track bars, lists, the ruler, the layout canvas),
    // soft parchment inputs, never a glaring white or a cool silver; squared tan buttons raised by a lit edge (light top
    // and left, dark bottom and right), wells and viewports sunk by the reverse; steel-blue under the pointer and on what
    // is chosen.
    private static readonly Dictionary<string, string> RenoPalette = new()
    {
        ["WindowBackgroundBrush"] = "#9A968C", ["PanelBrush"] = "#C2BEB4", ["ControlBrush"] = "#DAD6CC", ["ControlHoverBrush"] = "#B8CCE0",
        ["ControlBorderBrush"] = "#858177", ["TextBrush"] = "#1C1B18", ["MutedTextBrush"] = "#4E4C46", ["DisabledTextBrush"] = "#7E7A71",
        ["WarningTextBrush"] = "#8A4A00", ["InsetBrush"] = "#ABA79D", ["CardBrush"] = "#B0ACA2", ["InactiveTabBrush"] = "#B0ACA2",
        ["WellBrush"] = "#B4B0A6", ["WellBorderBrush"] = "#5E5A52/#E2DFD6", ["RaisedBorderBrush"] = "#EAE6DC/#646058",
        ["ButtonFaceBrush"] = "#CAC6BC", ["ButtonTextBrush"] = "#1C1B18", ["ButtonBorderBrush"] = "#EAE6DC/#646058", ["ButtonBevelBrush"] = "Transparent",
        ["ButtonHoverBrush"] = "#B8CCE0", ["ButtonPressedBrush"] = "#8EB2D6", ["NeutralButtonBrush"] = "#CAC6BC", ["NeutralButtonBorderBrush"] = "#EAE6DC/#646058",
        ["HoverFillBrush"] = "#B8CCE0", ["HoverBorderBrush"] = "#4A7DA8",
        ["SelectedFillBrush"] = "#8EB2D6", ["SelectedBorderBrush"] = "#2F6290", ["SelectedTextBrush"] = "#1C1B18", ["ActiveTabTraceBrush"] = "#2F6290",
        ["SelectedRowBrush"] = "#ECE9E0", ["SelectedRowBorderBrush"] = "#8A867C",
    };

    // Reno in the dark (Pass 4.1C): gunmetal surfaces, the same squared buttons raised by a lit edge and wells sunk by the
    // reverse (4.1C.2), steel-blue under the pointer and on what is chosen.
    private static readonly Dictionary<string, string> RenoDarkPalette = new()
    {
        ["WindowBackgroundBrush"] = "#1A1C20", ["PanelBrush"] = "#24272D", ["ControlBrush"] = "#1E2126", ["ControlHoverBrush"] = "#263B50",
        ["ControlBorderBrush"] = "#3A3F48", ["TextBrush"] = "#E3E6EA", ["MutedTextBrush"] = "#9AA2AD", ["DisabledTextBrush"] = "#5C636D",
        ["WarningTextBrush"] = "#E0A040", ["InsetBrush"] = "#141619", ["CardBrush"] = "#2A2E35",
        ["WellBorderBrush"] = "#0B0C0E/#383C46", ["RaisedBorderBrush"] = "#464B55/#101215",
        ["ButtonFaceBrush"] = "#2E323A", ["ButtonTextBrush"] = "#E3E6EA", ["ButtonBorderBrush"] = "#464B55/#101215", ["ButtonBevelBrush"] = "Transparent",
        ["ButtonHoverBrush"] = "#263B50", ["ButtonPressedBrush"] = "#30506E", ["NeutralButtonBrush"] = "#2E323A", ["NeutralButtonBorderBrush"] = "#464B55/#101215",
        ["HoverFillBrush"] = "#263B50", ["HoverBorderBrush"] = "#528CC4",
        ["SelectedFillBrush"] = "#30506E", ["SelectedBorderBrush"] = "#6AA2D8", ["SelectedTextBrush"] = "#F0F3F6", ["ActiveTabTraceBrush"] = "#528CC4",
    };

    /// <summary>
    /// The edges a palette may light from the top left, as "upper/lower" (Pass 4.1C.2): the upper colour is the token,
    /// along the top and left; the lower one, along the bottom and right, is the same name with "LowerBrush" for
    /// "Brush" (ButtonBorderLowerBrush, ...), and is null (one colour all round) in a theme that gives a plain colour.
    /// Light over dark raises a control (buttons, toggles, combo boxes, cards), dark over light sinks a well (lists, text
    /// boxes, check boxes, the player, the layout canvas, the time bars).
    /// </summary>
    private static readonly string[] EdgeTokens = ["ButtonBorderBrush", "NeutralButtonBorderBrush", "RaisedBorderBrush", "WellBorderBrush"];

    private static string LowerOf(string edgeToken) => edgeToken[..^"Brush".Length] + "LowerBrush";

    /// <summary>
    /// A theme: its palette, its accent (null: Windows' own), the colour of text on that accent, the rounding of its
    /// controls, cards and boxed tabs (whose bottom corners are always square: they join the panel under them), and the
    /// sunken edge it gives the wells that have none in a flat theme (track bars, ruler, waveforms, the layout canvas).
    /// </summary>
    private sealed record Theme(Dictionary<string, string> Palette, bool Dark, Color? Accent, Color OnAccent,
        double ControlRadius = 4, double CardRadius = 6, double TabRadius = 6, double WellEdge = 0);

    private static Theme Resolve(string theme) => Normalize(theme) switch
    {
        OledBlack => new(OledPalette, true, null, Colors.White),
        Leonardo => new(LeonardoPalette, true, Color.FromRgb(0xE6, 0x4B, 0x35), Colors.White, ControlRadius: 3, CardRadius: 4, TabRadius: 0),
        Reno => new(RenoPalette, false, Color.FromRgb(0x2F, 0x62, 0x90), Colors.White, ControlRadius: 2, CardRadius: 2, TabRadius: 2, WellEdge: 1),
        RenoDark => new(RenoDarkPalette, true, Color.FromRgb(0x4A, 0x82, 0xB8), Colors.White, ControlRadius: 2, CardRadius: 2, TabRadius: 2, WellEdge: 1),
        _ => WindowsUsesLightApps() ? new(LightPalette, false, null, Colors.White) : new(DarkPalette, true, null, Colors.White),
    };

    /// <summary>
    /// The look of buttons, hovered and chosen things and active tabs (Pass 4.1C), for a theme whose palette does not
    /// give its own: the accent's buttons, as HandPeg always had, flat (no bevel), hover in the control hover colour;
    /// lists on the inputs' colour, their edges, the combo boxes' and the cards' the plain control border, and the tabs
    /// not shown open to what is behind them (4.1C.2).
    /// </summary>
    private static Dictionary<string, Color> LookDefaults(Dictionary<string, string> palette, Color accent, Color hover, Color pressed, Color onAccent)
    {
        Color Of(string key) => (Color)ColorConverter.ConvertFromString(palette[key]);
        return new()
        {
            ["CardBrush"] = Of("ControlBrush"), ["InactiveTabBrush"] = Colors.Transparent,
            ["WellBrush"] = Of("ControlBrush"), ["WellBorderBrush"] = Of("ControlBorderBrush"), ["RaisedBorderBrush"] = Of("ControlBorderBrush"),
            ["ButtonFaceBrush"] = accent, ["ButtonTextBrush"] = onAccent, ["ButtonBorderBrush"] = accent, ["ButtonBevelBrush"] = Colors.Transparent,
            ["ButtonHoverBrush"] = hover, ["ButtonPressedBrush"] = pressed,
            ["NeutralButtonBrush"] = Of("ControlHoverBrush"), ["NeutralButtonBorderBrush"] = Of("ControlBorderBrush"),
            ["HoverFillBrush"] = Of("ControlHoverBrush"), ["HoverBorderBrush"] = accent,
            ["SelectedFillBrush"] = pressed, ["SelectedBorderBrush"] = accent, ["SelectedTextBrush"] = onAccent, ["ActiveTabTraceBrush"] = accent,
        };
    }

    /// <summary>
    /// One of <see cref="Themes"/> for a saved name: the settings of HandPeg 4.0 and before said Follow System, Dark or
    /// Light, now Windows System, Reno Dark and Reno (the neutral palettes nearest them).
    /// </summary>
    public static string Normalize(string? theme) => theme switch
    {
        "Dark" => RenoDark,
        "Light" => Reno,
        { } named when Themes.Contains(named) => named,
        _ => WindowsSystem,
    };

    /// <summary>The theme's name as a crash report gives it (the saved name, brought up to date).</summary>
    public static string DisplayName(string? theme) => Normalize(theme);

    private static Application? _application;

    /// <summary>Whether the palette in effect is a dark one.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>The theme in effect: the saved one, or one being previewed (the Layout selector's Themes row).</summary>
    public static string Current { get; private set; } = WindowsSystem;

    /// <summary>Raised after a theme is put into effect (saved or previewed), for what draws with colours of its own.</summary>
    public static event Action? Applied;

    /// <summary>Sets the colours now, and again whenever Windows reports a change of colours or of its own theme.</summary>
    public static void Follow(Application application)
    {
        _application = application;
        Apply();

        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.WindowGlassColor) or nameof(SystemParameters.WindowGlassBrush))
                application.Dispatcher.BeginInvoke(Reapply);
        };

        // Switching Windows between light and dark arrives as a general change of preferences.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                application.Dispatcher.BeginInvoke(Reapply);
        };
    }

    // Windows' colours changed: the theme in effect (a preview stays a preview) is put in again.
    private static void Reapply() => Apply(Current);

    /// <summary>Puts the theme named in the settings into effect, in every window.</summary>
    public static void Apply() => Apply(AppSettings.Current.Theme);

    /// <summary>
    /// Puts a theme into effect without saving it: the Layout selector's live preview (<see cref="Apply()"/> puts the
    /// saved one back). Each brush is made and frozen once per change.
    /// </summary>
    public static void Apply(string theme)
    {
        if (_application is not { } application)
            return;

        Current = Normalize(theme);
        var chosen = Resolve(Current);
        IsDark = chosen.Dark;

        var resources = application.Resources;
        foreach (var key in EdgeTokens)
            resources[LowerOf(key)] = null;
        foreach (var (key, value) in chosen.Palette)
        {
            if (value.Split('/') is [var upper, var lower])
                (resources[key], resources[LowerOf(key)]) = (Brush(ColorOf(upper)), Brush(ColorOf(lower)));
            else
                resources[key] = Brush(ColorOf(value));
        }

        var accent = chosen.Accent ?? MakeUsable(ReadAccent());
        var (hover, pressed) = (Mix(accent, Colors.White, 0.16), Mix(accent, Colors.Black, 0.25));
        resources["AccentBrush"] = Brush(accent);
        resources["AccentHoverBrush"] = Brush(hover);
        resources["AccentPressedBrush"] = Brush(pressed);
        resources["OnAccentBrush"] = Brush(chosen.OnAccent);
        foreach (var (key, color) in LookDefaults(chosen.Palette, accent, hover, pressed, chosen.OnAccent))
        {
            if (!chosen.Palette.ContainsKey(key))
                resources[key] = Brush(color);
        }

        // The chosen row of a list (a track, a segment, a tile): the chosen fill unless the palette gives its own (Reno: warm off-white).
        foreach (var (row, fill) in new[] { ("SelectedRowBrush", "SelectedFillBrush"), ("SelectedRowBorderBrush", "SelectedBorderBrush") })
        {
            if (!chosen.Palette.ContainsKey(row))
                resources[row] = resources[fill];
        }

        resources["ControlCornerRadius"] = new CornerRadius(chosen.ControlRadius);
        resources["CardCornerRadius"] = new CornerRadius(chosen.CardRadius);
        resources["TabCornerRadius"] = new CornerRadius(chosen.TabRadius, chosen.TabRadius, 0, 0);
        resources["WellEdgeThickness"] = new Thickness(chosen.WellEdge);

        foreach (Window window in application.Windows)
            ApplyTitleBar(window);

        Applied?.Invoke();
        CrashReporter.EnvironmentChanged();
    }

    /// <summary>A theme's look in four colours, for its card: the window, a panel, the accent and the text.</summary>
    public static (Color Window, Color Panel, Color Accent, Color Text) Swatch(string theme)
    {
        var chosen = Resolve(theme);
        Color Of(string key) => (Color)ColorConverter.ConvertFromString(chosen.Palette[key]);
        return (Of("WindowBackgroundBrush"), Of("PanelBrush"), chosen.Accent ?? MakeUsable(ReadAccent()), Of("TextBrush"));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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

    /// <summary>
    /// Hides a window from the screen while it keeps being drawn (DWMWA_CLOAK), or shows it again: a window cloaked
    /// before it is shown appears only once it has a frame to show, instead of as an empty white rectangle first.
    /// </summary>
    public static void SetCloaked(Window window, bool cloaked)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var value = cloaked ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 13, ref value, sizeof(int));
    }

    private static bool WindowsUsesLightApps()
    {
        // A self-contained copy does not read the registry: it is dark unless its own settings say otherwise.
        if (AppPaths.IsSelfContained)
            return false;

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
            if (AppPaths.IsSelfContained)
                throw new System.IO.IOException("The registry is not read by a self-contained copy.");

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

    private static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
