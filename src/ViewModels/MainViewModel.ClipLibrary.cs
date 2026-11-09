using System.Globalization;
using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Ping-to-Mark highlights and the Personal Clip Library (Pass 5.2C, Services/ClipLibrary.cs) -----
// Ctrl+J (or Alt + a click on the player's picture) drops a highlight marker at the playhead, the click's place on the picture kept on it
// as its focal point (TimelineMarker.FocalX / FocalY: what Pass 5.3B's Zoom-Hold macro aims at). A clip's "Save Clip to Personal Clip
// Library..." puts its span in the library with a title, tags and the focal point of a highlight inside it; a clip dropped from the
// media browser's Clip Library starts and ends where it was saved.
public partial class MainViewModel
{
    /// <summary>The library's clips changed (one saved or taken out): the media browser's Clip Library lists them again.</summary>
    public event Action? ClipLibraryChanged;

    /// <summary>
    /// Ping-to-Mark: a highlight marker at the playhead (one step of Undo), with where on the picture it was pinged (fractions of the frame,
    /// 0 to 1 across and down) when it was clicked there. A marker already at the playhead becomes the highlight (and takes the focal point).
    /// </summary>
    public TimelineMarker? PingToMark(double? focalX = null, double? focalY = null)
    {
        if (!HasSource)
        {
            StatusText = "Nothing to mark yet: load a video first.";
            return null;
        }

        var at = Math.Round(Math.Clamp(PositionMs / 1000, 0, SequenceSeconds), 3);
        (double? X, double? Y) focal = focalX is { } x && focalY is { } y && double.IsFinite(x) && double.IsFinite(y)
            ? (Math.Round(Math.Clamp(x, 0, 1), 4), Math.Round(Math.Clamp(y, 0, 1), 4))
            : (null, null);
        var there = Markers.FirstOrDefault(m => Math.Abs(m.Seconds - at) < 0.01);
        TimelineMarker marker;
        if (there is not null)
        {
            Checkpoint($"ping {there.Name}");
            marker = there with { Highlight = true, FocalX = focal.X ?? there.FocalX, FocalY = focal.Y ?? there.FocalY };
            SetMarkers(Markers.Select(m => ReferenceEquals(m, there) ? marker : m).ToList());
        }
        else
        {
            Checkpoint("ping a highlight");
            var number = 1;
            while (Markers.Any(m => m.Name == $"Highlight {number}"))
                number++;
            marker = new TimelineMarker(at, $"Highlight {number}") { Highlight = true, FocalX = focal.X, FocalY = focal.Y };
            SetMarkers([.. Markers, marker]);
        }

        StatusText = marker.FocalX is { } fx && marker.FocalY is { } fy
            ? string.Create(CultureInfo.InvariantCulture, $"{marker.Name} at {TimeDisplay.Format(at)}, its focus {fx:0%} across and {fy:0%} down the picture.")
            : $"{marker.Name} at {TimeDisplay.Format(at)}. Alt + a click on the picture marks where to look, too.";
        Log($"Pinged {marker.Name} at {TimeDisplay.Format(at)}");
        return marker;
    }

    /// <summary>Whether a clip can go into the Clip Library: one of a file (a video, a sound, a picture).</summary>
    public bool CanSaveToClipLibrary(Layer view) =>
        ClipOf(view) is { SourcePath.Length: > 0, ContentKind: LayerKind.VideoFile or LayerKind.Audio or LayerKind.Image };

    /// <summary>The focal point of the first highlight with one inside a clip's span, or null.</summary>
    public (double X, double Y)? FocalIn(Layer view) =>
        ClipOf(view) is { } clip && Markers.FirstOrDefault(m => m is { FocalX: not null, FocalY: not null } && m.Seconds >= clip.Start - 1e-6 && m.Seconds <= clip.End + 1e-6) is { } marker
            ? (marker.FocalX!.Value, marker.FocalY!.Value)
            : null;

    /// <summary>
    /// Puts a clip's span into the Personal Clip Library (ClipLibrary.Save: a lossless copy, away from the interface's thread), with a title,
    /// tags and the focal point of a highlight inside it. Null when it could not be saved (the status line says why).
    /// </summary>
    public async Task<ClipLibrary.Entry?> SaveToClipLibraryAsync(Layer view, string title, IReadOnlyList<string> tags)
    {
        if (!CanSaveToClipLibrary(view) || ClipOf(view) is not { SourcePath: { } source } clip)
        {
            StatusText = "A clip of a video, a sound or a picture goes into the Clip Library.";
            return null;
        }

        var (from, to) = clip.PlaysFile ? (clip.MediaOffset, clip.MediaEnd) : (0.0, clip.Duration);
        var (picture, still) = (clip.ContentKind == LayerKind.VideoFile, clip.ContentKind == LayerKind.Image);
        var focal = FocalIn(view);
        var name = title.Trim().Length > 0 ? title.Trim() : view.Name;
        StatusText = $"Saving {name} to the Clip Library...";
        ClipLibrary.Entry? entry;
        try
        {
            entry = await WorkScheduler.Run(new WorkSpec($"Saving {name} to the Clip Library", WorkLane.Interactive),
                work => Task.Run(() => ClipLibrary.Save(source, picture, still, from, to, name, tags, focal?.X, focal?.Y, work.Token)), _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{name} was not saved to the Clip Library (cancelled).";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            StatusText = $"Could not save {name} to the Clip Library: {ex.Message}";
            return null;
        }

        if (entry is null)
        {
            StatusText = $"Could not save {name} to the Clip Library: its file could not be cut there exactly (is FFmpeg installed?).";
            return null;
        }

        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"\"{entry.Title}\" ({entry.Duration:0.#} s) is in your Clip Library: the Media pane's Clip Library lists it, to drag into any project.");
        ClipLibraryChanged?.Invoke();
        return entry;
    }

    /// <summary>
    /// A clip just placed from a file of the Clip Library: it starts and ends where the clip was saved (ClipLibrary.Entry.In / Out), its copy
    /// having begun on the key frame before. No undo step of its own: the drop's.
    /// </summary>
    private void FitToLibraryEntry(Layer view)
    {
        if (ClipLibrary.Find(view.ImagePath) is not { } entry || ClipOf(view) is not { PlaysFile: true } clip || entry.Out <= entry.In)
            return;
        var length = entry.Out - entry.In;
        if (entry.In < 0.02 && Math.Abs(clip.Duration - length) < 0.02)
            return;

        Edit($"start {view.Name} where it was saved", edit =>
        {
            if (Document.FindClip(clip.Id) is not { } now)
                return;
            if (entry.In >= 0.001)
                edit.SlipClip(now, entry.In - now.MediaOffset);
            if (Math.Abs(now.Duration - length) >= 0.001 && length >= TimelineDocument.MinimumClipSeconds)
                edit.TrimClipEnd(now, now.Start + length);
        }, undoable: false);
    }

    /// <summary>
    /// A clip just placed from a sub-clip of its file (5.4C, the Media Browser's cutter): it plays from <paramref name="inSeconds"/> to
    /// <paramref name="outSeconds"/> of the file (its MediaOffset clock), from where it starts. Cut to the length first, then slipped, so
    /// the slip always has the file's room. No undo step of its own: the add's (Undo takes the clip off whole).
    /// </summary>
    internal void FitToSourceRange(Layer view, double inSeconds, double outSeconds)
    {
        if (ClipOf(view) is not { PlaysFile: true } clip || !double.IsFinite(inSeconds) || !double.IsFinite(outSeconds))
            return;

        var from = Math.Max(inSeconds, 0);
        var length = (outSeconds - from) / Math.Max(clip.Speed, 0.01);
        if (length < TimelineDocument.MinimumClipSeconds)
            return;

        Edit($"cut {view.Name} to its sub-clip", edit =>
        {
            if (Document.FindClip(clip.Id) is not { } now)
                return;
            if (Math.Abs(now.Duration - length) >= 0.0005)
                edit.TrimClipEnd(now, now.Start + length);
            if (Math.Abs(now.MediaOffset - from) >= 0.0005)
                edit.SlipClip(now, from - now.MediaOffset);
        }, undoable: false);
    }
}
