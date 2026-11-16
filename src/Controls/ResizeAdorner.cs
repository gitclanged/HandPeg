using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Bottom-right resize grip adorner for a layer box (Canvas and player caption box). As an adorner it tracks the box's position
/// and renders above all boxes.
/// </summary>
internal sealed class ResizeAdorner : Adorner
{
    private readonly VisualCollection _visuals;
    private readonly Thumb _grip;

    /// <summary>Raised during a grip drag with the delta in screen units.</summary>
    public event DragDeltaEventHandler? ResizeDelta;

    /// <summary>Raised when a grip drag starts.</summary>
    public event DragStartedEventHandler? ResizeStarted;

    public ResizeAdorner(UIElement adornedElement, Style gripStyle, string name) : base(adornedElement)
    {
        _grip = new Thumb { Style = gripStyle, Cursor = Cursors.SizeNWSE, ToolTip = "Drag to resize." };
        System.Windows.Automation.AutomationProperties.SetName(_grip, $"Resize {name}");
        _grip.DragDelta += (_, e) => ResizeDelta?.Invoke(this, e);
        _grip.DragStarted += (_, e) => ResizeStarted?.Invoke(this, e);
        _visuals = new VisualCollection(this) { _grip };
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Centre the grip on the box's bottom-right corner.
        var size = AdornedElement.RenderSize;
        _grip.Arrange(new Rect(size.Width - _grip.Width / 2, size.Height - _grip.Height / 2, _grip.Width, _grip.Height));
        return finalSize;
    }
}
