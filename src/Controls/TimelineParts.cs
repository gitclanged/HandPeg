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

        // Scrub hot path: redraw only on a height change or the first hide; otherwise translate the existing visual.
        if (shown ? height != _playheadHeight : _playheadHeight != 0)
        {
            _playheadHeight = shown ? height : 0;
            using var drawing = _playhead.RenderOpen();
            if (shown)
                drawing.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, 1.5, height));
        }

        if (shown)
            _playhead.Offset = new Vector(_playheadX, 0);
        if (_pointer is not null)
            PlacePointer();
    }

    // ----- Cursor-Locked Scrub Pointer (Pass 5.7.1) -----
    // While the OS cursor is hidden for a playhead drag, the pointer is drawn as a child of the playhead visual: it moves with the line by
    // the same translation, so the two never part (0 px) however fast the display refreshes. Built on first use; NaN hides it.

    private DrawingVisual? _pointer;
    private double _pointerY = double.NaN;

    /// <summary>The scrub pointer's tip, DIP from the bar's top (clamped to the bar); NaN hides it.</summary>
    public double PointerY
    {
        get => _pointerY;
        set
        {
            if (value.Equals(_pointerY))
                return;

            _pointerY = value;
            PlacePointer();
        }
    }

    private void PlacePointer()
    {
        if (double.IsNaN(_pointerY) || double.IsNaN(_playheadX))
        {
            if (_pointer is not null)
                _pointer.Opacity = 0;
            return;
        }

        if (_pointer is null)
        {
            _pointer = new DrawingVisual();
            using (var drawing = _pointer.RenderOpen())
                DrawScrubPointer(drawing);
            _playhead.Children.Add(_pointer);
        }

        // The bar's playhead is drawn from 0 to 1.5 DIP: the pointer's tip sits on its centre line.
        _pointer.Opacity = 1;
        _pointer.Offset = new Vector(0.75, Math.Clamp(_pointerY, 0, Math.Max(ActualHeight, 4)));
    }

    /// <summary>
    /// The scrub pointer (ruler and track surface): an arrow whose tip is the hidden cursor's hot spot, on the playhead's centre line,
    /// white with a dark rim so it reads on any track colour.
    /// </summary>
    internal static void DrawScrubPointer(DrawingContext drawing) => drawing.DrawGeometry(Brushes.White, ScrubPointerRim, ScrubPointerShape);

    private static readonly Pen ScrubPointerRim = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(230, 16, 16, 18)), 1) { LineJoin = PenLineJoin.Miter });

    private static readonly Geometry ScrubPointerShape = Frozen(Geometry.Parse("M 0,0 L 5.5,7 L 2,7 L 2,14 L -2,14 L -2,7 L -5.5,7 Z"));

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
/// slide under slips. A Cutout's <paramref name="Crop"/> (Pass 5.6.4A) is the source region [UMin, VMin, UMax, VMax] its frames were
/// cut to from the parent file's shared strip (MainWindow.FilmOf: at least 16 x 16 strip pixels, aspect kept), so DrawFilm draws them as
/// they are and slots take the region's shape; null for whole frames.
/// </summary>
public readonly record struct FilmWindow(IReadOnlyList<ImageSource> Frames, double SecondsPerFrame, double MediaFrom, double Speed, FilmCrop? Crop = null);

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

/// <summary>What a block's inline quick-edit icons act on: none, its effect (effect lane), its effects ("fx N" badge), its crossfade.</summary>
public enum QuickEditTarget { None, Effect, Effects, Transition }

/// <summary>Inline quick-edit verbs, in drawn order.</summary>
public enum QuickEditVerb { Edit, Keyframes, Toggle, Remove }

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

    /// <summary>
    /// A Cutout block's source region (normalized [UMin, VMin, UMax, VMax]: its cropped source rect intersected with its mask outline's
    /// bounds, MainWindow.CutoutFilmCrop) that its <see cref="Film"/> frames show; null for whole-frame blocks. Read from the film, so the
    /// region and the frames cut to it never disagree.
    /// </summary>
    public FilmCrop? FilmCrop => Film?.Crop;

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

    /// <summary>
    /// Unused media beyond its edges in timeline seconds (before the start, after the end): drawn as 25%-opacity Ghost Trim Tails while
    /// an edge is trimmed; 0 for none or unbounded (stills).
    /// </summary>
    public double HeadRoom { get; init; }

    /// <summary>See <see cref="HeadRoom"/>: media left after the end, in timeline seconds.</summary>
    public double TailRoom { get; init; }

    /// <summary>
    /// Ducked sound: the hand-drawn ducking keys (dB at clip-relative seconds, Layer.DuckOverrideKeys) and the automatic depth (dB) drawn
    /// dashed where no keys rule. The amber curve shows on hover; null when the clip is not ducked.
    /// </summary>
    public (IReadOnlyList<Models.Keyframe> Keys, double AutoDb)? Duck { get; init; }

    /// <summary>Ducking curve edit: the new keys (empty = reset to automatic) and whether the gesture ended (false = live).</summary>
    public Action<IReadOnlyList<Models.Keyframe>, bool>? DuckEdit { get; init; }

    /// <summary>Double-click on the gain line: auto-level the clip to -14 LUFS. Null when not offered.</summary>
    public Action? AutoLevel { get; init; }

    /// <summary>What the inline quick-edit icons act on when this block is hovered (effect lane block, or a clip's "fx N" badge).</summary>
    public QuickEditTarget QuickEditOf { get; init; }

    /// <summary>Inline quick-edit icon pressed: the target (Effect, Effects badge, or Transition = its crossfade) and the verb.</summary>
    public Action<QuickEditTarget, QuickEditVerb>? QuickEdit { get; init; }

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
        // Hidden or unloaded mid-drag (tab switch, pane closed): the drag ends where it is and the cursor comes back.
        Unloaded += (_, _) =>
        {
            StopMomentum();
            CancelSeek();
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                return;
            StopMomentum();
            CancelSeek();
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

    /// <summary>
    /// True when a pointer move reports its time (skim line, hover hints): never during a seek drag, whose 500 Hz moves only move the
    /// playhead (the skim line is cleared at the press and placed again at the release). Pure; checked by goldens.
    /// </summary>
    internal static bool NotesPointer(bool seeking) => !seeking;

    // Pointer time at x (clamped to the ruler), NaN before the first layout.
    private double PointerTimeAt(double x) => ActualWidth > 0 && _span > 0 ? Math.Max(TimeAt(Math.Clamp(x, 0, ActualWidth)), 0) : double.NaN;

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

        // Snap exactly to a marker under the pointer. The skim line steps aside for the drag (NotesPointer). The press seeks at once;
        // the drag's moves are then sampled once per rendered frame (BeginScrubSampling).
        _seeking = true;
        NotePointer(double.NaN);
        var point = e.GetPosition(this);
        _sampledX = Math.Clamp(point.X, 0, ActualWidth);
        (_movedX, _lastScrubMoveTicks) = (point.X, System.Diagnostics.Stopwatch.GetTimestamp());
        SeekRequested?.Invoke(MarkerNear(point.X) is >= 0 and var marker ? _markers[marker] : TimeAt(_sampledX));
        BeginScrubSampling(point.Y);
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

        // The release lands exactly where the button came up (the frame sampler may not have seen that position yet).
        SeekToSample(e.GetPosition(this).X);
        _seeking = false;
        EndScrubSampling();
        ReleaseMouseCapture();
        SeekEnded?.Invoke();
        if (IsMouseOver)
            NotePointer(PointerTimeAt(e.GetPosition(this).X));
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
        if (NotesPointer(_seeking))
            NotePointer(PointerTimeAt(at));
        if (_panFrom is not null)
        {
            MiddlePanMove(at, MomentumScroller.Now());
        }
        else if (_seeking)
        {
            // A seek drag's raw move (up to 1000 Hz) is only stamped: the frame sampler seeks once per rendered frame from the live cursor.
            (_movedX, _lastScrubMoveTicks) = (at, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture lost mid middle-drag: not a flick, no coast. Lost mid seek drag (window deactivated, Esc, a dialog): the cursor returns.
        if (_panFrom is not null)
            StopMomentum();
        EndScrubSampling();
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

    // ----- Frame-sampled seek drag and Cursor-Locked Scrub Pointer (Pass 5.7.1) -----
    // A seek drag's raw WM_MOUSEMOVEs (up to 1000 Hz) are only stamped (_lastScrubMoveTicks). Once per rendered frame
    // (CompositionTarget.Rendering) the live cursor (GetCursorPos) is mapped straight to ruler x and seeks, so the playhead follows the
    // freshest position at the display's rate and nothing downstream runs at the pointer's rate. The host samples first in its own frame
    // callback (SampleScrubFrame, before it takes the frame's one engine seek), so frame N's seek is frame N's cursor; this ruler's own hook
    // then skips that frame (one sample per RenderingTime) and samples only when no host does. With Settings > Cursor-locked scrub
    // pointer on (off by default, user decision Q1) the OS cursor is hidden for the drag and the playhead visual draws the pointer; it
    // always comes back on release, capture loss (window deactivated, a dialog), Esc, hiding, unloading or a fault.

    private bool _scrubHooked, _cursorHidden;
    private double _sampledX = double.NaN, _movedX = double.NaN;
    private long _lastScrubMoveTicks;
    private object? _cursorBefore;

    // RenderingTime of the frame last sampled: whichever render callback asks first samples it, the other skips.
    private TimeSpan _sampledFrame = TimeSpan.MinValue;

    /// <summary>
    /// Raised once per rendered frame of a seek drag with the live cursor's screen point (real pixels): the track surface draws the
    /// scrub pointer when the cursor is over it (<see cref="ScrubPointerElsewhere"/>).
    /// </summary>
    public event Action<Point>? ScrubSampled;

    /// <summary>
    /// Asked once per frame while the scrub pointer is locked: true when another element (the track surface) draws the pointer for this
    /// screen point. Outside the ruler and that element the OS cursor shows again, so the pointer is never lost.
    /// </summary>
    public Func<Point, bool>? ScrubPointerElsewhere { get; set; }

    /// <summary>True while this drag's scrub pointer is locked (the setting was on at its press).</summary>
    public bool PointerLocked { get; private set; }

    /// <summary>Stopwatch ticks of the seek drag's last raw pointer move (0 before the first); stamped, never acted on per move.</summary>
    internal long LastScrubMoveTicks => _lastScrubMoveTicks;

    /// <summary>True while the frame sampler is hooked (a seek drag is held).</summary>
    internal bool IsSampling => _scrubHooked;

    /// <summary>
    /// The x a sample seeks to, clamped to the ruler, or NaN when it seeks nothing: the same x as the last sample (a still pointer wakes
    /// nothing downstream) or no layout yet. Pure; goldens.
    /// </summary>
    internal static double SampleTarget(double x, double lastX, double width) =>
        !(width > 0) || !double.IsFinite(x) ? double.NaN : Math.Clamp(x, 0, width) is var clamped && clamped.Equals(lastX) ? double.NaN : clamped;

    /// <summary>
    /// Whether the OS cursor is hidden for this frame of a locked drag: only while the pointer is drawn, over the ruler
    /// (<paramref name="overRuler"/>) or by another element (<paramref name="drawnElsewhere"/>). Pure; goldens.
    /// </summary>
    internal static bool HidesCursor(bool locked, bool overRuler, bool drawnElsewhere) => locked && (overRuler || drawnElsewhere);

    /// <summary>
    /// True when a render callback samples this frame: not yet sampled (<paramref name="renderingTime"/> differs from the last sampled
    /// frame's), or no frame time (a direct call). WPF raises Rendering to every handler, sometimes twice, with the same time. Pure; goldens.
    /// </summary>
    internal static bool SamplesFrame(TimeSpan? renderingTime, TimeSpan lastSampled) => renderingTime is not { } time || time != lastSampled;

    /// <summary>
    /// Samples this frame's live cursor now, from the host's frame callback before it takes the frame's one engine seek, so the seek sent
    /// on frame N is frame N's pointer (this ruler's own hook then skips the frame). Nothing unless a seek drag is held.
    /// </summary>
    internal void SampleScrubFrame(EventArgs e)
    {
        if (_scrubHooked)
            OnScrubFrame(this, e);
    }

    private void BeginScrubSampling(double pointerY)
    {
        PointerLocked = HandPegApp.Services.AppSettings.Current.CursorLockedScrubPointer;
        if (PointerLocked)
            ShowPointer(pointerY, overRuler: pointerY >= 0 && pointerY <= ActualHeight, drawnElsewhere: false);
        if (_scrubHooked)
            return;

        _scrubHooked = true;
        _sampledFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnScrubFrame;
    }

    /// <summary>Unhooks the sampler and gives the cursor back (idempotent: every end path calls it).</summary>
    private void EndScrubSampling()
    {
        if (_scrubHooked)
        {
            _scrubHooked = false;
            CompositionTarget.Rendering -= OnScrubFrame;
        }

        PointerLocked = false;
        ShowPointer(double.NaN, overRuler: false, drawnElsewhere: false);
    }

    /// <summary>Ends a held seek drag where it is (Esc, hiding, unloading, a fault): capture loss raises <see cref="SeekEnded"/>.</summary>
    internal void CancelSeek()
    {
        if (!_seeking)
        {
            EndScrubSampling();
            return;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            return;
        }

        _seeking = false;
        EndScrubSampling();
        SeekEnded?.Invoke();
    }

    private void OnScrubFrame(object? sender, EventArgs e)
    {
        // One sample per rendered frame: the host's callback (SampleScrubFrame) usually took this one already.
        var renderingTime = (e as RenderingEventArgs)?.RenderingTime;
        if (!SamplesFrame(renderingTime, _sampledFrame))
            return;
        if (renderingTime is { } time)
            _sampledFrame = time;

        try
        {
            // Capture gone without notice, or Esc (the ruler takes no focus, so the key is read here): the drag ends where it is.
            if (!_seeking || !IsMouseCaptured || Keyboard.IsKeyDown(Key.Escape))
            {
                CancelSeek();
                return;
            }

            // The live cursor; the last raw move's x when the cursor cannot be read (a secure desktop) or the ruler has no window.
            var screen = new Point(double.NaN, double.NaN);
            var local = new Point(_movedX, double.NaN);
            if (PresentationSource.FromVisual(this) is not null && Native.User32.GetCursorPos(out var cursor))
            {
                screen = new Point(cursor.X, cursor.Y);
                local = PointFromScreen(screen);
            }

            SeekToSample(local.X);
            if (double.IsNaN(screen.X))
                return;

            ScrubSampled?.Invoke(screen);
            if (PointerLocked)
            {
                // Asked every frame, so the tracks also hide their pointer once the cursor is back over the ruler.
                var overRuler = local.Y >= 0 && local.Y <= ActualHeight;
                var elsewhere = ScrubPointerElsewhere?.Invoke(screen) == true;
                ShowPointer(local.Y, overRuler, elsewhere && !overRuler);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave the cursor hidden or the drag stuck behind a fault.
            HandPegApp.Services.AppLog.Write("Timeline ruler: a scrub frame failed", ex);
            CancelSeek();
        }
    }

    // One seek per changed x (SeekRequested; the host coalesces the engine seek to once per frame as well).
    private void SeekToSample(double x)
    {
        var target = SampleTarget(x, _sampledX, ActualWidth);
        if (double.IsNaN(target))
            return;

        _sampledX = target;
        SeekRequested?.Invoke(TimeAt(target));
    }

    // Draws the ruler's pointer (over the ruler only) and hides or restores the OS cursor to match.
    private void ShowPointer(double y, bool overRuler, bool drawnElsewhere)
    {
        PointerY = PointerLocked && overRuler ? y : double.NaN;
        var hide = HidesCursor(PointerLocked, overRuler, drawnElsewhere);
        if (hide == _cursorHidden)
            return;

        _cursorHidden = hide;
        if (hide)
        {
            _cursorBefore = ReadLocalValue(CursorProperty);
            Cursor = Cursors.None;
        }
        else
        {
            if (_cursorBefore is Cursor before)
                Cursor = before;
            else
                ClearValue(CursorProperty);
            _cursorBefore = null;
        }

        Mouse.UpdateCursor();
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
