using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandPegApp;

/// <summary>Icon kind of an <see cref="ExpandablePillSlider"/> (vector strokes drawn in OnRender).</summary>
public enum PillIcon
{
    /// <summary>Speaker with level-dependent waves; crossed out when muted or at minimum.</summary>
    Speaker,
    /// <summary>Magnifier.</summary>
    Magnifier,
    /// <summary>
    /// Text face: the readout replaces the icon (e.g. speed "1x") so the value is visible while collapsed; the open pill has no separate
    /// readout. Set a <see cref="ExpandablePillSlider.CollapsedWidth"/> that fits the widest text.
    /// </summary>
    Readout,
}

/// <summary>
/// Icon-only slider that expands on hover into a pill (track, thumb, readout) drawn over its neighbours without moving them (layout
/// size is always the collapsed slot). Used for Monitor Volume, Playback Speed (text face, <see cref="Detent"/> snapping to seven speeds)
/// and Timeline Zoom. Rendered in OnRender with no child visuals; open-state resources are created lazily.
/// <para>
/// Opens on hover, keyboard focus (Tab) or drag in 140 ms; closes 250 ms after pointer leave (2.5 s after last touch). Track press jumps
/// and drags; Shift drags at 1/10 speed; wheel steps <see cref="SmallChange"/>; double-click restores <see cref="DefaultValue"/>; icon click
/// toggles <see cref="IsIconChecked"/> (mute). While focused it owns arrows, Page Up/Down, Home, End and Enter (IOwnsKeys). Theme brushes
/// are read at render and it redraws on theme change; fixed-surface hosts (fullscreen bar) set <see cref="IconBrush"/> and related brushes.
/// </para>
/// </summary>
public sealed class ExpandablePillSlider : FrameworkElement, IOwnsKeys
{
    /// <summary>Open/close animation duration in ms (ease-out).</summary>
    internal const double OpenMs = 140;

    /// <summary>Close delay after pointer leave in ms (<see cref="TouchCloseMs"/> after the last touch).</summary>
    internal const double CloseDelayMs = 250, TouchCloseMs = 2500;

    /// <summary>Shift-drag speed factor relative to a normal drag.</summary>
    internal const double FineFactor = 0.1;

    // Open pill metrics (DIPs): thumb size, track thickness, gaps icon-to-thumb stop, track-to-readout and after readout, and min readout width.
    private const double ThumbRadius = 5, TrackThickness = 3, IconRoom = 3, ReadoutGap = 6, EndPad = 9, LeastReadout = 26, ReadoutSize = 11;
    private const double IconBox = 16, IconStroke = 1.3;

    /// <summary>
    /// Text face metrics: font size (bar text, 12 DIPs), min horizontal padding in the slot, and min shrink size for text too wide for the
    /// 24 x 22 slot (e.g. "0.25x").
    /// </summary>
    internal const double FaceFontSize = 12, FacePad = 2, FaceLeastSize = 7;

    // Detent dot size (slightly wider than the track) and the max number of detents drawn.
    private const double DetentRadius = 2;
    private const int MostDetentsShown = 32;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(double.NaN));

    public static readonly DependencyProperty SmallChangeProperty = DependencyProperty.Register(nameof(SmallChange), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.1));

    public static readonly DependencyProperty LargeChangeProperty = DependencyProperty.Register(nameof(LargeChange), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(1.0));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(PillIcon), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(PillIcon.Speaker, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIconToggleProperty = DependencyProperty.Register(nameof(IsIconToggle), typeof(bool), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsIconCheckedProperty = DependencyProperty.Register(nameof(IsIconChecked), typeof(bool), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnIconCheckedChanged));

    public static readonly DependencyProperty IconAutomationNameProperty = DependencyProperty.Register(nameof(IconAutomationName), typeof(string), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty ReadoutFormatProperty = DependencyProperty.Register(nameof(ReadoutFormat), typeof(string), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata("{0:0}", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackLengthProperty = DependencyProperty.Register(nameof(TrackLength), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(96.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ExpandsLeftProperty = DependencyProperty.Register(nameof(ExpandsLeft), typeof(bool), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(nameof(SurfaceBrush), typeof(Brush), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EdgeBrushProperty = DependencyProperty.Register(nameof(EdgeBrush), typeof(Brush), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CollapsedWidthProperty = DependencyProperty.Register(nameof(CollapsedWidth), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DetentProperty = DependencyProperty.Register(nameof(Detent), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DetentLabelsProperty = DependencyProperty.Register(nameof(DetentLabels), typeof(IReadOnlyList<string>), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackInsetProperty = DependencyProperty.Register(nameof(TrackInset), typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(nameof(ValueChanged), RoutingStrategy.Bubble,
        typeof(RoutedPropertyChangedEventHandler<double>), typeof(ExpandablePillSlider));

    public static readonly RoutedEvent IconClickEvent = EventManager.RegisterRoutedEvent(nameof(IconClick), RoutingStrategy.Bubble,
        typeof(RoutedEventHandler), typeof(ExpandablePillSlider));

    // Animated openness 0 (icon only) to 1 (open); rendering and hit-testing use the current value.
    private static readonly DependencyProperty OpenProperty = DependencyProperty.Register("Open", typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Open/close animations and easing: created on first open, frozen, shared by all instances.
    private static CubicEase? _ease;
    private static DoubleAnimation? _opening, _closing;

    // Open/opening flag; delayed-close timer; last-input-was-touch; opened-by-keyboard (Tab or a key while focused: stays open while
    // focused); and the hover-dwell open timer used when the slot lies under a neighbour's open pill.
    private bool _wantOpen, _touch, _keyOpened;
    private DispatcherTimer? _closeTimer, _openTimer;

    // Track drag state (last pointer; Shift-drag origin pointer and value) and a pending icon press.
    private bool _dragging, _fine, _iconPressed;
    private double _lastX;
    private (double X, double Value) _fineFrom;
    private int _wheel;

    // Lazily created render resources: icon geometries (by kind and waves) and pen; open-pill border, focus ring and readout (the widest
    // readout since opening is kept so the pill does not jitter as the value changes).
    private readonly StreamGeometry?[] _icons = new StreamGeometry?[5];
    private Size _iconsFor;
    private Pen? _iconPen, _edgePen, _focusPen;
    private FormattedText? _readout;
    private string? _readoutText;
    private Brush? _readoutBrush;
    private double _readoutWidth;

    // Cached text-face FormattedText, rebuilt only when text, brush or DPI scale changes.
    private FormattedText? _face;
    private string? _faceText;
    private Brush? _faceBrush;
    private double _faceRoom;

    public ExpandablePillSlider()
    {
        Height = 22;
        Focusable = true;
        FocusVisualStyle = null;

        // Theme changes swap the brushes read during render without notification: redraw on theme apply and on reattach to a window.
        Loaded += (_, _) =>
        {
            Services.ThemeManager.Applied -= ThemeApplied;
            Services.ThemeManager.Applied += ThemeApplied;
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            Services.ThemeManager.Applied -= ThemeApplied;
            WatchClickOff(false);
        };
    }

    // Pens and readout are cached per brush, so a redraw is sufficient after a theme change.
    private void ThemeApplied() => InvalidateVisual();

    /// <summary>Value in [<see cref="Minimum"/>, <see cref="Maximum"/>] (binds two-way by default).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Double-click reset value (NaN disables reset).</summary>
    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    /// <summary>Step per wheel notch or arrow key.</summary>
    public double SmallChange
    {
        get => (double)GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    /// <summary>Step per Page Up / Page Down.</summary>
    public double LargeChange
    {
        get => (double)GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public PillIcon Icon
    {
        get => (PillIcon)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Icon click toggles <see cref="IsIconChecked"/> (speaker mute).</summary>
    public bool IsIconToggle
    {
        get => (bool)GetValue(IsIconToggleProperty);
        set => SetValue(IsIconToggleProperty, value);
    }

    /// <summary>Icon toggle state (two-way by default); muted for the speaker.</summary>
    public bool IsIconChecked
    {
        get => (bool)GetValue(IsIconCheckedProperty);
        set => SetValue(IsIconCheckedProperty, value);
    }

    /// <summary>Automation name of the icon toggle ("Mute Player").</summary>
    public string IconAutomationName
    {
        get => (string)GetValue(IconAutomationNameProperty);
        set => SetValue(IconAutomationNameProperty, value);
    }

    /// <summary>Readout format string ("{0:0}%"); <see cref="ReadoutFor"/> overrides when set.</summary>
    public string ReadoutFormat
    {
        get => (string)GetValue(ReadoutFormatProperty);
        set => SetValue(ReadoutFormatProperty, value);
    }

    /// <summary>Open track length in DIPs.</summary>
    public double TrackLength
    {
        get => (double)GetValue(TrackLengthProperty);
        set => SetValue(TrackLengthProperty, value);
    }

    /// <summary>Expand leftward from the fixed icon instead of rightward.</summary>
    public bool ExpandsLeft
    {
        get => (bool)GetValue(ExpandsLeftProperty);
        set => SetValue(ExpandsLeftProperty, value);
    }

    /// <summary>
    /// Icon/readout brush override (null = theme text). Used with <see cref="SurfaceBrush"/>, <see cref="EdgeBrush"/> and
    /// <see cref="TrackBrush"/> on theme-independent surfaces (fullscreen bar).
    /// </summary>
    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    /// <summary>Icon well and open-pill fill override (null = theme hover fill).</summary>
    public Brush? SurfaceBrush
    {
        get => (Brush?)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>Open-pill border override (null = theme control border).</summary>
    public Brush? EdgeBrush
    {
        get => (Brush?)GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    /// <summary>Track groove override (null = theme muted text).</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>
    /// Collapsed (layout) width in DIPs; NaN = icon square (height + 2). A <see cref="PillIcon.Readout"/> face keeps a fixed width for all
    /// values so neighbours never shift; text wider than the slot is drawn smaller.
    /// </summary>
    public double CollapsedWidth
    {
        get => (double)GetValue(CollapsedWidthProperty);
        set => SetValue(CollapsedWidthProperty, value);
    }

    /// <summary>
    /// Detent spacing from <see cref="Minimum"/>; user-set values snap to detents (drawn as dots on the track). 0 = continuous.
    /// </summary>
    public double Detent
    {
        get => (double)GetValue(DetentProperty);
        set => SetValue(DetentProperty, value);
    }

    /// <summary>Readout labels per detent from the minimum (e.g. "0.25x".."2x"); null uses <see cref="ReadoutFormat"/>.</summary>
    public IReadOnlyList<string>? DetentLabels
    {
        get => (IReadOnlyList<string>?)GetValue(DetentLabelsProperty);
        set => SetValue(DetentLabelsProperty, value);
    }

    /// <summary>
    /// Gap in DIPs between the slot and the track's near end, kept clear over a neighbour's slot that the open pill covers (Volume beside
    /// Playback Speed): no detent, thumb or track hit lies there. 0 = none.
    /// </summary>
    public double TrackInset
    {
        get => (double)GetValue(TrackInsetProperty);
        set => SetValue(TrackInsetProperty, value);
    }

    /// <summary>Custom readout text for a value, queried only while open or for a text face (e.g. Timeline "2.4x", not the raw value).</summary>
    public Func<double, string>? ReadoutFor { get; set; }

    /// <summary>True while open or opening (false while closing).</summary>
    public bool IsExpanded => _wantOpen;

    public event RoutedPropertyChangedEventHandler<double> ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    /// <summary>Raised on icon click (after <see cref="IsIconChecked"/> toggles, if enabled).</summary>
    public event RoutedEventHandler IconClick
    {
        add => AddHandler(IconClickEvent, value);
        remove => RemoveHandler(IconClickEvent, value);
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var pill = (ExpandablePillSlider)sender;
        var (from, to) = ((double)e.OldValue, (double)e.NewValue);
        pill.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(from, to, ValueChangedEvent));
        if (UIElementAutomationPeer.FromElement(pill) is not AutomationPeer peer)
            return;

        peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, from, to);

        // With detent labels, expose them so screen readers announce "1.5x" rather than the step index.
        if (pill.Worded)
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, pill.ReadoutTextFor(from), pill.ReadoutTextFor(to));
    }

    private static void OnIconCheckedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (UIElementAutomationPeer.FromElement((ExpandablePillSlider)sender) is PillPeer { Icon: { } icon })
            icon.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, Toggled((bool)e.OldValue), Toggled((bool)e.NewValue));
    }

    private static ToggleState Toggled(bool on) => on ? ToggleState.On : ToggleState.Off;

    // ----- Layout: always the collapsed slot (icon square or CollapsedWidth); the open pill renders and hit-tests beyond it -----

    private Size Slot()
    {
        var height = RenderSize.Height > 0 ? RenderSize.Height : double.IsNaN(Height) ? 22 : Height;
        return new Size(RenderSize.Width > 0 ? RenderSize.Width : CollapsedWidthFor(height), height);
    }

    // Collapsed width: CollapsedWidth if set, else the icon square.
    private double CollapsedWidthFor(double height) => CollapsedWidth is > 0 and var width && !double.IsInfinity(width) ? width : height + 2;

    // Layout size is always the collapsed size.
    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsNaN(Height) ? 22 : Height;
        return new Size(CollapsedWidthFor(height), height);
    }

    // No layout clip: the open pill draws past its slot.
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;

    // Pill geometry at a given openness: pill rect, track ends (thumb centre at min and max) and readout rect. The icon stays in the slot
    // and the pill grows left or right from it. A text face is the readout (no extra readout room). TrackInset moves the track further
    // from the slot.
    private readonly record struct Parts(Rect Pill, double TrackFrom, double TrackTo, Rect Readout);

    private Parts Lay(double open)
    {
        var slot = Slot();
        var face = Icon == PillIcon.Readout;
        var (length, readout) = (Math.Max(TrackLength, 16), face ? 0 : Math.Max(_readoutWidth, LeastReadout));
        var near = IconRoom + (TrackInset is > 0 and var inset && !double.IsInfinity(inset) ? inset : 0);
        var reach = (near + ThumbRadius + length + ThumbRadius + (face ? 0 : ReadoutGap + readout) + EndPad) * Math.Max(0, Math.Min(open, 1));
        if (ExpandsLeft)
        {
            var to = -(near + ThumbRadius);
            var from = to - length;
            return new Parts(new Rect(-reach, 0, slot.Width + reach, slot.Height), from, to,
                new Rect(from - ThumbRadius - ReadoutGap - readout, 0, readout, slot.Height));
        }

        var start = slot.Width + near + ThumbRadius;
        return new Parts(new Rect(0, 0, slot.Width + reach, slot.Height), start, start + length,
            new Rect(start + length + ThumbRadius + ReadoutGap, 0, readout, slot.Height));
    }

    /// <summary>Test hook: open-track ends in control coordinates (thumb centre at min and max).</summary>
    internal (double From, double To) TrackSpan
    {
        get
        {
            var parts = Lay(1);
            return (parts.TrackFrom, parts.TrackTo);
        }
    }

    private double OpenNow => (double)GetValue(OpenProperty);

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var open = OpenNow;
        var area = open > 0 ? Lay(open).Pill : new Rect(Slot());
        return area.Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
    }

    // ----- Open and close -----

    /// <summary>Openness <paramref name="ms"/> after opening began: cubic ease-out from 0 to 1 over <see cref="OpenMs"/>.</summary>
    internal static double OpenFraction(double ms) => Ease.Ease(Math.Max(0, Math.Min(ms / OpenMs, 1)));

    private static CubicEase Ease
    {
        get
        {
            if (_ease is null)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                ease.Freeze();
                _ease = ease;
            }

            return _ease;
        }
    }

    private static DoubleAnimation Animation(double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(OpenMs)) { EasingFunction = Ease };
        animation.Freeze();
        return animation;
    }

    private void Expand()
    {
        _closeTimer?.Stop();
        _openTimer?.Stop();
        if (_wantOpen)
            return;

        _wantOpen = true;
        _readoutWidth = 0;
        Animate(open: true);
        WatchClickOff(true);

        // Close an open neighbour the pointer has left immediately (not after CloseDelayMs) so adjacent pills never overlap open.
        foreach (var other in Neighbours())
        {
            if (other._wantOpen && !other.Held)
                other.Collapse();
        }
    }

    // Sibling pills in the same panel (e.g. Volume and Playback Speed).
    private IEnumerable<ExpandablePillSlider> Neighbours() => VisualTreeHelper.GetParent(this) is System.Windows.Controls.Panel panel
        ? panel.Children.OfType<ExpandablePillSlider>().Where(other => !ReferenceEquals(other, this))
        : [];

    /// <summary>
    /// True when this slot lies under a neighbour's open pill: hover then opens only after a dwell (<see cref="OpensLater"/>), so crossing
    /// it toward that pill's track opens nothing.
    /// </summary>
    internal bool UnderOpenNeighbour()
    {
        var slot = new Rect(Slot());
        foreach (var other in Neighbours())
        {
            if (!other._wantOpen || other.Visibility != Visibility.Visible)
                continue;

            var over = Rect.Intersect(other.TransformToVisual(this).TransformBounds(other.Lay(1).Pill), slot);
            if (!over.IsEmpty && over.Width > 0.5)
                return true;
        }

        return false;
    }

    /// <summary>Test hook: true when a dwell-delayed open (CloseDelayMs) is pending.</summary>
    internal bool OpensLater => _openTimer?.IsEnabled == true;

    private void OpenLater()
    {
        if (_openTimer is null)
        {
            _openTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(CloseDelayMs) };
            _openTimer.Tick += (_, _) =>
            {
                _openTimer.Stop();
                if (IsMouseOver && !_touch)
                    Expand();
            };
        }

        _openTimer.Stop();
        _openTimer.Start();
    }

    private void Collapse()
    {
        _closeTimer?.Stop();
        if (!_wantOpen)
            return;

        _wantOpen = false;
        WatchClickOff(false);
        Animate(open: false);
    }

    // ----- Click-away close -----
    //
    // Any press elsewhere in the window (mouse, pen, touch) closes the open pill immediately, so covered controls are one click away.
    // Hooked only while open, as a tunnelling handler at the visual root (controls handling their own press cannot hide it); the press
    // continues to its target.

    private UIElement? _clickOffRoot;

    private void WatchClickOff(bool watch)
    {
        if (_clickOffRoot is { } watched)
        {
            watched.PreviewMouseDown -= ClickedOff;
            watched.PreviewTouchDown -= TouchedOff;
            _clickOffRoot = null;
        }

        if (!watch)
            return;

        DependencyObject node = this;
        while (VisualTreeHelper.GetParent(node) is { } parent)
            node = parent;
        if (node is UIElement root && !ReferenceEquals(root, this))
        {
            root.PreviewMouseDown += ClickedOff;
            root.PreviewTouchDown += TouchedOff;
            _clickOffRoot = root;
        }
    }

    private void ClickedOff(object sender, MouseButtonEventArgs e) => PressedAt(e.OriginalSource);

    private void TouchedOff(object? sender, TouchEventArgs e) => PressedAt(e.OriginalSource);

    // Presses on the pill itself (slot or drawn extension; it has no children) are ignored.
    private void PressedAt(object? source)
    {
        if (ReferenceEquals(source, this) || !_wantOpen)
            return;
        _keyOpened = false;
        Collapse();
    }

    private void Animate(bool open)
    {
        // Windows client-area animations off: snap open/closed.
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetOpenNow(open);
            return;
        }

        BeginAnimation(OpenProperty, open ? _opening ??= Animation(1) : _closing ??= Animation(0));
    }

    private void SetOpenNow(bool open)
    {
        BeginAnimation(OpenProperty, null);
        SetValue(OpenProperty, open ? 1.0 : 0.0);
    }

    /// <summary>Test hook: opens or closes immediately without animation (tests have no pointer).</summary>
    internal void OpenAtOnce(bool open)
    {
        _closeTimer?.Stop();
        _openTimer?.Stop();
        (_wantOpen, _readoutWidth) = (open, open ? 0 : _readoutWidth);
        WatchClickOff(open);
        SetOpenNow(open);
    }

    // Stay open while hovered (not after touch: a lifted finger is gone), captured, or keyboard-opened and still focused.
    private bool Held => (IsMouseOver && !_touch) || IsMouseCaptured || (IsKeyboardFocused && _keyOpened);

    private void CloseLater()
    {
        if (!_wantOpen || Held)
            return;

        if (_closeTimer is null)
        {
            _closeTimer = new DispatcherTimer(DispatcherPriority.Input);
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer.Stop();
                if (!Held)
                    Collapse();
            };
        }

        _closeTimer.Stop();
        _closeTimer.Interval = TimeSpan.FromMilliseconds(_touch ? TouchCloseMs : CloseDelayMs);
        _closeTimer.Start();
    }

    // ----- Value changes -----

    // User-set value: bounded to [Minimum, Maximum] without Math.Clamp (bindings may set Maximum before Minimum), then snapped to the
    // nearest detent if any.
    private void Set(double value)
    {
        var kept = Snapped(Math.Max(Minimum, Math.Min(value, Maximum)));
        if (!double.IsNaN(kept) && kept != Value)
            Value = kept;
    }

    // Snaps an in-range value to the nearest detent, never past Maximum when the range is not a whole number of detents; identity
    // without detents.
    private double Snapped(double value)
    {
        var detent = Detent;
        if (!(detent > 0) || double.IsInfinity(detent) || double.IsNaN(value))
            return value;

        var on = Minimum + Math.Round((value - Minimum) / detent, MidpointRounding.AwayFromZero) * detent;
        return on > Maximum ? Math.Max(Minimum, on - detent) : on;
    }

    // Visible detent count from Minimum every Detent up to Maximum; 0 if none or too dense to distinguish.
    private int Detents()
    {
        var (detent, range) = (Detent, Maximum - Minimum);
        if (!(detent > 0) || !(range > 0))
            return 0;

        var count = Math.Floor(range / detent + 1e-9) + 1;
        return count is >= 2 and <= MostDetentsShown ? (int)count : 0;
    }

    private double Fraction()
    {
        var range = Maximum - Minimum;
        return range > 0 ? Math.Max(0, Math.Min((Value - Minimum) / range, 1)) : 0;
    }

    /// <summary>
    /// Track drag to <paramref name="x"/> (control coordinates): absolute, or relative at 1/10 speed in fine mode. With detents, snap the
    /// pointer's position (not the last snapped value) so snapping never fights the drag.
    /// </summary>
    internal void DragTo(double x, bool fine)
    {
        var parts = Lay(1);
        var span = parts.TrackTo - parts.TrackFrom;
        fine &= !(Detent > 0);
        if (fine)
        {
            if (!_dragging || !_fine)
                _fineFrom = (_dragging ? _lastX : x, Value);
            _fine = true;
            if (span > 0)
                Set(_fineFrom.Value + (x - _fineFrom.X) / span * (Maximum - Minimum) * FineFactor);
        }
        else
        {
            _fine = false;
            if (span > 0)
                Set(Minimum + Math.Max(0, Math.Min((x - parts.TrackFrom) / span, 1)) * (Maximum - Minimum));
        }

        (_dragging, _lastX) = (true, x);
    }

    /// <summary>Ends a track drag.</summary>
    internal void EndDrag() => (_dragging, _fine) = (false, false);

    /// <summary>Wheel by <paramref name="delta"/> (120 per notch; touchpad partials accumulate): <see cref="SmallChange"/> per notch.</summary>
    internal void WheelBy(int delta)
    {
        _wheel += delta;
        var notches = _wheel / 120;
        _wheel -= notches * 120;
        if (notches != 0)
            Set(Value + notches * SmallChange);
    }

    /// <summary>Double-click: restores <see cref="DefaultValue"/> (no-op if NaN).</summary>
    internal void ResetToDefault()
    {
        if (!double.IsNaN(DefaultValue))
            Set(DefaultValue);
    }

    /// <summary>Icon click: toggles the switch if enabled, then raises IconClick.</summary>
    internal void ClickIcon()
    {
        if (IsIconToggle)
            IsIconChecked = !IsIconChecked;
        RaiseEvent(new RoutedEventArgs(IconClickEvent, this));
    }

    /// <summary>Current readout: <see cref="ReadoutFor"/>, else the detent label, else <see cref="ReadoutFormat"/>.</summary>
    internal string ReadoutText() => ReadoutTextFor(Value);

    // True when detents have labels; UI Automation then also exposes them via ValuePattern.
    private bool Worded => DetentLabels is { Count: > 0 } && Detent > 0 && !double.IsInfinity(Detent);

    // Readout text for a value (also used for the old value in UI Automation change events).
    private string ReadoutTextFor(double value)
    {
        if (ReadoutFor is { } words)
        {
            try
            {
                return words(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Services.AppLog.Write("Pill slider: its readout failed", ex);
            }
        }

        // Detent label (e.g. "1.25x") when available.
        if (Worded && DetentLabels is { } labels && !double.IsNaN(value))
        {
            var index = Math.Round((value - Minimum) / Detent, MidpointRounding.AwayFromZero);
            if (index >= 0 && index < labels.Count && labels[(int)index] is { } label)
                return label;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, ReadoutFormat, value);
        }
        catch (FormatException)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    // ----- Pointer, wheel, touch and keyboard input -----

    private bool OnIcon(Point at) => new Rect(Slot()).Contains(at);

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _touch = e.StylusDevice is not null;

        // Under a neighbour's open pill, hover opens after a dwell; a press or wheel opens immediately.
        if (!_touch && !_wantOpen && UnderOpenNeighbour())
            OpenLater();
        else if (!_touch)
            Expand();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _openTimer?.Stop();
        CloseLater();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        _touch = e.StylusDevice is not null;

        // First touch only opens (touch has no hover preview).
        if (_touch && OpenNow <= 0 && !_wantOpen)
        {
            Expand();
            return;
        }

        Expand();
        var at = e.GetPosition(this);

        // A text face is not a toggle: double-click on it also restores the default (e.g. "1.5x" to 1x).
        if (e.ClickCount == 2 && Icon == PillIcon.Readout && !IsIconToggle && OnIcon(at))
        {
            ResetToDefault();
            return;
        }

        if (OnIcon(at))
        {
            _iconPressed = CaptureMouse();
            return;
        }

        if (e.ClickCount == 2)
        {
            ResetToDefault();
            return;
        }

        var parts = Lay(1);
        if (at.X < parts.TrackFrom - ThumbRadius - 3 || at.X > parts.TrackTo + ThumbRadius + 3 || !CaptureMouse())
            return;
        DragTo(at.X, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && IsMouseCaptured)
            DragTo(e.GetPosition(this).X, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var (icon, dragged, at) = (_iconPressed, _dragging, e.GetPosition(this));
        if (icon || dragged)
        {
            e.Handled = true;
            _iconPressed = false;
            EndDrag();
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            if (icon && OnIcon(at))
                ClickIcon();
        }

        // Touch: close 2.5 s after the last touch (no hover to keep it open).
        if (icon || dragged || _touch)
            CloseLater();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _iconPressed = false;
        EndDrag();
        CloseLater();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        Expand();
        WheelBy(e.Delta);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        // Keyboard focus (Tab): open while focused so the stepped value is visible.
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            _keyOpened = true;
            Expand();
        }

        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _keyOpened = false;
        CloseLater();
        InvalidateVisual();
    }

    /// <summary>Keys owned while focused (MainWindow.KeyBelongsToFocus): arrows, Page Up/Down, Home, End, and Enter for the icon toggle.</summary>
    public bool OwnsKey(Key key, ModifierKeys modifiers) => modifiers == ModifierKeys.None
        && (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End || (key == Key.Enter && IsIconToggle));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !OwnsKey(e.Key, Keyboard.Modifiers))
            return;

        e.Handled = true;
        _keyOpened = true;
        Expand();
        switch (e.Key)
        {
            case Key.Left or Key.Down: Set(Value - SmallChange); break;
            case Key.Right or Key.Up: Set(Value + SmallChange); break;
            case Key.PageDown: Set(Value - LargeChange); break;
            case Key.PageUp: Set(Value + LargeChange); break;
            case Key.Home: Set(Minimum); break;
            case Key.End: Set(Maximum); break;
            case Key.Enter: ClickIcon(); break;
        }
    }

    // ----- Rendering -----

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        (_readout, _face) = (null, null);
        InvalidateVisual();
    }

    private Brush Themed(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    // Override brush if set, else the theme resource.
    private Brush Look(DependencyProperty over, string key, Brush fallback) => GetValue(over) as Brush ?? Themed(key, fallback);

    protected override void OnRender(DrawingContext drawing)
    {
        var slot = Slot();
        var open = OpenNow;
        var text = Look(IconBrushProperty, "TextBrush", Brushes.White);
        var surface = Look(SurfaceBrushProperty, "ControlHoverBrush", Brushes.DimGray);
        var focused = IsKeyboardFocused;
        if (open <= 0)
        {
            // Collapsed: icon only, on a well while hovered or focused.
            if (IsMouseOver || IsMouseCaptured || focused)
                drawing.DrawRoundedRectangle(surface, focused ? FocusPen() : null, new Rect(slot), slot.Height / 2, slot.Height / 2);
            DrawIcon(drawing, slot, text);
            return;
        }

        // A text face is the readout; no separate readout is built.
        var readout = Icon == PillIcon.Readout ? null : Readout(text);
        var parts = Lay(open);
        var full = Lay(1);
        var radius = slot.Height / 2;
        var edge = focused ? FocusPen() : EdgePen(Look(EdgeBrushProperty, "ControlBorderBrush", Brushes.Gray));
        var pill = parts.Pill;
        pill.Inflate(-0.5, -0.5);
        drawing.DrawRoundedRectangle(surface, edge, pill, radius, radius);

        // During the transition, contents fade with openness and are clipped to the current pill.
        var growing = open < 1;
        if (growing)
        {
            var clip = new RectangleGeometry(parts.Pill, radius, radius);
            clip.Freeze();
            drawing.PushClip(clip);
            drawing.PushOpacity(open);
        }

        var (middle, accent) = (slot.Height / 2, Themed("AccentBrush", Brushes.SteelBlue));
        var x = full.TrackFrom + Fraction() * (full.TrackTo - full.TrackFrom);
        var groove = Look(TrackBrushProperty, "MutedTextBrush", Brushes.Gray);
        drawing.PushOpacity(0.45);
        drawing.DrawRoundedRectangle(groove, null,
            new Rect(full.TrackFrom, middle - TrackThickness / 2, full.TrackTo - full.TrackFrom, TrackThickness), TrackThickness / 2, TrackThickness / 2);
        drawing.Pop();
        if (x > full.TrackFrom)
            drawing.DrawRoundedRectangle(accent, null, new Rect(full.TrackFrom, middle - TrackThickness / 2, x - full.TrackFrom, TrackThickness), TrackThickness / 2, TrackThickness / 2);

        // Detent dots: accent up to the thumb, stronger groove colour beyond.
        if (Detents() is > 0 and var detents)
        {
            var step = Detent / (Maximum - Minimum) * (full.TrackTo - full.TrackFrom);
            for (var i = 0; i < detents; i++)
            {
                var at = full.TrackFrom + i * step;
                drawing.DrawEllipse(at <= x + 0.5 ? accent : groove, null, new Point(at, middle), DetentRadius, DetentRadius);
            }
        }

        drawing.DrawEllipse(accent, null, new Point(x, middle), ThumbRadius, ThumbRadius);
        if (readout is not null)
            drawing.DrawText(readout, new Point(ExpandsLeft ? full.Readout.Right - readout.Width : full.Readout.Left, middle - readout.Height / 2));
        if (growing)
        {
            drawing.Pop();
            drawing.Pop();
        }

        DrawIcon(drawing, slot, text);
    }

    // Readout FormattedText, rebuilt only on text, brush or DPI change; its reserved width grows to the widest seen.
    private FormattedText Readout(Brush brush)
    {
        var words = ReadoutText();
        if (_readout is null || words != _readoutText || !ReferenceEquals(brush, _readoutBrush))
        {
            var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _readout = new FormattedText(words, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, ReadoutSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            (_readoutText, _readoutBrush) = (words, brush);
        }

        _readoutWidth = Math.Max(_readoutWidth, Math.Ceiling(_readout.Width));
        return _readout;
    }

    private Pen EdgePen(Brush brush)
    {
        if (_edgePen is null || !ReferenceEquals(_edgePen.Brush, brush))
        {
            _edgePen = new Pen(brush, 1);
            _edgePen.Freeze();
        }

        return _edgePen;
    }

    private Pen FocusPen()
    {
        var accent = Themed("AccentBrush", Brushes.SteelBlue);
        if (_focusPen is null || !ReferenceEquals(_focusPen.Brush, accent))
        {
            _focusPen = new Pen(accent, 1);
            _focusPen.Freeze();
        }

        return _focusPen;
    }

    private void DrawIcon(DrawingContext drawing, Size slot, Brush brush)
    {
        if (Icon == PillIcon.Readout)
        {
            DrawFace(drawing, slot, brush);
            return;
        }

        if (_iconPen is null || !ReferenceEquals(_iconPen.Brush, brush))
        {
            _iconPen = new Pen(brush, IconStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _iconPen.Freeze();
        }

        if (_iconsFor != slot)
        {
            Array.Clear(_icons);
            _iconsFor = slot;
        }

        var (waves, crossed) = Icon == PillIcon.Speaker ? SpeakerLevel(Fraction(), IsIconChecked) : (0, false);
        var index = Icon == PillIcon.Magnifier ? 4 : crossed ? 0 : waves;
        drawing.DrawGeometry(null, _iconPen, _icons[index] ??= IconGeometry(Icon, waves, crossed, new Point((slot.Width - IconBox) / 2, (slot.Height - IconBox) / 2)));
    }

    // Text face: value text centred in the slot, rebuilt only on text, brush, slot width or DPI change. With the 24 DIP slot, text wider
    // than the slot minus FacePad on each side ("0.25x", "1.25x") is shrunk to fit; "1x" and "2x" render at full size.
    private void DrawFace(DrawingContext drawing, Size slot, Brush brush)
    {
        var words = ReadoutText();
        var room = slot.Width - 2 * FacePad;
        if (_face is null || words != _faceText || !ReferenceEquals(brush, _faceBrush) || room != _faceRoom)
        {
            _face = FaceText(words, TextElement.GetFontFamily(this), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip, room);
            (_faceText, _faceBrush, _faceRoom) = (words, brush, room);
        }

        drawing.DrawText(_face, new Point(Math.Round((slot.Width - _face.Width) / 2), Math.Round((slot.Height - _face.Height) / 2)));
    }

    /// <summary>
    /// Builds text-face FormattedText: window typeface at <see cref="FaceFontSize"/> DIPs regardless of system text scaling (Segoe UI digits
    /// are tabular), Ideal formatting so DIP width is scale-independent. Text wider than <paramref name="room"/> is shrunk to fit, never below
    /// <see cref="FaceLeastSize"/>.
    /// </summary>
    internal static FormattedText FaceText(string words, FontFamily family, Brush brush, double pixelsPerDip, double room = double.PositiveInfinity)
    {
        var text = new FormattedText(words, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FaceFontSize, brush, pixelsPerDip);
        // Step down 0.1 DIP from the proportional size and re-measure: glyph metrics need not scale linearly.
        var size = FaceFontSize;
        for (var pass = 0; pass < 4 && room > 0 && double.IsFinite(room) && text.WidthIncludingTrailingWhitespace > room && size > FaceLeastSize; pass++)
        {
            size = Math.Max(FaceLeastSize, Math.Min(size - 0.1, Math.Floor(size * room / text.WidthIncludingTrailingWhitespace * 10) / 10));
            text.SetFontSize(size);
        }

        return text;
    }

    /// <summary>Speaker wave count for a level (0..1) and mute: crossed (none) when muted or at minimum; else 1 up to 1/3, 2 up to 2/3, 3 above.</summary>
    internal static (int Waves, bool Crossed) SpeakerLevel(double fraction, bool muted) =>
        muted || fraction <= 0 ? (0, true) : fraction <= 1.0 / 3 ? (1, false) : fraction <= 2.0 / 3 ? (2, false) : (3, false);

    private static StreamGeometry IconGeometry(PillIcon icon, int waves, bool crossed, Point origin)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var stroke in IconStrokes(icon, waves, crossed, origin))
            {
                context.BeginFigure(stroke.From, false, false);
                if (stroke.Radius > 0)
                    context.ArcTo(stroke.To, new Size(stroke.Radius, stroke.Radius), 0, false, SweepDirection.Clockwise, true, false);
                else
                    context.LineTo(stroke.To, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Icon stroke: a line, or (with a radius) a clockwise circular arc between two points.</summary>
    internal readonly record struct PillStroke(Point From, Point To, double Radius);

    /// <summary>
    /// Vector icon strokes in a 16 DIP box at <paramref name="origin"/>: speaker = box + cone with <paramref name="waves"/> arcs, or a cross
    /// when <paramref name="crossed"/>; magnifier = ring + handle.
    /// </summary>
    internal static PillStroke[] IconStrokes(PillIcon icon, int waves, bool crossed, Point origin)
    {
        Point P(double x, double y) => new(origin.X + x, origin.Y + y);
        var strokes = new List<PillStroke>(10);
        if (icon == PillIcon.Magnifier)
        {
            strokes.Add(new PillStroke(P(2, 6.5), P(11, 6.5), 4.5));
            strokes.Add(new PillStroke(P(11, 6.5), P(2, 6.5), 4.5));
            strokes.Add(new PillStroke(P(9.9, 9.9), P(14, 14), 0));
            return [.. strokes];
        }

        Point[] cone = [P(1.5, 6), P(4.5, 6), P(8.5, 2.5), P(8.5, 13.5), P(4.5, 10), P(1.5, 10), P(1.5, 6)];
        for (var i = 1; i < cone.Length; i++)
            strokes.Add(new PillStroke(cone[i - 1], cone[i], 0));

        if (crossed)
        {
            strokes.Add(new PillStroke(P(11, 5.5), P(15, 10.5), 0));
            strokes.Add(new PillStroke(P(11, 10.5), P(15, 5.5), 0));
            return [.. strokes];
        }

        // Each wave: an arc centred on the cone mouth, +/-45 degrees from the axis.
        for (var wave = 0; wave < Math.Max(0, Math.Min(waves, 3)); wave++)
        {
            var radius = 2.5 + wave * 2;
            var reach = radius * Math.Sqrt(0.5);
            strokes.Add(new PillStroke(P(8.5 + reach, 8 - reach), P(8.5 + reach, 8 + reach), radius));
        }

        return [.. strokes];
    }

    // ----- UI Automation: RangeValue slider (XAML name) with the icon as a child toggle ("Mute Player") -----

    protected override AutomationPeer OnCreateAutomationPeer() => new PillPeer(this);

    private sealed class PillPeer(ExpandablePillSlider owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider, IValueProvider
    {
        public IconPeer? Icon { get; private set; }

        protected override string GetClassNameCore() => nameof(ExpandablePillSlider);

        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Slider";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        // Always RangeValue; also read-only Value when detents are labelled ("1.25x" vs the RangeValue step index).
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.RangeValue => this,
            PatternInterface.Value when owner.Worded => this,
            _ => base.GetPattern(patternInterface),
        };

        string IValueProvider.Value => owner.ReadoutText();

        bool IValueProvider.IsReadOnly => true;

        void IValueProvider.SetValue(string value) => throw new InvalidOperationException("Its words are read-only: set its step through RangeValue.");

        protected override List<AutomationPeer>? GetChildrenCore() =>
            owner.IsIconToggle || owner.IconAutomationName.Length > 0 ? [Icon ??= new IconPeer(owner)] : null;

        public double Value => owner.Value;

        public bool IsReadOnly => !owner.IsEnabled;

        public double Maximum => owner.Maximum;

        public double Minimum => owner.Minimum;

        public double LargeChange => owner.LargeChange;

        public double SmallChange => owner.SmallChange;

        public void SetValue(double value)
        {
            if (!owner.IsEnabled)
                throw new ElementNotEnabledException();
            owner.Set(value);
        }
    }

    private sealed class IconPeer(ExpandablePillSlider owner) : AutomationPeer, IToggleProvider
    {
        public ToggleState ToggleState => Toggled(owner.IsIconChecked);

        public void Toggle()
        {
            if (!owner.IsEnabled)
                throw new ElementNotEnabledException();
            owner.ClickIcon();
        }

        private Rect Bounds() => PresentationSource.FromVisual(owner) is null ? Rect.Empty
            : new Rect(owner.PointToScreen(new Point(0, 0)), owner.PointToScreen(new Point(owner.Slot().Width, owner.Slot().Height)));

        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Toggle && owner.IsIconToggle ? this : null;

        protected override string GetAcceleratorKeyCore() => "";

        protected override string GetAccessKeyCore() => "";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetAutomationIdCore() => "";

        protected override Rect GetBoundingRectangleCore() => Bounds();

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override string GetClassNameCore() => "PillIcon";

        protected override Point GetClickablePointCore()
        {
            var bounds = Bounds();
            return bounds.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        protected override string GetHelpTextCore() => "";

        protected override string GetItemStatusCore() => "";

        protected override string GetItemTypeCore() => "";

        protected override AutomationPeer? GetLabeledByCore() => null;

        protected override string GetNameCore() => owner.IconAutomationName;

        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;

        protected override bool HasKeyboardFocusCore() => false;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        protected override bool IsEnabledCore() => owner.IsEnabled;

        protected override bool IsKeyboardFocusableCore() => false;

        protected override bool IsOffscreenCore() => Bounds().IsEmpty;

        protected override bool IsPasswordCore() => false;

        protected override bool IsRequiredForFormCore() => false;

        protected override void SetFocusCore()
        {
        }
    }
}
