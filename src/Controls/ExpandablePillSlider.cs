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

/// <summary>What an <see cref="ExpandablePillSlider"/> shows as its icon (vector strokes, drawn in OnRender).</summary>
public enum PillIcon
{
    /// <summary>A speaker whose waves follow the level; crossed out while its toggle is on (muted) or the value is at its least.</summary>
    Speaker,
    /// <summary>A magnifier.</summary>
    Magnifier,
    /// <summary>
    /// No picture: the readout's words where the icon goes (5.4.4, the playback speed's "1x"), so the value shows while it is closed;
    /// the open pill then has no readout of its own. Give it a <see cref="ExpandablePillSlider.CollapsedWidth"/> its widest words fit.
    /// </summary>
    Readout,
}

/// <summary>
/// A slider that is only its icon until the pointer is on it (Pass 5.4.4): then it opens into a pill with a track, a thumb and a
/// readout of its value, over its neighbours and never moving them (its layout size is always the collapsed one). The Monitor Volume
/// and the Playback Speed of the transport row (the speed's face its value, its thumb snapping to the seven speeds: <see cref="Detent"/>)
/// and the Timeline Zoom of the timeline's corner. Drawn in OnRender: no child visuals, nothing made for the open pill before it first opens.
/// <para>
/// It opens on the pointer, on the keyboard's focus (Tab) and while dragged, in 140 ms, and closes 250 ms after the pointer leaves (2.5 s
/// after the last touch). A press on the track jumps there and drags; Shift drags finely (a tenth as fast); the wheel steps by
/// <see cref="SmallChange"/>; a double-click puts <see cref="DefaultValue"/> back; a click on the icon switches <see cref="IsIconChecked"/>
/// (the speaker's mute). While it has the keyboard it keeps the arrows, Page Up / Down, Home, End and Enter (IOwnsKeys). Theme brushes are
/// read as it draws, and it draws again when a theme is put into effect, so every theme (the light one too) shows it right; a pill on a
/// surface of its own whatever the theme (the fullscreen bar) sets <see cref="IconBrush"/> and its kin instead.
/// </para>
/// </summary>
public sealed class ExpandablePillSlider : FrameworkElement, IOwnsKeys
{
    /// <summary>How long the pill takes to open or close, eased out (ms).</summary>
    internal const double OpenMs = 140;

    /// <summary>How long after the pointer leaves the pill closes (ms); after the last touch, <see cref="TouchCloseMs"/>.</summary>
    internal const double CloseDelayMs = 250, TouchCloseMs = 2500;

    /// <summary>A Shift drag moves the value this much of what a plain drag would.</summary>
    internal const double FineFactor = 0.1;

    // The open pill's parts (DIPs): the thumb, the track's thickness, room between the thumb's end stop and the icon, between the track
    // and the readout, after the readout, and the least room the readout gets.
    private const double ThumbRadius = 5, TrackThickness = 3, IconRoom = 3, ReadoutGap = 6, EndPad = 9, LeastReadout = 26, ReadoutSize = 11;
    private const double IconBox = 16, IconStroke = 1.3;

    /// <summary>
    /// A readout face's type size (the bar's text, 12 DIPs), the least room either side of its words in the slot, and the least size words
    /// too wide for the slot are drawn down to (5.5A: the speed pill is the Volume pill's 24 x 22 now, so "0.25x" is drawn smaller to fit).
    /// </summary>
    internal const double FaceFontSize = 12, FacePad = 2, FaceLeastSize = 7;

    // A detent's dot on the open track (a little wider than the track), and the most detents drawn apart (5.4.4).
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

    // How far open the pill is (0 the icon alone, 1 open), animated; drawn and hit-tested as it is.
    private static readonly DependencyProperty OpenProperty = DependencyProperty.Register("Open", typeof(double), typeof(ExpandablePillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // The opening and closing (made at the first opening, frozen, shared by every pill) and their ease.
    private static CubicEase? _ease;
    private static DoubleAnimation? _opening, _closing;

    // Whether it is open or opening; the close after the pointer leaves; whether the last input was a touch, and whether the keyboard
    // opened it (Tab, or a key pressed while it has the keyboard: it then stays open while it keeps the keyboard); and its opening once
    // the pointer rests on it, when its slot lies under a neighbour's open pill (5.4.4).
    private bool _wantOpen, _touch, _keyOpened;
    private DispatcherTimer? _closeTimer, _openTimer;

    // A drag on the track (where the pointer last was, and a Shift drag's start: the pointer and the value), and a press on the icon.
    private bool _dragging, _fine, _iconPressed;
    private double _lastX;
    private (double X, double Value) _fineFrom;
    private int _wheel;

    // What it draws with, made as it is first needed: the icons (by kind and waves), their pen; the open pill's edge, focus ring and
    // readout (the widest readout since it opened keeps the pill from twitching as the value changes).
    private readonly StreamGeometry?[] _icons = new StreamGeometry?[5];
    private Size _iconsFor;
    private Pen? _iconPen, _edgePen, _focusPen;
    private FormattedText? _readout;
    private string? _readoutText;
    private Brush? _readoutBrush;
    private double _readoutWidth;

    // A readout face's words as drawn (5.4.4), made at its first drawing and again only when they, their brush or the screen's scale change.
    private FormattedText? _face;
    private string? _faceText;
    private Brush? _faceBrush;
    private double _faceRoom;

    public ExpandablePillSlider()
    {
        Height = 22;
        Focusable = true;
        FocusVisualStyle = null;

        // A theme put into effect swaps the brushes it reads as it draws, which nothing else tells it of: it draws again then (5.4.4),
        // and when it comes back into a window, in case the theme changed while it was out of one.
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

    // The pens and the readout are made again for a new brush (they are kept by it), so drawing again is all a theme needs.
    private void ThemeApplied() => InvalidateVisual();

    /// <summary>The value, <see cref="Minimum"/> to <see cref="Maximum"/> (two-way by default).</summary>
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

    /// <summary>What a double-click on the track sets (NaN: no reset).</summary>
    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    /// <summary>A wheel notch, an arrow key.</summary>
    public double SmallChange
    {
        get => (double)GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    /// <summary>Page Up, Page Down.</summary>
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

    /// <summary>Whether a click on the icon switches <see cref="IsIconChecked"/> (the speaker's mute).</summary>
    public bool IsIconToggle
    {
        get => (bool)GetValue(IsIconToggleProperty);
        set => SetValue(IsIconToggleProperty, value);
    }

    /// <summary>The icon's switch (two-way by default): muted, for the speaker.</summary>
    public bool IsIconChecked
    {
        get => (bool)GetValue(IsIconCheckedProperty);
        set => SetValue(IsIconCheckedProperty, value);
    }

    /// <summary>The icon's name for UI Automation ("Mute Player").</summary>
    public string IconAutomationName
    {
        get => (string)GetValue(IconAutomationNameProperty);
        set => SetValue(IconAutomationNameProperty, value);
    }

    /// <summary>The readout's format of the value ("{0:0}%"); <see cref="ReadoutFor"/>, when set, is asked instead.</summary>
    public string ReadoutFormat
    {
        get => (string)GetValue(ReadoutFormatProperty);
        set => SetValue(ReadoutFormatProperty, value);
    }

    /// <summary>How long the open pill's track is (DIPs).</summary>
    public double TrackLength
    {
        get => (double)GetValue(TrackLengthProperty);
        set => SetValue(TrackLengthProperty, value);
    }

    /// <summary>Whether the pill opens to the left of its icon (the icon stays where it is), not to the right.</summary>
    public bool ExpandsLeft
    {
        get => (bool)GetValue(ExpandsLeftProperty);
        set => SetValue(ExpandsLeftProperty, value);
    }

    /// <summary>
    /// The icon's and the readout's brush in place of the theme's text (5.4.4); null, the theme's. With <see cref="SurfaceBrush"/>,
    /// <see cref="EdgeBrush"/> and <see cref="TrackBrush"/> for a pill on a surface of its own whatever the theme (the fullscreen bar).
    /// </summary>
    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    /// <summary>The well under the icon and the open pill's fill in place of the theme's hover fill (5.4.4); null, the theme's.</summary>
    public Brush? SurfaceBrush
    {
        get => (Brush?)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>The open pill's edge in place of the theme's control border (5.4.4); null, the theme's.</summary>
    public Brush? EdgeBrush
    {
        get => (Brush?)GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    /// <summary>The track's groove in place of the theme's muted text (5.4.4); null, the theme's.</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>
    /// The collapsed pill's width, its width in layout (DIPs, 5.4.4); NaN, the icon's square (its height and 2). A readout face
    /// (<see cref="PillIcon.Readout"/>) keeps one width whatever the value, so nothing beside it moves; its words wider than the slot are
    /// drawn smaller to fit it (5.5A).
    /// </summary>
    public double CollapsedWidth
    {
        get => (double)GetValue(CollapsedWidthProperty);
        set => SetValue(CollapsedWidthProperty, value);
    }

    /// <summary>
    /// The spacing of the detents from <see cref="Minimum"/> that every value from the user's hand snaps to (5.4.4: the speed's one step
    /// apart), each a dot on the open track; 0, none: any value.
    /// </summary>
    public double Detent
    {
        get => (double)GetValue(DetentProperty);
        set => SetValue(DetentProperty, value);
    }

    /// <summary>The readout's words for each detent from the least (5.4.4: the speeds, "0.25x" to "2x"); null, the value in <see cref="ReadoutFormat"/>.</summary>
    public IReadOnlyList<string>? DetentLabels
    {
        get => (IReadOnlyList<string>?)GetValue(DetentLabelsProperty);
        set => SetValue(DetentLabelsProperty, value);
    }

    /// <summary>
    /// The room between the slot and the open track's near end (DIPs, 5.4.4), kept clear for a neighbour's slot the open pill passes over
    /// (the Volume pill's beside the Playback Speed's, raised over it): no detent, thumb or press on the track lies under it. 0, none.
    /// </summary>
    public double TrackInset
    {
        get => (double)GetValue(TrackInsetProperty);
        set => SetValue(TrackInsetProperty, value);
    }

    /// <summary>The readout's words for a value, asked only while the pill is open or its face is the readout (the Timeline's "2.4x", which is not the slider's own value).</summary>
    public Func<double, string>? ReadoutFor { get; set; }

    /// <summary>Whether the pill is open, or opening (false while it closes).</summary>
    public bool IsExpanded => _wantOpen;

    public event RoutedPropertyChangedEventHandler<double> ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    /// <summary>The icon was clicked (after <see cref="IsIconChecked"/> switched, when it is a toggle).</summary>
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

        // Its words too when its detents have them (5.4.4): what a screen reader says is the speed ("1.5x"), not its step.
        if (pill.Worded)
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, pill.ReadoutTextFor(from), pill.ReadoutTextFor(to));
    }

    private static void OnIconCheckedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (UIElementAutomationPeer.FromElement((ExpandablePillSlider)sender) is PillPeer { Icon: { } icon })
            icon.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, Toggled((bool)e.OldValue), Toggled((bool)e.NewValue));
    }

    private static ToggleState Toggled(bool on) => on ? ToggleState.On : ToggleState.Off;

    // ----- Size and place: always the collapsed slot in layout (the icon's square, or CollapsedWidth); the open pill drawn (and hit) past it -----

    private Size Slot()
    {
        var height = RenderSize.Height > 0 ? RenderSize.Height : double.IsNaN(Height) ? 22 : Height;
        return new Size(RenderSize.Width > 0 ? RenderSize.Width : CollapsedWidthFor(height), height);
    }

    // The collapsed width: CollapsedWidth when one is set (5.4.4), else the icon's square.
    private double CollapsedWidthFor(double height) => CollapsedWidth is > 0 and var width && !double.IsInfinity(width) ? width : height + 2;

    // The layout size is always the collapsed one.
    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsNaN(Height) ? 22 : Height;
        return new Size(CollapsedWidthFor(height), height);
    }

    // Drawn past its slot while open: never cut to it.
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;

    // The pill's parts, open this far: the pill itself, the track (its ends: where the thumb's centre goes at the least and the most) and
    // the readout's room. The icon stays in the slot; the pill grows from it to the left or the right. A readout face is the readout
    // (5.4.4): the pill keeps no room for another. TrackInset (5.4.4) sets the track that much further from the slot.
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

    /// <summary>The track's ends on the control when open (where the thumb's centre is at the least and at the most): for tests.</summary>
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

    // ----- Opening and closing -----

    /// <summary>How far open the pill is <paramref name="ms"/> after it began opening: 0 at 0, 1 at <see cref="OpenMs"/>, eased out (a cubic).</summary>
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

        // It takes over from a neighbour still open with the pointer gone from it (5.4.4: Volume and Playback Speed side by side): that
        // one closes now rather than CloseDelayMs on, so the two never stand open over each other.
        foreach (var other in Neighbours())
        {
            if (other._wantOpen && !other.Held)
                other.Collapse();
        }
    }

    // The pills beside it in its panel (5.4.4: the transport row's Volume and Playback Speed).
    private IEnumerable<ExpandablePillSlider> Neighbours() => VisualTreeHelper.GetParent(this) is System.Windows.Controls.Panel panel
        ? panel.Children.OfType<ExpandablePillSlider>().Where(other => !ReferenceEquals(other, this))
        : [];

    /// <summary>
    /// Whether its slot lies under a neighbour's open pill (5.4.4: the Volume pill's, raised over the open Playback Speed's): the pointer
    /// then opens it only once it rests there (<see cref="OpensLater"/>), so a pointer crossing it on the way to that one's track opens nothing.
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

    /// <summary>Whether it opens CloseDelayMs on if the pointer stays (5.4.4: as the neighbour over it closes): for tests.</summary>
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

    // ----- Clicking off it (5.4.4, the user's) -----
    //
    // The open pill lies over its neighbours and never moves them: a press anywhere else in its window (mouse, pen or touch) closes it at
    // once, so whatever lay under it is one click away. Watched only while it is open, at the root of its visual tree and tunnelling (a
    // control that handles its own press cannot hide it); the press itself goes on to whatever it was meant for.

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

    // A press on the pill itself (its slot, or the open pill drawn past it: it has no children) leaves it be.
    private void PressedAt(object? source)
    {
        if (ReferenceEquals(source, this) || !_wantOpen)
            return;
        _keyOpened = false;
        Collapse();
    }

    private void Animate(bool open)
    {
        // With Windows' animations off, it opens and closes at once.
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

    /// <summary>Opens or closes the pill at once, with no animation (for tests, which have no pointer).</summary>
    internal void OpenAtOnce(bool open)
    {
        _closeTimer?.Stop();
        _openTimer?.Stop();
        (_wantOpen, _readoutWidth) = (open, open ? 0 : _readoutWidth);
        WatchClickOff(open);
        SetOpenNow(open);
    }

    // Whether it stays open: under the pointer (not after a touch: a lifted finger is not there), held, or keeping the keyboard it was opened with.
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

    // ----- Changing the value -----

    // The value set from the user's hand, kept between Minimum and Maximum (never Math.Clamp: a binding may set Maximum before Minimum),
    // on its nearest detent when it has them (5.4.4).
    private void Set(double value)
    {
        var kept = Snapped(Math.Max(Minimum, Math.Min(value, Maximum)));
        if (!double.IsNaN(kept) && kept != Value)
            Value = kept;
    }

    // The value (already kept in the range) on its nearest detent (5.4.4), never past Maximum when the range is no whole number of
    // detents; itself when there are none.
    private double Snapped(double value)
    {
        var detent = Detent;
        if (!(detent > 0) || double.IsInfinity(detent) || double.IsNaN(value))
            return value;

        var on = Minimum + Math.Round((value - Minimum) / detent, MidpointRounding.AwayFromZero) * detent;
        return on > Maximum ? Math.Max(Minimum, on - detent) : on;
    }

    // How many detents the open track shows (5.4.4): from Minimum every Detent up to Maximum; 0 for none or too many to tell apart.
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
    /// A drag on the track reached <paramref name="x"/> (the control's coordinates): there (fine: from where it was, a tenth as fast). With
    /// detents (5.4.4) always there, on the nearest one: the pointer picks it, never the value it last snapped to, so the snap never fights the drag.
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

    /// <summary>The drag on the track is over.</summary>
    internal void EndDrag() => (_dragging, _fine) = (false, false);

    /// <summary>The wheel turned by <paramref name="delta"/> (120 a notch; a touchpad's smaller turns add up): <see cref="SmallChange"/> a notch.</summary>
    internal void WheelBy(int delta)
    {
        _wheel += delta;
        var notches = _wheel / 120;
        _wheel -= notches * 120;
        if (notches != 0)
            Set(Value + notches * SmallChange);
    }

    /// <summary>A double-click: <see cref="DefaultValue"/> again (nothing when it has none).</summary>
    internal void ResetToDefault()
    {
        if (!double.IsNaN(DefaultValue))
            Set(DefaultValue);
    }

    /// <summary>The icon clicked: its switch flipped (when it is one), and IconClick raised.</summary>
    internal void ClickIcon()
    {
        if (IsIconToggle)
            IsIconChecked = !IsIconChecked;
        RaiseEvent(new RoutedEventArgs(IconClickEvent, this));
    }

    /// <summary>What the readout says now: <see cref="ReadoutFor"/>'s words, else its detent's label, else the value in <see cref="ReadoutFormat"/>.</summary>
    internal string ReadoutText() => ReadoutTextFor(Value);

    // Whether its detents have words (5.4.4: the speeds): UI Automation then gives them as its Value too.
    private bool Worded => DetentLabels is { Count: > 0 } && Detent > 0 && !double.IsInfinity(Detent);

    // What the readout says at a value (the value before a change too, for UI Automation's change event).
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

        // The detent's own words (5.4.4: "1.25x"), when it has them.
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

    // ----- The pointer, the wheel, touch and keys -----

    private bool OnIcon(Point at) => new Rect(Slot()).Contains(at);

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _touch = e.StylusDevice is not null;

        // (Under a neighbour's open pill, 5.4.4, it opens once the pointer rests on it; a press or the wheel opens it at once.)
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

        // A first touch only opens it (a finger has no hover to show what it is first).
        if (_touch && OpenNow <= 0 && !_wantOpen)
        {
            Expand();
            return;
        }

        Expand();
        var at = e.GetPosition(this);

        // A readout face is no switch: a double-click on it puts the default back too (5.4.4: the speed's "1.5x" back to 1x).
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

        // (A lifted finger is no pointer resting on it: it closes 2.5 s after the last touch.)
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

        // Reached with the keyboard (Tab): open while it has it, so the value it steps is in sight.
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

    /// <summary>The keys it keeps while it has the keyboard (MainWindow.KeyBelongsToFocus): the arrows, Page Up / Down, Home, End, and Enter for its icon's switch.</summary>
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

    // ----- Drawing -----

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        (_readout, _face) = (null, null);
        InvalidateVisual();
    }

    private Brush Themed(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    // A brush set in place of the theme's (5.4.4), else the theme's.
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
            // The icon alone, on a well while the pointer or the keyboard is on it.
            if (IsMouseOver || IsMouseCaptured || focused)
                drawing.DrawRoundedRectangle(surface, focused ? FocusPen() : null, new Rect(slot), slot.Height / 2, slot.Height / 2);
            DrawIcon(drawing, slot, text);
            return;
        }

        // (A readout face is the readout: no other is made, 5.4.4.)
        var readout = Icon == PillIcon.Readout ? null : Readout(text);
        var parts = Lay(open);
        var full = Lay(1);
        var radius = slot.Height / 2;
        var edge = focused ? FocusPen() : EdgePen(Look(EdgeBrushProperty, "ControlBorderBrush", Brushes.Gray));
        var pill = parts.Pill;
        pill.Inflate(-0.5, -0.5);
        drawing.DrawRoundedRectangle(surface, edge, pill, radius, radius);

        // While it opens or closes, what is inside fades with it and is cut to the pill as it is.
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

        // The detents (5.4.4): a dot at each, the accent's up to the thumb, the groove's (stronger) past it.
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

    // The readout's text, made again only when its words, its brush or the screen's scale change; the readout's room grows to the widest.
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

    // A readout face (5.4.4): the value's words in the middle of the slot, made again only when they, their brush, the slot's width or the
    // screen's scale change. 5.5A: the slot is the Volume pill's 24 DIPs; words wider than it less FacePad either side ("0.25x", "1.25x")
    // are drawn smaller to fit, "1x" and "2x" at the bar's full size.
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
    /// A readout face's words as drawn (5.4.4): the bar's text (the window's face, <see cref="FaceFontSize"/> DIPs, whatever the system's text
    /// size; Segoe UI's figures are tabular, every digit one width), in WPF's ideal text layout, so their width in DIPs is the same at any scale.
    /// 5.5A: words wider than <paramref name="room"/> are drawn smaller to fit it (their width follows the size), never below
    /// <see cref="FaceLeastSize"/>.
    /// </summary>
    internal static FormattedText FaceText(string words, FontFamily family, Brush brush, double pixelsPerDip, double room = double.PositiveInfinity)
    {
        var text = new FormattedText(words, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FaceFontSize, brush, pixelsPerDip);
        // (Down a tenth of a DIP at a time from the proportional size, looked at again: a scale's glyph metrics need not shrink exactly in step.)
        var size = FaceFontSize;
        for (var pass = 0; pass < 4 && room > 0 && double.IsFinite(room) && text.WidthIncludingTrailingWhitespace > room && size > FaceLeastSize; pass++)
        {
            size = Math.Max(FaceLeastSize, Math.Min(size - 0.1, Math.Floor(size * room / text.WidthIncludingTrailingWhitespace * 10) / 10));
            text.SetFontSize(size);
        }

        return text;
    }

    /// <summary>The speaker's waves for a level (0 to 1 of the range) and whether it is muted: crossed out (no waves) when muted or at the least; else 1 to a third, 2 to two thirds, 3 above.</summary>
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

    /// <summary>One stroke of an icon: a line, or (a radius) a clockwise arc of a circle, from one point to another.</summary>
    internal readonly record struct PillStroke(Point From, Point To, double Radius);

    /// <summary>
    /// The icon's strokes in a 16-DIP box at <paramref name="origin"/> (CLAUDE.md 10.4, vector strokes): the speaker is a box and a cone,
    /// with <paramref name="waves"/> arcs before it, or a cross there (<paramref name="crossed"/>); the magnifier a ring and its handle.
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

        // Each wave an arc about the cone's mouth, 45 degrees either side of straight ahead.
        for (var wave = 0; wave < Math.Max(0, Math.Min(waves, 3)); wave++)
        {
            var radius = 2.5 + wave * 2;
            var reach = radius * Math.Sqrt(0.5);
            strokes.Add(new PillStroke(P(8.5 + reach, 8 - reach), P(8.5 + reach, 8 + reach), radius));
        }

        return [.. strokes];
    }

    // ----- UI Automation: a slider (RangeValue) named as the XAML names it, with its icon as a child toggle ("Mute Player") -----

    protected override AutomationPeer OnCreateAutomationPeer() => new PillPeer(this);

    private sealed class PillPeer(ExpandablePillSlider owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider, IValueProvider
    {
        public IconPeer? Icon { get; private set; }

        protected override string GetClassNameCore() => nameof(ExpandablePillSlider);

        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Slider";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        // RangeValue always; Value too (read-only) when its detents have words (5.4.4: the speed's "1.25x", where RangeValue gives its step).
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
