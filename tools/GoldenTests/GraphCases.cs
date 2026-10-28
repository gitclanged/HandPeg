using System.Globalization;
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
/// an export command, a live graph at a moment, or a Render Preview command. Exports without cuts and live graphs with
/// sound are also run through the A/V check (<see cref="AvCheck"/>).
/// </summary>
public static class GraphCases
{
    private static readonly Media MainVideo = Media.Mp4("D:/media/main.mp4", 120);
    private static readonly Media Clip1 = Media.Mp4("D:/media/clip1.mp4", 30, indexed: true);
    private static readonly Media Clip2 = Media.Mp4("D:/media/clip2.mp4", 60);
    private static readonly Media Clip3 = Media.Mp4("D:/media/clip3.mp4", 40);
    private static readonly Media Recording = Media.Ts("D:/media/recording.ts", 90, indexed: true);
    private static readonly Media Music = Media.Mp4("D:/media/music.m4a", 240);

    /// <summary>The live request the player makes: its size, where it plays from, and its sound laid out from there.</summary>
    private static LiveGraphRequest Live(double at, LiveLayerMode layers = LiveLayerMode.Live) => new()
    {
        SurfaceWidth = 960, SurfaceHeight = 540, AudioFrom = layers == LiveLayerMode.Live ? at : null, MainAudioTrack = 0,
        VideoFrom = at, Layers = layers, DecodeThreads = 2,
    };

    public static IEnumerable<GraphResult> All()
    {
        // ----- Cuts -----
        yield return Export(CutsScene(engine: false), "export-cuts", "Encoder Mode: three kept stretches, two close together in the file (one opening) and one far (another), two audio tracks, chapters at the cuts.");
        yield return Export(CutsScene(engine: true), "export-cuts-engine", "The same cuts with the Layer Engine on and a picture on the timeline: each stretch composed on its own clock.");

        // ----- Layers -----
        yield return Export(LayersScene(), "export-layers", "Vertical frame: a video clip with sound, a picture (rotated, see-through, a quote in its name), words (quotes, a colon, two lines) and music.");
        yield return Export(RecordingScene(), "export-recording", "A clip of an MPEG-TS recording (its clock starts at 1.4 s), cut into and played at double speed, with its sound.");
        yield return Export(EffectsScene(), "export-effects", "Effect cards in every zone: a grade with keyframes, a blur over part of the clip, a flip, a chroma key, rounded and soft edges, a shadow, a blend mode; an adjustment layer; the Sequence Output's own effects.");
        yield return Export(KeyframesScene(), "export-keyframes", "Keyframes on place, size, turn and opacity with every kind of easing (closed form, pieced, a custom Bezier), about an anchor point off the middle.");
        yield return Export(TransitionsScene(), "export-transitions", "Fades in and out in each family of style: dissolve, wipe, slide, zoom, blur dissolve; the main video fading too.");
        yield return Export(CrossfadeScene(), "export-crossfade", "A crossfade between two clips of one track (laid over each other across the cut) and one across tracks (the lower keeps its picture).");
        yield return Export(HardwareScene(tenBit: false), "export-hardware", "A hardware encoder (h264_nvenc) on an odd-sized 4:4:4 screen recording: the picture ends cropped to even sides and converted to yuv420p with full chroma filtering.");
        yield return Export(HardwareScene(tenBit: true), "export-hardware-10bit", "The same with 10-bit on (hevc_nvenc): the tail ends in p010le.");

        // ----- Live, at a seek, with sound -----
        var live = LiveScene(proxies: false);
        foreach (var at in new[] { 100.0, 300, 600 })
            yield return LiveGraph(live, $"live-sound-at-{at.ToString(CultureInfo.InvariantCulture)}", $"Live graph after a seek to {at} s, sound laid out from there: the layers' amovie and the main video's two tracks.", Live(at));

        var proxied = LiveScene(proxies: true);
        foreach (var at in new[] { 100.0, 300 })
            yield return LiveGraph(proxied, $"live-proxies-at-{at.ToString(CultureInfo.InvariantCulture)}", $"The same at {at} s with proxies ready for the clips: the picture read from the proxy (its clock from 0), the sound from the original.", Live(at));

        yield return LiveGraph(live, "live-scrub-at-120", "Scrubbing at 120 s: each clip one held frame, no sound.", Live(120, LiveLayerMode.Still));

        // ----- Render Preview -----
        yield return Preview(LayersScene(), "preview-layers", "Render Preview of two stretches of the layered timeline at half size.", null);
        yield return Preview(LayersScene(), "preview-timeline-proxy", "The composed timeline proxy: the same graph, silent, with the proxy encoder.", "-c:v libx264 -preset veryfast -crf 26");
    }

    // ----- The fixtures -----

    private static Scene CutsScene(bool engine)
    {
        var scene = new Scene("cuts") { FrameEngine = engine, Main = MainVideo, ChaptersAtCuts = true }.WithMainAudio(2);
        scene.Segments.AddRange([(5, 12), (14, 20), (80, 95)]);
        if (engine)
            scene.AddImage(1, "D:/media/logo.png", 400, 200, 6, 80, (0.75, 0.05, 0.2));
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

    private static Scene LiveScene(bool proxies)
    {
        var main = Media.Mp4("D:/media/session.mp4", 1900);
        var scene = new Scene(proxies ? "live-proxies" : "live") { Main = main }.WithMainAudio(2);
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

    private static GraphResult Export(Scene scene, string golden, string description)
    {
        var state = scene.Compile();
        var command = FilterGraphBuilder.BuildExportCommand(state);
        var graphs = new List<string>();
        var text = new StringBuilder();
        text.Append("# ").AppendLine(description);
        text.Append("# reads the cuts list: ").Append(command.UsesCutsFile ? "yes" : "no").Append("; the chapters list: ").AppendLine(command.UsesChaptersFile ? "yes" : "no");
        text.AppendLine().Append(GoldenText.Command(command.CommandLine, graphs));

        // The A/V check follows the sequence's clock through the graph; with cuts the output's clock is another.
        var problems = new List<string>();
        var moments = 0;
        if (state.Segments.Count == 0)
            problems.AddRange(AvCheck.Check(scene, state, graphs, live: false, at: null, audioFrom: 0, out moments));
        return new GraphResult(golden, text.ToString(), problems, moments) { CommandLine = command.CommandLine };
    }

    private static GraphResult LiveGraph(Scene scene, string golden, string description, LiveGraphRequest request)
    {
        var state = scene.Compile();
        var graph = FilterGraphBuilder.BuildLiveGraph(state, request);
        var text = new StringBuilder();
        text.Append("# ").AppendLine(description);
        text.Append("# request: surface ").Append(GoldenText.Num(request.SurfaceWidth)).Append('x').Append(GoldenText.Num(request.SurfaceHeight))
            .Append(", video from ").Append(request.VideoFrom is { } v ? GoldenText.Num(v) : "-").Append(", sound from ").Append(request.AudioFrom is { } a ? GoldenText.Num(a) : "-")
            .Append(", layers ").Append(request.Layers).Append(", decode threads ").Append(request.DecodeThreads.ToString(CultureInfo.InvariantCulture)).AppendLine();
        text.Append("# rebuild at: ").AppendLine(double.IsPositiveInfinity(graph.RebuildAt) ? "never" : GoldenText.Num(graph.RebuildAt));
        text.AppendLine().Append(GoldenText.Graph(graph.Text, "live graph"));

        var problems = new List<string>();
        var moments = 0;
        if (request is { AudioFrom: { } from, Layers: LiveLayerMode.Live })
            problems.AddRange(AvCheck.Check(scene, state, [graph.Text], live: true, at: request.VideoFrom, audioFrom: from, out moments));
        return new GraphResult(golden, text.ToString(), problems, moments);
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
