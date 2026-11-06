using System.IO;
using System.Reflection;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// The Timeline's lanes and switches of Pass 5.2-HotfixAndDeBloat, on a real view model with no file and no FFmpeg:
/// streams-in-line (every clip's streams on the track's shared lanes by their place in the file, and a stream moved onto a lane or a
/// track of its own), clip-switches (a clip's own On/Off leaves its children alone, a track header's takes the family; a child's
/// Lock), effect-blocks (an effect unlocked to a stretch of its own, which the GPU engine keeps to; its On/Off), and effect-rotation
/// (the Rotation of the six spatial effects, in the lane each shader reads, the shared slots' variants where they were).
/// </summary>
public static class TimelineLaneTests
{
    private const int W = 1920, H = 1080;
    private const string A = "D:/media/obs-a.mkv", B = "D:/media/obs-b.mp4";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("streams-in-line", "# Three multi-stream clips on one track: their streams in line on the track's lanes, by place in the file; one moved off.", StreamsInLine);
        yield return Case("clip-switches", "# A clip's own On/Off is its alone; a track header's takes its family; a child's Lock to its clip.", ClipSwitches);
        yield return Case("effect-blocks", "# An effect's block: locked to its clip's span, or unlocked to a stretch of its own the GPU engine keeps to; its On/Off.", EffectBlocks);
        yield return Case("effect-rotation", "# The Rotation of the spatial effects: one more lane each, where its shader reads it, the shared slots' variants kept.", EffectRotation);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "FAILED: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect, text);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // A video clip of a file on a track, with its sound on it (its first stream).
    private static TimelineClip VideoClip(string file, double start, double end, double offset, int firstStream) => new()
    {
        ContentKind = LayerKind.VideoFile, SourcePath = file, Start = start, End = end, MediaOffset = offset, SourceDuration = 10, AudioStreamIndex = firstStream,
        State = new LayerState { Kind = LayerKind.VideoFile, Name = Path.GetFileNameWithoutExtension(file), ImagePath = file, ImageWidth = W, ImageHeight = H, MediaDuration = 10, HasAudio = true, SizeWidth = 0.5 },
    };

    private static MediaInfo Probed(string container, params (int Index, string Title)[] streams) =>
        new(container, 10, 0, new VideoStreamInfo(W, H, "h264", 60, "16:9", "1:1", "bt709", "bt709", 0),
            [.. streams.Select(s => new AudioStreamInfo(s.Index, "aac", 2, "stereo", 48000, 160000, s.Title, ""))], []);

    // ----- streams-in-line -----

    private static void StreamsInLine(Action<bool, string> expect, StringBuilder text)
    {
        var made = new TimelineTrackModel { Kind = TimelineTrackKind.Video };
        made.Clips.AddRange([VideoClip(A, 0, 3, 0, 1), VideoClip(B, 3, 6, 0, 2), VideoClip(A, 6, 8, 3, 1)]);
        var vm = DogfoodTests.Open(made);
        // (The file opened is a clip of a file on the first track too, 5.2-AuditB: the track made here is the one with A's clips.)
        var track = vm.Document.Tracks.First(t => t.Kind == TimelineTrackKind.Video && t.Clips.Any(c => c.SourcePath == A));
        var videos = track.Clips.ToList();

        // B's file numbers its streams from 2 (a data stream first): the lanes go by place in the file, never by number.
        var (infoA, infoB) = (Probed("matroska", (1, "Game"), (2, "Mic"), (3, "Discord")), Probed("mp4", (2, "Game"), (3, "Mic"), (4, "Discord")));
        var unpack = typeof(MainViewModel).GetMethod("UnpackStreams", Hidden)!;
        foreach (var clip in videos)
            unpack.Invoke(vm, [vm.ViewOf(clip)!, clip.SourcePath == A ? infoA : infoB, false]);

        List<TimelineTrackModel> Lanes() => [.. vm.Document.Tracks.Where(t => t.Kind == TimelineTrackKind.Audio && t.ParentTrackId == track.Id)];
        var lanes = Lanes();
        expect(lanes.Count == 3, $"three lanes under the track, not one set per clip ({lanes.Count})");
        expect(lanes.Select(l => l.StreamOrdinal).SequenceEqual(new int?[] { 0, 1, 2 }) && lanes.Select(l => l.Name).SequenceEqual(["Stream 1", "Stream 2", "Stream 3"]),
            "each lane a stream by its place in the file: Stream 1, Stream 2, Stream 3");
        expect(lanes.All(l => l.Clips.Select(c => c.Start).SequenceEqual([0.0, 3.0, 6.0])), "on each lane the three clips' streams in line, one after another");
        expect(lanes.Select((l, k) => l.Clips.Select(c => c.AudioStreamIndex).SequenceEqual([1 + k, 2 + k, 1 + k])).All(x => x),
            "each the stream at that place in its own file (A's 1, 2, 3; B's 2, 3, 4)");
        expect(lanes.All(l => l.Clips.Select((c, k) => c.ParentClipId == videos[k].Id).All(x => x)), "each stream a child of its own video clip");
        expect(videos.All(v => v.AudioStreamIndex == -1), "the video clips silent: nothing heard twice");
        expect(lanes.All(l => vm.StreamOrdinalOf(vm.ViewOf(l.Clips[0])!) == l.StreamOrdinal), "the rows of a stack in stream order");

        // B's Mic onto a lane of its own under the track; A's last Discord onto an Audio track of its own.
        var mic = lanes[1].Clips[1];
        var discord = lanes[2].Clips[2];
        expect(vm.MoveSoundToOwnLane(vm.ViewOf(mic)!, vm.ViewOf(videos[0])!), "a stream dragged onto its video's row: moved");
        var own = vm.Document.TrackOf(mic)!;
        expect(own.ParentTrackId == track.Id && own.StreamOrdinal == -1 && own.Clips.Count == 1 && mic.Start == 3,
            "onto a lane of its own in the track's stack, at its own time, no other stream ever put there");
        expect(mic.ParentClipId == videos[1].Id, "still its video clip's child: it goes with it until unlocked");
        expect(vm.MoveSoundToOwnLane(vm.ViewOf(discord)!, null) && vm.Document.TrackOf(discord) is { ParentTrackId: null, Kind: TimelineTrackKind.Audio },
            "a stream onto an Audio track of its own");
        expect(Lanes().Count(l => l.StreamOrdinal >= 0) == 3 && lanes[1].Clips.Count == 2 && lanes[2].Clips.Count == 2,
            "the shared lanes keep the others' streams in line");
    }

    // ----- clip-switches -----

    private static void ClipSwitches(Action<bool, string> expect, StringBuilder text)
    {
        var picture = new TimelineTrackModel { Kind = TimelineTrackKind.Video };
        picture.Clips.Add(VideoClip(A, 0, 4, 0, -1));
        var cutouts = new TimelineTrackModel { Kind = TimelineTrackKind.Video, ParentTrackId = picture.Id };
        var piece = VideoClip(A, 0, 4, 0, -1);
        piece.State.Name = "Health";
        piece.ParentClipId = picture.Clips[0].Id;
        cutouts.Clips.Add(piece);
        var vm = DogfoodTests.Open(picture, cutouts);
        var (parent, child) = (picture.Clips[0], cutouts.Clips[0]);
        var (parentView, childView) = (vm.ViewOf(vm.Document.FindClip(parent.Id)!)!, vm.ViewOf(vm.Document.FindClip(child.Id)!)!);
        parent = vm.Document.FindClip(parent.Id)!;
        child = vm.Document.FindClip(child.Id)!;
        var track = vm.Document.TrackOf(parent)!;

        vm.SetClipOn(parentView, false);
        expect(parent.IsHidden && parentView.IsHidden && !vm.IsClipOn(parentView), "a clip turned off by its own switch: left out of the picture");
        expect(!child.IsHidden && !childView.IsHidden && vm.IsClipOn(childView), "its Cutout keeps its own switch: still drawn");
        vm.SetClipOn(parentView, true);

        vm.ToggleHidden(parentView);
        expect(track.IsHidden && parentView.IsHidden && childView.IsHidden && !parent.IsHidden && !child.IsHidden,
            "the track header's eye hides the family: the track and its stack, no clip's own switch touched");
        expect(vm.Document.IsHiddenWithParents(vm.Document.TrackOf(child)!), "the stack's tracks are hidden through their parent");
        vm.SetClipOn(parentView, false);
        vm.ToggleHidden(parentView);
        expect(!track.IsHidden && parentView.IsHidden && !childView.IsHidden, "shown again, the clip turned off alone stays off; its Cutout shows");
        vm.SetClipOn(parentView, true);

        expect(vm.ClipLock(childView) == true && vm.ClipLock(parentView) is null, "a Cutout has a Lock, locked to its clip; its clip has none");
        vm.SetClipLocked(childView, false);
        expect(child.ParentClipId is null && vm.ClipLock(childView) == false, "unlocked: no longer its clip's child, free on its own");
        vm.SetClipLocked(childView, true);
        expect(child.ParentClipId == parent.Id && vm.ClipLock(childView) == true, "locked again to the clip it lies over");
    }

    // ----- effect-blocks -----

    private static void EffectBlocks(Action<bool, string> expect, StringBuilder text)
    {
        var vm = DogfoodTests.Open();
        var clip = vm.Document.Tracks[0].Clips[0];
        var view = vm.ViewOf(clip)!;
        var card = EffectRegistry.Find(EffectRegistry.Explosion)!.NewEffect();
        view.AddEffect(card);
        card = view.Effects.First(e => e.Type == EffectRegistry.Explosion);

        expect(card.Range is null && card.EvaluateGpuPacket(0.5).Slot == GpuShaderSlot.Explosion, "locked (as it comes): it works over its whole clip");
        vm.SetEffectLocked(view, card, false);
        expect(card.Range == (0.0, Math.Round(clip.Duration, 3)), "unlocked: a stretch of its own, at first all of the clip");
        vm.SetEffectRange(view, card, 2, 3);
        expect(card.Range == (2.0, 3.0), "its block dragged and trimmed: the stretch it works in");
        expect(card.EvaluateGpuPacket(1).Slot == GpuShaderSlot.None && card.EvaluateGpuPacket(2.5).Slot == GpuShaderSlot.Explosion,
            "the GPU engine draws it in its stretch and nowhere else, as FFmpeg's graph gates it");
        vm.SetEffectRange(view, card, -5, 99);
        expect(card.Range == (0.0, Math.Round(clip.Duration, 3)), "never past its clip's ends");
        vm.SetEffectOn(view, card, false);
        expect(!card.Enabled && card.EvaluateGpuPacket(2.5).Slot == GpuShaderSlot.None, "its own On/Off: off, it draws nothing");
        vm.SetEffectOn(view, card, true);
        vm.SetEffectLocked(view, card, true);
        expect(card.Range is null && card.Enabled, "locked again: its clip's whole span");
        vm.RemoveEffect(view, card);
        expect(!view.Effects.Contains(card), "taken off its clip from its block");

        // Unlocked and dragged past its clip (5.2-Audit, the user's Q2): an Effect Clip of its own, its keys at the same moments.
        var leaving = EffectRegistry.Find(EffectRegistry.Explosion)!.NewEffect();
        view.AddEffect(leaving);
        leaving = view.Effects.First(e => e.Type == EffectRegistry.Explosion);
        vm.SetEffectLocked(view, leaving, false);
        leaving.SetKeys("rotation", [new Keyframe(1, 10), new Keyframe(2, 40)]);
        var tracks = vm.Document.Tracks.Count;
        var moved = vm.DetachEffect(view, leaving, clip.Start + 0.5, clip.End + 3);
        var effectClip = vm.Document.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.ContentKind == LayerKind.Adjustment);
        var effectView = effectClip is null ? null : vm.ViewOf(effectClip);
        var own = effectView?.Effects.FirstOrDefault(e => e.Type == EffectRegistry.Explosion);
        expect(moved && !view.Effects.Contains(leaving), "taken out of its clip");
        expect(effectClip is not null && Math.Abs(effectClip.Start - (clip.Start + 0.5)) < 0.001 && Math.Abs(effectClip.End - (clip.End + 3)) < 0.001,
            "an Effect Clip over the stretch it was let go at, past its clip's end");
        expect(own is { Range: null } && own.GetKeys("rotation") is [{ Time: 0.5 }, { Time: 1.5 }], "its effect works over all of its clip, each key at the same moment of the sequence");
        expect(vm.Document.Tracks.Count == tracks + 1, "on a track of its own");
        text.Append($"  detached: an Effect Clip from {effectClip?.Start:0.###} s to {effectClip?.End:0.###} s, keys at {string.Join(", ", own?.GetKeys("rotation")?.Select(k => k.Time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)) ?? [])}\n");
    }

    // ----- effect-rotation -----

    private static void EffectRotation(Action<bool, string> expect, StringBuilder text)
    {
        // Where each shader reads the turn: P0..P7 the first packet's lanes, Q0..Q7 the second's.
        (string Type, int Lane)[] wanted =
        [
            (EffectRegistry.Explosion, 10), (EffectRegistry.Sparks, 15), (EffectRegistry.AnimeLines, 13),
            (EffectRegistry.EnergyShield, 11), (EffectRegistry.Saber, 12), (EffectRegistry.AnamorphicLens, 8),
        ];
        foreach (var (type, lane) in wanted)
        {
            var definition = EffectRegistry.Find(type)!;
            var rotation = definition.Param("rotation");
            expect(rotation is { Min: -180, Max: 180, Default: 0 }, $"{definition.Name}: a Rotation from -180 to 180 degrees, 0 as it comes");

            var card = definition.NewEffect();
            card.Set("rotation", 30);
            var lanes = Enumerable.Range(0, definition.GpuPacketCount).SelectMany(k =>
            {
                var p = card.EvaluateGpuPacket(0.1, null, k);
                return new[] { p.P0, p.P1, p.P2, p.P3, p.P4, p.P5, p.P6, p.P7 };
            }).ToList();
            var at = lanes.FindIndex(v => Math.Abs(v - 30) < 1e-4);
            var name = at < 0 ? "nowhere" : $"{(at < 8 ? 'P' : 'Q')}{at % 8}";
            text.Append($"  {definition.Name}: {definition.GpuPacketCount} packets, Rotation in {name}\n");
            expect(at == lane, $"{definition.Name}: in the lane its shader reads");
        }

        // The shared slots' variants: the Saber's in its second packet's last lane; the Anamorphic Lens's still in its first packet's (P7),
        // where its slot tells it from the Impact Shockwave, whose one packet leaves it 0 (its Audio Reactive Pulse, 5.2B, goes in a second,
        // marked as the pulse's, only while the pulse is in use).
        var saber = EffectRegistry.Find(EffectRegistry.Saber)!.NewEffect();
        expect(saber.EvaluateGpuPacket(0.1, null, 1).P7 == 1, "the Saber's variant where it was (Q7)");
        var lens = EffectRegistry.Find(EffectRegistry.AnamorphicLens)!.NewEffect();
        expect(lens.Definition!.GpuPacketCount == 2 && lens.EvaluateGpuPacket(0.1, null, 0).P7 == 1 && lens.EvaluateGpuPacket(0.1, null, 1).P7 == 0,
            "the Anamorphic Lens's variant still in its first packet (P7), its Rotation in a second");
        var shockwave = EffectRegistry.Find(EffectRegistry.Shockwave)!.NewEffect();
        expect(shockwave.EvaluateGpuPacket(0.1).P7 == 0 && shockwave.EvaluateGpuPacket(0.1, null, 1).Slot == GpuShaderSlot.None, "the Impact Shockwave: one packet, variant 0");
        shockwave.Set(EffectRegistry.AudioPulseKey, 30);
        expect(shockwave.EvaluateGpuPacket(0.1).P7 == 0 && shockwave.EvaluateGpuPacket(0.1, null, 1) is { Slot: GpuShaderSlot.ShockwaveLens, P0: 30, P7: GpuEffectPacket.PulseMarker },
            "with an Audio Reactive Pulse (5.2B), a second packet, marked as the pulse's (its last lane -1), its first as it was");
    }
}
