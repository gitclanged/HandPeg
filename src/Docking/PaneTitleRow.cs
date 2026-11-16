using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandPegApp.Docking;

/// <summary>
/// Pane title row: tabs (<see cref="PaneTitleSlot"/>) left, controls right-aligned in declaration order. Overflow shedding order:
/// <see cref="OptionalProperty"/> controls hide, <see cref="CanCompactProperty"/> controls switch to compact form
/// (<see cref="IsCompactProperty"/>, styled with shorter labels), tabs shrink to the active one (others via the strip menu), MinWidth
/// controls narrow, then remaining overflow hides from the right. The active tab is never cut or overlapped and the row clips, so
/// controls never spill onto a neighbouring pane. Compactable controls are never truncated: they compact or hide.
///
/// An <see cref="IsTrailingProperty"/> control (window header joined by the dock manager) stays whole at the right end; other controls
/// sit between tabs and it while all fit, else wrap to a right-aligned second line with the full width.
/// </summary>
public sealed class PaneTitleRow : Panel
{
    /// <summary>First control hidden on overflow.</summary>
    public static readonly DependencyProperty OptionalProperty = DependencyProperty.RegisterAttached(
        "Optional", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetOptional(UIElement element) => (bool)element.GetValue(OptionalProperty);

    public static void SetOptional(UIElement element, bool value) => element.SetValue(OptionalProperty, value);

    /// <summary>Has a compact form, requested via <see cref="IsCompactProperty"/> before other controls are hidden.</summary>
    public static readonly DependencyProperty CanCompactProperty = DependencyProperty.RegisterAttached(
        "CanCompact", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetCanCompact(UIElement element) => (bool)element.GetValue(CanCompactProperty);

    public static void SetCanCompact(UIElement element, bool value) => element.SetValue(CanCompactProperty, value);

    /// <summary>
    /// Inherited flag set only by the row on compactable controls; styles trigger on it (shorter labels).
    /// </summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);

    /// <summary>Pinned whole at the right end; other controls wrap below it when they do not fit beside it.</summary>
    public static readonly DependencyProperty IsTrailingProperty = DependencyProperty.RegisterAttached(
        "IsTrailing", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsTrailing(UIElement element) => (bool)element.GetValue(IsTrailingProperty);

    public static void SetIsTrailing(UIElement element, bool value) => element.SetValue(IsTrailingProperty, value);

    // Gap between lines when controls wrap below a trailing control.
    private const double LineGap = 4;

    public PaneTitleRow() => ClipToBounds = true;

    // Last measure results: slot width, per-control allotted width (0 = hidden), trailing width, line heights (0 = no second line).
    private double _slotWidth, _trailingWidth, _firstLine, _secondLine;
    private UIElement? _trailing;
    private readonly Dictionary<UIElement, double> _given = [];

    // Cached widths of compactable controls in each form, so layout avoids toggling forms every pass just to measure.
    private readonly Dictionary<UIElement, double> _wideWidth = [], _compactWidth = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        _given.Clear();
        var children = InternalChildren.Cast<UIElement>().ToList();
        var slot = children.OfType<PaneTitleSlot>().FirstOrDefault();
        _trailing = children.FirstOrDefault(c => GetIsTrailing(c) && c.Visibility != Visibility.Collapsed);
        var extras = children.Where(c => !ReferenceEquals(c, slot) && !ReferenceEquals(c, _trailing) && c.Visibility != Visibility.Collapsed).ToList();

        foreach (var child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            Remember(child);
        }

        // Ensure both form widths are known before deciding: measure the hidden form once on first pass, then revert.
        foreach (var child in extras.Where(GetCanCompact))
        {
            var shows = GetIsCompact(child);
            if ((shows ? _wideWidth : _compactWidth).ContainsKey(child))
                continue;
            MeasureAs(child, !shows, availableSize.Height);
            MeasureAs(child, shows, availableSize.Height);
        }

        var slotFull = slot?.DesiredSize.Width ?? 0;
        var slotLeast = slot?.Content is OverflowStrip strip ? Math.Min(strip.MinimumWidth + Margins(slot), slotFull) : slotFull;
        var width = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        _trailingWidth = _trailing?.DesiredSize.Width ?? 0;
        var slotHeight = slot?.DesiredSize.Height ?? 0;
        var trailingHeight = _trailing?.DesiredSize.Height ?? 0;

        // With a trailing control, keep controls on line 1 only if all fit at full size.
        var oneLine = _trailing is null || slotFull + extras.Sum(c => WidthIn(c, compact: false)) + _trailingWidth <= width;
        if (oneLine)
        {
            var controls = Fit(extras, Math.Max(width - _trailingWidth, 0), slotFull, slotLeast, availableSize.Height);
            var used = _given.Values.Sum();
            _slotWidth = slot is null ? 0 : Math.Max(Math.Min(slotFull, width - _trailingWidth - used), 0);
            (_firstLine, _secondLine) = (Math.Max(Math.Max(slotHeight, trailingHeight), controls), 0);
        }
        else
        {
            _slotWidth = slot is null ? 0 : Math.Max(Math.Min(slotFull, width - _trailingWidth), 0);
            _secondLine = Fit(extras, width, 0, 0, availableSize.Height);
            _firstLine = Math.Max(slotHeight, trailingHeight);
        }

        slot?.Measure(new Size(_slotWidth, availableSize.Height));
        var height = _firstLine + (_secondLine > 0 ? LineGap + _secondLine : 0);
        var wanted = _slotWidth + _given.Values.Sum() + _trailingWidth;
        return new Size(double.IsInfinity(availableSize.Width) ? wanted : availableSize.Width, height);
    }

    /// <summary>
    /// Allocates control widths on a line (beside the given tab widths, if any) into <see cref="_given"/> and returns the line
    /// height. Overflow: hide optional labels, compact, narrow MinWidth controls, then hide from the right.
    /// </summary>
    private double Fit(List<UIElement> extras, double width, double slotFull, double slotLeast, double height)
    {
        // Per-control width in its target form and its minimum narrowed width.
        var compact = false;
        double Full(UIElement c) => WidthIn(c, compact);
        double Least(UIElement c) => c is FrameworkElement { MinWidth: > 0 } f && !GetCanCompact(c) ? Math.Min(f.MinWidth + Margins(f), Full(c)) : Full(c);
        double Need(List<UIElement> list, bool narrowed) => list.Sum(c => narrowed ? Least(c) : Full(c));

        // Hide optional labels, then compact, stopping as soon as the tabs fit whole.
        var kept = extras.ToList();
        foreach (var optional in extras.Where(GetOptional).Reverse())
        {
            if (slotFull + Need(kept, false) <= width)
                break;
            kept.Remove(optional);
        }

        if (slotFull + Need(kept, false) > width && kept.Any(GetCanCompact))
            compact = true;

        // Anything still overflowing beside the active tab after max narrowing hides, from the right.
        while (kept.Count > 0 && slotLeast + Need(kept, true) > width)
            kept.RemoveAt(kept.Count - 1);

        foreach (var child in kept.Where(c => GetCanCompact(c) && GetIsCompact(c) != compact))
            MeasureAs(child, compact, height);

        // Tabs shrink first (to the active one), then MinWidth controls narrow. Compactable controls never narrow below their
        // current form's width.
        var full = Need(kept, false);
        var narrowed = slotFull + full > width;
        var room = narrowed ? Math.Max(Math.Min(full, width - slotLeast), Need(kept, true)) : full;
        var extra = Math.Max(room - Need(kept, true), 0);
        var shrinkable = kept.Where(c => c is FrameworkElement { MinWidth: > 0 } && !GetCanCompact(c)).ToList();
        var line = 0.0;
        foreach (var child in kept)
        {
            var given = narrowed && shrinkable.Contains(child) ? Math.Min(child.DesiredSize.Width, Least(child) + extra / shrinkable.Count) : Math.Max(child.DesiredSize.Width, Full(child));
            _given[child] = given;
            child.Measure(new Size(given, height));
            line = Math.Max(line, child.DesiredSize.Height);
        }

        return line;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var used = _given.Values.Sum();
        var twoLines = _secondLine > 0;
        var lineTop = twoLines ? _firstLine + LineGap : 0;
        var lineHeight = twoLines ? _secondLine : finalSize.Height;
        var lineEnd = twoLines ? finalSize.Width : finalSize.Width - _trailingWidth;

        // Right-align against the edge (or trailing control), never over tabs; content wider than the row (arranged at a different
        // width than measured) is clipped on the right.
        var x = Math.Max(lineEnd - used, twoLines ? 0 : _slotWidth);
        foreach (var child in InternalChildren.Cast<UIElement>())
        {
            if (child is PaneTitleSlot slot)
            {
                slot.Arrange(new Rect(0, 0, _slotWidth, twoLines ? _firstLine : finalSize.Height));
            }
            else if (ReferenceEquals(child, _trailing))
            {
                child.Arrange(new Rect(Math.Max(finalSize.Width - _trailingWidth, 0), 0, _trailingWidth, twoLines ? _firstLine : finalSize.Height));
            }
            else if (_given.TryGetValue(child, out var given))
            {
                child.Arrange(new Rect(x, lineTop, given, lineHeight));
                x += given;
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }

    /// <summary>Cached width of a compactable control in the given form.</summary>
    private double WidthIn(UIElement child, bool compact) => !GetCanCompact(child) ? child.DesiredSize.Width
        : (compact ? _compactWidth : _wideWidth).TryGetValue(child, out var known) ? known : child.DesiredSize.Width;

    /// <summary>Switches a compactable control's form and measures it.</summary>
    private void MeasureAs(UIElement child, bool compact, double height)
    {
        SetIsCompact(child, compact);

        // A form change only invalidates the inner labels; intermediate ancestors would report stale sizes, so invalidate the
        // whole chain.
        InvalidateTree(child);
        child.Measure(new Size(double.PositiveInfinity, height));
        Remember(child);
    }

    private static void InvalidateTree(DependencyObject element)
    {
        if (element is UIElement ui)
            ui.InvalidateMeasure();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            InvalidateTree(VisualTreeHelper.GetChild(element, i));
    }

    private void Remember(UIElement child)
    {
        if (GetCanCompact(child))
            (GetIsCompact(child) ? _compactWidth : _wideWidth)[child] = child.DesiredSize.Width;
    }

    private static double Margins(FrameworkElement element) => element.Margin.Left + element.Margin.Right;
}
