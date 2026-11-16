using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>
/// Immediate-mode timeline bar: blocks, frames, waveforms and keyframe diamonds are drawn in OnRender with no child elements, so layout
/// cost is independent of block count. The playhead is a separate translated visual, so playback never re-renders the bar.
/// </summary>
public abstract class TimelineBar : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowerBorderBrushProperty = DependencyProperty.Register(
        nameof(LowerBorderBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(Thickness), typeof(TimelineBar), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsRender));

    protected static readonly Pen KeyOutline = Frozen(new Pen(Brushes.Black, 0.7));

    private readonly DrawingVisual _playhead = new();
    private double _playheadX = double.NaN;
    private double _playheadHeight;

    protected TimelineBar()
    {
        ClipToBounds = true;
        AddVisualChild(_playhead);
    }

    /// <summary>Background fill; also makes the whole bar hit-testable (null renders as Transparent).</summary>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>
    /// Sunken well edge, top and left (theme WellBorderBrush), drawn above the background and below content; takes no layout space.
    /// </summary>
    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>Bottom and right edge brush (WellBorderLowerBrush); null uses one colour for all sides.</summary>
    public Brush? LowerBorderBrush
    {
        get => (Brush?)GetValue(LowerBorderBrushProperty);
        set => SetValue(LowerBorderBrushProperty, value);
    }

    /// <summary>Edge thickness (Top used for all sides); 0 (flat themes) draws none.</summary>
    public Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    /// <summary>Playhead x in pixels; NaN for none.</summary>
    public double PlayheadX
    {
        get => _playheadX;
        set
        {
            _playheadX = value;
            PlacePlayhead();
        }
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _playhead;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PlacePlayhead();
    }

    private void PlacePlayhead()
    {
        var height = Math.Max(ActualHeight, 4);
        var shown = !double.IsNaN(_playheadX);
        if (height != _playheadHeight || !shown)
        {
            _playheadHeight = shown ? height : 0;
            using var drawing = _playhead.RenderOpen();
            if (shown)
                drawing.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, 1.5, height));
        }

        if (shown)
            _playhead.Offset = new Vector(_playheadX, 0);
    }

    // Cached edge pens, reused while brush and width are unchanged (theme brushes are frozen, so reference equality suffices).
    private Pen? _edgePen, _lowerEdgePen;

    // Square caps: each half of a two-tone edge covers its corners fully; the lower half (drawn last) owns the shared corners.
    private static Pen EdgePen(ref Pen? kept, Brush brush, double width)
    {
        if (kept is null || !ReferenceEquals(kept.Brush, brush) || kept.Thickness != width)
            kept = Frozen(new Pen(brush, width) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square });
        return kept;
    }

    protected void DrawBackground(DrawingContext drawing)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        drawing.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, width, height));

        var edge = BorderThickness.Top;
        if (edge <= 0 || BorderBrush is not { } brush || width <= 2 * edge || height <= 2 * edge)
            return;

        // Snap to device pixels like ButtonChrome edges, stroking along the edge centre line.
        var guidelines = new GuidelineSet([0, width], [0, height]);
        guidelines.Freeze();
        drawing.PushGuidelineSet(guidelines);
        var (left, top, right, bottom) = (edge / 2, edge / 2, width - edge / 2, height - edge / 2);
        if (LowerBorderBrush is { } lower && !ReferenceEquals(lower, brush))
        {
            drawing.DrawGeometry(null, EdgePen(ref _edgePen, brush, edge), Frozen(Polyline(new(left, bottom), new(left, top), new(right, top))));
            drawing.DrawGeometry(null, EdgePen(ref _lowerEdgePen, lower, edge), Frozen(Polyline(new(right, top), new(right, bottom), new(left, bottom))));
        }
        else
        {
            drawing.DrawRectangle(null, EdgePen(ref _edgePen, brush, edge), new Rect(left, top, right - left, bottom - top));
        }

        drawing.Pop();
    }

    private static StreamGeometry Polyline(Point from, Point corner, Point to)
    {
        var line = new StreamGeometry();
        using var path = line.Open();
        path.BeginFigure(from, false, false);
        path.LineTo(corner, true, false);
        path.LineTo(to, true, false);
        return line;
    }

    /// <summary>Keyframe diamond geometry centred on a point.</summary>
    protected static void DrawDiamond(DrawingContext drawing, double x, double y, double radius) =>
        drawing.DrawGeometry(HandPegApp.Services.ThemeManager.KeyFill, KeyOutline, Diamonds([x], double.MaxValue, y, radius));

    /// <summary>
    /// Single frozen geometry of diamonds at each x (height y), culling those wholly outside [0, <paramref name="width"/>].
    /// </summary>
    protected static Geometry Diamonds(IReadOnlyList<double> xs, double width, double y, double radius)
    {
        var diamonds = new StreamGeometry();
        using (var path = diamonds.Open())
        {
            foreach (var x in xs)
            {
                if (x < -radius || x > width + radius)
                    continue;

                path.BeginFigure(new Point(x, y - radius), isFilled: true, isClosed: true);
                path.LineTo(new Point(x + radius, y), true, false);
                path.LineTo(new Point(x, y + radius), true, false);
                path.LineTo(new Point(x - radius, y), true, false);
            }
        }

        diamonds.Freeze();
        return diamonds;
    }

    protected static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    protected static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    // ----- UI Automation -----
    // One named automation element with bounds; drawn content has no peers (the playhead is a Visual), so children are not walked.

    /// <summary>Fallback automation name when AutomationProperties.Name is unset.</summary>
    protected abstract string AutomationName { get; }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineBarPeer(this);

    private sealed class TimelineBarPeer(TimelineBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => owner.GetType().Name;

        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.AutomationName;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// Waveform window for a block: the full waveform laid out is <paramref name="Span"/> pixels wide and the block starts
/// <paramref name="Offset"/> pixels into it. A zero span fits the whole waveform to the block.
/// </summary>
public readonly record struct WaveSpan(HandPegApp.Services.WaveformData Data, double Offset, double Span);

/// <summary>
/// Time-based waveform window for a unified-timeline block: media seconds [<paramref name="MediaFrom"/>, <paramref name="MediaTo"/>] at
/// <paramref name="Speed"/>, scaled by <paramref name="GainScale"/> (1 = 0 dB), laid from the audio's left edge (block left minus any J-cut
/// lead-in, TimelineBlock.AudioSplit). Media second <c>s</c> maps to <c>soundLeft + (s - MediaFrom) / Speed * pixelsPerSecond</c>, so
/// trims, cuts and folds always place audio exactly.
/// </summary>
public readonly record struct WaveWindow(HandPegApp.Services.WaveformData Data, double MediaFrom, double MediaTo, double Speed = 1, double GainScale = 1);

/// <summary>
/// A block's thumbnails: frames sampled evenly over the whole file (frame k at <c>k * SecondsPerFrame</c>), shown from
/// <paramref name="MediaFrom"/> at <paramref name="Speed"/>. Laid on the file's time grid, so they stay fixed under start trims and
/// slide under slips.
/// </summary>
public readonly record struct FilmWindow(IReadOnlyList<ImageSource> Frames, double SecondsPerFrame, double MediaFrom, double Speed);

/// <summary>Block drag mode (a roll also affects the adjacent block).</summary>
public enum BlockEdit
{
    /// <summary>Move along its row or onto another track.</summary>
    Move,

    /// <summary>Right-edge trim (duration).</summary>
    TrimEnd,

    /// <summary>Left-edge trim (start time and media in-point).</summary>
    TrimStart,

    /// <summary>Roll: its end and the next block's start move together.</summary>
    Roll,

    /// <summary>Slip (Alt-drag inside): shifts media content; the block stays in place.</summary>
    Slip,
}

/// <summary>Released block drag: new left and width, slip offset (pixels), ripple flag (Ctrl: later clips follow its end), and for a
/// roll the adjacent block as it is now.</summary>
public readonly record struct BlockEditDone(BlockEdit Kind, double Left, double Width, double Slip, bool Ripple, TimelineBlock? Neighbor);

/// <summary>Gain line edit: new gain (dB), or a fade handle's new length (seconds) at start or end.</summary>
public readonly record struct GainEditDone(double GainDb, double FadeIn, double FadeOut);

/// <summary>Parent edges a child block shares; trimming such an edge trims the child too.</summary>
[Flags]
public enum BlockEdges { None = 0, Start = 1, End = 2 }

/// <summary>
/// A block's clip-family relation, used to draw carried blocks during drags: <paramref name="Key"/> (stable across rebuilds, unique per
/// block), <paramref name="Parent"/> (parent's Key, or null), <paramref name="Rides"/> (parent moves carry it: overlaps on an unlocked
/// track, matching the model), <paramref name="OnParentEdges"/> (shared edges trimmed along). Computed by the host in seconds, not pixels.
/// </summary>
public readonly record struct BlockFamily(object Key, object? Parent, bool Rides, BlockEdges OnParentEdges);

/// <summary>Per-block switch identifier.</summary>
public enum BlockSwitch { OnOff, Lock }

/// <summary>One block of a Timeline row: a clip, cut segment, audio track part or subtitle.</summary>
public sealed class TimelineBlock
{
    public double Left { get; set; }
    public double Top { get; init; }
    public double Width { get; set; }
    public double Height { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
    public string Name { get; init; } = "";

    /// <summary>Model object the block represents (selected when the block is).</summary>
    public object? Clip { get; init; }

    /// <summary>Thumbnails from the left edge: image, x offset and width each.</summary>
    public IReadOnlyList<(ImageSource Picture, double Left, double Width)>? Frames { get; init; }

    /// <summary>Waveform image drawn over the thumbnails.</summary>
    public Brush? Wave { get; init; }

    /// <summary>Resolution-independent peaks drawn over thumbnails; overrides <see cref="Wave"/> when set.</summary>
    public WaveSpan? Peaks { get; init; }

    /// <summary>Keyframe x offsets from the left edge, in pixels.</summary>
    public IReadOnlyList<double>? Keys { get; init; }

    /// <summary>
    /// Detected beat x offsets (Detect &amp; Snap to Audio Beats) from the left edge like <see cref="Keys"/>, ascending; drawn as faint
    /// ticks along the bottom. Null for none.
    /// </summary>
    public IReadOnlyList<double>? Beats { get; init; }

    /// <summary>Whether the block can currently move; null if never movable.</summary>
    public Func<bool>? CanDrag { get; init; }

    /// <summary>Right edge is trimmable.</summary>
    public bool Resizable { get; init; }

    /// <summary>Left edge is trimmable (media in-point), and roll/ripple are allowed.</summary>
    public bool TrimmableStart { get; init; }

    /// <summary>Alt-drag slips its media (video or audio with a duration).</summary>
    public bool Slippable { get; init; }

    /// <summary>Commit callback for start trim, roll, slip or ripple; null when only move and end trim apply.</summary>
    public Action<BlockEditDone>? Edit { get; init; }

    /// <summary>
    /// Audio gain line: gain in dB (0 dB at two thirds of the height) plus fade in/out seconds; null when silent. Drags call
    /// <see cref="GainEdit"/>.
    /// </summary>
    public (double GainDb, double FadeIn, double FadeOut)? Gain { get; set; }

    /// <summary>Gain line commit callback.</summary>
    public Action<GainEditDone>? GainEdit { get; init; }

    /// <summary>
    /// Fade in/out lengths (timeline seconds) and styles: handles at the top corners (inset by each fade), dragged to set them; each fade
    /// is shaded above its curve. Null for none.
    /// </summary>
    public (double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle)? Fades { get; set; }

    /// <summary>Fade handle commit: fade in (true) or out, and new length in seconds.</summary>
    public Action<bool, double>? FadeEdit { get; init; }

    /// <summary>Crossfade length (seconds) from the previous block, drawn as a box centred on the cut.</summary>
    public double CrossfadeIn { get; set; }

    /// <summary>Crossfade commit: new length in seconds (0 = remove button). Null when not editable here.</summary>
    public Action<double>? CrossfadeEdit { get; init; }

    /// <summary>Pixels per media second (speed applied), used by slips and fades; 0 for none.</summary>
    public double PixelsPerMediaSecond { get; init; }

    /// <summary>Move/end-trim commit with the new left and width in pixels.</summary>
    public Action<double, double>? Commit { get; init; }

    /// <summary>Status text shown when a drag is refused.</summary>
    public string CannotDrag { get; init; } = "";

    /// <summary>May extend left of x = 0 (slipped audio).</summary>
    public bool MayStartBeforeZero { get; init; }

    /// <summary>
    /// Cross-row drop predicate given the target row's data and the block's left in pixels; null when the block stays on its row.
    /// </summary>
    public Func<object, double, bool>? CanMoveTo { get; init; }

    /// <summary>Cross-row drop commit given the target row's data and the block's left in pixels.</summary>
    public Action<object, double>? MoveTo { get; init; }

    /// <summary>
    /// New-track seam drop predicate ("+ New Video Track"): adjacent top-level video row's data, above (false = below), and the block's
    /// left in pixels. Null when the block never moves to a new track.
    /// </summary>
    public Func<object, bool, double, bool>? CanMoveToNewTrack { get; init; }

    /// <summary>New-track seam drop commit (arguments as <see cref="CanMoveToNewTrack"/>).</summary>
    public Action<object, bool, double>? MoveToNewTrack { get; init; }

    /// <summary>
    /// Tether predicate: may the block (left in pixels) become a child of the given block's clip (dropped on the bottom edge of an
    /// overlapped clip on another lane, or anywhere with Alt)? Returns the parent name for the pill (video blocks have empty Name), or
    /// null if not allowed. Null property: never tetherable.
    /// </summary>
    public Func<TimelineBlock, double, string?>? CanAttachTo { get; init; }

    /// <summary>Tether commit (see <see cref="CanAttachTo"/>): parent block and the block's left in pixels.</summary>
    public Action<TimelineBlock, double>? AttachTo { get; init; }

    /// <summary>Time-based waveform window; overrides <see cref="Peaks"/> when set.</summary>
    public WaveWindow? SoundWindow { get; init; }

    /// <summary>
    /// Combined waveform of a video block's audible audio-stack lanes (WaveformData.Combine, per-lane gain) in the block's file time,
    /// drawn translucent across the block. Set by the host after stacking; null when nothing is audible or there is no stack.
    /// </summary>
    public WaveWindow? StackSound { get; set; }

    /// <summary>File-time thumbnails; override <see cref="Frames"/> when set.</summary>
    public FilmWindow? Film { get; init; }

    /// <summary>Short top-right badge text (speed, "fx N"); null for none.</summary>
    public string? Badge { get; init; }

    /// <summary>
    /// Top-right On/Off for this clip only (a parent's does not affect children): true on, false off, null hidden. Raises
    /// <see cref="Switched"/>.
    /// </summary>
    public bool? IsOn { get; init; }

    /// <summary>On/Off glyph is a speaker (audio) rather than an eye (video).</summary>
    public bool SwitchIsSound { get; init; }

    /// <summary>Lock-to-parent switch beside On/Off (Cutouts, child clips, effects): true locked, false free, null hidden.</summary>
    public bool? IsLocked { get; init; }

    /// <summary>Callback for a block switch press.</summary>
    public Action<BlockSwitch>? Switched { get; init; }

    /// <summary>
    /// J/L cut in seconds: audio lead-in before the video (J; negative = later) and tail-out after it (L; negative = shorter). Drawn as a
    /// lower band extending past the block edges. Null for none or when the audio is not the video's own.
    /// </summary>
    public (double LeadIn, double TailOut)? AudioSplit { get; init; }

    /// <summary>
    /// J/L commit (Alt on an edge's lower half): new lead-in and tail-out in seconds. Null when the audio cannot be split.
    /// </summary>
    public Action<double, double>? AudioSplitEdit { get; init; }

    /// <summary>Family relation (drag carry set and identity); null when not in a family.</summary>
    public BlockFamily? Family { get; init; }

    /// <summary>
    /// Fold-aware trim query on edge press: for the given edge (true = start), the outward (unfold) and inward (fold) limits in seconds;
    /// null when the edge is at no dead-air seam.
    /// </summary>
    public Func<bool, (double Reveal, double Fold)?>? SeamRange { get; init; }

    /// <summary>Seam edge commit: edge (true = start) and outward travel in seconds (negative = inward).</summary>
    public Action<bool, double>? SeamTrim { get; init; }

    /// <summary>Drawn as a collapsed mini-stack lane; set by the unified timeline during layout, not by the host.</summary>
    public bool IsMini { get; internal set; }

    internal FormattedText? Label;

    // Unified-timeline render cache, valid while position is unchanged: badge text, keyframe diamonds and fade geometry (with the
    // rect they were built for), and the J/L audio band clip.
    internal FormattedText? BadgeText;
    internal Rect KeyShapeRect;
    internal Geometry? KeyShape;
    internal (Rect Rect, double In, double Out, Models.TransitionStyle InStyle, Models.TransitionStyle OutStyle) FadeKey;
    internal Geometry? FadeShade, FadeLine;
    internal Rect SoundClipRect;
    internal Geometry? SoundClip;

    // Geometry cache valid while position and size are unchanged (pans and playhead moves reuse it): rounded outline and keyframe diamonds.
    internal Rect ShapeRect;
    internal Geometry? Shape;
    internal Geometry? Diamonds;

    /// <summary>Rounded outline geometry for <paramref name="rect"/>, cached while the rect is unchanged.</summary>
    internal Geometry ShapeAt(Rect rect)
    {
        if (Shape is null || ShapeRect != rect)
        {
            // Width changed (end trim): rebuild diamonds for the new width.
            if (ShapeRect.Size != rect.Size)
                Diamonds = null;

            var shape = new RectangleGeometry(rect, 3, 3);
            shape.Freeze();
            (Shape, ShapeRect) = (shape, rect);
        }

        return Shape;
    }
}

/// <summary>
/// Shared Timeline metrics: edge and snap tolerances, gain line range, and the past-end shade.
/// </summary>
public static class TimelineMetrics
{
    /// <summary>Right-edge trim hit tolerance in pixels.</summary>
    public const double EdgePixels = 7;

    /// <summary>Gain line range: silence at the bottom, +12 dB at the top, 0 dB one third down.</summary>
    public const double LowestGain = -40, HighestGain = 12;

    /// <summary>Snap distance in pixels.</summary>
    public const double SnapPixels = 8;

    /// <summary>Shade for the area past the sequence end (drops or drags there extend the sequence).</summary>
    internal static readonly Brush PastEndShade = FrozenBrush(Color.FromArgb(90, 0, 0, 0));
    internal static readonly Pen EndLine = Frozen(new Pen(FrozenBrush(Color.FromArgb(150, 255, 255, 255)), 1));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

}

/// <summary>
/// Time ruler: ticks and labels for the visible range, shaded past-end area, markers and playhead. Press/drag seeks; wheel zooms about
/// the pointer; Shift+wheel or middle-drag pans with momentum (<see cref="MomentumScroller"/>: flicks and fast Shift+wheel coast); any
/// press, zoom or hiding stops the coast.
/// </summary>
public sealed class TimelineRuler : TimelineBar
{
    protected override string AutomationName => "Timeline ruler";

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Pen Tick = Frozen(new Pen(FrozenBrush(Color.FromArgb(150, 255, 255, 255)), 1));
    private static readonly Pen MinorTick = Frozen(new Pen(FrozenBrush(Color.FromArgb(80, 255, 255, 255)), 1));
    private static readonly Brush LabelBrush = FrozenBrush(Color.FromArgb(200, 255, 255, 255));

    // Major tick steps in seconds, each with its minor tick step.
    private static readonly (double Major, double Minor)[] Steps =
    [
        (0.05, 0.01), (0.1, 0.02), (0.2, 0.05), (0.5, 0.1), (1, 0.2), (2, 0.5), (5, 1), (10, 2), (15, 5), (30, 5), (60, 10),
        (120, 30), (300, 60), (600, 120), (900, 300), (1800, 300), (3600, 600), (7200, 1800), (14400, 3600),
    ];

    private double _start, _span, _sequence;
    private double? _panFrom;
    private bool _seeking;

    // Momentum scroller (created on first use), middle-drag pan since press, and whether the frame hook is attached.
    private MomentumScroller? _momentum;
    private double _panTravel;
    private bool _coastHooked;

    private MomentumScroller Momentum => _momentum ??= new MomentumScroller();

    public TimelineRuler()
    {
        Unloaded += (_, _) => StopMomentum();
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
                StopMomentum();
        };
    }
    private IReadOnlyList<double> _markers = [], _highlights = [];

    /// <summary>Marker colour (ruler and Sequence Overview Scrubber).</summary>
    public static readonly Brush MarkerBrush = FrozenBrush(Color.FromRgb(0x7C, 0xD9, 0x92));

    /// <summary>Ping-to-Mark highlight colour (TimelineMarker.Highlight).</summary>
    public static readonly Brush HighlightBrush = FrozenBrush(Color.FromRgb(0xFF, 0xB3, 0x47));

    /// <summary>Downward-pointing marker flag geometry, 9 px wide.</summary>
    public static readonly Geometry MarkerFlag = Frozen(Geometry.Parse("M -4.5,0 L 4.5,0 L 4.5,6 L 0,10 L -4.5,6 Z"));

    /// <summary>Marker right-clicked: its index in the shown list.</summary>
    public event Action<int>? MarkerMenuRequested;

    /// <summary>Seek request from a ruler press or drag (seconds).</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Seek drag ended (release or capture loss); the host commits the deferred playhead value.</summary>
    public event Action? SeekEnded;

    /// <summary>Wheel zoom: factor and anchor (pixels from the left).</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Horizontal pan in pixels (Shift+wheel or middle-drag).</summary>
    public event Action<double>? PanRequested;

    /// <summary>
    /// Pointer time over the ruler (seconds, >= 0; also during seek drags) for Instant Seek hints; null on leave. Raised only on change;
    /// allocation-free.
    /// </summary>
    public event Action<double?>? PointerSecondsChanged;

    // Last raised pointer time (PointerSecondsChanged), NaN for none.
    private double _pointerSeconds = double.NaN;

    private void NotePointer(double seconds)
    {
        if (seconds.Equals(_pointerSeconds))
            return;

        var was = _pointerSeconds;
        _pointerSeconds = seconds;
        if (!double.IsNaN(seconds))
            PointerSecondsChanged?.Invoke(seconds);
        else if (!double.IsNaN(was))
            PointerSecondsChanged?.Invoke(null);
    }

    /// <summary>
    /// Sets the visible range (<paramref name="start"/>, <paramref name="span"/>) of a <paramref name="sequence"/>-second sequence, plus
    /// marker times; Ping-to-Mark highlights use their own colour.
    /// </summary>
    public void Show(double start, double span, double sequence, IReadOnlyList<double> markers, IReadOnlyList<double>? highlights = null)
    {
        highlights ??= [];
        if (start == _start && span == _span && sequence == _sequence && markers.SequenceEqual(_markers) && highlights.SequenceEqual(_highlights))
            return;

        // Zoom or fit: stop any coast on the old view.
        if (span != _span)
            StopMomentum();

        (_start, _span, _sequence, _markers, _highlights) = (start, span, sequence, [.. markers], [.. highlights]);
        InvalidateVisual();
    }

    /// <summary>Index of the marker within a few pixels of x; -1 for none.</summary>
    private int MarkerNear(double x)
    {
        var (best, nearest) = (-1, 6.0);
        for (var i = 0; i < _markers.Count && ActualWidth > 0 && _span > 0; i++)
        {
            var at = (_markers[i] - _start) / _span * ActualWidth;
            if (Math.Abs(at - x) < nearest)
                (best, nearest) = (i, Math.Abs(at - x));
        }

        return best;
    }

    /// <summary>Time in seconds at x.</summary>
    public double TimeAt(double x) => ActualWidth > 0 ? _start + x / ActualWidth * _span : 0;

    // FormattedText cache by label text (zooms and pans mostly redraw the same labels).
    private readonly Dictionary<string, FormattedText> _labels = [];
    private double _labelsDpi;

    private FormattedText Label(string text, double dpi)
    {
        if (dpi != _labelsDpi || _labels.Count > 256)
        {
            _labelsDpi = dpi;
            _labels.Clear();
        }

        if (!_labels.TryGetValue(text, out var label))
            _labels[text] = label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, LabelBrush, dpi);
        return label;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        DrawBackground(drawing);
        if (_span <= 0 || ActualWidth <= 0)
            return;

        var scale = ActualWidth / _span;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var end = (_sequence - _start) * scale;
        if (end < ActualWidth)
        {
            drawing.DrawRectangle(TimelineMetrics.PastEndShade, null, new Rect(Math.Max(end, 0), 0, ActualWidth - Math.Max(end, 0), ActualHeight));
            if (end >= 0)
                drawing.DrawLine(TimelineMetrics.EndLine, new Point(end + 0.5, 0), new Point(end + 0.5, ActualHeight));
        }

        // Minimum label spacing so labels never overlap.
        var (major, minor) = Steps.FirstOrDefault(step => step.Major * scale >= 84, Steps[^1]);
        var bottom = ActualHeight;
        var first = Math.Floor(_start / minor) * minor;
        for (var i = 0; ; i++)
        {
            var time = first + i * minor;
            var x = (time - _start) * scale;
            if (x > ActualWidth)
                break;
            if (x < -1)
                continue;

            var isMajor = Math.Abs(time / major - Math.Round(time / major)) < 1e-6;
            var top = isMajor ? bottom - 9 : bottom - 4;
            drawing.DrawLine(isMajor ? Tick : MinorTick, new Point(Math.Round(x) + 0.5, top), new Point(Math.Round(x) + 0.5, bottom));
            if (isMajor && time >= -1e-6)
            {
                var label = Label(Format(Math.Max(time, 0), major), dpi);
                drawing.DrawText(label, new Point(Math.Round(x) + 3, Math.Max(bottom - 9 - label.Height, 0)));
            }
        }

        // Marker flags along the top.
        foreach (var marker in _markers)
        {
            var x = (marker - _start) * scale;
            if (x < -6 || x > ActualWidth + 6)
                continue;

            drawing.PushTransform(new TranslateTransform(Math.Round(x) + 0.5, 0));
            drawing.DrawGeometry(_highlights.Contains(marker) ? HighlightBrush : MarkerBrush, null, MarkerFlag);
            drawing.Pop();
        }
    }

    /// <summary>Formats a ruler label with the precision the step needs (m:ss, with hours when non-zero).</summary>
    private static string Format(double seconds, double step)
    {
        var whole = (long)Math.Floor(seconds + 1e-6);
        var (hours, minutes, rest) = (whole / 3600, whole / 60 % 60, whole % 60);
        var text = hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
        if (step >= 1)
            return text;

        var fraction = seconds - whole;
        return step >= 0.1
            ? text + string.Create(CultureInfo.InvariantCulture, $".{(int)Math.Round(fraction * 10) % 10}")
            : text + string.Create(CultureInfo.InvariantCulture, $".{(int)Math.Round(fraction * 100) % 100:00}");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CaptureMouse())
            return;

        // Snap exactly to a marker under the pointer.
        _seeking = true;
        var x = e.GetPosition(this).X;
        SeekRequested?.Invoke(MarkerNear(x) is >= 0 and var marker ? _markers[marker] : TimeAt(x));
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (MarkerNear(e.GetPosition(this).X) is >= 0 and var marker)
        {
            MarkerMenuRequested?.Invoke(marker);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_seeking)
            return;

        _seeking = false;
        ReleaseMouseCapture();
        SeekEnded?.Invoke();
        e.Handled = true;
    }

    // Any ruler press first stops a coast in place.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        StopMomentum();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && CaptureMouse())
        {
            MiddlePanPress(e.GetPosition(this).X, MomentumScroller.Now());
            Cursor = Cursors.ScrollWE;
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panFrom is not null)
        {
            // Release capture first (capture loss stops coasts), then start the flick coast if any.
            var now = MomentumScroller.Now();
            _panFrom = null;
            Cursor = null;
            ReleaseMouseCapture();
            MiddlePanRelease(now);
            e.Handled = true;
        }
    }

    /// <summary>Test seam: middle press at <paramref name="x"/> stops any coast and starts a pan.</summary>
    internal void MiddlePanPress(double x, double nowMs)
    {
        _panFrom = x;
        _panTravel = 0;
        Momentum.Press(nowMs);
        Momentum.Sample(0, nowMs);
    }

    /// <summary>Test seam: middle-drag to <paramref name="x"/> pans with the pointer.</summary>
    internal void MiddlePanMove(double x, double nowMs)
    {
        if (_panFrom is not { } from)
            return;

        _panFrom = x;
        _panTravel += from - x;
        Momentum.Sample(_panTravel, nowMs);
        PanRequested?.Invoke(from - x);
    }

    /// <summary>Test seam: middle release; a flick (>= 250 DIP/s, moving within the last 40 ms) starts a coast.</summary>
    internal void MiddlePanRelease(double nowMs)
    {
        _panFrom = null;
        if (Momentum.Release(nowMs) != 0)
            HookCoast();
    }

    /// <summary>
    /// Test seam (called by <see cref="OnMouseWheel"/>): Shift+wheel pan of <paramref name="pixels"/> (positive = later) at
    /// <paramref name="nowMs"/>; fast streams coast, an opposing tick stops the coast.
    /// </summary>
    internal void PanWheelAt(double pixels, double nowMs)
    {
        var (before, step) = (_start, Momentum.Wheel(pixels, nowMs));
        if (step != 0)
            PanRequested?.Invoke(step);
        if (step != 0 && _start == before)
            Momentum.Stop();
        if (Momentum.IsCoasting)
            HookCoast();
    }

    /// <summary>Test seam (called by the frame hook): advances the coast to <paramref name="nowMs"/> and pans (host applies synchronously);
    /// stops when the view did not move (end reached, fitted). Returns whether it moved.</summary>
    internal bool StepCoast(double nowMs)
    {
        var moved = false;
        if (_momentum is { IsCoasting: true } momentum)
        {
            var pixels = momentum.Tick(nowMs);
            if (pixels != 0)
            {
                var before = _start;
                PanRequested?.Invoke(pixels);
                if (_start == before)
                    momentum.Stop();
                else
                    moved = true;
            }
        }

        if (_momentum?.IsCoasting != true)
            UnhookCoast();
        return moved;
    }

    /// <summary>True while a coast started by this ruler is active.</summary>
    public bool IsCoasting => _momentum?.IsCoasting == true;

    /// <summary>Stops this ruler's coast in place (press elsewhere on the timeline, playhead page turn).</summary>
    public void StopMomentum()
    {
        _momentum?.Stop();
        UnhookCoast();
    }

    // Frame hook attached only while coasting and visible; tests step the coast directly.
    private void HookCoast()
    {
        if (_coastHooked || !IsVisible)
            return;

        _coastHooked = true;
        CompositionTarget.Rendering += OnCoastFrame;
    }

    private void UnhookCoast()
    {
        if (!_coastHooked)
            return;

        _coastHooked = false;
        CompositionTarget.Rendering -= OnCoastFrame;
    }

    private void OnCoastFrame(object? sender, EventArgs e) => StepCoast(MomentumScroller.Now());

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this).X;
        NotePointer(ActualWidth > 0 && _span > 0 ? Math.Max(TimeAt(Math.Clamp(at, 0, ActualWidth)), 0) : double.NaN);
        if (_panFrom is not null)
        {
            MiddlePanMove(at, MomentumScroller.Now());
        }
        else if (_seeking)
        {
            SeekRequested?.Invoke(TimeAt(Math.Clamp(at, 0, ActualWidth)));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture lost mid middle-drag: not a flick, no coast.
        if (_panFrom is not null)
            StopMomentum();
        var wasSeeking = _seeking;
        (_seeking, _panFrom) = (false, null);
        if (wasSeeking)
            SeekEnded?.Invoke();

        // Seek drag released outside: clear the pointer time.
        if (!IsMouseOver)
            NotePointer(double.NaN);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured)
            NotePointer(double.NaN);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanWheelAt(-e.Delta, MomentumScroller.Now());
        }
        else
        {
            // Glide the zoom (WheelGlide) with the time under the pointer pinned.
            StopMomentum();
            _wheelGlide ??= new(_ => { }, (factor, at) => ZoomRequested?.Invoke(factor, at), () => IsVisible);
            _wheelGlide.Zoom(Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(this).X);
        }

        e.Handled = true;
    }

    private WheelGlide? _wheelGlide;

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
