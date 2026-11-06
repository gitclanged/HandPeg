using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// HandPeg's own UI scale (Pass 5.2-HotfixAndDeBloat, the user's 13-inch laptop at 2880 x 1800 and 200 %): ui-scale-screens, the
/// screen-aware scale worked out for the monitors people have (UiScale.BasePixelsPerDip, no window needed); and
/// ui-scale-popups, a real window on this PC's own monitor with a popup, a tooltip, a context menu, a popup placed by
/// UiScale.PlaceRelative and the Quick-Start Tour on it, each measured in screen pixels. Every one must be drawn at the
/// window's pixels per DIP and stand where it is put, whatever this monitor's scale (the text is the same on any monitor).
/// </summary>
public static class UiScaleTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("ui-scale-screens", "# UiScale.BasePixelsPerDip (5.2): a content DIP in real pixels by monitor (its longer side, Windows' scale), before the UI Scale.", Screens);
        yield return Case("ui-scale-popups", "# A window on this PC's monitor at HandPeg's scale, and every kind of popup over it (5.2: drawn at the window's scale, placed unmirrored).", Popups);
    }

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        var settings = AppSettings.Current;
        var (follow, percent) = (settings.FollowWindowsDpiScaling, settings.UiScalePercent);
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (follow, percent);
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    // ----- ui-scale-screens -----

    private static void Screens(StringBuilder text, List<string> problems)
    {
        var settings = AppSettings.Current;
        settings.UiScalePercent = 100;
        (int Pixels, double Scale, string Name)[] monitors =
        [
            (1920, 1.0, "a 1080p desktop at 100 %"), (1920, 1.25, "a 1080p laptop at 125 %"), (1920, 1.5, "a 1080p laptop at 150 %"),
            (2560, 1.25, "a 1440p desktop at 125 %"), (2560, 1.5, "a 1440p screen at 150 %"), (3440, 1.25, "a 3440 ultrawide at 125 %"),
            (2560, 1.75, "a 2560 x 1600 laptop at 175 %"), (2880, 2.0, "a 13-inch laptop's 2880 x 1800 at 200 %"),
            (3840, 1.5, "a 4K desktop at 150 %"), (3840, 1.75, "a 4K screen at 175 %"), (3840, 2.0, "a 4K screen at 200 %"),
        ];

        Line(text, $"monitor: off (HandPeg's own, as it comes) / followed (Windows' scale)");
        foreach (var (pixels, scale, name) in monitors)
        {
            settings.FollowWindowsDpiScaling = false;
            var own = UiScale.BasePixelsPerDip(scale, pixels);
            settings.FollowWindowsDpiScaling = true;
            var followed = UiScale.BasePixelsPerDip(scale, pixels);
            Line(text, $"  {name} ({pixels} px): {own:0.###} / {followed:0.###}, {pixels / own:0} / {pixels / followed:0} DIPs across");

            if (own > scale + 1e-9)
                problems.Add($"{name}: HandPeg's own scale {own} is larger than Windows' {scale}");
            if (Math.Abs(followed - scale) > 1e-9)
                problems.Add($"{name}: followed, {followed} instead of Windows' {scale}");
        }

        settings.FollowWindowsDpiScaling = false;
        var laptop = UiScale.BasePixelsPerDip(2.0, 2880);
        Line(text, $"a 13-inch laptop at 2880 x 1800 and 200 %: 1.5 pixels a DIP, a workspace 1920 DIPs across (not 1:1, which drew it at half Windows' size)");
        if (Math.Abs(laptop - 1.5) > 1e-9)
            problems.Add($"the 13-inch laptop: {laptop} pixels a DIP instead of 1.5");
        Line(text, $"a desktop monitor (125 % or less, or 2560 pixels or fewer at 150 % or less): 1:1, every pixel the editor's");
        foreach (var (pixels, scale, name) in monitors.Where(m => m.Scale <= 1.25 || (m.Pixels <= 2560 && m.Scale <= 1.5)))
        {
            if (UiScale.BasePixelsPerDip(scale, pixels) != 1)
                problems.Add($"{name}: not 1:1");
        }

        foreach (var percent in new[] { 75, 150 })
        {
            settings.UiScalePercent = percent;
            Line(text, $"the 13-inch laptop with the UI Scale at {percent} %: {UiScale.PixelsPerDip(2.0, 2880):0.###} pixels a DIP");
        }
    }

    // ----- ui-scale-popups -----

    private static void Idle()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static (Point From, Point To) OnScreen(FrameworkElement element) =>
        (element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

    private static double PerDip(FrameworkElement element)
    {
        var (from, to) = OnScreen(element);
        return (to.X - from.X) / element.ActualWidth;
    }

    private static Border Box() => new() { Width = 200, Height = 100, Background = Brushes.Red };

    private static void Popups(StringBuilder text, List<string> problems)
    {
        var settings = AppSettings.Current;
        (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (false, 100);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        UiScale.Follow();

        var target = new Border { Width = 100, Height = 40, Background = Brushes.Blue, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(50) };
        var grid = new Grid();
        grid.Children.Add(target);
        var window = new Window { Left = 100, Top = 100, Width = 600, Height = 400, Content = grid, ShowActivated = false, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        var mainWindow = app.MainWindow;
        app.MainWindow = window;
        var popups = new List<Popup>();
        try
        {
            window.Show();
            Idle();
            var monitor = UiScale.MonitorScale(window);
            var pixels = UiScale.MonitorPixels(window);
            var expected = UiScale.PixelsPerDip(monitor, pixels);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"        this monitor: {pixels} px at {monitor * 100:0} %, menus drop {(SystemParameters.MenuDropAlignment ? "to the left (mirrored)" : "to the right")}: {expected:0.###} pixels a DIP"));

            void Drawn(string what, FrameworkElement element)
            {
                Line(text, $"  {what}: drawn at the window's pixels per DIP");
                if (Math.Abs(PerDip(element) - expected) > 0.02)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: {PerDip(element):0.###} pixels a DIP, the window's {expected:0.###}"));
            }

            Line(text, $"a window at HandPeg's scale for its monitor:");
            Drawn("its content", target);

            var popupBox = Box();
            var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, Child = popupBox, AllowsTransparency = true };
            popups.Add(popup);
            UiScale.Track(popup);
            popup.IsOpen = true;
            Idle();
            Drawn("a popup made in code (UiScale.Track)", popupBox);
            popup.IsOpen = false;
            Idle();
            popup.IsOpen = true;
            Idle();
            Drawn("the same popup opened again", popupBox);
            popup.IsOpen = false;

            var tipBox = Box();
            var tip = new ToolTip { Content = tipBox, PlacementTarget = target, Placement = PlacementMode.Bottom };
            tip.IsOpen = true;
            Idle();
            Drawn("a tooltip", tipBox);
            tip.IsOpen = false;

            var menuBox = Box();
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
            menu.Items.Add(new MenuItem { Header = menuBox });
            menu.IsOpen = true;
            Idle();
            Drawn("a context menu", menuBox);
            menu.IsOpen = false;
            Idle();

            // Where PlaceRelative puts a popup: its top left that many of the target's DIPs from the target's, mirrored menus or not.
            var placed = Box();
            var relative = new Popup { AllowsTransparency = true, Child = placed };
            popups.Add(relative);
            UiScale.Track(relative);
            var (targetFrom, targetTo) = OnScreen(target);
            void At(string what, double x, double y)
            {
                Line(text, $"  {what}");
                var (from, _) = OnScreen(placed);
                var wanted = new Point(targetFrom.X + x * expected, targetFrom.Y + y * expected);
                if (Math.Abs(from.X - wanted.X) > 1.5 || Math.Abs(from.Y - wanted.Y) > 1.5)
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{what}: at {from.X:0},{from.Y:0}, not {wanted.X:0},{wanted.Y:0}"));
            }

            UiScale.PlaceRelative(relative, target, 30, 20);
            relative.IsOpen = true;
            Idle();
            At("a popup placed 30, 20 DIPs from the target's top left (UiScale.PlaceRelative) is there", 30, 20);
            UiScale.PlaceRelative(relative, target, 60, 0);
            Idle();
            At("and moved while open to 60, 0, it is there", 60, 0);
            relative.IsOpen = false;

            // The Quick-Start Tour on the target: the ring 4 DIPs round it, the card 12 DIPs under it, at the window's scale.
            var tour = new QuickStartTour(window, [new QuickStartTour.Step("Title", "Text", () => target)]);
            tour.Start();
            Idle();
            var borders = PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<Visual>().SelectMany(Descendants).OfType<Border>().ToList();
            var ring = borders.First(b => b.BorderThickness.Left == 2 && b.CornerRadius.TopLeft == 6);
            var card = borders.First(b => b.Width == 340);
            var (ringFrom, ringTo) = OnScreen(ring);
            var pad = 4 * expected;
            Line(text, $"the Quick-Start Tour on it:");
            Line(text, $"  its ring 4 DIPs round the target, on every side");
            if (Math.Abs(ringFrom.X - (targetFrom.X - pad)) > 1.5 || Math.Abs(ringFrom.Y - (targetFrom.Y - pad)) > 1.5 || Math.Abs(ringTo.X - (targetTo.X + pad)) > 1.5 || Math.Abs(ringTo.Y - (targetTo.Y + pad)) > 1.5)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"the tour's ring {ringFrom.X:0},{ringFrom.Y:0}-{ringTo.X:0},{ringTo.Y:0} is not 4 DIPs round {targetFrom.X:0},{targetFrom.Y:0}-{targetTo.X:0},{targetTo.Y:0}"));
            Drawn("its card", card);
            Line(text, $"  its card 12 DIPs under the target");
            var (cardFrom, _) = OnScreen(card);
            if (Math.Abs(cardFrom.Y - (targetTo.Y + 12 * expected)) > 1.5)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"the tour's card at {cardFrom.Y:0}, not 12 DIPs under the target's {targetTo.Y:0}"));
            tour.Stop();
        }
        finally
        {
            foreach (var popup in popups)
                popup.IsOpen = false;
            window.Close();
            app.MainWindow = mainWindow;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var below in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return below;
        }
    }
}
