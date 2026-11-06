using System.Globalization;
using System.IO;
using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.2-AuditB, the primary chain's removal, on its own: audio-chain-typed (a track's processing made into a strip's effects
/// with no FFmpeg words between, AudioChainBuilder.From, against the words it replaced read back as they were, AudioChainOracle)
/// hproj-format6-clips (5.2-Arm64AndNpu, in place of hproj-main-video-migration: a project of clips of files written and opened
/// again, and a project of 5.1's shape, a main video, read as it is and told apart in every way that shape can show), and
/// hproj-51-import (5.2E: such a project opened in the view model, converted, and written over its own file
/// by the window only). Every line says what it proved.
/// </summary>
public static class MigrationTests
{
    // ----- audio-chain-typed -----

    /// <summary>The typed chain against the deleted parser reading the deleted words (AudioChainOracle), on settings written here and 400 fuzzed ones.</summary>
    public static GraphResult AudioChainTyped()
    {
        var text = new StringBuilder("# A track's processing typed (AudioChainBuilder.From) against FFmpeg's words for it read back as before 5.2-AuditB (AudioChainOracle): every effect and parameter within 1e-4.\n");
        var problems = new List<string>();
        try
        {
            ChainCases(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("audio-chain-typed", text.ToString(), problems, 0);
    }

    private const double Within = 1e-4;

    private static void ChainCases(StringBuilder text, List<string> problems)
    {
        var every = new TrackAudioFilters
        {
            HighPass = true, NoiseGate = true, DeNoise = true, Compressor = true, BassTreble = true, BassGain = 3, TrebleGain = -2,
            Limiter = true, Equalizer = true, PitchTempo = true, PitchSemitones = 2, Tempo = 1.1, StereoWidener = true,
        };
        every.EqualizerGains[1] = 4;
        every.EqualizerGains[7] = -6.25;

        // Past every end each setting is held to (the clamps), the gain past what a strip takes too.
        var far = new TrackAudioFilters
        {
            HighPass = true, HighPassHz = 5000, NoiseGate = true, GateThreshold = -120, DeNoise = true, DeNoiseStrength = 0.2,
            Compressor = true, CompressorThreshold = 12, CompressorRatio = 40, BassTreble = true, BassGain = 33, TrebleGain = -33,
            Limiter = true, LimiterCeiling = -60, Equalizer = true, PitchTempo = true, PitchSemitones = -30, Tempo = 9,
            StereoWidener = true, StereoWidth = -2,
        };
        Array.Fill(far.EqualizerGains, 30.0);

        (string Name, TrackAudioFilters? Filters, double GainDb)[] written =
        [
            ("no filters, no gain", null, 0),
            ("a gain alone, -6 dB", null, -6),
            ("a gain too small to write, 0.04 dB", null, 0.04),
            ("filters all off, +3.5 dB", new TrackAudioFilters { HighPassHz = 300, CompressorRatio = 9 }, 3.5),
            ("every filter on, two bands moved, +1.25 dB", every, 1.25),
            ("every setting past its ends, -40 dB", far, -40),
            ("an octave up at half the tempo (two stretches)", new TrackAudioFilters { PitchTempo = true, PitchSemitones = 12, Tempo = 0.5 }, 0),
            ("7.3 semitones down at 1.7 times", new TrackAudioFilters { PitchTempo = true, PitchSemitones = -7.3, Tempo = 1.7 }, 2),
        ];

        foreach (var (name, filters, gain) in written)
        {
            var (typed, words) = Compare(name, filters, gain, problems);
            text.Append(CultureInfo.InvariantCulture, $"\n{name}: {(words.Count == 0 ? "no words" : string.Join(",", words))}\n");
            foreach (var effect in typed.Effects)
                text.Append("  ").Append(effect).Append('\n');
            text.Append(CultureInfo.InvariantCulture, $"  pitch {typed.Pitch:0.######}, tempo {typed.Tempo:0.######}\n");
        }

        // Settings drawn at random (the same every run), each switch on half the time, every value anywhere in and past its range.
        var random = new Random(5202);
        var (count, effects) = (0, 0);
        for (var i = 0; i < 400; i++)
        {
            var gain = random.NextDouble() < 0.25 ? 0 : Between(random, -30, 30);
            var (typed, _) = Compare($"fuzzed setting {i + 1}", Fuzzed(random), gain, problems);
            (count, effects) = (count + 1, effects + typed.Effects.Length);
        }

        text.Append(CultureInfo.InvariantCulture, $"\n{count} fuzzed settings: {effects} effects, each of the same kind, in the same order, every parameter, the pitch and the tempo within {Within:0.####} of the words'\n");
    }

    /// <summary>One setting both ways; what differs into the problems. The typed chain and the words it stands for.</summary>
    private static (AudioChain Typed, List<string> Words) Compare(string name, TrackAudioFilters? filters, double gainDb, List<string> problems)
    {
        var typed = AudioChainBuilder.From(filters, gainDb);
        var words = AudioChainOracle.BuildChain(filters ?? new TrackAudioFilters(), gainDb);
        var read = AudioChainOracle.Parse(words);
        var differs = new List<string>();
        if (read.Unsupported.Length > 0)
            differs.Add($"the words hold what the parser does not play ({string.Join(", ", read.Unsupported)})");
        if (typed.Effects.Length != read.Effects.Length)
        {
            differs.Add($"{typed.Effects.Length} effects, the words {read.Effects.Length}");
        }
        else
        {
            for (var k = 0; k < typed.Effects.Length; k++)
            {
                var (a, b) = (typed.Effects[k], read.Effects[k]);
                double[] ours = [a.P1, a.P2, a.P3, a.P4, a.P5, a.P6, a.P7, a.P8], theirs = [b.P1, b.P2, b.P3, b.P4, b.P5, b.P6, b.P7, b.P8];
                if (a.Kind != b.Kind)
                    differs.Add($"effect {k + 1} is {a.Kind}, the words' {b.Kind}");
                for (var p = 0; p < ours.Length; p++)
                {
                    if (!(Math.Abs(ours[p] - theirs[p]) <= Within))
                        differs.Add(string.Create(CultureInfo.InvariantCulture, $"effect {k + 1} ({a.Kind}) P{p + 1} is {ours[p]:0.######}, the words' {theirs[p]:0.######}"));
                }
            }
        }

        if (!(Math.Abs(typed.Pitch - read.Pitch) <= Within) || !(Math.Abs(typed.Tempo - read.Tempo) <= Within))
            differs.Add(string.Create(CultureInfo.InvariantCulture, $"pitch {typed.Pitch:0.######} and tempo {typed.Tempo:0.######}, the words' {read.Pitch:0.######} and {read.Tempo:0.######}"));
        if (differs.Count > 0)
            problems.Add($"{name} ({string.Join(",", words)}): {string.Join("; ", differs)}");
        return (typed, words);
    }

    private static double Between(Random random, double from, double to) => from + random.NextDouble() * (to - from);

    // A track's settings at random: each switch on half the time, each value anywhere in its slider's range and a little past it.
    private static TrackAudioFilters Fuzzed(Random random)
    {
        bool On() => random.NextDouble() < 0.5;
        var filters = new TrackAudioFilters
        {
            HighPass = On(), HighPassHz = Between(random, 0, 1200),
            NoiseGate = On(), GateThreshold = Between(random, -90, 5),
            DeNoise = On(), DeNoiseStrength = Between(random, 0, 70),
            Compressor = On(), CompressorThreshold = Between(random, -70, 5), CompressorRatio = Between(random, 0.5, 25),
            BassTreble = On(), BassGain = Between(random, -25, 25), TrebleGain = Between(random, -25, 25),
            Limiter = On(), LimiterCeiling = Between(random, -30, 2),
            Equalizer = On(),
            PitchTempo = On(), PitchSemitones = Between(random, -14, 14), Tempo = Between(random, 0.4, 2.2),
            StereoWidener = On(), StereoWidth = Between(random, -0.5, 4.5),
        };
        for (var band = 0; band < filters.EqualizerGains.Length; band++)
            filters.EqualizerGains[band] = random.NextDouble() < 0.3 ? 0 : Between(random, -25, 25);
        return filters;
    }

    // ----- hproj-format6-clips (5.2-Arm64AndNpu: in place of hproj-main-video-migration) -----
    // 5.2-AuditB converted a project of 5.1's shape (a main video) as it was opened, and hproj-main-video-migration followed that
    // conversion clip by clip. Since 5.2-Arm64AndNpu the app no longer converts: such a project is refused as it is opened
    // (ProjectStore.OldShapeOf), and the conversion is the golden tests' own (LegacyMainVideo, through LegacyFixtures), proved by
    // every export golden staying as it was. What is proved here is a project of the current shape, and the refusal.

    private const string Primary = "D:/media/match.mkv", Music = "D:/media/music.m4a", KillfeedMask = "D:/styles/killfeed-mask.png";
    private const int FrameWidth = 1080, FrameHeight = 1920, SourceWidth = 1920, SourceHeight = 1080;

    // The Center box of the main video cut to its middle (left and right 20 %): the frame's width at the 1152 x 1080 shape the
    // crop leaves, in the middle of the vertical frame.
    private static readonly (int X, int Y, int Width, int Height) Center = (0, 454, 1080, 1012);

    /// <summary>
    /// A format 6 project of the current shape written and opened again as the window opens one (the old shape refused), printed
    /// clip by clip; then a project of the shape before 5.2-AuditB refused, in each way that shape can show, and nothing else.
    /// </summary>
    public static GraphResult Format6Clips()
    {
        var text = new StringBuilder("# A format 6 project of clips (each its own file's, a Cutout its clip's child on its clock, the file's streams on Sub-Lanes) written and opened again; "
                                     + "one of the shape before 5.2-AuditB (a main video) refused as it is opened.\n");
        var problems = new List<string>();
        try
        {
            Format6(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("hproj-format6-clips", text.ToString(), problems, 0);
    }

    /// <summary>
    /// The project of the current shape: a vertical frame; on Video 1 two clips of the file, cut to its middle and drawn in the
    /// Center box with their blur fill, the second crossfading in; on Video 2 a Cutout of the first (a clip of the same file, its
    /// child, on its clock, cut to the killfeed and masked); music on an Audio track; under Video 1 the file's Game and Mic streams
    /// on Sub-Lanes (Game -3 dB, a compressor, ducked; Mic a voice, a high-pass), each sound clip its picture clip's child. Its
    /// output choices are Game, Mic and Discord (left out); its captions are heard from the Mic.
    /// </summary>
    private static ProjectState CurrentProject()
    {
        var document = new TimelineDocument
        {
            Width = FrameWidth, Height = FrameHeight, UseVerticalResolution = true, FrameRate = FrameRate.Fps30, NaturalWidth = 1152, NaturalHeight = 1080,
        };
        TimelineTrackModel Add(TimelineTrackKind kind)
        {
            var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
            document.Tracks.Add(track);
            return track;
        }

        var (video1, video2, music) = (Add(TimelineTrackKind.Video), Add(TimelineTrackKind.Video), Add(TimelineTrackKind.Audio));
        LayerState Look(double offset, double start, double duration) => new()
        {
            Kind = LayerKind.VideoFile, Name = "match.mkv", ImagePath = Primary, ImageWidth = SourceWidth, ImageHeight = SourceHeight, MediaDuration = 120,
            SourceX = 0, SourceY = 0, SourceWidth = 1, SourceHeight = 1, CropLeft = 0.2, CropRight = 0.2, BlurFill = true,
            PositionX = Center.X / (double)FrameWidth, PositionY = Center.Y / (double)FrameHeight, SizeWidth = Center.Width / (double)FrameWidth,
            MediaOffset = offset, StartTime = start, Duration = duration,
        };

        TimelineClip Picture(double start, double end, double offset) => new()
        {
            ContentKind = LayerKind.VideoFile, SourcePath = Primary, AudioStreamIndex = -1, Start = start, End = end, MediaOffset = offset, SourceDuration = 120,
            State = Look(offset, start, end - start),
        };

        var (first, second) = (Picture(0, 5, 10), Picture(5, 10, 30));
        second.TransitionIn = 1;
        video1.Clips.AddRange([first, second]);

        video2.Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.VideoFile, SourcePath = Primary, AudioStreamIndex = -1, Start = 3, End = 5, MediaOffset = 13, SourceDuration = 120,
            ParentClipId = first.Id, FadeInSeconds = 0.5,
            State = new LayerState
            {
                Kind = LayerKind.VideoFile, Name = "Killfeed", ImagePath = Primary, ImageWidth = SourceWidth, ImageHeight = SourceHeight, MediaDuration = 120,
                SourceX = 0, SourceY = 0, SourceWidth = 1, SourceHeight = 1, CropLeft = 0.65, CropTop = 0.01, CropRight = 0.01, CropBottom = 0.73,
                PositionX = 0.36, PositionY = 0.03, SizeWidth = 0.62, BlurFill = false, CustomMask = true, MaskPath = KillfeedMask, MaskGrow = 4,
                MediaOffset = 13, StartTime = 3, Duration = 2,
            },
        });

        music.Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.Audio, SourcePath = Music, AudioStreamIndex = 0, Start = 0, End = 10, MediaOffset = 4, SourceDuration = 240, GainDb = -8,
            State = new LayerState { Kind = LayerKind.Audio, Name = "music", ImagePath = Music, MediaDuration = 240, HasAudio = true },
        });

        // The file's streams on Sub-Lanes under Video 1, one lane for each stream place, as a video file's are unpacked.
        foreach (var (name, ordinal, voice) in new[] { ("Game", 0, false), ("Mic", 1, true) })
        {
            var lane = Add(TimelineTrackKind.Audio);
            (lane.Name, lane.StreamOrdinal, lane.ParentTrackId) = (name, ordinal, video1.Id);
            (lane.GainDb, lane.DuckAgainstVoice, lane.AudioFilters) = voice
                ? (0.0, false, new TrackAudioFilters { HighPass = true, HighPassHz = 90 })
                : (-3.0, true, new TrackAudioFilters { Compressor = true, CompressorThreshold = -18, CompressorRatio = 3 });
            foreach (var picture in new[] { first, second })
            {
                lane.Clips.Add(new TimelineClip
                {
                    ContentKind = LayerKind.Audio, SourcePath = Primary, AudioStreamIndex = ordinal, Start = picture.Start, End = picture.End,
                    MediaOffset = picture.MediaOffset, SourceDuration = 120, TransitionIn = picture.TransitionIn, ParentClipId = picture.Id,
                    State = new LayerState
                    {
                        Kind = LayerKind.Audio, Name = name, ImagePath = Primary, MediaDuration = 120, HasAudio = true, IsVoice = voice, AudioCodec = "aac",
                        AudioChannels = "stereo", MediaOffset = picture.MediaOffset, StartTime = picture.Start, Duration = picture.Duration,
                    },
                });
            }
        }

        return new ProjectState
        {
            Name = "Format 6 clips",
            SourcePath = Primary,
            LocalMediaPath = Primary,
            Timeline = document,
            AudioTracks =
            [
                new AudioTrackState(0, AudioTrack.Reencode, "aac", "160k", "Game"),
                new AudioTrackState(1, AudioTrack.Reencode, "libopus", "96k", "Mic"),
                new AudioTrackState(2, AudioTrack.Drop, "aac", "160k", "Discord"),
            ],
            Subtitles = new SubtitleTrackData { Source = new CaptionSource(CaptionSourceKind.Clip, Primary, 1) },
        };
    }

    /// <summary>
    /// A project of 5.1's shape: a vertical frame; on Video 1 the main video in four clips (the first cropped to its middle and
    /// placed by the Center box, the second crossfading in with a J and an L cut, the third's sound unlinked and slipped, the
    /// fourth uncropped in a box of its own); on Video 2 a clip of the main video placed as a picture in picture; on Video 3 a
    /// cut-out of the main video across the first cut, with a mask of the whole frame; music on an Audio track.
    /// </summary>
    private static TimelineDocument OldProject()
    {
        var document = new TimelineDocument { Width = FrameWidth, Height = FrameHeight, UseVerticalResolution = true, FrameRate = FrameRate.Fps30 };
        TimelineTrackModel Add(TimelineTrackKind kind)
        {
            var track = new TimelineTrackModel { Kind = kind, TrackNumber = document.Tracks.Count(t => t.Kind == kind) + 1 };
            document.Tracks.Add(track);
            return track;
        }

        var (video1, video2, video3, audio1) = (Add(TimelineTrackKind.Video), Add(TimelineTrackKind.Video), Add(TimelineTrackKind.Video), Add(TimelineTrackKind.Audio));
        LayerState Look(double x, double y, double width) => new()
        {
            Kind = LayerKind.MainVideo, Name = "Main Video", PositionX = x, PositionY = y, SizeWidth = width, MediaDuration = 120, HasAudio = true,
        };

        TimelineClip Main(double start, double end, double offset, LayerState look) => new()
        {
            ContentKind = LayerKind.MainVideo, SourcePath = Primary, AudioStreamIndex = 0, Start = start, End = end, MediaOffset = offset, SourceDuration = 120, State = look,
        };

        var anchor = Look(0, Center.Y / (double)FrameHeight, 1);
        (anchor.CropLeft, anchor.CropRight) = (0.2, 0.2);
        var first = Main(0, 5, 10, anchor);
        var second = Main(5, 10, 30, anchor);
        (second.TransitionIn, second.AudioLeadInSeconds, second.AudioTailOutSeconds) = (1, 0.5, 0.25);
        var third = Main(10, 12, 50, anchor);
        (third.AudioLinked, third.AudioSlipSeconds) = (false, 0.3);
        var fourth = Main(12, 16, 60, Look(0, Center.Y / (double)FrameHeight, 1));
        video1.Clips.AddRange([first, second, third, fourth]);

        video2.Clips.Add(Main(2, 6, 70, Look(0.6, 0.05, 0.35)));

        var piece = new LayerState
        {
            Kind = LayerKind.Video, Name = "Killfeed", SourceX = 0.65, SourceY = 0.01, SourceWidth = 0.34, SourceHeight = 0.26,
            PositionX = 0.36, PositionY = 0.03, SizeWidth = 0.62, CustomMask = true, MaskFrame = true, MaskPath = KillfeedMask, MaskGrow = 4,
        };
        video3.Clips.Add(new TimelineClip { ContentKind = LayerKind.Video, SourcePath = Primary, Start = 3, End = 8, ParentClipId = first.Id, FadeInSeconds = 0.5, FadeOutSeconds = 0.25, State = piece });

        audio1.Clips.Add(new TimelineClip
        {
            ContentKind = LayerKind.Audio, SourcePath = Music, AudioStreamIndex = 0, Start = 0, End = 16, MediaOffset = 4, SourceDuration = 240, GainDb = -8,
            State = new LayerState { Kind = LayerKind.Audio, Name = "music", ImagePath = Music, MediaDuration = 240, HasAudio = true },
        });
        return document;
    }

    private static void Format6(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        // ----- The current shape: written, and opened again as the window opens a project -----
        var state = CurrentProject();
        var json = ProjectStore.Serialize(state);
        Expect(ProjectStore.OldShapeOf(state.Timeline, state.Deleted, state.Subtitles) is null && !ProjectStore.HoldsOldKinds(state.Timeline, state.Deleted),
            "a project of clips is of the current shape: nothing in it is of the old kinds");
        var opened = ProjectStore.Deserialize(json);
        Expect(ProjectStore.Serialize(opened) == json, "opened as the window opens a project, and written again: the same file");

        var document = opened.Timeline;
        text.Append(CultureInfo.InvariantCulture, $"\nthe Sequence's natural size: {document.NaturalWidth} x {document.NaturalHeight}\n");
        text.Append("the output choices: ").AppendJoin("; ", opened.AudioTracks.Select(a => $"{a.Index} {a.Title} {a.Codec} {a.Bitrate} {a.Action}")).Append('\n');
        text.Append("\nthe document opened:\n");
        Describe(text, document);
        text.Append('\n');

        var wrong = document.FindProblems();
        Expect(wrong.Count == 0, "the document holds together (TimelineDocument.FindProblems)");
        problems.AddRange(wrong.Select(p => "the opened document: " + p));
        Expect((document.NaturalWidth, document.NaturalHeight) == (1152, 1080), "the Sequence's natural size is the document's own");

        var clips = document.Tracks.SelectMany(t => t.Clips).ToList();
        Expect(clips.All(c => c.ContentKind is LayerKind.VideoFile or LayerKind.Audio) && clips.All(c => c.State.Kind == c.ContentKind),
            "every clip and every look is a clip of a file or a sound: none of the old kinds");
        var (first, second) = (document.Tracks[0].Clips[0], document.Tracks[0].Clips[1]);
        Expect(Box(first) == Box(second) && new[] { first, second }.All(c => c.State.BlurFill == true), "the clips of the file are drawn in one box, each with its blur fill");
        var cutout = document.Tracks[1].Clips[0];
        Expect(cutout.ParentClipId == first.Id && cutout.SourcePath == first.SourcePath && Same(cutout.MediaOffset, first.MediaOffset + (cutout.Start - first.Start) * first.Speed)
               && cutout.AudioStreamIndex == -1 && cutout.State.BlurFill == false,
            "the Cutout is its clip's child, a clip of the same file on its clock (the file at 13 s), silent, with no blur fill");

        var lanes = document.Tracks.Where(t => t.StreamOrdinal is >= 0).ToList();
        Expect(lanes.Count == 2 && lanes.All(l => l.ParentTrackId == document.Tracks[0].Id), "the file's streams are on Sub-Lanes under Video 1");
        Expect(lanes is [{ StreamOrdinal: 0, GainDb: -3.0, DuckAgainstVoice: true, AudioFilters.Compressor: true }, { StreamOrdinal: 1, DuckAgainstVoice: false, AudioFilters.HighPass: true }],
            "each lane mixes its stream itself: Game -3 dB, its compressor, ducked; Mic its high-pass");
        Expect(lanes.All(l => l.Clips.Count == 2 && l.Clips[0].ParentClipId == first.Id && l.Clips[1].ParentClipId == second.Id
                              && l.Clips.All(c => c.AudioStreamIndex == l.StreamOrdinal && c.SourcePath == Primary)),
            "each sound clip is its picture clip's child, of the same file, from its stream");
        Expect(lanes[1].Clips.All(c => c.State.IsVoice) && lanes[0].Clips.All(c => !c.State.IsVoice), "the Mic's sound clips are a voice, the Game's are not");
        Expect(opened.AudioTracks.Select(a => (a.Title, a.Action)).SequenceEqual([("Game", AudioTrack.Reencode), ("Mic", AudioTrack.Reencode), ("Discord", AudioTrack.Drop)]),
            "the output choices are read back: Game, Mic, and Discord left out");
        Expect(opened.Subtitles?.Source is { Kind: CaptionSourceKind.Clip, Stream: 1, IsAnchored: true }, "the captions are heard from a clip's stream (the Mic's), anchored to it");

        // ----- The shape before 5.2-AuditB: read as it is, and told apart (converted as it is opened: hproj-51-import) -----
        text.Append('\n');
        string? Shape(ProjectState project)
        {
            var read = ProjectStore.Deserialize(ProjectStore.Serialize(project));
            return ProjectStore.OldShapeOf(read.Timeline, read.Deleted, read.Subtitles);
        }

        // A copy of the current project to change one way at a time.
        ProjectState Changed(Action<ProjectState> change)
        {
            var copy = ProjectStore.Deserialize(json);
            change(copy);
            return copy;
        }

        var old = new ProjectState { Name = "A 5.1 project", SourcePath = Primary, LocalMediaPath = Primary, Timeline = OldProject() };
        var oldJson = ProjectStore.Serialize(old);
        var told = Shape(old);
        text.Append("5.1's project: ").Append(told ?? "(of the current shape)").Append('\n');
        Expect(told == "5 clips of the main video and 1 cut-out of it",
            "a project of 5.1's shape (four clips of the main video, a picture in picture of it, a cut-out of it) is read as it is, and told apart by what it holds");
        Expect(ProjectStore.Deserialize(oldJson).Timeline.Tracks.Count == 4, "read as it is: nothing of it is converted or dropped by reading (the view model converts it as it opens it)");
        Expect(ProjectStore.HoldsOldKinds(old.Timeline, null) && !ProjectStore.HoldsOldKinds(state.Timeline, state.Deleted),
            "an undo step holding the old kinds is told apart the same way (it is let go when it is stepped to)");

        // Read from its file: the file is left as it was (only the window's opening writes the converted project over it).
        var folder = Path.Combine(AppPaths.Temp, "format6-clips");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "A 5.1 project" + ProjectStore.Extension);
        File.WriteAllText(path, oldJson);
        var bytes = File.ReadAllBytes(path);
        var fromFile = ProjectStore.Load(path);
        Expect(ProjectStore.OldShapeOf(fromFile.Timeline, fromFile.Deleted, fromFile.Subtitles) is not null && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes),
            "read from its file it is the same project, and the file is left as it was, to the byte");
        File.Delete(path);

        // Each way the old shape can show, alone in a project of the current shape.
        var oneMain = Shape(Changed(p =>
        {
            var clip = p.Timeline.Tracks[0].Clips[1];
            clip.ContentKind = LayerKind.MainVideo;
            clip.State = new LayerState { Kind = LayerKind.MainVideo, Name = "Main Video", MediaDuration = 120 };
        }));
        text.Append("a clip of the main video: ").Append(oneMain ?? "(of the current shape)").Append('\n');
        Expect(oneMain == "1 clip of the main video", "one clip of the main video among clips of files is told apart");

        var binned = Shape(Changed(p =>
        {
            var cut = p.Timeline.Tracks[1].Clips[0].Snapshot();
            (cut.ContentKind, cut.State) = (LayerKind.Video, new LayerState { Kind = LayerKind.Video, Name = "Killfeed" });
            p.Deleted = [new DeletedClip(p.Timeline.Tracks[0].Clips[0].Snapshot(), p.Timeline.Tracks[0].Id, TimelineTrackKind.Video, 0)
                { Children = [new DeletedClip(cut, p.Timeline.Tracks[1].Id, TimelineTrackKind.Video, 1)] }];
        }));
        text.Append("a cut-out of it in the recycle bin: ").Append(binned ?? "(of the current shape)").Append('\n');
        Expect(binned?.Contains("recycle bin", StringComparison.Ordinal) == true, "a cut-out of the main video that went to the recycle bin with its clip is told apart");

        var folded = Shape(Changed(p =>
        {
            var main = p.Timeline.Tracks[0].Clips[0].Snapshot();
            (main.ContentKind, main.State, main.Start, main.End) = (LayerKind.MainVideo, new LayerState { Kind = LayerKind.MainVideo, Name = "Main Video" }, 0, 2);
            p.Timeline.Folds = [new FoldedSegment(Guid.NewGuid(), p.Timeline.Tracks[0].Clips[1].Id, false, true, 2, [new FoldedClip(p.Timeline.Tracks[0].Id, TimelineTrackKind.Video, main)])];
        }));
        text.Append("a clip of it folded out: ").Append(folded ?? "(of the current shape)").Append('\n');
        Expect(folded == "1 clip of the main video", "a clip of the main video folded out of the timeline is told apart");

        var heard = Shape(Changed(p => p.Subtitles = new SubtitleTrackData { Source = new CaptionSource(CaptionSourceKind.MainFile, Primary, 1) }));
        text.Append("captions heard from the main file: ").Append(heard ?? "(of the current shape)").Append('\n');
        Expect(heard == "its captions are heard from the main video's file", "captions heard from the main video's file are told apart");

        // Not a sign of it: the old stream mixing a 5.1 project kept beside the output choices (a 5.2 project may carry a duck there).
        var mixed = Shape(Changed(p => p.AudioTracks = [.. p.AudioTracks.Select(a => a.Index == 0 ? a with { GainDb = -3, AutoDuck = true, Offset = 0.1 } : a)]));
        Expect(mixed is null, "a stream's old mixing beside its output choice alone is no sign of the old shape: nothing to convert");
    }

    // ----- hproj-51-import (5.2E, the user's answer) -----
    // A project of 5.1's shape opened as the app opens one (MainViewModel.LoadProjectAsync: the file it opened probed, its settings and
    // streams put back, RestoreStateAsync), with nothing faked but the probe: the media file is a stand-in whose facts are what this
    // session already inspected (MediaProbe's memory of it, matched by its size and write time). First as a harness opens it (SeekBench,
    // AvSync: converted in memory, the file left as it was), then as the window does (SavesConvertedProjects: written over its own file,
    // the same name, nothing beside it), then opened again (nothing left to convert).

    private const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

    /// <summary>A 5.1 project opened in the view model: converted, written over its file by the window alone, and opened again as one of this version's.</summary>
    public static GraphResult Import51()
    {
        var text = new StringBuilder("# A 5.1 project (a main video) opened as the app opens one: converted (LegacyMainVideo through MainViewModel.ConvertLegacy), "
                                     + "written over its own file only by the window (the same name), and opened again as a project of this version's.\n");
        var problems = new List<string>();
        try
        {
            Import(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("hproj-51-import", text.ToString(), problems, 0);
    }

    /// <summary>
    /// The 5.1 project of <see cref="OldProject"/> with what a 5.1 project kept beside it: the vertical frame asked for in its settings, the
    /// file's three streams as the Audio tab mixed them (Game -3 dB, a compressor, ducked; Mic a voice, a high-pass, slipped 0.1 s,
    /// silenced from 31 to 32 s of it; Discord left out), and captions heard from the main file's Mic (a line at 12-13 s of the file, which
    /// the first clip plays at 2-3 s, its Mic 0.1 s later). Its frame is the 1080 x 1920 its Resolution &amp; Cropping names with the aspect unlinked: a frame
    /// that keeps the aspect is the natural shape's, whatever was typed (5.1 saved it so).
    /// </summary>
    private static ProjectState OldState(string media)
    {
        var document = OldProject();
        foreach (var clip in document.Tracks.SelectMany(t => t.Clips).Where(c => c.SourcePath == Primary))
            clip.SourcePath = media;

        var settings = new EncodingPreset { Name = "A 5.1 project", OutputWidth = "1080", OutputHeight = "1920", UseVerticalResolution = true, KeepAspectRatio = false };
        return new ProjectState
        {
            Name = "A 5.1 project",
            SourcePath = media,
            LocalMediaPath = media,
            Timeline = document,
            Settings = settings,
            AudioTracks =
            [
                new AudioTrackState(0, AudioTrack.Reencode, "aac", "160k", "Game", GainDb: -3,
                    Filters: new TrackAudioFilters { Compressor = true, CompressorThreshold = -18, CompressorRatio = 3 }, AutoDuck: true),
                new AudioTrackState(1, AudioTrack.Reencode, "libopus", "96k", "Mic", Filters: new TrackAudioFilters { HighPass = true, HighPassHz = 90 },
                    Offset: 0.1, Pieces: [new(0, 31, false), new(31, 32, true), new(32, 120, false)], IsVoice: true),
                new AudioTrackState(2, AudioTrack.Drop, "aac", "160k", "Discord", GainDb: 2),
            ],
            Subtitles = new SubtitleTrackData { Source = new CaptionSource(CaptionSourceKind.MainFile, media, 1), Anchors = [new SubtitleCueState(12, 13, "nice shot")] },
        };
    }

    // The window's thread, turned until the task is done (or 30 s have gone): the view model's continuations come back to it.
    private static void PumpUntil(Task task)
    {
        var until = Environment.TickCount64 + 30_000;
        while (!task.IsCompleted && Environment.TickCount64 < until)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (!task.IsCompleted)
                Thread.Sleep(5);
        }
    }

    /// <summary>Opens a project in a new view model, from the window's thread's own queue (as a click would), and waits for it.</summary>
    private static MainViewModel OpenIn(string path, bool savesConverted)
    {
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports) = (false, false);
        var vm = new MainViewModel { SavesConvertedProjects = savesConverted };
        var opening = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => vm.LoadProjectAsync(path)).Task.Unwrap();
        PumpUntil(opening);
        if (!opening.IsCompleted)
            throw new TimeoutException("the project did not open within 30 s");
        opening.GetAwaiter().GetResult();
        return vm;
    }

    private static void Import(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var folder = Path.Combine(AppPaths.Temp, "import-51");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        // The stand-in file, and what inspecting it found (as MediaProbe keeps it: by its full path, its size and its last write).
        var media = Path.Combine(folder, "match.mkv");
        File.WriteAllBytes(media, new byte[4096]);
        var stamp = new FileInfo(media);
        var info = new MediaInfo("matroska", 120, 0, new VideoStreamInfo(SourceWidth, SourceHeight, "h264", 30, "16:9", "1:1", "bt709", "bt709", 0),
            [new AudioStreamInfo(0, "aac", 2, "stereo", 48000, 160000, "Game", ""), new AudioStreamInfo(1, "aac", 2, "stereo", 48000, 160000, "Mic", ""),
             new AudioStreamInfo(2, "aac", 2, "stereo", 48000, 160000, "Discord", "")], []);
        var probed = (System.Collections.Concurrent.ConcurrentDictionary<string, (MediaInfo, long, long)>)typeof(MediaProbe).GetField("Probed", Hidden)!.GetValue(null)!;
        probed[Path.GetFullPath(media)] = (info, stamp.Length, stamp.LastWriteTimeUtc.Ticks);

        var path = Path.Combine(folder, "A 5.1 project" + ProjectStore.Extension);
        File.WriteAllText(path, ProjectStore.Serialize(OldState(media)).Replace(ProjectStore.WriterName, "HandPeg 5.1.0", StringComparison.Ordinal));
        var bytes = File.ReadAllBytes(path);
        string[] Files() => [.. Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];
        var before = Files();

        // ----- As a harness opens it: converted in memory, the file left as it was -----
        var harness = OpenIn(path, savesConverted: false);
        text.Append("opened by a harness: ").Append(harness.StatusText).Append('\n');
        text.Append("converted from: ").Append(harness.ConvertedFrom ?? "(nothing)").Append('\n');
        Expect(harness.ConvertedFrom == "5 clips of the main video and 1 cut-out of it", "the view model says what it converted");
        Expect(!ProjectStore.HoldsOldKinds(harness.Document, null) && harness.Document.FindProblems().Count == 0,
            "the Sequence it holds is of this version's shape (no clip of the old kinds) and holds together");
        Expect(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) && Files().SequenceEqual(before),
            "a harness's opening (SavesConvertedProjects off: SeekBench, AvSync) leaves the 5.1 file as it was, to the byte, and writes nothing beside it");

        // ----- As the window opens it: converted, and written over its own file -----
        var window = OpenIn(path, savesConverted: true);
        text.Append("opened by the window: ").Append(window.StatusText).Append('\n');
        var header = ProjectStore.PeekHeader(path);
        Expect(header is { Format: ProjectFile.FormatName, Version: ProjectFile.CurrentVersion } && header.Value.WrittenBy == ProjectStore.WriterName,
            "the window wrote the converted project over its own file: format 6, written by this version");
        Expect(Files().SequenceEqual(before) && window.CurrentProjectPath == path,
            "under the same name, and nothing beside it (no second project, no part-written file): Save goes on writing to it");
        Expect(!window.HasUnsavedChanges, "what was written is what is open: nothing counts as unsaved");
        var written = ProjectStore.Load(path, unpackMasks: false);
        Expect(ProjectStore.OldShapeOf(written.Timeline, written.Deleted, written.Subtitles) is null,
            "the file holds nothing of the old shape: no clip of the main video, no cut-out of it, no captions heard from its file");
        text.Append(CultureInfo.InvariantCulture,
            $"captions written: {written.Subtitles?.Source?.Kind.ToString() ?? "none"} stream {written.Subtitles?.Source?.Stream}, {written.Subtitles?.Anchors?.Count ?? 0} anchored line(s), "
            + $"{window.SubtitleCues.Count} shown{(window.SubtitleCues.Count > 0 ? $" from {GoldenText.Num(window.SubtitleCues[0].Start)} s" : "")}\n");
        Expect(written.Subtitles?.Source is { Kind: CaptionSourceKind.Clip, Stream: 1, IsAnchored: true } source && source.Path == media
               && written.Subtitles.Anchors is [{ Start: 12, End: 13, Text: "nice shot" }] && window.SubtitleCues.Count == 1 && Same(window.SubtitleCues[0].Start, 2.1),
            "its captions are heard from a clip's stream (the Mic's) of the same file, anchored to it in the file's time, and shown at 2.1 s: where the first clip plays that moment of the Mic, slipped 0.1 s");
        Expect(written.AudioTracks.Count == 3 && written.AudioTracks.All(a => a.GainDb == 0 && a.Filters is not { IsActive: true } && !a.AutoDuck && !a.IsVoice && a.Offset == 0 && a.Pieces is not { Count: > 0 })
               && written.AudioTracks.Select(a => (a.Title, a.Action)).SequenceEqual([("Game", AudioTrack.Reencode), ("Mic", AudioTrack.Reencode), ("Discord", AudioTrack.Drop)]),
            "its streams' old mixing is let go (it lives on the Sub-Lanes now); their output choices stay: Game, Mic, Discord left out");

        var document = written.Timeline;
        text.Append(CultureInfo.InvariantCulture, $"\nthe Sequence's natural size: {document.NaturalWidth} x {document.NaturalHeight}, frame {window.FrameWidth} x {window.FrameHeight}\n");
        text.Append("\nthe project as the window wrote it:\n");
        Describe(text, document);
        text.Append('\n');
        Expect((document.NaturalWidth, document.NaturalHeight) == (1152, 1080) && (window.FrameWidth, window.FrameHeight) == (FrameWidth, FrameHeight),
            "the Sequence's natural size is the file's picture cut by the first clip's crop, in the 1080 x 1920 frame its settings ask for");
        var wrong = document.FindProblems();
        Expect(wrong.Count == 0, "the document holds together (TimelineDocument.FindProblems)");
        problems.AddRange(wrong.Select(p => "the written document: " + p));
        var main = document.Tracks[0].Clips;
        Expect(main.Count == 4 && main.Take(3).All(c => Box(c) == Center) && main.All(c => c.ContentKind == LayerKind.VideoFile && c.SourcePath == media && c.AudioStreamIndex == -1),
            "the main video's clips are clips of its file, the three placed by the Center box drawn in it as they were, silent (their sound on Sub-Lanes)");
        var lanes = document.Tracks.Where(t => t.StreamOrdinal is >= 0).GroupBy(t => t.StreamOrdinal!.Value).OrderBy(g => g.Key).ToList();
        Expect(lanes.Count == 3 && lanes.All(g => g.All(l => l.ParentTrackId == document.Tracks[0].Id))
               && lanes[0].All(l => l is { GainDb: -3.0, DuckAgainstVoice: true, AudioFilters.Compressor: true }) && lanes[1].All(l => l is { AudioFilters.HighPass: true, DuckAgainstVoice: false })
               && lanes[2].All(l => l.IsMuted),
            "each of the file's streams is on Sub-Lanes under Video 1 (two each, for the crossfade) mixing it as the Audio tab did: Game -3 dB, its compressor, ducked; Mic its high-pass; Discord muted (left out)");
        Expect(lanes[1].SelectMany(l => l.Clips).All(c => c.State.IsVoice) && lanes[1].SelectMany(l => l.Clips).Any(c => c.IsMuted && Same(c.Start, 6.1) && Same(c.End, 7.1)),
            "the Mic's sound clips are a voice, and its silenced second (31 to 32 s of the file) is a muted part from 6.1 to 7.1 s");
        var parts = document.Tracks.SelectMany(t => t.Clips).Where(c => c.State.Name == "Killfeed").OrderBy(c => c.Start).ToList();
        Expect(parts.Count == 2 && parts.All(c => c.ContentKind == LayerKind.VideoFile && c.SourcePath == media && c.ParentClipId is not null),
            "the cut-out across the cut is two Cutouts, each a clip of the file and its clip's child");

        // ----- Opened again: a project of this version's, nothing to convert -----
        var writtenBytes = File.ReadAllBytes(path);
        var again = OpenIn(path, savesConverted: true);
        Expect(again.ConvertedFrom is null && File.ReadAllBytes(path).AsSpan().SequenceEqual(writtenBytes) && Files().SequenceEqual(before),
            "opened again it is a project of this version's: nothing converted, nothing written");
        Directory.Delete(folder, recursive: true);

        // The test's own folder (it holds this run's process id) as a placeholder, so that the text is the same on every run.
        text.Replace(folder + Path.DirectorySeparatorChar, "<folder>\\");
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>A clip's box in the frame's pixels, as its layer comes out of its look (Layer.GetOutputRect at its picture's size).</summary>
    private static (int X, int Y, int Width, int Height) Box(TimelineClip clip)
    {
        var look = clip.State;
        var layer = Layer.FromState(look, look.ImageWidth, look.ImageHeight, FrameWidth, FrameHeight);
        return layer.GetOutputRect(FrameWidth, FrameHeight, look.ImageWidth, look.ImageHeight);
    }

    /// <summary>The document track by track (bottom first), clip by clip: what the conversion made of each.</summary>
    private static void Describe(StringBuilder text, TimelineDocument document)
    {
        string PlaceOf(Guid? id)
        {
            for (var t = 0; t < document.Tracks.Count; t++)
            {
                var at = document.Tracks[t].Clips.FindIndex(c => c.Id == id);
                if (at >= 0)
                    return $"track {t} clip {at}";
            }

            return "nothing";
        }

        static string N(double value) => GoldenText.Num(value);
        for (var t = 0; t < document.Tracks.Count; t++)
        {
            var track = document.Tracks[t];
            var under = track.ParentTrackId is { } parent ? $", under track {document.Tracks.FindIndex(p => p.Id == parent)}" : "";
            var lane = track.StreamOrdinal is { } ordinal ? $", stream place {ordinal}" : "";
            var mixing = string.Create(CultureInfo.InvariantCulture,
                $"{(track.GainDb != 0 ? $", {N(track.GainDb)} dB" : "")}{(track.AudioFilters is { IsActive: true } f ? $", {f.ActiveCount} filter(s)" : "")}{(track.DuckAgainstVoice ? ", ducked" : "")}{(track.IsMuted ? ", muted" : "")}{(track.IsHidden ? ", hidden" : "")}");
            text.Append(CultureInfo.InvariantCulture, $"  track {t}: {track.Kind} \"{track.DisplayName}\"{under}{lane}{mixing}\n");
            foreach (var clip in track.Clips)
            {
                var look = clip.State;
                text.Append(CultureInfo.InvariantCulture,
                    $"    {clip.ContentKind} \"{look.Name}\" {N(clip.Start)}-{N(clip.End)} s, the file at {N(clip.MediaOffset)} s x{N(clip.Speed)} (of {N(clip.SourceDuration)} s), v{clip.VideoStreamIndex} a{clip.AudioStreamIndex}");
                if (clip.ParentClipId is { } parentClip)
                    text.Append(", child of ").Append(PlaceOf(parentClip));
                if (clip.IsHidden)
                    text.Append(", off");
                if (clip.IsMuted)
                    text.Append(", muted");
                if (clip.GainDb != 0)
                    text.Append(", ").Append(N(clip.GainDb)).Append(" dB");
                if (clip.FadeInSeconds > 0 || clip.FadeOutSeconds > 0)
                    text.Append(CultureInfo.InvariantCulture, $", fades {N(clip.FadeInSeconds)}/{N(clip.FadeOutSeconds)}");
                if (clip.TransitionIn > 0)
                    text.Append(", crossfade in ").Append(N(clip.TransitionIn));
                if (!clip.AudioLinked || clip.AudioSlipSeconds != 0 || clip.AudioLeadInSeconds != 0 || clip.AudioTailOutSeconds != 0)
                    text.Append(CultureInfo.InvariantCulture, $", sound {(clip.AudioLinked ? "linked" : "unlinked")} slip {N(clip.AudioSlipSeconds)} J {N(clip.AudioLeadInSeconds)} L {N(clip.AudioTailOutSeconds)}");
                text.Append('\n');

                if (clip.ContentKind == LayerKind.Audio)
                {
                    text.Append(CultureInfo.InvariantCulture, $"      look: {look.Kind}, {look.AudioChannels}{(look.IsVoice ? ", a voice" : "")}\n");
                    continue;
                }

                var (x, y, w, h) = Box(clip);
                text.Append(CultureInfo.InvariantCulture,
                    $"      look: {look.Kind} {look.ImageWidth} x {look.ImageHeight} \"{look.ImagePath}\", at ({N(look.PositionX)}, {N(look.PositionY)}) {N(look.SizeWidth)} wide{(look.LockAspectRatio ? "" : $" {N(look.SizeHeight)} tall")} = box ({x}, {y}) {w} x {h}\n");
                text.Append(CultureInfo.InvariantCulture,
                    $"      crop ({N(look.CropLeft)}, {N(look.CropTop)}, {N(look.CropRight)}, {N(look.CropBottom)}), source ({N(look.SourceX)}, {N(look.SourceY)}) {N(look.SourceWidth)} x {N(look.SourceHeight)}, blur fill {(look.BlurFill is { } fill ? (fill ? "on" : "off") : "unset")}{(look.CustomMask ? $", mask {(look.MaskFrame ? "of the frame" : "of its picture")} grown {look.MaskGrow}" : "")}, sound {(look.HasAudio ? "on" : "off")}\n");
            }
        }
    }
}
