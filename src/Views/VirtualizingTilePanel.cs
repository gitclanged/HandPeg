using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HandPegApp.Views;

/// <summary>
/// Virtualizing media-browser tile grid: only visible rows plus one buffer row each side (for keyboard navigation and upcoming
/// thumbnails) are realized. A WrapPanel realized every tile, ~40 MB of elements for a 290-video folder, more than all thumbnails
/// combined. Uniform tile size makes position a function of index; the panel scrolls by pixels (IScrollInfo) and brings tiles into view
/// for keyboard navigation and ScrollIntoView.
/// Columns fill the full width: as many <see cref="TileWidth"/> tiles as fit (at least two from <see cref="TwoColumnsFrom"/>), each
/// stretched to an equal share so no empty band remains at the right. The share is computed in the same measure pass that places
/// the tiles, so layout runs once.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    // Wheel and arrow-key scroll step in pixels.
    private const double LineStep = 48;

    /// <summary>Panel width at or above which at least two columns are used, regardless of requested tile width.</summary>
    public const double TwoColumnsFrom = 190;

    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(208.0, FrameworkPropertyMetadataOptions.AffectsMeasure), width => width is double w && w >= 1);

    /// <summary>Requested tile width including margins; actual cells are stretched or shrunk slightly to fill the row.</summary>
    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    /// <summary>Current per-tile width after stretching columns to the full width.</summary>
    public double CellWidth { get; private set; }

    public static readonly RoutedEvent CellWidthChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(CellWidthChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(VirtualizingTilePanel));

    /// <summary>Bubbling event raised when <see cref="CellWidth"/> changes, so the pane can decode thumbnails at that size.</summary>
    public event RoutedEventHandler CellWidthChanged
    {
        add => AddHandler(CellWidthChangedEvent, value);
        remove => RemoveHandler(CellWidthChangedEvent, value);
    }

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
        // Access InternalChildren first: the generator is only initialized after children are requested.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var count = Count;
        var width = double.IsInfinity(availableSize.Width) ? TileWidth * Math.Max(_perRow, 1) : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? _tile.Height : availableSize.Height;

        // Compute columns and cell width; tile height is the first realized tile's height at that width.
        (_perRow, var cell) = Columns(width, TileWidth);
        if (cell != CellWidth)
        {
            CellWidth = cell;
            RaiseEvent(new RoutedEventArgs(CellWidthChangedEvent, this));
        }

        _tile = new Size(cell, _tile.Height);
        if (count > 0)
            _tile = new Size(cell, MeasureOne(children, generator, children.Count > 0 ? IndexOf(generator, 0) : FirstIndex(count)).Height);

        var rows = (count + _perRow - 1) / _perRow;
        UpdateScrollInfo(new Size(width, height), new Size(width, rows * _tile.Height));

        // Realize visible rows plus one buffer row on each side.
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

                    child.Measure(new Size(cell, double.PositiveInfinity));
                }
            }
        }

        // Virtualize tiles now outside the realized range.
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

    /// <summary>
    /// Column count and cell width for a panel width: as many requested-width tiles as fit (at least two from
    /// <see cref="TwoColumnsFrom"/>), sharing the width equally.
    /// </summary>
    public static (int Columns, double Cell) Columns(double width, double tileWidth)
    {
        var columns = Math.Max((int)(width / Math.Max(tileWidth, 1)), 1);
        if (width >= TwoColumnsFrom)
            columns = Math.Max(columns, 2);
        return (columns, width / columns);
    }

    // Realizes (if needed) and measures one tile to determine the uniform tile size.
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

            child.Measure(new Size(_tile.Width, double.PositiveInfinity));
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

    /// <summary>Scrolls a realized tile (e.g. the keyboard's next target) fully into view.</summary>
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

    /// <summary>Scrolls a possibly unrealized tile into view and realizes it (ScrollIntoView, keyboard beyond realized rows).</summary>
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
