using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.4: the tab hover card scrubbed across its own width, the dock's Rebind that writes nothing when nothing differs, and the one
/// Views button of the transport row.
/// <para>tab-hover-scrub: the pure scrub maps (a tab with its name across the whole Sequence; a narrowed tab about its playhead, as fast as
/// the card; the card edge to edge; the playhead's cell at a narrowed tab's middle), the card's hold on its own clock (tab to gap to card
/// within the grace keeps it, tab to tab never closes it, off both for the grace closes it), and the wiring: the card takes the pointer,
/// its handlers, a click on it chooses the tab with no seek, every immediate close stays, scrubbing asks nothing of the engine, a late
/// thumbnail never covers a scrubbed picture.</para>
/// <para>dock-rebind-fast-path: the pure share and front-tab rules, then a real DockManager laid out at 1280 x 760 with no window: the same
/// arrangement again (and after a border drag) invalidates no grid and keeps every length; one differing only in shares is taken up in
/// place (the same grids, the columns back as they were, the JSON exact); a 0.75/0.25 split loaded as 3/1 writes nothing; shares under 1
/// keep their ratio; a front-tab-only difference still shows that pane; an Active naming a hidden pane activates nothing; anything else
/// is still built again.</para>
/// <para>unified-views-playback-menu: MainWindow.xaml's transport row 26 DIPs tall, its buttons in the Timeline's tool styles (keyed in
/// the row), the Views button an icon alone, the playback bar's settings button and its popup retired, their list (and Dynamic Preview
/// Scaling) a second column of the Views menu, and the code-built Live Encoded Preview toggle in the row's style.</para>
/// </summary>
public static class TabsDockTransportTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("tab-hover-scrub", "# A tab behind's hover card: scrubbed across its own width, held open across the gap under its tab, clicked to bring its tab to the front; nothing asked of the engine.", HoverScrub);
        yield return Case("unified-views-playback-menu", "# One icon-only Views button for the views and the playback bar; the transport row as tall as the Timeline's row of tools, its buttons in that row's styles.", ViewsMenu);
    }

    /// <summary>The cases that need the WPF Application (run after UiScaleTests made it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield return Case("dock-rebind-fast-path", "# The dock taken up in place: nothing written when nothing differs, shares set in place when only they differ, a front tab shown only when it changes.", RebindFastPath);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static bool Near(double a, double b, double within = 1e-9) => Math.Abs(a - b) <= within;

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    /// <summary>A source file under src, its line endings made \n.</summary>
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts])).ReplaceLineEndings("\n");

    private static XDocument Xaml(params string[] parts) => XDocument.Load(Path.Combine([RepositoryRoot(), "src", .. parts]));

    /// <summary>A member's text: from its signature to the end of its body (the next line that closes at its indent).</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..(end + 6)];
    }

    /// <summary>Whether every one of <paramref name="parts"/> is in <paramref name="text"/>, each after the one before.</summary>
    private static bool InOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, at, StringComparison.Ordinal);
            if (found < 0)
                return false;
            at = found + part.Length;
        }

        return true;
    }

    private static XElement? Named(XContainer xaml, string name) => xaml.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == name);

    private static string? Attr(XElement? element, XName name) => element?.Attribute(name)?.Value;

    // ----- tab-hover-scrub -----

    private static void HoverScrub(Action<bool, string> expect)
    {
        // A tab with its name: 0 to 1 across it, as 5.4.2A had it.
        var named = new[] { (0.0, 0.0), (50.0, 0.5), (100.0, 1.0), (150.0, 1.0), (-5.0, 0.0) }
            .All(c => ProjectTabStrip.TabScrub(c.Item1, 100, compact: false, span: 318) is var s && Near(s.Value, c.Item2) && !s.AboutPlayhead);
        expect(named, "across a tab with its name (100 DIPs): 0 at its left, 0.5 at its middle, 1 at its right, held to 0 and 1 beyond (the whole Sequence)");

        // A narrowed tab (28 DIPs, Compact): about its playhead, as if it were as wide as its card.
        var middle = ProjectTabStrip.TabScrub(14, ProjectTabStrip.CompactTabWidth, compact: true, span: 318);
        var left = ProjectTabStrip.TabScrub(0, ProjectTabStrip.CompactTabWidth, compact: true, span: 318);
        var right = ProjectTabStrip.TabScrub(28, ProjectTabStrip.CompactTabWidth, compact: true, span: 318);
        expect(middle.AboutPlayhead && Near(middle.Value, 0) && Near(left.Value, -14.0 / 318) && Near(right.Value, 14.0 / 318) && left.AboutPlayhead && right.AboutPlayhead,
            F($"across a narrowed tab (28 DIPs): an offset from its playhead, 0 at its middle, {left.Value:0.0000} to {right.Value:+0.0000} at its edges (28 of a 318 DIP card: the picture moves as fast as across the card)"));

        // The card: edge to edge, the whole Sequence.
        expect(Near(ProjectTabStrip.CardScrub(0, 318), 0) && Near(ProjectTabStrip.CardScrub(159, 318), 0.5) && Near(ProjectTabStrip.CardScrub(318, 318), 1)
               && Near(ProjectTabStrip.CardScrub(400, 318), 1) && Near(ProjectTabStrip.CardScrub(-3, 191), 0) && Near(ProjectTabStrip.CardScrub(95.5, 191), 0.5)
               && Near(ProjectTabStrip.CardScrub(10, 0), 0) && Near(ProjectTabStrip.CardScrub(double.NaN, 318), 0),
            "across the card (318 wide, or a tall project's 191): 0 at its left edge, 0.5 at its middle, 1 at its right edge, the whole Sequence; no width, nothing");

        // Where along the Sequence: the value itself, or about the playhead's cell (40 of 100: its middle, 0.405).
        expect(Near(MainWindow.ScrubFraction(100, 40, 0.3, aboutPlayhead: false), 0.3) && Near(MainWindow.ScrubFraction(100, 40, 1.7, aboutPlayhead: false), 1)
               && Near(MainWindow.ScrubFraction(100, 40, 0, aboutPlayhead: true), 0.405) && Near(MainWindow.ScrubFraction(100, 40, 14.0 / 318, aboutPlayhead: true), 0.405 + 14.0 / 318)
               && Near(MainWindow.ScrubFraction(100, 99, 0.5, aboutPlayhead: true), 1) && Near(MainWindow.ScrubFraction(100, 0, -0.5, aboutPlayhead: true), 0)
               && Near(MainWindow.ScrubFraction(100, 250, 0, aboutPlayhead: true), 0.995),
            "along the Sequence: the card's or a named tab's value as it is; a narrowed tab's offset from the middle of its playhead's cell (cell 40 of 100: 0.405), held to 0 and 1, a playhead past the sheet at its last cell");
        expect(MainWindow.ScrubCell(100, MainWindow.ScrubFraction(100, 40, 0, aboutPlayhead: true)) == 40 && MainWindow.ScrubCell(100, 1) == 99 && MainWindow.ScrubCell(100, 0) == 0
               && MainWindow.ScrubCell(100, 0.555) == 55 && MainWindow.ScrubCell(0, 0.5) == 0 && MainWindow.ScrubCell(100, double.NaN) == 0,
            "the cell shown: a narrowed tab's middle shows the very picture its playhead is on (cell 40); the end the last cell (99), the start the first; no sheet, none");
        var item = new ProjectTabItem { Id = "card", Title = "Card", ScrubFraction = 0.5 };
        expect(Near(item.ScrubWidth, ProjectTabItem.CardMaxWidth / 2) && item.IsScrubbing,
            "the card's scrub bar reaches where the pointer is across the card (half of a 318 DIP card at 0.5)");

        // The card's hold, on its own clock.
        expect(ProjectTabStrip.HoverCardGraceMs == HoverFlyout.CloseDelayMs && ProjectTabStrip.HoverCardGraceMs == 260,
            "the card waits 260 ms after the pointer left its tab and itself (HoverFlyout's close delay)");
        var hold = new HoverCardHold(ProjectTabStrip.HoverCardGraceMs);
        hold.EnterTab();
        hold.LeaveTab(1000);
        var inGap = !hold.Due(1100) && Near(hold.Remaining(1100), 160);
        hold.EnterCard();
        var onCard = !hold.Due(5000) && hold.IsOver && double.IsPositiveInfinity(hold.Remaining(5000));
        hold.LeaveCard(5000);
        var leftCard = !hold.Due(5259) && hold.Due(5260);
        expect(inGap && onCard && leftCard,
            "tab, then the gap under it (100 ms, 160 to go), then the card: it stays open for as long as the pointer is on the card; off the card, it closes 260 ms later, not before");
        hold.Reset();
        hold.EnterTab();
        hold.LeaveTab(0);
        hold.EnterTab();
        expect(!hold.Due(10_000), "gliding from a tab to the next (the next one's enter right after the last one's leave) never closes it");
        hold.Reset();
        hold.EnterTab();
        hold.LeaveTab(0);
        expect(!hold.Due(259) && hold.Due(260), "off the tab and never onto the card: closed 260 ms later");
        hold.Reset();
        hold.EnterCard();
        hold.LeaveCard(0);
        hold.EnterTab();
        var backOnTab = !hold.Due(1000);
        hold.Reset();
        expect(backOnTab && !hold.Due(1_000_000) && !hold.IsOver, "from the card back onto the tab it stays; closed (Reset), nothing is held and nothing is due");

        // The XAML: the card takes the pointer, on its surface; its body is what the pointer is measured across.
        var strip = Xaml("Controls", "ProjectTabStrip.xaml");
        var card = Named(strip, "HoverCard");
        var surface = Named(strip, "HoverSurface");
        var body = Named(strip, "HoverBody");
        expect(card is { Name.LocalName: "Popup" } && Attr(card, "IsHitTestVisible") is null && Attr(card, "StaysOpen") == "True",
            "ProjectTabStrip.xaml: HoverCard is a Popup that takes the pointer (no IsHitTestVisible False), and stays open on its own (StaysOpen)");
        expect(surface is not null && surface.Parent == card && Attr(surface, "MouseEnter") == "HoverCard_MouseEnter" && Attr(surface, "MouseLeave") == "HoverCard_MouseLeave"
               && Attr(surface, "MouseMove") == "HoverCard_MouseMove" && Attr(surface, "MouseLeftButtonDown") == "HoverCard_MouseLeftButtonDown"
               && Attr(surface, "MouseLeftButtonUp") == "HoverCard_MouseLeftButtonUp",
            "its surface (HoverSurface, the card's outer border) hears the pointer come, go, move, press and release");
        expect(body is { Name.LocalName: "Grid" } && Attr(body, "Width") == "{Binding CardWidth}" && body.Ancestors().Contains(surface),
            "the scrub is measured across the card's body (HoverBody, the card's width)");

        // The wiring.
        var handler = typeof(ProjectTabStrip).GetEvent("HoverScrubbed")?.EventHandlerType;
        expect(handler == typeof(Action<ProjectTabItem, double, bool>), "ProjectTabStrip.HoverScrubbed tells the tab, the value and whether it is about the tab's playhead");
        var code = Read("Controls", "ProjectTabStrip.xaml.cs");
        expect(Method(code, "private void HoverCard_MouseMove(").Contains("CardScrub(e.GetPosition(HoverBody).X, HoverBody.ActualWidth)", StringComparison.Ordinal)
               && Method(code, "private void Tab_MouseMove(").Contains("TabScrub(e.GetPosition(tab).X, tab.ActualWidth, Compact, item.CardWidth)", StringComparison.Ordinal),
            "the card's move scrubs across the card as it is drawn now; a tab's move through TabScrub (narrowed: about its playhead, the card's width as its span)");
        expect(InOrder(Method(code, "private void HoverCard_MouseLeftButtonUp("), "e.Handled = true;", "_cardPressed", "CloseHoverCard();", "TabChosen?.Invoke(item);")
               && Method(code, "private void HoverCard_MouseLeftButtonDown(").Contains("e.Handled = true;", StringComparison.Ordinal),
            "a click on the card (pressed and let go on it) closes it and chooses its tab, as a click on the tab does: no seek; the press and release are the card's own (Handled)");
        expect(InOrder(Method(code, "public void CloseHoverCard()"), "HoverItem = null;", "_hoverHold.Reset();", "_hoverClose?.Stop();", "HoverCard.IsOpen = false;")
               && Method(code, "private void Tab_PreviewMouseLeftButtonDown(").Contains("CloseHoverCard();", StringComparison.Ordinal)
               && Method(code, "private void Tab_MouseUp(").Contains("CloseHoverCard();", StringComparison.Ordinal)
               && Method(code, "internal bool BeginReorder(").Contains("CloseFlyouts();", StringComparison.Ordinal)
               && Method(code, "private void Tab_MouseRightButtonUp(").Contains("CloseFlyouts();", StringComparison.Ordinal)
               && code.Contains("owner.Deactivated += (_, _) => CloseFlyouts();", StringComparison.Ordinal)
               && InOrder(Method(code, "private void Tab_MouseEnter("), "{ IsActive: false }", "CloseHoverCard();", "_hoverHold.EnterTab();", "OpenHoverCard(item, tab);"),
            "every close that was at once still is (a press or a middle click on a tab, a drag, the right-click menu, the window let go, the tab in front entered), and stops the grace's timer");
        expect(InOrder(Method(code, "private void HoverCloseTick()"), "_hoverTab?.IsMouseOver == true || HoverSurface.IsMouseOver", "_hoverHold.Due(now)", "CloseHoverCard();")
               && Method(code, "private void HoldOff(").Contains("_hoverClose = new DispatcherTimer(", StringComparison.Ordinal)
               && !Method(code, "public ProjectTabStrip()").Contains("DispatcherTimer", StringComparison.Ordinal),
            "the grace's timer closes the card only when the pointer is on neither the tab nor the card; it is made the first time a card is left (nothing new before the first frame)");
        var scrub = Method(Read("MainWindow.TabCard.cs"), "private void ScrubTabCard(ProjectTabItem item, double value, bool aboutPlayhead)");
        expect(scrub.Length > 0 && scrub.Contains("sheet.Cell(index)", StringComparison.Ordinal)
               && new[] { "_gpu", "Engine", "Prewarm", "Decode", "Seek" }.All(w => !scrub.Contains(w, StringComparison.Ordinal)),
            "scrubbing shows a cell of the overview sheet already held, and asks nothing of the engine (no decode, no seek, no pre-warm: zero VRAM)");
        expect(Read("MainWindow.TabMenu.cs").Contains("if (ReferenceEquals(ProjectTabStrip.HoverItem, item) && !item.IsScrubbing)", StringComparison.Ordinal),
            "a thumbnail decoded late is given to the card only while it is not being scrubbed (never over a scrubbed picture)");
        expect(Read("MainWindow.ProjectTabs.cs").Contains("ProjectTabStrip.HoverScrubbed += ScrubTabCard;", StringComparison.Ordinal),
            "the window still takes the strip's scrub with ScrubTabCard");
    }

    // ----- dock-rebind-fast-path -----

    private static readonly string[] RigPanes = ["Rig:Player", "Rig:Timeline", "Rig:Media", "Rig:Effects", "Rig:Keyframes"];

    /// <summary>The rig's arrangement: the player over the timeline, the media, and the effects and keyframes in one group, side by side.</summary>
    private static DockLayoutState RigLayout(double[] columns, double[] rows, string front = "Rig:Effects") => new()
    {
        Main = new DockNode
        {
            Kind = DockNode.SplitKind,
            Children =
            [
                DockNode.Split(true, (DockNode.Group("Rig:Player"), rows[0]), (DockNode.Group("Rig:Timeline"), rows[1])),
                DockNode.Group("Rig:Media"),
                new DockNode { Kind = DockNode.GroupKind, Panes = ["Rig:Effects", "Rig:Keyframes"], Active = front },
            ],
            Sizes = [.. columns],
        },
    };

    private static string Json(DockLayoutState state) => JsonSerializer.Serialize(state, AppSettingsJsonContext.Default.DockLayoutState);

    private static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
                yield return found;
            foreach (var deeper in Under<T>(child))
                yield return deeper;
        }
    }

    /// <summary>Every grid's columns and rows as laid out: their lengths and least sizes.</summary>
    private static string Lengths(DependencyObject root) => string.Join(" | ", Under<Grid>(root).Select(g =>
        string.Join(",", g.ColumnDefinitions.Select(c => F($"{c.Width}/{c.MinWidth}"))) + ";" + string.Join(",", g.RowDefinitions.Select(r => F($"{r.Height}/{r.MinHeight}")))));

    /// <summary>
    /// The elements under <paramref name="host"/> not measured or arranged as they are now. A tree's very first pass with no window to pump
    /// it leaves its window root and its grids so (made stale during that pass, never queued again): the checks ask that a load makes no
    /// element stale that was not (<see cref="NothingNew"/>), and count the grids' writes themselves (<see cref="GridWrites"/>).
    /// </summary>
    private static HashSet<UIElement> Stale(FrameworkElement host) => Under<UIElement>(host).Prepend(host).Where(e => !e.IsMeasureValid || !e.IsArrangeValid).ToHashSet();

    private static bool NothingNew(FrameworkElement host, HashSet<UIElement> before) => Stale(host).IsSubsetOf(before);

    /// <summary>Counts every change of a column's width or least width and a row's height or least height in the grids under a host (each lays its grid out again).</summary>
    private sealed class GridWrites
    {
        public GridWrites(FrameworkElement host)
        {
            foreach (var grid in Under<Grid>(host))
            {
                foreach (var column in grid.ColumnDefinitions)
                {
                    Hook(column, ColumnDefinition.WidthProperty);
                    Hook(column, ColumnDefinition.MinWidthProperty);
                }

                foreach (var row in grid.RowDefinitions)
                {
                    Hook(row, RowDefinition.HeightProperty);
                    Hook(row, RowDefinition.MinHeightProperty);
                }
            }
        }

        public int Count { get; set; }

        private void Hook(DependencyObject definition, DependencyProperty property) =>
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, definition.GetType()).AddValueChanged(definition, (_, _) => Count++);
    }

    private static void Lay(FrameworkElement host)
    {
        host.Measure(new Size(1280, 760));
        host.Arrange(new Rect(0, 0, 1280, 760));
        host.UpdateLayout();
    }

    // The top split's grid (five columns: three parts and two borders) and its parts' widths.
    private static Grid TopGrid(FrameworkElement host) => Under<Grid>(host).First(g => g.ColumnDefinitions.Count == 5);

    private static double[] Widths(FrameworkElement host) => TopGrid(host) is var grid ? [grid.ColumnDefinitions[0].ActualWidth, grid.ColumnDefinitions[2].ActualWidth, grid.ColumnDefinitions[4].ActualWidth] : [];

    private static void RebindFastPath(Action<bool, string> expect)
    {
        // The pure rules.
        expect(DockManager.SharesMatch([70, 30], [0.7, 0.3]) && DockManager.SharesMatch([1, 1.3], [10, 13]) && DockManager.SharesMatch([0.5, 0.5], [1, 1])
               && DockManager.SharesMatch([900, 380], [900.05, 379.95]) && DockManager.SharesMatch([], []),
            "shares match as ratios: 70/30 is 0.7/0.3, 1/1.3 is 10/13, 0.5/0.5 is 1/1, a drag's DIPs within 0.01 % of the whole; no shares match no shares");
        expect(!DockManager.SharesMatch([900, 380], [901, 379]) && !DockManager.SharesMatch([1, 1], [1, 1, 1]) && !DockManager.SharesMatch([double.NaN, 1], [1, 1])
               && !DockManager.SharesMatch([0, 1], [0, 1]) && Near(DockManager.ShareTolerance, 1e-4),
            "they differ by 1 DIP of 1280 (0.08 %), in count, or with a part that is not a share (Auto, none)");
        var group = new DockNode { Kind = DockNode.GroupKind, Panes = ["a", "b", "c"], Active = "b" };
        var front = DockManager.FrontOf(group, _ => true);
        group.Active = "a";
        var hiddenActive = DockManager.FrontOf(group, p => p != "a");
        group.Active = null;
        var noActive = DockManager.FrontOf(group, _ => true);
        var noneShown = DockManager.FrontOf(group, _ => false);
        expect(front == "b" && hiddenActive == "b" && noActive == "a" && noneShown is null,
            "the pane a group shows: its Active when shown; an Active that is hidden, or none, the first shown (Render's rule); none shown, none");
        var columns = Json(RigLayout([1, 1, 1], [0.75, 0.25]));
        expect(DockManager.SameButShares(columns, Json(RigLayout([0.5, 0.5, 1.3], [3, 1]))) && !DockManager.SameButShares(columns, Json(new DockLayoutState { Main = DockNode.Group(RigPanes) })),
            "two arrangements the same but for their shares are told apart from ones of another shape");

        // A real dock with the rig's panes, laid out at 1280 x 760 with no window.
        var host = new ContentControl { Focusable = false };
        var shown = RigPanes.ToDictionary(p => p, _ => true);
        var contents = new Dictionary<string, FrameworkElement>();
        var dock = new DockManager(new Window(), host, () => RigLayout([1, 1, 1], [0.75, 0.25]));
        foreach (var id in RigPanes)
        {
            var content = new Border { Background = Brushes.DimGray, Child = new TextBlock { Text = id } };
            contents[id] = content;
            dock.Register(new DockPane { Id = id, Title = id, Content = () => content, MinWidth = () => 120, MinHeight = 60, IsShown = () => shown[id] });
        }

        var changed = 0;
        dock.Changed += () => changed++;
        var start = Json(RigLayout([1, 1, 1], [0.75, 0.25]));
        dock.Load(start);

        Lay(host);
        var grids = Under<Grid>(host).ToList();
        var lengths = Lengths(host);
        var writes = new GridWrites(host);
        expect(grids.Count >= 3 && Widths(host) is [var a, var b, var c] && Near(a, b, 0.5) && Near(b, c, 0.5),
            F($"laid out: {grids.Count} grids, the three columns as wide as each other ({string.Join(", ", Widths(host).Select(w => w.ToString("0.#", CultureInfo.InvariantCulture)))})"));

        // The same arrangement again: nothing written, nothing to lay out.
        var stale = Stale(host);
        dock.Load(dock.Save());
        expect(writes.Count == 0 && NothingNew(host, stale) && Lengths(host) == lengths && Under<Grid>(host).SequenceEqual(grids) && changed == 0,
            "the same arrangement loaded again (Load(Save())): no width, height or least size written, no element made stale, the same grids, nothing raised");

        // A border dragged, then what is saved loaded again: still nothing to write (the screen is what the arrangement says).
        var border = Under<Thumb>(host).First(t => t.GetType().Name == "SeamThumb" && t.GetType().GetProperty("Nudged")?.GetValue(t) is not null
            && !(bool)t.GetType().GetProperty("Vertical")!.GetValue(t)!);
        var before = Widths(host);
        border.RaiseEvent(new DragStartedEventArgs(0, 0));
        border.RaiseEvent(new DragDeltaEventArgs(-120, 0));
        border.UpdateLayout();
        border.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        Lay(host);
        var dragged = Widths(host);
        var draggedLengths = Lengths(host);
        (changed, writes.Count, stale) = (0, 0, Stale(host));
        dock.Load(dock.Save());
        expect(Near(before[0] - dragged[0], 120, 0.5) && writes.Count == 0 && NothingNew(host, stale) && Lengths(host) == draggedLengths && Under<Grid>(host).SequenceEqual(grids),
            F($"a border dragged 120 DIPs ({before[0]:0.#} to {dragged[0]:0.#}), then Load(Save()): nothing written (the screen is what the arrangement says), no element made stale, the same grids"));

        // The arrangement from before the drag (it differs only in its shares): taken up in place, the columns back as they were.
        var parents = RigPanes.ToDictionary(p => p, p => VisualTreeHelper.GetParent(contents[p]));
        writes.Count = 0;
        dock.Load(start);
        var inPlace = Under<Grid>(host).SequenceEqual(grids) && RigPanes.All(p => ReferenceEquals(VisualTreeHelper.GetParent(contents[p]), parents[p]));
        var shareWrites = writes.Count;
        Lay(host);
        var back = Widths(host);
        expect(inPlace && shareWrites == 3 && back.Zip(before).All(p => Near(p.First, p.Second, 0.5)) && dock.Save() == start && changed == 0,
            F($"the arrangement from before the drag loaded (only its shares differ): taken up in place (the same grids, no pane moved, its three column shares set), the columns back ({string.Join(", ", back.Select(w => w.ToString("0.#", CultureInfo.InvariantCulture)))}), saved exactly as loaded"));

        // 0.75/0.25 on screen, loaded as 3/1: the same ratio, nothing written.
        var rowsGrid = Under<Grid>(host).First(g => g.RowDefinitions.Count == 3);
        var threeToOne = Json(RigLayout([1, 1, 1], [3, 1]));
        (writes.Count, stale) = (0, Stale(host));
        dock.Load(threeToOne);
        expect(writes.Count == 0 && NothingNew(host, stale) && rowsGrid.RowDefinitions[0].Height == new GridLength(0.75, GridUnitType.Star) && rowsGrid.RowDefinitions[2].Height == new GridLength(0.25, GridUnitType.Star)
               && Under<Grid>(host).SequenceEqual(grids) && dock.Save() == threeToOne,
            "a 0.75/0.25 split loaded as 3/1: the same ratio, so nothing is written (its rows still 0.75* and 0.25*), the arrangement saved as 3/1");

        // Shares under 1: kept as they are (a drag's SetDips would make 0.5, 0.5, 1.3 into 1, 1, 1.3).
        writes.Count = 0;
        dock.Load(Json(RigLayout([0.5, 0.5, 1.3], [3, 1])));
        var top = TopGrid(host);
        var stars = new[] { top.ColumnDefinitions[0].Width.Value, top.ColumnDefinitions[2].Width.Value, top.ColumnDefinitions[4].Width.Value };
        Lay(host);
        var sub = Widths(host);
        expect(Under<Grid>(host).SequenceEqual(grids) && writes.Count == 3 && stars is [0.5, 0.5, 1.3] && Near(sub[2] / sub[0], 2.6, 0.01) && Near(sub[0], sub[1], 0.5),
            F($"shares under 1 (0.5, 0.5, 1.3) taken up in place as they are: the stars 0.5, 0.5 and 1.3, the columns {sub[0]:0.#}, {sub[1]:0.#} and {sub[2]:0.#} (2.6 to 1)"));

        // Only the tab in front differs: that pane is shown, once.
        changed = 0;
        var keyframes = Json(RigLayout([0.5, 0.5, 1.3], [3, 1], front: "Rig:Keyframes"));
        dock.Load(keyframes);
        Lay(host);
        expect(changed == 1 && contents["Rig:Keyframes"].IsDescendantOf(host) && !contents["Rig:Effects"].IsDescendantOf(host) && dock.Save() == keyframes,
            "a layout that differs only in its tab in front (Keyframes): Keyframes shown in place of Effects (one change), saved exactly as loaded");

        // An Active that names a hidden pane: Render showed the first shown pane; loading that pane as Active shows nothing anew.
        shown["Rig:Keyframes"] = false;
        dock.Load(keyframes);
        Lay(host);
        var hiddenGrids = Under<Grid>(host).ToList();
        var effectsShown = contents["Rig:Effects"].IsDescendantOf(host);
        var hiddenWrites = new GridWrites(host);
        (changed, stale) = (0, Stale(host));
        var effects = Json(RigLayout([0.5, 0.5, 1.3], [3, 1], front: "Rig:Effects"));
        dock.Load(effects);
        expect(effectsShown && changed == 0 && hiddenWrites.Count == 0 && NothingNew(host, stale) && Under<Grid>(host).SequenceEqual(hiddenGrids) && dock.Save() == effects,
            "Keyframes hidden while it is Active, so Effects is shown: Effects loaded as Active is what is shown already, so nothing is activated, raised, written or made stale");
        shown["Rig:Keyframes"] = true;

        // Another shape: built again.
        dock.Load(Json(new DockLayoutState { Main = DockNode.Split(false, (DockNode.Split(true, (DockNode.Group("Rig:Player"), 3), (DockNode.Group("Rig:Timeline"), 1)), 2), (DockNode.Group("Rig:Media", "Rig:Effects", "Rig:Keyframes"), 1)) }));
        Lay(host);
        var rebuilt = Under<Grid>(host).ToList();
        expect(!rebuilt.SequenceEqual(hiddenGrids) && rebuilt.Any(g => g.ColumnDefinitions.Count == 3) && contents["Rig:Media"].IsDescendantOf(host),
            "an arrangement of another shape (the media, effects and keyframes in one group) is laid out again, as before");

        // The source: Load, Apply and Reset all choose through RebindOrRebuild; Rebind and its rules are in their own file.
        var manager = Read("Docking", "DockManager.cs");
        var rebind = Read("Docking", "DockManager.Rebind.cs");
        var sides = Read("Docking", "DockManager.DragAndSides.cs");
        expect(Method(manager, "public void Load(").Contains("RebindOrRebuild();", StringComparison.Ordinal) && Method(manager, "public void Reset(").Contains("RebindOrRebuild();", StringComparison.Ordinal)
               && Method(sides, "public void Apply(").Contains("RebindOrRebuild();", StringComparison.Ordinal) && !manager.Contains("private void Rebind()", StringComparison.Ordinal)
               && rebind.Contains("private void Rebind()", StringComparison.Ordinal) && rebind.Contains("private static void ShowShares(", StringComparison.Ordinal)
               && manager.Split('\n').Length < 1792,
            "Load, Apply and Reset all take an arrangement up through RebindOrRebuild; Rebind and ShowShares live in DockManager.Rebind.cs (DockManager.cs only shrinks)");
        expect(InOrder(Method(rebind, "private void RebindOrRebuild()"), "if (signature == _builtSignature)", "Rebind();", "!_dragging && _builtSignature is { } built && SameButShares(built, signature)", "Rebind();", "MarkAllBuilt();", "Rebuild();")
               && Method(manager, "private static void SetDips(").Contains("Math.Max(size, 1)", StringComparison.Ordinal),
            "a shares-only difference is taken up in place (never while a pane is dragged) and counted as built; a drag's own sizes still keep each share at least 1");
    }

    // ----- unified-views-playback-menu -----

    private static List<(string Property, string Value)> Setters(XElement? style) =>
        style?.Elements().Where(e => e.Name.LocalName == "Setter").Select(e => (Attr(e, "Property") ?? "", Attr(e, "Value") ?? "")).ToList() ?? [];

    private static void ViewsMenu(Action<bool, string> expect)
    {
        var main = Xaml("MainWindow.xaml");
        var timeline = Xaml("Views", "UnifiedTimelinePane.xaml");
        XElement? Keyed(XContainer xaml, string key) => xaml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && Attr(e, X + "Key") == key);

        // The row and its styles.
        var row = Named(main, "TransportRow");
        expect(Attr(row, "Height") == "26" && Attr(row, "Margin") == "0" && Attr(Named(timeline, "ToolbarRow"), "Height") == "26",
            "the transport row is 26 DIPs tall, as the Timeline's row of tools (ToolbarRow) is, with no band above it");
        var resources = row?.Elements().FirstOrDefault(e => e.Name.LocalName == "PlaybackRow.Resources");
        var (bar, toggle, play) = (Keyed(resources!, "BarButton"), Keyed(resources!, "BarToggle"), Keyed(resources!, "BarPlay"));
        expect(resources is not null && Setters(bar).SequenceEqual(Setters(Keyed(timeline, "Tool"))) && Setters(toggle).SequenceEqual(Setters(Keyed(timeline, "ToolToggle")))
               && Attr(bar, "BasedOn") == Attr(Keyed(timeline, "Tool"), "BasedOn") && Attr(toggle, "BasedOn") == Attr(Keyed(timeline, "ToolToggle"), "BasedOn")
               && Setters(bar).Contains(("FontSize", "13")) && Setters(bar).Contains(("Padding", "7,4")),
            "the row's BarButton and BarToggle, keyed in its own resources, set exactly what the Timeline's Tool and ToolToggle set (Segoe MDL2 13, Padding 7,4, centred)");
        expect(Attr(play, "BasedOn") == "{StaticResource PlayPauseButton}" && Setters(play).Contains(("FontSize", "13")) && Setters(play).Contains(("Padding", "14,4"))
               && Setters(Keyed(main, "TransportButton")).Contains(("FontSize", "14")) && Setters(Keyed(main, "PlayPauseButton")).Contains(("FontSize", "15")),
            "Play / Pause in BarPlay (PlayPauseButton at 13, Padding 14,4); the window's TransportButton and PlayPauseButton unchanged (the simple bar and the studios use them)");

        // Every button of the row in those styles, none taller than the row.
        var buttons = row!.Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleButton" && !e.Ancestors().Any(a => a.Name.LocalName == "PlaybackRow.Resources")).ToList();
        static double Vertical(string? padding) => padding?.Split(',') is { Length: 2 } p ? double.Parse(p[1], CultureInfo.InvariantCulture) * 2
            : padding?.Split(',') is { Length: 4 } q ? double.Parse(q[1], CultureInfo.InvariantCulture) + double.Parse(q[3], CultureInfo.InvariantCulture) : 0;
        var odd = buttons.Where(b => Attr(b, "Style") is not ("{StaticResource BarButton}" or "{StaticResource BarToggle}" or "{StaticResource BarPlay}")
            || (Attr(b, "FontSize") is { } size && double.Parse(size, CultureInfo.InvariantCulture) > 13) || (Attr(b, "Padding") is { } padding && Vertical(padding) > 8)
            || b.Descendants().Any(p => p.Name.LocalName == "Path" && double.Parse(Attr(p, "Height") ?? "0", CultureInfo.InvariantCulture) + 8 + 2 > 26)).ToList();
        expect(buttons.Count >= 20 && odd.Count == 0,
            F($"all {buttons.Count} buttons of the row are in BarButton, BarToggle or BarPlay, none with a larger type or more padding, picture icons at most 16 tall (16 + 8 + 2 = 26)"));
        // The speed (5.4.4): a pill as the volume is (22 DIPs tall, its own height, inside the row), its face the speed; no box left.
        var speed = row.Descendants().FirstOrDefault(e => Attr(e, "AutomationProperties.Name") == "Playback Speed");
        expect(speed is { Name.LocalName: "ExpandablePillSlider" } && Attr(speed, "Icon") == "Readout" && Attr(speed, "Height") is null
               && !row.Descendants().Any(e => e.Name.LocalName == "ComboBox"),
            "the speed is a pill, 22 DIPs tall as the volume pill is, inside the row, its face the speed; no box (ComboBox) left on the row");
        expect(!row.Descendants().Any(e => Attr(e, "Content") == "" || Attr(e, "Text") == ""), "no gear (E713) on the row");

        // The Views button: an icon alone.
        var views = Named(main, "ViewsButton");
        var picture = views?.Elements().SingleOrDefault();
        expect(views is not null && Attr(views, "AutomationProperties.Name") == "Views" && Attr(views, "Style") == "{StaticResource BarToggle}"
               && picture is { Name.LocalName: "Path" } && Attr(picture, "Width") == "16" && Attr(picture, "Height") == "14"
               && !views.Descendants().Any(e => e.Name.LocalName == "TextBlock") && views.Parent == Named(main, "TimelineOptionsBar"),
            "ViewsButton keeps its name, its place (TimelineOptionsBar) and UI Automation's \"Views\", in BarToggle: one 16 x 14 picture (three sliders), no text");

        // The settings button and its popup retired; their list in the Views menu.
        expect(Named(main, "PlaybackSettingsButton") is null && !main.Descendants().Any(e => Attr(e, "AutomationProperties.Name") == "Playback Controls Settings")
               && Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.*", SearchOption.AllDirectories)
                   .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                   .All(f => !File.ReadAllText(f).Contains("PlaybackSettingsButton", StringComparison.Ordinal)),
            "the playback bar's settings button (PlaybackSettingsButton, \"Playback Controls Settings\") is gone, from the XAML and from the code");
        var popup = Named(main, "ViewsPopup");
        var lists = main.Descendants().Where(e => e.Name.LocalName == "ItemsControl" && Attr(e, "ItemsSource") == "{Binding PlaybackControls.Options}").ToList();
        var barList = Named(main, "ViewsBarControls");
        expect(lists.Count == 1 && lists[0] == barList && barList!.Ancestors().Contains(popup) && Attr(barList.Parent, "Grid.Column") is null && Attr(barList.Parent, "Grid.Row") is null
               && Attr(barList.Parent, X + "Name") == "ViewsBarColumn"
               && barList.Descendants().Where(e => e.Name.LocalName == "WrapPanel").All(w => double.Parse(Attr(w, "MaxHeight") ?? "1000", CultureInfo.InvariantCulture) <= 420)
               && barList.Descendants().Any(e => e.Name.LocalName == "GroupStyle"),
            "one list of the bar's controls (PlaybackControls.Options, under their headings): ViewsBarControls, the Views menu's top-left column (5.4.4-Final), wrapping at 400 DIPs");
        var column = barList!.Parent!;
        expect(column.Elements().Any(e => e.Name.LocalName == "TextBlock" && Attr(e, "Text") == "Playback Bar") && column.Elements().Any(e => Attr(e, "Text") == "What this bar shows, in this mode."),
            "its column's heading: Playback Bar, what this bar shows, in this mode");
        var scaling = Named(main, "ViewsPreviewScalingBox");
        expect(scaling is { Name.LocalName: "CheckBox" } && scaling.Parent == column && scaling.ElementsBeforeSelf().Contains(barList) && Attr(scaling, "Click") == "ViewsPreviewScalingBox_Click"
               && Attr(scaling, "AutomationProperties.Name") == "Dynamic Preview Scaling" && Attr(scaling, "IsChecked") is null,
            "under the list, Dynamic Preview Scaling (ViewsPreviewScalingBox), set from code as the menu opens, as the Layout Selector's per-tab box is");
        // 5.4.4-Final (the user's): Playback Bar left, Timeline right, then one flat two-column list of tabs and panes and Reset Panes Layout under both.
        var timelineColumn = Named(main, "ViewsPlaybackOptions");
        var panes = Named(main, "ViewsPanesSection");
        var paneList = Named(main, "ViewsPaneList");
        var order = panes?.Descendants().Select(e => Attr(e, X + "Name") ?? Attr(e, "Click")).Where(n => n is "ViewsPaneList" or "ResetPanesLayout_Click").ToList();
        expect(timelineColumn is not null && Attr(timelineColumn, "Grid.Column") == "1" && Attr(timelineColumn, "Grid.Row") is null && timelineColumn.Ancestors().Contains(popup)
               && panes is not null && Attr(panes, "Grid.Row") == "1" && Attr(panes, "Grid.ColumnSpan") == "2" && order is ["ViewsPaneList", "ResetPanesLayout_Click"]
               && paneList is { Name.LocalName: "UniformGrid" } && Attr(paneList, "Columns") == "2" && Named(main, "ViewsTabList") is null
               && !popup!.Descendants().Any(e => Attr(e, "Text") is "Arrange" or "Tabs and Panes" or "Tabs"),
            "the Views menu: Playback Bar (left) and Timeline (right) side by side, then one flat two-column list of tabs and panes (ViewsTabList gone) and Reset Panes Layout, no Arrange text");
        expect(Method(Read("MainWindow.Export.cs"), "private void FillViewsLists()").Contains("Fill(TabToggles(), tabs: true);", StringComparison.Ordinal),
            "FillViewsLists puts the tabs, then the panes, in the one list (each still named \"... Tab\" / \"... Pane\" for UI Automation)");
        expect(popup is not null && new[] { "Placement", "PlacementTarget", "HorizontalOffset", "VerticalOffset", "PlacementRectangle" }.All(a => popup.Attribute(a) is null),
            "ViewsPopup has no placement of its own (UiScale.PlaceAgainst places it over its button)");

        // The code.
        var menu = Read("MainWindow.ViewsMenu.cs");
        expect(Method(menu, "private void ViewsPopup_Opened(").Contains("ViewsPreviewScalingBox.IsChecked = AppSettings.Current.DynamicPreviewScaling;", StringComparison.Ordinal)
               && Method(menu, "private void ViewsPreviewScalingBox_Click(").Contains("_viewModel.SavePaneLayout(s => s.DynamicPreviewScaling = on);", StringComparison.Ordinal),
            "the box shows the setting as the menu opens and saves it at once when clicked (the Settings window's setting: the player reads it as it draws)");
        expect(menu.Contains("? Math.Ceiling((ViewsButton.ActualWidth > 0 ? ViewsButton.ActualWidth : 34) + 22)", StringComparison.Ordinal)
               && menu.Contains("UiScale.PlaceAgainst(ViewsPopup, ViewsButton, UiScale.FlyoutSide.AboveStart, 4)", StringComparison.Ordinal),
            "the row keeps room for the icon-only button (34 DIPs until it is laid out); the menu is placed over it");
        var build = Method(Read("MainWindow.LoopbackPreview.cs"), "private void BuildLoopbackToggle()");
        expect(build.Contains("(Style)TransportRow.Resources[\"BarToggle\"]", StringComparison.Ordinal) && build.Split("Style = barToggle,").Length == 3
               && !build.Contains("FontSize = 14", StringComparison.Ordinal) && !build.Contains("Padding = new Thickness(12, 7, 8, 7)", StringComparison.Ordinal),
            "the Live Encoded Preview toggle and its arrow, made in code, take the row's BarToggle (13, as tall as the other toggles)");
    }
}
