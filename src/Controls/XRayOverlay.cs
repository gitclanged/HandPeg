using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// The X-Ray HUD (4.4), shown while Tab is held: every pane of the window outlined with its id, and a panel of what the
/// editor is doing (the UI's frame time, memory, the work lanes, the player, the live graph, undo, the timeline's view). It
/// takes no input: the popup it is drawn in lets every click through to the window under it. Nothing it shows is logged.
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

    /// <summary>What to show: the panes' places in this element's coordinates with their ids, and the panel's lines.</summary>
    public void Show(IReadOnlyList<(Rect Bounds, string Id)> panes, IReadOnlyList<string> lines)
    {
        (_panes, _lines) = (panes, lines);
        InvalidateVisual();
    }

    /// <summary>
    /// Lets the pointer through the popup's window (layered and click-through), so a click while X-Ray shows lands on the
    /// window under it. Called once the popup is open (its window exists then).
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

        // The panel: at the top right, clear of the title bar's buttons.
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

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
