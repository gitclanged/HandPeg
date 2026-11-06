using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Tools.AvSync;

/// <summary>What was heard and seen at one moment, against what the files say should have been.</summary>
/// <param name="What">What was measured: a clip's file (its extension only), heard by its own sound ("clip") or by its Sub-Lanes ("lanes").</param>
/// <param name="VideoLateMs">
/// How late the picture is, in milliseconds: from the moment of its file the scene plan gives the worker and the file's
/// own time stamps (exact), checked against which frames the output really shows (<paramref name="VideoRange"/>).
/// Positive: an earlier frame than due is shown.
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
/// Measures what the GPU engine made from a moment (<see cref="GpuRun"/>: the worker's frames and the mixer's sound on the
/// sequence's clock): which part of the file the sound is from (cross-correlation with the file's own sound, to the
/// sample), and which frames the picture shows (each output frame matched against the file's frames around where it
/// should be). Where the picture is and what it reads come from the scene plan itself (Evaluate and LayerInfoAt: the
/// layer of the measured clip, its box, its crop and its moment of the file), as the worker was told them.
/// </summary>
internal static class Measurement
{
    // How much of the output is compared: from a little after the start (the first moments after a seek are allowed to
    // settle) for this long.
    private const double SoundSettle = 0.4, SoundLength = 2.0, SoundWindow = 1.0;

    public static PointResult Measure(SequenceExportState state, GpuScenePlan plan, GpuRun run, double at, double seconds, Shift shift)
    {
        var notes = new List<string>();

        // What is to be heard at this moment (the timeline as it is lowered: TimelineLowering.Lowering, which the plan and the sound
        // program read too). No clip is the main one (5.2-AuditB): a video clip's own sound (the top one that plays here), or else a
        // video clip heard through its streams on its Sub-Lanes (sound clips that are its children, as the main video's are once a
        // project from before is opened), each measured on its clip's own clock.
        var timeline = HandPegApp.Services.Gpu.TimelineLowering.Lowering(state);
        var document = state.Timeline;
        bool Plays(Layer l) => l.StartTime <= at + 0.001 && (l.Duration <= 0.001 || at < l.StartTime + l.Duration - 0.5);
        if (timeline.LayerAudio.LastOrDefault(l => l.IsVideoFile && Plays(l)) is { } own && document.FindClip(own.ClipId) is { } clip)
            return MeasureClip(state, plan, run, at, seconds, clip, [Math.Max(own.AudioStream, 0)], "clip", shift, notes);

        foreach (var lane in timeline.LayerAudio.Reverse())
        {
            if (!lane.IsAudio || !Plays(lane) || document.FindClip(lane.ClipId) is not { ParentClipId: { } parentId }
                || document.FindClip(parentId) is not { ContentKind: LayerKind.VideoFile } picture || picture.Start > at || at >= picture.End - 0.5)
                continue;

            // Its streams heard here, by their numbers in the file (what the reference is decoded from).
            var streams = timeline.LayerAudio.Where(l => l.IsAudio && Plays(l) && document.FindClip(l.ClipId)?.ParentClipId == parentId)
                .Select(l => Math.Max(l.AudioStream, 0)).Distinct().ToList();
            return MeasureClip(state, plan, run, at, seconds, picture, streams, "lanes", shift, notes);
        }

        return new PointResult(at, "nothing", null, null, 0, 0, null, 0, "no video clip heard by its own sound or its Sub-Lanes plays here");
    }

    /// <summary>
    /// A clip of a file at a moment, on its own clock: a moment t of the sequence shows, and plays from <paramref name="streams"/>,
    /// the file's time stamp origin + MediaOffset + (t - start) x speed (its moment 0 its first picture, as the export cuts it).
    /// </summary>
    private static PointResult MeasureClip(SequenceExportState state, GpuScenePlan plan, GpuRun run, double at, double seconds, TimelineClip clip,
        IReadOnlyList<int> streams, string what, Shift shift, List<string> notes)
    {
        // Never across the clip's end: what follows a cut is another clip's picture and sound, on another clock (its frames could only
        // disagree with this one's), so a moment near a cut is measured up to it.
        if (clip.End - at - 0.05 < seconds)
        {
            seconds = Math.Max(clip.End - at - 0.05, 0.5);
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"measured up to the clip's end ({seconds:0.###} s)"));
        }

        var file = (clip.SourcePath ?? "").Trim().Trim('"');
        var speed = double.IsFinite(clip.Speed) ? Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
        var origin = Media.StreamStart(file, "v") ?? 0;
        double Due(double t) => origin + clip.MediaOffset + (t - clip.Start) * speed;

        var (audioLate, score) = LocateSound(run.Output, at, file, streams, Due, seconds, notes);

        // The picture: the clip's layer the plan draws (the worker's moment of it is from its first frame).
        var probe = at + SoundSettle;
        var drawn = Find(state, plan, probe - shift.VideoSeconds, c => c.Id == clip.Id);
        double? videoLate = null;
        (double, double)? range = null;
        int matched = 0, compared = 0;
        var proxied = false;
        if (drawn is { } d)
        {
            proxied = d.Clip.ProxyChoice == ProxyChoice.Make && state.IsPreview && state.PreviewPaths.ContainsKey(file);
            videoLate = (Due(probe) - (d.Layer.MediaSeconds + origin)) / speed * 1000;
            (range, matched, compared) = MatchFrames(run, at, seconds, file, Placement(d.Layer, run, notes), f => clip.Start + (f - origin - clip.MediaOffset) / speed, notes);
        }
        else
        {
            notes.Add("the clip is not in the scene the plan draws");
        }

        return new PointResult(at, $"{what} {System.IO.Path.GetExtension(file)}{(proxied ? " (proxy)" : "")}", videoLate, range, matched, compared, audioLate, score, string.Join("; ", notes));
    }

    /// <summary>
    /// The topmost layer the plan draws at a moment for a clip that <paramref name="wanted"/> picks (its picture, not a
    /// blurred background): the descriptor the worker is given and the clip it is of (LayerInfoAt, in Evaluate's order).
    /// </summary>
    private static (GpuLayerDescriptor Layer, TimelineClip Clip)? Find(SequenceExportState state, GpuScenePlan plan, double moment, Func<TimelineClip, bool> wanted)
    {
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var infos = new GpuLayerInfo[CompositorProtocol.MaxLayers];
        var header = default(FrameSceneHeader);
        var count = Math.Min(plan.Evaluate(moment, ref header, layers), plan.LayerInfoAt(moment, infos));
        var clips = state.Timeline.Tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Id);
        for (var i = count - 1; i >= 0; i--)
        {
            if (layers[i].SourceKind != (int)GpuSourceKind.Media || (layers[i].Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0)
                continue;
            if (Guid.TryParse(infos[i].ClipId, out var id) && clips.TryGetValue(id, out var clip) && wanted(clip))
                return (layers[i], clip);
        }

        return null;
    }

    /// <summary>
    /// Where the output's sound at <paramref name="at"/> is found in <paramref name="file"/>, near where it is due; and if
    /// it is not found there, anywhere from a little before that to as far after it as the moment is into the sequence.
    /// Returns the lateness in milliseconds and the correlation.
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
            reference = Media.Sound(file, Math.Max(near - 0.1, 0), length + 0.2, streams);
            (lag, score, runner) = Media.Locate(heard, reference);
            found = Math.Max(near - 0.1, 0) + lag / Media.Rate;
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"the sound is far from where it is due: {(expected - near) * 1000:+0;-0} ms by a coarse match ({roughScore:0.00}) at {near:0.###} s of the file, due at {expected:0.###} s"));
            if (double.IsNaN(lag))
                return (null, 0);
        }

        if (runner > score * 0.9)
            notes.Add($"the sound's match is not unique ({score:0.000} against {runner:0.000})");
        return ((expected - found) * 1000, score);
    }

    /// <summary>
    /// The visible part of a picture in the output frame (x, y, width, height), the size its box is scaled to, where the
    /// visible part begins inside it, and the part of the file's picture the box shows (its crop, FFmpeg's crop of the file's
    /// frames; null: the whole picture).
    /// </summary>
    internal readonly record struct Placed((int X, int Y, int W, int H) Rect, (int X, int Y) Inside, (int W, int H) Scaled, string? SourceCrop);

    /// <summary>A layer's place in the output, from its descriptor (canvas pixels, the crop as fractions of its picture); null when it is turned.</summary>
    private static Placed? Placement(in GpuLayerDescriptor d, GpuRun run, List<string> notes)
    {
        if (Math.Abs(d.Rotation) > 0.01)
        {
            notes.Add("the picture is turned: its frames are not matched");
            return null;
        }

        var s = run.Scale;
        var place = ((int)Math.Round(d.X * s), (int)Math.Round(d.Y * s));
        var scaled = (Math.Max((int)Math.Round(d.Width * s), 2), Math.Max((int)Math.Round(d.Height * s), 2));
        var whole = d.CropLeft <= 0.0005 && d.CropTop <= 0.0005 && d.CropRight >= 0.9995 && d.CropBottom >= 0.9995;
        string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var crop = whole ? null
            : $"crop=iw*{F(d.CropRight - d.CropLeft)}:ih*{F(d.CropBottom - d.CropTop)}:iw*{F(d.CropLeft)}:ih*{F(d.CropTop)}";
        return Visible(place, scaled, run.Size, crop);
    }

    private static Placed Visible((int X, int Y) place, (int W, int H) scaled, (int W, int H) frame, string? crop)
    {
        var x0 = Math.Max(place.X, 0);
        var y0 = Math.Max(place.Y, 0);
        var x1 = Math.Min(place.X + scaled.W, frame.W);
        var y1 = Math.Min(place.Y + scaled.H, frame.H);
        return new Placed((x0, y0, Math.Max(x1 - x0, 0), Math.Max(y1 - y0, 0)), (x0 - place.X, y0 - place.Y), scaled, crop);
    }

    /// <summary>
    /// Matches every output frame from <paramref name="at"/> on to the file's frames around where it is due, and returns
    /// the range of the picture's lateness (ms) that every match allows, with how many frames matched surely. A frame
    /// shown at moment p that is the file's frame k says the lateness d is in (p - due(k + 1), p - due(k)].
    /// </summary>
    private static ((double Lo, double Hi)? Range, int Matched, int Compared) MatchFrames(GpuRun run, double at, double seconds, string file,
        Placed? visible, Func<double, double> shownAt, List<string> notes)
    {
        if (visible is not { Rect.W: > 8, Rect.H: > 8 } place)
        {
            notes.Add("the picture is not on screen");
            return (null, 0, 0);
        }

        var (w, h) = place.Rect.W >= place.Rect.H ? (64, Math.Max((int)Math.Round(64.0 * place.Rect.H / place.Rect.W), 8)) : (Math.Max((int)Math.Round(64.0 * place.Rect.W / place.Rect.H), 8), 64);
        var shown = Media.Frames(run.Output, at, seconds, $"crop={place.Rect.W}:{place.Rect.H}:{place.Rect.X}:{place.Rect.Y}", w, h, seek: false);

        // The file's frames, cut and scaled as the worker draws them and cut to the part that shows.
        var cut = place.Rect.W == place.Scaled.W && place.Rect.H == place.Scaled.H ? null : $"crop={place.Rect.W}:{place.Rect.H}:{place.Inside.X}:{place.Inside.Y}";
        var filters = string.Join(",", new[] { place.SourceCrop, cut is null && place.SourceCrop is null ? null : $"scale={place.Scaled.W}:{place.Scaled.H}", cut }.OfType<string>());
        var firstDue = Inverse(shownAt, at);
        var lastDue = Inverse(shownAt, at + seconds);
        var files = Media.Frames(file, firstDue - 0.6, lastDue - firstDue + 1.2, filters.Length == 0 ? null : filters, w, h);
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

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(a.Trim().Trim('"').Replace('\\', '/'), b.Trim().Trim('"').Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
