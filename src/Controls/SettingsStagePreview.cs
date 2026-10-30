using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Which picture the Settings window's live preview stage draws for the option being looked at (4.3-0B).</summary>
public enum StageKind
{
    /// <summary>No picture: the description says it all.</summary>
    None,
    Appearance,
    Timeline,
    Cache,
    Hardware,
    Captions,

    /// <summary>The keys of the main window (4.4): the page draws its own keyboard on the stage (StageKeybindsHost), so no picture here.</summary>
    Keybinds,
}

/// <summary>
/// The picture on the Settings window's live preview stage (Pass 4.3-0B): what the options of a category do, drawn from
/// the copy of the settings being edited, so that it follows every change before Save. A miniature of the chosen theme, a
/// stretch of timeline, the caches' gauges, the processors and graphics cards at work, a speech model's trade-off. Above
/// it (4.4), the part of the editor the option is about, live, as it is now (ShowMirror). All of it is drawn here
/// (OnRender): no child elements.
/// </summary>
public sealed class SettingsStagePreview : FrameworkElement
{
    private AppSettings _settings = new();

    public SettingsStagePreview()
    {
        // The editor's picture is kept between its changes, and drawn again only once the stage's scale has halved or doubled.
        RenderOptions.SetCachingHint(_mirror, CachingHint.Cache);
        RenderOptions.SetCacheInvalidationThresholdMinimum(_mirror, 0.5);
        RenderOptions.SetCacheInvalidationThresholdMaximum(_mirror, 2);
    }

    public StageKind Kind { get; private set; }

    // Whether the kind on show has a picture of its own (the keys' page draws its keyboard itself).
    private bool HasPicture => Kind is not (StageKind.None or StageKind.Keybinds);

    /// <summary>Where a dropped clip goes, as the Timeline tab's box says (the settings take it only on Save).</summary>
    public bool DroppedClipsJoin { get; set; }

    /// <summary>What each cache holds now, in bytes, as last measured: the timeline's, the media browser's, the proxies'.</summary>
    public (long Timeline, long Browser, long Proxies) CacheBytes { get; set; }

    /// <summary>A theme to draw instead of the chosen one: the one under the pointer in the open Theme list.</summary>
    public string? ThemeOverride { get; set; }

    /// <summary>Draws the picture of a kind from the settings being edited (read now and at every redraw).</summary>
    public void Show(StageKind kind, AppSettings settings)
    {
        (Kind, _settings) = (kind, settings);
        ApplyVisibility();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ApplyVisibility() => Visibility = HasPicture || _mirrorSource is not null ? Visibility.Visible : Visibility.Collapsed;

    private const double CoreGap = 4;

    // The labels' row ("Now in the editor", "After Save"), the gap between the editor and the picture, and the editor's
    // least and greatest height on the stage.
    private const double LabelRow = 18, PictureGap = 14, MirrorLeast = 90, MirrorMost = 300;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 420;
        var picture = Kind switch
        {
            StageKind.Appearance => 236,
            StageKind.Timeline => 262,
            StageKind.Cache => 272,
            StageKind.Hardware => 40 + CoreRows() * (CoreCell(width) + CoreGap) + 180 + BenchmarkHeight(),
            StageKind.Captions => 96,
            _ => 0,
        };

        var mirror = MirrorLayout(width).Drawn;
        if (mirror.IsEmpty)
            return new Size(width, picture);

        return new Size(width, mirror.Bottom + (picture > 0 ? PictureGap + LabelRow + picture : 0));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var size = RenderSize;
        if (size.Width < 60)
            return;

        // The editor as it is now, then (labelled, when both are on show) the picture of what Save makes of it.
        var top = 0.0;
        var (viewbox, drawn, scale) = MirrorLayout(size.Width);
        if (!drawn.IsEmpty)
        {
            DrawMirror(drawing, viewbox, drawn, scale);
            top = drawn.Bottom + PictureGap;
            if (HasPicture)
            {
                Text(drawing, "After Save", 0, top, 11, MutedBrush, bold: true, width: size.Width);
                top += LabelRow;
            }
        }

        var rest = new Size(size.Width, size.Height - top);
        if (!HasPicture || rest.Height < 40)
            return;

        if (top > 0)
        {
            var below = new TranslateTransform(0, top);
            below.Freeze();
            drawing.PushTransform(below);
        }

        switch (Kind)
        {
            case StageKind.Appearance: DrawAppearance(drawing, rest); break;
            case StageKind.Timeline: DrawTimeline(drawing, rest); break;
            case StageKind.Cache: DrawCaches(drawing, rest); break;
            case StageKind.Hardware: DrawHardware(drawing, rest); break;
            case StageKind.Captions: DrawSpeechModel(drawing, rest); break;
        }

        if (top > 0)
            drawing.Pop();
    }

    // ----- The editor, live (4.4): the part of the main window an option is about, as it is now -----

    // The editor's picture. A VisualBrush cannot be frozen (a Visual is not a Freezable): it is the one brush here that is
    // not, by necessity, and there is one for the life of the stage. It shows the part asked for in the part's own
    // coordinates (Absolute: children reaching past the part, or turned, never shift it), and never lays out what it shows.
    private readonly VisualBrush _mirror = new()
    {
        Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, AutoLayoutContent = false, TileMode = TileMode.None,
    };

    private FrameworkElement? _mirrorSource;
    private Rect _mirrorView = Rect.Empty, _mirrorVideoRect = Rect.Empty;
    private ImageSource? _mirrorVideo;

    /// <summary>Raised when the part of the editor on show is hidden, shown or resized: the stage asks for it again.</summary>
    public event Action? MirrorChanged;

    /// <summary>
    /// Shows a part of the editor above the picture, live: the element, the part of it (in its own coordinates), and for
    /// the player a still standing in for its video with where the video lies in it (the GPU preview draws the video in a
    /// window of its own, which no brush sees). Null shows none, and lets go of the element shown before.
    /// </summary>
    public void ShowMirror(FrameworkElement? source, Rect view, ImageSource? video, Rect videoRect)
    {
        if (source is null || view.IsEmpty || view.Width <= 0 || view.Height <= 0)
        {
            source = null;
            video = null;
            view = videoRect = Rect.Empty;
        }

        var sameSource = ReferenceEquals(source, _mirrorSource);
        if (sameSource && view == _mirrorView && videoRect == _mirrorVideoRect && ReferenceEquals(video, _mirrorVideo))
            return;

        if (!sameSource)
        {
            if (_mirrorSource is { } old)
            {
                old.IsVisibleChanged -= MirrorSource_IsVisibleChanged;
                old.SizeChanged -= MirrorSource_SizeChanged;
            }

            if (source is not null)
            {
                source.IsVisibleChanged += MirrorSource_IsVisibleChanged;
                source.SizeChanged += MirrorSource_SizeChanged;
            }

            _mirrorSource = source;
            _mirror.Visual = source;
        }

        (_mirrorView, _mirrorVideo, _mirrorVideoRect) = (view, video, videoRect);
        ApplyVisibility();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void MirrorSource_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => MirrorChanged?.Invoke();

    private void MirrorSource_SizeChanged(object sender, SizeChangedEventArgs e) => MirrorChanged?.Invoke();

    /// <summary>
    /// Where the editor's part goes on a stage this wide: as wide as the stage; a short part (a header, a bar) no less than
    /// 90 px tall, or its own height when it is shorter (never enlarged for it), its left end only; a tall one no taller
    /// than 300 px, narrower and centred. The part shown (in its own coordinates), where it is drawn, and its scale.
    /// </summary>
    private (Rect Viewbox, Rect Drawn, double Scale) MirrorLayout(double width)
    {
        var view = _mirrorView;
        if (_mirrorSource is null || view.IsEmpty || view.Width <= 0 || view.Height <= 0 || width <= 0)
            return (Rect.Empty, Rect.Empty, 0);

        var scale = width / view.Width;
        var least = Math.Min(MirrorLeast, view.Height);
        if (view.Height * scale < least)
            scale = least / view.Height;
        if (view.Height * scale > MirrorMost)
            scale = MirrorMost / view.Height;

        var shown = Math.Min(view.Width, width / scale);
        var drawnWidth = shown * scale;
        return (new Rect(view.X, view.Y, shown, view.Height), new Rect((width - drawnWidth) / 2, LabelRow, drawnWidth, view.Height * scale), scale);
    }

    private void DrawMirror(DrawingContext drawing, Rect viewbox, Rect drawn, double scale)
    {
        Text(drawing, "Now in the editor", drawn.Left, 0, 11, MutedBrush, bold: true, width: drawn.Width);
        if (_mirror.Viewbox != viewbox)
            _mirror.Viewbox = viewbox;
        drawing.DrawRoundedRectangle(_mirror, Line(BorderBrush, 1), drawn, 4, 4);

        // The player's video: a still from the thumbnail sheet, or the stand-in landscape, fitted where the video lies.
        var video = _mirrorVideoRect;
        if (video.IsEmpty)
            return;

        var area = new Rect(drawn.X + (video.X - viewbox.X) * scale, drawn.Y + (video.Y - viewbox.Y) * scale, video.Width * scale, video.Height * scale);
        area.Intersect(drawn);
        if (area.IsEmpty || area.Width < 24 || area.Height < 16)
            return;

        var clip = new RectangleGeometry(area);
        clip.Freeze();
        drawing.PushClip(clip);

        var aspect = _mirrorVideo is { Width: > 0, Height: > 0 } still ? still.Width / still.Height : 16.0 / 9;
        var (fitWidth, fitHeight) = area.Width / area.Height > aspect ? (area.Height * aspect, area.Height) : (area.Width, area.Width / aspect);
        var picture = new Rect(area.X + (area.Width - fitWidth) / 2, area.Y + (area.Height - fitHeight) / 2, fitWidth, fitHeight);
        if (_mirrorVideo is { } frame)
        {
            drawing.DrawImage(frame, picture);
        }
        else
        {
            drawing.DrawRectangle(StageBlack, null, picture);
            DrawLandscape(drawing, picture);
        }

        if (area.Height >= 48)
        {
            drawing.DrawRectangle(FadeShade, null, new Rect(area.X, area.Bottom - 18, area.Width, 18));
            Text(drawing, _mirrorVideo is null ? "The video plays in the player itself" : "A still at the playhead: the video plays in the player itself",
                area.X + 6, area.Bottom - 16, 10.5, Brushes.White, width: area.Width - 12);
        }

        drawing.Pop();
    }

    // ----- Appearance: the chosen theme as a little workspace -----

    private void DrawAppearance(DrawingContext drawing, Size size)
    {
        var theme = ThemeOverride ?? _settings.Theme;
        var (tokens, controlRadius, tabRadius) = ThemeManager.Look(theme);
        Brush Of(string key, string fallback = "ControlBrush") =>
            Frozen(tokens.TryGetValue(key, out var color) || tokens.TryGetValue(fallback, out color) ? color : Colors.Gray);

        var (w, h) = (size.Width, size.Height - 22);
        if (w < 240 || h < 190)
            return;

        var frame = new Rect(0.5, 0.5, w - 1, h - 1);
        drawing.DrawRoundedRectangle(Of("WindowBackgroundBrush"), Line(Of("ControlBorderBrush"), 1), frame, 6, 6);

        // The title bar, its words and the three caption buttons.
        var title = Of("TitleBarBrush", "WindowBackgroundBrush");
        drawing.DrawRoundedRectangle(title, null, new Rect(1, 1, w - 2, 24), 6, 6);
        drawing.DrawRectangle(title, null, new Rect(1, 13, w - 2, 12));
        var text = Of("TextBrush");
        var muted = Of("MutedTextBrush", "TextBrush");
        Text(drawing, "HandPeg", 10, 5, 11, text, bold: true);
        var caption = Line(muted, 1);
        drawing.DrawLine(caption, new Point(w - 62, 13), new Point(w - 54, 13));
        drawing.DrawRectangle(null, caption, new Rect(w - 42, 9, 8, 8));
        drawing.DrawLine(caption, new Point(w - 22, 9), new Point(w - 14, 17));
        drawing.DrawLine(caption, new Point(w - 22, 17), new Point(w - 14, 9));

        // Tabs: the open one boxed in the panel's colour with its trace, the others inline.
        var panel = Of("PanelBrush");
        var tabs = new[] { "Layers", "Effects", "Export" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = new Rect(10 + i * 70, 32, 66, 22);
            if (i == 0)
            {
                drawing.DrawRoundedRectangle(panel, Line(Of("ActiveTabEdgeBrush", "TabEdgeBrush"), 1), new Rect(tab.X, tab.Y, tab.Width, tab.Height + 4), tabRadius, tabRadius);
                drawing.DrawRectangle(Of("ActiveTabTraceBrush", "AccentBrush"), null, new Rect(tab.X + 1, tab.Y, tab.Width - 2, 2));
            }
            else
            {
                drawing.DrawRoundedRectangle(Of("InactiveTabBrush", "WindowBackgroundBrush"), Line(Of("TabEdgeBrush", "WindowBackgroundBrush"), 1), tab, tabRadius, tabRadius);
            }

            Text(drawing, tabs[i], tab.X + 10, tab.Y + 4, 11, i == 0 ? text : muted);
        }

        // The panel, with a scrollbar as thick as the slider says, at full size.
        var thickness = Math.Clamp(_settings.ScrollbarThickness, 4, 16);
        var body = new Rect(10, 54, w - 20, h - 64);
        drawing.DrawRoundedRectangle(panel, Line(Of("ControlBorderBrush"), 1), body, controlRadius, controlRadius);
        var thumb = new Rect(body.Right - thickness - 3, body.Top + 8, thickness, Math.Max(body.Height * 0.42, 12));
        drawing.DrawRoundedRectangle(Of("ScrollThumbBrush", "MutedTextBrush"), null, thumb, thickness / 2, thickness / 2);

        // A raised button (the accent's face, its bevel) and a plain one.
        var (x, y) = (body.Left + 12, body.Top + 12);
        var raised = new Rect(x, y, 78, 24);
        drawing.DrawRoundedRectangle(Of("ButtonFaceBrush", "AccentBrush"), Line(Of("ButtonBorderBrush", "AccentBrush"), 1), raised, controlRadius, controlRadius);
        drawing.DrawLine(Line(Of("ButtonBevelBrush", "ButtonFaceBrush"), 1), new Point(raised.Left + controlRadius + 1, raised.Top + 1.5), new Point(raised.Right - controlRadius - 1, raised.Top + 1.5));
        Text(drawing, "Export", raised.Left, raised.Top + 4, 11, Of("ButtonTextBrush", "OnAccentBrush"), width: raised.Width, center: true);
        var plain = new Rect(raised.Right + 8, y, 72, 24);
        drawing.DrawRoundedRectangle(Of("NeutralButtonBrush", "ControlHoverBrush"), Line(Of("NeutralButtonBorderBrush", "ControlBorderBrush"), 1), plain, controlRadius, controlRadius);
        Text(drawing, "Cancel", plain.Left, plain.Top + 4, 11, text, width: plain.Width, center: true);

        // A ticked box with the focus's dotted rectangle round its words, and a sunken empty one.
        y += 38;
        var ticked = new Rect(x, y, 14, 14);
        drawing.DrawRoundedRectangle(Of("CheckedBoxBrush", "AccentBrush"), Line(Of("CheckedBoxBorderBrush", "AccentBrush"), 1), ticked, 2, 2);
        var mark = new StreamGeometry();
        using (var context = mark.Open())
        {
            context.BeginFigure(new Point(ticked.Left + 3, ticked.Top + 7.5), false, false);
            context.PolyLineTo([new Point(ticked.Left + 6, ticked.Top + 10.5), new Point(ticked.Left + 11, ticked.Top + 4)], true, true);
        }

        mark.Freeze();
        drawing.DrawGeometry(null, Line(Of("CheckMarkBrush", "OnAccentBrush"), 1.6), mark);
        var label = Text(drawing, "Snap to I-frames", ticked.Right + 8, y - 1, 11, text);
        if (tokens.TryGetValue("DottedFocusBrush", out var dots) && dots.A > 0)
        {
            var dotted = new Pen(Frozen(dots), 1) { DashStyle = new DashStyle([1, 1], 0) };
            dotted.Freeze();
            drawing.DrawRectangle(null, dotted, new Rect(ticked.Right + 5.5, y - 2.5, label + 6, 19));
        }

        y += 24;
        var well = new Rect(x, y, 14, 14);
        drawing.DrawRoundedRectangle(Of("WellBrush"), Line(Of("WellBorderBrush", "ControlBorderBrush"), 1), well, 2, 2);
        Text(drawing, "Show waveforms", well.Right + 8, y - 1, 11, text);

        // A list with its chosen row.
        y += 26;
        var list = new Rect(x, y, Math.Clamp(body.Width - thickness - 36, 40, 220), Math.Max(body.Bottom - y - 10, 20));
        drawing.DrawRoundedRectangle(Of("WellBrush"), Line(Of("WellBorderBrush", "ControlBorderBrush"), 1), list, controlRadius, controlRadius);
        var rowHeight = Math.Min(18, list.Height / 2);
        drawing.DrawRectangle(Of("SelectedRowBrush", "SelectedFillBrush"), null, new Rect(list.Left + 2, list.Top + 2, list.Width - 4, rowHeight));
        Text(drawing, "Clip 1", list.Left + 8, list.Top + 3, 11, Of("SelectedTextBrush", "TextBrush"));
        if (list.Height > rowHeight * 2 + 4)
            Text(drawing, "Clip 2", list.Left + 8, list.Top + 3 + rowHeight, 11, text);

        Text(drawing, string.Create(CultureInfo.InvariantCulture, $"{ThemeManager.DisplayName(theme)}   ·   scrollbars {thickness:0} px"), 2, h + 4, 11, MutedBrush);
    }

    // ----- Timeline & Player: a stretch of timeline -----

    private void DrawTimeline(DrawingContext drawing, Size size)
    {
        var (left, right) = (8.0, size.Width - 8);
        var span = right - left;
        double At(double fraction) => left + span * fraction;

        // The hover preview above the pointer, as large as its scale makes it.
        var zoom = _settings.HoverPreviewZoom;
        var (pw, ph) = (Math.Min(48 * zoom, span * 0.5), Math.Min(27 * zoom, 76));
        var pointer = At(0.3);
        var preview = new Rect(Math.Clamp(pointer - pw / 2, left, right - pw), 82 - ph, pw, ph);
        drawing.DrawRectangle(StageBlack, Line(MutedBrush, 1), preview);
        DrawLandscape(drawing, preview);
        drawing.DrawLine(Line(MutedBrush, 1), new Point(pointer, preview.Bottom), new Point(pointer, 92));
        Text(drawing, string.Create(CultureInfo.InvariantCulture, $"Hover preview ×{zoom:0.##}"), Math.Min(preview.Right + 8, right - 110), preview.Top, 11, MutedBrush);

        // The ruler and its I-frames; the snap zone round the one nearest the playhead.
        drawing.DrawRectangle(WellBrush, null, new Rect(left, 86, span, 12));
        var iframe = Line(MutedBrush, 1);
        for (var i = 0; i <= 8; i++)
            drawing.DrawLine(iframe, new Point(At(i / 8.0), 90), new Point(At(i / 8.0), 98));

        var (lane1, lane2, lane3) = (new Rect(left, 102, span, 40), new Rect(left, 146, span, 40), new Rect(left, 190, span, 22));
        drawing.DrawRectangle(WellBrush, null, lane1);
        drawing.DrawRectangle(WellBrush, null, lane2);

        var snapAt = At(5 / 8.0);
        var snaps = _settings.SnapTimelineToIFrames;
        if (snaps)
        {
            var radius = Math.Clamp(_settings.TimelineMagnetism, 1, 5) * 5;
            drawing.DrawRectangle(Tint(AccentBrush, 0.22), null, new Rect(snapAt - radius, 86, radius * 2, lane2.Bottom - 86));
        }

        var faint = Line(Tint(MutedBrush, 0.5), 1);
        for (var i = 0; i <= 8; i++)
            drawing.DrawLine(faint, new Point(At(i / 8.0), lane1.Top), new Point(At(i / 8.0), lane2.Bottom));

        // Clips, their corner fades and gain lines.
        DrawClip(drawing, new Rect(At(0.04), lane1.Top + 3, span * 0.46, lane1.Height - 6));
        DrawClip(drawing, new Rect(At(0.52), lane1.Top + 3, span * 0.4, lane1.Height - 6));
        DrawClip(drawing, new Rect(At(0.16), lane2.Top + 3, span * 0.28, lane2.Height - 6));

        // Where a dropped clip goes: into the free stretch of the track under the pointer, or onto a new track.
        var dashed = new Pen(TextBrush, 1) { DashStyle = new DashStyle([3, 2], 0) };
        dashed.Freeze();
        if (DroppedClipsJoin)
        {
            drawing.DrawRoundedRectangle(null, dashed, new Rect(At(0.56), lane2.Top + 3, span * 0.24, lane2.Height - 6), 3, 3);
            Text(drawing, "dropped: joins the track", At(0.57), lane2.Top + 12, 10, TextBrush);
        }
        else
        {
            drawing.DrawRectangle(null, dashed, lane3);
            Text(drawing, "dropped: a new track", lane3.Left + 6, lane3.Top + 4, 10, TextBrush);
        }

        // The ripple: the later clips of every track move, or only of the edited one.
        var ripple = Line(AccentBrush, 1.5);
        foreach (var lane in _settings.RippleAllTracks ? new[] { lane1, lane2 } : [lane1])
        {
            var (x, y) = (At(0.95), lane.Top + lane.Height / 2);
            drawing.DrawLine(ripple, new Point(x - 10, y), new Point(x + 2, y));
            drawing.DrawLine(ripple, new Point(x - 2, y - 4), new Point(x + 2, y));
            drawing.DrawLine(ripple, new Point(x - 2, y + 4), new Point(x + 2, y));
        }

        // The playhead, pulled onto the I-frame when snapping is on.
        var playhead = snaps ? snapAt : At(0.6);
        drawing.DrawLine(Line(PlayheadBrush, 1.5), new Point(playhead, 84), new Point(playhead, lane2.Bottom));

        var y0 = lane3.Bottom + 8;
        Text(drawing, snaps
            ? string.Create(CultureInfo.InvariantCulture, $"Snap to I-frames: on, magnetism {Math.Clamp(_settings.TimelineMagnetism, 1, 5)}")
            : "Snap to I-frames: off", left, y0, 11, TextBrush);
        Text(drawing, (_settings.RippleAllTracks ? "Ripple: every track" : "Ripple: its own track") + (_settings.ShowClipGainLines ? "   ·   gain lines shown" : "   ·   gain lines hidden"),
            left, y0 + 17, 11, MutedBrush);
    }

    private void DrawClip(DrawingContext drawing, Rect clip)
    {
        drawing.DrawRoundedRectangle(Tint(AccentBrush, 0.55), Line(AccentBrush, 1), clip, 3, 3);

        // The fades: the curve from each top corner, the faded stretch darker.
        var fade = Math.Min(clip.Width * 0.18, 26);
        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(clip.BottomLeft, true, true);
            context.QuadraticBezierTo(new Point(clip.Left + fade * 0.15, clip.Top), new Point(clip.Left + fade, clip.Top), true, false);
            context.LineTo(new Point(clip.Left, clip.Top), false, false);
            context.BeginFigure(clip.BottomRight, true, true);
            context.QuadraticBezierTo(new Point(clip.Right - fade * 0.15, clip.Top), new Point(clip.Right - fade, clip.Top), true, false);
            context.LineTo(new Point(clip.Right, clip.Top), false, false);
        }

        curve.Freeze();
        drawing.DrawGeometry(FadeShade, Line(TextBrush, 1), curve);

        if (!_settings.ShowClipGainLines)
            return;

        var gain = clip.Top + clip.Height * 0.35;
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(new Point(clip.Left + 2, clip.Bottom - 2), false, false);
            context.PolyLineTo([new Point(clip.Left + fade, gain), new Point(clip.Right - fade, gain), new Point(clip.Right - 2, clip.Bottom - 2)], true, true);
        }

        line.Freeze();
        drawing.DrawGeometry(null, Line(GainBrush, 1.2), line);
    }

    // A frame's stand-in picture: a sun over two hills.
    private static void DrawLandscape(DrawingContext drawing, Rect frame)
    {
        var hills = new StreamGeometry();
        using (var context = hills.Open())
        {
            context.BeginFigure(frame.BottomLeft, true, true);
            context.PolyLineTo([new Point(frame.Left + frame.Width * 0.35, frame.Top + frame.Height * 0.45),
                new Point(frame.Left + frame.Width * 0.55, frame.Top + frame.Height * 0.7),
                new Point(frame.Left + frame.Width * 0.75, frame.Top + frame.Height * 0.5), frame.BottomRight], false, false);
        }

        hills.Freeze();
        drawing.DrawGeometry(HillBrush, null, hills);
        drawing.DrawEllipse(SunBrush, null, new Point(frame.Left + frame.Width * 0.78, frame.Top + frame.Height * 0.28), frame.Height * 0.11, frame.Height * 0.11);
    }

    // ----- Preview & Cache: what the caches hold, and a proxy beside its original -----

    private void DrawCaches(DrawingContext drawing, Size size)
    {
        var width = size.Width;
        var (timeline, browser, proxies) = CacheBytes;
        var y = 0.0;
        y = Gauge(drawing, "Timeline previews", timeline, _settings.TimelineCacheMegabytes * (1L << 20), _settings.CacheTimelinePreviews, y, width);
        y = Gauge(drawing, "Media browser", browser, _settings.BrowserCacheMegabytes * (1L << 20), _settings.CacheBrowserPreviews, y, width);
        y = Gauge(drawing, "Proxies", proxies, (long)(_settings.ProxyCacheGigabytes * (1L << 30)), true, y, width);

        // A 2160-line original beside a proxy of the chosen height, in proportion.
        y += 6;
        var tier = Math.Clamp(_settings.ProxyTier, 240, 2160);
        var tall = Math.Min(size.Height - y - 34, (width - 30) * 0.5 * 9 / 16);
        if (tall < 20)
            return;

        var original = new Rect(0, y + 18, tall * 16 / 9, tall);
        var proxy = new Rect(original.Right + 24, original.Bottom - tall * tier / 2160, tall * tier / 2160 * 16 / 9, tall * tier / 2160);
        Text(drawing, "Original (2160p)", original.Left, y, 11, MutedBrush);
        Text(drawing, string.Create(CultureInfo.InvariantCulture, $"Proxy ({tier}p)"), proxy.Left, y, 11, MutedBrush);
        drawing.DrawRectangle(StageBlack, Line(MutedBrush, 1), original);
        DrawLandscape(drawing, original);
        drawing.DrawRectangle(StageBlack, Line(AccentBrush, 1), proxy);
        DrawLandscape(drawing, proxy);

        var share = 2160.0 / tier * (2160.0 / tier);
        Text(drawing, string.Create(CultureInfo.InvariantCulture, $"About 1/{share:0} of the pixels to decode and seek through"), 0, original.Bottom + 6, 11, TextBrush);
    }

    private double Gauge(DrawingContext drawing, string name, long bytes, long cap, bool on, double y, double width)
    {
        Text(drawing, name, 0, y, 11, TextBrush, bold: true);
        Text(drawing, on ? $"{PreviewCache.Describe(bytes)} of {PreviewCache.Describe(Math.Max(cap, 0))}" : "Off: made again each time", width, y, 11, MutedBrush, alignRight: true);
        var track = new Rect(0, y + 18, width, 10);
        drawing.DrawRoundedRectangle(WellBrush, Line(BorderBrush, 1), track, 5, 5);
        if (on && cap > 0 && bytes > 0)
        {
            var share = Math.Clamp((double)bytes / cap, 0, 1);
            drawing.DrawRoundedRectangle(share > 0.9 ? WarningBrush : AccentBrush, null, new Rect(0, track.Top, Math.Max(track.Width * share, 10), track.Height), 5, 5);
        }

        return y + 44;
    }

    // ----- Export & Hardware: the processors and graphics cards at work -----

    private static int CoreColumns => Environment.ProcessorCount <= 8 ? Environment.ProcessorCount : Environment.ProcessorCount <= 16 ? 8 : 16;

    private static int CoreRows() => (Environment.ProcessorCount + CoreColumns - 1) / CoreColumns;

    private static double CoreCell(double width) => Math.Clamp((width - (CoreColumns - 1) * CoreGap) / CoreColumns, 8, 30);

    private void DrawHardware(DrawingContext drawing, Size size)
    {
        var width = size.Width;
        var cores = Environment.ProcessorCount;

        // The reserved processors are the first ones, as the tools' Job Object affinity leaves them out (JobObjects.ToolAffinity).
        var reserved = _settings.EffectiveReservedCores;
        var limited = reserved > 0 && cores <= 64 && cores - reserved >= 2;
        Text(drawing, string.Create(CultureInfo.InvariantCulture, $"{cores} logical processors"), 0, 0, 11, TextBrush, bold: true);
        Text(drawing, limited ? string.Create(CultureInfo.InvariantCulture, $"{reserved} kept for the editor and player") : "none kept back: tools may use them all",
            width, 0, 11, MutedBrush, alignRight: true);

        var cell = CoreCell(width);
        var y = 22.0;
        for (var i = 0; i < cores; i++)
        {
            var (row, column) = Math.DivRem(i, CoreColumns);
            var box = new Rect(column * (cell + CoreGap), y + row * (cell + CoreGap), cell, cell);
            var mine = limited && i < reserved;
            drawing.DrawRoundedRectangle(mine ? AccentBrush : Tint(TextBrush, 0.16), Line(mine ? AccentBrush : BorderBrush, 1), box, 3, 3);
        }

        y += CoreRows() * (cell + CoreGap) + 6;
        drawing.DrawRoundedRectangle(AccentBrush, null, new Rect(0, y + 3, 10, 10), 2, 2);
        Text(drawing, "Editor and player", 16, y, 11, TextBrush);
        drawing.DrawRoundedRectangle(Tint(TextBrush, 0.16), Line(BorderBrush, 1), new Rect(130, y + 3, 10, 10), 2, 2);
        var jobs = _settings.BackgroundJobs > 0 ? _settings.BackgroundJobs.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"Auto, {WorkScheduler.LaneLimit(WorkLane.Background)}");
        Text(drawing, $"Background tools ({jobs} at once)", 146, y, 11, TextBrush);

        // The four GPU roles (5.0-0): which card composites, decodes the timeline's layers, takes background work, and exports.
        y += 24;
        var adapters = GpuAdapters.All;
        string Named(int index) => adapters.FirstOrDefault(a => a.Index == index)?.Name ?? "Automatic";
        var compositor = _settings.CompositorGpuAdapter < 0 ? "Auto: the high-performance card" : Named(_settings.CompositorGpuAdapter);
        var decode = _settings.DecodeGpuAdapter < 0 ? "Auto: the compositor's card" : Named(_settings.DecodeGpuAdapter);
        var background = _settings.BackgroundGpu switch
        {
            -2 => "Processor only",
            -1 => GpuAdapters.BackgroundAdapter is { } card ? $"Auto: {card.Name}" : "Automatic",
            var index => Named(index),
        };
        var export = _settings.HardwareDecodeAdapter < 0 ? "Automatic (FFmpeg's choice)" : Named(_settings.HardwareDecodeAdapter);

        var half = (width - 10) / 2;
        Card(drawing, new Rect(0, y, half, 54), "Compositor (player and shaders)", compositor);
        Card(drawing, new Rect(half + 10, y, half, 54), "Timeline layers decode on", decode);
        Card(drawing, new Rect(0, y + 62, half, 54), "Background work on", background);
        Card(drawing, new Rect(half + 10, y + 62, half, 54), "Exports decode on", export);
        var encoder = _settings.DefaultVideoEncoder.Length == 0 ? "the built-in default (Copy)" : _settings.DefaultVideoEncoder;
        Text(drawing, $"A new export encodes with {encoder}", 0, y + 126, 11, MutedBrush, width: width);

        if (BenchmarkHeight() > 0)
            DrawBenchmark(drawing, y + 150, width);
    }

    // ----- The hardware benchmark (5.0-0): each card's measured decoding, seeking, accuracy and transfer speeds -----

    private HardwareBenchmarkResult? _benchmark;
    private double? _benchmarkFraction;
    private string _benchmarkMessage = "";
    private bool _benchmarkStale;

    // Heights of the benchmark's parts: its heading (with the progress track while it runs), a card's block, the cross-card line.
    private const double BenchHeading = 22, BenchProgress = 10, BenchCard = 78, BenchCross = 40, BenchLine = 18;

    /// <summary>
    /// The hardware benchmark's results drawn under the GPU roles: the last saved, or (with <paramref name="fraction"/>,
    /// while it runs) what it has measured so far and how far it has got. A null result and fraction draw nothing.
    /// </summary>
    public void ShowBenchmark(HardwareBenchmarkResult? result, double? fraction, string message, bool stale)
    {
        (_benchmark, _benchmarkFraction, _benchmarkMessage, _benchmarkStale) = (result, fraction, message, stale);
        if (Kind == StageKind.Hardware)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private double BenchmarkHeight()
    {
        if (_benchmark is null && _benchmarkFraction is null)
            return 0;

        var height = BenchHeading + (_benchmarkFraction is null ? 0 : BenchProgress) + 6;
        if (_benchmark is { } result)
        {
            height += result.Adapters.Count * BenchCard;
            if (result.CrossAdapter is not null)
                height += BenchCross;
            if (result.Error.Length > 0)
                height += BenchLine;
        }

        return height;
    }

    private void DrawBenchmark(DrawingContext drawing, double y, double width)
    {
        var result = _benchmark;
        Text(drawing, "Hardware benchmark", 0, y, 11, TextBrush, bold: true);
        var status = _benchmarkFraction is not null ? _benchmarkMessage
            : result is null ? ""
            : _benchmarkStale ? "measured on other cards or drivers: test again"
            : DateTime.TryParse(result.MeasuredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? string.Create(CultureInfo.InvariantCulture, $"measured {at.ToLocalTime():d MMM yyyy, HH:mm} in {result.TotalSeconds:0.0} s")
                : "";
        Text(drawing, status, width, y, 11, _benchmarkStale && _benchmarkFraction is null ? WarningBrush : MutedBrush, alignRight: true);
        y += BenchHeading;

        if (_benchmarkFraction is { } fraction)
        {
            var track = new Rect(0, y, width, 4);
            drawing.DrawRoundedRectangle(WellBrush, null, track, 2, 2);
            drawing.DrawRoundedRectangle(AccentBrush, null, new Rect(0, y, Math.Max(width * Math.Clamp(fraction, 0, 1), 4), 4), 2, 2);
            y += BenchProgress;
        }

        if (result is null)
            return;

        // Every bar of speed on one scale, so that cards compare at a glance; the processor's figure is measured once.
        var software = result.Adapters.Count == 0 ? 0 : result.Adapters.Max(a => a.SoftwareDecodeFps);
        var softwareSeek = result.Adapters.Count == 0 ? 0 : result.Adapters.Max(a => a.SoftwareSeekMs);
        var fastest = Math.Max(software, result.Adapters.Count == 0 ? 0 : result.Adapters.Max(a => a.HardwareDecodeFps));
        var processorBar = Tint(MutedBrush, 0.7);
        foreach (var card in result.Adapters)
        {
            Text(drawing, string.Create(CultureInfo.InvariantCulture, $"{card.Index}: {card.Name}"), 0, y, 11, TextBrush, bold: true, width: width * 0.6);
            var codecs = card.DecodeCodecs.Count > 0 ? string.Join(" ", card.DecodeCodecs) : "no hardware decoders";
            var memory = card.DedicatedVideoMemoryMb > 0 ? string.Create(CultureInfo.InvariantCulture, $"{card.DedicatedVideoMemoryMb / 1024.0:0.#} GB · ") : "";
            Text(drawing, memory + codecs, width, y, 10, MutedBrush, alignRight: true);
            Meter(drawing, "Hardware", card.HardwareDecodeFps, fastest, Fps(card.HardwareDecodeFps), y + 18, width, AccentBrush);
            Meter(drawing, "Processor", software, fastest, Fps(software), y + 34, width, processorBar);

            var line = card.Note.Length > 0 ? card.Note : string.Join("  ·  ", new[]
            {
                card.HardwareSeekMs > 0 ? string.Create(CultureInfo.InvariantCulture, $"Seek {card.HardwareSeekMs:0.#} ms") + (softwareSeek > 0 ? string.Create(CultureInfo.InvariantCulture, $" (processor {softwareSeek:0.#})") : "") : "",
                card.BitExact || card.PsnrDb >= 99 ? "PSNR bit-exact" : card.PsnrDb > 0 ? string.Create(CultureInfo.InvariantCulture, $"PSNR {card.PsnrDb:0.0} dB") : "",
                card.ReadbackMBps > 0 ? "Readback " + MegabytesPerSecond(card.ReadbackMBps) : "",
                card.UploadMBps > 0 ? "Upload " + MegabytesPerSecond(card.UploadMBps) : "",
            }.Where(part => part.Length > 0));
            Text(drawing, line, 0, y + 52, 10, card.Note.Length > 0 ? WarningBrush : MutedBrush, width: width);
            y += BenchCard;
        }

        if (result.CrossAdapter is { } cross)
        {
            Text(drawing, string.Create(CultureInfo.InvariantCulture, $"Across the bus, card {cross.DecodeAdapter} to card {cross.CompositeAdapter}"), 0, y, 11, TextBrush, bold: true);
            Text(drawing, string.Create(CultureInfo.InvariantCulture, $"{cross.TransferMsPerFrame:0.##} ms a 1080p frame"), width, y, 10, MutedBrush, alignRight: true);
            var widest = Math.Max(cross.BandwidthMBps, result.Adapters.Count == 0 ? 0 : result.Adapters.Max(a => Math.Max(a.ReadbackMBps, a.UploadMBps)));
            Meter(drawing, "PCIe", cross.BandwidthMBps, widest, MegabytesPerSecond(cross.BandwidthMBps), y + 18, width, cross.CrossIsFaster ? AccentBrush : processorBar);
            y += BenchCross;
        }

        if (result.Error.Length > 0)
            Text(drawing, $"Stopped short: {result.Error}", 0, y, 10, WarningBrush, width: width);
    }

    // A labelled bar with its figure at the right: the share of the greatest figure it is drawn against.
    private void Meter(DrawingContext drawing, string name, double value, double greatest, string figure, double y, double width, Brush brush)
    {
        const double labelWidth = 66, figureWidth = 84;
        Text(drawing, name, 0, y, 10, MutedBrush, width: labelWidth - 4);
        var track = new Rect(labelWidth, y + 3, Math.Max(width - labelWidth - figureWidth, 20), 8);
        drawing.DrawRoundedRectangle(WellBrush, Line(BorderBrush, 1), track, 4, 4);
        if (value > 0 && greatest > 0)
            drawing.DrawRoundedRectangle(brush, null, new Rect(track.Left, track.Top, Math.Max(track.Width * Math.Clamp(value / greatest, 0, 1), 8), track.Height), 4, 4);
        Text(drawing, value > 0 ? figure : "none", width, y, 10, value > 0 ? TextBrush : MutedBrush, alignRight: true);
    }

    private static string Fps(double fps) => string.Create(CultureInfo.InvariantCulture, $"{fps:#,0} fps");

    private static string MegabytesPerSecond(double rate) => string.Create(CultureInfo.InvariantCulture, $"{rate:#,0} MB/s");

    private void Card(DrawingContext drawing, Rect box, string heading, string value)
    {
        drawing.DrawRoundedRectangle(WellBrush, Line(BorderBrush, 1), box, 4, 4);
        Text(drawing, heading, box.Left + 10, box.Top + 8, 11, MutedBrush);
        Text(drawing, value, box.Left + 10, box.Top + 27, 12, TextBrush, bold: true, width: box.Width - 20);
    }

    // ----- Auto-Captions: the speech model's speed against its accuracy -----

    private void DrawSpeechModel(DrawingContext drawing, Size size)
    {
        var width = size.Width;
        var model = _settings.WhisperModel;
        var (speed, accuracy) = TradeOff(model);
        Text(drawing, model.Replace("ggml-", "").Replace(".bin", ""), 0, 0, 12, TextBrush, bold: true);
        Text(drawing, _settings.UseVad ? "silence skipped (VAD on)" : "listens to all of it (VAD off)", width, 0, 11, MutedBrush, alignRight: true);
        Bar(drawing, "Speed", speed, 22, width);
        Bar(drawing, "Accuracy", accuracy, 46, width);
        Text(drawing, "A rough guide for English speech, not a measurement of this computer.", 0, 72, 10, MutedBrush, width: width);
    }

    private void Bar(DrawingContext drawing, string name, double share, double y, double width)
    {
        Text(drawing, name, 0, y, 11, TextBrush);
        var track = new Rect(70, y + 3, Math.Max(width - 70, 20), 10);
        drawing.DrawRoundedRectangle(WellBrush, Line(BorderBrush, 1), track, 5, 5);
        drawing.DrawRoundedRectangle(AccentBrush, null, new Rect(track.Left, track.Top, Math.Max(track.Width * share, 10), track.Height), 5, 5);
    }

    // Smaller models are faster and make more mistakes; the turbo large model is about the medium one's speed; an English-only model is a little better at English.
    private static (double Speed, double Accuracy) TradeOff(string model)
    {
        var name = model.ToLowerInvariant();
        var (speed, accuracy) = name.Contains("tiny") ? (0.95, 0.35) : name.Contains("base") ? (0.8, 0.5) : name.Contains("small") ? (0.6, 0.66)
            : name.Contains("medium") ? (0.35, 0.8) : name.Contains("turbo") ? (0.45, 0.88) : name.Contains("large") ? (0.15, 0.92) : (0.5, 0.5);
        return (speed, name.Contains(".en") ? Math.Min(accuracy + 0.05, 1) : accuracy);
    }

    // ----- Drawing helpers -----

    private Brush TextBrush => Resource("TextBrush", Brushes.White);

    private Brush MutedBrush => Resource("MutedTextBrush", Brushes.Gray);

    private Brush AccentBrush => Resource("AccentBrush", Brushes.SteelBlue);

    private Brush WellBrush => Resource("WellBrush", Brushes.DimGray);

    private Brush BorderBrush => Resource("ControlBorderBrush", Brushes.Gray);

    private Brush WarningBrush => Resource("WarningTextBrush", Brushes.OrangeRed);

    // A theme brush as it is now, frozen: the pens drawn with it are frozen, and freezing one must never freeze the theme's own brush.
    private Brush Resource(string key, Brush fallback) => TryFindResource(key) is Brush brush ? (Brush)brush.GetCurrentValueAsFrozen() : fallback;

    private static readonly Brush StageBlack = Frozen(Color.FromRgb(0x10, 0x12, 0x16));
    private static readonly Brush HillBrush = Frozen(Color.FromRgb(0x3E, 0x6B, 0x4F));
    private static readonly Brush SunBrush = Frozen(Color.FromRgb(0xF2, 0xC1, 0x4E));
    private static readonly Brush PlayheadBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x48));
    private static readonly Brush GainBrush = Frozen(Color.FromRgb(0xF2, 0xC1, 0x4E));
    private static readonly Brush FadeShade = Frozen(Color.FromArgb(0x70, 0x00, 0x00, 0x00));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // A brush's colour at a share of its opacity (a solid one; any other comes back as it was).
    private static Brush Tint(Brush brush, double opacity) => brush is SolidColorBrush { Color: var color }
        ? Frozen(Color.FromArgb((byte)(color.A * opacity), color.R, color.G, color.B))
        : brush;

    private static Pen Line(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>Writes a line of text; returns how wide it is. Given a width, it is trimmed to it (or centred in it).</summary>
    private double Text(DrawingContext drawing, string text, double x, double y, double size, Brush brush,
        bool bold = false, double width = 0, bool center = false, bool alignRight = false)
    {
        var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (width > 0)
        {
            formatted.MaxTextWidth = width;
            if (center)
                formatted.TextAlignment = TextAlignment.Center;
        }

        drawing.DrawText(formatted, new Point(alignRight ? x - formatted.Width : x, y));
        return formatted.Width;
    }
}
