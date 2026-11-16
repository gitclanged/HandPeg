using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Per-block Timeline switches -----
// Each clip block has its own one-click On/Off (picture shown/hidden, audio heard/muted) affecting only that clip; children (Cutouts,
// stream lanes, effects) keep their switches. Only track header switches cascade to the family (ToggleHidden via
// TimelineDocument.IsHiddenWithParents; SetTrackSoundMuted). Child blocks (Cutouts, streams, clips on nested tracks, effects) have a
// parent Lock: locked follows parent move/trim/split (TimelineClip.ParentClipId); unlocked is free (effects get their own
// EffectInstance.Range, honoured by the GPU engine and FFmpeg graph).
public partial class MainViewModel
{
    /// <summary>Clip's own On state (track header switch excluded).</summary>
    public bool IsClipOn(Layer clip) => IsClipOn(ClipOf(clip));

    internal static bool IsClipOn(TimelineClip? model) => model is not { IsHidden: true };

    /// <summary>Toggles one clip only (children unaffected). One undo step.</summary>
    public void SetClipOn(Layer clip, bool on)
    {
        if (ClipOf(clip) is not { } model || model.IsHidden == !on)
            return;

        if (!Edit(on ? $"turn on {clip.Name}" : $"turn off {clip.Name}", edit => edit.SetClipFlags(model, hidden: !on)))
            return;

        RefreshLayerRows();
        LayerVisibilityChanged?.Invoke();
        StatusText = on ? $"{clip.Name} is on again."
            : clip.IsAudio ? $"{clip.Name} is off: not heard. The clips with it keep their own switches."
            : $"{clip.Name} is off: left out of the picture. Its Cutouts and effects keep their own switches; the track header's switch takes them all.";
    }

    /// <summary>
    /// Child Lock state: true when locked to a parent clip (follows move/trim/split), false when free on a nested track with a lock
    /// candidate, null when nothing to lock to.
    /// </summary>
    public bool? ClipLock(Layer clip) => ClipOf(clip) is { } model && Document.TrackOf(model) is { } track ? ClipLock(model, track) : null;

    /// <summary>Overload for an already-resolved clip and track.</summary>
    internal bool? ClipLock(TimelineClip model, TimelineTrackModel track)
    {
        if (model.ParentClipId is { } parent && Document.FindClip(parent) is not null)
            return true;
        return ParentClipUnder(model, track) is not null ? false : null;
    }

    // Lock target: the clip on the parent track with the greatest overlap.
    private TimelineClip? ParentClipUnder(TimelineClip child, TimelineTrackModel track)
    {
        if (track.ParentTrackId is not { } id || Document.Tracks.Find(t => t.Id == id) is not { } parent)
            return null;
        return parent.Clips.Where(c => c.Start < child.End && c.End > child.Start)
            .MaxBy(c => Math.Min(c.End, child.End) - Math.Max(c.Start, child.Start));
    }

    /// <summary>Locks a child to the clip beneath it, or frees it for independent drag/slip/trim. One undo step.</summary>
    public void SetClipLocked(Layer clip, bool locked)
    {
        if (ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } track || (ClipLock(clip) == true) == locked)
            return;

        var parent = locked ? ParentClipUnder(model, track) : null;
        if (locked && parent is null)
        {
            StatusText = $"{clip.Name} has no clip to lock to: move it over a clip of its track's parent first.";
            return;
        }

        // A Link-locked clip is a piece of its parent, never tethered (TimelineClip.Tethered cleared).
        if (!Edit(locked ? $"lock {clip.Name} to its clip" : $"unlock {clip.Name} from its clip", edit => edit.SetClipParent(model, parent?.Id, tethered: false)))
            return;

        RefreshLayerRows();
        StatusText = locked ? $"{clip.Name} is locked to its clip: it moves, trims and splits with it."
            : $"{clip.Name} is unlocked: drag, slide or trim it on its own; its clip no longer takes it along.";
    }

    // ----- An effect's block -----

    /// <summary>Toggles one effect card. One undo step.</summary>
    public void SetEffectOn(Layer clip, EffectInstance effect, bool on)
    {
        if (!clip.Effects.Contains(effect) || effect.Enabled == on)
            return;

        Checkpoint(on ? $"turn on {NameOf(effect)} on {clip.Name}" : $"turn off {NameOf(effect)} on {clip.Name}");
        effect.Enabled = on;
        StatusText = $"{NameOf(effect)} on {clip.Name} is {(on ? "on" : "off")}.";
    }

    /// <summary>
    /// Locks an effect to the full clip span (follows every trim/split), or unlocks it to its own range (initially the whole clip),
    /// draggable and trimmable within the clip. One undo step.
    /// </summary>
    public void SetEffectLocked(Layer clip, EffectInstance effect, bool locked)
    {
        if (ClipOf(clip) is not { } model || !clip.Effects.Contains(effect) || (effect.Range is null) == locked)
            return;

        Checkpoint(locked ? $"lock {NameOf(effect)} to {clip.Name}" : $"unlock {NameOf(effect)} from {clip.Name}");
        effect.SetRange(locked ? null : (0, Math.Round(model.Duration, 3)));
        StatusText = locked ? $"{NameOf(effect)} works over the whole of {clip.Name} again." : $"{NameOf(effect)} is unlocked: drag or trim its block along {clip.Name}.";
    }

    /// <summary>Sets an unlocked effect's range in clip seconds, clamped to the clip. One undo step.</summary>
    public void SetEffectRange(Layer clip, EffectInstance effect, double start, double end)
    {
        if (ClipOf(clip) is not { } model || !clip.Effects.Contains(effect) || !double.IsFinite(start) || !double.IsFinite(end))
            return;

        var length = Math.Round(model.Duration, 3);
        var (from, to) = (Math.Clamp(Math.Round(start, 3), 0, Math.Max(length - 0.05, 0)), Math.Clamp(Math.Round(end, 3), 0, length));
        if (to - from < 0.05 || effect.Range == (from, to))
            return;

        Checkpoint($"move {NameOf(effect)} on {clip.Name}");
        effect.SetRange((from, to));
    }

    /// <summary>
    /// Detaches an unlocked effect dragged past its clip or onto a picture/Effect track into a standalone Effect Clip (adjustment clip,
    /// like 3D VFX drop-ins) over <paramref name="start"/>..<paramref name="end"/> on <paramref name="onTrack"/> or a new track,
    /// affecting everything beneath. Keyframes keep their Sequence times. One undo step; false if it cannot be placed.
    /// </summary>
    public bool DetachEffect(Layer clip, EffectInstance effect, double start, double end, Layer? onTrack = null)
    {
        if (ClipOf(clip) is not { } model || !clip.Effects.Contains(effect) || effect.IsMissing || !double.IsFinite(start) || !double.IsFinite(end))
            return false;

        var (from, to) = (Math.Max(Math.Round(start, 3), 0), Math.Round(end, 3));
        if (to - from < 0.05)
            return false;

        // Rebase key times from the source clip's start to the new clip's start; the card spans the whole new clip.
        var card = effect.Clone();
        card.Range = null;
        var shift = model.Start - from;
        foreach (var key in card.Keys.Keys.ToList())
            card.Keys[key] = [.. card.Keys[key].Select(k => k with { Time = Math.Round(k.Time + shift, 4) })];

        var name = NameOf(effect);
        var element = VfxClip(name, card);
        var placed = Edit($"take {name} out of {clip.Name}", edit =>
        {
            clip.Effects.Remove(effect);
            PlaceNew(edit, element, from, to, onTrack is { IsAudio: false } ? onTrack : null, joins: false);
        });
        if (!placed)
            return false;

        KeyLayer = element;
        StatusText = $"{name} is an Effect Clip of its own now: it works on everything under it. Ctrl+Z puts it back on {clip.Name}.";
        return true;
    }

    /// <summary>Removes an effect card from its clip. One undo step.</summary>
    public void RemoveEffect(Layer clip, EffectInstance effect)
    {
        if (!clip.Effects.Contains(effect))
            return;

        Checkpoint($"remove {NameOf(effect)} from {clip.Name}");
        clip.Effects.Remove(effect);
        StatusText = $"{NameOf(effect)} is off {clip.Name}. Ctrl+Z puts it back.";
    }

    private static string NameOf(EffectInstance effect) => effect.Definition?.Name ?? effect.Type;
}
