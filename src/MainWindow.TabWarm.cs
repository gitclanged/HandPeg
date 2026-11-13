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
    /// <para>On a lean machine (5.4.4, GpuPreviewEngine.LeanTabs: unified memory or on battery) nothing is decoded ahead on a switch, and a
    /// hovered tab's frames only once the pointer has rested on it (or its card) <see cref="TabHoverRestMs"/> with the player paused and no
    /// scrub or switch under way, for that tab alone.</para>
    /// </summary>
    private void PrewarmLikelyTabs(ProjectTabHeader? hovered)
    {
        if (_gpu is not { IsPlaying: false } engine || _tabs.Active is not { } front)
            return;

        if (engine.LeanTabs)
        {
            _tabRestWarm?.Stop();
            if (hovered is null || ReferenceEquals(hovered, front))
            {
                engine.PrewarmTabs([]);
                return;
            }

            // Made at the first hover (never before Frame 1); each hover restarts it.
            if (_tabRestWarm is null)
            {
                _tabRestWarm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TabHoverRestMs) };
                _tabRestWarm.Tick += (_, _) => TabRested();
            }

            _tabRestFor = hovered;
            _tabRestWarm.Start();
            return;
        }

        var frames = new List<(string Path, int Stream, double MediaSeconds)>();
        foreach (var tab in new[] { _tabs.Next(front, 1), hovered }.Distinct())
        {
            if (tab is not null && !ReferenceEquals(tab, front) && _hydrated.TryGetValue(tab.Id, out var live))
                frames.AddRange(VideoFramesAt(live.Project.Document, tab.PlayheadMs / 1000, MostPreRollVideos));
        }

        engine.PrewarmTabs(frames);
    }

    /// <summary>How long the pointer rests on a tab behind (or its card) before its frames are decoded ahead on a lean machine, ms (5.4.4).</summary>
    internal const int TabHoverRestMs = 200;

    // The lean machine's rest timer (made at the first hover) and the tab it is for.
    private System.Windows.Threading.DispatcherTimer? _tabRestWarm;
    private ProjectTabHeader? _tabRestFor;

    /// <summary>
    /// Whether a lean machine decodes a hovered tab's frames ahead now (5.4.4, pure: the goldens): the pointer still on that tab or its card,
    /// the player paused, no scrub held and no switch under way.
    /// </summary>
    internal static bool LeanPrewarmDue(bool stillHovered, bool playing, bool scrubbing, bool switching) => stillHovered && !playing && !scrubbing && !switching;

    /// <summary>The pointer rested on a tab behind (a lean machine): that tab's frames at its playhead decoded ahead, if all still holds.</summary>
    private void TabRested()
    {
        _tabRestWarm?.Stop();
        if (_tabRestFor is not { } tab || _gpu is not { } engine)
            return;
        var hovered = ProjectTabStrip.HoverItem is { } item && item.Id == tab.Id;
        if (!LeanPrewarmDue(hovered, _viewModel.IsPlaying || engine.IsPlaying, IsScrubHeld, _switchingTab) || !_hydrated.TryGetValue(tab.Id, out var live))
            return;
        engine.PrewarmTabs(VideoFramesAt(live.Project.Document, tab.PlayheadMs / 1000, MostPreRollVideos));
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
}
