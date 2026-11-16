using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Multi-clip selection -----
// Ctrl/Shift+click builds a group: dragging one moves or trims all, and Delete, Ripple Delete, Mute and Paste Effects apply to all,
// each in one undo step. The window owns the primary selection (inspector, Crop boxes, Keyframes); the group lives here so every
// pane can read it (Effects pane Paste Effects). A group is two or more timed timeline clips; never background, captions or
// empty-track placeholders.
public partial class MainViewModel
{
    private readonly List<Layer> _selectedClips = [];
    private readonly HashSet<Layer> _selectedClipSet = new(ReferenceEqualityComparer.Instance);

    /// <summary>Grouped clips in selection order; fewer than two means no group.</summary>
    public IReadOnlyList<Layer> SelectedClips => _selectedClips;

    /// <summary>Whether a group of two or more clips is selected.</summary>
    public bool HasClipGroup => _selectedClips.Count > 1;

    /// <summary>Raised when the group changes (selection, narrowing, removal by an edit, undo).</summary>
    public event Action? ClipSelectionChanged;

    /// <summary>Whether a clip is in the group (false when no group exists).</summary>
    public bool IsInClipGroup(Layer clip) => _selectedClips.Count > 1 && _selectedClipSet.Contains(clip);

    /// <summary>Whether a clip is groupable (a timed timeline clip).</summary>
    public bool CanGroup(Layer clip) => clip is { HasTiming: true, IsBackground: false, IsCaptions: false, IsEmptyTrack: false } && ClipOf(clip) is not null;

    /// <summary>Sets the group in order (deduplicated; ungroupable clips filtered).</summary>
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

    /// <summary>Clears the group.</summary>
    public void ClearClipGroup() => SetClipSelection([]);

    /// <summary>Rebinds the group to rebuilt views by clip id after undo/redo/load; clips no longer present drop out.</summary>
    public void RemapClipSelection()
    {
        if (_selectedClips.Count == 0)
            return;

        SetClipSelection(_selectedClips.Select(v => Document.FindClip(v.ClipId) is { } clip ? ViewOf(clip) : null).OfType<Layer>().ToList());
    }

    /// <summary>
    /// Batches edits (e.g. group drag): verbs inside <paramref name="body"/> (CommitClipMove, TrimClipStart, ...) fold into one
    /// transaction and one undo step named <paramref name="label"/>; any refusal rolls back all. False when refused or failed.
    /// </summary>
    public bool EditTogether(string label, Action body) => Edit(label, _ => body());

    /// <summary>
    /// Group Delete/Ripple Delete in one undo step, each to the recycle bin with its family. Children whose parent is also deleted go
    /// with the parent (model rule: overlapping children follow) and share its bin entry. Ripple deletes run latest-first so earlier
    /// clips stay put (a child is then removed on its own, before its parent).
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

        // Clips with a deleted ancestor go with it: delete roots first, then any still present (non-overlapping children are deleted
        // individually).
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

        // One bin entry per independently deleted clip, holding the family members it removed; carried children are in the parent's.
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

    /// <summary>Whether all audio-bearing clips here are muted (false when none have audio).</summary>
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

    /// <summary>Mutes or unmutes clip audio together (picture unaffected). One undo step.</summary>
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
    /// Group Paste Effects: copies an effect stack with its card values to each clip in one undo step, replacing the stack or, with
    /// <paramref name="append"/> (Paste Some Effects...), appending; single-instance effects replace the existing one.
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
            using (OwnLookScope())
            {
                var pasted = TakenBy(clip, cards).Select(clip.AdoptEffect).ToList();
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
