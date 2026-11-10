using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.3-Stage2's facelift and de-clutter:
/// dock-standard-layout (pure data, through MainWindow's window-free layouts): Standard Editing's middle column is one group of tabs,
/// Effects, Keyframes, Canvas and Master Meter, Effects shown; a version 3 Standard Editing is brought up to it (step 3 to 4,
/// StackCanvasWithEffects) keeping the column's share and the tab shown, a 4.4 one goes 2 to 3 to 4, the frozen version 3 shape and the
/// 4.4 fingerprint derived from it keep the Canvas in a part of its own, an arrangement the user made is left alone, and a second pass
/// changes nothing;
/// layout-edge-to-edge (the XAML as written, read with XDocument, then the real Timeline pane laid out at a size of its own with no
/// window): the title bar 26 on both windows from one resource, the window's body with no margin but 6 under the title bar, no band round the playback row, the
/// scrubber, the Timeline pane or the Preset Bar, the drop hint with no outline (5.3-Stage3), the scrubber's grip on its padding; the dock's seams a 7 px grip with
/// the line on the seam, wholly beside the floating player, never over it (DockManager.SeamGrip and FloatsAt); the transport row's checkboxes and
/// both Live Preview switches gone; Undo and Redo named and hidden; Quick FX one button with its slots in the flyout, a pointer click on a
/// slot handing the keyboard back to the button; and in the live
/// pane the ruler at its top, the tracks at its left, the rows' scroll bar at its right and the tools at its bottom;
/// lazy-settings-tabs: every settings tab's body is a template (Export too; 5.4A retired the Summary tab), and every part of them the window declares by
/// hand is named, of its type, in one of them.
/// Every line says what it proved.
/// </summary>
public static class LayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The settings tabs' pane ids, in their order (MainWindow.xaml's SettingsTabs: "Tab:" and the header).
    // (5.4A: no Tab:Summary, the retired tab; 5.4B: no Tab:Video, its controls the Export tab's three cards.)
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("dock-standard-layout", "# Standard Editing's Canvas is a tab beside the effects and keyframes; version 3 is brought up to it, anything the user made is left alone.", StandardLayout);
        yield return Case("layout-edge-to-edge", "# Edge to edge: a 26 px title bar, no bands round the panes, the transport and Timeline de-cluttered; the live Timeline pane flush.", EdgeToEdge);
        yield return Case("lazy-settings-tabs", "# Every settings tab's body is a template (Export too); every part declared by hand is named in one.", LazyTabs);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "FAILED: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // ----- dock-standard-layout -----

    private static void StandardLayout(Action<bool, string> expect, StringBuilder text)
    {
        static string Shape(DockLayoutState state) => DockManager.Shape(state.Main);
        DockNode TopOf(DockLayoutState state) => state.Main.Children[0];

        // Today's Standard Editing.
        var standard = MainWindow.StandardLayout(TabIds);
        text.Append("   standard (version ").Append(standard.Version.ToString(CultureInfo.InvariantCulture)).Append("): ").Append(Shape(standard)).Append('\n');
        var middle = standard.Main.GroupOf("Effects");
        expect(middle is { Panes: ["Effects", "Keyframes", "Layout", "Meter"], Active: "Effects" },
            "Standard Editing's middle column is one group: Effects, Keyframes, Canvas (Layout), Master Meter, Effects shown");
        expect(!standard.Main.AllGroups().Any(g => g.Panes is ["Layout"]), "and no part holds the Canvas alone");
        expect(standard.Version == DockLayoutState.CurrentVersion && DockLayoutState.CurrentVersion == 4, "it is an arrangement of version 4, the current one");
        expect(standard.Main.GroupOf("UnifiedTimeline") is { Active: "UnifiedTimeline" } bottom && bottom.Panes[0] == "UnifiedTimeline" && TabIds.All(bottom.Panes.Contains),
            "the timeline is still the first tab of the settings tabs' group, and shown");

        // The frozen version 3 shape, and the 4.4 fingerprint derived from it: the Canvas still in a part of its own.
        var v3 = MainWindow.StandardLayoutV3(TabIds);
        var beside = MainWindow.StandardLayoutBeside(TabIds);
        text.Append("   version 3: ").Append(Shape(v3)).Append('\n');
        text.Append("   4.4 (version 2): ").Append(Shape(beside)).Append('\n');
        expect(v3.Main.GroupOf("Layout") is { Panes.Count: 1 } canvas3 && v3.Main.ParentOf(canvas3) is { Vertical: true, Children.Count: 2 },
            "StandardLayoutV3 keeps the Canvas over the effects in a column of its own (what step 3 to 4 recognises)");
        expect(beside.Main.GroupOf("Layout") is { Panes.Count: 1 } && beside.Main.GroupOf("Meter") is null
               && beside.Main.GroupOf("UnifiedTimeline") is { Panes: ["UnifiedTimeline"] },
            "StandardLayoutBeside (the 2 to 3 fingerprint) is still 4.4's: the Canvas alone, no Master Meter, the timeline in a group of its own");
        expect(!DockManager.SameShape(standard, v3), "today's Standard Editing is not version 3's shape");

        // A version 3 Standard Editing as a user left it: odd sizes, the keyframes shown. Brought up to today's, in place.
        var left = MainWindow.StandardLayoutV3(TabIds);
        left.Main.GroupOf("Effects")!.Active = "Keyframes";
        TopOf(left).Sizes = [50.0, 30.0, 20.0];
        MainWindow.UpgradeLayout(left, TabIds);
        text.Append("   version 3, upgraded: ").Append(Shape(left)).Append('\n');
        expect(left.Version == 4 && DockManager.SameShape(left, standard), "a version 3 Standard Editing is brought up to version 4, today's shape");
        expect(left.Main.GroupOf("Layout") is { Panes: ["Effects", "Keyframes", "Layout", "Meter"], Active: "Keyframes" },
            "the Canvas goes in after the keyframes, and the tab shown (Keyframes) stays shown");
        expect(TopOf(left).Sizes.SequenceEqual([50.0, 30.0, 20.0]) && ReferenceEquals(TopOf(left).Children[1], left.Main.GroupOf("Layout")),
            "the group takes the column's place, and so its share of the room (30 of 100)");

        // 4.4's Standard Editing (version 2, no Master Meter yet): 2 to 3 (the timeline first among the tabs), then 3 to 4.
        var old = MainWindow.StandardLayoutBeside(TabIds);
        MainWindow.UpgradeLayout(old, TabIds);
        text.Append("   4.4, upgraded: ").Append(Shape(old)).Append('\n');
        var withoutMeter = MainWindow.StandardLayout(TabIds);
        withoutMeter.Main.GroupOf("Meter")!.Panes.Remove("Meter");
        expect(old.Version == 4 && DockManager.SameShape(old, withoutMeter),
            "4.4's Standard Editing goes 2 to 3 to 4: today's shape but for the Master Meter (put in afterwards by DockManager.PlaceAsDefault)");
        expect(old.Main.GroupOf("UnifiedTimeline") is { } tabs4 && tabs4.Panes[0] == "UnifiedTimeline" && old.Main.GroupOf("Layout") is { Panes: ["Effects", "Keyframes", "Layout"] },
            "its timeline is the tabs' first (2 to 3) and its Canvas a tab after the keyframes (3 to 4)");

        // Arrangements the user made are left as they are: the Canvas beside the player, and Standard Editing with the media in the Canvas's part.
        var player = DockNode.Split(true, (DockNode.Split(false, (DockNode.Group("Player"), 72), (DockNode.Group("Layout"), 28)), 1), (DockNode.Group("Playback"), 1));
        var side = DockNode.Split(true, (DockNode.Group("Header"), 1), (DockNode.Group("Media"), 1));
        var own = new DockLayoutState
        {
            Version = 3,
            Main = DockNode.Split(true, (DockNode.Split(false, (player, 56), (DockNode.Group("Effects", "Keyframes", "Meter"), 22), (side, 22)), 46),
                (DockNode.Group("Timeline"), 1), (DockNode.Group("Presets"), 1), (DockNode.Group(["UnifiedTimeline", .. TabIds]), 38)),
        };
        var ownShape = Shape(own);
        MainWindow.UpgradeLayout(own, TabIds);
        expect(own.Version == 4 && Shape(own) == ownShape && own.Main.GroupOf("Layout") is { Panes: ["Layout"] },
            "the user's own arrangement (the Canvas beside the player) is only marked version 4: nothing moves");

        var mixed = MainWindow.StandardLayoutV3(TabIds);
        mixed.Main.GroupOf("Media")!.Panes.Remove("Media");
        mixed.Main.GroupOf("Layout")!.Panes.Add("Media");
        mixed.Main = DockTree.Tidy(mixed.Main)!;
        var mixedShape = Shape(mixed);
        MainWindow.UpgradeLayout(mixed, TabIds);
        expect(Shape(mixed) == mixedShape && mixed.Main.GroupOf("Layout") is { Panes: ["Layout", "Media"] },
            "a Standard Editing the user changed (the media moved into the Canvas's part) is left as it is");

        // A second pass changes nothing; nor does a pass over today's Standard Editing.
        var again = left.Copy();
        MainWindow.UpgradeLayout(again, TabIds);
        var fresh = MainWindow.StandardLayout(TabIds);
        MainWindow.UpgradeLayout(fresh, TabIds);
        expect(Shape(again) == Shape(left) && again.Main.GroupOf("Layout")?.Active == "Keyframes" && TopOf(again).Sizes.SequenceEqual(TopOf(left).Sizes),
            "a version 4 arrangement is unchanged by a second pass");
        expect(Shape(fresh) == Shape(standard), "and today's Standard Editing passes through unchanged");
    }

    // ----- layout-edge-to-edge -----

    private static void EdgeToEdge(Action<bool, string> expect, StringBuilder text)
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var app = XDocument.Load(Path.Combine(src, "App.xaml"));
        var main = XDocument.Load(Path.Combine(src, "MainWindow.xaml"));
        var settings = XDocument.Load(Path.Combine(src, "Dialogs", "SettingsWindow.xaml"));
        var timeline = XDocument.Load(Path.Combine(src, "Views", "UnifiedTimelinePane.xaml"));

        // The title bar: one resource (App.xaml's CaptionBarHeight), taken by both windows' chrome, bars, progress fills and caption buttons.
        const string barResource = "{StaticResource CaptionBarHeight}";
        var bar = app.Descendants().FirstOrDefault(e => e.Name.LocalName == "Double" && Key(e) == "CaptionBarHeight")?.Value;
        text.Append("   CaptionBarHeight: ").Append(bar ?? "none").Append('\n');
        expect(bar == "26", "App.xaml's CaptionBarHeight is 26 (from 32)");
        var captionStyle = app.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && Key(e) == "CaptionButton");
        var styleHeight = captionStyle?.Elements().FirstOrDefault(s => s.Name.LocalName == "Setter" && Attr(s, "Property") == "Height") is { } setter ? Attr(setter, "Value") : null;
        expect(styleHeight == barResource, "the caption buttons' style takes its height from it");
        foreach (var (window, xaml) in new[] { ("the main window", main), ("the Settings screen", settings) })
        {
            var chrome = xaml.Descendants().FirstOrDefault(e => e.Name.LocalName == "WindowChrome" && e.Attribute("CaptionHeight") is not null);
            var buttons = xaml.Descendants().Where(e => Attr(e, "Style") is "{StaticResource CaptionButton}" or "{StaticResource CaptionCloseButton}").ToList();
            expect(Attr(chrome, "CaptionHeight") == barResource && Attr(Named(xaml, "TitleBar"), "Height") == barResource && Attr(Named(xaml, "TitleProgressFill"), "Height") == barResource,
                $"{window}: its WindowChrome's CaptionHeight, its title bar and its progress fill are all CaptionBarHeight");
            expect(buttons.Count >= 2 && buttons.All(b => (Attr(b, "Height") ?? styleHeight) == barResource),
                $"{window}: its {buttons.Count} caption buttons are as tall as the bar (no height of their own)");
        }

        // The window's body and the bands round the panes.
        expect(Attr(Named(main, "ContentRoot"), "Margin") == "0,6,0,0",
            "the window's body (ContentRoot) is edge to edge at its sides and foot in every state, 6 under the title bar (the Header's buttons clear the caption buttons)");
        expect(Attr(Named(main, "TransportRow"), "Margin") == "0", "no band above the playback row");
        var scrubber = Named(main, "TimelinePane");
        var grip = Named(main, "OverviewGrip");
        var (padding, gripMargin) = (Sides(Attr(scrubber, "Padding")), Sides(Attr(grip, "Margin")));
        text.Append("   Sequence Overview Scrubber padding ").Append(Attr(scrubber, "Padding")).Append(", its grip's margin ").Append(Attr(grip, "Margin")).Append('\n');
        expect(padding is [12, 3, 12, 3] && Attr(scrubber, "CornerRadius") == "0", "the Sequence Overview Scrubber is slim (3 above and below) and square");
        expect(Attr(scrubber, "Background") == "{DynamicResource ScrubberBrush}" && app.Descendants().Any(e => e.Name.LocalName == "SolidColorBrush" && Key(e) == "ScrubberBrush"),
            "and on a background of its own, ScrubberBrush (a little darker than the panes and the window, the user's call after 5.3.0), with App.xaml's default for the first frame");
        expect(gripMargin is { Length: 4 } && gripMargin[0] == -padding[0] && gripMargin[2] == -padding[2] && gripMargin[3] == -padding[3],
            "its lower edge's grip lies over its padding: the padding's left, right and bottom, negated");
        expect(Attr(Named(main, "UnifiedTimelinePanel"), "Padding") == "0" && Attr(Named(main, "UnifiedTimelinePanel"), "CornerRadius") == "0",
            "the Timeline's panel has no padding and square corners: the ruler right under the tab strip");
        var hint = Named(main, "DropHint");
        expect(hint is not null && !hint.Descendants().Any(e => e.Name.LocalName is "Rectangle" or "Border" || e.Attribute("StrokeDashArray") is not null),
            "the drop hint has no outline (5.3-Stage3: no dashed Rectangle, no border): its icon and words only");
        expect(Attr(Named(main, "TitleStatus"), "Visibility") == "Collapsed",
            "the title bar's middle (TitleStatus) starts collapsed: nothing there until the user does something, something loads or work begins (5.3-Stage3)");
        expect(Named(main, "StatusBar") is null && Attr(Named(main, "DestinationBar"), "Margin") == "0,1,0,0",
            "no 10 px band above the destination bar, and no status bar (retired in 5.4.1-M5)");
        expect(Named(main, "WorkChip") is null && Named(main, "UpdateNotice")?.Ancestors().Any(e => Attr(e, X + "Name") == "TitleRight") == true
               && Attr(Named(main, "WorkFlyout"), "PlacementTarget") == "{Binding ElementName=TitleStatus}",
            "the status, the work under way and the update notice live in the title bar alone (5.4.2A: no progress chip on the transport row)");
        expect(Attr(Named(main, "PresetBarPane"), "Margin") == "0", "no band under the Preset Bar either (its Margin 0, from 0,0,0,10): the dock's seams part the panes");
        Seams(expect, text, app);

        // The transport row's checkboxes and the Live Preview switches are gone; the Views menu always has the I-frame checkboxes.
        expect(Named(main, "TimelineOptionsHost") is null && Named(main, "TimelineOptionsInline") is null, "the transport row's I-frame checkboxes (TimelineOptionsInline) are gone");
        expect(Attr(Named(main, "TimelineOptionsBar"), "LastChildFill") == "False", "and the Views button left in their place keeps its own width (LastChildFill off)");
        expect(Named(main, "ViewsPlaybackOptions") is { } views && views.Attribute("Visibility") is null, "the Views menu always shows the I-frame checkboxes");
        var livePreview = main.Descendants().Attributes().Where(a => a.Value.Contains("LivePreview", StringComparison.Ordinal)).ToList();
        expect(livePreview.Count == 0, $"no Live Preview switch is left: nothing in MainWindow.xaml binds LivePreview ({livePreview.Count} found)");

        // The Timeline's row of tools: Undo and Redo named and hidden; Quick FX one button, its slots in the flyout made after the first frame.
        var toolbar = Named(timeline, "Toolbar");
        foreach (var name in new[] { "UndoButton", "RedoButton" })
            expect(Named(timeline, name) is { } button && Attr(button, "Visibility") == "Collapsed" && button.Parent == toolbar, $"{name} is on the row of tools, Collapsed in the XAML");
        expect(Named(timeline, "QuickFxHost") is { } host && host.Parent == toolbar && Named(timeline, "QuickFxButton") is { } quick && quick.Ancestors().Contains(host),
            "Quick FX is one button on the row of tools (QuickFxHost)");
        var slots = timeline.Descendants().Where(e => Attr(e, "Tag") is { } tag && tag.StartsWith("Hotbar", StringComparison.Ordinal)).ToList();
        expect(slots.Count == 5 && Named(timeline, "QuickFxPopup") is { } flyout && slots.All(s => s.Ancestors().Contains(flyout)) && flyout.Ancestors().Any(a => Key(a) == "DetailsTemplate"),
            "its five slots are in its flyout (QuickFxPopup), inside DetailsTemplate: none on the row, none made before the first frame");
        expect(slots.Count == 5 && slots.All(s => Attr(s, "Click") == "HotbarSlot_Click"),
            "each slot's click goes through HotbarSlot_Click, which hands the keyboard back to the Quick FX button after a pointer click (leaving both then closes the flyout)");
        expect(Named(timeline, "Hotbar") is null, "the inline Creator Hotbar is gone");
        expect(Attr(Named(timeline, "RowScroll"), "WindowChrome.IsHitTestVisibleInChrome") == "True", "the rows' scroll bar keeps its clicks over a restored window's resize band");
        expect(Attr(Named(timeline, "ToolbarRow"), "Margin") == "0,1,0,0", "the row of tools is a hairline under the tracks");

        // The real pane, laid out at 1200 x 480 with no window, its title slot hidden as the dock hides it in a row of tabs.
        var pane = new UnifiedTimelinePane();
        if (PaneTitleSlot.Of("UnifiedTimeline") is { } slot)
            slot.Visibility = Visibility.Collapsed;
        var size = new Size(1200, 480);
        void Lay()
        {
            pane.Measure(size);
            pane.Arrange(new Rect(size));
            pane.UpdateLayout();
        }

        Lay();
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(pane).TransformBounds(new Rect(element.RenderSize));
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
        var (ruler, surface, rows, tools) = (Bounds(pane.Ruler), Bounds(pane.Surface), Bounds(pane.RowScroll), Bounds(pane.ToolbarRow));
        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"   live pane 1200 x 480: ruler top {ruler.Top:0.#}, tracks left {surface.Left:0.#}, rows' scroll bar right {rows.Right:0.#}, tools' row bottom {tools.Bottom:0.#}\n"));
        expect(Near(ruler.Top, 0), "the ruler is the pane's top edge (no gap under the tab strip)");
        expect(Near(surface.Left, 0), "the tracks start at the pane's left edge");
        expect(Near(rows.Right, size.Width), "the rows' scroll bar ends at the pane's right edge");
        expect(Near(tools.Bottom, size.Height), "the row of tools ends at the pane's bottom edge");

        // Undo and Redo hidden until asked for; Quick FX shown or hidden as the settings say.
        expect(pane.UndoButton.Visibility == Visibility.Collapsed && pane.RedoButton.Visibility == Visibility.Collapsed, "Undo and Redo are hidden as the pane is made");
        pane.ShowUndoRedo(true);
        Lay();
        expect(pane.UndoButton.Visibility == Visibility.Visible && pane.RedoButton.Visibility == Visibility.Visible, "ShowUndoRedo(true) shows both");
        pane.ShowUndoRedo(false);
        expect(pane.UndoButton.Visibility == Visibility.Collapsed && pane.RedoButton.Visibility == Visibility.Collapsed, "ShowUndoRedo(false) hides both again");
        var defaults = HotbarSlot.FromSettings(null);
        pane.ShowHotbar(defaults, false);
        expect(pane.QuickFxHost.Visibility == Visibility.Collapsed, "ShowHotbar(slots, false) hides the Quick FX button");
        pane.ShowHotbar(defaults, true);
        expect(pane.QuickFxHost.Visibility == Visibility.Visible && pane.Toolbar.Children.Contains(pane.QuickFxHost)
               && !pane.Toolbar.Children.OfType<FrameworkElement>().Any(c => c.Tag is string tag && tag.StartsWith("Hotbar", StringComparison.Ordinal)),
            "ShowHotbar(slots, true) shows it, and no slot is on the row (they are made with the flyout, after the first frame)");
        Lay();
    }

    /// <summary>
    /// The dock's seams (DockManager.SeamGrip, App.xaml's QuietSplitter): a 7 px grip laid out in the seam's 1 px, the line on the seam;
    /// beside the floating player (its native window covers whatever lies over its place) the grip lies wholly in the other part, for a
    /// column seam (a GridSplitter) as for a row seam (a Thumb or a GridSplitter), the player on either side; and in Standard Editing the
    /// seams beside the player are known as such (DockManager.FloatsAt).
    /// </summary>
    private static void Seams(Action<bool, string> expect, StringBuilder text, XDocument app)
    {
        var template = app.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && Key(e) == "QuietSplitter")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ControlTemplate");
        var outer = template?.Elements().FirstOrDefault(e => e.Name.LocalName == "Border");
        var line = outer?.Elements().FirstOrDefault(e => e.Name.LocalName == "Border");
        expect(outer is not null && Attr(outer, "Margin") is null && Attr(line, "Margin") == "{TemplateBinding Padding}",
            "QuietSplitter's grip is the splitter itself, its line inside its Padding (no grip of its own reaching 3 px each way)");

        static string Span(double from, double to) => string.Create(CultureInfo.InvariantCulture, $"{from + 0.0:0.#}..{to + 0.0:0.#}"); // + 0.0: never "-0"
        foreach (var vertical in new[] { false, true })
        {
            var way = vertical ? "a row seam" : "a column seam";
            var (beforeName, afterName) = vertical ? ("above", "below") : ("at the left", "at the right");
            foreach (var (floatsBefore, floatsAfter) in new[] { (false, false), (true, false), (false, true) })
            {
                // Along the split, against the seam's own 1 px at 0..1: where the grip lies, and the line in it.
                var (grip, padding) = DockManager.SeamGrip(vertical, floatsBefore, floatsAfter);
                var (lead, trail, padLead, padTrail) = vertical ? (grip.Top, grip.Bottom, padding.Top, padding.Bottom) : (grip.Left, grip.Right, padding.Left, padding.Right);
                var across = vertical ? grip.Left + grip.Right + padding.Left + padding.Right : grip.Top + grip.Bottom + padding.Top + padding.Bottom;
                var (from, to) = (lead, 1 - trail);
                var (lineFrom, lineTo) = (from + padLead, to - padTrail);
                var where = floatsBefore ? "the player " + beforeName : floatsAfter ? "the player " + afterName : "no floating pane";
                text.Append("   ").Append(way).Append(", ").Append(where).Append(": grip ").Append(Span(from, to)).Append(", line ").Append(Span(lineFrom, lineTo)).Append(" (the seam 0..1)\n");
                var placed = floatsBefore ? from == 0 : floatsAfter ? to == 1 : from == -3 && to == 4;
                expect(to - from == DockManager.SeamGrab && lineFrom == 0 && lineTo == 1 && across == 0 && placed,
                    $"{way}, {where}: a 7 px grip with its 1 px line on the seam, "
                    + (floatsBefore ? $"wholly in the part {afterName} (none of it over the player)" : floatsAfter ? $"wholly in the part {beforeName} (none of it over the player)" : "reaching 3 px into each part"));
            }
        }

        // Standard Editing: the column seam beside the player has it at its left; the seam under the top row has it above only while the
        // playback controls (the lowest part under it) are hidden; a part with the player along its top floats there.
        var standard = MainWindow.StandardLayout(TabIds);
        var top = standard.Main.Children[0];
        Func<string, bool> floats = id => id == "Player", all = _ => true, noPlayback = id => id != "Playback";
        expect(DockManager.FloatsAt(top.Children[0], DockZone.Right, all, floats) && !DockManager.FloatsAt(top.Children[1], DockZone.Left, all, floats),
            "Standard Editing: the column seam between the player and the effects has the player at its left (its grip goes wholly into the effects' column)");
        expect(!DockManager.FloatsAt(top, DockZone.Bottom, all, floats) && DockManager.FloatsAt(top, DockZone.Bottom, noPlayback, floats),
            "the seam under the top row has the player above it only while the playback controls, the lowest part there, are hidden");
        var lower = DockNode.Split(false, (DockNode.Group("Player"), 1), (DockNode.Group("Effects"), 1));
        expect(DockManager.FloatsAt(lower, DockZone.Top, all, floats) && !DockManager.FloatsAt(lower, DockZone.Top, id => id != "Player", floats),
            "a part under a seam with the player along its top floats there (the grip then reaches up only), unless the player is hidden");
    }

    // ----- lazy-settings-tabs -----

    private static void LazyTabs(Action<bool, string> expect, StringBuilder text)
    {
        var main = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MainWindow.xaml"));
        var tabsControl = Named(main, "SettingsTabs");
        expect(tabsControl is not null, "MainWindow.xaml has its SettingsTabs");
        if (tabsControl is null)
            return;

        // Each tab: no body of its own, only its Tag's DataTemplate.
        var tabs = tabsControl.Elements().Where(e => e.Name.LocalName == "TabItem").ToList();
        var templates = new List<XElement>();
        foreach (var tab in tabs)
        {
            var header = Attr(tab, "Header") ?? "?";
            var body = tab.Elements().Where(e => !e.Name.LocalName.StartsWith("TabItem.", StringComparison.Ordinal)).ToList();
            var template = tab.Elements().FirstOrDefault(e => e.Name.LocalName == "TabItem.Tag")?.Elements().FirstOrDefault(e => e.Name.LocalName == "DataTemplate");
            text.Append("   ").Append(header).Append(template is null ? ": built with the window" : ": a template, made when first shown or at idle").Append('\n');
            expect(body.Count == 0 && template is not null, $"the {header} tab's body is its Tag's DataTemplate, nothing built with the window");
            if (template is not null)
                templates.Add(template);
        }

        expect(tabs.Select(t => Attr(t, "Header")).SequenceEqual(TabIds.Select(id => id["Tab:".Length..])), "the tabs are Filters, Audio, Export and Command Preview, in that order (no Summary, no Video)");
        var export = tabs.FirstOrDefault(t => Attr(t, "Header") == "Export");
        expect(export is not null && Attr(export, X + "Name") == "ExportTab", "the Export tab itself keeps its name (ExportTab): only its body is a template");
        var exportRoot = export?.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataTemplate")?.Elements().FirstOrDefault();
        expect(exportRoot is not null && Attr(exportRoot, X + "Name") == "ExportPanel", "the Export template's root is ExportPanel (RealizeTabContent finds it there)");

        // The parts the window declares by hand, as each template is made (RealizeTabContent): each is named, of its type, in a tab's template.
        // (5.4B: the Easy | Advanced switch, its cards and the "each section has a tab" hint are gone; the three columns' grid came.)
        string[] declared =
        [
            "ExportPanel", "ExportBarSlot", "ExportSections", "ExportSectionHost", "ExportColumns",
            "AudioPanel", "AudioOptionsHost", "AudioOptionsInline", "AudioOptionsButton",
        ];
        foreach (var name in declared)
        {
            var field = typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            var named = templates.SelectMany(t => t.Descendants().Prepend(t)).Where(e => Attr(e, X + "Name") == name).ToList();
            text.Append("   ").Append(name).Append(": ").Append(field?.FieldType.Name ?? "no field").Append('\n');
            expect(field is not null && named.Count == 1 && named[0].Name.LocalName == field.FieldType.Name,
                $"{name}: a field of the window, named once in a tab's template, as a {field?.FieldType.Name ?? "?"}");
        }

        // Nothing in the window's XAML outside the templates is named as one of them (no field made by the XAML for them any more).
        var outside = main.Descendants().Where(e => Attr(e, X + "Name") is { } n && declared.Contains(n) && !e.Ancestors().Any(a => a.Name.LocalName == "DataTemplate")).ToList();
        expect(outside.Count == 0, "none of them is named outside a template (the XAML makes no field for them)");
    }

    // ----- XAML read as XML -----

    /// <summary>The repository's folder (where HandPegApp.csproj is), found upwards from the built program.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    private static XElement? Named(XContainer xaml, string name) => xaml.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == name);

    private static string? Key(XElement element) => Attr(element, X + "Key");

    private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

    /// <summary>A XAML thickness as left, top, right, bottom ("6", "12,3" and "12,3,12,3" alike); none for nothing.</summary>
    private static double[] Sides(string? value)
    {
        if (value is null)
            return [];

        var parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts switch
        {
            [var all] => [all, all, all, all],
            [var across, var down] => [across, down, across, down],
            [_, _, _, _] => parts,
            _ => [],
        };
    }
}
