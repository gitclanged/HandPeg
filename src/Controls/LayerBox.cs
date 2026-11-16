using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Layer hit box on the Canvas and over the player. A plain Thumb captures every point of its rectangle, so the topmost box wins even
/// for a hidden layer or a HUD mask's empty corner, while a press on the selected object must never land on anything else. This box
/// answers WPF hit testing via <see cref="Takes"/>: declined points fall through to the box beneath. Template parts are not
/// hit-testable (MainWindow.xaml), otherwise they would answer first.
/// </summary>
public sealed class LayerBox : Thumb
{
    /// <summary>Represented layer; null for none.</summary>
    public Layer? Layer { get; init; }

    /// <summary>Hit predicate for a point in box-local units; null accepts every point.</summary>
    public Func<LayerBox, Point, bool>? Takes { get; set; }

    /// <summary>True when a box-local point lies within the box (local space, so rotation is already accounted for).</summary>
    public static bool Holds(FrameworkElement box, Point at) => at.X >= 0 && at.Y >= 0 && at.X < box.ActualWidth && at.Y < box.ActualHeight;

    /// <summary>
    /// Hit rule: undrawn layers never hit; the selected box takes its whole area regardless of what is above; other boxes never take
    /// points inside the selected box; unselected masked layers take only mask-visible points, so presses fall through a HUD piece's
    /// empty corners to what is beneath.
    /// </summary>
    /// <param name="held">Selected layer's box on this surface; null when nothing is selected.</param>
    /// <param name="drawn">True when a layer's picture is currently drawn (MainViewModel.IsPictureDrawn).</param>
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

    /// <summary>True when the layer's mask leaves the given box-local point visible.</summary>
    public static bool Shows(LayerBox box, Point at) =>
        box.Layer is null || LayerHitMask.Shows(box.Layer, at.X / Math.Max(box.ActualWidth, 1), at.Y / Math.Max(box.ActualHeight, 1), box.ActualWidth, box.ActualHeight);

    /// <summary>
    /// For a drag-free click on the selected box: the next lower box that would take the pointer, wrapping from the bottom, so sticky
    /// selection never strands layers under a full-frame clip. Null when no other box qualifies.
    /// </summary>
    /// <param name="boxes">Surface boxes, bottom first.</param>
    /// <param name="at">Pointer position on <paramref name="surface"/>.</param>
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
