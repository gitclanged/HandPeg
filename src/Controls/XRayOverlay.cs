using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static HandPegApp.UiBrushes;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp;

/// <summary>
/// X-Ray HUD shown while Tab is held: outlines every pane with its id plus a diagnostics panel (UI frame time, memory, work lanes,
/// player, live graph, undo, Timeline view). Input-transparent: its popup passes all clicks through to the window beneath. Nothing
/// shown is logged.
/// </summary>
public sealed partial class XRayOverlay : FrameworkElement
{
    private const double PanelWidth = 340, PanelPadding = 10, LineGap = 2;

    private IReadOnlyList<(Rect Bounds, string Id)> _panes = [];
    private IReadOnlyList<string> _lines = [];

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface StatsFace = new("Consolas");

    private static readonly Brush Outline = Frozen(Color.FromArgb(0xE0, 0x4F, 0xC3, 0xF7));
    private static readonly Brush Tint = Frozen(Color.FromArgb(0x16, 0x4F, 0xC3, 0xF7));
    private static readonly Brush TagFill = Frozen(Color.FromArgb(0xE6, 0x0B, 0x3A, 0x52));
    private static readonly Brush PanelFill = Frozen(Color.FromArgb(0xE6, 0x10, 0x10, 0x10));
    private static readonly Brush TextFill = Frozen(Colors.White);
    private static readonly Brush Muted = Frozen(Color.FromRgb(0xB8, 0xC4, 0xCC));
    private static readonly Pen OutlinePen = FrozenPen(Outline, 1.5, dashed: true);
    private static readonly Pen PanelPen = FrozenPen(Outline, 1, dashed: false);

    public XRayOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Sets pane bounds (in this element's coordinates) with ids, and the diagnostics panel lines.</summary>
    public void Show(IReadOnlyList<(Rect Bounds, string Id)> panes, IReadOnlyList<string> lines)
    {
        (_panes, _lines) = (panes, lines);
        InvalidateVisual();
    }

    /// <summary>
    /// Makes the popup window layered and click-through so clicks during X-Ray reach the window beneath. Call after the popup opens
    /// (its HWND exists only then).
    /// </summary>
    public void MakeClickThrough()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource { Handle: var handle } || handle == IntPtr.Zero)
            return;

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if ((style & WsExTransparent) == 0)
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExLayered | WsExNoActivate));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var (bounds, id) in _panes)
        {
            if (bounds.Width < 4 || bounds.Height < 4)
                continue;

            var box = new Rect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
            drawing.DrawRectangle(Tint, OutlinePen, box);

            var tag = Text(id, LabelFace, 11, TextFill, dpi);
            var tagBox = new Rect(box.X + 4, box.Y + 4, tag.Width + 10, tag.Height + 4);
            drawing.DrawRoundedRectangle(TagFill, null, tagBox, 3, 3);
            drawing.DrawText(tag, new Point(tagBox.X + 5, tagBox.Y + 2));
        }

        if (_lines.Count == 0)
            return;

        // Diagnostics panel: top right, below the title bar buttons.
        var texts = new FormattedText[_lines.Count];
        var height = 2 * PanelPadding;
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = Text(_lines[i], StatsFace, i == 0 ? 13 : 11.5, i == 0 ? TextFill : Muted, dpi);
            texts[i].MaxTextWidth = PanelWidth - 2 * PanelPadding;
            texts[i].Trimming = TextTrimming.CharacterEllipsis;
            texts[i].MaxLineCount = 1;
            height += texts[i].Height + LineGap;
        }

        var panel = new Rect(Math.Max(ActualWidth - PanelWidth - 16, 8), 48, PanelWidth, height);
        drawing.DrawRoundedRectangle(PanelFill, PanelPen, panel, 6, 6);
        var y = panel.Y + PanelPadding;
        foreach (var text in texts)
        {
            drawing.DrawText(text, new Point(panel.X + PanelPadding, y));
            y += text.Height + LineGap;
        }
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    private static Pen FrozenPen(Brush brush, double thickness, bool dashed)
    {
        var pen = new Pen(brush, thickness);
        if (dashed)
            pen.DashStyle = new DashStyle([5, 3], 0);
        pen.Freeze();
        return pen;
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;

}

/// <summary>Why a selected clip cannot be seen or heard (the Find Layer X-Ray pill, Pass 5.6.5), in the order a fix reveals it.</summary>
public enum LayerIssue
{
    None,
    NotAtPlayhead,
    Hidden,
    Transparent,
    OffCanvas,
    Covered,
    Muted,
}

/// <summary>
/// "Find Layer / why can't I see or hear this?" (Pass 5.6.5, pure; golden-tested): the first issue of a selected clip at the playhead and
/// the pill's words (at most four each) for it and its one-click fix.
/// </summary>
public static class LayerXRay
{
    /// <summary>
    /// The first issue: not at the playhead, hidden, at 0% opacity, wholly off the frame (<paramref name="box"/> in frame fractions), covered
    /// by an opaque picture above, or (with sound) muted; <see cref="LayerIssue.None"/> when it shows and sounds.
    /// </summary>
    public static LayerIssue Diagnose(bool atPlayhead, bool hidden, double opacityPercent, Rect box, bool covered, bool hasSound, bool soundMuted) =>
        !atPlayhead ? LayerIssue.NotAtPlayhead
        : hidden ? LayerIssue.Hidden
        : opacityPercent <= 0.5 ? LayerIssue.Transparent
        : !OnFrame(box) ? LayerIssue.OffCanvas
        : covered ? LayerIssue.Covered
        : hasSound && soundMuted ? LayerIssue.Muted
        : LayerIssue.None;

    /// <summary>True when some of a box (frame fractions) lies on the frame.</summary>
    public static bool OnFrame(Rect box) =>
        box.Width > 0 && box.Height > 0 && box.Right > 0 && box.X < 1 && box.Bottom > 0 && box.Y < 1;

    /// <summary>True when an upper box (frame fractions) covers all of a lower one, to a thousandth of the frame.</summary>
    public static bool Covers(Rect upper, Rect lower) =>
        upper.Width > 0 && upper.Height > 0 && upper.X <= lower.X + 1e-3 && upper.Y <= lower.Y + 1e-3
        && upper.Right >= lower.Right - 1e-3 && upper.Bottom >= lower.Bottom - 1e-3;

    /// <summary>The pill's text and its fix button's label for an issue; empty for none.</summary>
    public static (string Text, string Fix) Words(LayerIssue issue) => issue switch
    {
        LayerIssue.NotAtPlayhead => ("Not at playhead", "Go to clip"),
        LayerIssue.Hidden => ("Layer hidden", "Show"),
        LayerIssue.Transparent => ("Opacity 0%", "Set 100%"),
        LayerIssue.OffCanvas => ("Off canvas", "Center"),
        LayerIssue.Covered => ("Covered by upper layer", "Select cover"),
        LayerIssue.Muted => ("Sound muted", "Unmute"),
        _ => ("", ""),
    };
}
