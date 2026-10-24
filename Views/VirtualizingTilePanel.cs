using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HandPegApp.Views;

/// <summary>
/// The media browser's tiles, row after row, with only the rows in view (and one more on each side, for the keyboard
/// and the pictures about to come into view) made. A wrap panel made every tile at once: in a folder of 290 videos that
/// was about 40 MB of elements, more than all the pictures together. Every tile is the same size (the tile template's),
/// so where each one goes follows from its place in the list; the panel scrolls itself by pixels (IScrollInfo) and
/// brings a tile into view for the keyboard and for ScrollIntoView.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    // How far the wheel and the arrows move, in pixels.
    private const double LineStep = 48;

    private Size _extent, _viewport, _tile = new(1, 1);
    private double _offset;
    private int _perRow = 1;

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;

    private int Count => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Touched first: the generator is only ready once the children have been asked for.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var count = Count;
        var width = double.IsInfinity(availableSize.Width) ? _tile.Width * Math.Max(_perRow, 1) : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? _tile.Height : availableSize.Height;

        // The tiles' size: the first one made, measured as it wants to be.
        if (count > 0)
            _tile = MeasureOne(children, generator, children.Count > 0 ? IndexOf(generator, 0) : FirstIndex(count));

        _perRow = Math.Max((int)(width / _tile.Width), 1);
        var rows = (count + _perRow - 1) / _perRow;
        UpdateScrollInfo(new Size(width, height), new Size(width, rows * _tile.Height));

        // The rows in view, and one more on each side.
        var (first, last) = (FirstIndex(count), LastIndex(count));
        if (count > 0 && first <= last)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var at = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (var i = first; i <= last; i++, at++)
                {
                    if (generator.GenerateNext(out var made) is not UIElement child)
                        break;

                    if (made)
                    {
                        if (at >= children.Count)
                            AddInternalChild(child);
                        else
                            InsertInternalChild(at, child);
                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                }
            }
        }

        // The tiles now out of sight are let go.
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last && count > 0)
                continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }

        return new Size(width, Math.Min(height, _extent.Height));
    }

    // Makes (if need be) and measures one tile, for the tiles' size.
    private Size MeasureOne(UIElementCollection children, IItemContainerGenerator generator, int index)
    {
        var start = generator.GeneratorPositionFromIndex(Math.Max(index, 0));
        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            if (generator.GenerateNext(out var made) is not UIElement child)
                return _tile;

            if (made)
            {
                var at = start.Offset == 0 ? start.Index : start.Index + 1;
                if (at >= children.Count)
                    AddInternalChild(child);
                else
                    InsertInternalChild(at, child);
                generator.PrepareItemContainer(child);
            }

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return child.DesiredSize is { Width: > 0, Height: > 0 } size ? size : _tile;
        }
    }

    private static int IndexOf(IItemContainerGenerator generator, int child) => generator.IndexFromGeneratorPosition(new GeneratorPosition(child, 0));

    private int FirstIndex(int count) => count == 0 ? 0 : Math.Clamp(((int)(_offset / _tile.Height) - 1) * _perRow, 0, count - 1);

    private int LastIndex(int count) => count == 0 ? -1 : Math.Clamp(((int)((_offset + _viewport.Height) / _tile.Height) + 2) * _perRow - 1, 0, count - 1);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = IndexOf(generator, i);
            var (row, column) = (index / _perRow, index % _perRow);
            InternalChildren[i].Arrange(new Rect(column * _tile.Width, row * _tile.Height - _offset, _tile.Width, _tile.Height));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = viewport != _viewport || extent != _extent;
        (_viewport, _extent) = (viewport, extent);
        var offset = Math.Clamp(_offset, 0, Math.Max(_extent.Height - _viewport.Height, 0));
        if (offset != _offset)
            (_offset, changed) = (offset, true);
        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    // ----- Scrolling (IScrollInfo) -----

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(_extent.Height - _viewport.Height, 0));
        if (offset == _offset)
            return;

        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void LineUp() => SetVerticalOffset(_offset - LineStep);
    public void LineDown() => SetVerticalOffset(_offset + LineStep);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - LineStep * SystemParameters.WheelScrollLines / 3);
    public void MouseWheelDown() => SetVerticalOffset(_offset + LineStep * SystemParameters.WheelScrollLines / 3);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>Scrolls a tile made (the keyboard's next one) wholly into view.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !InternalChildren[i].IsAncestorOf(visual))
                continue;

            BringRowIntoView(IndexOf(ItemContainerGenerator, i));
            break;
        }

        return rectangle;
    }

    /// <summary>Scrolls a tile that may not be made yet into view, and makes it (ScrollIntoView, the keyboard past the rows made).</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= Count)
            return;

        BringRowIntoView(index);
        UpdateLayout();
    }

    private void BringRowIntoView(int index)
    {
        var top = index / _perRow * _tile.Height;
        if (top < _offset)
            SetVerticalOffset(top);
        else if (top + _tile.Height > _offset + _viewport.Height)
            SetVerticalOffset(top + _tile.Height - _viewport.Height);
    }
}
