using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- A block's own switches on the Timeline (Pass 5.2-HotfixAndDeBloat, the user's call) -----
// Every clip's block has its own On/Off (its picture shown or hidden, a sound heard or not) in one click, and it is that clip's
// alone: a parent clip switched off leaves its Cutouts, its stream lanes and its effects' own switches as they are. Only a track
// header's switch takes the whole family (ToggleHidden: TimelineDocument.IsHiddenWithParents; SetTrackSoundMuted). A child block (a
// Cutout, a stream, any clip on a track in another's stack, an effect) has a Lock to its parent: locked it moves, trims and splits
// with its parent clip (TimelineClip.ParentClipId); unlocked it is free (an effect: a stretch of its clip of its own,
// EffectInstance.Range, which the GPU engine and FFmpeg's graph both keep to).
public partial class MainViewModel
{
    /// <summary>Whether a clip is on: its own switch, a track header's not counted.</summary>
    public bool IsClipOn(Layer clip) => ClipOf(clip) is not { } model || !model.IsHidden;

    /// <summary>Turns one clip on or off, alone (its children keep their own switches): one step of Undo.</summary>
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
    /// A child block's Lock: true when it is locked to a parent clip (it moves, trims and splits with it), false when it is free on a
    /// track that lies in another's stack with a clip there to lock to, null for a clip with nothing to be locked to.
    /// </summary>
    public bool? ClipLock(Layer clip)
    {
        if (ClipOf(clip) is not { } model || Document.TrackOf(model) is not { } track)
            return null;
        if (model.ParentClipId is { } parent && Document.FindClip(parent) is not null)
            return true;
        return ParentClipUnder(model, track) is not null ? false : null;
    }

    // The clip a child is locked to: the one on its track's parent track that it overlaps the most.
    private TimelineClip? ParentClipUnder(TimelineClip child, TimelineTrackModel track)
    {
        if (track.ParentTrackId is not { } id || Document.Tracks.Find(t => t.Id == id) is not { } parent)
            return null;
        return parent.Clips.Where(c => c.Start < child.End && c.End > child.Start)
            .MaxBy(c => Math.Min(c.End, child.End) - Math.Max(c.Start, child.Start));
    }

    /// <summary>Locks a child block to the clip it lies over, or frees it to be dragged, slid and trimmed on its own: one step of Undo.</summary>
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

        // Pass 5.5A: a clip its Link switch locks is one of its clip's pieces, as before 5.5A (never tethered: TimelineClip.Tethered).
        if (!Edit(locked ? $"lock {clip.Name} to its clip" : $"unlock {clip.Name} from its clip", edit => edit.SetClipParent(model, parent?.Id, tethered: false)))
            return;

        RefreshLayerRows();
        StatusText = locked ? $"{clip.Name} is locked to its clip: it moves, trims and splits with it."
            : $"{clip.Name} is unlocked: drag, slide or trim it on its own; its clip no longer takes it along.";
    }

    // ----- An effect's block -----

    /// <summary>Turns one effect card on or off: one step of Undo.</summary>
    public void SetEffectOn(Layer clip, EffectInstance effect, bool on)
    {
        if (!clip.Effects.Contains(effect) || effect.Enabled == on)
            return;

        Checkpoint(on ? $"turn on {NameOf(effect)} on {clip.Name}" : $"turn off {NameOf(effect)} on {clip.Name}");
        effect.Enabled = on;
        StatusText = $"{NameOf(effect)} on {clip.Name} is {(on ? "on" : "off")}.";
    }

    /// <summary>
    /// Locks an effect to its clip's whole span (no stretch of its own: it goes with every trim and split), or unlocks it to a stretch of
    /// its own, at first all of the clip, which its block then drags and trims along the clip: one step of Undo.
    /// </summary>
    public void SetEffectLocked(Layer clip, EffectInstance effect, bool locked)
    {
        if (ClipOf(clip) is not { } model || !clip.Effects.Contains(effect) || (effect.Range is null) == locked)
            return;

        Checkpoint(locked ? $"lock {NameOf(effect)} to {clip.Name}" : $"unlock {NameOf(effect)} from {clip.Name}");
        effect.SetRange(locked ? null : (0, Math.Round(model.Duration, 3)));
        StatusText = locked ? $"{NameOf(effect)} works over the whole of {clip.Name} again." : $"{NameOf(effect)} is unlocked: drag or trim its block along {clip.Name}.";
    }

    /// <summary>The stretch an unlocked effect works in, in seconds of its clip, kept within the clip: one step of Undo.</summary>
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
    /// An unlocked effect taken out of its clip (5.2-Audit, the user's Q2): dragged past its clip's span, or onto a picture or Effect
    /// track, it becomes an Effect Clip of its own (an adjustment clip, as the 3D VFX drop-ins are) over <paramref name="start"/> to
    /// <paramref name="end"/> on the sequence, on <paramref name="onTrack"/> or a new track; it then works on everything under it, not on
    /// its clip alone. Its keyframes keep their moments on the sequence. One step of Undo; false when it could not be placed.
    /// </summary>
    public bool DetachEffect(Layer clip, EffectInstance effect, double start, double end, Layer? onTrack = null)
    {
        if (ClipOf(clip) is not { } model || !clip.Effects.Contains(effect) || effect.IsMissing || !double.IsFinite(start) || !double.IsFinite(end))
            return false;

        var (from, to) = (Math.Max(Math.Round(start, 3), 0), Math.Round(end, 3));
        if (to - from < 0.05)
            return false;

        // The card as the clip's own: its keys counted from the new clip's start (they were from its clip's), working over all of it.
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

    /// <summary>Takes an effect card off its clip: one step of Undo.</summary>
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
