using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HandPegApp.Docking;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Workspace (5.5A): a live miniature of a built-in layout in a theme, for the first-run window -----
//
// The layout's arrangement (MainWindow.BuiltInLayoutStates: window-free) drawn as the editor lays it out: each split by its shares,
// the rows as tall as their content (the playback controls, the Sequence Overview Scrubber, the Preset Bar, the header) as thin
// strips, each group a panel with its tabs and a picture of the pane it shows (the player's frame, the timeline's Tracks and Clips,
// the media's tiles, a settings page's rows), all in the theme's own colours (ThemeManager.Look). Every brush and pen frozen.
public sealed partial class SettingsStagePreview
{
    private DockNode? _workspace;
    private string _workspaceName = "";

    /// <summary>The arrangement the Workspace kind draws (a built-in layout's Main); null draws the window with nothing in it.</summary>
    public DockNode? Workspace => _workspace;

    /// <summary>The layout's name, written under the miniature with the theme's.</summary>
    public string WorkspaceName => _workspaceName;

    /// <summary>Shows a layout (by its name and arrangement) in the Workspace kind, drawn again at once when it is on show.</summary>
    public void ShowWorkspace(string name, DockNode? layout)
    {
        if (ReferenceEquals(layout, _workspace) && name == _workspaceName)
            return;

        (_workspaceName, _workspace) = (name, layout);
        if (Kind == StageKind.Workspace)
            InvalidateVisual();
    }

    // The miniature's height for a width: a 16:10 window and its caption line, within reason.
    private static double WorkspaceHeight(double width) => Math.Round(Math.Clamp(width * 0.62, 140, 320));

    // The panes laid out as tall as their content (DockPane.FitsContent in MainWindow), and the height each is drawn at here.
    private static double ThinHeight(string pane) => pane switch
    {
        "Header" => 10,
        "Playback" => 9,
        "Timeline" or "Presets" => 6,
        _ => 0,
    };

    // The panes a new start hides (the Master Meter is off until Tabs and Panes shows it): left out of the miniature's tabs.
    private static bool HiddenAtStart(string pane) => pane == "Meter";

    // A pane's name on its tab, as the editor titles it.
    private static string TabTitle(string pane) => pane switch
    {
        "UnifiedTimeline" => "Timeline",
        "Layout" => "Canvas",
        MainWindow.SubtitleStudioId => "Subtitles",
        _ when pane.StartsWith("Tab:", StringComparison.Ordinal) => pane[4..],
        _ => pane,
    };

    private void DrawWorkspace(DrawingContext drawing, Size size)
    {
        var theme = ThemeOverride ?? _settings.Theme;
        var (tokens, _, tabRadius) = ThemeManager.Look(theme);
        var look = new WorkspaceLook(tokens);
        var (w, h) = (size.Width, size.Height - 20);
        if (w < 120 || h < 80)
            return;

        // The window: its frame, its title bar with the caption buttons, and the arrangement under it.
        var frame = new Rect(0.5, 0.5, w - 1, h - 1);
        drawing.DrawRoundedRectangle(look.Window, Line(look.Border, 1), frame, 5, 5);
        drawing.DrawRoundedRectangle(look.Title, null, new Rect(1, 1, w - 2, 16), 5, 5);
        drawing.DrawRectangle(look.Title, null, new Rect(1, 9, w - 2, 8));
        Text(drawing, "HandPeg", 8, 2.5, 9, look.Text, bold: true);
        var caption = Line(look.Muted, 0.8);
        drawing.DrawLine(caption, new Point(w - 44, 9), new Point(w - 38, 9));
        drawing.DrawRectangle(null, caption, new Rect(w - 30, 6, 6, 6));
        drawing.DrawLine(caption, new Point(w - 16, 6), new Point(w - 10, 12));
        drawing.DrawLine(caption, new Point(w - 16, 12), new Point(w - 10, 6));

        if (_workspace is { } layout)
            DrawDockNode(drawing, layout, new Rect(4, 20, w - 8, h - 24), look, tabRadius);

        Text(drawing, string.Create(CultureInfo.InvariantCulture, $"{_workspaceName}   ·   {ThemeManager.DisplayName(theme)}"), 2, h + 4, 11, MutedBrush, width: w - 4);
    }

    // A part of the arrangement in its room: a group drawn, a split's parts laid out by their shares (thin rows at their height).
    private void DrawDockNode(DrawingContext drawing, DockNode node, Rect room, WorkspaceLook look, double tabRadius)
    {
        if (room.Width < 4 || room.Height < 3)
            return;

        if (node.IsGroup)
        {
            DrawDockGroup(drawing, node, room, look, tabRadius);
            return;
        }

        const double gap = 2;
        var count = node.Children.Count;
        if (count == 0)
            return;

        var thin = node.Children.Select(child => node.Vertical && Thin(child) is { } pane ? ThinHeight(pane) : 0).ToList();
        var length = (node.Vertical ? room.Height : room.Width) - gap * (count - 1) - thin.Sum();
        var shares = node.Children.Select((_, i) => thin[i] > 0 ? 0 : i < node.Sizes.Count && node.Sizes[i] > 0 ? node.Sizes[i] : 1).ToList();
        var total = Math.Max(shares.Sum(), 1e-6);
        var at = node.Vertical ? room.Top : room.Left;
        for (var i = 0; i < count; i++)
        {
            var extent = thin[i] > 0 ? thin[i] : Math.Max(length, 0) * shares[i] / total;
            var part = node.Vertical ? new Rect(room.Left, at, room.Width, extent) : new Rect(at, room.Top, extent, room.Height);
            DrawDockNode(drawing, node.Children[i], part, look, tabRadius);
            at += extent + gap;
        }
    }

    // The pane of a group laid out as tall as its content, when that is all it shows; null otherwise.
    private static string? Thin(DockNode node) =>
        node.IsGroup && node.Panes.Where(p => !HiddenAtStart(p)).ToList() is [var only] && ThinHeight(only) > 0 ? only : null;

    private void DrawDockGroup(DrawingContext drawing, DockNode group, Rect room, WorkspaceLook look, double tabRadius)
    {
        var panes = group.Panes.Where(p => !HiddenAtStart(p)).ToList();
        if (panes.Count == 0)
            return;

        var active = group.Active is { } shown && panes.Contains(shown) ? shown : panes[0];
        if (panes.Count == 1 && ThinHeight(active) > 0)
        {
            DrawThinPane(drawing, active, room, look);
            return;
        }

        drawing.DrawRoundedRectangle(look.Panel, Line(look.Border, 0.75), room, 2.5, 2.5);

        // Tabs over a group of several, or over a settings tab or studio alone; the Timeline alone has none (its title row hidden,
        // CLAUDE.md 8), nor do the player's and the other title-slot panes.
        var body = room;
        var tabbed = panes.Count > 1 || active.StartsWith("Tab:", StringComparison.Ordinal) || active == MainWindow.SubtitleStudioId;
        if (tabbed && room.Height > 22)
        {
            var x = room.Left + 3;
            foreach (var pane in panes)
            {
                var chosen = pane == active;
                var label = TabTitle(pane);
                var width = Math.Min(label.Length * 4.6 + 8, room.Right - 3 - x);
                if (width < 12)
                    break;

                var tab = new Rect(x, room.Top + 2, width, 9);
                if (chosen)
                {
                    drawing.DrawRoundedRectangle(look.Window, null, tab, Math.Min(tabRadius, 2), Math.Min(tabRadius, 2));
                    drawing.DrawRectangle(look.Trace, null, new Rect(tab.Left + 1, tab.Top, tab.Width - 2, 1.2));
                }

                Text(drawing, label, tab.Left + 3, tab.Top - 0.5, 7.5, chosen ? look.Text : look.Muted, width: tab.Width - 4);
                x += width + 2;
            }

            body = new Rect(room.Left, room.Top + 12, room.Width, room.Height - 12);
        }

        var inner = new Rect(body.Left + 3, body.Top + 3, Math.Max(body.Width - 6, 0), Math.Max(body.Height - 6, 0));
        if (inner.Width < 6 || inner.Height < 4)
            return;

        switch (active)
        {
            case "Player": DrawMiniPlayer(drawing, inner); break;
            case "UnifiedTimeline": DrawMiniTracks(drawing, inner, look); break;
            case "Media": DrawMiniTiles(drawing, inner, look); break;
            default: DrawMiniRows(drawing, inner, look); break;
        }
    }

    // The player: a 16:9 frame (a hill under a sun), as large as the room allows, in the middle of it.
    private static void DrawMiniPlayer(DrawingContext drawing, Rect room)
    {
        var (width, height) = room.Width / room.Height > 16.0 / 9 ? (room.Height * 16 / 9, room.Height) : (room.Width, room.Width * 9 / 16);
        var picture = new Rect(room.Left + (room.Width - width) / 2, room.Top + (room.Height - height) / 2, width, height);
        drawing.DrawRectangle(StageBlack, null, picture);
        drawing.DrawEllipse(SunBrush, null, new Point(picture.Left + width * 0.72, picture.Top + height * 0.3), height * 0.09, height * 0.09);
        drawing.DrawRectangle(HillBrush, null, new Rect(picture.Left, picture.Top + height * 0.68, width, height * 0.32));
    }

    // The timeline: a few Tracks, each with its Clips, and the playhead across them.
    private static void DrawMiniTracks(DrawingContext drawing, Rect room, WorkspaceLook look)
    {
        var rows = (int)Math.Clamp(Math.Floor(room.Height / 9), 1, 5);
        var height = room.Height / rows;
        double[][] clips = [[0.02, 0.34, 0.37, 0.71], [0.12, 0.52, 0.58, 0.9], [0.0, 0.28, 0.44, 0.63], [0.2, 0.47], [0.05, 0.81]];
        for (var row = 0; row < rows; row++)
        {
            var top = room.Top + row * height;
            drawing.DrawRectangle(look.Lane, null, new Rect(room.Left, top + height - 1, room.Width, 0.6));
            var spans = clips[row % clips.Length];
            for (var i = 0; i + 1 < spans.Length; i += 2)
                drawing.DrawRoundedRectangle(row == 0 ? look.Clip : look.SoundClip, null,
                    new Rect(room.Left + room.Width * spans[i], top + 1.5, room.Width * (spans[i + 1] - spans[i]), Math.Max(height - 3.5, 1)), 1.2, 1.2);
        }

        var x = room.Left + room.Width * 0.4;
        drawing.DrawLine(Line(PlayheadBrush, 1), new Point(x, room.Top), new Point(x, room.Bottom));
    }

    // The media: a grid of tiles.
    private static void DrawMiniTiles(DrawingContext drawing, Rect room, WorkspaceLook look)
    {
        const double tileWidth = 16, tileHeight = 10, gap = 3;
        var columns = (int)Math.Max(Math.Floor((room.Width + gap) / (tileWidth + gap)), 1);
        var rows = (int)Math.Max(Math.Floor((room.Height + gap) / (tileHeight + gap)), 0);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
                drawing.DrawRectangle((row + column) % 3 == 0 ? look.Clip : look.Well, null,
                    new Rect(room.Left + column * (tileWidth + gap), room.Top + row * (tileHeight + gap), tileWidth, tileHeight));
        }
    }

    // A pane of settings or tools: rows of words and a slider.
    private static void DrawMiniRows(DrawingContext drawing, Rect room, WorkspaceLook look)
    {
        double[] widths = [0.62, 0.45, 0.78, 0.38, 0.55, 0.7];
        for (var row = 0; row < widths.Length; row++)
        {
            var top = room.Top + row * 7;
            if (top + 3 > room.Bottom)
                break;
            drawing.DrawRoundedRectangle(row == 2 ? look.Clip : look.Well, null, new Rect(room.Left, top, room.Width * widths[row], 3), 1.5, 1.5);
        }
    }

    // A row as tall as its content: the playback controls, the Sequence Overview Scrubber, the Preset Bar, the header.
    private static void DrawThinPane(DrawingContext drawing, string pane, Rect room, WorkspaceLook look)
    {
        drawing.DrawRoundedRectangle(look.Panel, null, room, 1.5, 1.5);
        var middle = room.Top + room.Height / 2;
        switch (pane)
        {
            case "Playback":
                var play = new StreamGeometry();
                using (var context = play.Open())
                {
                    var x = room.Left + room.Width * 0.3;
                    context.BeginFigure(new Point(x, middle - 2.5), true, true);
                    context.PolyLineTo([new Point(x + 4.5, middle), new Point(x, middle + 2.5)], true, false);
                }

                play.Freeze();
                drawing.DrawGeometry(look.Text, null, play);
                drawing.DrawRoundedRectangle(look.Well, null, new Rect(room.Left + room.Width * 0.3 + 9, middle - 1, room.Width * 0.45, 2), 1, 1);
                break;
            case "Timeline":
                drawing.DrawRectangle(look.Well, null, new Rect(room.Left + 2, middle - 1, room.Width - 4, 2));
                drawing.DrawRectangle(look.Clip, null, new Rect(room.Left + room.Width * 0.3, middle - 1.5, room.Width * 0.18, 3));
                break;
            case "Presets":
                for (var i = 0; i < 5 && room.Left + 4 + i * 20 + 16 < room.Right; i++)
                    drawing.DrawRoundedRectangle(look.Well, null, new Rect(room.Left + 4 + i * 20, middle - 1.5, 16, 3), 1.5, 1.5);
                break;
            default:
                drawing.DrawRoundedRectangle(look.Well, null, new Rect(room.Left + 4, middle - 1.5, room.Width * 0.5, 3), 1.5, 1.5);
                break;
        }
    }

    /// <summary>A theme's colours for the miniature, each a frozen brush made once a drawing.</summary>
    private sealed class WorkspaceLook
    {
        public WorkspaceLook(IReadOnlyDictionary<string, Color> tokens)
        {
            Brush Of(string key, string fallback = "ControlBrush") =>
                Frozen(tokens.TryGetValue(key, out var color) || tokens.TryGetValue(fallback, out color) ? color : Colors.Gray);

            Window = Of("WindowBackgroundBrush");
            Title = Of("TitleBarBrush", "WindowBackgroundBrush");
            Panel = Of("PanelBrush");
            Border = Of("ControlBorderBrush");
            Text = Of("TextBrush");
            Muted = Of("MutedTextBrush", "TextBrush");
            Trace = Of("ActiveTabTraceBrush", "AccentBrush");
            Well = Of("WellBrush");
            Lane = Tint(Muted, 0.35);
            var accent = tokens.TryGetValue("AccentBrush", out var color) ? color : Colors.SteelBlue;
            Clip = Frozen(Color.FromArgb(0xD0, accent.R, accent.G, accent.B));
            SoundClip = Frozen(Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        }

        public Brush Window { get; }
        public Brush Title { get; }
        public Brush Panel { get; }
        public Brush Border { get; }
        public Brush Text { get; }
        public Brush Muted { get; }
        public Brush Trace { get; }
        public Brush Well { get; }
        public Brush Lane { get; }
        public Brush Clip { get; }
        public Brush SoundClip { get; }
    }
}
