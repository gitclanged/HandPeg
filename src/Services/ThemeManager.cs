using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HandPegApp.Services;

/// <summary>
/// Application themes (five), selected in Settings or the Layout selector. Windows System follows OS light/dark and accent; others are
/// fixed palettes (OLED Black keeps the OS accent). Brushes are replaced in application resources (all DynamicResource), so changes apply
/// live. Themes also set look tokens (button faces, bevels, borders, hover/selection fills, active tab trace, corner radius) used by the
/// shared App.xaml templates; palettes may define two-tone "upper/lower" edges (<see cref="EdgeTokens"/>) for raised/sunken styling
/// without extra elements.
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

    // Dark grading-studio palette: near-black bars, slate panels/cards, dark viewer well, flat buttons, coral-red active accent.
    private static readonly Dictionary<string, string> LeonardoPalette = new()
    {
        ["WindowBackgroundBrush"] = "#161619", ["PanelBrush"] = "#212126", ["ControlBrush"] = "#28282E", ["ControlHoverBrush"] = "#35353E",
        ["ControlBorderBrush"] = "#32323A", ["TextBrush"] = "#C2C3C8", ["MutedTextBrush"] = "#787A82", ["DisabledTextBrush"] = "#55565D",
        ["WarningTextBrush"] = "#E8A33A", ["InsetBrush"] = "#111113", ["CardBrush"] = "#28282E", ["TitleBarBrush"] = "#111114",
        ["ButtonFaceBrush"] = "#2B2B32", ["ButtonTextBrush"] = "#C2C3C8", ["ButtonBorderBrush"] = "#3D3D46", ["ButtonBevelBrush"] = "Transparent",
        ["ButtonHoverBrush"] = "#35353E", ["ButtonPressedBrush"] = "#24242A", ["NeutralButtonBrush"] = "#2B2B32", ["NeutralButtonBorderBrush"] = "#3D3D46",
        ["HoverFillBrush"] = "#35353E", ["HoverBorderBrush"] = "#55555F",
        ["SelectedFillBrush"] = "#3A2C2E", ["SelectedBorderBrush"] = "#E64B35", ["SelectedTextBrush"] = "#E6E7EB", ["ActiveTabTraceBrush"] = "#E64B35",
    };

    // Classic mid-tone studio palette (low-glare pewter): pewter panels over darker grooves, slate title bar, darker inactive tabs and
    // cards (outlined), warm taupe wells, muted stone inputs; bevelled square buttons (light top-left, dark bottom-right) and inverse
    // sunken wells; parchment selection row; steel-blue hover/selection; near-black text. Check boxes stay sunken with a dark mark;
    // focus/checked/selected use a 1 px dotted rectangle.
    private static readonly Dictionary<string, string> RenoPalette = new()
    {
        ["WindowBackgroundBrush"] = "#7C7E83", ["PanelBrush"] = "#9A9B9E", ["ControlBrush"] = "#ACA9A1", ["ControlHoverBrush"] = "#9AB8D6",
        ["ControlBorderBrush"] = "#5E6065", ["TextBrush"] = "#141517", ["MutedTextBrush"] = "#383A3E", ["DisabledTextBrush"] = "#5E6064",
        ["WarningTextBrush"] = "#6A3400", ["InsetBrush"] = "#848179", ["CardBrush"] = "#8C8D91", ["InactiveTabBrush"] = "#8C8D91",
        ["TitleBarBrush"] = "#6E7075", ["TabEdgeBrush"] = "#B2B3B6/#5A5C61", ["ActiveTabEdgeBrush"] = "#BABBBE/#5A5C61",
        ["WellBrush"] = "#848179", ["WellBorderBrush"] = "#4A4843/#B0AEA6", ["RaisedBorderBrush"] = "#C6C4BC/#504E49",
        ["ButtonFaceBrush"] = "#A4A29B", ["ButtonTextBrush"] = "#141517", ["ButtonBorderBrush"] = "#C6C4BC/#504E49", ["ButtonBevelBrush"] = "Transparent",
        ["ButtonHoverBrush"] = "#9AB8D6", ["ButtonPressedBrush"] = "#7FA6CC", ["NeutralButtonBrush"] = "#A4A29B", ["NeutralButtonBorderBrush"] = "#C6C4BC/#504E49",
        ["HoverFillBrush"] = "#9AB8D6", ["HoverBorderBrush"] = "#3F6E98",
        ["SelectedFillBrush"] = "#7FA6CC", ["SelectedBorderBrush"] = "#2F6290", ["SelectedTextBrush"] = "#141517", ["ActiveTabTraceBrush"] = "#2F6290",
        ["SelectedRowBrush"] = "#C4C1B8", ["SelectedRowBorderBrush"] = "#5E5C55",
        ["CheckedBoxBrush"] = "#848179", ["CheckedBoxBorderBrush"] = "#4A4843/#B0AEA6", ["CheckMarkBrush"] = "#141517", ["DottedFocusBrush"] = "#2B2D32",
    };

    // Dark variant of Reno: gunmetal surfaces, near-black title bar, the same bevelled buttons and sunken wells, darker outlined inactive
    // tabs, warm-grey selection row, steel-blue hover/checked; check boxes sunken with a light-blue mark; pale dotted focus rectangle.
    private static readonly Dictionary<string, string> RenoDarkPalette = new()
    {
        ["WindowBackgroundBrush"] = "#1A1C20", ["PanelBrush"] = "#24272D", ["ControlBrush"] = "#1E2126", ["ControlHoverBrush"] = "#2B4A68",
        ["ControlBorderBrush"] = "#3A3F48", ["TextBrush"] = "#E3E6EA", ["MutedTextBrush"] = "#9AA2AD", ["DisabledTextBrush"] = "#5C636D",
        ["WarningTextBrush"] = "#E0A040", ["InsetBrush"] = "#141619", ["CardBrush"] = "#2A2E35", ["InactiveTabBrush"] = "#1E2126",
        ["TitleBarBrush"] = "#111317", ["TabEdgeBrush"] = "#3A3E47/#121418", ["ActiveTabEdgeBrush"] = "#4A505C/#4A505C",
        ["WellBrush"] = "#1E2126", ["WellBorderBrush"] = "#0B0C0E/#383C46", ["RaisedBorderBrush"] = "#464B55/#101215",
        ["ButtonFaceBrush"] = "#2E323A", ["ButtonTextBrush"] = "#E3E6EA", ["ButtonBorderBrush"] = "#464B55/#101215", ["ButtonBevelBrush"] = "Transparent",
        ["ButtonHoverBrush"] = "#2B4A68", ["ButtonPressedBrush"] = "#30506E", ["NeutralButtonBrush"] = "#2E323A", ["NeutralButtonBorderBrush"] = "#464B55/#101215",
        ["HoverFillBrush"] = "#2B4A68", ["HoverBorderBrush"] = "#5892C8",
        ["SelectedFillBrush"] = "#2B4A68", ["SelectedBorderBrush"] = "#5892C8", ["SelectedTextBrush"] = "#F0F3F6", ["ActiveTabTraceBrush"] = "#5892C8",
        ["SelectedRowBrush"] = "#383C44", ["SelectedRowBorderBrush"] = "#5A606E",
        ["CheckedBoxBrush"] = "#1E2126", ["CheckedBoxBorderBrush"] = "#0B0C0E/#383C46", ["CheckMarkBrush"] = "#7CC0FF", ["DottedFocusBrush"] = "#A8B2C2",
    };

    /// <summary>
    /// Tokens that may be two-tone "upper/lower": upper colour on top/left uses the token; lower on bottom/right uses the "LowerBrush"
    /// variant (ButtonBorderLowerBrush, ...), null for single-colour themes. Light-over-dark raises controls; dark-over-light sinks wells.
    /// </summary>
    private static readonly string[] EdgeTokens = ["ButtonBorderBrush", "NeutralButtonBorderBrush", "RaisedBorderBrush", "WellBorderBrush", "TabEdgeBrush", "ActiveTabEdgeBrush",
        "CheckedBoxBorderBrush"];

    private static string LowerOf(string edgeToken) => edgeToken[..^"Brush".Length] + "LowerBrush";

    /// <summary>
    /// Theme definition: palette, accent (null = OS accent), on-accent text colour, corner radius for controls/cards/tabs (tab bottoms
    /// stay square), and the sunken edge for wells that flat themes lack.
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
    /// Default look tokens for palettes that do not override them: flat accent buttons, control-hover fill, lists on input colour with
    /// plain borders, open unoutlined inactive tabs, title bar in the window colour, accent-filled check boxes, no dotted focus rectangle.
    /// Pane dividers are a near-black hairline on dark palettes and the control border colour on light ones.
    /// </summary>
    private static Dictionary<string, Color> LookDefaults(Dictionary<string, string> palette, bool dark, Color accent, Color hover, Color pressed, Color onAccent)
    {
        Color Of(string key) => (Color)ColorConverter.ConvertFromString(palette[key]);
        return new()
        {
            ["PaneSeamBrush"] = dark ? Color.FromRgb(0x22, 0x22, 0x28) : Of("ControlBorderBrush"),
            // Overview Scrubber pane: slightly darker than panes and window background so it reads as separate (light palettes slightly less).
            ["ScrubberBrush"] = Mix(Of("WindowBackgroundBrush"), Colors.Black, dark ? 0.25 : 0.04),
            ["CardBrush"] = Of("ControlBrush"), ["InactiveTabBrush"] = Colors.Transparent, ["TitleBarBrush"] = Of("WindowBackgroundBrush"),
            // Inspector cards (Export tab columns): #1E1E22 with #2A2A30 border on dark palettes; one step darker than panels with control
            // borders on light palettes.
            ["InspectorCardBrush"] = dark ? Color.FromRgb(0x1E, 0x1E, 0x22) : Mix(Of("PanelBrush"), Colors.Black, 0.06),
            ["InspectorCardBorderBrush"] = dark ? Color.FromRgb(0x2A, 0x2A, 0x30) : Of("ControlBorderBrush"),
            ["TabEdgeBrush"] = Colors.Transparent, ["ActiveTabEdgeBrush"] = Colors.Transparent,
            ["WellBrush"] = Of("ControlBrush"), ["WellBorderBrush"] = Of("ControlBorderBrush"), ["RaisedBorderBrush"] = Of("ControlBorderBrush"),
            ["ButtonFaceBrush"] = accent, ["ButtonTextBrush"] = onAccent, ["ButtonBorderBrush"] = accent, ["ButtonBevelBrush"] = Colors.Transparent,
            ["ButtonHoverBrush"] = hover, ["ButtonPressedBrush"] = pressed,
            ["NeutralButtonBrush"] = Of("ControlHoverBrush"), ["NeutralButtonBorderBrush"] = Of("ControlBorderBrush"),
            ["HoverFillBrush"] = Of("ControlHoverBrush"), ["HoverBorderBrush"] = accent,
            ["SelectedFillBrush"] = pressed, ["SelectedBorderBrush"] = accent, ["SelectedTextBrush"] = onAccent, ["ActiveTabTraceBrush"] = accent,
            ["CheckedBoxBrush"] = accent, ["CheckedBoxBorderBrush"] = accent, ["CheckMarkBrush"] = onAccent, ["DottedFocusBrush"] = Colors.Transparent,
        };
    }

    /// <summary>
    /// Maps a saved name to one of <see cref="Themes"/>; legacy Follow System/Dark/Light map to Windows System/Reno Dark/Reno.
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

    /// <summary>Active theme (saved or currently previewed).</summary>
    public static string Current { get; private set; } = WindowsSystem;

    /// <summary>Frozen keyframe diamond fill for the active theme.</summary>
    public static Brush KeyFill { get; private set; } = Brushes.Gold;

    /// <summary>The fill of an unkeyed property's hollow diamond in the Keyframes pane (frozen).</summary>
    public static Brush HollowKeyFill { get; private set; } = Brushes.Gold;

    /// <summary>Raised after a theme is applied (saved or preview) for custom-drawn elements.</summary>
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

    // OS colours changed: reapply the active theme (previews stay previews).
    private static void Reapply() => Apply(Current);

    /// <summary>Puts the theme named in the settings into effect, in every window.</summary>
    public static void Apply() => Apply(AppSettings.Current.Theme);

    /// <summary>
    /// Applies a theme without saving (live preview; <see cref="Apply()"/> restores the saved one). Brushes are created and frozen once per change.
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
        foreach (var (key, color) in LookDefaults(chosen.Palette, chosen.Dark, accent, hover, pressed, chosen.OnAccent))
        {
            if (!chosen.Palette.ContainsKey(key))
                resources[key] = Brush(color);
        }

        // List selection fill: the selection colour unless the palette overrides it (Reno: parchment).
        foreach (var (row, fill) in new[] { ("SelectedRowBrush", "SelectedFillBrush"), ("SelectedRowBorderBrush", "SelectedBorderBrush") })
        {
            if (!chosen.Palette.ContainsKey(row))
                resources[row] = resources[fill];
        }

        resources["ControlCornerRadius"] = new CornerRadius(chosen.ControlRadius);
        resources["CardCornerRadius"] = new CornerRadius(chosen.CardRadius);
        resources["TabCornerRadius"] = new CornerRadius(chosen.TabRadius, chosen.TabRadius, 0, 0);
        resources["WellEdgeThickness"] = new Thickness(chosen.WellEdge);
        resources["TabEdgeThickness"] = chosen.Palette.ContainsKey("TabEdgeBrush") ? new Thickness(1, 1, 1, 0) : new Thickness(0);

        // Themes with their own dotted focus rectangle disable WPF's FocusVisualStyle to avoid double rings.
        resources["ThemeFocusVisual"] = resources[chosen.Palette.ContainsKey("DottedFocusBrush") ? "NoFocusVisual" : "StandardFocusVisual"];

        // Keyframe accent: gold on dark palettes; deep amber (diamonds) and darker amber (text) on light ones for contrast.
        KeyFill = Brush(chosen.Dark ? Colors.Gold : Color.FromRgb(0xB8, 0x6E, 0x00));
        HollowKeyFill = Brush(chosen.Dark ? Color.FromArgb(90, 255, 215, 0) : Color.FromArgb(110, 0xB8, 0x6E, 0x00));
        resources["KeyAccentBrush"] = chosen.Dark ? KeyFill : Brush(Color.FromRgb(0x8A, 0x4F, 0x00));

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

    /// <summary>
    /// Theme look tokens as colours without applying them (Settings preview stage): same colours as <see cref="Apply(string)"/> (two-tone
    /// edges by their upper colour) plus control and tab corner radii.
    /// </summary>
    public static (IReadOnlyDictionary<string, Color> Tokens, double ControlRadius, double TabRadius) Look(string theme)
    {
        var chosen = Resolve(theme);
        var tokens = new Dictionary<string, Color>();
        foreach (var (key, value) in chosen.Palette)
            tokens[key] = ColorOf(value.Split('/')[0]);

        var accent = chosen.Accent ?? MakeUsable(ReadAccent());
        tokens["AccentBrush"] = accent;
        foreach (var (key, color) in LookDefaults(chosen.Palette, chosen.Dark, accent, Mix(accent, Colors.White, 0.16), Mix(accent, Colors.Black, 0.25), chosen.OnAccent))
            tokens.TryAdd(key, color);
        return (tokens, chosen.ControlRadius, chosen.TabRadius);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies immersive dark/light mode and, for native title bars, the theme's caption colours (TitleBarBrush, TextBrush). Requires an HWND.
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        // DWMWA_USE_IMMERSIVE_DARK_MODE
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));

        // Custom-chrome windows: set the DWM border colour to the background (DWMWA_BORDER_COLOR, Windows 11 only). Maximized placement
        // (MainWindow.MaximizedPlace) leaves no frame strip on screen to repaint.
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(window) is not null)
        {
            if (window.Background is SolidColorBrush background)
            {
                var border = ColorRef(background.Color);
                _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }

            ApplyCorners(window);
            return;
        }

        // DWMWA_CAPTION_COLOR/DWMWA_TEXT_COLOR (Windows 11 only); skipped for windows without a title bar.
        if (window.WindowStyle == WindowStyle.None)
            return;

        if (window.TryFindResource("TitleBarBrush") is SolidColorBrush bar)
        {
            var caption = ColorRef(bar.Color);
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        }

        if (window.TryFindResource("TextBrush") is SolidColorBrush text)
        {
            var words = ColorRef(text.Color);
            _ = DwmSetWindowAttribute(handle, 36, ref words, sizeof(int));
        }
    }

    // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11) and two of its values, DWMWCP_DONOTROUND and DWMWCP_ROUND.
    private const int DwmwaWindowCornerPreference = 33, DwmwcpDoNotRound = 1, DwmwcpRound = 2;

    /// <summary>The corner preference a window was last given (<see cref="ApplyCorners"/>; 0: none yet).</summary>
    private static readonly DependencyProperty CornersProperty =
        DependencyProperty.RegisterAttached("Corners", typeof(int), typeof(ThemeManager), new PropertyMetadata(0));

    /// <summary>
    /// Sets DWMWA_WINDOW_CORNER_PREFERENCE for custom-chrome windows by state: rounded when restored, square when maximized, unchanged
    /// when minimized. Called from MainWindow.ApplyWindowState, Settings ApplyFrame and <see cref="ApplyTitleBar"/>; DWM is called only on
    /// change. Windows 10 ignores it. GlassFrameThickness is never zero (that creates a window region and disables DWM frame, shadow and corners).
    /// </summary>
    public static void ApplyCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.WindowState == WindowState.Minimized || System.Windows.Shell.WindowChrome.GetWindowChrome(window) is null)
            return;

        var preference = window.WindowState == WindowState.Maximized ? DwmwcpDoNotRound : DwmwcpRound;
        if ((int)window.GetValue(CornersProperty) == preference)
            return;

        window.SetValue(CornersProperty, preference);
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    // A COLORREF: 0x00BBGGRR.
    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    /// <summary>
    /// Cloaks/uncloaks a window (DWMWA_CLOAK) while it keeps rendering, so a window cloaked before Show appears with content instead of a white flash.
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
    /// OS accent colour from the registry, else WPF's window-frame colour, else the fallback.
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
