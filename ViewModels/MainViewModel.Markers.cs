using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;

namespace HandPegApp.ViewModels;

// Markers: named moments of the sequence, put down while watching (M), to come back to (Shift+M, Ctrl+Shift+M) and,
// when asked for, to become the output's chapters.
public partial class MainViewModel
{
    /// <summary>The markers, in the order they come on the sequence.</summary>
    public ObservableCollection<TimelineMarker> Markers { get; } = [];

    /// <summary>The export's chapters begin at the markers (the ones in what is kept), named after them.</summary>
    [ObservableProperty] private bool _chaptersAtMarkers;

    /// <summary>Raised when a marker was added, moved, renamed or removed: the timelines draw them again.</summary>
    public event Action? MarkersChanged;

    /// <summary>Puts a marker at a moment of the sequence (the playhead, when none is given), named in turn.</summary>
    public TimelineMarker? AddMarker(double? seconds = null)
    {
        if (!HasSource)
            return null;

        var at = Math.Round(Math.Clamp(seconds ?? PositionMs / 1000, 0, SequenceSeconds), 3);
        if (Markers.Any(m => Math.Abs(m.Seconds - at) < 0.01))
        {
            StatusText = $"There is a marker at {TimeDisplay.Format(at)} already.";
            return null;
        }

        Checkpoint("add a marker");
        var number = 1;
        while (Markers.Any(m => m.Name == $"Marker {number}"))
            number++;

        var marker = new TimelineMarker(at, $"Marker {number}");
        SetMarkers([.. Markers, marker]);
        Log($"Added {marker.Name} at {TimeDisplay.Format(at)}");
        return marker;
    }

    public void RemoveMarker(TimelineMarker marker)
    {
        if (!Markers.Contains(marker))
            return;

        Checkpoint($"delete {marker.Name}");
        SetMarkers(Markers.Where(m => m != marker));
        Log($"Deleted {marker.Name}");
    }

    /// <summary>Renames a marker. Typing a name is many changes; one step of Undo is taken for the first of them (see <see cref="RenameMarkerStarted"/>).</summary>
    public void RenameMarker(TimelineMarker marker, string name)
    {
        // By its moment: each keystroke replaces the marker with one of the new name.
        var index = Markers.ToList().FindIndex(m => Math.Abs(m.Seconds - marker.Seconds) < 1e-6);
        if (index < 0 || string.IsNullOrWhiteSpace(name))
            return;

        Markers[index] = marker with { Name = name.Trim() };
        NotifyMarkersChanged();
    }

    /// <summary>A rename is about to begin: the markers as they are go on the Undo stack.</summary>
    public void RenameMarkerStarted(TimelineMarker marker) => Checkpoint($"rename {marker.Name}");

    /// <summary>Removes every marker.</summary>
    public void ClearMarkers()
    {
        if (Markers.Count == 0)
            return;

        Checkpoint("delete every marker");
        SetMarkers([]);
    }

    /// <summary>The first marker after a moment, or the last one before it; null when there is none that way.</summary>
    public TimelineMarker? MarkerFrom(double seconds, bool forward) => forward
        ? Markers.FirstOrDefault(m => m.Seconds > seconds + 0.01)
        : Markers.LastOrDefault(m => m.Seconds < seconds - 0.01);

    /// <summary>Moves the playhead to the next marker, or the one before.</summary>
    public void GoToMarker(bool forward)
    {
        if (MarkerFrom(PositionMs / 1000, forward) is { } marker)
        {
            PositionMs = marker.Seconds * 1000;
            StatusText = $"{marker.Name}, at {TimeDisplay.Format(marker.Seconds)}.";
        }
        else
        {
            StatusText = Markers.Count == 0 ? "There are no markers yet: M puts one at the playhead." : $"No marker {(forward ? "after" : "before")} the playhead.";
        }
    }

    /// <summary>Puts markers in place of the ones there are, in their order on the sequence.</summary>
    private void SetMarkers(IEnumerable<TimelineMarker>? markers)
    {
        Markers.Clear();
        foreach (var marker in (markers ?? []).OrderBy(m => m.Seconds))
            Markers.Add(marker);
        NotifyMarkersChanged();
    }

    private void NotifyMarkersChanged()
    {
        MarkersChanged?.Invoke();
        GenerateCommand();
    }

    /// <summary>A value as FFmpeg's metadata file reads it: =, ;, # and backslash escaped, on one line.</summary>
    private static string EscapeMetadata(string value)
    {
        var escaped = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '=' or ';' or '#' or '\\')
                escaped.Append('\\');
            escaped.Append(c is '\r' or '\n' ? ' ' : c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// The chapters of the output: one at each kept segment (Chapters at Cut Points), and one at each marker in what
    /// is kept (Chapters at Markers), all in the output's own time; a marker where a segment begins names that chapter.
    /// </summary>
    private List<(double Start, string Title)> BuildChapters(List<CutSegment> segments)
    {
        var chapters = new List<(double Start, string Title)>();
        if (ChaptersAtCuts && segments.Count > 0)
        {
            var at = 0.0;
            for (var i = 0; i < segments.Count; i++)
            {
                chapters.Add((at, string.Create(CultureInfo.InvariantCulture, $"Segment {i + 1}")));
                at += segments[i].Duration.TotalSeconds;
            }
        }

        if (ChaptersAtMarkers)
        {
            foreach (var marker in Markers)
            {
                // Where the marker's moment ends up in the output: inside a kept segment, or nowhere.
                double? inOutput = segments.Count == 0 ? marker.Seconds : null;
                var before = 0.0;
                foreach (var segment in segments)
                {
                    if (marker.Seconds >= segment.Start.TotalSeconds && marker.Seconds < segment.End.TotalSeconds)
                    {
                        inOutput = before + marker.Seconds - segment.Start.TotalSeconds;
                        break;
                    }

                    before += segment.Duration.TotalSeconds;
                }

                if (inOutput is not { } start)
                    continue;

                var same = chapters.FindIndex(c => Math.Abs(c.Start - start) < 0.5);
                if (same >= 0)
                    chapters[same] = (chapters[same].Start, marker.Name);
                else
                    chapters.Add((start, marker.Name));
            }
        }

        chapters.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Before the first marker there is still a beginning to name.
        if (chapters.Count > 0 && chapters[0].Start > 0.5)
            chapters.Insert(0, (0, "Start"));
        return chapters;
    }
}
