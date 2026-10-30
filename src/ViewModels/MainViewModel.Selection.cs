using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Clips selected together (5.0, the user's directive 1) -----
// Ctrl + click and Shift + click on the timeline make a group of clips: a drag of one of them moves (or trims) them all, and
// Delete, Ripple Delete, Mute and Paste Effects act on all of them; each is one step of Undo. The window keeps the primary
// selection (the one clip the inspector, the Center controls and the Keyframes pane show); the group is kept here, so that
// every pane can see it (the Effects pane's Paste Effects). A group is two clips or more of the timeline with times of their
// own: never the background, the captions or an empty track's stand-in.
public partial class MainViewModel
{
    private readonly List<Layer> _selectedClips = [];
    private readonly HashSet<Layer> _selectedClipSet = new(ReferenceEqualityComparer.Instance);

    /// <summary>The clips selected together on the timeline, in the order they were chosen; fewer than two is no group.</summary>
    public IReadOnlyList<Layer> SelectedClips => _selectedClips;

    /// <summary>Whether two clips or more are selected together.</summary>
    public bool HasClipGroup => _selectedClips.Count > 1;

    /// <summary>The group changed (chosen, narrowed, emptied by an edit that took its clips away, or by an undo).</summary>
    public event Action? ClipSelectionChanged;

    /// <summary>Whether a clip is one of the group (never true while there is no group of two or more).</summary>
    public bool IsInClipGroup(Layer clip) => _selectedClips.Count > 1 && _selectedClipSet.Contains(clip);

    /// <summary>Whether a clip can be one of a group: a clip of the timeline with times of its own.</summary>
    public bool CanGroup(Layer clip) => clip is { HasTiming: true, IsBackground: false, IsCaptions: false, IsEmptyTrack: false } && ClipOf(clip) is not null;

    /// <summary>The clips selected together from now on, in order (each once; those that cannot be in a group are left out).</summary>
    public void SetClipSelection(IEnumerable<Layer> clips)
    {
        var chosen = new List<Layer>();
        var seen = new HashSet<Guid>();
        foreach (var clip in clips)
        {
            if (CanGroup(clip) && seen.Add(clip.ClipId))
                chosen.Add(clip);
        }

        if (chosen.SequenceEqual(_selectedClips, ReferenceEqualityComparer.Instance))
            return;

        _selectedClips.Clear();
        _selectedClips.AddRange(chosen);
        _selectedClipSet.Clear();
        _selectedClipSet.UnionWith(chosen);
        ClipSelectionChanged?.Invoke();
    }

    /// <summary>No clips are selected together any more.</summary>
    public void ClearClipGroup() => SetClipSelection([]);

    /// <summary>
    /// The group found again in the views made afresh (an undo, a redo, a project put in place: every view is new), by the
    /// clips' ids; a clip no longer on the timeline leaves it.
    /// </summary>
    public void RemapClipSelection()
    {
        if (_selectedClips.Count == 0)
            return;

        SetClipSelection(_selectedClips.Select(v => Document.FindClip(v.ClipId) is { } clip ? ViewOf(clip) : null).OfType<Layer>().ToList());
    }

    /// <summary>
    /// Makes several edits as one (5.0: a group of clips dragged together): every verb called inside <paramref name="body"/>
    /// (CommitClipMove, TrimClipStart and the rest) folds into the one transaction, so the whole is one step of Undo, named
    /// <paramref name="label"/>, and a refusal of any of them takes back all of them. False when it was refused or failed.
    /// </summary>
    public bool EditTogether(string label, Action body) => Edit(label, _ => body());

    /// <summary>
    /// Deletes clips together, one step of Undo (5.0: Delete or Ripple Delete on a group): each goes to the recycle bin with
    /// what went with it. A clip whose parent is deleted with it is taken by its parent (the model's rule: its children that lie
    /// with it go with it) and kept in the bin with it. Rippled, they go from the latest to the earliest, so that what follows
    /// each closes up and the earlier ones stay where they are (a child then goes by itself, before its parent).
    /// </summary>
    public bool DeleteClips(IReadOnlyList<Layer> views, bool ripple)
    {
        var members = new List<(Layer View, TimelineClip Clip)>();
        foreach (var view in views)
        {
            if (ClipOf(view) is { } clip && Document.TrackOf(clip) is not null && members.TrueForAll(m => m.Clip.Id != clip.Id))
                members.Add((view, clip));
        }

        if (members.Count == 0)
        {
            StatusText = "Select the clips to delete: click their blocks on the timeline (Ctrl + click adds one, Shift + click a range).";
            return false;
        }

        // The ones whose parent (or its parent, and so on) is deleted too are taken with it: the roots first, then any of the
        // others still on the timeline after them (a child that does not lie with its parent stays, and goes by itself).
        var ids = members.Select(m => m.Clip.Id).ToHashSet();
        bool HasParentIn(TimelineClip clip)
        {
            var at = clip;
            for (var depth = 0; depth < 64 && at.ParentClipId is { } parentId && Document.FindClip(parentId) is { } parent; depth++, at = parent)
            {
                if (ids.Contains(parent.Id))
                    return true;
            }

            return false;
        }

        var ordered = ripple
            ? members.OrderByDescending(m => m.Clip.Start).ToList()
            : members.OrderBy(m => HasParentIn(m.Clip) ? 1 : 0).ThenBy(m => m.Clip.Start).ToList();
        var kept = ordered.Select(m => (m.View, m.Clip, Deleted: Binned(m.Clip, Document.TrackOf(m.Clip)!, m.View), Kin: BinnedKin(m.Clip))).ToList();
        var deleted = new HashSet<Guid>();
        var label = ripple ? $"ripple delete {members.Count} clips" : $"delete {members.Count} clips";
        if (!Edit(label, edit =>
            {
                deleted.Clear();
                foreach (var (_, clip) in ordered)
                {
                    if (Document.FindClip(clip.Id) is not { } live || Document.TrackOf(live) is null)
                        continue;

                    if (ripple)
                        edit.RippleDelete(live, AppSettings.Current.RippleAllTracks);
                    else
                        edit.DeleteClip(live);
                    deleted.Add(live.Id);
                }
            }))
        {
            return false;
        }

        // Each clip deleted by itself in the bin, with those of its kin the delete took off the timeline; a clip taken with
        // its parent is among its parent's.
        foreach (var (view, clip, binned, kin) in kept)
        {
            if (!deleted.Contains(clip.Id))
                continue;

            var gone = kin.Where(k => Document.TrackOf(k.Clip) is null && !deleted.Contains(k.Clip.Id)).Select(k => k.Kept).ToList();
            AddToBin(gone.Count == 0 ? binned.Clip : binned.Clip with { Children = gone }, view.Name, binned.Preview);
        }

        ClearClipGroup();
        Log($"{(ripple ? "Ripple deleted" : "Deleted")} {members.Count} clips (in the recycle bin; Ctrl+Z brings them back{(ripple ? ", and what followed them" : "")})");
        return true;
    }

    /// <summary>Whether every one of these clips that has sound is muted (false when none has).</summary>
    public bool AreClipsMuted(IReadOnlyList<Layer> views)
    {
        var any = false;
        foreach (var view in views)
        {
            if (ClipOf(view) is not { HasSound: true } clip)
                continue;
            if (!clip.IsMuted)
                return false;
            any = true;
        }

        return any;
    }

    /// <summary>Mutes the sound of clips together, or lets it be heard again (their pictures stay): one step of Undo.</summary>
    public void SetClipsMuted(IReadOnlyList<Layer> views, bool muted)
    {
        var clips = views.Select(ClipOf).OfType<TimelineClip>().Where(c => c.HasSound && c.IsMuted != muted).DistinctBy(c => c.Id).ToList();
        if (clips.Count == 0)
        {
            StatusText = muted ? "None of the selected clips has sound that is heard." : "None of the selected clips is muted.";
            return;
        }

        var label = muted ? $"mute {clips.Count} clips" : $"unmute {clips.Count} clips";
        if (Edit(label, edit =>
            {
                foreach (var clip in clips)
                    edit.SetClipFlags(clip, muted: muted);
            }))
        {
            StatusText = muted ? $"The sound of {clips.Count} clips is muted; their pictures stay." : $"The sound of {clips.Count} clips is heard again.";
        }
    }

    /// <summary>
    /// Gives each of these clips a copy of an effect stack (5.0: Paste Effects on a group), with the values its cards carry,
    /// one step of Undo: each clip's stack replaced, or (<paramref name="append"/>, Paste Some Effects...) added to, an effect
    /// a clip has once at most taking the place of the one there.
    /// </summary>
    public void PasteEffectsToClips(IReadOnlyList<Layer> clips, IReadOnlyList<EffectState> cards, bool append = false)
    {
        var targets = clips.Distinct().ToList();
        if (targets.Count == 0 || cards.Count == 0)
            return;

        Checkpoint($"paste effects onto {targets.Count} clips");
        using var whole = DeferCommand();
        foreach (var clip in targets)
        {
            using (Layer.Echo())
            {
                var pasted = cards.Select(clip.AdoptCard).ToList();
                if (!append)
                {
                    clip.SetEffects(pasted);
                    continue;
                }

                var once = pasted.Where(c => c.Definition is { Feature: not null }).Select(c => c.Type).ToHashSet();
                clip.SetEffects(clip.Effects.Where(c => !once.Contains(c.Type)).Concat(pasted).OrderBy(c => (int)(c.Definition?.Zone ?? EffectZone.Look)).ToList());
            }
        }

        StatusText = $"Pasted {cards.Count} effect{(cards.Count == 1 ? "" : "s")} onto {targets.Count} clips.";
    }
}
