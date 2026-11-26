using System.Globalization;

namespace HandPegApp.Services;

// ----- Slice-Preview ribbon (Pass 5.7.1 wave-0 contract) -----
// A 5-7 frame micro-filmstrip above the playhead while it is scrubbed slowly over a video clip (never over audio-only or image clips,
// user decision Q3). The ribbon (SlicePreviewRibbon) asks for its frames with a SlicePreviewRequest; GpuPreviewEngine resolves the
// file to its source-table entry and posts the times to the compositor (CompositorClient.PostSliceBurst, ControlBlock SliceBurst*),
// whose worker decodes them into its frame cache ahead of background work.

/// <summary>
/// How far apart the ribbon's neighbouring frames are: whole frames when zoomed in deep (<see cref="Frames"/> &gt; 0), else a round
/// part of a second. <see cref="Readout"/> is the label beside the ribbon.
/// </summary>
/// <param name="Seconds">Sequence seconds between two neighbouring slots (finite, &gt; 0 for a usable spacing).</param>
/// <param name="Frames">Whole frames between two neighbouring slots; 0 when the spacing is in seconds.</param>
public readonly record struct SlicePreviewFrameSpacing(double Seconds, int Frames)
{
    /// <summary>Spaced by whole frames.</summary>
    public bool InFrames => Frames > 0;

    /// <summary>A spacing the ribbon can lay out (finite and above zero).</summary>
    public bool IsUsable => double.IsFinite(Seconds) && Seconds > 0;

    /// <summary>
    /// The frame-spacing readout (item 10): a plus-minus sign, then the spacing in frames ("1f", "3f") or in seconds ("0.5s", "1s";
    /// invariant culture, at most two decimals); "" for an unusable spacing.
    /// </summary>
    public string Readout => !IsUsable ? ""
        : InFrames ? "±" + Frames.ToString(CultureInfo.InvariantCulture) + "f"
        : "±" + Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";

    /// <summary>A spacing of <paramref name="frames"/> whole frames at <paramref name="frameRate"/> fps (at least one frame).</summary>
    public static SlicePreviewFrameSpacing OfFrames(int frames, double frameRate)
    {
        var count = Math.Max(frames, 1);
        return double.IsFinite(frameRate) && frameRate > 0 ? new(count / frameRate, count) : default;
    }

    /// <summary>A spacing of <paramref name="seconds"/> sequence seconds (not snapped to frames).</summary>
    public static SlicePreviewFrameSpacing OfSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? new(seconds, 0) : default;
}

/// <summary>
/// One frame of the ribbon: its place (<paramref name="Offset"/> slots from the centre, negative to the left; 0 is the playhead's
/// frame), the sequence time it shows and the file time it is decoded from.
/// </summary>
public readonly record struct SlicePreviewSlot(int Offset, double SequenceSeconds, double MediaSeconds);

/// <summary>
/// What the ribbon asks the compositor for: one video file (<paramref name="Path"/>, video stream <paramref name="Stream"/>, 0 the
/// first), its slots left to right, their spacing, and the thumbnails' short side in pixels. Also the ribbon's gating rules, shared by
/// the timeline and the goldens.
/// </summary>
public sealed record SlicePreviewRequest(string Path, int Stream, IReadOnlyList<SlicePreviewSlot> Slots, SlicePreviewFrameSpacing Spacing,
    int ShortSide = SlicePreviewRequest.DefaultShortSide)
{
    /// <summary>Fewest and most slots the ribbon shows (odd counts keep the playhead's frame in the middle).</summary>
    public const int MinSlots = 5, MaxSlots = 7;

    /// <summary>Thumbnail short side, pixels, when the ribbon names none.</summary>
    public const int DefaultShortSide = 72;

    /// <summary>
    /// Pointer speeds along the timeline, DIP per second: above <see cref="DormantAbove"/> (a whip) the ribbon is dormant and costs
    /// nothing; below <see cref="WakeBelow"/> for <see cref="WakeAfterMs"/> it fades in (the gap between the two is hysteresis).
    /// </summary>
    public const double DormantAbove = 300, WakeBelow = 250;

    /// <summary>How long, ms, the pointer must stay below <see cref="WakeBelow"/> before the ribbon fades in.</summary>
    public const int WakeAfterMs = 120;

    /// <summary>
    /// A request the compositor can take: a path, a stream, a usable spacing, <see cref="MinSlots"/> or <see cref="MaxSlots"/> slots in
    /// offset order with the playhead's frame (offset 0) in the middle, finite sequence and file times at or after 0, and a positive
    /// thumbnail size.
    /// </summary>
    public bool IsValid => !string.IsNullOrEmpty(Path) && Stream >= 0 && ShortSide > 0 && Spacing.IsUsable && Slots is { Count: MinSlots or MaxSlots }
        && Centred(Slots);

    // Slot i at offset i - count / 2 (offset 0 the middle one), with finite sequence and file times at or after 0.
    private static bool Centred(IReadOnlyList<SlicePreviewSlot> slots)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.Offset != i - slots.Count / 2 || !(double.IsFinite(slot.SequenceSeconds) && slot.SequenceSeconds >= 0)
                || !(double.IsFinite(slot.MediaSeconds) && slot.MediaSeconds >= 0))
            {
                return false;
            }
        }

        return true;
    }
}
