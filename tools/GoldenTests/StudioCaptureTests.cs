using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// The close-gap pill before a track's first clip; the Voiceover's own WAV segments (written as recorded, joined natively) and a live
/// WASAPI recording on this PC's microphone; the Cutout Studio opening on a clip's whole Cutout family (bitmap-masked pieces of an
/// older Game Preset traced to outlines), whatever was picked, with the heat lamp's still and a frame-0 backdrop. Every line says
/// what it proved; machine-dependent details go to the console only.
/// </summary>
public static class StudioCaptureTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("timeline-leading-gap", "# The close-gap pill before a track's first clip: found on a top-level row from 0 s; closing it brings the track's clips to the start, the other tracks kept.", LeadingGap);
        yield return Case("voiceover-wav-segments", "# The Voiceover's WAV segments: read back as written, sizes kept current while recording, joined natively (a different rate or channel count converted).", WavSegments);
        yield return Case("voiceover-wasapi-capture", "# Voiceover capture through WASAPI on this PC (no FFmpeg): microphones listed once each, two stretches recorded into one WAV, or the reason it cannot.", Capture);
        yield return Case("cutout-studio-preset-family", "# The Cutout Studio opens on the picked clip's base video with all its Cutouts (an older preset's bitmap-masked pieces traced to outlines), its preset's name, and a still on frame 0.", PresetFamily);
    }

    // ----- timeline-leading-gap -----

    private static void LeadingGap(Action<bool, string> expect)
    {
        // A control 1000 DIPs wide, ten seconds across; clips at 2 to 4 s and 6 to 8 s.
        var surface = new UnifiedTimelineControl();
        surface.Measure(new Size(1000, 300));
        surface.Arrange(new Rect(0, 0, 1000, 300));
        surface.SetView(0, 10, 10);
        var scale = surface.PixelsPerSecond;
        TimelineBlock Block(double from, double to, string name) => new() { Left = from * scale, Width = (to - from) * scale, Name = name, Clip = name, CanDrag = () => true };
        bool Near((double Left, double Right) gap, double left, double right) => Math.Abs(gap.Left - left) < 1e-6 && Math.Abs(gap.Right - right) < 1e-6;

        var row = new TimelineRow { Key = "v1", Data = "v1", Kind = TimelineRowKind.Video, Height = 56, ClosesGaps = true };
        row.Blocks.Add(Block(2, 4, "a"));
        row.Blocks.Add(Block(6, 8, "b"));
        var tight = new TimelineRow { Key = "v2", Data = "v2", Kind = TimelineRowKind.Video, Height = 56, ClosesGaps = true };
        tight.Blocks.Add(Block(0.1, 3, "c"));
        var locked = new TimelineRow { Key = "v3", Data = "v3", Kind = TimelineRowKind.Video, Height = 56, ClosesGaps = false };
        locked.Blocks.Add(Block(2, 4, "d"));
        surface.SetRows([row, tight, locked]);
        expect(row.Gaps.Count == 2 && Near(row.Gaps[0], 0, 2 * scale) && Near(row.Gaps[1], 4 * scale, 6 * scale),
            "a top-level row: a pill for the gap before its first clip (0 to 2 s) and for the one between its clips (4 to 6 s)");
        expect(tight.Gaps.Count == 0, "a first clip 0.1 s in leaves too little room for the pill: none");
        expect(locked.Gaps.Count == 0, "a locked track's row closes no gap, before its first clip either");

        // The model: V1 holds A (2 to 4 s) and B (6 to 8 s), V2 holds C (1 to 3 s); the leading gap of V1 closed.
        var (a, b, c) = (Clip("a", 2, 4), Clip("b", 6, 8), Clip("c", 1, 3));
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps30 };
        using (var setup = doc.BeginTransaction("Set up"))
        {
            var first = setup.InsertTrack(0, TimelineTrackKind.Video);
            setup.PlaceClip(first, a);
            setup.PlaceClip(first, b);
            setup.PlaceClip(setup.InsertTrack(1, TimelineTrackKind.Video), c);
            setup.Commit();
        }

        using var tx = doc.BeginTransaction("Close the leading gap");
        var closed = tx.CloseGap(doc.Tracks[0], 0, a.Start, rippleAllUnlockedTracks: false);
        tx.Commit();
        expect(Math.Abs(closed - 2) < 1e-9 && a.Start == 0 && a.End == 2 && b.Start == 4 && b.End == 6,
            "closed: the track's first clip starts at 0 s and the one after it moves back as far (2 s)");
        expect(c.Start == 1 && c.End == 3 && doc.FindProblems().Count == 0, "the other track is kept as it was (Ripple All Tracks off), and the Sequence is sound");

        static TimelineClip Clip(string name, double start, double end) =>
            new() { SourcePath = $"D:/media/{name}.mp4", Start = start, End = end, SourceDuration = 60 };
    }

    // ----- voiceover-wav-segments -----

    private static void WavSegments(Action<bool, string> expect)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-wav-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            static short[] Tone(int rate, int channels, double seconds, double hertz)
            {
                var frames = (int)(rate * seconds);
                var samples = new short[frames * channels];
                for (var f = 0; f < frames; f++)
                {
                    for (var ch = 0; ch < channels; ch++)
                        samples[f * channels + ch] = (short)(12000 * Math.Sin(2 * Math.PI * hertz * f / rate + ch));
                }

                return samples;
            }

            string Write(string name, int rate, int channels, short[] samples)
            {
                var path = Path.Combine(folder, name);
                using var file = new WavFile(path, rate, channels);
                file.Write(samples);
                return path;
            }

            var (toneA, toneB, toneC) = (Tone(48000, 2, 0.5, 440), Tone(48000, 2, 0.25, 660), Tone(44100, 1, 0.5, 220));
            var (a, b, c) = (Write("a.wav", 48000, 2, toneA), Write("b.wav", 48000, 2, toneB), Write("c.wav", 44100, 1, toneC));
            var read = WavFile.Read(a);
            expect(read is { Rate: 48000, Channels: 2 } back && back.Samples.SequenceEqual(toneA), "a segment reads back as written: 48 kHz stereo, every sample");
            var bytes = File.ReadAllBytes(a);
            expect(BitConverter.ToUInt32(bytes, 4) == bytes.Length - 8 && BitConverter.ToUInt32(bytes, 40) == bytes.Length - WavFile.HeaderBytes
                   && BitConverter.ToUInt16(bytes, 20) == 1 && BitConverter.ToUInt16(bytes, 34) == 16,
                "its header is 16-bit PCM with the RIFF and data sizes of the file");

            // Still being written: after two seconds of audio the header already counts them, so a crash leaves a playable file.
            var growing = Path.Combine(folder, "growing.wav");
            using (var file = new WavFile(growing, 48000, 2))
            {
                file.Write(Tone(48000, 2, 2.5, 330));
                using var peek = new FileStream(growing, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var header = new byte[WavFile.HeaderBytes];
                peek.ReadExactly(header);
                expect(BitConverter.ToUInt32(header, 40) >= 2 * 48000 * 4, "while still recording, the header counts the audio written up to its last two-second mark");
            }

            var same = Path.Combine(folder, "same.wav");
            WavFile.Join([a, b], same);
            expect(WavFile.Read(same) is { Rate: 48000, Channels: 2 } joined && joined.Samples.SequenceEqual(toneA.Concat(toneB)),
                "two segments of one format are joined sample for sample, in order");

            var mixed = Path.Combine(folder, "mixed.wav");
            WavFile.Join([a, c], mixed);
            var converted = WavFile.Read(mixed);
            var frames = converted is { } m ? m.Samples.Length / 2 : 0;
            var tail = converted is { } t ? t.Samples.AsSpan(toneA.Length) : default;
            var mirrored = tail.Length > 0;
            for (var i = 0; i + 1 < tail.Length; i += 2)
                mirrored &= tail[i] == tail[i + 1];
            expect(converted is { Rate: 48000, Channels: 2 } && Math.Abs(frames - (24000 + 24000)) <= 1 && mirrored,
                "a 44.1 kHz mono segment joined after a 48 kHz stereo one becomes 48 kHz stereo of its own length, the same in both channels");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ----- voiceover-wasapi-capture -----

    private static void Capture(Action<bool, string> expect)
    {
        var microphones = Task.Run(() => VoiceRecorder.ListMicrophonesAsync(CancellationToken.None)).GetAwaiter().GetResult();
        Console.WriteLine($"        microphones: {(microphones.Count == 0 ? "none" : string.Join(", ", microphones))}");
        expect(microphones.Distinct(StringComparer.Ordinal).Count() == microphones.Count, "the microphones are listed by name, each name once (a shared name numbered)");

        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-voice-{Guid.NewGuid():N}");
        var output = Path.Combine(folder, "take.wav");
        var outcome = true;
        try
        {
            if (microphones.Count == 0)
            {
                Console.WriteLine("        no microphone on this PC: nothing recorded");
            }
            else
            {
                var recorder = new VoiceRecorder();
                try
                {
                    Task.Run(() => recorder.StartAsync(microphones[0], folder)).GetAwaiter().GetResult();
                    Thread.Sleep(700);
                    Task.Run(recorder.PauseAsync).GetAwaiter().GetResult();
                    var paused = !recorder.IsRecording && recorder.IsSessionOpen;
                    Task.Run(() => recorder.StartAsync(microphones[0], folder)).GetAwaiter().GetResult();
                    Thread.Sleep(400);
                    var kept = Task.Run(() => recorder.StopAsync(output, CancellationToken.None)).GetAwaiter().GetResult();
                    var take = kept ? WavFile.Read(output) : null;
                    var seconds = take is { } got ? got.Samples.Length / (double)got.Channels / got.Rate : 0;
                    Console.WriteLine($"        recorded from \"{microphones[0]}\": {(take is { } info ? $"{info.Rate} Hz, {info.Channels} channel(s), {seconds:0.00} s" : "nothing")}");
                    outcome = paused && kept && seconds is > 0.6 and < 2.5 && !recorder.IsSessionOpen
                              && !Directory.EnumerateFiles(folder, "voiceover_part*.wav").Any();
                }
                catch (InvalidOperationException ex)
                {
                    // Blocked by Windows privacy settings, held exclusively or unplugged: the reason is the outcome.
                    Console.WriteLine($"        could not record: {ex.Message}");
                    outcome = ex.Message.Length > 0;
                }
            }

            expect(outcome, "two stretches from the first microphone become one 16-bit WAV of about their length, the segments gone; or the reason it cannot record is given");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ----- cutout-studio-preset-family -----

    // An older Overwatch preset: a full-frame bitmap mask (frame fractions per island) and each piece's crop.
    private static readonly (string Name, Rect Crop, Rect[] Islands)[] OldPieces =
    [
        ("Overwatch: Killfeed", new Rect(0.65637, 0.01094, 0.33784, 0.26083), [new Rect(0.72148, 0.07222, 0.26329, 0.1125), new Rect(0.90469, 0.02708, 0.0789, 0.03056)]),
        ("Overwatch: Health & Name", new Rect(0.00137, 0.77656, 0.25343, 0.20879), [new Rect(0.0102, 0.8153, 0.2226, 0.1472)]),
        ("Overwatch: Abilities & Ammo", new Rect(0.7278, 0.76598, 0.2722, 0.23402), [new Rect(0.7551, 0.8382, 0.2168, 0.0951)]),
    ];

    private static void PresetFamily(Action<bool, string> expect)
    {
        const int W = 2560, H = 1440;
        const string main = "D:/media/overwatch.mkv";
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-family-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var mask = Path.Combine(folder, "owlayermask.png");
        WriteMask(mask, 1280, 720);
        try
        {
            var vm = Opened(main, W, H);
            vm.AddCutouts([.. OldPieces.Select(p => new CutoutPiece(p.Name, Box(p.Crop), p.Crop, 0.1, 0.1, 0.5))], 1.5);
            var parent = vm.Layers.First(l => l.IsVideoFile && !vm.IsCutout(l));
            var children = vm.Layers.Where(vm.IsCutout).ToList();

            // As that preset stored them: no outlines, the frame's bitmap mask cut by each piece's crop.
            foreach (var child in children)
            {
                var crop = OldPieces.First(p => p.Name == child.Name).Crop;
                (child.MaskShapes, child.MaskPath, child.MaskFrame, child.CustomMask) = ("", mask, false, true);
                (child.SourceX, child.SourceY, child.SourceWidth, child.SourceHeight) = (0, 0, 1, 1);
                (child.CropLeft, child.CropTop, child.CropRight, child.CropBottom) = (crop.X, crop.Y, 1 - crop.Right, 1 - crop.Bottom);
            }

            expect(children.Count == 3 && !vm.CutoutLayers.Any(), "three Cutouts with a bitmap mask and no outlines (which the Studio once left out)");

            var base0 = vm.CutoutStudioBase(null);
            expect(ReferenceEquals(base0, parent) && ReferenceEquals(vm.CutoutStudioBase(children[1]), parent) && ReferenceEquals(vm.CutoutStudioBase(parent), parent),
                "nothing picked, a Cutout picked or its video picked: the Studio's base is the video");

            var family = vm.CutoutFamily(parent, orAnyClip: false);
            var traced = family.Select(f => (f.Piece.Name, Shapes: MaskStore.ParseShapes(f.Piece.Shapes))).ToList();
            var fits = OldPieces.All(p => traced.FirstOrDefault(t => t.Name == p.Name) is { Shapes: { } shapes } && shapes.Count == p.Islands.Length
                && p.Islands.All(island => shapes.Any(s => Math.Abs(s.Min(q => q.X) - island.Left) < 0.006 && Math.Abs(s.Max(q => q.X) - island.Right) < 0.006
                    && Math.Abs(s.Min(q => q.Y) - island.Top) < 0.01 && Math.Abs(s.Max(q => q.Y) - island.Bottom) < 0.01)));
            Console.WriteLine($"        traced: {string.Join("; ", traced.Select(t => $"{t.Name} {t.Shapes.Count} outline(s), {t.Shapes.Sum(s => s.Count)} points"))}");
            expect(family.Count == 3 && fits, "each piece opens with the bitmap's islands inside its crop traced to outlines (the Killfeed's two, one each for the others)");
            expect(MainViewModel.PresetNameOf([.. family.Select(f => f.Piece.Name)]) == "Overwatch" && MainViewModel.PresetNameOf(["Overwatch Killfeed", "Overwatch Health"]) == "Overwatch"
                   && MainViewModel.PresetNameOf(["Killfeed", "Minimap"]) is null, "the preset's name is the pieces' shared prefix (\"Overwatch:\" or a first word), none without one");

            var target = MaskDrawWindow.StillTarget(vm, null);
            expect(target is not null && target == MaskDrawWindow.StillTarget(vm, children[0]) && target == MaskDrawWindow.StillTarget(vm, parent),
                "the heat lamp's still target is the same whichever of them is picked");

            // The window, off screen: opened with nothing picked, a Cutout picked (its still prewarmed), and on a Cutout from its menu.
            var picture = BitmapSource.Create(16, 9, 96, 96, PixelFormats.Gray8, null, new byte[16 * 9], 16);
            picture.Freeze();
            var warm = new MaskDrawWindow.WarmStill(target!.Value.Source, target.Value.Seconds, picture);
            var backdrop = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Gray8, null, new byte[8 * 4], 8);
            backdrop.Freeze();
            foreach (var (name, layer, opening) in new (string, Layer?, MaskDrawWindow.Opening?)[]
                     {
                         ("nothing picked", null, new MaskDrawWindow.Opening(null, null, (_, _) => backdrop)),
                         ("a Cutout picked", null, new MaskDrawWindow.Opening(children[2], Task.FromResult<MaskDrawWindow.WarmStill?>(warm), (_, _) => backdrop)),
                         ("on a Cutout from its menu", children.First(c => c.Name == "Overwatch: Killfeed"), null),
                     })
            {
                var window = new MaskDrawWindow(vm, layer, opening) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
                var first = window.StillShown;
                try
                {
                    window.Show();
                    Pump();
                    var named = OldPieces.All(p => Find<TextBox>(window, t => t.Text == p.Name) is not null);
                    var preset = Find<TextBox>(window, t => t.Text == "Overwatch") is not null;
                    expect(named && preset, $"{name}: the three pieces under their names, and the preset's name filled in");
                    if (layer is null)
                    {
                        expect(ReferenceEquals(first, opening?.Warm is null ? backdrop : picture),
                            $"{name}: frame 0 shows {(opening?.Warm is null ? "the cached backdrop" : "the prewarmed still")}, not a black view");
                    }
                    else
                    {
                        // Apply to This Clip takes this Cutout's own outlines only, not its family's; Apply to All Cutouts takes all of them.
                        expect(MaskStore.ParseShapes(window.ShapesFor(forAll: false)).Count == 2 && MaskStore.ParseShapes(window.ShapesFor(forAll: true)).Count == 4,
                            $"{name}: Apply to This Clip keeps its own two outlines, not its siblings'; Apply to All Cutouts takes all four");
                    }
                }
                finally
                {
                    window.Close();
                    Pump();
                }
            }

            // Added again from the Studio, the bitmap-masked pieces are replaced, not doubled.
            vm.AddCutouts([.. family.Select(f => f.Piece)], 1.5);
            expect(vm.Layers.Count(vm.IsCutout) == 3 && vm.CutoutLayers.Count() == 3, "added to the timeline again, the three pieces take the place of the old ones");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Box(Rect r) => MaskStore.FormatShapes([[r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft]]);

    // A grey mask, white over each island of the old pieces, written as PNG.
    private static void WriteMask(string path, int width, int height)
    {
        var pixels = new byte[width * height];
        foreach (var island in OldPieces.SelectMany(p => p.Islands))
        {
            for (var y = (int)(island.Top * height); y < (int)(island.Bottom * height); y++)
            {
                for (var x = (int)(island.Left * width); x < (int)(island.Right * width); x++)
                    pixels[y * width + x] = 255;
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static MainViewModel Opened(string main, int width, int height)
    {
        var vm = new MainViewModel { SourceWidth = width, SourceHeight = height, LocalMediaPath = main };
        vm.SetMediaInfoForTests(new MediaInfo("matroska", 8, 0, new VideoStreamInfo(width, height, "hevc", 60, "16:9", "1:1", "bt709", "bt709", 0), [], []));
        var document = new TimelineDocument { Width = width, Height = height, NaturalWidth = width, NaturalHeight = height, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        var look = new LayerState { Kind = LayerKind.VideoFile, Name = "Main", SizeWidth = 1, ImagePath = main, ImageWidth = width, ImageHeight = height, MediaDuration = 8 };
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.VideoFile, SourcePath = main, Start = 0, End = 8, MediaOffset = 0, SourceDuration = 8, State = look });
        document.Tracks.Add(track);
        vm.InstallDocument(document);
        return vm;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static T? Find<T>(DependencyObject under, Func<T, bool> wanted) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(under); i++)
        {
            var child = VisualTreeHelper.GetChild(under, i);
            if (child is T found && wanted(found))
                return found;
            if (Find(child, wanted) is { } deeper)
                return deeper;
        }

        return null;
    }
}
