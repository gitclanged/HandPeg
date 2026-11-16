using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Project tabs: prewarming likely next tabs -----
// The next Ctrl+Tab tab and the hovered tab get their playhead frames pre-decoded (GpuPreviewEngine.PrewarmTabs: half a second past each
// playhead, decoders kept open), so a switch, and Space or a scrub right after it, never waits. Hover-card scrubbing of background tabs
// (from in-memory overview sheets) lives in the tab card partials.
public partial class MainWindow
{
    /// <summary>Max videos per tab to pre-decode (topmost at its playhead).</summary>
    private const int MostPreRollVideos = 4;

    /// <summary>
    /// Pre-decodes playhead frames for the next Ctrl+Tab tab and <paramref name="hovered"/> (null: none). Only live-kept tabs (Sequence in
    /// memory; every open tab while memory allows); nothing while playing.
    /// <para>On lean systems (GpuPreviewEngine.LeanTabs: unified memory or on battery) nothing is pre-decoded for the next tab; a hovered
    /// tab is pre-decoded alone, only after the pointer rests on it (or its card) for <see cref="TabHoverRestMs"/> while paused with no
    /// scrub or switch in progress.</para>
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

            // Created on first hover (never before Frame 1); each hover restarts it.
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

    /// <summary>Hover rest on a background tab (or its card) before pre-decoding on lean systems, ms.</summary>
    internal const int TabHoverRestMs = 200;

    // Lean-system hover rest timer (created on first hover) and its target tab.
    private System.Windows.Threading.DispatcherTimer? _tabRestWarm;
    private ProjectTabHeader? _tabRestFor;

    /// <summary>
    /// True when a lean system should pre-decode the hovered tab now (pure; golden-tested): still hovered (tab or card), paused, no scrub
    /// held and no switch in progress.
    /// </summary>
    internal static bool LeanPrewarmDue(bool stillHovered, bool playing, bool scrubbing, bool switching) => stillHovered && !playing && !scrubbing && !switching;

    /// <summary>Lean-system hover rest elapsed: pre-decodes that tab's playhead frames if conditions still hold.</summary>
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
    /// Videos visible in a Sequence at <paramref name="seconds"/>, topmost first, up to <paramref name="most"/>: file, video stream and media
    /// time each (pure; tab-perf-preroll golden). Hidden tracks and clips are skipped.
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
