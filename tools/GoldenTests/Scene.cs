using System.Reflection;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// A media file as the fixtures imagine it: nothing is read from disk (the builder never reads files), but the timing
/// checks need to know where its picture and sound start on its own clock, its frame rate, and where its I-frames are.
/// </summary>
/// <param name="ContainerStart">Where the container's clock starts: what movie='s sp= and amovie's seek_point count from.</param>
/// <param name="VideoStart">The time stamp of its first picture (MediaInfo.VideoStartSeconds): 0 for most MP4s, about 1.4 for MPEG-TS.</param>
/// <param name="AudioStart">The time stamp of its first sound.</param>
/// <param name="Gop">Seconds between its I-frames, which start at its first picture.</param>
/// <param name="Probed">Whether HandPeg has inspected it (its start goes into MediaStarts).</param>
/// <param name="Indexed">Whether its I-frames have been listed (it goes into SeekIndexes).</param>
public sealed record Media(
    string Path, double Seconds, double ContainerStart = 0, double VideoStart = 0, double AudioStart = 0, double Fps = 30, double Gop = 2,
    bool HasAudio = true, int Width = 1920, int Height = 1080, bool Probed = true, bool Indexed = false)
{
    /// <summary>A file of the usual kind: an MP4 whose picture and sound start at 0.</summary>
    public static Media Mp4(string path, double seconds, bool indexed = false) => new(path, seconds, Indexed: indexed);

    /// <summary>A recording in MPEG-TS: its clock starts at 1.4 s, its picture a frame later than its sound.</summary>
    public static Media Ts(string path, double seconds, bool indexed = false) =>
        new(path, seconds, ContainerStart: 1.4, VideoStart: 1.4 + 1 / 30.0, AudioStart: 1.4, Indexed: indexed);

    public double[] IFrames
    {
        get
        {
            var count = (int)Math.Floor(Seconds / Gop) + 1;
            return Enumerable.Range(0, count).Select(i => Math.Round(VideoStart + i * Gop, 6)).ToArray();
        }
    }
}

/// <summary>A clip the fixtures put on the timeline, with the file it plays (null for a picture or words).</summary>
public sealed record SceneClip(LayerState State, Media? Media);

/// <summary>
/// A timeline written as a fixture, made into the <see cref="SequenceExportState"/> the view model would hand the builder:
/// the same copies of the layers, the same preparation of them (folded keyframes, crossfades laid over each other), the
/// same derived numbers (the sequence's length, the main video's clips). Only the values a fixture sets differ.
/// </summary>
public sealed class Scene(string name)
{
    public string Name { get; } = name;
    public int FrameWidth { get; init; } = 1920;
    public int FrameHeight { get; init; } = 1080;
    public double FrameRate { get; init; } = 30;
    public bool FrameEngine { get; init; } = true;
    public bool AudioLinked { get; init; } = true;
    public bool BackgroundHidden { get; init; }
    public bool FadeIn { get; init; }
    public bool FadeOut { get; init; }
    public int MainVideoIndex { get; init; }
    public Media Main { get; init; } = Media.Mp4("D:/media/main.mp4", 120);

    /// <summary>The main video's own clip on the timeline: where it starts, how far into its file, how long (0 for the rest of it).</summary>
    public (double Start, double Offset, double Duration) MainClip { get; init; } = (0, 0, 0);

    public List<MainPiece> MainPieces { get; } = [];
    public List<SceneClip> Clips { get; } = [];
    public List<ExportAudioTrack> AudioTracks { get; } = [];
    public List<(double Start, double End)> Segments { get; } = [];
    public Dictionary<string, string> Proxies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EffectInstance> SequenceEffects { get; } = [];
    public bool ChaptersAtCuts { get; init; }
    public bool MergeAudioTracks { get; init; }
    public string Container { get; init; } = "mp4";
    public string VideoCodecArguments { get; init; } = "-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p";

    // ----- Building the timeline -----

    /// <summary>The main video's audio tracks, re-encoded to AAC, as many as asked.</summary>
    public Scene WithMainAudio(int count)
    {
        for (var i = 0; i < count; i++)
            AudioTracks.Add(new ExportAudioTrack(i, $"Track {i + 1}", "aac", "160k", false, false, false, false, false, false, [], []));
        return this;
    }

    public LayerState AddVideo(int track, Media media, double start, double duration, double offset = 0, (double X, double Y, double Width) place = default, string? name = null)
    {
        var state = Clip(LayerKind.VideoFile, track, name ?? System.IO.Path.GetFileNameWithoutExtension(media.Path), start, duration, place);
        (state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaDuration, state.MediaOffset) = (media.Path, media.Width, media.Height, media.Seconds, offset);
        (state.HasAudio, state.AudioCodec, state.AudioChannels) = (media.HasAudio, media.HasAudio ? "aac" : "", media.HasAudio ? "stereo" : "");
        Clips.Add(new SceneClip(state, media));
        return state;
    }

    public LayerState AddImage(int track, string path, int width, int height, double start, double duration, (double X, double Y, double Width) place = default)
    {
        var state = Clip(LayerKind.Image, track, System.IO.Path.GetFileNameWithoutExtension(path), start, duration, place);
        (state.ImagePath, state.ImageWidth, state.ImageHeight) = (path, width, height);
        Clips.Add(new SceneClip(state, null));
        return state;
    }

    public LayerState AddText(int track, string words, double start, double duration, (double X, double Y, double Width) place = default, string font = "Arial", double size = 72)
    {
        var state = Clip(LayerKind.Text, track, "Text", start, duration, place);
        (state.TextContent, state.FontFamily, state.FontSize) = (words, font, size);

        // The box is the words' own (MainViewModel.LayoutText measures them the same way).
        var layout = TextLayout.Measure(words, font, size, state.OutlineThickness);
        (state.ImageWidth, state.ImageHeight) = (layout.Width, layout.Height);
        Clips.Add(new SceneClip(state, null));
        return state;
    }

    public LayerState AddSound(int track, Media media, double start, double duration, double offset = 0)
    {
        var state = Clip(LayerKind.Audio, track, System.IO.Path.GetFileNameWithoutExtension(media.Path), start, duration, default);
        (state.ImagePath, state.MediaDuration, state.MediaOffset, state.HasAudio, state.AudioCodec, state.AudioChannels) = (media.Path, media.Seconds, offset, true, "aac", "stereo");
        Clips.Add(new SceneClip(state, media));
        return state;
    }

    public LayerState AddAdjustment(int track, double start, double duration)
    {
        var state = Clip(LayerKind.Adjustment, track, "Adjustment", start, duration, (0, 0, 1));
        Clips.Add(new SceneClip(state, null));
        return state;
    }

    private static LayerState Clip(LayerKind kind, int track, string name, double start, double duration, (double X, double Y, double Width) place) => new()
    {
        Kind = kind, Name = name, TrackId = track, StartTime = start, Duration = duration,
        PositionX = place.X, PositionY = place.Y, SizeWidth = place.Width > 0 ? place.Width : 0.4,
        SourceWidth = 0.25, SourceHeight = 0.25,
    };

    /// <summary>An effect card, with fixed id, values, words and keyframes.</summary>
    public static EffectState Effect(string type, int id, IReadOnlyDictionary<string, object>? values = null, Dictionary<string, List<Keyframe>>? keys = null, double[]? range = null) => new()
    {
        Type = type,
        Id = new Guid(id, 0, 0, new byte[8]),
        Values = values?.ToDictionary(v => v.Key, v => System.Text.Json.JsonSerializer.SerializeToElement(v.Value)),
        Keys = keys,
        Range = range,
    };

    // ----- Into the builder's state, as MainViewModel.CompileExportState makes it -----

    public SequenceExportState Compile()
    {
        var (frameWidth, frameHeight) = (FrameWidth, FrameHeight);
        var (sourceWidth, sourceHeight) = (Main.Width, Main.Height);
        Layer Make(LayerState state) => Layer.FromState(state, sourceWidth, sourceHeight, frameWidth, frameHeight);

        var mainState = new LayerState
        {
            Kind = LayerKind.MainVideo, Name = "Main Video", StartTime = MainClip.Start, MediaOffset = MainClip.Offset, Duration = MainClip.Duration,
            MediaDuration = Main.Seconds, SourceWidth = 0.25, SourceHeight = 0.25, HasAudio = Main.HasAudio,
        };
        var mainRow = Make(mainState);
        var captionRow = Make(new LayerState
        {
            Kind = LayerKind.Captions, Name = "Subtitles", PositionX = 0.05, PositionY = 0.7, SizeWidth = 0.9, SizeHeight = 0.2, LockAspectRatio = false,
            SourceWidth = 0.25, SourceHeight = 0.25,
        });
        var layers = Clips.Select(c => Make(c.State)).ToList();

        if (FrameEngine)
        {
            foreach (var layer in layers.Where(l => l.IsUsable))
                layer.GetOutputRect(frameWidth, frameHeight, sourceWidth, sourceHeight);
        }

        var copies = new Dictionary<Layer, Layer>(ReferenceEqualityComparer.Instance);
        Layer Snapshot(Layer layer)
        {
            if (!copies.TryGetValue(layer, out var copy))
            {
                copies[layer] = copy = Layer.FromState(layer.ToState(), sourceWidth, sourceHeight, frameWidth, frameHeight);
                FilterGraphBuilder.FoldConstantKeys(copy);
            }

            return copy;
        }

        var order = layers.Where(l => !l.IsAudio).ToList();
        order.Insert(Math.Clamp(MainVideoIndex, 0, order.Count), mainRow);
        var stack = order.Where(l => l is { IsLoading: false, IsEmptyTrack: false }).Select(Snapshot).ToList();
        var sounds = layers.Where(l => l.CarriesSound && !l.IsHidden && !l.IsSoundMuted).Select(Snapshot).ToList();
        var caption = Snapshot(captionRow);
        Prepare("LayLaterClipsOver", stack);
        Prepare("ApplyCrossfades", (IReadOnlyCollection<Layer>)copies.Values);
        Prepare("PairLayerCrossfades", stack);

        var mainClips = MainClips(mainRow);
        var sequenceSeconds = mainClips.Count == 0 ? MainClip.Start : mainClips.Max(c => c.End);
        if (FrameEngine)
            sequenceSeconds = layers.Where(l => l.Duration > 0.001).Select(l => l.StartTime + l.Duration).Append(sequenceSeconds).Max();

        var segments = Segments.Select(s => new ExportSegment(TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(s.End))).ToList();
        var wholeSequence = !FrameEngine || (MainPieces.Count == 0 && (mainClips.Count == 0
            || (mainClips[0].Start < 0.001 && MainClip.Offset < 0.001 && Math.Abs(mainClips.Max(c => c.End) - Main.Seconds) < 0.02 && sequenceSeconds - mainClips.Max(c => c.End) < 0.02)));

        var centerWidth = frameWidth;
        var centerHeight = (int)Math.Round((double)centerWidth * sourceHeight / sourceWidth / 2) * 2;
        var videoFiles = stack.Where(l => l.IsVideoFile).Select(l => Clips.First(c => c.State.ImagePath == l.ImagePath).Media!).DistinctBy(m => m.Path).ToList();

        return new SequenceExportState
        {
            SequenceSeconds = sequenceSeconds,
            OutputDuration = segments.Count > 0 ? segments.Sum(s => s.Duration.TotalSeconds) : sequenceSeconds,
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            SourceFrameRate = FrameRate,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            RequestedSize = FrameEngine ? (frameWidth, frameHeight) : null,
            OutputSize = (frameWidth, frameHeight),
            UseVerticalResolution = frameHeight > frameWidth,
            LocalMediaPath = Main.Path,
            HasSource = true,
            SourceMayHaveAudio = Main.HasAudio,
            MainMediaSeconds = Main.Seconds,
            MainClips = mainClips,
            MainShift = FrameEngine ? MainClip.Start - MainClip.Offset : 0,
            IsMainSpliced = FrameEngine && MainPieces.Count > 0,
            IsMainWholeSequence = wholeSequence,
            CenterRect = ((frameWidth - centerWidth) / 2, (frameHeight - centerHeight) / 2, centerWidth, centerHeight),
            FrameEngine = FrameEngine,
            Stack = stack,
            MainLayer = Snapshot(mainRow),
            BackgroundHidden = BackgroundHidden,
            CaptionLayer = caption,
            FadeIn = FadeIn,
            FadeOut = FadeOut,
            Segments = segments,
            HasCutSegments = segments.Count > 0,
            ChaptersAtCuts = ChaptersAtCuts,
            CutsFilePath = "D:/session/cuts.txt",
            ChaptersFilePath = "D:/session/chapters.txt",
            AudioTracks = AudioTracks,
            LayerAudio = sounds,
            AudioLinked = AudioLinked,
            MergeAudioTracks = MergeAudioTracks,
            VideoCodecArguments = VideoCodecArguments,
            Container = Container,
            OutputPath = $"D:/out/{Name}.{Container}",
            ExistingFiles = ExistingFiles,
            MediaStarts = videoFiles.Where(m => m.Probed).ToDictionary(m => m.Path, m => m.VideoStart, StringComparer.OrdinalIgnoreCase),
            SeekIndexes = videoFiles.Where(m => m.Indexed).ToDictionary(m => m.Path, m => new SeekIndex(m.ContainerStart, m.IFrames), StringComparer.OrdinalIgnoreCase),
            PreviewPaths = new Dictionary<string, string>(Proxies, StringComparer.OrdinalIgnoreCase),
            SequenceEffects = [.. SequenceEffects.Select(e => e.Clone())],
        };
    }

    /// <summary>The main video's clips as MainViewModel.GetMainClips lays them out.</summary>
    private List<MainClip> MainClips(Layer main)
    {
        var media = Main.Seconds;
        var clips = new List<MainClip>();
        if (!FrameEngine)
            return [new MainClip(0, 0, media, 0)];

        void Add(int index, double start, double duration, double offset, double fadeIn, double fadeOut, TransitionStyle inStyle, TransitionStyle outStyle)
        {
            var left = Math.Max(media - offset, 0);
            var length = duration > 0.001 ? Math.Min(duration, left) : left;
            if (length > 0.001)
                clips.Add(new MainClip(index, start, start + length, offset) { FadeIn = fadeIn, FadeOut = fadeOut, FadeInStyle = inStyle, FadeOutStyle = outStyle });
        }

        Add(0, main.StartTime, main.Duration, main.MediaOffset, main.FadeInSeconds, main.FadeOutSeconds, main.FadeInStyle, main.FadeOutStyle);
        for (var i = 0; i < MainPieces.Count; i++)
        {
            var piece = MainPieces[i];
            Add(i + 1, piece.Start, piece.Duration, piece.Offset, piece.FadeIn, piece.FadeOut, piece.FadeInStyle, piece.FadeOutStyle);
        }

        clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = clips.Count - 2; i >= 0; i--)
        {
            if (clips[i].End > clips[i + 1].Start)
                clips[i] = clips[i] with { End = Math.Max(clips[i + 1].Start, clips[i].Start) };
            if (clips[i].End - clips[i].Start < 0.001)
                clips.RemoveAt(i);
        }

        return clips;
    }

    /// <summary>
    /// The view model's own preparation of the copies (crossfades, the order of a track's clips), called as it is: these
    /// are private to MainViewModel, and a fixture that prepared its copies some other way would test something else.
    /// </summary>
    private static void Prepare(string method, object argument)
    {
        var found = typeof(HandPegApp.ViewModels.MainViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingMethodException($"MainViewModel.{method} is not there any more: Scene.Compile prepares the copies as CompileExportState does, and needs updating with it.");
        found.Invoke(null, [argument]);
    }
}
