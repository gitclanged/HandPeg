using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// The A/V invariant, checked on the graphs by code rather than by their text: for every video clip with sound, a moment
/// of its file is seen and heard at the same moment of the sequence, and that moment is where the timeline puts it
/// (the clip's start plus how far into the clip the moment is, at the clip's speed).
///
/// The picture's moment is followed from its movie= through the graph to the overlay that lays it on the canvas; the
/// canvas's own clock is followed the same way, from the sequence's canvas (color=, or the player's frames) to that
/// overlay, so the frame's time stamp there says which moment of the sequence shows it. The sound's moment is followed
/// from its amovie= to the amix it is mixed by; amix lines its inputs up by their first samples, so the moment of the
/// sequence it is heard at is its distance from its stream's first sample plus where the mix's first input (a track of
/// the main video, or the silence standing in for one) starts on the sequence, followed the same way.
/// </summary>
public static class AvCheck
{
    // The graphs write times to the millisecond; a few of them add up.
    private const double Tolerance = 0.002;

    /// <param name="files">The media files the fixture imagines, for where their pictures and sounds start and their frame rates.</param>
    /// <param name="graphs">The graphs of the command or the player: an export's -filter_complex and -vf, or the live graph.</param>
    /// <param name="at">The playhead of a live graph (it holds and reads from there); null for an export.</param>
    /// <param name="audioFrom">Where a live graph's sound is laid out from; 0 for an export.</param>
    public static List<string> Check(IEnumerable<Media> files, SequenceExportState state, IReadOnlyList<string> graphs, bool live, double? at, double audioFrom, out int checkedMoments)
    {
        var problems = new List<string>();
        checkedMoments = 0;
        var parsed = graphs.Where(g => g.Length > 0).Select(FilterGraph.Parse).ToList();
        var label = live ? $"live at {Num(at ?? 0)} s" : "export";
        var known = files.DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList();

        // Every video clip on the timeline whose sound is heard, the first of each file (the graph's first source of the file is
        // the one followed). Not the primary file's: its clips are read from the first input or the player's own stream (the
        // primary chain), and one of them that is a layer would be taken for the chain's first.
        var heard = state.Timeline.Tracks
            .Where(t => t.Kind is not (HandPegApp.Models.Timeline.TimelineTrackKind.Audio or HandPegApp.Models.Timeline.TimelineTrackKind.Subtitle) && !t.IsMuted)
            .SelectMany(t => t.Clips)
            .Where(c => c.ContentKind == LayerKind.VideoFile && c.HasSound && !c.IsHidden && !c.IsMuted && c.SourcePath is not null && !SamePath(c.SourcePath, state.LocalMediaPath))
            .DistinctBy(c => c.SourcePath!, StringComparer.OrdinalIgnoreCase);
        foreach (var layer in heard)
        {
            if (known.FirstOrDefault(m => SamePath(m.Path, layer.SourcePath!)) is not { HasAudio: true } media)
                continue;

            var proxy = live && state.PreviewPaths.TryGetValue(media.Path, out var proxyPath) ? proxyPath : null;
            var picture = FindSource(parsed, "movie", proxy ?? media.Path);
            var sound = FindSource(parsed, "amovie", media.Path);
            var name = $"{label}, {System.IO.Path.GetFileName(media.Path)}{(proxy is null ? "" : " (proxy)")}";
            if (picture is null || sound is null)
            {
                // Out of the live graph's window, or not yet on: nothing to check.
                if (picture is null != sound is null)
                    problems.Add($"{name}: the {(picture is null ? "picture" : "sound")} is not in the graph but the {(picture is null ? "sound" : "picture")} is.");
                continue;
            }

            // The picture of a moment is where the clip puts it; its sound as far from there as an unlinked clip's sound was slipped.
            var speed = Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed);
            var slip = layer.AudioLinked ? 0 : layer.AudioSlipSeconds;
            var playhead = at ?? 0;
            var into = Math.Max(playhead - layer.Start, 0);
            var lowest = media.VideoStart + layer.MediaOffset + into * speed;
            var shownTo = layer.Duration;
            if (live)
                shownTo = Math.Min(shownTo, playhead + FilterGraphBuilder.LiveLookAhead - layer.Start);
            var highest = media.VideoStart + layer.MediaOffset + shownTo * speed;

            // Seeks that land after the cut lose the start of what was asked for.
            problems.AddRange(CheckSeek(picture.Value, media, proxy is not null, name + ": picture"));
            problems.AddRange(CheckSeek(sound.Value, media, false, name + ": sound"));

            foreach (var offset in new[] { 0.02, 0.5, 1.5, 7 })
            {
                var moment = OnFrame(media, lowest + offset * speed);
                if (moment >= highest - 0.05)
                    continue;

                checkedMoments++;
                var expected = layer.Start + (moment - media.VideoStart - layer.MediaOffset) / speed;
                var seen = Seen(picture.Value, media, proxy is not null, moment, state, live, at, out var whyNotSeen);
                var heardAt = Heard(sound.Value, media, moment, state, live, audioFrom, out var whyNotHeard);
                var what = $"{name}, file moment {Num(moment)} s (timeline {Num(expected)} s{(slip != 0 ? $", its sound slipped {Num(slip)} s" : "")})";
                if (seen is null || heardAt is null)
                {
                    problems.Add($"{what}: {(seen is null ? $"never seen ({whyNotSeen})" : "")}{(seen is null && heardAt is null ? "; " : "")}{(heardAt is null ? $"never heard ({whyNotHeard})" : "")}.");
                    continue;
                }

                if (Math.Abs(seen.Value - expected) > Tolerance || Math.Abs(heardAt.Value - (expected + slip)) > Tolerance)
                {
                    var drift = heardAt.Value - slip - seen.Value;
                    problems.Add($"{what}: seen at {Num(seen.Value)} s, heard at {Num(heardAt.Value)} s"
                                 + (Math.Abs(drift) > Tolerance ? $" (the sound {Num(Math.Abs(drift))} s {(drift > 0 ? "late" : "early")})" : "")
                                 + (Math.Abs(seen.Value - expected) > Tolerance ? $" (the picture {Num(seen.Value - expected)} s off the timeline)" : "") + ".");
                }
            }
        }

        return problems;
    }

    // ----- The picture -----

    private static double? Seen((FilterGraph Graph, GraphNode Node) source, Media media, bool proxy, double moment, SequenceExportState state, bool live, double? at, out string why)
    {
        why = "";
        var (graph, movie) = source;
        var clock = new Timing.Clock(proxy ? moment - media.VideoStart : moment, Landing(movie, media, proxy, video: true));
        List<Timing.Arrival> arrivals;
        try
        {
            arrivals = Timing.Walk(graph, movie, -1, clock, media.Fps, (node, pad) => IsOverlay(node) && pad >= 1 ? true : false);
        }
        catch (Timing.Unsupported ex)
        {
            why = ex.Message;
            return null;
        }

        if (arrivals.Count == 0)
        {
            why = WhyDropped(graph, movie, clock, media.Fps);
            return null;
        }

        // Where the canvas under it is on the sequence: its clock followed from the canvas to the same overlay.
        var overlay = arrivals[0].Node;
        var canvas = CanvasClock(graph, overlay, state, live, at);
        if (canvas is null)
        {
            why = "the canvas it is laid on could not be followed";
            return null;
        }

        var (scale, shift) = canvas.Value;
        return arrivals[0].Clock.T * scale + shift;
    }

    /// <summary>The canvas's time at an overlay as a moment of the sequence: sequence = t * scale + shift.</summary>
    private static (double Scale, double Shift)? CanvasClock(FilterGraph graph, GraphNode overlay, SequenceExportState state, bool live, double? at)
    {
        var starts = new List<(GraphNode Node, int Pad, Func<double, Timing.Clock> Clock)>();
        foreach (var node in graph.Nodes.Where(n => n.IsSource && n.Name is "color" or "nullsrc"))
            starts.Add((node, -1, t => new Timing.Clock(t, 0)));
        foreach (var name in new[] { "vid1", "0:v:0", "0:v" })
        {
            foreach (var consumer in graph.ConsumersOf(name))
                starts.Add((consumer.Node, consumer.Pad, t => new Timing.Clock(t, live ? Math.Max((at ?? 0) - FilterGraphBuilder.LiveLookBehind, 0) : 0)));
        }

        foreach (var (node, pad, clockAt) in starts)
        {
            double? Through(double sequence)
            {
                var arrivals = Timing.Walk(graph, node, pad, clockAt(sequence), state.SourceFrameRate,
                    (n, p) => ReferenceEquals(n, overlay) ? (p == 0 ? true : null) : IsOverlay(n) && p >= 1 ? null : false);
                return arrivals.Count > 0 ? arrivals[0].Clock.T : null;
            }

            var (first, second) = (10.0, 20.0);
            if (Through(first) is { } a && Through(second) is { } b && Math.Abs(b - a) > 1e-9)
            {
                var scale = (second - first) / (b - a);
                return (scale, first - a * scale);
            }
        }

        return null;
    }

    private static bool IsOverlay(GraphNode node) => node.Name is "overlay" or "blend" or "overlay_opencl" or "overlay_cuda";

    // ----- The sound -----

    private static double? Heard((FilterGraph Graph, GraphNode Node) source, Media media, double moment, SequenceExportState state, bool live, double audioFrom, out string why)
    {
        why = "";
        var (graph, amovie) = source;
        var clock = new Timing.Clock(moment, Landing(amovie, media, false, video: false));
        List<Timing.Arrival> arrivals;
        try
        {
            arrivals = Timing.Walk(graph, amovie, -1, clock, 48000, MixStop);
        }
        catch (Timing.Unsupported ex)
        {
            why = ex.Message;
            return null;
        }

        if (arrivals.Count == 0)
        {
            why = WhyDropped(graph, amovie, clock, 48000);
            return null;
        }

        var mix = arrivals[0].Node;
        var position = arrivals[0].Clock.T - arrivals[0].Clock.First;
        var start = MixStart(graph, mix, state, live, audioFrom);
        if (start is null)
        {
            why = "where the mix it goes into starts on the sequence could not be followed";
            return null;
        }

        return position + start.Value;
    }

    private static bool? MixStop(GraphNode node, int pad) =>
        node.Name == "amix" ? true : node.Name == "sidechaincompress" && pad >= 1 ? null : false;

    /// <summary>
    /// The moment of the sequence the mix's first sample is: its first input followed from the main video's track (or the
    /// silence). Live, the player's track is the sequence's own clock. In an export the track is the file's, placed where the
    /// main file's clips are (cut, held back, mixed clip by clip): a moment of the file is followed from where the timeline
    /// says it is heard, a little into each clip of the main video in turn (a clip plays its file from its offset at its start).
    /// </summary>
    private static double? MixStart(FilterGraph graph, GraphNode mix, SequenceExportState state, bool live, double audioFrom)
    {
        // Through the mixes that place the track clip by clip (followed), to this one.
        bool? Stop(GraphNode node, int pad) => ReferenceEquals(node, mix) ? (pad == 0 ? true : null) : node.Name == "sidechaincompress" && pad >= 1 ? null : false;

        // (file moment, sequence moment) pairs to follow: the sequence's own moments live; in an export, the file's clips.
        var probes = live
            ? [(audioFrom + 5, audioFrom + 5)]
            : state.Timeline.Tracks.SelectMany(t => t.Clips)
                .Where(c => c.ContentKind == LayerKind.MainVideo && c.SourcePath is not null && SamePath(c.SourcePath, state.LocalMediaPath) && Math.Abs(c.Speed - 1) < 0.0005)
                .Select(c => (File: c.MediaOffset + Math.Min(0.5, c.Duration / 2), Sequence: c.Start + Math.Min(0.5, c.Duration / 2)))
                .Append((5.0, 5.0))
                .ToList();

        var tracks = graph.ExternalInputs().Where(l => l.StartsWith("aid", StringComparison.Ordinal) || l.StartsWith("0:a:", StringComparison.Ordinal));
        foreach (var consumer in tracks.SelectMany(graph.ConsumersOf))
        {
            // A track of the main video: where its sample of a moment is in the mix, counted from the mix's first sample.
            double? Position(double moment)
            {
                var arrivals = Timing.Walk(graph, consumer.Node, consumer.Pad, new Timing.Clock(moment, live ? audioFrom : 0), 48000, Stop);
                return arrivals.Count > 0 ? arrivals[0].Clock.T - arrivals[0].Clock.First : null;
            }

            foreach (var (file, sequence) in probes)
            {
                if (Position(file) is { } position)
                    return sequence - position;
            }
        }

        // Silence standing in for the main video's sound: it is laid from the moment the sound is laid out from.
        foreach (var silence in graph.Nodes.Where(n => n.IsSource && n.Name == "anullsrc"))
        {
            if (Timing.Walk(graph, silence, -1, new Timing.Clock(5, 0), 48000, Stop) is [var arrival, ..])
                return 5 - (arrival.Clock.T - arrival.Clock.First) + (live ? audioFrom : 0);
        }

        return null;
    }

    // ----- Seeking -----

    /// <summary>
    /// The first frame or sample a source gives, on the file's clock: the stream's start, or where its seek lands. sp= and
    /// seek_point= count from the container's start (a proxy's is 0). A picture lands on the I-frame at or before the aim.
    /// A sound lands on a packet at or before it, but which one is the demuxer's to say (an MPEG-TS seek aimed at 18.4 s was
    /// measured landing at 18.08 s): so the sound is taken to land on its cut when it aims at or past it, and an aim past the
    /// cut is reported on its own by <see cref="CheckSeek"/>.
    /// </summary>
    private static double Landing(GraphNode source, Media media, bool proxy, bool video)
    {
        var start = proxy ? 0 : video ? media.VideoStart : media.AudioStart;
        if (Aim(source, media, proxy) is not { } target)
            return start;

        if (!video)
            return Math.Max(start, FirstCut(source) is { } cut ? Math.Min(target, cut) : target);

        var frames = proxy ? media.IFrames.Select(f => f - media.VideoStart).ToArray() : media.IFrames;
        var landing = frames.Where(f => f <= target + 1e-9).DefaultIfEmpty(start).Max();
        return Math.Max(start, landing);
    }

    /// <summary>Where a source's seek aims on the file's clock; null when it reads from the start.</summary>
    private static double? Aim(GraphNode source, Media media, bool proxy)
    {
        var video = source.Name == "movie";
        if (source.Options.Get(video ? "sp" : "seek_point", -1, video ? "seek_point" : "sp") is not { } seek)
            return null;
        return (proxy ? 0 : media.ContainerStart) + double.Parse(seek, CultureInfo.InvariantCulture);
    }

    /// <summary>The first cut after a source, before anything else changes the time stamps: on the file's clock.</summary>
    private static double? FirstCut(GraphNode source)
    {
        for (var next = source.Next; next is not null; next = next.Next)
        {
            if (next.Name is "setpts" or "asetpts")
                return null;
            if (next.Name is "trim" or "atrim" && next.Options.Get("start", 0) is { } start)
                return double.Parse(start, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static IEnumerable<string> CheckSeek((FilterGraph Graph, GraphNode Node) source, Media media, bool proxy, string name)
    {
        var (_, node) = source;
        if (FirstCut(node) is not { } cut)
            yield break;

        if (node.Name == "movie")
        {
            var landing = Landing(node, media, proxy, video: true);
            if (landing > cut + 0.001)
                yield return $"{name}: the seek lands at {Num(landing)} s on the file's clock, after the cut at {Num(cut)} s: what lies between is lost.";
        }
        else if (Aim(node, media, proxy) is { } target && target > cut + 0.001)
        {
            yield return $"{name}: the seek aims at {Num(target)} s on the file's clock, past the cut at {Num(cut)} s (seek_point counts from the container's start, "
                         + $"{Num(media.ContainerStart)} s here): where it lands is up to the demuxer, and up to {Num(target - cut)} s of the cut's start may be lost.";
        }
    }

    // ----- Finding things -----

    private static (FilterGraph Graph, GraphNode Node)? FindSource(List<FilterGraph> graphs, string name, string path)
    {
        foreach (var graph in graphs)
        {
            var found = graph.Nodes.Where(n => n.Name == name && n.Options.Get("filename", 0) is { } file && SamePath(file, path)).ToList();
            if (found.Count == 1)
                return (graph, found[0]);
            if (found.Count > 1)
                throw new InvalidOperationException($"{path} is read by more than one {name} in a graph: a fixture for the A/V check has each file once.");
        }

        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().Trim('"').Replace('\\', '/'), b.Trim().Trim('"').Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string WhyDropped(FilterGraph graph, GraphNode source, Timing.Clock clock, double rate)
    {
        // Along the chain from the source, the first filter that drops the moment says why.
        for (var node = source.Next; node is not null; node = node.Next)
        {
            try
            {
                clock = Timing.Apply(node, clock, rate);
            }
            catch (Timing.Dropped dropped)
            {
                return dropped.Message;
            }
            catch (Timing.Unsupported unsupported)
            {
                return unsupported.Message;
            }
        }

        return "it reaches no overlay or mix";
    }

    /// <summary>The frame of the file at or after a moment.</summary>
    private static double OnFrame(Media media, double moment) =>
        media.VideoStart + Math.Ceiling((moment - media.VideoStart) * media.Fps - 1e-6) / media.Fps;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
