using System.Windows;
using System.Windows.Media;

namespace HandPegApp;

// ----- Cursor-Locked Scrub Pointer over the tracks (Pass 5.7.1) -----
// While a locked playhead drag (TimelineRuler.PointerLocked) has the cursor over the tracks rather than the ruler, the pointer is drawn
// here: a child of the playhead's own DrawingVisual, so PlaceLine's translation carries it with the line (0 px apart at any refresh rate)
// and its redraws (height changes) never touch it. Built on first use; hidden on release, capture loss or when the cursor leaves.
public sealed partial class UnifiedTimelineControl
{
    private DrawingVisual? _scrubPointer;

    /// <summary>True while the scrub pointer is drawn on the tracks.</summary>
    public bool ShowsScrubPointer => _scrubPointer is { Opacity: > 0 };

    /// <summary>
    /// Draws the scrub pointer at the cursor's height when <paramref name="screen"/> (real pixels; null hides it) is over the tracks'
    /// time area and the playhead is in view; true when drawn (the ruler then keeps the OS cursor hidden).
    /// </summary>
    public bool ShowScrubPointerAt(Point? screen)
    {
        var y = double.NaN;
        if (screen is { } at && PresentationSource.FromVisual(this) is not null)
        {
            var local = PointFromScreen(at);
            if (ScrubPointerOver(local, _headerWidth, ActualWidth, ActualHeight) && _playhead.Opacity > 0)
                y = local.Y;
        }

        if (double.IsNaN(y))
        {
            if (_scrubPointer is not null)
                _scrubPointer.Opacity = 0;
            return false;
        }

        if (_scrubPointer is null)
        {
            _scrubPointer = new DrawingVisual();
            using (var drawing = _scrubPointer.RenderOpen())
                TimelineBar.DrawScrubPointer(drawing);
            _playhead.Children.Add(_scrubPointer);
        }

        // The surface's playhead is drawn centred on its x: the tip sits on that line.
        _scrubPointer.Opacity = 1;
        _scrubPointer.Offset = new Vector(0, y);
        return true;
    }

    /// <summary>
    /// Whether a point (surface DIPs) is where the tracks draw the scrub pointer: inside the time area (right of the headers) and the
    /// control's height. Pure; goldens.
    /// </summary>
    internal static bool ScrubPointerOver(Point local, double headerWidth, double width, double height) =>
        double.IsFinite(local.X) && double.IsFinite(local.Y) && local.X >= headerWidth && local.X <= width && local.Y >= 0 && local.Y <= height;
}
