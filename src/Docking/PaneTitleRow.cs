using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandPegApp.Docking;

/// <summary>
/// A pane's title row: its tabs (the <see cref="PaneTitleSlot"/>) at the left, the pane's own controls against the right
/// edge, in the order written. Short of room, the row gives up room in this order: the controls marked
/// <see cref="OptionalProperty"/> (a label whose control says enough by its tooltip) leave, those marked
/// <see cref="CanCompactProperty"/> take their compact form (<see cref="IsCompactProperty"/>, which their styles answer:
/// shorter labels), the tabs give up room down to the one shown (their strip lists the others in its menu), the
/// controls with a MinWidth narrow down to it, and then what still does not fit leaves, from the right. The tab shown is
/// never cut, nothing is ever placed left of it, and nothing is drawn outside the row (it clips), so a narrow pane
/// can never have its controls lie over the pane beside it. A control that can be compact is never given less than
/// its width in the form it shows: it takes the compact form, or leaves, instead of being cut.
///
/// A row may also hold a control marked <see cref="IsTrailingProperty"/> (the window's header, joined to the pane under
/// it by the dock manager): it stays whole at the right-hand end. The pane's own controls sit between the tabs and it
/// while they all fit as they are; otherwise they go onto a second line under it, against the right edge, with the
/// whole width to give up room in.
/// </summary>
public sealed class PaneTitleRow : Panel
{
    /// <summary>Left out of the row first when it is short of room.</summary>
    public static readonly DependencyProperty OptionalProperty = DependencyProperty.RegisterAttached(
        "Optional", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetOptional(UIElement element) => (bool)element.GetValue(OptionalProperty);

    public static void SetOptional(UIElement element, bool value) => element.SetValue(OptionalProperty, value);

    /// <summary>Has a compact form, which the row asks for (through <see cref="IsCompactProperty"/>) before it leaves anything else out.</summary>
    public static readonly DependencyProperty CanCompactProperty = DependencyProperty.RegisterAttached(
        "CanCompact", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetCanCompact(UIElement element) => (bool)element.GetValue(CanCompactProperty);

    public static void SetCanCompact(UIElement element, bool value) => element.SetValue(CanCompactProperty, value);

    /// <summary>
    /// Set by the row on a control that can be compact, and inherited by what is in it: whether it is to show its
    /// compact form now. Its styles answer it (a trigger on it, shorter labels); nothing else should set it.
    /// </summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);

    /// <summary>Kept whole at the row's right-hand end, with the pane's own controls moved under it when they do not fit beside it.</summary>
    public static readonly DependencyProperty IsTrailingProperty = DependencyProperty.RegisterAttached(
        "IsTrailing", typeof(bool), typeof(PaneTitleRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsTrailing(UIElement element) => (bool)element.GetValue(IsTrailingProperty);

    public static void SetIsTrailing(UIElement element, bool value) => element.SetValue(IsTrailingProperty, value);

    // The room between the first line and the second, when the controls are under a trailing control.
    private const double LineGap = 4;

    public PaneTitleRow() => ClipToBounds = true;

    // As of the last measure: the slot's width, the room each control was given (0 for one left out), the trailing
    // control's width, and the lines' heights (no second line: 0).
    private double _slotWidth, _trailingWidth, _firstLine, _secondLine;
    private UIElement? _trailing;
    private readonly Dictionary<UIElement, double> _given = [];

    // The widths of the controls that can be compact, in each form, as last measured in it: the row decides from them
    // without measuring a control in the form it is not in on every pass (which would change it there and back).
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

        // Each control that can be compact has its width in both forms known before anything is decided: the form it
        // does not show is measured once (the first pass it is in this row), then it goes back.
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

        // With a trailing control: the controls stay on the first line only while every one of them fits there as it is.
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
    /// Gives the controls their room on a line of a width (beside tabs of these widths, or none): fills <see cref="_given"/>
    /// and returns the line's height. Short of room: labels leave, compact forms are taken, MinWidth controls narrow,
    /// and what still does not fit leaves from the right.
    /// </summary>
    private double Fit(List<UIElement> extras, double width, double slotFull, double slotLeast, double height)
    {
        // Each control's width in the form it would have, and the least it can be narrowed to.
        var compact = false;
        double Full(UIElement c) => WidthIn(c, compact);
        double Least(UIElement c) => c is FrameworkElement { MinWidth: > 0 } f && !GetCanCompact(c) ? Math.Min(f.MinWidth + Margins(f), Full(c)) : Full(c);
        double Need(List<UIElement> list, bool narrowed) => list.Sum(c => narrowed ? Least(c) : Full(c));

        // The labels go first, then the compact forms are taken: whichever first leaves the tabs whole.
        var kept = extras.ToList();
        foreach (var optional in extras.Where(GetOptional).Reverse())
        {
            if (slotFull + Need(kept, false) <= width)
                break;
            kept.Remove(optional);
        }

        if (slotFull + Need(kept, false) > width && kept.Any(GetCanCompact))
            compact = true;

        // Whatever still does not fit beside the tab shown, narrowed as far as it goes, leaves: from the right.
        while (kept.Count > 0 && slotLeast + Need(kept, true) > width)
            kept.RemoveAt(kept.Count - 1);

        foreach (var child in kept.Where(c => GetCanCompact(c) && GetIsCompact(c) != compact))
            MeasureAs(child, compact, height);

        // The tabs give up their room first (down to the one shown), then the controls with a MinWidth narrow. A control
        // that can be compact is never narrowed: it has the width of the form it shows.
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

        // Against the right edge (or the trailing control), and never over the tabs on their line: what is wider than
        // the row (arranged at another width than it was measured for) is cut at the right by the row's clip instead.
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

    /// <summary>A control's width in a form: the one known for that form, for a control that can be compact.</summary>
    private double WidthIn(UIElement child, bool compact) => !GetCanCompact(child) ? child.DesiredSize.Width
        : (compact ? _compactWidth : _wideWidth).TryGetValue(child, out var known) ? known : child.DesiredSize.Width;

    /// <summary>Puts a control that can be compact into one form or the other, and measures it there.</summary>
    private void MeasureAs(UIElement child, bool compact, double height)
    {
        SetIsCompact(child, compact);

        // The form changes what is deep inside the control (its labels), and that marks only those for measuring: the
        // elements between them and the control would answer with their size in the other form. All of them are marked.
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
