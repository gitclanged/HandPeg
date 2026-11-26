using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Slice-Preview ribbon (Pass 5.7.1) -----
// A 5-7 frame micro-filmstrip floating over the tracks at the playhead while it is scrubbed slowly over a video clip (never over
// audio-only or image clips, user decision Q3). Dormant during fast whips (above SlicePreviewRequest.DormantAbove DIP/s: no lookup, no
// request, no drawing); it fades in once the playhead has moved below WakeBelow DIP/s for WakeAfterMs. Its frames come from the
// compositor's frame cache (GpuPreviewEngine.RequestSlicePreview -> the shared-memory slice burst -> a strip read back once decoded).
// The element stretches over the tracks' top and never resizes: the strip is a translated child visual, so showing, moving and filling it
// costs no layout pass (zero layout shift).

/// <summary>Where the ribbon's velocity gate is: asleep, waiting out <see cref="SlicePreviewRequest.WakeAfterMs"/>, or shown.</summary>
public enum SlicePreviewPhase
{
    Dormant,
    Arming,
    Shown,
}

/// <summary>
/// The ribbon's velocity gate (pure; goldens): above <see cref="SlicePreviewRequest.DormantAbove"/> DIP/s, or off a video clip, it
/// sleeps at once; below <see cref="SlicePreviewRequest.WakeBelow"/> held for <see cref="SlicePreviewRequest.WakeAfterMs"/> it shows;
/// once shown, the band between the two keeps it (hysteresis).
/// </summary>
public sealed class SlicePreviewGate
{
    private double _armedAt = double.NaN;

    public SlicePreviewPhase Phase { get; private set; }

    /// <summary>One frame: the playhead's speed (DIP/s), the time (ms) and whether a video clip is under it.</summary>
    public SlicePreviewPhase Update(double velocity, double nowMs, bool overVideo)
    {
        if (!overVideo || !(velocity <= SlicePreviewRequest.DormantAbove))
            return Sleep();
        if (Phase == SlicePreviewPhase.Shown)
            return Phase;
        if (velocity >= SlicePreviewRequest.WakeBelow)
            return Sleep();

        if (Phase == SlicePreviewPhase.Dormant)
            (Phase, _armedAt) = (SlicePreviewPhase.Arming, nowMs);
        if (nowMs - _armedAt >= SlicePreviewRequest.WakeAfterMs)
            Phase = SlicePreviewPhase.Shown;
        return Phase;
    }

    /// <summary>Asleep at once (release, playback, a fast whip).</summary>
    public SlicePreviewPhase Sleep()
    {
        (Phase, _armedAt) = (SlicePreviewPhase.Dormant, double.NaN);
        return Phase;
    }
}

/// <summary>What the ribbon shows where (pure; goldens): its spacing for the zoom, its slot count for the room, and its request.</summary>
public static class SlicePreviewPlanner
{
    /// <summary>Timeline DIPs one slot step stands for: the spacing is the time 16 DIP of the tracks covers at the zoom, rounded.</summary>
    public const double SlotTimelineDip = 16;

    /// <summary>Seconds spacings past three frames, smallest first.</summary>
    public static readonly double[] SecondSteps = [0.25, 0.5, 1, 2, 5, 10, 30, 60];

    /// <summary>The ribbon's cell height, DIP (its width follows the Sequence's shape).</summary>
    public const double CellHeight = 40;

    /// <summary>
    /// The spacing at <paramref name="pixelsPerSecond"/> (the tracks' zoom): whole frames (1, 2 or 3) when zoomed in deep, else the
    /// smallest of <see cref="SecondSteps"/> covering <see cref="SlotTimelineDip"/>; unusable without a zoom.
    /// </summary>
    public static SlicePreviewFrameSpacing SpacingFor(double pixelsPerSecond, double frameRate)
    {
        if (!(pixelsPerSecond > 0) || !double.IsFinite(pixelsPerSecond))
            return default;

        var raw = SlotTimelineDip / pixelsPerSecond;
        if (frameRate > 0 && double.IsFinite(frameRate) && raw * frameRate <= 4.5)
        {
            var frames = raw * frameRate;
            return SlicePreviewFrameSpacing.OfFrames(frames <= 1.5 ? 1 : frames <= 2.5 ? 2 : 3, frameRate);
        }

        foreach (var step in SecondSteps)
        {
            if (step >= raw)
                return SlicePreviewFrameSpacing.OfSeconds(step);
        }

        return SlicePreviewFrameSpacing.OfSeconds(SecondSteps[^1]);
    }

    /// <summary>Seven slots when the time area holds them with room to spare, else five.</summary>
    public static int SlotCountFor(double timeAreaWidth, double cellWidth) =>
        cellWidth > 0 && timeAreaWidth >= (SlicePreviewRequest.MaxSlots + 2) * cellWidth ? SlicePreviewRequest.MaxSlots : SlicePreviewRequest.MinSlots;

    /// <summary>
    /// The topmost visible video clip at <paramref name="seconds"/> (hidden tracks and clips skipped; audio-only and image clips never
    /// count, user decision Q3), or null.
    /// </summary>
    public static TimelineClip? VideoClipAt(TimelineDocument document, double seconds)
    {
        for (var t = document.Tracks.Count - 1; t >= 0; t--)
        {
            var track = document.Tracks[t];
            if (document.IsHiddenWithParents(track))
                continue;

            foreach (var clip in track.Clips)
            {
                if (clip.Start <= seconds && seconds < clip.End && !clip.IsHidden && clip.ContentKind == LayerKind.VideoFile
                    && !string.IsNullOrWhiteSpace(clip.SourcePath))
                {
                    return clip;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The request for <paramref name="count"/> slots (odd, centred on <paramref name="seconds"/>) of the video clip under the playhead
    /// (<see cref="Plan(TimelineClip, double, SlicePreviewFrameSpacing, int, int)"/>). Null when no video clip is there or the spacing is
    /// unusable.
    /// </summary>
    public static SlicePreviewRequest? Plan(TimelineDocument document, double seconds, SlicePreviewFrameSpacing spacing, int count, int shortSide) =>
        spacing.IsUsable && double.IsFinite(seconds) && VideoClipAt(document, seconds) is { } clip ? Plan(clip, seconds, spacing, count, shortSide) : null;

    /// <summary>
    /// The request for <paramref name="count"/> slots of <paramref name="clip"/> (the video clip <see cref="VideoClipAt"/> found under the
    /// playhead) centred on <paramref name="seconds"/>: every slot, so one file and one centred run of offsets (a valid request), a slot
    /// past the clip's edge holding its edge frame; each with its file time. Null when the spacing is unusable.
    /// </summary>
    public static SlicePreviewRequest? Plan(TimelineClip clip, double seconds, SlicePreviewFrameSpacing spacing, int count, int shortSide)
    {
        if (!spacing.IsUsable || !double.IsFinite(seconds) || string.IsNullOrWhiteSpace(clip.SourcePath) || !(clip.End > clip.Start))
            return null;

        var half = Math.Clamp(count, SlicePreviewRequest.MinSlots, SlicePreviewRequest.MaxSlots) / 2;

        // The player's own mapping (GpuScenePlan.Into, Layer.MediaSecondsAt): a keyframed ramp's integral (keys normalized as the clip's
        // layer holds them), else the constant speed; so every slot, the playhead's included, asks for the frame the player shows there.
        IReadOnlyList<Keyframe> ramp = clip.State?.KeysSpeed is { Count: > 0 } keys ? SpeedRamp.Normalized(keys) : [];
        var slots = new List<SlicePreviewSlot>(2 * half + 1);
        var last = Math.Max(clip.Start, clip.End - 1e-6);
        for (var offset = -half; offset <= half; offset++)
        {
            var at = Math.Clamp(seconds + offset * spacing.Seconds, clip.Start, last);
            var media = clip.MediaOffset + Math.Max(SpeedRamp.MediaSeconds(ramp, clip.Speed, at - clip.Start), 0);
            if (clip.SourceDuration > 0)
                media = Math.Min(media, clip.SourceDuration);
            if (!(media >= 0) || !double.IsFinite(media) || !(at >= 0))
                return null;
            slots.Add(new SlicePreviewSlot(offset, at, media));
        }

        return new SlicePreviewRequest(clip.SourcePath!, Math.Max(clip.VideoStreamIndex, 0), slots, spacing, Math.Clamp(shortSide, 16, 256));
    }

    /// <summary>
    /// The ribbon's left edge for a playhead at <paramref name="centerX"/>: centred on it, kept inside [<paramref name="minX"/>,
    /// <paramref name="maxX"/>] when it fits (the playhead's slot then leaves the middle, never the view). Pure.
    /// </summary>
    public static double LeftFor(double centerX, double ribbonWidth, double minX, double maxX)
    {
        var left = centerX - ribbonWidth / 2;
        if (maxX - minX < ribbonWidth)
            return left;
        return Math.Clamp(left, minX, maxX - ribbonWidth);
    }
}

/// <summary>
/// The ribbon element (UnifiedTimelinePane, floating above the ruler; not hit-testable). The host lays out slots and spacing with
/// <see cref="Present"/>, places it with <see cref="Place"/>, fills it with <see cref="SetStrip"/> and fades it with <see cref="Show"/>.
/// </summary>
public sealed class SlicePreviewRibbon : FrameworkElement
{
    private static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(235, 0x1E, 0x1E, 0x22)));
    private static readonly Pen PlateEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30))), 1));
    private static readonly Brush Placeholder = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x31)));
    private static readonly Pen Separator = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16))), 2));
    private static readonly Pen CentreEdge = Frozen(new Pen(Brushes.OrangeRed, 1.5));
    private static readonly Brush ReadoutBrush = Frozen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
    private static readonly Typeface Face = new("Segoe UI");

    /// <summary>Padding round the cells, and the readout row above them, DIP.</summary>
    private const double Pad = 3, ReadoutRow = 14;

    private readonly DrawingVisual _strip = new();
    private int _half = SlicePreviewRequest.MinSlots / 2;
    private double _cellWidth = SlicePreviewPlanner.CellHeight * 16 / 9;
    private string _readout = "";

    // The frames: a frozen strip of cells in slot order covering offsets _firstOffset.._firstOffset + count - 1 (null: placeholders).
    private BitmapSource? _frames;
    private int _firstOffset, _frameCount;
    private bool _shown;

    public SlicePreviewRibbon()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Opacity = 0;
        AddVisualChild(_strip);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _strip;

    /// <summary>The strip's width, DIP: the slots plus padding.</summary>
    public double RibbonWidth => (2 * _half + 1) * _cellWidth + 2 * Pad;

    /// <summary>The strip's height, DIP.</summary>
    public static double RibbonHeight => SlicePreviewPlanner.CellHeight + 2 * Pad + ReadoutRow;

    /// <summary>True while shown (or fading in).</summary>
    public bool IsShown => _shown;

    /// <summary>The readout beside the strip ("+-1f", "+-0.5s"); "" while none.</summary>
    public string Readout => _readout;

    /// <summary>Slots, cell width (DIP) and spacing readout; redrawn only when one changed.</summary>
    public void Present(int slotCount, double cellWidth, SlicePreviewFrameSpacing spacing)
    {
        var half = Math.Clamp(slotCount, SlicePreviewRequest.MinSlots, SlicePreviewRequest.MaxSlots) / 2;
        var readout = spacing.Readout;
        if (half == _half && cellWidth.Equals(_cellWidth) && readout == _readout)
            return;

        (_half, _cellWidth, _readout) = (half, Math.Max(cellWidth, 8), readout);
        Redraw();
    }

    /// <summary>Puts the strip's left edge at <paramref name="left"/> (DIP from this element's left): a translation, no redraw.</summary>
    public void Place(double left) => _strip.Offset = new Vector(Math.Round(left), 0);

    /// <summary>
    /// The frames read back for the slots at offsets <paramref name="firstOffset"/> onward (BGRA, cells side by side in slot order); null
    /// clears them back to placeholders. The bitmap is built here (UI thread) and frozen.
    /// </summary>
    public void SetStrip(byte[]? pixels, int width, int height, int firstOffset, int count)
    {
        _frames = null;
        if (pixels is not null && width > 0 && height > 0 && count > 0 && pixels.Length >= width * height * 4)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            (_frames, _firstOffset, _frameCount) = (bitmap, firstOffset, count);
        }

        Redraw();
    }

    /// <summary>Fades the ribbon in (120 ms) or out (90 ms); a no-op when already there.</summary>
    public void Show(bool shown)
    {
        if (shown == _shown)
            return;

        _shown = shown;
        var fade = new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 120 : 90)) { FillBehavior = FillBehavior.HoldEnd };
        fade.Freeze();
        BeginAnimation(OpacityProperty, fade);
        if (!shown)
            _frames = null;
    }

    private void Redraw()
    {
        var count = 2 * _half + 1;
        var (width, height) = (RibbonWidth, RibbonHeight);
        var top = ReadoutRow + Pad;
        using var drawing = _strip.RenderOpen();
        drawing.DrawRoundedRectangle(Plate, PlateEdge, new Rect(0.5, ReadoutRow - 0.5, width - 1, height - ReadoutRow), 3, 3);

        // Placeholders first, then the frames over the run of slots they cover (one image: the cells are contiguous).
        drawing.DrawRectangle(Placeholder, null, new Rect(Pad, top, count * _cellWidth, SlicePreviewPlanner.CellHeight));
        if (_frames is { } frames && _frameCount > 0)
        {
            var from = Math.Clamp(_firstOffset + _half, 0, count - 1);
            var cells = Math.Min(_frameCount, count - from);
            if (cells > 0)
            {
                drawing.PushClip(Frozen(new RectangleGeometry(new Rect(Pad + from * _cellWidth, top, cells * _cellWidth, SlicePreviewPlanner.CellHeight))));
                drawing.DrawImage(frames, new Rect(Pad + from * _cellWidth, top, _frameCount * _cellWidth, SlicePreviewPlanner.CellHeight));
                drawing.Pop();
            }
        }

        for (var i = 1; i < count; i++)
        {
            var x = Pad + i * _cellWidth;
            drawing.DrawLine(Separator, new Point(x, top), new Point(x, top + SlicePreviewPlanner.CellHeight));
        }

        // The playhead's frame: outlined in the playhead's colour.
        drawing.DrawRectangle(null, CentreEdge, new Rect(Pad + _half * _cellWidth + 0.75, top + 0.75, _cellWidth - 1.5, SlicePreviewPlanner.CellHeight - 1.5));

        if (_readout.Length > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var text = new FormattedText(_readout, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, ReadoutBrush, dpi);
            var box = new Rect(width / 2 - text.Width / 2 - 4, 0, text.Width + 8, ReadoutRow - 1);
            drawing.DrawRoundedRectangle(Plate, null, box, 3, 3);
            drawing.DrawText(text, new Point(box.Left + 4, Math.Max((ReadoutRow - 1 - text.Height) / 2, 0)));
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
