using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.Tools.AvSync;

/// <summary>What was heard and seen at one moment, against what the files say should have been.</summary>
/// <param name="What">What was measured: the layer's file (its extension only) or the main video.</param>
/// <param name="VideoLateMs">
/// How late the picture is, in milliseconds: from the graph's own time stamps and the files' (exact), checked against
/// which frames the output really shows (<paramref name="VideoRange"/>). Positive: an earlier frame than due is shown.
/// </param>
/// <param name="VideoRange">The lateness the frames shown allow, from matching every output frame to the file's frames.</param>
/// <param name="AudioLateMs">How late the sound is, from where the output's sound is found in the file. Positive: behind.</param>
internal sealed record PointResult(
    double At, string What, double? VideoLateMs, (double Lo, double Hi)? VideoRange, int FramesMatched, int FramesCompared,
    double? AudioLateMs, double AudioScore, string Notes)
{
    public double? SyncMs => VideoLateMs is { } video && AudioLateMs is { } audio ? audio - video : null;

    /// <summary>Within the tolerance: the sound found surely, the frames shown consistent with the picture's lateness, and the two close enough.</summary>
    public bool Passes(double toleranceMs) =>
        SyncMs is { } sync && Math.Abs(sync) < toleranceMs && AudioScore >= 0.6
        && (VideoRange is not { } range || (VideoLateMs >= range.Lo - 1 && VideoLateMs <= range.Hi + 1));
}

/// <summary>
/// Measures an output of the player (written by mpv's encoding mode on the sequence's clock) at a moment: which part of
/// the file the sound is from (cross-correlation with the file's own sound, to the sample), and which frames the
/// picture shows (each output frame matched against the file's frames around where it should be).
/// </summary>
internal static partial class Measurement
{
    // How much of the output is compared: from a little after the start (the first moments after a restart are allowed
    // to settle) for this long.
    private const double SoundSettle = 0.4, SoundLength = 2.0, SoundWindow = 1.0;

    public static PointResult Measure(SequenceExportState state, string graph, string output, double at, double seconds,
        IReadOnlyDictionary<string, string> proxies)
    {
        var notes = new List<string>();

        // What is to be heard at this moment: a layer's sound (the top one that plays here), or else the main video's.
        var layer = state.LayerAudio.LastOrDefault(l => l.StartTime <= at + 0.001 && (l.Duration <= 0.001 || at < l.StartTime + l.Duration - 0.5));
        if (layer is not null)
            return MeasureLayer(state, graph, output, at, seconds, layer, proxies, notes);
        if (state.MainClips.FirstOrDefault(c => c.Start <= at && at < c.End - 0.5) is { End: > 0 } clip)
            return MeasureMain(state, graph, output, at, seconds, clip, proxies, notes);
        return new PointResult(at, "nothing", null, null, 0, 0, null, 0, "neither a clip of the main video nor a layer with sound plays here");
    }

    private static PointResult MeasureLayer(SequenceExportState state, string graph, string output, double at, double seconds, Layer layer,
        IReadOnlyDictionary<string, string> proxies, List<string> notes)
    {
        var file = layer.ImagePath.Trim().Trim('"');
        var speed = layer.HasSpeed && double.IsFinite(layer.Speed) ? layer.Speed : 1;

        // The truth, from the file itself: its moment 0 is its first picture (as the export cuts it), so a moment t of the
        // sequence shows and plays the file's time stamp origin + MediaOffset + (t - start) x speed.
        var origin = Media.StreamStart(file, "v") ?? 0;
        double Due(double t) => origin + layer.MediaOffset + (t - layer.StartTime) * speed;

        // The sound.
        var (audioLate, score) = LocateSound(output, at, file, [0], Due, seconds, notes);

        // The picture: where the layer is in the output frame, and the lateness its graph gives it.
        double? videoLate = null;
        (double, double)? range = null;
        int matched = 0, compared = 0;
        var proxied = proxies.TryGetValue(file, out var proxy);
        if (FindLayerPlacement(graph, file, proxied ? proxy! : file) is { } placement)
        {
            // Read from a proxy, the graph's time stamps are the proxy's: a proxy's frame q is the original's q + shift.
            var shift = proxied ? origin - (Media.StreamStart(proxy!, "v") ?? 0) : 0;
            if (placement.Shift is { } k && Math.Abs(speed - 1) < 0.0005)
                videoLate = (k - shift - (layer.StartTime - origin - layer.MediaOffset)) * 1000;
            else
                notes.Add("the picture's lateness is taken from the frames alone (the clip's graph is not the plain form)");

            (range, matched, compared) = MatchFrames(output, at, seconds, file, placement.Visible, f => layer.StartTime + (f - origin - layer.MediaOffset) / speed, notes);
            videoLate ??= range is { } r ? (r.Item1 + r.Item2) / 2 : null;
        }
        else
        {
            notes.Add("the layer was not found in the graph");
        }

        return new PointResult(at, $"layer {Path.GetExtension(file)}{(proxied ? " (proxy)" : "")}", videoLate, range, matched, compared, audioLate, score, string.Join("; ", notes));
    }

    private static PointResult MeasureMain(SequenceExportState state, string graph, string output, double at, double seconds, MainClip clip,
        IReadOnlyDictionary<string, string> proxies, List<string> notes)
    {
        var file = state.LocalMediaPath.Trim().Trim('"');

        // The player plays the main video's clip from its file's time stamp Offset at the clip's start (its EDL).
        double Due(double t) => clip.Offset + (t - clip.Start);
        var tracks = state.AudioTracks.Where(t => !t.IsSilentBase).Select(t => t.Index).ToList();
        var (audioLate, score) = tracks.Count == 0 ? (null, 0) : LocateSound(output, at, file, tracks, Due, seconds, notes);

        // The picture fills the frame where it is not placed otherwise; through its proxy, a proxy's frame q is the original's q + shift.
        var proxied = proxies.TryGetValue(file, out var proxy);
        var shift = proxied ? (Media.StreamStart(file, "v") ?? 0) - (Media.StreamStart(proxy!, "v") ?? 0) : 0;
        var visible = FindCenterPlacement(graph);
        var (range, matched, compared) = MatchFrames(output, at, seconds, file, visible, f => clip.Start + (f - clip.Offset), notes);

        // The EDL lays the original's time stamps on the sequence as they are, and the proxy's by its first frame.
        double? videoLate = proxied ? (MediaProbe.KnownVideoStart(file) ?? 0) * 1000 - shift * 1000 : 0;
        return new PointResult(at, $"main video{(proxied ? " (proxy)" : "")}", videoLate, range, matched, compared, audioLate, score, string.Join("; ", notes));
    }

    /// <summary>
    /// Where the output's sound at <paramref name="at"/> is found in <paramref name="file"/>, near where it is due; and if
    /// it is not found there, anywhere from a little before that to as far after it as the moment is into the sequence
    /// (where a graph whose sound is counted from 0 puts it). Returns the lateness in milliseconds and the correlation.
    /// </summary>
    private static (double? LateMs, double Score) LocateSound(string output, double at, string file, IReadOnlyList<int> streams, Func<double, double> due,
        double seconds, List<string> notes)
    {
        var from = at + SoundSettle;
        var length = Math.Min(SoundLength, Math.Max(seconds - SoundSettle - 0.1, 0.5));
        var heard = Media.Sound(output, from, length, [0], seek: false);
        if (Media.Level(heard) < 1e-4)
        {
            notes.Add("the output is silent there");
            return (null, 0);
        }

        var expected = due(from);
        var reference = Media.Sound(file, expected - SoundWindow, length + 2 * SoundWindow, streams);
        var (lag, score, runner) = Media.Locate(heard, reference);
        var found = expected - SoundWindow + lag / Media.Rate;
        if (score < 0.6)
        {
            // Far off: looked for coarsely over a long stretch, then exactly where that points.
            const int coarse = 2000;
            var start = Math.Max(expected - 5, 0);
            var span = Math.Max(at, 10) + 10;
            var rough = Media.Sound(file, start, span, streams, rate: coarse);
            var (roughLag, roughScore, _) = Media.Locate(Media.Sound(output, from, length, [0], seek: false, rate: coarse), rough);
            var near = start + roughLag / coarse;
            reference = Media.Sound(file, near - 0.1, length + 0.2, streams);
            (lag, score, runner) = Media.Locate(heard, reference);
            found = near - 0.1 + lag / Media.Rate;
            notes.Add($"the sound is far from where it is due (coarse match {roughScore:0.00})");
        }

        if (runner > score * 0.9)
            notes.Add($"the sound's match is not unique ({score:0.000} against {runner:0.000})");
        return ((expected - found) * 1000, score);
    }

    /// <summary>The visible part of a picture in the output frame (x, y, width, height) and, in the picture's own pixels as scaled, where that part begins.</summary>
    internal readonly record struct Placement((int X, int Y, int W, int H) Rect, (int X, int Y) Inside, (int W, int H) Scaled, (int W, int H) Frame);

    /// <summary>
    /// A layer's chain in the graph: the movie= source reading <paramref name="read"/>, scaled to its size, laid on the
    /// frame by an overlay at its place. Returns where it shows and the plain setpts shift that puts it on the clock.
    /// </summary>
    private static (Placement Visible, double? Shift)? FindLayerPlacement(string graph, string file, string read)
    {
        var chains = graph.Split(';');
        var escaped = FilterGraphBuilderPath(read);
        var chain = chains.FirstOrDefault(c => c.StartsWith("movie='", StringComparison.Ordinal) && c.Contains(escaped, StringComparison.Ordinal));
        if (chain is null)
            return null;

        var label = LabelAtEnd().Match(chain);
        var scales = ScaleTo().Matches(chain);
        var overlay = chains.Select(c => OverlayOf().Match(c)).FirstOrDefault(m => m.Success && m.Groups[1].Value == label.Groups[1].Value);
        if (!label.Success || scales.Count == 0 || overlay is null || FrameSize(graph) is not { } frame)
            return null;

        var scaled = (W: Int(scales[^1].Groups[1].Value), H: Int(scales[^1].Groups[2].Value));
        var place = (X: Int(overlay.Groups[2].Value), Y: Int(overlay.Groups[3].Value));
        double? shift = SetptsShift().Match(chain) is { Success: true } s ? double.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        return (Visible(place, scaled, frame), shift);
    }

    /// <summary>The main video's picture: laid on the frame as the center layer, or the whole frame.</summary>
    private static Placement? FindCenterPlacement(string graph)
    {
        if (FrameSize(graph) is not { } frame)
            return null;
        var chains = graph.Split(';');
        var chain = chains.FirstOrDefault(c => c.StartsWith("[center_in]", StringComparison.Ordinal));
        var overlay = chains.Select(c => OverlayOf().Match(c)).FirstOrDefault(m => m.Success && m.Groups[1].Value == "center_layer");
        if (chain is null || overlay is null || ScaleTo().Matches(chain) is not { Count: > 0 } scales)
            return Visible((0, 0), frame, frame);
        return Visible((Int(overlay.Groups[2].Value), Int(overlay.Groups[3].Value)), (Int(scales[^1].Groups[1].Value), Int(scales[^1].Groups[2].Value)), frame);
    }

    private static Placement Visible((int X, int Y) place, (int W, int H) scaled, (int W, int H) frame)
    {
        var x0 = Math.Max(place.X, 0);
        var y0 = Math.Max(place.Y, 0);
        var x1 = Math.Min(place.X + scaled.W, frame.W);
        var y1 = Math.Min(place.Y + scaled.H, frame.H);
        return new Placement((x0, y0, Math.Max(x1 - x0, 0), Math.Max(y1 - y0, 0)), (x0 - place.X, y0 - place.Y), scaled, frame);
    }

    // The size of the finished frame: the canvas's last scale before [stage0].
    private static (int W, int H)? FrameSize(string graph)
    {
        var canvas = graph.Split(';').FirstOrDefault(c => c.EndsWith("[stage0]", StringComparison.Ordinal));
        return canvas is not null && ScaleTo().Matches(canvas) is { Count: > 0 } scales ? (Int(scales[^1].Groups[1].Value), Int(scales[^1].Groups[2].Value)) : null;
    }

    /// <summary>
    /// Matches every output frame from <paramref name="at"/> on to the file's frames around where it is due, and returns
    /// the range of the picture's lateness (ms) that every match allows, with how many frames matched surely. A frame
    /// shown at moment p that is the file's frame k says the lateness d is in (p - due(k + 1), p - due(k)].
    /// </summary>
    private static ((double Lo, double Hi)? Range, int Matched, int Compared) MatchFrames(string output, double at, double seconds, string file,
        Placement? visible, Func<double, double> shownAt, List<string> notes)
    {
        if (visible is not { Rect.W: > 8, Rect.H: > 8 } place)
        {
            notes.Add("the picture is not on screen");
            return (null, 0, 0);
        }

        var (w, h) = place.Rect.W >= place.Rect.H ? (64, Math.Max((int)Math.Round(64.0 * place.Rect.H / place.Rect.W), 8)) : (Math.Max((int)Math.Round(64.0 * place.Rect.W / place.Rect.H), 8), 64);
        var shown = Media.Frames(output, at, seconds, $"crop={place.Rect.W}:{place.Rect.H}:{place.Rect.X}:{place.Rect.Y}", w, h, seek: false);

        // The file's frames, scaled as the graph scales them and cut to the part that shows.
        var crop = place.Rect.W == place.Scaled.W && place.Rect.H == place.Scaled.H ? null
            : $"scale={place.Scaled.W}:{place.Scaled.H},crop={place.Rect.W}:{place.Rect.H}:{place.Inside.X}:{place.Inside.Y}";
        var firstDue = Inverse(shownAt, at);
        var lastDue = Inverse(shownAt, at + seconds);
        var files = Media.Frames(file, firstDue - 0.6, lastDue - firstDue + 1.2, crop, w, h);
        if (shown.Count == 0 || files.Count < 2)
        {
            notes.Add("no frames to compare");
            return (null, 0, shown.Count);
        }

        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        var matched = 0;
        foreach (var (pts, pixels) in shown)
        {
            var distance = files.Select(f => Difference(pixels, f.Pixels)).ToArray();
            var best = distance.Min();

            // Every frame as close as the best (a picture that holds still) is a candidate; they must be neighbours.
            var close = Enumerable.Range(0, distance.Length).Where(i => distance[i] <= best * 1.15 + 0.4).ToList();
            if (close[^1] - close[0] + 1 != close.Count || close[^1] + 1 >= files.Count || best > 40)
                continue;

            var dLo = pts - shownAt(files[close[^1] + 1].Pts);
            var dHi = pts - shownAt(files[close[0]].Pts);
            if (Environment.GetEnvironmentVariable("AVSYNC_DEBUG") == "1")
                Console.WriteLine($"    frame {pts:0.000}: best {files[close[0]].Pts:0.000}..{files[close[^1]].Pts:0.000} ({best:0.00}), next {distance.Order().Skip(close.Count).FirstOrDefault():0.00}");
            (lo, hi) = (Math.Max(lo, dLo), Math.Min(hi, dHi));
            matched++;
        }

        if (matched == 0)
        {
            notes.Add("no frame matched surely");
            return (null, 0, shown.Count);
        }

        if (lo > hi)
            notes.Add($"the frames shown disagree with each other (from {lo * 1000:0.0} to {hi * 1000:0.0} ms)");
        return ((lo * 1000, hi * 1000), matched, shown.Count);
    }

    // The file's time stamp due at a moment of the sequence, for a mapping that is a straight line.
    private static double Inverse(Func<double, double> shownAt, double moment)
    {
        var (a, b) = (shownAt(0), shownAt(1));
        return (moment - a) / (b - a);
    }

    private static double Difference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    // The file name as FilterGraphBuilder writes it into a graph (EscapeFilterPath): forward slashes, the drive's colon escaped.
    private static string FilterGraphBuilderPath(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\\\\\''");

    private static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\[([A-Za-z0-9_]+)\]$")]
    private static partial Regex LabelAtEnd();

    [GeneratedRegex(@"(?<![a-z_])scale=(\d+):(\d+)")]
    private static partial Regex ScaleTo();

    [GeneratedRegex(@"^\[[A-Za-z0-9_]+\]\[([A-Za-z0-9_]+)\]overlay=(-?\d+):(-?\d+)")]
    private static partial Regex OverlayOf();

    [GeneratedRegex(@",setpts=PTS([+-][0-9.]+)/TB")]
    private static partial Regex SetptsShift();
}
