using System.Globalization;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.Services.Gpu;

namespace HandPegGoldenTests;

/// <summary>One timeline the GPU side of the A/V invariant is checked on: its files, and the playheads the moments are followed from.</summary>
/// <param name="Playheads">0 for the moments from each clip's start (an export's); a later playhead follows them from there on.</param>
public sealed record GpuAvFixture(string Name, SequenceExportState State, IReadOnlyList<Media> Files, IReadOnlyList<double> Playheads)
{
    /// <summary>What was found wrong with the fixture itself (its document's problems), reported with the case's.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public static partial class AvCheck
{
    // The plan's and the program's media times are both worked out in seconds; nothing is written to the millisecond.
    private const double GpuTolerance = 0.0005;

    /// <summary>
    /// The A/V invariant on the GPU engine (5.0-Verify, in place of the live graphs' arm): at the moments the export's check
    /// follows (and as many into every clip of the main video), every layer <see cref="GpuScenePlan"/> draws from a file is at
    /// the moment of that file which <see cref="AudioProgram"/> plays for its clip's sound there, within half a millisecond;
    /// and a clip's picture is at the moment the timeline puts there (its offset plus how far into the clip, at its speed).
    /// Both are in seconds from the file's first video frame (the plan's class comment). A clip whose sound is slipped is
    /// heard as far from its picture as the slip; a piece cut from the main video, and its blurred background, are its
    /// picture, so they are held to the main video's sound. The sound a layer is held to is the voice of its file heard then
    /// that is nearest to it (the main file's streams all play the same moment; two clips of one file rarely overlap), and
    /// which one it was is in the text; where that voice is past its file's end, nothing is heard to be held to (a repeating
    /// clip's sound is played once, in the export too). No worker and no file is needed: the plan and the program are worked out.
    /// </summary>
    public static GraphResult GpuInvariant(IEnumerable<GpuAvFixture> fixtures)
    {
        var text = new StringBuilder("# The A/V invariant on the GPU engine: at each moment, every layer the scene plan (GpuScenePlan) draws from a file\n"
                                     + "# against the moment of that file the sound program (AudioProgram) plays for its clip there (within 0.5 ms), and against\n"
                                     + "# the timeline; seconds from the file's first video frame. picture = sound (the voice heard), or why nothing is heard.\n");
        var problems = new List<string>();
        var moments = 0;
        using var broker = new GpuResourceBroker(null, bakeOnCallingThread: true);
        var layers = new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var infos = new GpuLayerInfo[CompositorProtocol.MaxLayers];
        foreach (var fixture in fixtures)
        {
            problems.AddRange(fixture.Problems.Select(p => $"{fixture.Name}: {p}"));
            try
            {
                var state = fixture.State;
                var plan = GpuScenePlan.Create(state, broker);
                var program = AudioProgram.Build(state);
                var clips = state.Timeline.Tracks.SelectMany(t => t.Clips.Select(c => (Clip: c, Track: t))).ToDictionary(p => p.Clip.Id);
                var times = fixture.Playheads.SelectMany(p => GpuMoments(state, fixture.Files, p)).Select(t => Math.Round(t, 6)).Distinct().Order().ToList();
                text.Append(CultureInfo.InvariantCulture, $"\n{fixture.Name}: {times.Count} moments, {program.Voices.Count} voices, chain shift {Num(plan.ChainShift)} s\n");
                foreach (var t in times)
                {
                    var header = default(FrameSceneHeader);
                    var count = plan.Evaluate(t, ref header, layers);
                    var described = plan.LayerInfoAt(t, infos);
                    var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"  at {t:0.000} s:"));
                    var separator = " ";
                    for (var i = 0; i < count && i < described; i++)
                    {
                        if (layers[i].SourceKind != (int)GpuSourceKind.Media)
                            continue;
                        line.Append(separator).Append(Describe(plan, program, state, clips, fixture.Files, layers[i], infos[i], t, fixture.Name, problems));
                        separator = "; ";
                    }

                    if (separator == " ")
                        line.Append(" nothing from a file");
                    text.Append(line).Append('\n');
                    moments++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"{fixture.Name}: threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        return new GraphResult("gpu-av-invariant", text.ToString(), problems, moments);
    }

    /// <summary>One layer at a moment: its picture's moment, the sound it is held to, and what is wrong (into the problems).</summary>
    private static string Describe(GpuScenePlan plan, AudioProgram program, SequenceExportState state, Dictionary<Guid, (TimelineClip Clip, TimelineTrackModel Track)> clips,
        IReadOnlyList<Media> files, in GpuLayerDescriptor layer, GpuLayerInfo info, double t, string fixture, List<string> problems)
    {
        var picture = layer.MediaSeconds;
        var blurred = (layer.Flags & GpuLayerDescriptor.FlagBlurFillOnly) != 0;
        var name = $"{(info.Name is { Length: > 0 } n ? n : "?")}{(blurred ? " (blurred)" : "")}";
        var where = string.Create(CultureInfo.InvariantCulture, $"{fixture} at {t:0.000} s, {name}");
        if (!Guid.TryParse(info.ClipId, out var id) || !clips.TryGetValue(id, out var found))
        {
            problems.Add($"{where}: the layer's clip ({info.ClipId}) is not on the timeline");
            return $"{name} {Num4(picture)}, of no clip";
        }

        var (clip, track) = found;
        var main = SamePath(clip.SourcePath ?? "", state.LocalMediaPath);

        // A piece cut from the main video (and the chain's blurred background) is the main video's picture: its sound is the main file's.
        var ownSound = clip.ContentKind is LayerKind.VideoFile or LayerKind.MainVideo;
        var heard = ownSound ? clip.HasSound && !clip.IsMuted && !track.IsMuted : main;

        // Where the timeline puts the picture of a clip that plays its file (in it: a repeating clip's later rounds are its own).
        if (ownSound && t >= clip.Start - 1e-9 && t < clip.End - 1e-9 && !blurred)
        {
            var speed = Math.Clamp(clip.Speed, Layer.SlowestSpeed, Layer.FastestSpeed);
            // (The main video's offsets count from its container's start: GpuScenePlan.ChainShift.)
            var due = clip.MediaOffset + (t - clip.Start) * speed - (clip.ContentKind == LayerKind.MainVideo ? plan.ChainShift : 0);
            if (Math.Abs(picture - due) > GpuTolerance)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: the picture is at {picture:0.0000} s of its file, the timeline puts {due:0.0000} s there"));
        }

        if (!heard)
            return $"{name} {Num4(picture)}, not heard";

        var slip = ownSound && !clip.AudioLinked ? clip.AudioSlipSeconds : 0;
        var at = t + slip;
        AudioVoice? voice = null;
        var sound = double.NaN;
        foreach (var candidate in program.Voices)
        {
            if (!SamePath(candidate.Source.Path, clip.SourcePath ?? "") || at < candidate.SequenceStart - 1e-9 || at >= candidate.End)
                continue;
            if (candidate.Silenced.Any(s => at >= s.Start && at < s.End))
                continue;
            var media = candidate.MediaAt(at);
            if (voice is null || Math.Abs(media - picture) < Math.Abs(sound - picture))
                (voice, sound) = (candidate, media);
        }

        if (voice is null)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: its picture ({picture:0.0000} s of its file) is seen, but no voice of its file is heard{(slip != 0 ? $" {slip:0.###} s on (its slip)" : "")}"));
            return $"{name} {Num4(picture)}, no sound";
        }

        // Past the end of its file (a repeating clip's later rounds: its sound is played once, as the export's amovie plays it), nothing is heard.
        if (files.FirstOrDefault(m => SamePath(m.Path, clip.SourcePath ?? "")) is { } file && sound >= file.Seconds - 1e-6)
            return $"{name} {Num4(picture)}, silent ({voice.Label} at {Num4(sound)}, past its file's end)";

        if (Math.Abs(sound - picture) > GpuTolerance)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: the picture is at {picture:0.0000} s of its file, its sound ({voice.Label}) at {sound:0.0000} s: {(sound - picture) * 1000:+0.00;-0.00} ms"));
        return $"{name} {Num4(picture)} = {Num4(sound)} ({voice.Label}{(slip != 0 ? $", {Num(slip)} s on" : "")})";
    }

    /// <summary>
    /// The moments followed on a timeline from a playhead: those of the export's check (each heard video clip of another file,
    /// on its frames) and as many into every clip of the main video (on the sequence's frames).
    /// </summary>
    private static IEnumerable<double> GpuMoments(SequenceExportState state, IReadOnlyList<Media> files, double playhead)
    {
        foreach (var layer in HeardLayers(state))
        {
            if (files.FirstOrDefault(m => SamePath(m.Path, layer.SourcePath!)) is { HasAudio: true } media)
            {
                foreach (var (_, sequence) in FileMoments(layer, media, playhead))
                    yield return sequence;
            }
        }

        var fps = state.SourceFrameRate > 1 ? state.SourceFrameRate : 30;
        foreach (var clip in state.Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.ContentKind == LayerKind.MainVideo && !c.IsHidden))
        {
            var into = Math.Max(playhead - clip.Start, 0);
            foreach (var offset in Offsets)
            {
                var moment = Math.Ceiling((into + offset) * fps - 1e-6) / fps;
                if (moment < clip.Duration - 0.05)
                    yield return clip.Start + moment;
            }
        }
    }

    private static string Num4(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
}
