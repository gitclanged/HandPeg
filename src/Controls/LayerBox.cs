using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// A layer's box on the Canvas and over the player. A plain Thumb takes every point of its rectangle, so whatever lies on top wins,
/// be it a hidden layer or the empty corner of a HUD piece's mask (5.1-CleanupStudioAndFfmpegPurge, the user's directive: a press on
/// the selected object must never land on anything else). Here the box itself answers WPF's hit test, through <see cref="Takes"/>: a
/// point it declines goes on to the box under it. Its template's parts are not hit-testable (MainWindow.xaml), or they would answer first.
/// </summary>
public sealed class LayerBox : Thumb
{
    /// <summary>The layer it stands for; null for none.</summary>
    public Layer? Layer { get; init; }

    /// <summary>Whether it takes a point of itself, in its own units; null takes every point.</summary>
    public Func<LayerBox, Point, bool>? Takes { get; set; }

    /// <summary>Whether a point in a box's own units lies within it, however it is turned.</summary>
    public static bool Holds(FrameworkElement box, Point at) => at.X >= 0 && at.Y >= 0 && at.X < box.ActualWidth && at.Y < box.ActualHeight;

    /// <summary>
    /// Whether a box takes a point (the user's directive, 5.1-CleanupStudioAndFfmpegPurge): a layer not drawn never does; the selected
    /// one takes all of its box, whatever lies over it; no other takes a point inside the selected one's; and an unselected masked
    /// layer takes only what its mask shows, so that a press falls through a HUD piece's empty corners to what lies under them.
    /// </summary>
    /// <param name="held">The selected layer's box on this surface; null with nothing selected.</param>
    /// <param name="drawn">Whether a layer's picture is drawn now (MainViewModel.IsPictureDrawn).</param>
    public static bool TakesPoint(LayerBox box, LayerBox? held, Point at, Func<Layer, bool> drawn)
    {
        if (box.Layer is { } layer && !drawn(layer))
            return false;

        if (ReferenceEquals(box, held))
            return true;

        if (held is { Layer: { } selected } && drawn(selected) && box.TranslatePoint(at, held) is var inside && Holds(held, inside))
            return false;

        return Shows(box, at);
    }

    /// <summary>Whether what a box's layer's mask leaves of it shows at a point of the box.</summary>
    public static bool Shows(LayerBox box, Point at) =>
        box.Layer is null || LayerHitMask.Shows(box.Layer, at.X / Math.Max(box.ActualWidth, 1), at.Y / Math.Max(box.ActualHeight, 1), box.ActualWidth, box.ActualHeight);

    /// <summary>
    /// A click with no drag on the selected box: the next box down at the pointer that would have it, wrapping round from the bottom, so
    /// that sticky selection never strands the layers under a full-frame clip. Null when no other box is there.
    /// </summary>
    /// <param name="boxes">The surface's boxes, bottom first.</param>
    /// <param name="at">The pointer, on <paramref name="surface"/>.</param>
    public static LayerBox? NextDown(IReadOnlyList<LayerBox> boxes, LayerBox from, UIElement surface, Point at, Func<Layer, bool> drawn)
    {
        var here = -1;
        for (var i = 0; i < boxes.Count && here < 0; i++)
            here = ReferenceEquals(boxes[i], from) ? i : -1;

        for (var step = 1; here >= 0 && step < boxes.Count; step++)
        {
            var box = boxes[(here - step + boxes.Count) % boxes.Count];
            var point = surface.TranslatePoint(at, box);
            if (Holds(box, point) && box.Layer is { } layer && drawn(layer) && Shows(box, point))
                return box;
        }

        return null;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var at = hitTestParameters.HitPoint;
        return Holds(this, at) && Takes?.Invoke(this, at) != false ? new PointHitTestResult(this, at) : null;
    }

    protected override GeometryHitTestResult HitTestCore(GeometryHitTestParameters hitTestParameters) =>
        new(this, IntersectionDetail.Intersects);
}
