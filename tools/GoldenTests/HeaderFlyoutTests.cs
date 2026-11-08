using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Services;
using Side = HandPegApp.Services.UiScale.FlyoutSide;

namespace HandPegGoldenTests;

/// <summary>
/// header-flyouts-placement (Pass 5.3-Stage3, the user's bug: the Layout Selector opened far to the left of the Layout button on the
/// touch-screen laptop, and jumped while layouts were previewed under it). (1) Where UiScale.FlyoutCorner puts a flyout against its
/// button, side by side, and kept inside the room it is given: pure numbers. (2) MainWindow.xaml leaves the five flyouts' placement
/// to code (no Placement or offsets of their own, so nothing WPF mirrors). (3) A real window on this PC's monitor at HandPeg's scale,
/// its panes laid out by a real DockManager in Standard Editing with a header of four buttons: a flyout placed by
/// UiScale.PlaceAgainst against the Layout button opens under it, right edges level, 6 DIPs down; the four built-in layouts are
/// previewed under it (the dock laid out again, the button moved, taken out and put back) and the panes put back as they were, and
/// the flyout never moves by a pixel; opened again, it is under the button where the button now is. A flyout over a button (the
/// Views menu) opens above it, left edges level. What a Relative placement would have done on this PC (mirrored or not) is printed
/// to the console only: it depends on the PC.
/// </summary>
public static class HeaderFlyoutTests
{
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Summary", "Tab:Video", "Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];

    public static GraphResult Run()
    {
        var text = new StringBuilder("# The header's flyouts (5.3-Stage3): one placement in device pixels against the button, never mirrored, never moved by a layout previewed under it.\n");
        var problems = new List<string>();
        var settings = AppSettings.Current;
        var (follow, percent) = (settings.FollowWindowsDpiScaling, settings.UiScalePercent);
        try
        {
            Table(text, problems);
            Xaml(text, problems);
            Live(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (follow, percent);
        }

        return new GraphResult("header-flyouts-placement", text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    // ----- (1) The numbers -----

    private static void Table(StringBuilder text, List<string> problems)
    {
        // A header button 48 x 33 device pixels, a flyout 420 x 300, 9 pixels apart (6 DIPs at 1.5 a DIP), a room 12 in from the left.
        var button = new Rect(600, 40, 48, 33);
        var flyout = new Size(420, 300);
        var room = new Rect(12, 0, 1188, 700);
        Line(text, $"UiScale.FlyoutCorner, a button at (600, 40) 48 x 33, a flyout 420 x 300, 9 apart, the room (12, 0) to (1200, 700):");
        (string What, Side Side, Rect Button, Size Flyout, Rect? Room, Point Wanted)[] cases =
        [
            ("under it, right edges level (the header's flyouts)", Side.BelowEnd, button, flyout, room, new Point(228, 82)),
            ("under it, left edges level", Side.BelowStart, button, flyout, room, new Point(600, 82)),
            ("over it, left edges level (the Views menu)", Side.AboveStart, new Rect(20, 600, 80, 30), new Size(250, 200), null, new Point(20, 391)),
            ("at its left, tops level (the header a strip at the right)", Side.Left, button, flyout, room, new Point(171, 40)),
            ("at its right, tops level (the header a strip at the left)", Side.Right, button, flyout, room, new Point(657, 40)),
            ("under a button near the room's left: kept 12 in", Side.BelowEnd, new Rect(100, 40, 48, 33), flyout, room, new Point(12, 82)),
            ("under a button near the room's foot: kept inside it", Side.BelowEnd, new Rect(600, 650, 48, 33), flyout, room, new Point(228, 400)),
            ("the same with no room: where it falls (Windows keeps it on the screen)", Side.BelowEnd, new Rect(100, 40, 48, 33), flyout, null, new Point(-272, 82)),
        ];
        foreach (var (what, side, at, size, within, wanted) in cases)
        {
            var corner = UiScale.FlyoutCorner(side, at, size, 9, within);
            Line(text, $"  {what}: its top left at ({corner.X:0}, {corner.Y:0})");
            if (corner != wanted)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: ({corner.X:0}, {corner.Y:0}), not ({wanted.X:0}, {wanted.Y:0})"));
        }
    }

    // ----- (2) MainWindow.xaml leaves their place to code -----

    private static void Xaml(StringBuilder text, List<string> problems)
    {
        var main = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Line(text, $"MainWindow.xaml, each placed in code by UiScale.PlaceAgainst (no Placement, PlacementTarget or offsets of its own):");
        foreach (var name in new[] { "LayoutSelector", "LayoutFlyout", "AddMediaFlyout", "SettingsFlyout", "ViewsPopup" })
        {
            var popup = main.Descendants().FirstOrDefault(e => (string?)e.Attribute(x + "Name") == name);
            var own = popup?.Attributes().Select(a => a.Name.LocalName).Where(a => a is "Placement" or "PlacementTarget" or "HorizontalOffset" or "VerticalOffset").ToList() ?? [];
            Line(text, $"  {name}");
            if (popup is null || popup.Name.LocalName != "Popup")
                problems.Add($"{name}: no such Popup in MainWindow.xaml");
            else if (own.Count > 0)
                problems.Add($"{name} still sets {string.Join(", ", own)} in MainWindow.xaml");
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HandPegApp.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }

    // ----- (3) A real window, a real dock, the four built-in layouts previewed under an open flyout -----

    private static void Idle()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    // An element's rectangle on the screen, in device pixels; null while it is in no window or not shown.
    private static Rect? OnScreen(FrameworkElement element)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null)
            return null;

        return new Rect(element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    private static string Of(Rect? rect) => rect is { } r
        ? string.Create(CultureInfo.InvariantCulture, $"({r.X:0}, {r.Y:0}) {r.Width:0} x {r.Height:0}")
        : "not shown";

    private static void Live(StringBuilder text, List<string> problems)
    {
        var settings = AppSettings.Current;
        (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (false, 100);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        UiScale.Follow();

        // The header: Add Media, Layout, Project and Settings, a row (a column while its slot is narrow, as the editor's).
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new[] { "Add Media", "Layout", "Project", "Settings" }.Select(n => new Button { Content = n, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(6, 0, 0, 6) }).ToList();
        foreach (var button in buttons)
            header.Children.Add(button);
        var layoutButton = buttons[1];
        var shapes = new List<PaneShape>();

        // The transport row, the Views button at its left.
        var views = new Button { Content = "Views", Padding = new Thickness(9, 4, 9, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        var playback = new Grid { Height = 40 };
        playback.Children.Add(views);

        var host = new ContentControl { Focusable = false };
        var window = new Window { Left = 40, Top = 40, Width = 1200, Height = 760, Content = host, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        var dock = new DockManager(window, host, () => MainWindow.StandardLayout(TabIds));
        static FrameworkElement Pane(string name) => new Border { Background = Brushes.DimGray, Child = new TextBlock { Text = name } };
        void Add(string id, FrameworkElement content, PaneHandle handle = PaneHandle.TitleSlot, bool fits = false, bool floats = false, bool joins = false, Action<PaneShape>? reshape = null) =>
            dock.Register(new DockPane
            {
                Id = id, Title = id, Content = () => content, Handle = handle, FitsContent = fits, Floats = floats, JoinsPaneBelow = joins, Reshape = reshape,
                MinWidth = () => 160, MinHeight = 60,
            });
        Add("Player", Pane("Player"), PaneHandle.Hover, floats: true);
        Add("Playback", playback, PaneHandle.Hover, fits: true);
        Add("Timeline", new Border { Height = 48, Background = Brushes.DarkSlateGray }, PaneHandle.Hover, fits: true);
        Add("UnifiedTimeline", Pane("Timeline"));
        Add("Layout", Pane("Canvas"));
        Add("Header", header, PaneHandle.Hover, fits: true, joins: true, reshape: shape =>
        {
            shapes.Add(shape);
            header.Orientation = shape == PaneShape.Column ? Orientation.Vertical : Orientation.Horizontal;
        });
        foreach (var id in new[] { "Media", "Keyframes", "Effects", "Meter" })
            Add(id, Pane(id));
        Add("Presets", new Border { Height = 30, Background = Brushes.DarkSlateGray }, PaneHandle.Hover, fits: true);
        foreach (var id in TabIds)
            Add(id, Pane(id), PaneHandle.Tab);

        var mainWindow = app.MainWindow;
        app.MainWindow = window;
        var popups = new List<Popup>();
        Popup Flyout(double width, double height)
        {
            var popup = new Popup { AllowsTransparency = true, StaysOpen = true, Child = new Border { Width = width, Height = height, Background = Brushes.SlateGray } };
            UiScale.Track(popup);
            popups.Add(popup);
            return popup;
        }

        try
        {
            window.Show();
            dock.Load(null);
            Idle();
            var perDip = OnScreen(layoutButton) is { } first ? first.Width / layoutButton.ActualWidth : 1;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"        this monitor: {perDip:0.###} pixels a DIP; menus drop {(SystemParameters.MenuDropAlignment ? "to the left of the hand (Relative, Bottom and Top placement mirrored)" : "to the right (nothing mirrored)")}"));

            // The Layout Selector's way: under the Layout button, right edges level, 6 DIPs down, kept in the panes' area 8 DIPs from its left.
            Line(text, $"a window at HandPeg's scale, its panes laid out by a DockManager in Standard Editing, a header of four buttons:");
            var selector = Flyout(520, 300);
            var buttonAtOpen = OnScreen(layoutButton);
            UiScale.PlaceAgainst(selector, layoutButton, Side.BelowEnd, 6, host, 8);
            selector.IsOpen = true;
            Idle();
            var selectorChild = (FrameworkElement)selector.Child;
            var opened = OnScreen(selectorChild);
            Line(text, $"  a flyout placed against the Layout button opens under it, right edges level, 6 DIPs down");
            if (buttonAtOpen is not { } b || opened is not { } o || Math.Abs(o.Right - b.Right) > 1.5 || Math.Abs(o.Top - (b.Bottom + 6 * perDip)) > 1.5)
                problems.Add($"the flyout opened at {Of(opened)}, the button at {Of(buttonAtOpen)}: not under it, right edges level, 6 DIPs down");

            // What a Relative placement in the panes' area (the selector's until 5.3-Stage3) does on this PC: printed, never compared.
            if (buttonAtOpen is { } at && OnScreen(host) is { } room)
            {
                var old = Flyout(520, 300);
                (old.PlacementTarget, old.Placement) = (host, PlacementMode.Relative);
                (old.HorizontalOffset, old.VerticalOffset) = ((at.Right - room.Left) / perDip - 520, (at.Bottom - room.Top) / perDip + 6);
                old.IsOpen = true;
                Idle();
                var where = OnScreen((FrameworkElement)old.Child);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"        a Relative placement with the same offsets lands {(where is { } w ? w.Right - at.Right : double.NaN):0} px right of the button's right edge (the device-pixel placement: {(opened is { } op ? op.Right - at.Right : double.NaN):0})"));
                old.IsOpen = false;
            }

            // The four built-in layouts previewed under it, then the panes as they were.
            var saved = dock.Save();
            var moved = 0;
            foreach (var (name, layout) in MainWindow.BuiltInLayoutStates(TabIds))
            {
                dock.Preview(dock.Prepared(layout));
                Idle();
                var buttonNow = OnScreen(layoutButton);
                var flyoutNow = OnScreen(selectorChild);
                if (buttonNow != buttonAtOpen)
                    moved++;
                Console.WriteLine($"        {name}: the button at {Of(buttonNow)}, the flyout at {Of(flyoutNow)}");
                Line(text, $"  {name} previewed under it: the flyout has not moved");
                if (flyoutNow != opened)
                    problems.Add($"{name} previewed: the flyout moved from {Of(opened)} to {Of(flyoutNow)}");
            }

            Line(text, $"  the layouts moved the button (or took it out) at least once, so the test proves something");
            if (moved == 0)
                problems.Add("no layout previewed moved the Layout button: the test proves nothing");

            dock.Load(saved);
            Idle();
            var restored = OnScreen(selectorChild);
            Line(text, $"  the panes put back as they were: the flyout has not moved");
            if (restored != opened)
                problems.Add($"the panes put back: the flyout moved from {Of(opened)} to {Of(restored)}");

            // Opened again after a layout kept (Assembly & Media): under the button where it is now.
            selector.IsOpen = false;
            dock.Preview(dock.Prepared(MainWindow.AssemblyLayout(TabIds)));
            Idle();
            var buttonNowKept = OnScreen(layoutButton);
            UiScale.PlaceAgainst(selector, layoutButton, Side.BelowEnd, 6, host, 8);
            selector.IsOpen = true;
            Idle();
            var reopened = OnScreen(selectorChild);
            Line(text, $"  opened again over another layout: under the button where it is now, right edges level, 6 DIPs down");
            if (buttonNowKept is not { } k || reopened is not { } r || Math.Abs(r.Right - k.Right) > 1.5 || Math.Abs(r.Top - (k.Bottom + 6 * perDip)) > 1.5)
                problems.Add($"opened again: the flyout at {Of(reopened)}, the button at {Of(buttonNowKept)}");
            selector.IsOpen = false;
            dock.Load(saved);
            Idle();

            // The Views menu's way: over its button at the transport row's left, left edges level, 4 DIPs up.
            var menu = Flyout(250, 200);
            UiScale.PlaceAgainst(menu, views, Side.AboveStart, 4);
            menu.IsOpen = true;
            Idle();
            var viewsAt = OnScreen(views);
            var menuAt = OnScreen((FrameworkElement)menu.Child);
            Line(text, $"  a flyout placed over the Views button opens above it, left edges level, 4 DIPs up");
            if (viewsAt is not { } v || menuAt is not { } m || Math.Abs(m.Left - v.Left) > 1.5 || Math.Abs(m.Bottom - (v.Top - 4 * perDip)) > 1.5)
                problems.Add($"the Views flyout at {Of(menuAt)}, its button at {Of(viewsAt)}: not above it, left edges level, 4 DIPs up");
            menu.IsOpen = false;
        }
        finally
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
            window.Close();
            app.MainWindow = mainWindow;
        }
    }
}
