using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// FilterGraphBuilder on a timeline written as a <see cref="TimelineDocument"/> by hand (4.3B), not through the fixtures of
/// 4.2.0: what only the document can say. Video 1 is not privileged: it starts with a gap, has a gap between its clips, a
/// picture and another file's clip of its own, a clip of the main video at double speed (which leaves the primary chain
/// and is read as a layer in the main video's box) and a crossfade from a picture into a clip of the chain (a dip: the two
/// cannot be laid over each other). Under it all, a picture in picture of the main video's own file stays a layer (it is
/// not the main video). A clip of the chain has a J and an L cut, a layer has both too, another's sound is unlinked and
/// slipped, and an Audio track has a crossfade and a gain of its own. The export and a live graph, both through the A/V check.
/// </summary>
public static class TimelineGraphCases
{
    private static readonly Media Main = Media.Mp4("D:/media/main.mp4", 120);
    private static readonly Media Clip1 = Media.Mp4("D:/media/clip1.mp4", 30, indexed: true);
    private static readonly Media Clip2 = Media.Mp4("D:/media/clip2.mp4", 60);
    private static readonly Media Music = Media.Mp4("D:/media/music.m4a", 240);

    public static IEnumerable<GraphResult> All()
    {
        var (state, problems) = Native();
        Media[] files = [Main, Clip1, Clip2, Music];

        var export = GraphCases.Export(state, files, "export-timeline-native",
            "A timeline written as a document: Video 1 with a gap first and between clips, a picture, a clip at double speed and another file's clip of its own; J and L cuts, a slip, a crossfade into the chain and one on an Audio track.");
        yield return export with { Problems = [.. problems, .. export.Problems] };

        yield return GraphCases.LiveGraph(state, files, "live-timeline-native-at-20",
            "The same timeline live at 20 s, sound laid out from there.", GraphCases.Live(20));
    }

    private static (SequenceExportState State, List<string> Problems) Native()
    {
        var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
        var (under, video1, video2) = (Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video));
        var audio1 = Track(document, TimelineTrackKind.Audio);
        audio1.GainDb = -6;

        // The lowest track: a picture in picture of the main video's own file (a video clip of it, not the main video).
        var inset = Video(Main, 3, 6, offset: 50, look: Look(LayerKind.VideoFile, "inset", 0.7, 0.7, 0.25, Main));
        inset.IsMuted = true;
        under.Clips.Add(inset);

        // Video 2: nothing for 2 s, the main video, a second's gap, a picture, then the main video again (a J and an L cut,
        // a crossfade from the picture), the main video at double speed, and another file's clip with its sound slipped.
        var mainLook = Look(LayerKind.MainVideo, "Main", 0, 0, 1, Main);
        video1.Clips.Add(Video(Main, 2, 12, offset: 30, look: mainLook, LayerKind.MainVideo));
        video1.Clips.Add(Picture(13, 16));
        var mainAgain = Video(Main, 16, 26, offset: 60, look: mainLook, LayerKind.MainVideo);
        (mainAgain.AudioLeadInSeconds, mainAgain.AudioTailOutSeconds, mainAgain.TransitionIn) = (1, 0.5, 1);
        video1.Clips.Add(mainAgain);
        var faster = Video(Main, 26, 30, offset: 80, look: mainLook, LayerKind.MainVideo);
        faster.Speed = 2;
        video1.Clips.Add(faster);
        var slipped = Video(Clip2, 30, 38, offset: 3, look: Look(LayerKind.VideoFile, "clip2", 0.05, 0.05, 0.5, Clip2));
        (slipped.AudioLinked, slipped.AudioSlipSeconds) = (false, 0.25);
        video1.Clips.Add(slipped);

        // Video 3: a clip whose sound starts half a second before its picture and runs on a second after it.
        var cutAway = Video(Clip1, 5, 15, offset: 2, look: Look(LayerKind.VideoFile, "clip1", 0.6, 0.05, 0.35, Clip1));
        (cutAway.AudioLeadInSeconds, cutAway.AudioTailOutSeconds, cutAway.FadeInSeconds) = (0.5, 1, 0.5);
        video2.Clips.Add(cutAway);

        // Audio 1: music, crossfading into a later part of itself.
        audio1.Clips.Add(Sound(Music, 0, 20, offset: 0));
        var later = Sound(Music, 20, 40, offset: 100);
        later.TransitionIn = 2;
        audio1.Clips.Add(later);

        var problems = document.FindProblems(requireFrames: true).Select(p => "the document: " + p).ToList();
        var caption = Layer.FromState(new LayerState
        {
            Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
            SourceWidth = 0.25, SourceHeight = 0.25,
        }, Main.Width, Main.Height, document.Width, document.Height);

        Media[] videos = [Clip1, Clip2, Main];
        var state = new SequenceExportState
        {
            Timeline = document,
            SourceFrameRate = 30,
            SourceWidth = Main.Width,
            SourceHeight = Main.Height,
            RequestedSize = (document.Width, document.Height),
            OutputSize = (document.Width, document.Height),
            LocalMediaPath = Main.Path,
            HasSource = true,
            MainMediaSeconds = Main.Seconds,
            CenterRect = (0, 0, document.Width, document.Height),
            FrameEngine = true,
            CaptionLayer = caption,
            AudioTracks = [new ExportAudioTrack(0, "Track 1", "aac", "160k", false, false, false, false, false, false, [], [])],
            VideoCodecArguments = "-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p",
            OutputPath = "D:/out/timeline.mp4",
            MediaStarts = videos.ToDictionary(m => m.Path, m => m.VideoStart, StringComparer.OrdinalIgnoreCase),
            SeekIndexes = videos.Where(m => m.Indexed).ToDictionary(m => m.Path, m => new SeekIndex(m.ContainerStart, m.IFrames), StringComparer.OrdinalIgnoreCase),
        };

        return (state, problems);
    }

    private static TimelineTrackModel Track(TimelineDocument document, TimelineTrackKind kind)
    {
        var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
        document.Tracks.Add(track);
        return track;
    }

    private static LayerState Look(LayerKind kind, string name, double x, double y, double width, Media? media = null) => new()
    {
        Kind = kind, Name = name, PositionX = x, PositionY = y, SizeWidth = width, SourceWidth = 0.25, SourceHeight = 0.25,
        ImagePath = media?.Path ?? "", ImageWidth = media?.Width ?? 0, ImageHeight = media?.Height ?? 0, MediaDuration = media?.Seconds ?? 0, HasAudio = media?.HasAudio ?? false,
    };

    private static TimelineClip Video(Media media, double start, double end, double offset, LayerState look, LayerKind kind = LayerKind.VideoFile) => new()
    {
        ContentKind = kind, SourcePath = media.Path, AudioStreamIndex = media.HasAudio ? 0 : -1,
        Start = start, End = end, MediaOffset = offset, SourceDuration = media.Seconds, State = look,
    };

    private static TimelineClip Picture(double start, double end) => new()
    {
        ContentKind = LayerKind.Image, SourcePath = "D:/media/still.png", Start = start, End = end,
        State = new LayerState { Kind = LayerKind.Image, Name = "still", ImagePath = "D:/media/still.png", ImageWidth = 1920, ImageHeight = 1080, SizeWidth = 1 },
    };

    private static TimelineClip Sound(Media media, double start, double end, double offset) => new()
    {
        ContentKind = LayerKind.Audio, SourcePath = media.Path, AudioStreamIndex = 0, Start = start, End = end, MediaOffset = offset, SourceDuration = media.Seconds,
        State = new LayerState { Kind = LayerKind.Audio, Name = "music", ImagePath = media.Path, MediaDuration = media.Seconds, HasAudio = true },
    };
}
