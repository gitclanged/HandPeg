using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Markers: named Sequence times added during playback (M), navigable (Shift+M, Ctrl+Shift+M) and optionally exported as chapters.
public partial class MainViewModel
{
    /// <summary>Markers in Sequence time order.</summary>
    public ObservableCollection<TimelineMarker> Markers { get; } = [];

    /// <summary>Export chapters at markers within the output, titled by marker name.</summary>
    [ObservableProperty] private bool _chaptersAtMarkers;

    /// <summary>Raised on marker add/move/rename/remove so timelines redraw.</summary>
    public event Action? MarkersChanged;

    /// <summary>Adds an auto-named marker at a time (default: playhead).</summary>
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

    /// <summary>Renames a marker per keystroke; the single undo step is taken at the first (<see cref="RenameMarkerStarted"/>).</summary>
    public void RenameMarker(TimelineMarker marker, string name)
    {
        // Keyed by time: each keystroke replaces the marker with a renamed copy.
        var index = Markers.ToList().FindIndex(m => Math.Abs(m.Seconds - marker.Seconds) < 1e-6);
        if (index < 0 || string.IsNullOrWhiteSpace(name))
            return;

        Markers[index] = marker with { Name = name.Trim() };
        NotifyMarkersChanged();
    }

    /// <summary>Pushes the current markers to Undo before a rename begins.</summary>
    public void RenameMarkerStarted(TimelineMarker marker) => Checkpoint($"rename {marker.Name}");

    /// <summary>Removes all markers.</summary>
    public void ClearMarkers()
    {
        if (Markers.Count == 0)
            return;

        Checkpoint("delete every marker");
        SetMarkers([]);
    }

    /// <summary>Next marker after a time, or previous before it; null when none.</summary>
    public TimelineMarker? MarkerFrom(double seconds, bool forward) => forward
        ? Markers.FirstOrDefault(m => m.Seconds > seconds + 0.01)
        : Markers.LastOrDefault(m => m.Seconds < seconds - 0.01);

    /// <summary>Jumps the playhead to the next or previous marker.</summary>
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

    /// <summary>Replaces all markers, sorted by time.</summary>
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

    /// <summary>Escapes a value for FFMETADATA (=, ;, #, backslash) on a single line.</summary>
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
    /// Output chapters in timeline (= output) time: one per video-track clip edge (Chapters at Cut Points; all tracks, edges within
    /// 0.5 s merged) and one per marker (Chapters at Markers); a marker on a clip start names that chapter. Copy exports via the cuts
    /// list (SequenceSpans.CopyCutOf, <paramref name="throughCuts"/>) use the list's concatenated clock; markers in gaps are dropped.
    /// </summary>
    private List<(double Start, string Title)> BuildChapters(bool throughCuts = false)
    {
        var chapters = new List<(double Start, string Title)>();
        var clips = throughCuts ? SequenceSpans.CopyCutOf(Document)?.Pieces ?? [] : [];
        double? OutputTime(double seconds)
        {
            if (!throughCuts)
                return seconds;

            var before = 0.0;
            foreach (var clip in clips)
            {
                if (seconds >= clip.Start - 0.0005 && seconds < clip.End)
                    return before + Math.Max(seconds - clip.Start, 0);
                before += clip.End - clip.Start;
            }

            return null;
        }

        if (ChaptersAtCuts)
        {
            // Cuts list: only the copied file's clips; otherwise all video-track edges in order. Bounded by the output end (last
            // picture, SequenceSpans.ProgramSeconds; trailing audio is not exported).
            var length = SequenceSpans.ProgramSeconds(Document);
            var edges = throughCuts
                ? clips.Select(c => c.Start)
                : Document.Tracks.Where(t => t.Kind is Models.Timeline.TimelineTrackKind.Video or Models.Timeline.TimelineTrackKind.Switch)
                    .SelectMany(t => t.Clips).SelectMany(c => (double[])[c.Start, c.End]);
            var last = double.NegativeInfinity;
            foreach (var edge in edges.Where(e => e > 0.0005 && e < length - 0.5).Order())
            {
                if (edge - last < 0.5 || OutputTime(edge) is not { } at)
                    continue;
                if (chapters.Count == 0)
                    chapters.Add((0, "Part 1"));
                if (at - chapters[^1].Start >= 0.5)
                    chapters.Add((at, string.Create(CultureInfo.InvariantCulture, $"Part {chapters.Count + 1}")));
                last = edge;
            }
        }

        if (ChaptersAtMarkers)
        {
            // Markers past the output end (on unexported trailing audio) start no chapter.
            var length = SequenceSpans.ProgramSeconds(Document);
            foreach (var marker in Markers)
            {
                if (marker.Seconds < 0 || marker.Seconds > length || OutputTime(marker.Seconds) is not { } start)
                    continue;

                var same = chapters.FindIndex(c => Math.Abs(c.Start - start) < 0.5);
                if (same >= 0)
                    chapters[same] = (chapters[same].Start, marker.Name);
                else
                    chapters.Add((start, marker.Name));
            }
        }

        chapters.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Insert a "Start" chapter at 0 when the first chapter begins later than 0.5 s.
        if (chapters.Count > 0 && chapters[0].Start > 0.5)
            chapters.Insert(0, (0, "Start"));
        return chapters;
    }

    // ----- YouTube description timestamps and SponsorBlock -----

    /// <summary>Chapters-group SponsorBlock options, as stored in AppSettings.SponsorBlockMode.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SponsorBlockModes => AppSettings.SponsorBlockModes;

    /// <summary>
    /// SponsorBlock mode for the next download (app setting; Chapters group and Settings > Downloads). Choosing cut/mark with no
    /// categories enables the default three (sponsor, self-promotion, interaction reminders); each remains a separate Settings box.
    /// </summary>
    public string SponsorBlockMode
    {
        get => AppSettings.Current.SponsorBlockMode is AppSettings.SponsorBlockChapters or AppSettings.SponsorBlockIgnore
            ? AppSettings.Current.SponsorBlockMode : AppSettings.SponsorBlockCut;
        set
        {
            if (value == SponsorBlockMode || SponsorBlockModes.All(m => m.Key != value))
                return;

            SetViewSetting(s =>
            {
                s.SponsorBlockMode = value;
                if (value != AppSettings.SponsorBlockIgnore && s.SponsorBlockCategories.Length == 0)
                    (s.SponsorBlockSponsor, s.SponsorBlockSelfPromo, s.SponsorBlockInteraction) = (true, true, true);
            }, nameof(SponsorBlockMode));
            StatusText = value switch
            {
                AppSettings.SponsorBlockChapters => $"SponsorBlock: the next download keeps {AppSettings.Current.SponsorBlockCategories} as named chapters.",
                AppSettings.SponsorBlockIgnore => "SponsorBlock: the next download is left as it is.",
                _ => $"SponsorBlock: the next download has {AppSettings.Current.SponsorBlockCategories} cut out.",
            };
        }
    }

    /// <summary>
    /// Copies YouTube description timestamps ("00:00 Intro", "01:24 Gameplay", h:mm:ss past an hour): all markers in timeline (= output)
    /// time, plus mapped source chapters (incl. SponsorBlock) when kept as chapters. Always starts at 00:00 ("Intro" if empty); on a
    /// same-second collision the marker wins.
    /// </summary>
    [RelayCommand]
    private void CopyYouTubeTimestamps()
    {
        var lines = new List<(int Second, string Title)>();
        void Add(double at, string title)
        {
            if (at < 0 || string.IsNullOrWhiteSpace(title))
                return;

            var second = (int)Math.Floor(at + 0.0005);
            if (lines.All(l => l.Second != second))
                lines.Add((second, title.Trim()));
        }

        foreach (var marker in Markers)
            Add(marker.Seconds, marker.Name);

        if (SponsorBlockMode == AppSettings.SponsorBlockChapters && _mediaInfo is { Chapters.Count: > 0 } info)
        {
            foreach (var chapter in info.Chapters)
            {
                if (SequenceTimeOfOpenedFile(chapter.Start) is { } at)
                    Add(at, YouTubeChapterTitle(chapter.Title));
            }
        }

        if (lines.Count == 0 && !HasSource)
        {
            StatusText = $"Nothing to copy yet: load a video and put {KeybindManager.WithGesture("markers", "marker.add")} where the chapters begin.";
            return;
        }

        lines.Sort((a, b) => a.Second.CompareTo(b.Second));
        if (lines.Count == 0 || lines[0].Second > 0)
            lines.Insert(0, (0, "Intro"));

        var text = string.Join(Environment.NewLine, lines.Select(l => $"{YouTubeTime(l.Second)} {l.Title}"));
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText = "The clipboard is busy (another program has it open): try Copy YouTube Timestamps again.";
            return;
        }

        StatusText = lines.Count >= 3
            ? $"Copied {lines.Count} chapter timestamps to clipboard"
            : $"Copied {lines.Count} chapter timestamps to clipboard (YouTube shows chapters from 3 on: put markers with M)";
    }

    /// <summary>YouTube timestamp: mm:ss, or h:mm:ss from one hour.</summary>
    private static string YouTubeTime(int seconds) => seconds >= 3600
        ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");

    /// <summary>Strips yt-dlp's SponsorBlock title prefix ("[SponsorBlock]: Sponsor" -> "Sponsor").</summary>
    private static string YouTubeChapterTitle(string title)
    {
        const string prefix = "[SponsorBlock]:";
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? title[prefix.Length..].Trim() : title;
    }

    /// <summary>Maps a source time of the last opened file to Sequence time via the first visible clip containing it (SequenceSpans); null if none.</summary>
    private double? SequenceTimeOfOpenedFile(double fileSeconds)
    {
        foreach (var span in SequenceSpans.SpansOf(Document, OpenedFile))
        {
            var speed = span.Speed > 0 ? span.Speed : 1;
            if (fileSeconds >= span.MediaOffset - 0.0005 && fileSeconds < span.MediaOffset + (span.End - span.Start) * speed)
                return span.Start + Math.Max(fileSeconds - span.MediaOffset, 0) / speed;
        }

        return null;
    }
}
