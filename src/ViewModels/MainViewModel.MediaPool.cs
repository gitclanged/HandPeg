using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Project Media -----
// Persisted project file bin (TimelineDocument.MediaPool): New Project picks, Media Browser drops, and every file a placed clip used
// (PoolsPlacedFiles), retained after clip deletion until removed. All changes are document Edits so Undo stays consistent with clips:
// manual add/remove is its own step, a clip's file joins with the clip's step, and removing a used file deletes its clips in the same
// step after confirmation.
public partial class MainViewModel
{
    /// <summary>
    /// Media Browser listing: bin files in insertion order, then other used files not in the bin (voiceover, caption audio, or files of
    /// legacy projects without a bin).
    /// </summary>
    public IReadOnlyList<string> ProjectMediaFiles()
    {
        var files = new List<string>();
        foreach (var path in Document.MediaPool.Concat(MediaFilesInUse()))
        {
            var file = TimelineDocument.PoolPath(path);
            if (file.Length > 0 && !files.Contains(file, StringComparer.OrdinalIgnoreCase))
                files.Add(file);
        }

        return files;
    }

    /// <summary>
    /// Adds supported media (MediaFiles.KindOf), deduplicated, in one undo step ("add 3 files to Project Media"); with
    /// <paramref name="undoable"/> false, no step (New Project picks added before the first open, so a failed open keeps the file).
    /// Returns the count added.
    /// </summary>
    public int AddToProjectMedia(IEnumerable<string> files, bool undoable = true)
    {
        var media = files.Select(TimelineDocument.PoolPath).Where(f => f.Length > 0 && MediaFiles.KindOf(f) is not null && !Document.PoolHolds(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (media.Count == 0)
            return 0;

        var label = media.Count == 1 ? $"add {System.IO.Path.GetFileName(media[0])} to Project Media" : $"add {media.Count} files to Project Media";
        if (!Edit(label, edit => edit.AddToMediaPool(media), undoable))
            return 0;

        if (undoable)
            StatusText = media.Count == 1 ? $"{System.IO.Path.GetFileName(media[0])} is in Project Media." : $"{media.Count} files are in Project Media.";
        return media.Count;
    }

    /// <summary>
    /// Removes files in one undo step. Files still used on the Sequence require confirmation (<see cref="ConfirmWarning"/>, else
    /// <see cref="Confirm"/>); on yes, one step deletes exactly those clips (to the recycle bin) and removes all requested files. If
    /// declined or headless, used files are refused (status shows clip count) and the rest removed. Returns the count removed.
    /// </summary>
    public int RemoveFromProjectMedia(IEnumerable<string> files)
    {
        var asked = files.Select(TimelineDocument.PoolPath).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var used = asked.Select(f => (File: f, Clips: ClipsUsing(f))).Where(u => u.Clips > 0).ToList();
        var free = asked.Where(f => Document.PoolHolds(f) && !used.Exists(u => string.Equals(u.File, f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (used.Count > 0 && AskToDeleteClipsOf(used) is { } clips)
            return RemoveWithClips(asked, clips);

        if (used.Count > 0)
        {
            var first = used[0];
            StatusText = used.Count == 1
                ? $"{System.IO.Path.GetFileName(first.File)} stays in Project Media: {first.Clips} clip{(first.Clips == 1 ? "" : "s")} on the Sequence play{(first.Clips == 1 ? "s" : "")} it. Delete {(first.Clips == 1 ? "it" : "them")} first."
                : $"{used.Count} files stay in Project Media: clips on the Sequence play them ({used.Sum(u => u.Clips)} clips). Delete those clips first.";
        }

        if (free.Count == 0)
            return 0;

        var label = free.Count == 1 ? $"remove {System.IO.Path.GetFileName(free[0])} from Project Media" : $"remove {free.Count} files from Project Media";
        if (!Edit(label, edit => edit.RemoveFromMediaPool(free)))
            return 0;

        if (used.Count == 0)
            StatusText = free.Count == 1 ? $"{System.IO.Path.GetFileName(free[0])} was taken out of Project Media." : $"{free.Count} files were taken out of Project Media.";
        return free.Count;
    }

    /// <summary>
    /// Destructive confirmation (Enter = Cancel): title, message, confirm label. Null falls back to <see cref="Confirm"/>.
    /// </summary>
    public Func<string, string, string, bool>? ConfirmWarning { get; set; }

    // Clips to delete with used files after confirmation; null when declined or headless.
    private List<TimelineClip>? AskToDeleteClipsOf(List<(string File, int Clips)> used)
    {
        if ((ConfirmWarning ?? Confirm) is not { } ask)
            return null;

        var clips = used.SelectMany(u => ClipsPlaying(u.File)).Distinct().ToList();
        var count = clips.Count;
        var named = used.Count == 1 ? System.IO.Path.GetFileName(used[0].File) : $"{used.Count} of these files";
        var message = $"{named} {(used.Count == 1 ? "is" : "are")} played by {count} clip{(count == 1 ? "" : "s")} on the Sequence. Taking "
            + $"{(used.Count == 1 ? "it" : "them")} out of Project Media deletes {(count == 1 ? "that clip" : $"those {count} clips")} too "
            + "(into the recycle bin). One step of Undo brings back both. The files on disk stay.";
        var confirm = $"Remove from Project Media & Delete {count} Clip{(count == 1 ? "" : "s")}";
        return count > 0 && ask("Remove from Project Media", message, confirm) ? clips : null;
    }

    /// <summary>
    /// Removes files together with their clips in one undo step: deletes exactly those clips to the recycle bin (withChildren false,
    /// like the Delete key), then removes the files. A clip on a locked track refuses the whole step. Returns the count removed.
    /// </summary>
    private int RemoveWithClips(List<string> asked, List<TimelineClip> clips)
    {
        // Files removed: bin entries, plus listed-but-unbinned used files (legacy projects) once their clips are deleted.
        var pooled = asked.Where(Document.PoolHolds).ToList();
        var gone = asked.Where(f => Document.PoolHolds(f) || clips.Exists(c => string.Equals(TimelineDocument.PoolFileOf(c), f, StringComparison.OrdinalIgnoreCase))).ToList();
        var binned = clips.Where(c => Document.TrackOf(c) is not null)
            .Select(c => (Clip: c, Kept: Binned(c, Document.TrackOf(c)!, ViewOf(c)), Name: ViewOf(c)?.Name ?? c.State.Name)).ToList();
        var count = binned.Count;
        var label = gone.Count == 1
            ? $"remove {System.IO.Path.GetFileName(gone[0])} from Project Media and delete {count} clip{(count == 1 ? "" : "s")}"
            : $"remove {gone.Count} files from Project Media and delete {count} clip{(count == 1 ? "" : "s")}";
        if (!Edit(label, edit =>
            {
                foreach (var (clip, _, _) in binned)
                {
                    if (Document.FindClip(clip.Id) is { } live && Document.TrackOf(live) is not null)
                        edit.DeleteClip(live, withChildren: false);
                }

                if (pooled.Count > 0)
                    edit.RemoveFromMediaPool(pooled);
            }))
        {
            return 0;
        }

        foreach (var (clip, kept, name) in binned)
        {
            if (Document.TrackOf(clip) is null)
                AddToBin(kept.Clip, name, kept.Preview);
        }

        Log(gone.Count == 1
            ? $"{System.IO.Path.GetFileName(gone[0])} was taken out of Project Media with its {count} clip{(count == 1 ? "" : "s")} (in the recycle bin; Ctrl+Z brings both back)"
            : $"{gone.Count} files were taken out of Project Media with their {count} clip{(count == 1 ? "" : "s")} (in the recycle bin; Ctrl+Z brings them back)");
        return gone.Count;
    }

    /// <summary>Whether any Sequence clip (excluding the recycle bin) uses the file.</summary>
    public bool IsFileInUse(string file) => ClipsUsing(file) > 0;

    /// <summary>Count of Sequence clips using the file (Cutouts included).</summary>
    public int ClipsUsing(string file) => ClipsOf(file).Count();

    /// <summary>Sequence clips using the file (as counted by <see cref="ClipsUsing"/>), in track order.</summary>
    internal IReadOnlyList<TimelineClip> ClipsPlaying(string file) => ClipsOf(file).ToList();

    private IEnumerable<TimelineClip> ClipsOf(string file)
    {
        var wanted = TimelineDocument.PoolPath(file);
        if (wanted.Length == 0)
            yield break;

        foreach (var track in Document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (TimelineDocument.PoolFileOf(clip) is { } path && string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase))
                    yield return clip;
            }
        }
    }
}
