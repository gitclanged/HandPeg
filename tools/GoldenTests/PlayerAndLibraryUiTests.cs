using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.4: a clip dragged from the Timeline onto the Clip Library (timeline-drag-to-clip-library), the collapsing pill sliders
/// (expandable-pill-slider), the playback speed's pill (speed-pill) and the fullscreen preview (player-fullscreen).
/// </summary>
public static class PlayerAndLibraryUiTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-drag-to-clip-library", "# A clip dragged out of the Timeline onto the Clip Library: held back where it began while the pane offers the drop, let go there nothing is committed and the Save dialog is asked for after the release; anywhere else the drag is as before.", DragToLibrary);
        yield return Case("expandable-pill-slider", "# The pill sliders (Volume, Timeline Zoom): the icon's size in layout open or closed, drawn past it with no child visuals, drag, Shift, wheel, double-click, keys, the readout, the icon's waves, the eased opening, UI Automation and their homes.", PillSlider);
        yield return Case("speed-pill", "# The playback speed's pill: its face the speed at the Volume pill's 24 x 22 (the wider speeds drawn smaller to fit, 5.5A), a track of the seven speeds its thumb snaps to with no jitter, the wheel, the keys, a double-click for 1x, UI Automation, the view model's step and speed following each other, and its home.", SpeedPill);
        yield return Case("player-fullscreen", "# The Fullscreen Preview: the hover button and the bar coming and going, the order Esc goes in, F11, the bar's parts bound to the player's state, the borderless window, and the surface lifted out of its pane and given back with the dock untouched.", Fullscreen);
    }

    /// <summary>The cases that need the WPF Application (run after UiScaleTests made it).</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
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
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }

    private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

    // ----- timeline-drag-to-clip-library -----

    private static void DragToLibrary(Action<bool, string> expect, StringBuilder text)
    {
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(1000, 300));
        surface.Arrange(new Rect(0, 0, 1000, 300));
        surface.SetView(0, 10, 10);
        var scale = surface.PixelsPerSecond;
        var calls = new List<string>();
        var block = new TimelineBlock
        {
            Left = 1 * scale, Width = 2 * scale, Name = "clip", Clip = "clip",
            CanDrag = () => true, Resizable = true, TrimmableStart = true, Slippable = true,
            Commit = (_, _) => calls.Add("commit"),
            Edit = done => calls.Add($"edit {done.Kind}"),
            CanMoveTo = (_, _) => true,
            MoveTo = (data, _) => calls.Add($"move to {data}"),
            CanMoveToNewTrack = (_, _, _) => true,
            MoveToNewTrack = (_, _, _) => calls.Add("new track"),
        };
        var upper = new TimelineRow { Key = "upper", Data = "upper", Kind = TimelineRowKind.Video, Height = 56 };
        var lower = new TimelineRow { Key = "lower", Data = "lower", Kind = TimelineRowKind.Video, Height = 56 };
        lower.Blocks.Add(block);
        surface.SetRows([upper, lower]);
        surface.UpdateLayout();

        var lane = surface.LaidLanes.First(l => Equals(l.Key, "lower"));
        var middle = lane.Top + lane.Height / 2;
        Point At(double seconds) => new(surface.HeaderWidth + seconds * scale, middle);
        var (start, width) = (1 * scale, 2 * scale);
        var above = new Point(500, -40);
        void Reset()
        {
            (block.Left, block.Width) = (start, width);
            calls.Clear();
        }

        var asked = new List<Point?>();
        TimelineBlock? askedAbout = null;
        var (offer, take, drops, ended) = (true, true, 0, 0);
        surface.DragOutside = (_, dragged, at) =>
        {
            asked.Add(at);
            askedAbout = dragged;
            return at is not null && offer;
        };
        surface.DropOutside = (_, _, _) =>
        {
            drops++;
            return take;
        };
        surface.DragEnded += () => ended++;

        // Out of the control over a place that takes it, and let go there.
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.None);
        surface.MoveBlockDrag(At(4));
        expect(asked.Count == 0 && Math.Abs(block.Left - 3 * scale) < 0.5, "inside the control the host is never asked: the block follows the pointer as before");
        surface.MoveBlockDrag(above);
        expect(asked is [{ } point] && point == above && ReferenceEquals(askedAbout, block) && surface.IsDragParked,
            "above the control, the host offering a drop: asked once about the pressed block, with the point itself (in no window there is no screen to put it on)");
        expect(Math.Abs(block.Left - start) < 0.5 && surface.NewTrackSeam is null,
            "the block is drawn back where it began, over no row and no new track's seam (above the rows it would offer a new track otherwise)");
        ended = 0;
        var released = surface.ReleaseBlockDrag();
        expect(released && calls.Count == 0 && drops == 1 && ended == 1 && !surface.IsDragging,
            "let go there: the host takes it (once); no commit, no move, no new track (nothing for Undo), the drag ended once");
        expect(asked[^1] is null && Math.Abs(block.Left - start) < 0.5, "the host was told the drag is no longer outside (null), and the block is where it began");

        // The host offering nothing there: the drag is today's.
        Reset();
        offer = false;
        asked.Clear();
        drops = 0;
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.None);
        surface.MoveBlockDrag(above);
        expect(asked.Count == 1 && !surface.IsDragParked && surface.NewTrackSeam is not null,
            "the host offering nothing there: asked, and the drag is as before (above the rows, the new track's seam)");
        surface.MoveBlockDrag(At(5));
        expect(asked[^1] is null && Math.Abs(block.Left - 4 * scale) < 0.5, "back inside the control: the host is told (null), the block follows the pointer");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["commit"]) && drops == 0, "let go inside: committed as before (one move along its row), the host never asked to take it");

        // Parked, then back inside: the drag goes on as before.
        Reset();
        offer = true;
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.None);
        surface.MoveBlockDrag(above);
        surface.MoveBlockDrag(At(3));
        expect(!surface.IsDragParked && asked[^1] is null && Math.Abs(block.Left - 2 * scale) < 0.5, "parked, then back inside: the block follows the pointer again");
        expect(surface.ReleaseBlockDrag() && calls.SequenceEqual(["commit"]) && drops == 0, "and let go inside, it is committed as before");

        // Offered as it moved, refused as it is let go (the pane went away meanwhile): nothing at all.
        Reset();
        take = false;
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.None);
        surface.MoveBlockDrag(above);
        surface.ReleaseBlockDrag();
        expect(calls.Count == 0 && drops == 1 && Math.Abs(block.Left - start) < 0.5 && !surface.IsDragging,
            "offered as it moved but refused as it is let go: nothing is committed, the block stays where it began");

        // Parked, the wheel turned under the held drag (the view moves, the pointer is still over the place): the block stays put back,
        // through the moves after it too; let go there and refused, nothing is committed (5.4.4).
        Reset();
        drops = 0;
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.None);
        surface.MoveBlockDrag(above);
        surface.WheelAt(-120, ModifierKeys.None, 0, overTime: true);
        var wheeled = (block.Left, surface.NewTrackSeam);
        surface.WheelAt(120, ModifierKeys.Control, 0);
        surface.MoveBlockDrag(new Point(above.X + 30, above.Y - 10));
        var movedOn = (block.Left, surface.NewTrackSeam, surface.IsDragParked);
        surface.ReleaseBlockDrag();
        text.Append(F($"   parked, after the wheel the block is {(wheeled.Left - start) / scale:0.###} s from where it began, after the next move {(movedOn.Left - start) / scale:0.###} s; let go refused: {(calls.Count == 0 ? "nothing" : string.Join(", ", calls))}\n"));
        expect(Math.Abs(wheeled.Left - start) < 0.5 && wheeled.NewTrackSeam is null && Math.Abs(movedOn.Left - start) < 0.5 && movedOn.NewTrackSeam is null && movedOn.IsDragParked
               && calls.Count == 0 && drops == 1 && Math.Abs(block.Left - start) < 0.5 && !surface.IsDragging,
            "parked, a turn of the wheel (the plain wheel, Ctrl's zoom) leaves the block where it began, over no row and no new track, and so does the next move; let go there refused, nothing is committed");
        take = true;

        // A trim, a slip: never asked.
        Reset();
        asked.Clear();
        surface.PressBlockAt(lower, block, new Point(At(3).X - 1, middle), ModifierKeys.None);
        surface.MoveBlockDrag(above);
        surface.ReleaseBlockDrag();
        var trimmed = calls.ToList();
        Reset();
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.Alt);
        surface.MoveBlockDrag(above);
        surface.ReleaseBlockDrag();
        expect(asked.Count == 0 && trimmed.SequenceEqual(["commit"]) && calls.SequenceEqual(["edit Slip"]),
            "a trim of its end and an Alt slip dragged out of the control never ask the host: committed as before");

        // A selected group: asked about the pressed clip, every block of it held back.
        Reset();
        var member = new TimelineBlock { Left = 6 * scale, Width = 1 * scale, Name = "member", Clip = "member", CanDrag = () => true, Commit = (_, _) => calls.Add("member") };
        upper.Blocks.Add(member);
        surface.IsSelected = clip => clip is "clip" or "member";
        surface.SetRows([upper, lower]);
        surface.UpdateLayout();
        asked.Clear();
        (askedAbout, drops) = (null, 0);
        surface.PressBlockAt(lower, block, At(2), ModifierKeys.None);
        surface.MoveBlockDrag(At(3));
        var memberMoved = Math.Abs(member.Left - 7 * scale) < 0.5;
        surface.MoveBlockDrag(above);
        expect(memberMoved && ReferenceEquals(askedAbout, block) && Math.Abs(member.Left - 6 * scale) < 0.5 && Math.Abs(block.Left - start) < 0.5,
            "a selected group dragged out: the host is asked about the pressed clip, and every block of the group is held back where it began");
        expect(surface.ReleaseBlockDrag() && calls.Count == 0 && drops == 1, "let go there: nothing of the group is committed");
        surface.IsSelected = null;

        // The window's side: the decision, the pane, the wiring.
        var decisions = new (LibraryDropZone Zone, double Ms)[] { (LibraryDropZone.None, 0), (LibraryDropZone.None, 500), (LibraryDropZone.Library, 0), (LibraryDropZone.Tab, 0), (LibraryDropZone.Tab, 179), (LibraryDropZone.Tab, 180), (LibraryDropZone.Tab, 400) };
        text.Append("   decisions: ").Append(string.Join("; ", decisions.Select(d => MainWindow.LibraryDropDecision(d.Zone, d.Ms) is var made
            ? F($"{d.Zone} {d.Ms:0} ms = {(made.Offers ? "drop" : "nothing")}{(made.ShowLibrary ? ", show the Clip Library" : "")}") : ""))).Append('\n');
        expect(MainWindow.LibraryDropDecision(LibraryDropZone.Tab, 179) == (true, false) && MainWindow.LibraryDropDecision(LibraryDropZone.Tab, 180) == (true, true)
               && MainWindow.LibraryDropDecision(LibraryDropZone.Library, 0) == (true, false) && MainWindow.LibraryDropDecision(LibraryDropZone.None, 500) == (false, false)
               && MainWindow.LibraryDwellMs == 180,
            "over the Clip Library a drop; over its button a drop too (let go before the dwell), and held 180 ms (not 179) the pane shows the Clip Library; elsewhere nothing");

        var settings = AppSettings.Current;
        var kept = (settings.BrowserShowsProject, settings.BrowserShowsLibrary);
        var slot = PaneTitleSlot.Of("Media");
        try
        {
            (settings.BrowserShowsProject, settings.BrowserShowsLibrary) = (false, false);
            var pane = new MediaBrowserPane();
            expect(!pane.ShowsClipLibrary && pane.LibraryDropZoneAt(new Point(10, 10)) == LibraryDropZone.None && pane.LibraryDropRect(false).IsEmpty && pane.LibraryDropRect(true).IsEmpty,
                "a Media pane in no window: the Folder Browser, nothing under any point takes a clip, no rectangle for the guide (and nothing throws)");
            pane.ShowClipLibrary();
            expect(pane.ShowsClipLibrary && pane.LibraryDropZoneAt(new Point(10, 10)) == LibraryDropZone.None, "ShowClipLibrary shows the Clip Library (still nothing to drop on in no window)");
            pane.Release();

            (settings.BrowserShowsProject, settings.BrowserShowsLibrary) = (false, false);
            var docked = new PaneTitleSlot { PaneId = "Media" };
            var picker = new MediaBrowserPane(pickerMode: true);
            picker.ShowClipLibrary();
            expect(!picker.ShowsClipLibrary && picker.LibraryDropZoneAt(new Point(10, 10)) == LibraryDropZone.None && ReferenceEquals(PaneTitleSlot.Of("Media"), docked),
                "the New Project studio's picker never switches for a drag and takes nothing");
            picker.Release();
        }
        finally
        {
            PaneTitleSlot.Restore("Media", slot);
            (settings.BrowserShowsProject, settings.BrowserShowsLibrary) = kept;
        }

        var host = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MainWindow.ClipLibrary.cs"));
        var drop = host[host.IndexOf("private bool Timeline_DropOutside", StringComparison.Ordinal)..];
        drop = drop[..drop.IndexOf("\n    }", StringComparison.Ordinal)];
        expect(host.Contains("surface.DragOutside = Timeline_DragOutside;", StringComparison.Ordinal) && host.Contains("surface.DropOutside = Timeline_DropOutside;", StringComparison.Ordinal)
               && host.Contains("surface.DragEnded += ForgetLibraryDrag;", StringComparison.Ordinal),
            "the window answers the Timeline's drags out (MainWindow.ClipLibrary.cs) and forgets each when it ends");
        expect(drop.Contains("Dispatcher.InvokeAsync(() => SaveToClipLibrary(clip), DispatcherPriority.Input)", StringComparison.Ordinal)
               && !drop.Contains("Edit(", StringComparison.Ordinal) && !drop.Contains("Document", StringComparison.Ordinal),
            "a drop asks for Save Clip to Personal Clip Library... after the release (posted, never inside it) and edits nothing of the Sequence");
    }

    // ----- expandable-pill-slider -----

    private static void PillSlider(Action<bool, string> expect, StringBuilder text)
    {
        var pill = new ExpandablePillSlider
        {
            Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 20, DefaultValue = 100, ReadoutFormat = "{0:0}%", ExpandsLeft = true,
            IsIconToggle = true, IconAutomationName = "Mute Player", Value = 80,
        };
        AutomationProperties.SetName(pill, "Volume");
        void Lay()
        {
            pill.InvalidateMeasure();
            pill.Measure(new Size(400, 100));
            pill.Arrange(new Rect(pill.DesiredSize));
            pill.UpdateLayout();
        }

        Lay();
        var collapsed = pill.DesiredSize;
        pill.OpenAtOnce(true);
        Lay();
        expect(pill.IsExpanded && pill.DesiredSize == collapsed && collapsed == new Size(24, 22) && pill.RenderSize == collapsed,
            F($"its size in layout is the icon's, {collapsed.Width:0} x {collapsed.Height:0}, open or closed: opening it moves nothing beside it"));
        expect(VisualTreeHelper.GetChildrenCount(pill) == 0, "no child visuals: it is drawn in OnRender");
        pill.OpenAtOnce(false);
        expect(!pill.IsExpanded, "closed again");

        // Drawn past its slot: a row 300 wide with the pill at its right end, left of it nothing until it opens.
        var row = new Grid { Width = 300, Height = 30, Background = Brushes.Black };
        var drawn = new ExpandablePillSlider { HorizontalAlignment = HorizontalAlignment.Right, ExpandsLeft = true, Minimum = 0, Maximum = 100, Value = 50 };
        row.Children.Add(drawn);
        byte[] Shot()
        {
            row.Measure(new Size(300, 30));
            row.Arrange(new Rect(0, 0, 300, 30));
            row.UpdateLayout();
            var bitmap = new RenderTargetBitmap(300, 30, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(row);
            var pixels = new byte[300 * 30 * 4];
            bitmap.CopyPixels(pixels, 300 * 4, 0);
            return pixels;
        }

        int LeftOfSlot(byte[] pixels)
        {
            var count = 0;
            for (var y = 0; y < 30; y++)
                for (var x = 0; x < 300 - 30; x++)
                    if (pixels[(y * 300 + x) * 4] != 0 || pixels[(y * 300 + x) * 4 + 1] != 0 || pixels[(y * 300 + x) * 4 + 2] != 0)
                        count++;
            return count;
        }

        var closedLeft = LeftOfSlot(Shot());
        drawn.OpenAtOnce(true);
        var openLeft = LeftOfSlot(Shot());
        expect(closedLeft == 0 && openLeft > 500 && drawn.ActualWidth == 24, F($"open, it is drawn over what is left of its slot ({openLeft} pixels there, none closed), its own width still 24"));

        // A theme put into effect while it is shown (ThemeManager.Applied, raised after the theme's brushes are swapped) is drawn at once,
        // with nothing else about it changing (5.4.4).
        var themed = new Grid { Width = 40, Height = 30, Background = Brushes.Black };
        themed.Resources["TextBrush"] = Brushes.Red;
        var repainted = new ExpandablePillSlider { Minimum = 0, Maximum = 100, Value = 50 };
        themed.Children.Add(repainted);
        (int Red, int Green) Strokes()
        {
            themed.Measure(new Size(40, 30));
            themed.Arrange(new Rect(0, 0, 40, 30));
            themed.UpdateLayout();
            var bitmap = new RenderTargetBitmap(40, 30, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(themed);
            var pixels = new byte[40 * 30 * 4];
            bitmap.CopyPixels(pixels, 40 * 4, 0);
            var (red, green) = (0, 0);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                red += pixels[i + 2] > 0x80 && pixels[i + 1] < 0x40 ? 1 : 0;
                green += pixels[i + 1] > 0x80 && pixels[i + 2] < 0x40 ? 1 : 0;
            }

            return (red, green);
        }

        Delegate[] Subscribed() => (typeof(ThemeManager).GetField(nameof(ThemeManager.Applied), BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Action)
            ?.GetInvocationList().Where(d => ReferenceEquals(d.Target, repainted)).ToArray() ?? [];
        repainted.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var oldTheme = Strokes();
        themed.Resources["TextBrush"] = Brushes.Lime;
        var listening = Subscribed();
        foreach (var handler in listening)
            ((Action)handler)();
        var newTheme = Strokes();
        repainted.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        expect(oldTheme is { Red: > 10, Green: 0 } && newTheme is { Red: 0, Green: > 10 } && listening.Length == 1 && Subscribed().Length == 0,
            "a theme put into effect while it is in a window (ThemeManager.Applied) redraws it at once in the new theme's brushes; it listens only while loaded");

        // The track, a drag, Shift, the wheel, a double-click.
        var (from, to) = pill.TrackSpan;
        expect(to < 0 && from < to && Math.Abs(to - from - 96) < 1e-9, F($"opening to the left, its 96-DIP track lies left of the icon ({from:0} to {to:0})"));
        pill.DragTo(from + (to - from) / 2, fine: false);
        var middle = pill.Value;
        pill.DragTo(from + (to - from) / 2 + 10, fine: true);
        var fine = pill.Value;
        pill.DragTo(to + 50, fine: false);
        var top = pill.Value;
        pill.DragTo(from - 50, fine: false);
        var bottom = pill.Value;
        pill.EndDrag();
        expect(Math.Abs(middle - 50) < 1e-9 && Math.Abs(fine - (50 + 10 / 96.0 * 100 * ExpandablePillSlider.FineFactor)) < 1e-9 && top == 100 && bottom == 0,
            F($"a press jumps to the point (half way: {middle:0}); Shift moves a tenth as far ({fine:0.###} for 10 DIPs); beyond either end it stops at {top:0} and {bottom:0}"));
        pill.Value = 50;
        pill.WheelBy(120);
        var up = pill.Value;
        pill.WheelBy(-240);
        var down = pill.Value;
        pill.WheelBy(40);
        pill.WheelBy(40);
        var partway = pill.Value;
        pill.WheelBy(40);
        expect(up == 55 && down == 45 && partway == 45 && pill.Value == 50, "the wheel: SmallChange a notch, both ways; a touchpad's small turns add up to a notch");
        pill.ResetToDefault();
        expect(pill.Value == 100, "a double-click puts the default back (100)");

        // The readout.
        pill.Value = 42;
        var percent = pill.ReadoutText();
        pill.ReadoutFor = _ => "2.4x";
        var words = pill.ReadoutText();
        pill.ReadoutFor = null;
        expect(percent == "42%" && words == "2.4x" && MainWindow.ZoomReadout(120, 50) == "2.4x" && MainWindow.ZoomReadout(0, 0) == "1x",
            "the readout: the value in ReadoutFormat (42%), or ReadoutFor's words (the Timeline's zoom: 2.4x, and 1x with nothing on it)");

        // Bounds that cross (a binding sets Maximum before Minimum) never throw.
        pill.Maximum = -5;
        pill.DragTo(from, fine: false);
        pill.WheelBy(120);
        pill.ResetToDefault();
        pill.EndDrag();
        expect(pill.Value == 0, "with Minimum above Maximum, a drag, the wheel and a reset keep it at Minimum and nothing throws (never Math.Clamp)");
        pill.Maximum = 100;

        // The icon: its switch, its click.
        var clicks = 0;
        pill.IconClick += (_, _) => clicks++;
        pill.ClickIcon();
        var muted = pill.IsIconChecked;
        pill.ClickIcon();
        expect(muted && !pill.IsIconChecked && clicks == 2, "a click on the speaker switches its mute, and says so (IconClick) each time");

        // Keys.
        var zoom = new ExpandablePillSlider { Icon = PillIcon.Magnifier };
        expect(new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End, Key.Enter }.All(k => pill.OwnsKey(k, ModifierKeys.None))
               && !pill.OwnsKey(Key.Space, ModifierKeys.None) && !pill.OwnsKey(Key.Left, ModifierKeys.Control) && !zoom.OwnsKey(Key.Enter, ModifierKeys.None) && zoom.OwnsKey(Key.Home, ModifierKeys.None),
            "while it has the keyboard it keeps the arrows, Page Up / Down, Home and End (and Enter, for a speaker), never Space (Play) or a chord");

        // The speaker's waves and the magnifier, as strokes.
        var levels = new[] { 0.0, 0.2, 1.0 / 3, 0.5, 2.0 / 3, 0.9 };
        text.Append("   speaker by level: ").Append(string.Join("; ", levels.Select(l => ExpandablePillSlider.SpeakerLevel(l, false) is var s
            ? F($"{l:0.##} = {(s.Crossed ? "crossed" : s.Waves + " waves")}") : ""))).Append('\n');
        expect(ExpandablePillSlider.SpeakerLevel(0, false) == (0, true) && ExpandablePillSlider.SpeakerLevel(0.9, true) == (0, true)
               && ExpandablePillSlider.SpeakerLevel(0.2, false) == (1, false) && ExpandablePillSlider.SpeakerLevel(1.0 / 3, false) == (1, false)
               && ExpandablePillSlider.SpeakerLevel(0.5, false) == (2, false) && ExpandablePillSlider.SpeakerLevel(0.9, false) == (3, false),
            "the speaker: crossed muted or at 0, one wave to a third, two to two thirds, three above");
        int Arcs(PillIcon icon, int waves, bool crossed) => ExpandablePillSlider.IconStrokes(icon, waves, crossed, new Point()).Count(s => s.Radius > 0);
        int Lines(PillIcon icon, int waves, bool crossed) => ExpandablePillSlider.IconStrokes(icon, waves, crossed, new Point()).Count(s => s.Radius == 0);
        var all = new[] { (PillIcon.Speaker, 0, true), (PillIcon.Speaker, 1, false), (PillIcon.Speaker, 2, false), (PillIcon.Speaker, 3, false), (PillIcon.Magnifier, 0, false) }
            .SelectMany(i => ExpandablePillSlider.IconStrokes(i.Item1, i.Item2, i.Item3, new Point()));
        expect(Arcs(PillIcon.Speaker, 1, false) == 1 && Arcs(PillIcon.Speaker, 2, false) == 2 && Arcs(PillIcon.Speaker, 3, false) == 3
               && Arcs(PillIcon.Speaker, 0, true) == 0 && Lines(PillIcon.Speaker, 0, true) == Lines(PillIcon.Speaker, 3, false) + 2
               && Arcs(PillIcon.Magnifier, 0, false) == 2 && Lines(PillIcon.Magnifier, 0, false) == 1
               && all.All(s => s.From.X is >= 0 and <= 16 && s.From.Y is >= 0 and <= 16 && s.To.X is >= 0 and <= 16 && s.To.Y is >= 0 and <= 16),
            "as vector strokes in a 16-DIP box: 1, 2 or 3 arcs for the waves, the cross two strokes instead; the magnifier a ring (two arcs) and a handle");

        // The opening, eased out over 140 ms.
        var curve = Enumerable.Range(0, 15).Select(i => ExpandablePillSlider.OpenFraction(i * 10)).ToList();
        text.Append("   opening every 10 ms: ").Append(string.Join(" ", curve.Select(c => c.ToString("0.00", CultureInfo.InvariantCulture)))).Append('\n');
        expect(ExpandablePillSlider.OpenFraction(0) == 0 && ExpandablePillSlider.OpenFraction(140) == 1 && ExpandablePillSlider.OpenFraction(400) == 1
               && curve.Zip(curve.Skip(1)).All(p => p.Second >= p.First) && ExpandablePillSlider.OpenFraction(70) > 0.8,
            "it opens in 140 ms (0 at 0, 1 at 140 and after), never going back, eased out (past 80% at half way)");

        // UI Automation.
        pill.Value = 30;
        var peer = UIElementAutomationPeer.CreatePeerForElement(pill);
        var range = peer.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
        expect(peer.GetAutomationControlType() == AutomationControlType.Slider && peer.GetName() == "Volume" && range is { Value: 30, Minimum: 0, Maximum: 100, SmallChange: 5, LargeChange: 20 },
            "UI Automation: a slider named Volume with its range (RangeValue)");
        range?.SetValue(60);
        var icon = peer.GetChildren()?.SingleOrDefault();
        var toggle = icon?.GetPattern(PatternInterface.Toggle) as IToggleProvider;
        var before = toggle?.ToggleState;
        toggle?.Toggle();
        expect(pill.Value == 60 && icon?.GetName() == "Mute Player" && before == ToggleState.Off && toggle?.ToggleState == ToggleState.On && pill.IsIconChecked,
            "its value set through RangeValue; its one child the icon, Mute Player, a toggle that mutes");

        // Their homes.
        var src = Path.Combine(RepositoryRoot(), "src");
        var main = XDocument.Load(Path.Combine(src, "MainWindow.xaml"));
        var timeline = XDocument.Load(Path.Combine(src, "Views", "UnifiedTimelinePane.xaml"));
        var transport = main.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == "TransportRight");
        var volumes = transport?.Descendants().Where(e => Attr(e, "AutomationProperties.Name") == "Volume").ToList() ?? [];
        expect(volumes is [{ Name.LocalName: "ExpandablePillSlider" } volume] && Attr(volume, X + "Name") == "VolumePill" && Attr(volume, "Value") == "{Binding Volume}"
               && Attr(volume, "IsIconChecked") == "{Binding IsPlayerMuted}" && Attr(volume, "IconAutomationName") == "Mute Player" && Attr(volume, "ExpandsLeft") == "True",
            "MainWindow.xaml: the playback bar's one Volume is the pill (VolumePill), the volume its value and the player's mute its speaker");
        expect(transport is not null && !main.Descendants().Any(e => Attr(e, "AutomationProperties.Name") == "Mute Player")
               && !transport.Descendants().Any(e => (e.Name.LocalName == "Slider" && Attr(e, "Value") == "{Binding Volume}") || Attr(e, "IsChecked") == "{Binding IsPlayerMuted}"),
            "no Mute Player button anywhere, and no Volume slider or mute switch left beside the pill");
        var zoomPill = timeline.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == "ZoomSlider");
        var corner = timeline.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == "ZoomCorner");
        expect(zoomPill is { Name.LocalName: "ExpandablePillSlider" } && Attr(zoomPill, "AutomationProperties.Name") == "Timeline Zoom" && Attr(zoomPill, "Icon") == "Magnifier"
               && Attr(zoomPill, "TrackLength") == "64" && Attr(zoomPill, "DefaultValue") == "0" && Attr(zoomPill, "ExpandsLeft") is null
               && !timeline.Descendants().Any(e => Attr(e, "AutomationProperties.Name") is "Zoom Timeline In" or "Zoom Timeline Out"),
            "UnifiedTimelinePane.xaml: ZoomSlider is the Timeline Zoom pill (a magnifier opening to the right, a double-click on its track fits) in place of - and +");
        expect(corner?.Elements().Select(e => e.Name.LocalName == "Button" ? Attr(e, "AutomationProperties.Name") : e.Name.LocalName).ToList() is ["Fit Timeline", "TrackHeightPill", "ExpandablePillSlider"]
               && Attr(corner, "Panel.ZIndex") == "1",
            "the corner holds Fit, the track height and then the zoom pill, so the open pill covers neither; the corner is raised over the ruler it opens onto");

        var bar = new PlaybackControlSet();
        bar.Load("Mute,Volume,@3");
        var resaved = bar.Save();
        bar.Load(null);
        text.Append("   Mute,Volume,@3 saves as ").Append(resaved).Append("; the defaults: ").Append(bar.Save()).Append('\n');
        expect(bar.Options.All(o => o.Id != "Mute") && resaved == "Volume,@3" && bar["Volume"],
            "the playback bar offers no standalone Mute (the pill's speaker is its one home); a bar saved with it loads the rest and saves without it");

        var slot = PaneTitleSlot.Of("UnifiedTimeline");
        try
        {
            var pane = new UnifiedTimelinePane();
            if (PaneTitleSlot.Of("UnifiedTimeline") is { } title)
                title.Visibility = Visibility.Collapsed;
            pane.Measure(new Size(1200, 480));
            pane.Arrange(new Rect(0, 0, 1200, 480));
            pane.UpdateLayout();
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(pane).TransformBounds(new Rect(element.RenderSize));
            var ruler = Bounds(pane.Ruler);
            var height = pane.ZoomCorner.Children.OfType<TrackHeightPill>().Single();
            var zoomAt = Bounds(pane.ZoomSlider);
            expect(pane.ZoomSlider is { ActualWidth: 24 } && Math.Abs(ruler.Top) < 0.5 && zoomAt.Left >= Bounds(height).Right,
                F($"the live pane at 1200 x 480: the zoom pill 24 wide at {zoomAt.Left:0} (after the track height), the ruler still at the top ({ruler.Top:0.#})"));
        }
        finally
        {
            PaneTitleSlot.Restore("UnifiedTimeline", slot);
        }
    }

    // ----- speed-pill -----

    // A presentation source for keys raised at a control in no window (KeyEventArgs asks for one; nothing is shown).
    private sealed class NoWindowSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    // What the transport row's pills bind, as a stand-in (5.4.4): its own choice of the bar's controls, so nothing is saved.
    private sealed class TransportState
    {
        public PlaybackControlSet PlaybackControls { get; } = new();

        public IReadOnlyList<string> PlaybackSpeeds { get; init; } = [];

        public int Volume { get; set; } = 100;

        public bool IsPlayerMuted { get; set; }

        public double PlaybackSpeedStep { get; set; } = 3;
    }

    private static void SpeedPill(Action<bool, string> expect, StringBuilder text)
    {
        // Its home: the transport row's one Playback Speed, after the Volume pill; the box it replaces gone.
        var main = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MainWindow.xaml"));
        var transport = main.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == "TransportRight");
        var homes = transport?.Descendants().Where(e => Attr(e, "AutomationProperties.Name") == "Playback Speed").ToList() ?? [];
        var home = homes.FirstOrDefault();
        expect(homes is [{ Name.LocalName: "ExpandablePillSlider" }] && Attr(home, X + "Name") == "SpeedPill" && Attr(home, "Icon") == "Readout" && Attr(home, "ExpandsLeft") == "True"
               && Attr(home, "Value") == "{Binding PlaybackSpeedStep}" && Attr(home, "DetentLabels") == "{Binding PlaybackSpeeds}"
               && (Attr(home, "Minimum"), Attr(home, "Maximum"), Attr(home, "SmallChange"), Attr(home, "LargeChange"), Attr(home, "DefaultValue"), Attr(home, "Detent")) == ("0", "6", "1", "1", "3", "1")
               && Attr(home, "Visibility") == "{Binding PlaybackControls[Speed], Converter={StaticResource BoolToVisibility}}"
               && Attr(home, "ToolTip") == "Playback Speed. Scroll or drag to change; double-click for 1x.",
            "MainWindow.xaml: the row's one Playback Speed is a pill (SpeedPill), its face the speed (Readout), opening left; its value PlaybackSpeedStep, 0 to 6 a step at a time, 3 (1x) on a double-click, the speeds its detents' words; shown as the Views menu says");
        expect(home?.ElementsBeforeSelf().LastOrDefault() is { } volumeHome && Attr(volumeHome, X + "Name") == "VolumePill" && !home.ElementsAfterSelf().Any()
               && !main.Descendants().Any(e => e.Name.LocalName == "ComboBox" && (Attr(e, "AutomationProperties.Name") == "Playback Speed" || Attr(e, "ItemsSource") == "{Binding PlaybackSpeeds}")),
            "it sits after the Volume pill at the row's right end; no speed box (ComboBox) left anywhere");
        // 5.5A (the user's): the Volume pill's size, the icon's 24 x 22 square, so no CollapsedWidth of its own.
        const double width = 24;
        expect(Attr(home, "CollapsedWidth") is null, "it sets no CollapsedWidth: its slot is the icon's square, the Volume pill's size (5.5A)");

        // A real pill as the XAML makes it, bound to a real view model.
        var vm = new MainViewModel();
        var pill = new ExpandablePillSlider
        {
            Icon = PillIcon.Readout, ExpandsLeft = true, Minimum = 0, Maximum = 6, SmallChange = 1, LargeChange = 1, DefaultValue = 3, Detent = 1,
        };
        AutomationProperties.SetName(pill, "Playback Speed");
        pill.SetBinding(ExpandablePillSlider.DetentLabelsProperty, new Binding(nameof(MainViewModel.PlaybackSpeeds)) { Source = vm });
        pill.SetBinding(ExpandablePillSlider.ValueProperty, new Binding(nameof(MainViewModel.PlaybackSpeedStep)) { Source = vm });
        void Lay()
        {
            pill.InvalidateMeasure();
            pill.Measure(new Size(400, 100));
            pill.Arrange(new Rect(pill.DesiredSize));
            pill.UpdateLayout();
        }

        Lay();
        expect(vm.PlaybackSpeed == "1x" && pill.Value == 3 && pill.ReadoutText() == "1x", "bound to the view model: 1x is step 3, and the face says 1x");

        // Collapsed: the speed itself, at one fixed width.
        var (faces, sizes) = (new List<string>(), new List<Size>());
        for (var step = 0; step <= 6; step++)
        {
            pill.Value = step;
            Lay();
            faces.Add(pill.ReadoutText());
            sizes.Add(pill.DesiredSize);
        }

        text.Append("   faces: ").Append(string.Join(" ", faces)).Append('\n');
        expect(faces.SequenceEqual(vm.PlaybackSpeeds) && sizes.All(s => s == new Size(width, 22)) && pill.RenderSize == new Size(width, 22),
            F($"collapsed, its face is the speed at each of the seven steps, and it is {width:0} x 22 at all of them (the Volume pill's size): nothing beside it moves as the speed changes"));

        // The words fitted to the slot at any scale (WPF's ideal text layout), with room either side: the short ones at the bar's size, the
        // long ones drawn smaller (5.5A), never below the least size.
        var family = TextElement.GetFontFamily(pill);
        var room = width - 2 * ExpandablePillSlider.FacePad;
        double FaceWidth(string words, double scale) => ExpandablePillSlider.FaceText(words, family, Brushes.White, scale).Width;
        var widest = vm.PlaybackSpeeds.MaxBy(w => FaceWidth(w, 1));
        var scales = new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };
        // (The type size drawn, from the line's height: it follows the size, and the full size's line is the bar's 12 DIPs.)
        var fullLine = ExpandablePillSlider.FaceText("0", family, Brushes.White, 1).Height;
        double TypeSize(FormattedText face) => ExpandablePillSlider.FaceFontSize * face.Height / fullLine;
        var fitted = scales.SelectMany(scale => vm.PlaybackSpeeds.Select(w => ExpandablePillSlider.FaceText(w, family, Brushes.White, scale, room))).ToList();
        var atOne = vm.PlaybackSpeeds.ToDictionary(w => w, w => TypeSize(ExpandablePillSlider.FaceText(w, family, Brushes.White, 1, room)));
        text.Append("   face sizes: ").Append(string.Join(" ", atOne.Select(p => F($"{p.Key} {p.Value:0.0}")))).Append('\n');
        expect(widest == "0.25x" && FaceWidth("0.25x", 1) > room && fitted.All(f => f.Width <= room + 0.01),
            F($"\"0.25x\" is the widest (too wide for the slot at the bar's size); every speed, fitted, lies within the {room:0} DIPs between {ExpandablePillSlider.FacePad:0} either side at every scale from 100% to 300%"));
        expect(Math.Abs(atOne["1x"] - ExpandablePillSlider.FaceFontSize) < 0.01 && Math.Abs(atOne["2x"] - ExpandablePillSlider.FaceFontSize) < 0.01
               && fitted.All(f => TypeSize(f) >= ExpandablePillSlider.FaceLeastSize - 0.01),
            F($"1x and 2x keep the bar's {ExpandablePillSlider.FaceFontSize:0} DIPs; the wider speeds are drawn smaller, never below {ExpandablePillSlider.FaceLeastSize:0}"));
        var digits = "0123456789".Select(d => FaceWidth(d.ToString(), 1)).ToList();
        expect(digits.Max() - digits.Min() < 1e-6 && Math.Abs(FaceWidth("0.25x", 1) - FaceWidth("0.75x", 1)) < 1e-6,
            F($"in the bar's text ({family.Source} {ExpandablePillSlider.FaceFontSize:0}), its figures tabular: every digit one width, 0.25x and 0.75x alike"));

        // Drawn: "0.25x" inside the slot at 100%, 150% and 200%, clear of either end, nothing outside it.
        var row = new Grid { Width = 120, Height = 30, Background = Brushes.Black };
        row.Resources["TextBrush"] = Brushes.White;
        var shown = new ExpandablePillSlider
        {
            Icon = PillIcon.Readout, ExpandsLeft = true, Minimum = 0, Maximum = 6, Detent = 1, DetentLabels = vm.PlaybackSpeeds, Value = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        row.Children.Add(shown);
        row.Measure(new Size(120, 30));
        row.Arrange(new Rect(0, 0, 120, 30));
        row.UpdateLayout();
        (double Left, double Right, int Outside, int Ink) Inked(double dpi)
        {
            var scale = dpi / 96;
            var (w, h) = ((int)Math.Round(120 * scale), (int)Math.Round(30 * scale));
            var bitmap = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(row);
            var pixels = new byte[w * h * 4];
            bitmap.CopyPixels(pixels, w * 4, 0);
            var (left, right, outside, ink) = (int.MaxValue, -1, 0, 0);
            var (slotLeft, slotTop, slotBottom) = ((120 - width) * scale, 4 * scale, 26 * scale);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var at = (y * w + x) * 4;
                    if (pixels[at] == 0 && pixels[at + 1] == 0 && pixels[at + 2] == 0)
                        continue;
                    if (x < slotLeft || y < slotTop || y >= slotBottom)
                    {
                        outside++;
                        continue;
                    }

                    (left, right, ink) = (Math.Min(left, x), Math.Max(right, x), ink + 1);
                }
            }

            return ((left - slotLeft) / scale, (w - 1 - right) / scale, outside, ink);
        }

        var inked = new[] { 96.0, 144.0, 192.0 }.Select(Inked).ToList();
        expect(inked.All(i => i.Ink > 20 && i.Outside == 0 && i.Left >= 1 && i.Right >= 1),
            F($"drawn at 96, 144 and 192 DPI, \"0.25x\" lies inside its {width:0}-DIP slot at least 1 DIP clear of either end (drawn smaller to fit, 5.5A): never cut, never over its neighbours"));

        // Open: the track left of the face, with no readout beside it (the face is the readout).
        var wide = new Grid { Width = 200, Height = 30, Background = Brushes.Black };
        var opened = new ExpandablePillSlider { Icon = PillIcon.Readout, ExpandsLeft = true, Minimum = 0, Maximum = 6, Detent = 1, Value = 3, HorizontalAlignment = HorizontalAlignment.Right };
        wide.Children.Add(opened);
        opened.OpenAtOnce(true);
        wide.Measure(new Size(200, 30));
        wide.Arrange(new Rect(0, 0, 200, 30));
        wide.UpdateLayout();
        var openShot = new RenderTargetBitmap(200, 30, 96, 96, PixelFormats.Pbgra32);
        openShot.Render(wide);
        var openPixels = new byte[200 * 30 * 4];
        openShot.CopyPixels(openPixels, 200 * 4, 0);
        var leftmost = Enumerable.Range(0, 200).FirstOrDefault(x => Enumerable.Range(0, 30).Any(y => openPixels[(y * 200 + x) * 4 + 1] != 0 || openPixels[(y * 200 + x) * 4 + 2] != 0), -1);
        pill.OpenAtOnce(true);
        var (from, to) = pill.TrackSpan;
        expect(Math.Abs(to + 8) < 1e-9 && Math.Abs(to - from - 96) < 1e-9 && Math.Abs(leftmost - (200 - width - 118)) <= 1 && opened.ActualWidth == width,
            F($"open, its 96-DIP track lies left of the face ({from:0} to {to:0}) and the pill reaches 118 DIPs past it, no readout of its own beside the track; its width in layout still {width:0}"));

        // A drag: the nearest detent, never between, never back.
        pill.Value = 0;
        var (pillChanges, speedChanges) = (0, 0);
        pill.ValueChanged += (_, _) => pillChanges++;
        vm.PropertyChanged += (_, e) => speedChanges += e.PropertyName == nameof(MainViewModel.PlaybackSpeed) ? 1 : 0;
        var dragged = new List<double>();
        for (var x = from - 10; x <= to + 10; x += 0.5)
        {
            pill.DragTo(x, fine: false);
            dragged.Add(pill.Value);
        }

        pill.EndDrag();
        expect(dragged.All(v => v == Math.Round(v)) && dragged.Zip(dragged.Skip(1)).All(p => p.Second >= p.First) && dragged.Distinct().Count() == 7
               && pillChanges == 6 && speedChanges == 6 && vm.PlaybackSpeed == "2x",
            "a drag across the track in half-DIP moves: only whole steps, never back (no jitter from the view model), each speed once: six changes of the pill and six of PlaybackSpeed, ending at 2x");
        var gap = (to - from) / 6;
        pill.DragTo(from + 1.4 * gap, fine: false);
        var nearLow = pill.Value;
        pill.DragTo(from + 1.6 * gap, fine: false);
        var nearHigh = pill.Value;
        pill.DragTo(from + 3 * gap + 2, fine: true);
        var shifted = pill.Value;
        pill.EndDrag();
        expect(nearLow == 1 && nearHigh == 2 && shifted == 3 && vm.PlaybackSpeed == "1x",
            "the thumb snaps to the nearest detent (0.4 of the way to the next: the one before; 0.6: the next); Shift has nothing finer to give, so it snaps too");

        // The wheel and the keys: one speed at a time.
        pill.Value = 3;
        pill.WheelBy(120);
        var up = vm.PlaybackSpeed;
        pill.WheelBy(-240);
        var down = vm.PlaybackSpeed;
        pill.WheelBy(-120 * 5);
        expect(up == "1.25x" && down == "0.75x" && vm.PlaybackSpeed == "0.25x" && pill.Value == 0,
            "the wheel steps one speed a notch (1x up to 1.25x, two down to 0.75x) and stops at 0.25x");
        var source = new NoWindowSource();
        var keyed = new List<string>();
        pill.Value = 3;
        foreach (var key in new[] { Key.Right, Key.Up, Key.Left, Key.Down, Key.Down, Key.PageUp, Key.PageDown, Key.End, Key.Home })
        {
            pill.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            keyed.Add(vm.PlaybackSpeed);
        }

        text.Append("   from 1x, Right Up Left Down Down PageUp PageDown End Home: ").Append(string.Join(" ", keyed)).Append('\n');
        expect(keyed.SequenceEqual(["1.25x", "1.5x", "1.25x", "1x", "0.75x", "1x", "0.75x", "2x", "0.25x"]) && pill.OwnsKey(Key.PageUp, ModifierKeys.None) && !pill.OwnsKey(Key.Enter, ModifierKeys.None),
            "the arrows and Page Up / Down step one speed; End is 2x, Home 0.25x; Enter is not its (no switch)");

        // A double-click: 1x again, on the track or on the face itself (a readout face is no switch); the speaker's stays its mute's.
        pill.Value = 6;
        pill.ResetToDefault();
        var reset = vm.PlaybackSpeed;
        MouseButtonEventArgs DoubleClick()
        {
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, 2);
            return press;
        }

        pill.Value = 0;
        pill.RaiseEvent(DoubleClick());
        var faceReset = vm.PlaybackSpeed;
        var speaker = new ExpandablePillSlider { Minimum = 0, Maximum = 100, DefaultValue = 100, Value = 30, IsIconToggle = true };
        speaker.Measure(new Size(400, 100));
        speaker.Arrange(new Rect(speaker.DesiredSize));
        speaker.OpenAtOnce(true);
        speaker.RaiseEvent(DoubleClick());
        expect(reset == "1x" && faceReset == "1x" && speaker.Value == 30,
            "a double-click puts 1x back, on the track or on the face (\"0.25x\" to 1x); a double-click on the Volume pill's speaker resets nothing (it is the mute's)");

        // UI Automation.
        var peer = UIElementAutomationPeer.CreatePeerForElement(pill);
        var range = peer.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
        range?.SetValue(4.6);
        expect(peer.GetAutomationControlType() == AutomationControlType.Slider && peer.GetName() == "Playback Speed" && range is { Minimum: 0, Maximum: 6, SmallChange: 1, LargeChange: 1 }
               && pill.Value == 5 && vm.PlaybackSpeed == "1.5x" && (peer.GetChildren()?.Count ?? 0) == 0,
            "UI Automation: a slider named Playback Speed, 0 to 6 a step at a time; a value set through RangeValue (4.6) lands on its step (1.5x); no icon child");

        // Its words as UI Automation's Value (5.4.4: a screen reader says "1.5x", not the step), read-only; none on the Volume pill.
        var words = peer.GetPattern(PatternInterface.Value) as IValueProvider;
        var refused = false;
        try
        {
            words?.SetValue("2x");
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        var volumePeer = UIElementAutomationPeer.CreatePeerForElement(new ExpandablePillSlider { Icon = PillIcon.Speaker, IsIconToggle = true });
        expect(words is { IsReadOnly: true, Value: "1.5x" } && refused && pill.Value == 5 && range?.Value == 5 && volumePeer.GetPattern(PatternInterface.Value) is null,
            "UI Automation's Value is the speed's words (1.5x; RangeValue still its step, 5), read-only (a value set there is refused); the Volume pill has none (its RangeValue is its value)");

        // The view model: the step follows the speed and the speed the step; the speed stays the one that counts.
        var stepRaised = 0;
        vm.PropertyChanged += (_, e) => stepRaised += e.PropertyName == nameof(MainViewModel.PlaybackSpeedStep) ? 1 : 0;
        vm.PlaybackSpeed = "0.5x";
        var (stepOfHalf, pillOfHalf, raisedOnce) = (vm.PlaybackSpeedStep, pill.Value, stepRaised);
        vm.PlaybackSpeedStep = 4;
        var (quarterMore, rate) = (vm.PlaybackSpeed, vm.PlaybackRate);
        vm.PlaybackSpeedStep = 9.5;
        var top = vm.PlaybackSpeed;
        vm.PlaybackSpeedStep = -2;
        var bottom = vm.PlaybackSpeed;
        vm.PlaybackSpeed = "3x";
        var unlisted = vm.PlaybackSpeedStep;
        vm.PlaybackSpeed = "fast";
        var nonsense = vm.PlaybackSpeedStep;
        expect(stepOfHalf == 1 && pillOfHalf == 1 && raisedOnce == 1 && quarterMore == "1.25x" && rate == 1.25f && top == "2x" && bottom == "0.25x" && unlisted == 6 && nonsense == 3,
            "PlaybackSpeed 0.5x is step 1 (the pill follows: one PlaybackSpeedStep change); step 4 is 1.25x (PlaybackRate 1.25); past either end 2x or 0.25x; a speed not listed reads as the nearest (3x: step 6), words that are no speed as 1x");
        vm.PlaybackSpeed = "0.75x";
        pill.Value = 2.4;
        expect(vm.PlaybackSpeed == "0.75x" && vm.PlaybackSpeedStep == 2 && pill.Value == 2,
            "a value between steps reaching the view model (2.4, past the pill's own snapping) is put on its step, and the pill is shown that step (2)");
        var viewOnly = typeof(MainViewModel).GetField("NonCommandProperties", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as HashSet<string>;
        expect(viewOnly is not null && viewOnly.Contains(nameof(MainViewModel.PlaybackSpeed)) && viewOnly.Contains(nameof(MainViewModel.PlaybackSpeedStep)),
            "neither the speed nor its step builds the command again (NonCommandProperties): a speed changed during playback rebuilds nothing");
        expect(typeof(MainViewModel).Attributes.HasFlag(TypeAttributes.BeforeFieldInit),
            "MainViewModel has no static constructor of its own (beforefieldinit): the step is named in NonCommandProperties' own list");

        // The row as MainWindow.xaml has it (TransportRight itself, its time left out), bound to a stand-in, the speed pill open (5.4.4).
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var copy = new XElement(transport ?? new XElement(presentation + "StackPanel"));
        copy.Elements().Where(e => e.Name.LocalName == "TimeReadout").Remove();
        copy.AddFirst(new XElement(presentation + "StackPanel.Resources",
            new XElement(presentation + "BooleanToVisibilityConverter", new XAttribute(X + "Key", "BoolToVisibility"))));
        copy.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:HandPegApp");
        copy.SetAttributeValue(XNamespace.Xmlns + "x", X.NamespaceName);
        var state = new TransportState { PlaybackSpeeds = vm.PlaybackSpeeds };
        state.PlaybackControls.Load(null);
        var bar = (StackPanel)System.Windows.Markup.XamlReader.Parse(copy.ToString()
            .Replace("\"clr-namespace:HandPegApp\"", "\"clr-namespace:HandPegApp;assembly=HandPeg\"", StringComparison.Ordinal));
        bar.DataContext = state;
        var host = new Grid { Width = 400, Height = 26 };
        host.Children.Add(bar);
        host.Measure(new Size(400, 26));
        host.Arrange(new Rect(0, 0, 400, 26));
        host.UpdateLayout();
        var (volumePill, speedPill) = ((ExpandablePillSlider)bar.FindName("VolumePill"), (ExpandablePillSlider)bar.FindName("SpeedPill"));
        Rect Within(FrameworkElement element) => element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));
        ExpandablePillSlider? HitAt(double x) => VisualTreeHelper.HitTest(host, new Point(x, 13))?.VisualHit as ExpandablePillSlider;
        speedPill.OpenAtOnce(true);

        // (Drawn once, as a frame on screen would be: WPF hit-tests by what was drawn, and the open pill is drawn past its slot.)
        host.UpdateLayout();
        new RenderTargetBitmap(400, 26, 96, 96, PixelFormats.Pbgra32).Render(host);
        var (volumeSlot, speedAt) = (Within(volumePill), Within(speedPill).Left);
        var crossed = Enumerable.Range(0, (int)volumeSlot.Width).Select(i => HitAt(volumeSlot.Left + 0.5 + i)).ToList();
        var (low, high) = speedPill.TrackSpan;
        var detents = Enumerable.Range(0, 7).Select(i => speedAt + low + i * (high - low) / 6).ToList();
        text.Append(F($"   the speed pill open, from its slot: the Volume slot {volumeSlot.Left - speedAt:0} to {volumeSlot.Right - speedAt:0}, the detents "))
            .Append(string.Join(" ", detents.Select(d => (d - speedAt).ToString("0", CultureInfo.InvariantCulture)))).Append('\n');
        expect(volumeSlot.Width == 24 && crossed.Count == 24 && crossed.All(hit => ReferenceEquals(hit, speedPill)) && Panel.GetZIndex(speedPill) > Panel.GetZIndex(volumePill),
            "the row as MainWindow.xaml has it, the speed pill open: it pops over the Volume pill (raised over it, ZIndex, 5.5A), every point of the speaker's 24-DIP slot the speed pill's, so no speaker shows through the speeds");
        expect(detents.All(d => ReferenceEquals(HitAt(d), speedPill)) && detents.Any(d => d > volumeSlot.Left && d < volumeSlot.Right),
            "the track sits right by the speed's face, over the covered speaker: every detent the speed pill's to the pointer, those over the speaker's slot too");

        // Shut, the speaker is the Volume pill's again: the pointer on it opens it at once; back on the speed, the speed opens at once and
        // the Volume pill shuts: never both open.
        MouseEventArgs Hover(RoutedEvent routed) => new(Mouse.PrimaryDevice, 0) { RoutedEvent = routed };
        speedPill.OpenAtOnce(false);
        host.UpdateLayout();
        new RenderTargetBitmap(400, 26, 96, 96, PixelFormats.Pbgra32).Render(host);
        var speakerAgain = Enumerable.Range(0, (int)volumeSlot.Width).All(i => ReferenceEquals(HitAt(volumeSlot.Left + 0.5 + i), volumePill));
        volumePill.RaiseEvent(Hover(Mouse.MouseEnterEvent));
        var (volumeOpened, waits) = (volumePill.IsExpanded, volumePill.OpensLater);
        volumePill.RaiseEvent(Hover(Mouse.MouseLeaveEvent));
        speedPill.RaiseEvent(Hover(Mouse.MouseEnterEvent));
        expect(speakerAgain && volumeOpened && !waits && speedPill.IsExpanded && !volumePill.IsExpanded,
            "the speed pill shut, every point of the speaker's slot is the Volume pill's again and the pointer there opens it at once; the pointer back on the speed opens it at once, the Volume pill handing over (shut): never both open");

        // A click off an open pill closes it at once (5.4.4, the user's: it opens over the bar's other buttons, never moving them), the press
        // going on to what it was meant for; a press on the pill itself leaves it open.
        MouseButtonEventArgs Press() => new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
        var elsewhere = new Border { Width = 10, Height = 10, Background = Brushes.Transparent };
        var reached = 0;
        elsewhere.PreviewMouseDown += (_, _) => reached++;
        host.Children.Add(elsewhere);
        speedPill.OpenAtOnce(true);
        elsewhere.RaiseEvent(Press());
        var clickedOff = !speedPill.IsExpanded;
        speedPill.OpenAtOnce(true);
        speedPill.RaiseEvent(Press());
        var pressedOn = speedPill.IsExpanded;
        speedPill.OpenAtOnce(false);
        elsewhere.RaiseEvent(Press());
        host.Children.Remove(elsewhere);
        var bar2 = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MainWindow.PlaybackBar.cs"));
        expect(clickedOff && reached == 2 && pressedOn && !speedPill.IsExpanded && !bar2.Contains("OpenPillOverhang", StringComparison.Ordinal),
            "a click anywhere off an open pill closes it at once and goes on to what it was meant for; a press on the pill keeps it open; the bar keeps no room for an open pill (it opens over the other buttons)");

        // No inset (5.5A): the track sits by the face whether the Volume pill is shown or hidden in the Views menu.
        var inset = speedPill.TrackInset;
        state.PlaybackControls.Options.Single(o => o.Id == "Volume").IsShown = false;
        var (alone, aloneSpan) = (speedPill.TrackInset, speedPill.TrackSpan);
        expect(inset == 0 && (low, high) == (-104.0, -8.0) && alone == 0 && aloneSpan == (-104.0, -8.0) && volumePill.Visibility == Visibility.Collapsed,
            "the track runs -104 to -8 from the speed's slot, the Volume pill shown (covered while the speed is open) or hidden in the Views menu alike: no inset");

        // The other pills as they were.
        var volume = new ExpandablePillSlider { Icon = PillIcon.Speaker, IsIconToggle = true };
        var zoom = new ExpandablePillSlider { Icon = PillIcon.Magnifier, TrackLength = 64 };
        foreach (var other in new[] { volume, zoom })
        {
            other.Measure(new Size(400, 100));
            other.Arrange(new Rect(other.DesiredSize));
        }

        expect(volume.DesiredSize == new Size(24, 22) && zoom.DesiredSize == new Size(24, 22) && double.IsNaN(volume.CollapsedWidth) && volume.Detent == 0 && volume.DetentLabels is null
               && volume.TrackInset == 0 && zoom.TrackInset == 0,
            "the Volume and Zoom pills are as they were: no CollapsedWidth (the icon's 24 x 22 square), no detents, no track inset");
    }

    // ----- player-fullscreen -----

    // The view model's members the fullscreen bar binds, as a stand-in.
    private sealed class PlayerState : INotifyPropertyChanged
    {
        private int _volume;
        private bool _muted, _encoded, _video, _playing;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Volume { get => _volume; set => Set(ref _volume, value); }

        public bool IsPlayerMuted { get => _muted; set => Set(ref _muted, value); }

        public bool IsExportQualityPreview { get => _encoded; set => Set(ref _encoded, value); }

        public bool IsVideoExport { get => _video; set => Set(ref _video, value); }

        public bool IsPlaying { get => _playing; set => Set(ref _playing, value); }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static void Fullscreen(Action<bool, string> expect, StringBuilder text)
    {
        // The HUD comes and goes.
        expect(HudChrome.ButtonRestMs == 1500 && HudChrome.ButtonShown(0, false) && HudChrome.ButtonShown(1499, false) && !HudChrome.ButtonShown(1500, false)
               && HudChrome.ButtonShown(60_000, overButton: true),
            "the Fullscreen button stays up while the pointer moved over the player less than 1.5 s ago, or while it is on the button");
        expect(HudChrome.BarRestMs == 1800 && HudChrome.BarShown(1799, false, false) && !HudChrome.BarShown(1800, false, false)
               && HudChrome.BarShown(60_000, overBar: true, held: false) && HudChrome.BarShown(60_000, overBar: false, held: true),
            "the fullscreen bar stays up 1.8 s after the last move, and for as long as the pointer is on it or something of it is held");

        // Esc, first come first served.
        var cases = new (string What, bool Dial, bool Tour, bool Focus, bool Drag, bool Full, bool Crop, bool Exploded, MainWindow.EscapeTaker Taker)[]
        {
            ("everything at once", true, true, false, true, true, true, true, MainWindow.EscapeTaker.Dial),
            ("the dial, a text box with the keyboard", true, false, true, false, true, false, false, MainWindow.EscapeTaker.Dial),
            ("the tour and all below it", false, true, false, true, true, true, true, MainWindow.EscapeTaker.Tour),
            ("a text box with the keyboard, fullscreen", false, false, true, true, true, true, true, MainWindow.EscapeTaker.None),
            ("a 3D drag, fullscreen, crop, 3D", false, false, false, true, true, true, true, MainWindow.EscapeTaker.ExplodedDrag),
            ("fullscreen, crop, 3D", false, false, false, false, true, true, true, MainWindow.EscapeTaker.Fullscreen),
            ("crop, 3D", false, false, false, false, false, true, true, MainWindow.EscapeTaker.Crop),
            ("3D", false, false, false, false, false, false, true, MainWindow.EscapeTaker.Exploded),
            ("nothing", false, false, false, false, false, false, false, MainWindow.EscapeTaker.None),
        };
        var wrong = new List<string>();
        foreach (var c in cases)
        {
            var taker = MainWindow.EscapeTakerFor(c.Dial, c.Tour, c.Focus, c.Drag, c.Full, c.Crop, c.Exploded);
            text.Append("   Esc with ").Append(c.What).Append(": ").Append(taker).Append('\n');
            if (taker != c.Taker)
                wrong.Add(c.What);
        }

        expect(wrong.Count == 0, "Esc goes to the dial, the tour, (nothing past them while a text box, a drop-down or a flyout has the keyboard) a 3D drag, the Fullscreen Preview, Interactive Crop, then the 3D view"
            + (wrong.Count == 0 ? "" : ": not with " + string.Join(", ", wrong)));

        // F11.
        var action = KeybindManager.Action("view.fullscreen");
        expect(action is { Label: "Fullscreen Preview", Category: KeybindManager.View, Glyph: "\uE740", Defaults: ["F11"] } && !action.Has(KeyActionFlags.ChangesProject),
            "view.fullscreen, Fullscreen Preview in the View keys, is F11 by default (listed in Settings > Keybinds by itself)");
        expect(KeybindManager.CheckPresets().Count == 0
               && KeybindManager.PresetNames.All(p => KeybindManager.PresetChords(p)["view.fullscreen"] is [{ Key: Key.F11, Mods: ModifierKeys.None, Button: null }]),
            "every key preset has it on F11, and none gives F11 to anything else");

        // The bar, bound to the player's state.
        var state = new PlayerState { Volume = 42, IsVideoExport = true };
        var bar = new FullscreenTransportBar(null) { DataContext = state };
        var host = new Grid { Width = 1600, Height = 300 };
        host.Children.Add(bar);
        host.Measure(new Size(1600, 300));
        host.Arrange(new Rect(0, 0, 1600, 300));
        host.UpdateLayout();
        var at = bar.TranslatePoint(new Point(), host);
        expect(bar.ActualWidth == 900 && Math.Abs(at.X - 350) < 0.5 && Math.Abs(at.Y + bar.ActualHeight - 284) < 0.5 && bar.CornerRadius == new CornerRadius(10)
               && bar.Background is SolidColorBrush { Color: var face, IsFrozen: true } && face == Color.FromArgb(0xCC, 0x18, 0x18, 0x1C)
               && bar.BorderBrush is SolidColorBrush { Color: var edge } && edge == Color.FromRgb(0x38, 0x38, 0x42),
            F($"the bar on a 1600 x 300 picture: 900 wide (its most), centred, 16 above the bottom ({at.X:0}, {at.Y:0}, {bar.ActualHeight:0} tall), dark and rounded as the wipe's pills"));

        var row = (Grid)bar.Child;
        var parts = row.Children.OfType<FrameworkElement>().OrderBy(Grid.GetColumn).Select(e => e is TimeReadout ? "(time)" : AutomationProperties.GetName(e)).ToList();
        text.Append("   the bar: ").Append(string.Join(", ", parts)).Append('\n');
        expect(parts is ["Play", "Volume", "Playhead", "(time)", "3D Exploded View", "Live Encoded Preview", "Exit Fullscreen Preview"]
               && row.ColumnDefinitions.Select((c, i) => c.Width.IsStar == (i == 2)).All(b => b) && bar.Time.Parent is TimeReadout,
            "Play / Pause, the Volume pill, the scrubber (all the room left), the time (held still: a TimeReadout), the 3D and Live Encoded Preview toggles, Exit");
        expect(!bar.PlayPause.Focusable && !bar.Scrubber.Focusable && !bar.Exploded.Focusable && !bar.EncodedPreview.Focusable && !bar.Exit.Focusable,
            "none of its buttons or its scrubber takes the keyboard: Space stays Play / Pause, the arrows step frames");

        bar.Volume.OpenAtOnce(false);
        var heldClosed = bar.IsHeld;
        bar.Volume.OpenAtOnce(true);
        var heldOpen = bar.IsHeld;
        bar.Volume.OpenAtOnce(false);
        bar.Volume.WheelBy(120);
        bar.Volume.ClickIcon();
        expect(bar.Volume is { Icon: PillIcon.Speaker, IsIconToggle: true, IconAutomationName: "Mute Player", ExpandsLeft: false, Minimum: 0, Maximum: 100 }
               && Panel.GetZIndex(bar.Volume) > Panel.GetZIndex(bar.Scrubber) && state.Volume == 47 && state.IsPlayerMuted && !heldClosed && heldOpen,
            "its Volume is the pill, the playback bar's volume and mute (a notch of the wheel: 42 to 47; its speaker mutes), opening to the right over the scrubber, above it; open, the bar is held up");

        // A light theme's brushes in reach (near-black text, a pale well), the bar stays dark and so does its Volume pill: its speaker and
        // its open pill are drawn in the HUD's own colours (5.4.4).
        var lightRow = new Grid { Width = 1600, Height = 100, Background = Brushes.Black };
        foreach (var (key, rgb) in new[] { ("TextBrush", 0x1A1A1A), ("ControlHoverBrush", 0xE6E6E6), ("ControlBorderBrush", 0xC8C8C8), ("MutedTextBrush", 0x5F5F5F) })
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            lightRow.Resources[key] = brush;
        }

        var lightBar = new FullscreenTransportBar(null) { DataContext = new PlayerState { Volume = 50 } };
        lightRow.Children.Add(lightBar);
        byte[] Shoot()
        {
            lightRow.Measure(new Size(1600, 100));
            lightRow.Arrange(new Rect(0, 0, 1600, 100));
            lightRow.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1600, 100, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(lightRow);
            var pixels = new byte[1600 * 100 * 4];
            bitmap.CopyPixels(pixels, 1600 * 4, 0);
            return pixels;
        }

        static bool Light(byte[] pixels, int x, int y) => pixels[(y * 1600 + x) * 4] > 0xB0 && pixels[(y * 1600 + x) * 4 + 1] > 0xB0 && pixels[(y * 1600 + x) * 4 + 2] > 0xB0;
        var closedShot = Shoot();
        var speakerAt = lightBar.Volume.TransformToAncestor(lightRow).TransformBounds(new Rect(lightBar.Volume.RenderSize));
        var lightStrokes = 0;
        for (var y = (int)speakerAt.Top; y < (int)speakerAt.Bottom; y++)
            for (var x = (int)speakerAt.Left; x < (int)speakerAt.Right; x++)
                lightStrokes += Light(closedShot, x, y) ? 1 : 0;
        lightBar.Volume.OpenAtOnce(true);
        var openShot = Shoot();
        var paleWell = Light(openShot, (int)speakerAt.Left + 50, (int)speakerAt.Top + 4);
        lightBar.Volume.OpenAtOnce(false);
        expect(lightStrokes > 10 && !paleWell,
            "in a light theme its speaker is drawn light on the dark bar, and opened, the pill is dark (the HUD's colours, not the theme's near-black text and pale well)");

        state.IsExportQualityPreview = true;
        var encodedOn = bar.EncodedPreview.IsChecked == true;
        state.IsVideoExport = false;
        var audioOnly = !bar.EncodedPreview.IsEnabled;
        state.IsVideoExport = true;
        bar.EncodedPreview.IsChecked = false;
        expect(encodedOn && audioOnly && !state.IsExportQualityPreview && bar.EncodedPreview.Content as string == MainWindow.LoopbackGlyph,
            "its Live Encoded Preview toggle is the preview's own switch (on, off), greyed out for an Audio Only export");

        var playStyle = new Style(typeof(Button));
        playStyle.Setters.Add(new Setter(ContentControl.ContentProperty, "\uE768"));
        var playing = new DataTrigger { Binding = new Binding("IsPlaying"), Value = true };
        playing.Setters.Add(new Setter(ContentControl.ContentProperty, "\uE769"));
        playStyle.Triggers.Add(playing);
        var styled = new FullscreenTransportBar(playStyle) { DataContext = state };

        // (A style's data trigger is bound once the data binding pass has run: the window's dispatcher runs it.)
        static void Bind() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Bind();
        var paused = styled.PlayPause.Content as string;
        state.IsPlaying = true;
        Bind();
        expect(paused == "\uE768" && styled.PlayPause.Content as string == "\uE769" && !styled.PlayPause.Focusable,
            "Play / Pause takes the playback bar's own style (its glyph turns to Pause while playing) in the bar's look");
        state.IsPlaying = false;

        var told = new List<string>();
        bar.SoughtTo += ms => told.Add(F($"sought {ms:0}"));
        bar.ScrubbedTo += ms => told.Add(F($"scrubbed {ms:0}"));
        bar.ScrubEnded += ms => told.Add(F($"let go at {ms:0}"));
        bar.ShowTime(5000, 10_000, "00:00:05.000", "00:00:10.000");
        var timeText = string.Concat(bar.Time.Inlines.OfType<Run>().Select(r => r.Text));
        var (value, end, silent) = (bar.Scrubber.Value, bar.Scrubber.Maximum, told.Count == 0);
        bar.Scrubber.Value = 7000;
        bar.Scrubber.RaiseEvent(new DragStartedEventArgs(0, 0));
        bar.Scrubber.Value = 8000;
        var held = bar.IsHeld;
        bar.ShowTime(5100, 10_000, "00:00:05.100", "00:00:10.000");
        var kept = bar.Scrubber.Value;
        bar.Scrubber.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        text.Append("   the scrubber said: ").Append(string.Join("; ", told)).Append('\n');
        expect(silent && value == 5000 && end == 10_000 && timeText == "00:00:05.000 / 00:00:10.000",
            "told the time, the scrubber and the readout show it and say nothing back");
        expect(told is ["sought 7000", "scrubbed 8000", "let go at 8000"] && held && kept == 8000 && !bar.IsHeld,
            "a press beside its playhead seeks; a drag scrubs and is let go where it ends; the playhead coming back during the drag does not pull it back");
        expect(KeybindManager.Action(KeyTip.GetAction(bar.Exit) ?? "") is { Id: "view.fullscreen" } && bar.Exit.Content as string == "\uE73F"
               && KeyTip.GetAction(bar.Exploded) == "view.exploded3d",
            "Exit (Back to Window) names F11 as its key; the 3D toggle names its own");

        // The Fullscreen button.
        var button = HudChrome.HoverButton();
        var hidden = button is { Visibility: Visibility.Collapsed, Opacity: 0, IsHitTestVisible: false };
        HudChrome.Fade(button, shown: true, instant: true);
        var up = button is { Visibility: Visibility.Visible, Opacity: 1, IsHitTestVisible: true };
        HudChrome.Fade(button, shown: false, instant: true);
        expect(button.Content as string == "\uE740" && AutomationProperties.GetName(button) == "Fullscreen Preview" && KeyTip.GetAction(button) == "view.fullscreen"
               && button is { HorizontalAlignment: HorizontalAlignment.Right, VerticalAlignment: VerticalAlignment.Top, Focusable: false } && button.Margin == new Thickness(10)
               && hidden && up && button is { Visibility: Visibility.Collapsed, IsHitTestVisible: false },
            "the Fullscreen button: top right, 10 in, the Full Screen glyph, F11 in its tip; made hidden, taking clicks only while shown");

        // The window.
        var window = new PlayerFullscreenWindow(new Int32Rect(0, 0, 1920, 1080));
        var asked = 0;
        var closed = false;
        window.ExitAsked += () => asked++;
        window.Closed += (_, _) => closed = true;
        expect(window is { WindowStyle: WindowStyle.None, ResizeMode: ResizeMode.NoResize, AllowsTransparency: false, ShowInTaskbar: false }
               && System.Windows.Shell.WindowChrome.GetWindowChrome(window) is null && UiScale.GetKeepsItsSize(window) && ReferenceEquals(window.Content, window.Host)
               && window.Background is SolidColorBrush { Color: var black } && black == Colors.Black,
            "the window: borderless, not resizable, opaque black, out of the taskbar, no WindowChrome; its content scaled, its size the monitor's");
        window.Close();
        var refused = !closed && asked == 1;
        window.LetClose();
        window.Close();
        expect(refused && closed && asked == 1, "closed by anything but its owner (Alt+F4) it stays and asks to be left; let close, it closes");

        // Where it all lives.
        var src = Path.Combine(RepositoryRoot(), "src");
        var main = XDocument.Load(Path.Combine(src, "MainWindow.xaml"));
        var video = main.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == "VideoView");
        expect(video?.Elements().ToList() is [{ Name.LocalName: "Grid" } hud] && Attr(hud, X + "Name") == "PlayerHud"
               && hud.Elements().ToList() is [{ } overlay] && Attr(overlay, X + "Name") == "PlayerOverlay",
            "MainWindow.xaml: the player's surface holds PlayerHud, which holds only PlayerOverlay (zoomed with the picture, hidden in 3D); the HUD itself is made in code");
        var fullscreen = File.ReadAllText(Path.Combine(src, "MainWindow.Fullscreen.cs")).ReplaceLineEndings("\n");
        var windowSource = File.ReadAllText(Path.Combine(src, "Controls", "PlayerFullscreenWindow.cs"));
        var keys = File.ReadAllText(Path.Combine(src, "MainWindow.Keybinds.cs"));
        expect(!fullscreen.Contains("_dock.", StringComparison.Ordinal) && !fullscreen.Contains("DockManager", StringComparison.Ordinal)
               && fullscreen.Contains("fullscreen.Host.Children.Remove(VideoView);\n        VideoBorder.Child = VideoView;", StringComparison.Ordinal),
            "MainWindow.Fullscreen.cs never touches the dock, and puts the surface straight back into its pane (VideoBorder.Child = VideoView)");
        expect(!windowSource.Contains("WindowChrome", StringComparison.Ordinal) && !windowSource.Contains("FrameHook", StringComparison.Ordinal)
               && !windowSource.Contains("NCCALCSIZE", StringComparison.Ordinal) && !windowSource.Contains("0x0083", StringComparison.Ordinal),
            "PlayerFullscreenWindow.cs: no WindowChrome, FrameHook or WM_NCCALCSIZE");
        expect(fullscreen.Contains("partial void InitializePlayerHud() => ContentRendered += PlayerHud_ContentRendered;", StringComparison.Ordinal)
               && fullscreen.Contains("Dispatcher.BeginInvoke(HookPlayerHud, DispatcherPriority.ApplicationIdle);", StringComparison.Ordinal),
            "Law 1: before the first frame only a ContentRendered hook; the HUD's own hooks at the first idle moment after it, the rest at first use");
        expect(keys.Contains("[\"view.fullscreen\"] = (() => _fullscreen is not null || VideoView.IsVisible, ToggleFullscreen),", StringComparison.Ordinal)
               && keys.Contains("switch (EscapeTakerFor(", StringComparison.Ordinal),
            "MainWindow.Keybinds.cs: F11 goes in (with a picture) and out; Esc asks EscapeTakerFor");
    }
}
