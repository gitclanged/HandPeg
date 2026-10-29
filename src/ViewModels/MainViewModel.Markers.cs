using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

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
    /// The chapters of the output: one at every edge of a clip on any video track (Chapters at Cut Points: where 4.2.0's
    /// kept segments began, now that cuts are split points; 4.3D: every track alike, edges within half a second of each
    /// other one chapter), and one at each marker (Chapters at Markers), all in the timeline's time, which is the
    /// output's; a marker where a clip begins names that chapter. A Copy export through the cuts list (the main video's
    /// clips end to end, <paramref name="throughCuts"/>) has them on its own clock, at its clips: a marker in a gap has none.
    /// </summary>
    private List<(double Start, string Title)> BuildChapters(bool throughCuts = false)
    {
        var chapters = new List<(double Start, string Title)>();
        var clips = GetMainClips();
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
            // Through the cuts list only the main video's clips are there; otherwise every video track's edges, in order.
            var length = SequenceSeconds;
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
            foreach (var marker in Markers)
            {
                if (marker.Seconds < 0 || marker.Seconds > SequenceSeconds || OutputTime(marker.Seconds) is not { } start)
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

    // ----- YouTube description timestamps and SponsorBlock (Pass 4.2B-2) -----

    /// <summary>The SponsorBlock choices of the Chapters group, as AppSettings.SponsorBlockMode stores them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SponsorBlockModes => AppSettings.SponsorBlockModes;

    /// <summary>
    /// What the next download does with SponsorBlock (an app setting, shown in the Chapters group and in Settings >
    /// Downloads). Choosing to cut or mark with no category ticked ticks the usual three (sponsor, self-promotion,
    /// interaction reminders), so the one click does something; each stays a box of its own in Settings.
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
    /// The output's chapters as a YouTube description lists them ("00:00 Intro", "01:24 Gameplay", h:mm:ss past an
    /// hour), copied to the clipboard: every marker, at its time on the timeline (which is the output's; clip
    /// speeds are in the sequence's time already), and, with SponsorBlock kept as chapters, the main video's own
    /// chapters (the SponsorBlock ones among them) where they land. Always opens with 00:00, "Intro" when nothing is
    /// there; two lines on the same second keep the marker's.
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
                if (SequenceTimeOfMainSource(chapter.Start) is { } at)
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

    /// <summary>mm:ss, or h:mm:ss from an hour on, as YouTube reads them.</summary>
    private static string YouTubeTime(int seconds) => seconds >= 3600
        ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");

    /// <summary>A chapter title as yt-dlp writes a SponsorBlock one ("[SponsorBlock]: Sponsor"), without its prefix.</summary>
    private static string YouTubeChapterTitle(string title)
    {
        const string prefix = "[SponsorBlock]:";
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? title[prefix.Length..].Trim() : title;
    }

    /// <summary>Where a moment of the main video's file is on the sequence: in the clip of it that holds it, or null when no clip does.</summary>
    private double? SequenceTimeOfMainSource(double fileSeconds)
    {
        foreach (var clip in GetMainClips())
        {
            if (fileSeconds >= clip.Offset - 0.0005 && fileSeconds < clip.Offset + (clip.End - clip.Start))
                return clip.Start + Math.Max(fileSeconds - clip.Offset, 0);
        }

        return null;
    }
}
