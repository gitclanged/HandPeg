using System.Globalization;
using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Ping-to-Mark highlights and Personal Clip Library (Services/ClipLibrary.cs) -----
// Ctrl+J (or Alt+click on the player) drops a highlight marker at the playhead, storing the click as its focal point
// (TimelineMarker.FocalX/FocalY, the Zoom-Hold macro target). "Save Clip to Personal Clip Library..." stores a clip's span with
// title, tags and an inner highlight's focal point; clips dropped from the Clip Library restore their saved in/out.
public partial class MainViewModel
{
    /// <summary>Raised when library clips change (save/remove) so the Clip Library view refreshes.</summary>
    public event Action? ClipLibraryChanged;

    /// <summary>
    /// Ping-to-Mark: adds a highlight marker at the playhead (one undo step) with an optional focal point (frame fractions 0..1). An
    /// existing marker at the playhead is promoted to a highlight and takes the focal point.
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

    /// <summary>Whether a clip can be saved to the Clip Library (file-backed video, audio or image).</summary>
    public bool CanSaveToClipLibrary(Layer view) =>
        ClipOf(view) is { SourcePath.Length: > 0, ContentKind: LayerKind.VideoFile or LayerKind.Audio or LayerKind.Image };

    /// <summary>Focal point of the first highlight with one within a clip's span, or null.</summary>
    public (double X, double Y)? FocalIn(Layer view) =>
        ClipOf(view) is { } clip && Markers.FirstOrDefault(m => m is { FocalX: not null, FocalY: not null } && m.Seconds >= clip.Start - 1e-6 && m.Seconds <= clip.End + 1e-6) is { } marker
            ? (marker.FocalX!.Value, marker.FocalY!.Value)
            : null;

    /// <summary>
    /// Saves a clip's span to the Personal Clip Library (ClipLibrary.Save: lossless copy off the UI thread) with title, tags and inner
    /// highlight focal point. Null on failure (reason in status line).
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
            Fail($"Could not save {name} to the Clip Library: {ex.Message}", $"Save {name} to the Clip Library", ex);
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
    /// Trims a clip placed from a Clip Library file to its saved In/Out (ClipLibrary.Entry; the copy starts at the preceding keyframe).
    /// Part of the drop's undo step.
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
    /// Applies a Media Browser sub-clip range to a just-placed clip: plays <paramref name="inSeconds"/>..<paramref name="outSeconds"/>
    /// of the file (MediaOffset clock) from its start. Trims to length first, then slips, so the slip always has room. Part of the
    /// add's undo step.
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
