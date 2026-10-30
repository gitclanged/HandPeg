using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>One golden file's worth: what was made for a fixture, the problems the checks found in it, and how many moments the A/V check followed.</summary>
public sealed record GraphResult(string Golden, string Text, IReadOnlyList<string> Problems, int CheckedMoments)
{
    /// <summary>The command line the text was made from, for a command.</summary>
    public string? CommandLine { get; init; }
}

/// <summary>
/// The fixtures of FilterGraphBuilder: timelines written in code (neutral paths, files that need not exist), each made into
/// an export command or a Render Preview command. Exports with sound are also run through the A/V check
/// (<see cref="AvCheck"/>), and the same timelines through its GPU side (<see cref="GpuAvInvariant"/>: the player has no
/// graph since 5.0-Verify).
/// </summary>
public static class GraphCases
{
    private static readonly Media MainVideo = Media.Mp4("D:/media/main.mp4", 120);
    private static readonly Media Clip1 = Media.Mp4("D:/media/clip1.mp4", 30, indexed: true);
    private static readonly Media Clip2 = Media.Mp4("D:/media/clip2.mp4", 60);
    private static readonly Media Clip3 = Media.Mp4("D:/media/clip3.mp4", 40);
    private static readonly Media Recording = Media.Ts("D:/media/recording.ts", 90, indexed: true);
    private static readonly Media Music = Media.Mp4("D:/media/music.m4a", 240);

    public static IEnumerable<GraphResult> All()
    {
        // ----- Cuts, as splits -----
        yield return Export(BakedCutsScene(), "export-baked-cuts", "4.2.0's cuts baked into splits: the main video in three butted clips from three stretches of its file (5-12, 14-20, 80-95 s), a picture split with them, two audio tracks, a chapter at each clip.");

        // ----- Layers -----
        yield return Export(LayersScene(), "export-layers", "Vertical frame: a video clip with sound, a picture (rotated, see-through, a quote in its name), words (quotes, a colon, two lines) and music.");
        yield return Export(RecordingScene(), "export-recording", "A clip of an MPEG-TS recording (its clock starts at 1.4 s), cut into and played at double speed, with its sound.");
        yield return Export(EffectsScene(), "export-effects", "Effect cards in every zone: a grade with keyframes, a blur over part of the clip, a flip, a chroma key, rounded and soft edges, a shadow, a blend mode; an adjustment layer; the Sequence Output's own effects.");
        yield return Export(KeyframesScene(), "export-keyframes", "Keyframes on place, size, turn and opacity with every kind of easing (closed form, pieced, a custom Bezier), about an anchor point off the middle.");
        yield return Export(TransitionsScene(), "export-transitions", "Fades in and out in each family of style: dissolve, wipe, slide, zoom, blur dissolve; the main video fading too.");
        yield return Export(CrossfadeScene(), "export-crossfade", "A crossfade between two clips of one track (laid over each other across the cut) and one across tracks (the lower keeps its picture).");
        yield return Export(HardwareScene(tenBit: false), "export-hardware", "A hardware encoder (h264_nvenc) on an odd-sized 4:4:4 screen recording: the picture ends cropped to even sides and converted to yuv420p with full chroma filtering.");
        yield return Export(HardwareScene(tenBit: true), "export-hardware-10bit", "The same with 10-bit on (hevc_nvenc): the tail ends in p010le.");
        yield return Export(LoopScene(), "export-loop-to-end", "A 2 s video with sound to the end of a 60 s timeline, fading in and out: written as its rounds (4.2.0's loop as clips), read as the one repeating source 4.2.0 made of it.");
        yield return Export(TrackProcessingScene(), "export-track-processing-scope", "A main track's own processing (5.0-Verify Q1) is that track's alone: the game track ducked, then its gain, compressor and EQ, then the clips' sound mixed in; the microphone's chain on its own stream; the music clip's on itself.");

        // ----- Render Preview -----
        yield return Preview(LayersScene(), "preview-layers", "Render Preview of two stretches of the layered timeline at half size.", null);
        yield return Preview(LayersScene(), "preview-timeline-proxy", "The composed timeline proxy: the same graph, silent, with the proxy encoder.", "-c:v libx264 -preset veryfast -crf 26");
    }

    // ----- The fixtures -----

    /// <summary>
    /// What 4.2.0's cut list (kept: 5-12, 14-20 and 80-95 s of the sequence, with a logo from 6 to 86 s) is once 4.3C bakes
    /// it into splits: the main video as three butted clips of the stretches of its file that were kept, the rest closed
    /// up, and the logo split the same way (what of it lay in each kept stretch, where that stretch now is).
    /// </summary>
    private static Scene BakedCutsScene()
    {
        var scene = new Scene("baked-cuts") { Main = MainVideo, MainClip = (0, 5, 7), ChaptersAtCuts = true }.WithMainAudio(2);
        scene.MoreMainClips.AddRange([(7, 14, 6), (13, 80, 15)]);
        foreach (var (start, duration) in new[] { (1.0, 6.0), (7.0, 6.0), (13.0, 6.0) })
            scene.AddImage(1, "D:/media/logo.png", 400, 200, start, duration, (0.75, 0.05, 0.2));
        return scene;
    }

    private static Scene LayersScene()
    {
        var scene = new Scene("layers") { FrameWidth = 1080, FrameHeight = 1920, Main = MainVideo, MainVideoIndex = 0 }.WithMainAudio(1);
        scene.AddVideo(1, Clip1, start: 10, duration: 12, offset: 3, place: (0.05, 0.05, 0.9));
        var logo = scene.AddImage(2, "D:/media/it's logo.png", 512, 256, 0, 0, (0.7, 0.02, 0.25));
        (logo.Opacity, logo.Rotation) = (80, 12);
        scene.AddText(3, "Hello, \"world\": it's 50%\nSecond line", 2, 8, (0.1, 0.75, 0.8));
        var music = scene.AddSound(4, Music, 0, 60);
        (music.AudioGainDb, music.FadeInSeconds, music.FadeOutSeconds) = (-6, 2, 3);
        return scene;
    }

    private static Scene RecordingScene()
    {
        var scene = new Scene("recording") { Main = MainVideo }.WithMainAudio(1);
        var clip = scene.AddVideo(1, Recording, start: 30, duration: 20, offset: 5, place: (0.1, 0.4, 0.5));
        clip.Speed = 2;
        return scene;
    }

    private static Scene EffectsScene()
    {
        var scene = new Scene("effects") { Main = MainVideo }.WithMainAudio(1);
        var card = scene.AddImage(1, "D:/media/card.png", 800, 600, 0, 20, (0.1, 0.1, 0.4));
        (card.ChromaKey, card.ChromaColor, card.ChromaSimilarity, card.CornerRadius, card.Feather, card.FeatherRadius, card.Shadow) =
            (true, "#00FF00", 0.2, 10, true, 8, true);
        card.Effects =
        [
            Scene.Effect(EffectRegistry.Grade, 1, new Dictionary<string, object> { ["contrast"] = 1.2, ["saturation"] = 1.3 },
                new() { ["brightness"] = [new Keyframe(0, 0, EasingType.EaseInOut), new Keyframe(2, 0.2)] }),
            Scene.Effect(EffectRegistry.Blur, 2, new Dictionary<string, object> { ["radius"] = 4.0 }, range: [1, 5]),
            Scene.Effect(EffectRegistry.Flip, 3, new Dictionary<string, object> { ["h"] = 1.0 }),
            Scene.Effect(EffectRegistry.ChromaKey, 4, new Dictionary<string, object> { ["despill"] = 0.5 }),
            Scene.Effect(EffectRegistry.Edges, 5),
            Scene.Effect(EffectRegistry.Shadow, 6),
            Scene.Effect(EffectRegistry.BlendMode, 7, new Dictionary<string, object> { ["mode"] = "screen" }),
        ];

        var adjustment = scene.AddAdjustment(2, 5, 5);
        adjustment.Effects =
        [
            Scene.Effect(EffectRegistry.Hue, 8, new Dictionary<string, object> { ["h"] = 30.0 }),
            Scene.Effect(EffectRegistry.Vignette, 9),
        ];

        scene.SequenceEffects.Add(EffectInstance.FromState(Scene.Effect(EffectRegistry.Temperature, 10, new Dictionary<string, object> { ["temperature"] = 5000.0 })));
        return scene;
    }

    private static Scene KeyframesScene()
    {
        var scene = new Scene("keyframes") { Main = MainVideo }.WithMainAudio(1);
        var moving = scene.AddImage(1, "D:/media/badge.png", 300, 300, 1, 10, (0.1, 0.1, 0.2));
        (moving.AnchorX, moving.AnchorY) = (0.2, 0.8);
        moving.KeysX = [new Keyframe(0, 0.1, EasingType.Linear), new Keyframe(4, 0.6, EasingType.Linear)];
        moving.KeysY = [new Keyframe(0, 0.1, EasingType.EaseInOut), new Keyframe(3, 0.5, EasingType.Smoothstep), new Keyframe(6, 0.2)];
        moving.KeysScale = [new Keyframe(0, 0.2, EasingType.Bounce), new Keyframe(3, 0.4, EasingType.Back), new Keyframe(5, 0.3)];
        moving.KeysRotation = [new Keyframe(0, 0, EasingType.Elastic), new Keyframe(2, 90, EasingType.Hold), new Keyframe(4, 45)];
        moving.KeysOpacity = [new Keyframe(0, 0, EasingType.EaseOut), new Keyframe(1, 100, EasingType.EaseIn), new Keyframe(9, 40)];

        var words = scene.AddText(2, "Custom curve", 0, 8, (0.2, 0.8, 0.5));
        words.KeysX = [new Keyframe(0, 0.2).WithEasing(EasingType.CubicBezier, (0.7, 0, 0.3, 1)), new Keyframe(5, 0.4)];
        return scene;
    }

    private static Scene TransitionsScene()
    {
        var scene = new Scene("transitions") { Main = MainVideo, MainClip = (0, 0, 60) }.WithMainAudio(1);
        var clip = scene.AddVideo(1, Clip2 with { HasAudio = false }, start: 5, duration: 10, offset: 1, place: (0.05, 0.05, 0.4));
        (clip.FadeInSeconds, clip.FadeInStyle, clip.FadeOutSeconds, clip.FadeOutStyle) = (1, TransitionStyle.Dissolve, 1.5, TransitionStyle.WipeLeft);
        var picture = scene.AddImage(2, "D:/media/still.png", 640, 360, 8, 10, (0.5, 0.5, 0.4));
        (picture.FadeInSeconds, picture.FadeInStyle, picture.FadeOutSeconds, picture.FadeOutStyle) = (0.8, TransitionStyle.SlideRight, 1, TransitionStyle.Zoom);
        var words = scene.AddText(3, "Title", 2, 6, (0.3, 0.1, 0.4));
        (words.FadeInSeconds, words.FadeInStyle, words.FadeOutSeconds, words.FadeOutStyle) = (1.2, TransitionStyle.BlurDissolve, 0.6, TransitionStyle.WipeDown);
        return scene;
    }

    private static Scene CrossfadeScene()
    {
        var scene = new Scene("crossfade") { Main = MainVideo }.WithMainAudio(1);
        scene.AddVideo(1, Clip2, start: 0, duration: 10, offset: 2, place: (0, 0, 0.5));
        var after = scene.AddVideo(1, Clip3, start: 10, duration: 10, offset: 5, place: (0, 0, 0.5));
        after.TransitionIn = 1;

        // Across tracks: the upper comes on over the lower as the lower goes, over the same second.
        var lower = scene.AddImage(2, "D:/media/lower.png", 640, 360, 8, 8, (0.5, 0.5, 0.4));
        lower.FadeOutSeconds = 1;
        var upper = scene.AddImage(3, "D:/media/upper.png", 640, 360, 15, 8, (0.5, 0.5, 0.4));
        upper.FadeInSeconds = 1;
        return scene;
    }

    private static Scene HardwareScene(bool tenBit)
    {
        var scene = new Scene(tenBit ? "hardware-10bit" : "hardware")
        {
            FrameWidth = 1919, FrameHeight = 1079, Main = Media.Mp4("D:/media/screen.mp4", 60),
            VideoCodecArguments = tenBit ? "-c:v hevc_nvenc -preset p5 -rc vbr -cq 23 -b:v 0 -pix_fmt p010le" : "-c:v h264_nvenc -preset p5 -rc vbr -cq 23 -b:v 0 -pix_fmt yuv420p",
            EncoderPixelFormat = tenBit ? "p010le" : "yuv420p",
        }.WithMainAudio(1);
        scene.AddImage(1, "D:/media/logo.png", 400, 200, 2, 10, (0.75, 0.05, 0.2));
        return scene;
    }

    private static Scene LoopScene()
    {
        var scene = new Scene("loop") { Main = Media.Mp4("D:/media/main.mp4", 60) }.WithMainAudio(1);
        var spinner = scene.AddVideo(1, Media.Mp4("D:/media/spinner.mp4", 2), start: 5, duration: 0, offset: 0.5, place: (0.8, 0.05, 0.15));
        (spinner.FadeInSeconds, spinner.FadeOutSeconds) = (0.5, 1);
        return scene;
    }

    /// <summary>
    /// The main file's game track (auto-ducked; -3 dB, a compressor, an EQ band) and microphone (a voice; a high-pass and a
    /// gate, +2 dB), a video clip with sound and a music clip with its own bass and gain, ducked too.
    /// </summary>
    private static Scene TrackProcessingScene()
    {
        var scene = new Scene("track-processing") { Main = MainVideo };
        var game = new TrackAudioFilters { Compressor = true, CompressorThreshold = -18, CompressorRatio = 3, Equalizer = true };
        game.EqualizerGains[2] = 4;
        var mic = new TrackAudioFilters { HighPass = true, HighPassHz = 90, NoiseGate = true, GateThreshold = -40 };
        scene.AudioTracks.Add(new ExportAudioTrack(0, "Game", "aac", "160k", false, false, AutoDuck: true, IsVoice: false, false, HasProcessing: true, [], game.BuildChain(-3)));
        scene.AudioTracks.Add(new ExportAudioTrack(1, "Microphone", "aac", "160k", false, false, AutoDuck: false, IsVoice: true, false, HasProcessing: true, [], mic.BuildChain(2)));
        scene.AddVideo(1, Clip1, start: 10, duration: 12, offset: 3, place: (0.05, 0.05, 0.4));
        var music = scene.AddSound(2, Music, 0, 60);
        (music.AudioGainDb, music.AutoDuck) = (-8, true);
        music.AudioFilters = new TrackAudioFilters { BassTreble = true, BassGain = 3 };
        return scene;
    }

    /// <summary>A long session: two main tracks, clips of three files on two tracks (one faster), music; proxies made for two of the files.</summary>
    private static Scene SessionScene(bool proxies)
    {
        var main = Media.Mp4("D:/media/session.mp4", 1900);
        var scene = new Scene(proxies ? "session-proxies" : "session") { Main = main }.WithMainAudio(2);
        scene.AddVideo(1, Clip1 with { Seconds = 130 }, start: 58, duration: 121, offset: 0, place: (0.34, 0, 0.32));
        scene.AddVideo(1, Recording with { Seconds = 1700 }, start: 182, duration: 1400, offset: 0, place: (0.34, 0, 0.32));
        var faster = scene.AddVideo(2, Clip3 with { Seconds = 60 }, start: 95, duration: 30, offset: 10, place: (0.02, 0.6, 0.3));
        faster.Speed = 1.5;
        scene.AddSound(3, Music, 90, 100, offset: 4);
        if (proxies)
        {
            scene.Proxies[Clip1.Path] = "D:/cache/proxies/clip1_540.mp4";
            scene.Proxies[Recording.Path] = "D:/cache/proxies/recording_540.mp4";
        }

        return scene;
    }

    // ----- Building and checking -----

    private static GraphResult Export(Scene scene, string golden, string description) =>
        Export(scene.Compile(), scene.Clips.Select(c => c.Media).OfType<Media>(), golden, description);

    /// <summary>An export of a state made by hand (a timeline written as a document), its files given for the A/V check.</summary>
    internal static GraphResult Export(SequenceExportState state, IEnumerable<Media> files, string golden, string description)
    {
        var command = FilterGraphBuilder.BuildExportCommand(state);
        var graphs = new List<string>();
        var text = new StringBuilder();
        text.Append("# ").AppendLine(description);
        text.Append("# reads the cuts list: ").Append(command.UsesCutsFile ? "yes" : "no").Append("; the chapters list: ").AppendLine(command.UsesChaptersFile ? "yes" : "no");
        text.AppendLine().Append(GoldenText.Command(command.CommandLine, graphs));

        // The A/V check follows the sequence's clock through the graph, which is the output's.
        var problems = AvCheck.Check(files, state, graphs, out var moments);
        return new GraphResult(golden, text.ToString(), problems, moments) { CommandLine = command.CommandLine };
    }

    /// <summary>
    /// The GPU side of the A/V invariant (gpu-av-invariant, 5.0-Verify) on the timelines the live graphs were checked on before:
    /// the exports' from each clip's start, the long session from where its seeks went (with its proxies, read by the clips
    /// that ask for them in the preview), the loop from 30 s, and the document-made timelines (<see cref="TimelineGraphCases"/>).
    /// </summary>
    public static GraphResult GpuAvInvariant()
    {
        static GpuAvFixture Of(Scene scene, params double[] playheads) => new(scene.Name, scene.Compile(), scene.Clips.Select(c => c.Media).OfType<Media>().ToList(), playheads);

        var proxied = SessionScene(proxies: true);
        var preview = proxied.Compile() with { IsPreview = true };
        foreach (var clip in preview.Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.SourcePath is { } path && preview.PreviewPaths.ContainsKey(path)))
            clip.ProxyChoice = ProxyChoice.Make;

        GpuAvFixture[] fixtures =
        [
            Of(BakedCutsScene(), 0), Of(LayersScene(), 0), Of(RecordingScene(), 0), Of(EffectsScene(), 0), Of(KeyframesScene(), 0),
            Of(TransitionsScene(), 0), Of(CrossfadeScene(), 0), Of(HardwareScene(tenBit: false), 0), Of(LoopScene(), 0, 30),
            Of(SessionScene(proxies: false), 100, 300, 600),
            new(proxied.Name, preview, proxied.Clips.Select(c => c.Media).OfType<Media>().ToList(), [100, 300]),
            .. TimelineGraphCases.GpuAvFixtures(),
        ];
        return AvCheck.GpuInvariant(fixtures);
    }

    private static GraphResult Preview(Scene scene, string golden, string description, string? videoEncode)
    {
        var state = scene.Compile();
        var command = FilterGraphBuilder.BuildPreviewCommand(state, "D:/session/previews/preview.mp4", [(10, 14), (60, 62)], 50, null, videoEncode);
        var text = new StringBuilder();
        text.Append("# ").AppendLine(description);
        text.AppendLine().Append(GoldenText.Command(command));
        return new GraphResult(golden, text.ToString(), [], 0) { CommandLine = command };
    }
}
