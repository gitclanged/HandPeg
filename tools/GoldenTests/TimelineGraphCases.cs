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
/// slipped, and an Audio track has a crossfade and a gain of its own. The export, through the A/V check.
///
/// 4.4-BugHunt adds two timelines. One where every kind of clip is cropped (the user's Q2: every clip's crop reframes its
/// picture, on every track), exported. And a game style with the main video split and its later part dragged 2 s on (a gap).
/// All three are on the GPU side of the A/V check too (<see cref="GpuAvFixtures"/>), as their live graphs were before 5.0-Verify.
/// </summary>
public static class TimelineGraphCases
{
    private static readonly Media Main = Media.Mp4("D:/media/main.mp4", 120);
    private static readonly Media Clip1 = Media.Mp4("D:/media/clip1.mp4", 30, indexed: true);
    private static readonly Media Clip2 = Media.Mp4("D:/media/clip2.mp4", 60);
    private static readonly Media Music = Media.Mp4("D:/media/music.m4a", 240);

    // A game recording as the user's are: 2560 x 1440, 24 s.
    private static readonly Media Match = new("D:/media/match.mkv", 24, Width: 2560, Height: 1440);

    public static IEnumerable<GraphResult> All()
    {
        var (state, problems) = Native();
        Media[] files = [Main, Clip1, Clip2, Music];

        var export = GraphCases.Export(state, files, "export-timeline-native",
            "A timeline written as a document: Video 1 with a gap first and between clips, a picture, a clip at double speed and another file's clip of its own; J and L cuts, a slip, a crossfade into the chain and one on an Audio track.");
        yield return export with { Problems = [.. problems, .. export.Problems] };

        // ----- Every clip's crop reframes its picture (4.4-BugHunt, the user's Q2) -----
        var (cropped, cropProblems) = UnifiedCrop();
        Media[] cropFiles = [Main, Clip1];
        var cropExport = GraphCases.Export(cropped, cropFiles, "export-unified-crop",
            "Every clip cropped: a main video clip cropped otherwise than the first (a rider, cut from the stream by its own crop, in its own box), a piece cut from the main video with a mask of the whole frame, a picture, another file's video with a mask of its own picture, and words.");
        yield return cropExport with { Problems = [.. cropProblems, .. cropExport.Problems] };
    }

    /// <summary>
    /// The three timelines for the GPU side of the A/V check (AvCheck.GpuInvariant): from each clip's start, and from the moments
    /// their live graphs were built at (20 s; 11 s; 11 s, in the game style's gap, and on into its later part).
    /// </summary>
    internal static IEnumerable<GpuAvFixture> GpuAvFixtures()
    {
        var (native, nativeProblems) = Native();
        yield return new GpuAvFixture("timeline-native", native, [Main, Clip1, Clip2, Music], [0, 20]) { Problems = nativeProblems };
        var (cropped, cropProblems) = UnifiedCrop();
        yield return new GpuAvFixture("unified-crop", cropped, [Main, Clip1], [0, 11]) { Problems = cropProblems };
        var (game, gameProblems) = GameStyle();
        yield return new GpuAvFixture("game-style", game, [Match], [0, 11]) { Problems = gameProblems };
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

    // The masks the fixtures imagine (they need not exist: the state says they do).
    private const string PieceMask = "D:/media/piece-mask.png", InsetMask = "D:/media/inset-mask.png";

    /// <summary>
    /// Every clip's crop reframes its picture (4.4-BugHunt, the user's Q2): its picture cut before it is scaled, its box at the
    /// shape the crop leaves (a locked box keeps its width), never an edge mask. Video 1: the main video whole for 10 s, then a
    /// clip of it placed and cropped otherwise (a rider: cut from the chain's stream by its own crop, in a box of its own).
    /// Video 2: a piece cut from the main video with a mask of the whole frame grown by 4 pixels and a crop of its own (taken
    /// off its source rectangle, and the mask cut to the same part). Video 3: a picture cropped to its middle half. Video 4:
    /// another file's video, cropped, with a mask of its own picture (cut by the same crop, so it stays where it was on the
    /// picture) and its sound. Video 5: words cropped on the right and the top (drawn whole at the box's scale, cut to the pixel).
    /// </summary>
    private static (SequenceExportState State, List<string> Problems) UnifiedCrop()
    {
        var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
        var (video1, video2, video3) = (Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video));
        var (video4, video5) = (Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video));

        video1.Clips.Add(Video(Main, 0, 10, offset: 0, look: Look(LayerKind.MainVideo, "Main", 0, 0, 1, Main), LayerKind.MainVideo));
        var reframed = Look(LayerKind.MainVideo, "Main", 0.2, 0.1, 0.6, Main);
        (reframed.CropLeft, reframed.CropTop, reframed.CropRight, reframed.CropBottom) = (0.1, 0.05, 0.2, 0.15);
        video1.Clips.Add(Video(Main, 10, 20, offset: 30, look: reframed, LayerKind.MainVideo));

        var piece = Look(LayerKind.Video, "Killfeed", 0.7, 0.04, 0.28);
        (piece.SourceX, piece.SourceY, piece.SourceWidth, piece.SourceHeight) = (0.65, 0.01, 0.34, 0.26);
        (piece.CropLeft, piece.CropBottom) = (0.1, 0.25);
        (piece.CustomMask, piece.MaskFrame, piece.MaskPath, piece.MaskGrow) = (true, true, PieceMask, 4);
        video2.Clips.Add(new TimelineClip { ContentKind = LayerKind.Video, SourcePath = Main.Path, Start = 0, End = 20, State = piece });

        var still = new LayerState
        {
            Kind = LayerKind.Image, Name = "still", ImagePath = "D:/media/still.png", ImageWidth = 1920, ImageHeight = 1080,
            PositionX = 0.03, PositionY = 0.05, SizeWidth = 0.3, CropLeft = 0.25, CropRight = 0.25, CropBottom = 0.1,
        };
        video3.Clips.Add(new TimelineClip { ContentKind = LayerKind.Image, SourcePath = still.ImagePath, Start = 2, End = 12, State = still });

        var inset = Look(LayerKind.VideoFile, "clip1", 0.6, 0.55, 0.35, Clip1);
        (inset.CropLeft, inset.CropTop, inset.CropBottom) = (0.2, 0.1, 0.3);
        (inset.CustomMask, inset.MaskPath) = (true, InsetMask);
        video4.Clips.Add(Video(Clip1, 4, 16, offset: 2, look: inset));

        var words = new LayerState
        {
            Kind = LayerKind.Text, Name = "Text", TextContent = "Cropped words", FontFamily = "Arial", FontSize = 72,
            PositionX = 0.1, PositionY = 0.8, SizeWidth = 0.4, CropRight = 0.3, CropTop = 0.2,
        };
        var layout = TextLayout.Measure(words.TextContent, words.FontFamily, words.FontSize, words.OutlineThickness);
        (words.ImageWidth, words.ImageHeight) = (layout.Width, layout.Height);
        video5.Clips.Add(new TimelineClip { ContentKind = LayerKind.Text, Start = 6, End = 14, State = words });

        var problems = document.FindProblems(requireFrames: true).Select(p => "the document: " + p).ToList();
        Media[] videos = [Clip1, Main];
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
            CaptionLayer = Captions(document, Main),
            AudioTracks = [new ExportAudioTrack(0, "Track 1", "aac", "160k", false, false, false, false, false, false, [], [])],
            VideoCodecArguments = "-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p",
            OutputPath = "D:/out/unified-crop.mp4",
            ExistingFiles = new HashSet<string>([PieceMask, InsetMask], StringComparer.OrdinalIgnoreCase),
            MediaStarts = videos.ToDictionary(m => m.Path, m => m.VideoStart, StringComparer.OrdinalIgnoreCase),
            SeekIndexes = videos.Where(m => m.Indexed).ToDictionary(m => m.Path, m => new SeekIndex(m.ContainerStart, m.IFrames), StringComparer.OrdinalIgnoreCase),
        };

        return (state, problems);
    }

    /// <summary>
    /// A vertical game style as the user's (reports\forensics-crop.md: "Overwatch Vertical HUDv2" on a 2560 x 1440 recording):
    /// the main video cropped to its middle (left and right 20 %) and laid across the frame, the blurred background, and two
    /// HUD pieces cut from it (Killfeed and Health &amp; Name, at their style's fractions), each with a mask of the whole frame
    /// read by its transparency and grown (19 and 2 pixels). The main video was split at 10 s and its later part dragged 2 s
    /// on, its pieces split and moved with it: a gap from 10 to 12 s, where nothing of it is drawn (the 4.4 player played its
    /// stand-in clock there, 16 x 8 pixels at a quarter scale, and a HUD piece cut from it crashed FFmpeg).
    /// </summary>
    private static (SequenceExportState State, List<string> Problems) GameStyle()
    {
        const string killfeedMask = "D:/styles/killfeed-mask.png", healthMask = "D:/styles/health-mask.png";
        var document = new TimelineDocument { Width = 1080, Height = 1920, UseVerticalResolution = true, FrameRate = FrameRate.Fps30 };
        var (video1, video2, video3) = (Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video), Track(document, TimelineTrackKind.Video));

        // The main video's box: the frame's width, at the shape its crop leaves (1536 x 1440), in the middle of the frame.
        var (centerWidth, centerHeight) = (1080, (int)Math.Round(1080.0 * 1440 / 1536 / 2) * 2);
        var centerY = (document.Height - centerHeight) / 2;
        var mainLook = Look(LayerKind.MainVideo, "Main", 0, (double)centerY / document.Height, 1, Match);
        (mainLook.CropLeft, mainLook.CropRight) = (0.2, 0.2);
        video1.Clips.Add(Video(Match, 0, 10, offset: 0, look: mainLook, LayerKind.MainVideo));
        video1.Clips.Add(Video(Match, 12, 26, offset: 10, look: mainLook, LayerKind.MainVideo));

        LayerState Piece(string name, (double X, double Y, double Width, double Height) source, (double X, double Y, double Width) place, int grow, string mask)
        {
            var look = Look(LayerKind.Video, name, place.X, place.Y, place.Width);
            (look.SourceX, look.SourceY, look.SourceWidth, look.SourceHeight) = source;
            (look.CustomMask, look.MaskFrame, look.MaskPath, look.MaskGrow) = (true, true, mask, grow);
            return look;
        }

        foreach (var (start, end) in new[] { (0.0, 10.0), (12.0, 26.0) })
        {
            video2.Clips.Add(new TimelineClip
            {
                ContentKind = LayerKind.Video, SourcePath = Match.Path, Start = start, End = end,
                State = Piece("Overwatch: Killfeed", (0.65637, 0.01094, 0.33784, 0.26083), (0.36, 0.03, 0.62), 19, killfeedMask),
            });
            video3.Clips.Add(new TimelineClip
            {
                ContentKind = LayerKind.Video, SourcePath = Match.Path, Start = start, End = end,
                State = Piece("Overwatch: Health & Name", (0.00137, 0.77656, 0.25343, 0.20879), (0.02, 0.8, 0.5), 2, healthMask),
            });
        }

        var problems = document.FindProblems(requireFrames: true).Select(p => "the document: " + p).ToList();
        var masks = new HashSet<string>([killfeedMask, healthMask], StringComparer.OrdinalIgnoreCase);
        var state = new SequenceExportState
        {
            Timeline = document,
            SourceFrameRate = 30,
            SourceWidth = Match.Width,
            SourceHeight = Match.Height,
            RequestedSize = (document.Width, document.Height),
            OutputSize = (document.Width, document.Height),
            LocalMediaPath = Match.Path,
            HasSource = true,
            MainMediaSeconds = Match.Seconds,
            HasCrop = true,
            CropFractions = (0.2, 0, 0.2, 0),
            CenterRect = (0, centerY, centerWidth, centerHeight),
            FrameEngine = true,
            CaptionLayer = Captions(document, Match),
            AudioTracks = [new ExportAudioTrack(0, "Track 1", "aac", "160k", false, false, false, false, false, false, [], [])],
            VideoCodecArguments = "-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p",
            OutputPath = "D:/out/game-style.mp4",
            ExistingFiles = masks,
            AlphaMasks = masks,
            MediaStarts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [Match.Path] = Match.VideoStart },
        };

        return (state, problems);
    }

    /// <summary>The caption box every fixture's state has (none of them burns captions in).</summary>
    private static Layer Captions(TimelineDocument document, Media source) => Layer.FromState(new LayerState
    {
        Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
        SourceWidth = 0.25, SourceHeight = 0.25,
    }, source.Width, source.Height, document.Width, document.Height);

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
