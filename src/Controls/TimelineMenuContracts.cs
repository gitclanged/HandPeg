using System.Windows.Controls;

namespace HandPegApp;

// ----- Region-aware timeline menus (Pass 5.7.1 wave-0 contract) -----
// UnifiedTimelineControl hit-tests the part of a clip block a right-click (or the Menu key) lands on and raises one
// TimelineMenuRequest; MainWindow.TrackMenus fills the menu for that region. One contract for the timeline (L1) and the menus (L2).

/// <summary>The part of a clip block a context menu was asked on.</summary>
/// <remarks>Append new regions; never reorder (goldens print them by name).</remarks>
public enum ClipMenuRegion
{
    /// <summary>The clip itself: the core clip menu (about a dozen items, the rest in submenus).</summary>
    Body,

    /// <summary>The fade-in wedge, curve or top-left handle: the Fade &amp; Ease menu for the clip's start.</summary>
    FadeIn,

    /// <summary>The fade-out wedge, curve or top-right handle: the Fade &amp; Ease menu for the clip's end.</summary>
    FadeOut,

    /// <summary>A cut seam between two clips on one track (<see cref="TimelineMenuRequest.Seam"/>): Crossfade, curve, Roll, Heal.</summary>
    Seam,

    /// <summary>The gain line or the waveform: Audio, Gain, Auto-Level and Ducking.</summary>
    Gain,
}

/// <summary>
/// A cut seam: where <paramref name="Left"/> ends and <paramref name="Right"/> starts on one track, at <paramref name="Seconds"/>
/// (sequence time). A seam is selectable on its own (Ctrl+T default crossfade, Delete heals a through-edit, comma and period nudge it a
/// frame) and is the target of the Seam menu.
/// </summary>
/// <param name="Row">The track row both blocks are on.</param>
/// <param name="Left">The block ending at the seam (its <see cref="TimelineBlock.Clip"/> is the outgoing clip).</param>
/// <param name="Right">The block starting at the seam (the incoming clip, which holds the crossfade: Layer.TransitionIn).</param>
/// <param name="Seconds">The cut's sequence time (the middle of the crossfade when there is one).</param>
public sealed record TimelineSeamTarget(TimelineRow Row, TimelineBlock Left, TimelineBlock Right, double Seconds)
{
    /// <summary>Length of the crossfade across the seam, seconds; 0 for a plain cut.</summary>
    public double CrossfadeSeconds { get; init; }

    /// <summary>One take cut in two (same file, continuous file time across the cut): Delete heals it into one clip.</summary>
    public bool IsThroughEdit { get; init; }

    /// <summary>The same seam: the same two clips (by model object), however often the blocks were rebuilt since.</summary>
    public bool IsSame(TimelineSeamTarget? other) =>
        other is not null && Left.Clip is { } left && Right.Clip is { } right && ReferenceEquals(other.Left.Clip, left) && ReferenceEquals(other.Right.Clip, right);
}

/// <summary>
/// Context-menu request: row, block (or null), header flag, fade handle (true = fade in), and the menu for the host to fill; plus the
/// <see cref="Region"/> of the block hit, the <see cref="Seam"/> for <see cref="ClipMenuRegion.Seam"/>, and the sequence time under the
/// pointer.
/// </summary>
public sealed record TimelineMenuRequest(TimelineRow? Row, TimelineBlock? Block, bool OnHeader, bool? FadeIn, ContextMenu Menu)
{
    /// <summary>
    /// The part of the block hit. Defaults to what <see cref="TimelineMenuRequest.FadeIn"/> names (a fade handle, else the body), so a
    /// request made the old way routes as before; the timeline's hit test sets it otherwise.
    /// </summary>
    public ClipMenuRegion Region { get; init; } = RegionOf(FadeIn);

    /// <summary>The seam asked on, with <see cref="ClipMenuRegion.Seam"/>; null otherwise.</summary>
    public TimelineSeamTarget? Seam { get; init; }

    /// <summary>Sequence seconds under the pointer when the menu was asked for; NaN from the keyboard (Menu key, Shift+F10).</summary>
    public double Seconds { get; init; } = double.NaN;

    /// <summary>The region a fade-handle flag names: true the fade in, false the fade out, null the body.</summary>
    public static ClipMenuRegion RegionOf(bool? fadeIn) => fadeIn switch
    {
        true => ClipMenuRegion.FadeIn,
        false => ClipMenuRegion.FadeOut,
        _ => ClipMenuRegion.Body,
    };
}
