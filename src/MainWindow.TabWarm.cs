using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Project tabs: the tabs likely next made ready, and a tab behind scrubbed from its hover card (Pass 5.4.2A) -----
// The tab Ctrl+Tab brings next and the one under the pointer have the frames at their playheads decoded ahead (GpuPreviewEngine.PrewarmTabs:
// half a second past each playhead, the decoders left open), so a switch, and Space or a scrub right after it, waits for nothing. Moving the
// pointer across a tab behind shows its overview sheet's picture at that point in its hover card: pictures it holds in memory already.
public partial class MainWindow
{
    /// <summary>The most videos of one tab whose frames are decoded ahead (the top ones at its playhead).</summary>
    private const int MostPreRollVideos = 4;

    /// <summary>
    /// The frames of the next tab in Ctrl+Tab's order and of <paramref name="hovered"/> (null: none) decoded ahead, at their playheads. Only tabs
    /// kept live (their Sequence in memory: every open tab, 5.4.2A, while memory allows); nothing while playing.
    /// </summary>
    private void PrewarmLikelyTabs(ProjectTabHeader? hovered)
    {
        if (_gpu is not { IsPlaying: false } engine || _tabs.Active is not { } front)
            return;

        var frames = new List<(string Path, int Stream, double MediaSeconds)>();
        foreach (var tab in new[] { _tabs.Next(front, 1), hovered }.Distinct())
        {
            if (tab is not null && !ReferenceEquals(tab, front) && _hydrated.TryGetValue(tab.Id, out var live))
                frames.AddRange(VideoFramesAt(live.Project.Document, tab.PlayheadMs / 1000, MostPreRollVideos));
        }

        engine.PrewarmTabs(frames);
    }

    /// <summary>
    /// The videos a Sequence shows at a moment, top track first, at most <paramref name="most"/>: each one's file, video stream and moment of the
    /// file (pure: the goldens' tab-perf-preroll). Hidden tracks and clips are left out.
    /// </summary>
    internal static List<(string Path, int Stream, double MediaSeconds)> VideoFramesAt(TimelineDocument document, double seconds, int most)
    {
        var frames = new List<(string Path, int Stream, double MediaSeconds)>();
        for (var t = document.Tracks.Count - 1; t >= 0 && frames.Count < most; t--)
        {
            var track = document.Tracks[t];
            if (document.IsHiddenWithParents(track))
                continue;

            foreach (var clip in track.Clips)
            {
                if (clip.Start > seconds || clip.End <= seconds || clip.IsHidden || clip.ContentKind != LayerKind.VideoFile || string.IsNullOrWhiteSpace(clip.SourcePath))
                    continue;

                var speed = double.IsFinite(clip.Speed) && clip.Speed > 0 ? clip.Speed : 1;
                frames.Add((clip.SourcePath, Math.Max(clip.VideoStreamIndex, 0), clip.MediaOffset + (seconds - clip.Start) * speed));
                if (frames.Count >= most)
                    break;
            }
        }

        return frames;
    }

    /// <summary>The pointer across a tab behind with its card open: its overview sheet's picture there, when it keeps one in memory.</summary>
    private void ScrubTabCard(ProjectTabItem item, double fraction)
    {
        if (!_hydrated.TryGetValue(item.Id, out var live) || live.ProgramSheet is not { Count: > 0 } sheet)
            return;

        var index = Math.Clamp((int)(fraction * sheet.Count), 0, sheet.Count - 1);
        if (!ReferenceEquals(item, ProjectTabStrip.HoverItem))
            return;
        if (item.IsScrubbing && Math.Clamp((int)(item.ScrubFraction * sheet.Count), 0, sheet.Count - 1) == index)
        {
            item.ScrubFraction = fraction;
            return;
        }

        item.Preview = sheet.Cell(index);
        item.ScrubFraction = fraction;
    }
}
