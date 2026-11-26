using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// 9:16 Social UI Ghost Guide (Pass 5.6.5): a toggleable, input-transparent overlay of where short-video apps draw their own UI over a
/// vertical video (the header bar, the right rail of buttons, and the caption and username block with the bottom bar), so HUD Cutouts are
/// never placed under platform UI. Drawn over <see cref="Frame"/> (the output frame on the host surface); a frame that is not vertical
/// shows the zones over its centred 9:16 region, outlined. Zones are fractions of a 1080 x 1920 frame shared by the common apps.
/// </summary>
public sealed class SocialGhostGuide : FrameworkElement
{
    /// <summary>The dead zones as fractions of a 9:16 frame (left, top, width, height) with their labels (at most two words).</summary>
    public static readonly IReadOnlyList<(Rect Zone, string Label)> Zones =
    [
        (new Rect(0, 0, 1, 0.075), "Header"),
        (new Rect(0.845, 0.42, 0.155, 0.42), "Buttons"),
        (new Rect(0, 0.775, 0.845, 0.225), "Caption, username"),
        (new Rect(0.845, 0.84, 0.155, 0.16), "Bottom bar"),
    ];

    private static readonly Brush ZoneFill = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0x40, 0x81)));
    private static readonly Pen ZonePen = FrozenPen(Color.FromArgb(0xB0, 0xFF, 0x40, 0x81), dashed: true);
    private static readonly Pen RegionPen = FrozenPen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), dashed: true);
    private static readonly Brush LabelFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Rect _frame;

    public SocialGhostGuide()
    {
        IsHitTestVisible = false;
        Focusable = false;
        AutomationProperties.SetName(this, "Social Safe Zones");
    }

    /// <summary>The output frame on the host surface (this element's coordinates); empty draws nothing.</summary>
    public Rect Frame
    {
        get => _frame;
        set
        {
            if (value == _frame)
                return;
            _frame = value;
            InvalidateVisual();
        }
    }

    /// <summary>Pure: the 9:16 region the zones are drawn over: the frame itself when vertical, else its centred, full-height 9:16 part.</summary>
    public static Rect VerticalRegion(Rect frame)
    {
        if (frame.IsEmpty || frame.Width <= 0 || frame.Height <= 0)
            return Rect.Empty;
        if (frame.Height >= frame.Width)
            return frame;

        var width = Math.Min(frame.Width, frame.Height * 9 / 16);
        return new Rect(frame.X + (frame.Width - width) / 2, frame.Y, width, frame.Height);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawing)
    {
        var region = VerticalRegion(_frame);
        if (region.IsEmpty)
            return;

        if (region != _frame)
            drawing.DrawRectangle(null, RegionPen, region);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (zone, label) in Zones)
        {
            var box = new Rect(region.X + zone.X * region.Width, region.Y + zone.Y * region.Height, zone.Width * region.Width, zone.Height * region.Height);
            drawing.DrawRectangle(ZoneFill, ZonePen, box);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelFill, dpi)
            {
                MaxTextWidth = Math.Max(box.Width - 6, 1), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis,
            };
            if (box.Width > 18 && box.Height > text.Height + 4)
                drawing.DrawText(text, new Point(box.X + 3, box.Y + 2));
        }
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, bool dashed)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(color)), 1);
        if (dashed)
            pen.DashStyle = new DashStyle([4, 3], 0);
        pen.Freeze();
        return pen;
    }
}
