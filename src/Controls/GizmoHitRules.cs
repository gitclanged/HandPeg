using System.Windows;

namespace HandPegApp;

/// <summary>
/// Pass 5.6.4A: the layer gizmo's anchor-dot hit rule (LayerGizmoAdorner). The dot stays 9 x 9 DIP on screen but takes the pointer only
/// within a 6 x 6 square, and not at all on a box under 36 DIP either way, so a press on a small box's body moves the box instead of
/// grabbing its anchor (user decision).
/// </summary>
public static class GizmoHitRules
{
    /// <summary>Drawn anchor dot, DIP each way.</summary>
    public const double AnchorDotSize = 9;

    /// <summary>Anchor hit square, DIP each way.</summary>
    public const double AnchorHitSize = 6;

    /// <summary>Boxes narrower or shorter than this (DIP) take no anchor hit.</summary>
    public const double AnchorMinBox = 36;

    /// <summary>True when a press at <paramref name="pointer"/> grabs the anchor dot at <paramref name="anchor"/> of a box this size.</summary>
    public static bool AnchorTakes(Point pointer, Point anchor, double boxWidth, double boxHeight) =>
        Math.Min(boxWidth, boxHeight) >= AnchorMinBox
        && Math.Abs(pointer.X - anchor.X) <= AnchorHitSize / 2 && Math.Abs(pointer.Y - anchor.Y) <= AnchorHitSize / 2;
}
