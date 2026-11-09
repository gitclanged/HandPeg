using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.ViewModels;

// ----- Project Media (5.4C) -----
// The project's bin of files (TimelineDocument.MediaPool), saved with it: the files picked in the New Project studio, those dropped onto
// the Media Browser, and every file a clip has played (a clip placed puts its file there: PoolsPlacedFiles), kept after its clips are
// deleted until it is taken out of the bin. Every change is an edit of the document (Edit), so Undo keeps it in step with the clips: an
// add or a removal by hand is a step of its own, a clip's file goes in with the clip's own step, and (5.4D) a used file taken out goes
// with its clips in one step, once the user has said so.
public partial class MainViewModel
{
    /// <summary>
    /// Project Media as the Media Browser lists it: the bin's files in the order they came in, then any other file the project uses that
    /// is not in it (the voiceover, the captions' sound, or a project from before 5.4C until it is opened again).
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
    /// Puts files into Project Media: only media HandPeg reads (MediaBrowserItem.KindOf), each once. One step of Undo ("add 3 files to
    /// Project Media"); with <paramref name="undoable"/> false none (the New Project studio's picks, put in before the project's first
    /// file is opened, so a file that fails to open stays in the bin). How many went in.
    /// </summary>
    public int AddToProjectMedia(IEnumerable<string> files, bool undoable = true)
    {
        var media = files.Select(TimelineDocument.PoolPath).Where(f => f.Length > 0 && MediaBrowserItem.KindOf(f) is not null && !Document.PoolHolds(f))
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
    /// Takes files out of Project Media, one step of Undo. A file a clip on the Sequence still plays (a clip's file is always in the bin)
    /// goes only with its clips (5.4D, Q5 b): the user is asked first (<see cref="ConfirmWarning"/>, else <see cref="Confirm"/>), and on
    /// yes one step deletes exactly those clips (into the recycle bin, as Delete does) and takes every file asked for out of the bin. Declined,
    /// or with no one to ask, such a file is refused as before (the status line says how many clips use it) and the others go. How many
    /// files went out.
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
    /// A question that goes ahead only on a deliberate click (5.4D: Cancel is the button Enter presses): title, message, what the
    /// confirming button says. Null: <see cref="Confirm"/> asks instead.
    /// </summary>
    public Func<string, string, string, bool>? ConfirmWarning { get; set; }

    // The clips that play the used files, once the user has said yes to deleting them with the files; null when declined or no one asks.
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
    /// The files asked for out of Project Media with the clips that play them (5.4D, Q5 b), one step of Undo: exactly those clips deleted
    /// (each by itself, its children left as the Delete key leaves a clip's: withChildren false) into the recycle bin, then the files out
    /// of the bin. A clip on a locked track refuses the whole step (its status text). How many files went out.
    /// </summary>
    private int RemoveWithClips(List<string> asked, List<TimelineClip> clips)
    {
        // The files that go: those in the bin, and a used file listed without being in it (a project from before 5.4C) once its clips go.
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

    /// <summary>Whether a clip on the Sequence (not one in the recycle bin) plays the file.</summary>
    public bool IsFileInUse(string file) => ClipsUsing(file) > 0;

    /// <summary>How many clips on the Sequence play the file (its Cutouts count as clips of it).</summary>
    public int ClipsUsing(string file) => ClipsOf(file).Count();

    /// <summary>The clips on the Sequence that play the file (5.4D: those <see cref="ClipsUsing"/> counts), in the tracks' order.</summary>
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
