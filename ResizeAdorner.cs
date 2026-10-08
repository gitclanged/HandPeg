using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Puts a resize handle on the bottom-right corner of a layer on the Arrange canvas. Living in the adorner
/// layer, the handle follows the layer wherever it is moved and always lies above every layer.
/// </summary>
internal sealed class ResizeAdorner : Adorner
{
    private readonly VisualCollection _visuals;
    private readonly Thumb _grip;

    /// <summary>Raised while the handle is dragged, with the distance moved in screen units.</summary>
    public event DragDeltaEventHandler? ResizeDelta;

    public ResizeAdorner(UIElement adornedElement, Style gripStyle, string name) : base(adornedElement)
    {
        _grip = new Thumb { Style = gripStyle, Cursor = Cursors.SizeNWSE, ToolTip = "Drag to resize." };
        System.Windows.Automation.AutomationProperties.SetName(_grip, $"Resize {name}");
        _grip.DragDelta += (_, e) => ResizeDelta?.Invoke(this, e);
        _visuals = new VisualCollection(this) { _grip };
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Centred on the corner of the layer.
        var size = AdornedElement.RenderSize;
        _grip.Arrange(new Rect(size.Width - _grip.Width / 2, size.Height - _grip.Height / 2, _grip.Width, _grip.Height));
        return finalSize;
    }
}
