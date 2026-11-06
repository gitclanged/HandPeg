using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.2C's streamer tools on real view models and real files: the OBS side-by-side unpacker, a project bundle that carries a long
/// recording as the stretches it uses (lossless, every frame where it was), Ping-to-Mark highlights with their focal points, and the
/// Personal Clip Library. Every line says what it proved.
/// </summary>
public static class StreamerToolsTests
{
    private const int Rate = 30;

    public static IEnumerable<GraphResult> All()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.StreamerTools", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        try
        {
            yield return Case("obs-side-by-side-unpack", "# Unpack OBS Side-by-Side Canvas (5.2C): the gameplay half on the clip, the facecam half a Cutout above it, placed for 16:9 and 9:16, one step of Undo.", (e, _) => Unpack(e));
            yield return Case("vod-trim-bundle", "# A project bundle carries a long recording as the stretches it uses (5.2C, VodTrim): stream copies from key frames, +5 s handles, every frame where it was.", (e, t) => TrimmedBundle(folder, e, t));
            yield return Case("ping-to-mark", "# Ping-to-Mark (5.2C): Ctrl+J a highlight at the playhead, Alt + a click its focal point too, kept in the project file, one step of Undo.", (e, _) => Ping(e));
            yield return Case("clip-library", "# The Personal Clip Library (5.2C): a clip's span kept losslessly with its title, tags and focal point, searched, dropped back where it was saved, taken out.", (e, _) => Library(folder, e));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
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

    private static void Note(FormattableString line) => Console.WriteLine("        " + line.ToString(CultureInfo.InvariantCulture));

    // ----- The OBS side-by-side unpacker -----

    private static TimelineClip ObsClip(double start, double end)
    {
        var clip = DogfoodTests.FileClip("D:/media/obs canvas.mkv", start, end);
        (clip.State.ImageWidth, clip.State.ImageHeight) = (3840, 1080);
        return clip;
    }

    private static void Unpack(Action<bool, string> expect)
    {
        expect(ObsUnpacker.IsSideBySide(3840, 1080) && ObsUnpacker.IsSideBySide(5120, 1440) && !ObsUnpacker.IsSideBySide(2560, 1080) && !ObsUnpacker.IsSideBySide(1920, 1080),
            "3840 x 1080 and 5120 x 1440 are two pictures side by side; 21:9 and 16:9 are one");

        var vm = DogfoodTests.Open(DogfoodTests.Lane(ObsClip(0, 8)));
        var clip = vm.Document.Tracks[1].Clips[0];
        var view = vm.ViewOf(clip)!;
        var main = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        expect(vm.CanUnpackSideBySide(view) && !vm.CanUnpackSideBySide(main), "the double-wide clip can be unpacked, the 16:9 one cannot");

        expect(vm.UnpackSideBySide(view), "unpacked");
        var (fw, fh) = (vm.FrameWidth, vm.FrameHeight);
        expect((view.CropLeft, view.CropTop, view.CropRight, view.CropBottom) == (0, 0, 0.5, 0) && view.GetOutputRect(fw, fh, vm.SourceWidth, vm.SourceHeight) == (0, 0, 1920, 1080),
            "16:9: the clip shows the left half (cropped right 50 %) and fills the 1920 x 1080 frame");
        var children = vm.Document.ChildrenOf(clip).ToList();
        var cam = children.Count == 1 ? children[0] : default;
        expect(cam.Clip is { ContentKind: LayerKind.VideoFile, AudioStreamIndex: -1 } c && c.SourcePath == clip.SourcePath && c.MediaOffset == clip.MediaOffset && c.Start == clip.Start && c.End == clip.End,
            "a Cutout of the same file on the same clock over the clip's span, silent: the facecam");
        expect(cam.Track is not null && vm.Document.Tracks.IndexOf(cam.Track) > vm.Document.Tracks.IndexOf(vm.Document.Tracks[1]) && cam.Track.ParentTrackId == vm.Document.Tracks[1].Id,
            "on a Sub-Lane above the clip's track");
        var face = cam.Clip is null ? null : vm.ViewOf(cam.Clip);
        var faceRect = face?.GetOutputRect(fw, fh, vm.SourceWidth, vm.SourceHeight);
        expect(face is { CropLeft: 0.5, CropRight: 0, CropTop: 0, CropBottom: 0, Name: MainViewModel.FacecamName } && faceRect == (1296, 708, 576, 324),
            "the facecam shows the right half (cropped left 50 %) whole, 30 % of the frame wide in the bottom right corner (48 px in)");

        vm.SplitLayer(view, 4);
        var halves = vm.Document.Tracks[1].Clips;
        expect(halves.Count == 2 && halves.All(h => vm.Document.ChildrenOf(h).Count() == 1)
               && vm.Document.ChildrenOf(halves[1]).Single().Clip.MediaOffset == halves[1].MediaOffset,
            "a split of the clip splits the facecam with it, each half on its half's clock");
        vm.Undo();
        vm.Undo();
        var back = vm.Document.Tracks[1].Clips;
        expect(back.Count == 1 && !vm.Document.ChildrenOf(back[0]).Any() && vm.ViewOf(back[0]) is { CropRight: 0 } && vm.Document.Tracks.Count == 2,
            "two steps of Undo (the split, then the unpack) leave the clip as it was, no facecam, no Sub-Lane");

        // 9:16: the facecam a band across the top, the gameplay the rest, each cut at its sides, nothing stretched.
        var tall = DogfoodTests.Open(DogfoodTests.Lane(ObsClip(0, 8)));
        tall.UseVerticalResolution = true;
        var tallView = tall.ViewOf(tall.Document.Tracks[1].Clips[0])!;
        expect(tall.UnpackSideBySide(tallView), "unpacked on a 9:16 Sequence");
        var (tw, th) = (tall.FrameWidth, tall.FrameHeight);
        var game = tallView.GetOutputRect(tw, th, tall.SourceWidth, tall.SourceHeight);
        var band = tall.ViewOf(tall.Document.ChildrenOf(tall.Document.Tracks[1].Clips[0]).Single().Clip)!.GetOutputRect(tw, th, tall.SourceWidth, tall.SourceHeight);
        Note($"unpack 9:16: facecam {band}, gameplay {game}");
        expect((tw, th) == (1080, 1920) && band.X == 0 && band.Y == 0 && band.Width == 1080 && Math.Abs(band.Height - 608) <= 2,
            "9:16: the facecam across the top, the frame's width and its own 16:9 shape (1080 x 608)");
        expect(game.X == 0 && game.Width == 1080 && Math.Abs(game.Y - band.Height) <= 2 && Math.Abs(game.Y + game.Height - 1920) <= 2
               && tallView.CropLeft > 0.1 && Math.Abs(tallView.CropLeft + (1 - tallView.CropRight) - 0.5) < 1e-6,
            "the gameplay fills the rest of the frame, cut at its sides about its half's middle");

        // A Sequence that took the canvas's own shape (the first file opened) takes one half's.
        var wide = DogfoodTests.Open(DogfoodTests.Lane(ObsClip(0, 8)));
        (wide.Document.NaturalWidth, wide.Document.NaturalHeight) = (3840, 1080);
        expect(wide.UnpackSideBySide(wide.ViewOf(wide.Document.Tracks[1].Clips[0])!) && (wide.FrameWidth, wide.FrameHeight) == (1920, 1080),
            "a Sequence shaped as the double-wide canvas becomes 1920 x 1080");

        var big = ObsUnpacker.Layout(5120, 1440, 1920, 1080);
        expect(big.Gameplay == new UnpackedPart(0, 0, 0.5, 0, 0, 0, 1) && big.Facecam.CropLeft == 0.5, "5120 x 1440 lays out as 3840 x 1080 does");
    }

    // ----- A bundle with a long recording trimmed -----

    private static string? Ffmpeg() => GpuExportTests.FfmpegCandidates().FirstOrDefault(File.Exists);

    private static void RunTool(string tool, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(tool)} failed: {errors.Result}");
    }

    // The gray picture of a video's frame number n (decode order aside: the n-th frame shown), by FFmpeg.
    private static byte[] FrameOf(string ffmpeg, string file, int n)
    {
        var start = new ProcessStartInfo(ffmpeg, string.Create(CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -i \"{file}\" -map 0:v:0 -vf \"select=eq(n\\,{n})\" -fps_mode passthrough -frames:v 1 -f rawvideo -pix_fmt gray -"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        using var bytes = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        process.WaitForExit();
        return bytes.ToArray();
    }

    private static TimelineClip VodClip(string path, double start, double end, double offset) => new()
    {
        ContentKind = LayerKind.VideoFile, SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, MediaOffset = offset, SourceDuration = 150,
        State = new LayerState { Kind = LayerKind.VideoFile, Name = Path.GetFileName(path), ImagePath = path, ImageWidth = 320, ImageHeight = 180, MediaDuration = 150, SizeWidth = 1, HasAudio = true },
    };

    private static void TrimmedBundle(string folder, Action<bool, string> expect, StringBuilder text)
    {
        // The plan, by the clips alone: a 150 s file, two clips 10 s and 8 s long far apart, a third file used whole, a short one.
        var document = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 };
        var vod = Path.Combine(folder, "vod.mkv");
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        track.Clips.Add(VodClip(vod, 0, 10, 30));
        track.Clips.Add(VodClip(vod, 10, 18, 100));
        document.Tracks.Add(track);
        var used = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 2 };
        var whole = VodClip(Path.Combine(folder, "used.mkv"), 0, 130, 0);
        used.Clips.Add(whole);
        var shortClip = VodClip(Path.Combine(folder, "short.mkv"), 0, 5, 10);
        shortClip.SourceDuration = 90;
        used.Clips.Add(shortClip);
        document.Tracks.Add(used);
        var state = new ProjectState { Name = "Vod", Timeline = document, SourcePath = vod, LocalMediaPath = vod };
        var plan = VodTrim.Plan(state);
        var planned = plan.FirstOrDefault(p => p.Path.EndsWith("vod.mkv", StringComparison.OrdinalIgnoreCase));
        expect(plan.Count == 1 && planned is not null, "only the long recording used little is planned (not the one 87 % used, not the 90 s one)");
        expect(planned is { Stretches: [{ From: 25, To: 45 }, { From: 95, To: 113 }] } && Math.Abs(planned.UsedShare - 18 / 150.0) < 1e-9,
            "its two stretches with 5 s handles each side (25-45 s, 95-113 s), 12 % of it used");

        // Joined when near: clips 20 s apart are one stretch.
        var near = new ProjectState { Timeline = new TimelineDocument() };
        var nearTrack = new TimelineTrackModel { Kind = TimelineTrackKind.Video };
        nearTrack.Clips.Add(VodClip(vod, 0, 5, 10));
        nearTrack.Clips.Add(VodClip(vod, 5, 10, 35));
        near.Timeline.Tracks.Add(nearTrack);
        expect(VodTrim.Plan(near) is [{ Stretches: [{ From: 5, To: 45 }] }], "stretches within 30 s of each other are one (5-45 s)");

        // The remap alone: each clip on the piece that holds it, its offset moved by the piece's start; the working state untouched.
        var pieces = new Dictionary<string, IReadOnlyList<VodTrim.Piece>>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(vod)] = [new VodTrim.Piece(Path.Combine(folder, "a.mkv"), 24.5, 20.5), new VodTrim.Piece(Path.Combine(folder, "b.mkv"), 94, 19)],
        };
        var (remapped, trimmedFiles) = VodTrim.Remap(state, pieces);
        var (first, second) = (remapped.Timeline.Tracks[0].Clips[0], remapped.Timeline.Tracks[0].Clips[1]);
        expect(first.SourcePath!.EndsWith("a.mkv", StringComparison.Ordinal) && Math.Abs(first.MediaOffset - 5.5) < 1e-9 && first.SourceDuration == 20.5 && first.State.ImagePath == first.SourcePath
               && second.SourcePath!.EndsWith("b.mkv", StringComparison.Ordinal) && Math.Abs(second.MediaOffset - 6) < 1e-9,
            "remapped: the first clip on the first piece at 5.5 s of it, the second on the second at 6 s");
        expect(remapped.LocalMediaPath.EndsWith("a.mkv", StringComparison.Ordinal) && state.Timeline.Tracks[0].Clips[0].SourcePath == vod && state.LocalMediaPath == vod
               && trimmedFiles.Count == 1,
            "the project's own name of the file names its first piece; the state given is not changed");
        var short_ = new Dictionary<string, IReadOnlyList<VodTrim.Piece>>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(vod)] = [new VodTrim.Piece(Path.Combine(folder, "a.mkv"), 24.5, 20.5), new VodTrim.Piece(Path.Combine(folder, "b.mkv"), 94, 12)],
        };
        var (untouched, none) = VodTrim.Remap(state, short_);
        expect(none.Count == 0 && untouched.Timeline.Tracks[0].Clips.All(c => c.SourcePath == vod && c.MediaOffset is 30 or 100),
            "a file one clip of which no piece holds whole is not trimmed at all: every clip of it as it was");

        // For real: a 150 s recording with B-frames (key frames every 2 s), two clips of it, bundled with and without trimming.
        if (Ffmpeg() is not { } ffmpeg)
        {
            Console.WriteLine("  note  no FFmpeg found: the stream copies of vod-trim-bundle were not made");
            text.Append("(no FFmpeg: the stream copies were not made)\n");
            return;
        }

        var configured = AppSettings.Current.FfmpegPath;
        AppSettings.Current.FfmpegPath = ffmpeg;
        try
        {
            RunTool(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=s=320x180:r={Rate}:d=150 -f lavfi -i sine=f=440:d=150 -c:v libx264 -preset ultrafast -g 60 -bf 2 -c:a aac -b:a 64k \"{vod}\"");
            var only = new ProjectState { Name = "Vod", Timeline = new TimelineDocument { Width = 1920, Height = 1080, FrameRate = FrameRate.Fps30 }, SourcePath = vod, LocalMediaPath = vod };
            var onlyTrack = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
            onlyTrack.Clips.Add(VodClip(vod, 0, 10, 30.5));
            onlyTrack.Clips.Add(VodClip(vod, 10, 18, 101.2));
            only.Timeline.Tracks.Add(onlyTrack);

            var bundle = Path.Combine(folder, "Vod.hpb");
            var watch = Stopwatch.StartNew();
            var (files, missing, trimmed) = ProjectBundle.Pack(only, bundle, trimLongVods: true);
            Note($"vod trim: packed in {watch.ElapsedMilliseconds} ms, {new FileInfo(bundle).Length / 1024} KB against the recording's {new FileInfo(vod).Length / 1024} KB");
            expect(files == 2 && missing == 0 && trimmed == 1, "packed: two pieces of the one recording, nothing missing");
            using (var archive = ZipFile.OpenRead(bundle))
            {
                var media = archive.Entries.Where(e => e.FullName.StartsWith("media/", StringComparison.Ordinal)).Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
                text.Append("entries: ").Append(string.Join(", ", media)).Append('\n');
                expect(media.Count == 2 && !media.Contains("media/vod.mkv"), "the bundle holds the two stretches and not the whole recording");
            }

            expect(!Directory.Exists(bundle + ".trim"), "the stretches cut beside the bundle are gone after it is written");
            expect(only.Timeline.Tracks[0].Clips[0].SourcePath == vod, "the working project still names the whole recording");

            var opened = ProjectStore.Load(ProjectBundle.Unpack(bundle));
            var clips = opened.Timeline.Tracks[0].Clips;
            var worst = 0;
            for (var k = 0; k < clips.Count; k++)
            {
                var (was, now) = (onlyTrack.Clips[k], clips[k]);
                foreach (var into in new[] { 0.0, 1.0, was.Duration - 1.0 / Rate })
                {
                    var n = (int)Math.Round((was.MediaOffset + into) * Rate);
                    var m = (int)Math.Round((now.MediaOffset + into) * Rate);
                    var (original, piece) = (FrameOf(ffmpeg, vod, n), FrameOf(ffmpeg, now.SourcePath!, m));
                    if (original.Length == 0 || !original.AsSpan().SequenceEqual(piece))
                        worst++;
                }
            }

            Note($"vod trim: offsets {string.Join(", ", clips.Select(c => c.MediaOffset.ToString("0.###", CultureInfo.InvariantCulture)))}, lengths {string.Join(", ", clips.Select(c => c.SourceDuration.ToString("0.###", CultureInfo.InvariantCulture)))}");
            expect(worst == 0 && clips.All(c => c.MediaOffset is > 4.9 and < 7.1), "unpacked, each clip's first, second and last frames are the recording's very frames (each piece starts on the key frame at or before its stretch)");

            // Off: the recording goes in whole.
            var plain = Path.Combine(folder, "Plain.hpb");
            var (plainFiles, _, plainTrimmed) = ProjectBundle.Pack(only, plain, trimLongVods: false);
            using (var archive = ZipFile.OpenRead(plain))
                expect(plainFiles == 1 && plainTrimmed == 0 && archive.GetEntry("media/vod.mkv") is not null, "with the setting off the recording goes in whole");
            expect(new AppSettings().TrimLongVodsOnBundle, "the setting is on as it comes");
        }
        finally
        {
            AppSettings.Current.FfmpegPath = configured;
        }
    }

    // ----- Ping-to-Mark -----

    private static void Ping(Action<bool, string> expect)
    {
        expect(KeybindManager.Action("marker.ping") is { } action && action.Defaults.Contains("Ctrl+J"), "Ping-to-Mark Highlight is on Ctrl+J");
        var vm = DogfoodTests.Open();
        vm.PositionMs = 2000;
        var plain = vm.PingToMark();
        expect(plain is { Seconds: 2, Highlight: true, FocalX: null, Name: "Highlight 1" }, "Ctrl+J: a highlight at the playhead (2 s), no focal point");
        vm.PositionMs = 5500;
        var aimed = vm.PingToMark(0.25, 0.75);
        expect(aimed is { Seconds: 5.5, Highlight: true, FocalX: 0.25, FocalY: 0.75, Name: "Highlight 2" }, "Alt + a click: a highlight with where on the picture it was clicked");
        vm.PositionMs = 2000;
        var again = vm.PingToMark(1.5, -1);
        expect(again is { Seconds: 2, FocalX: 1, FocalY: 0 } && vm.Markers.Count == 2, "pinged again at a marker: that marker takes the focal point (held to the picture), no second one");

        var marked = vm.AddMarker(8);
        expect(marked is { Highlight: false, FocalX: null }, "a plain marker (M) is no highlight");
        vm.Undo();
        vm.Undo();
        expect(vm.Markers.Count == 2 && vm.Markers[0] is { FocalX: null }, "Undo takes back the plain marker, then the second ping's focal point");

        // Kept in the project file, left out of it when there are none.
        var json = ProjectStore.Serialize(new ProjectState { Timeline = new TimelineDocument { Markers = [aimed!, new TimelineMarker(9, "Plain")] } });
        var read = ProjectStore.Deserialize(json).Timeline.Markers;
        expect(read is [{ Highlight: true, FocalX: 0.25, FocalY: 0.75 }, { Highlight: false, FocalX: null }], "a highlight and its focal point come back from the project file");
        expect(!ProjectStore.Serialize(new ProjectState { Timeline = new TimelineDocument { Markers = [new TimelineMarker(9, "Plain")] } }).Contains("focal", StringComparison.OrdinalIgnoreCase),
            "a plain marker writes no focal point and no highlight");
    }

    // ----- The Personal Clip Library -----

    private static void Library(string folder, Action<bool, string> expect)
    {
        var kept = ClipLibrary.Folder;
        ClipLibrary.Folder = Path.Combine(folder, "Clips");
        var configured = AppSettings.Current.FfmpegPath;
        try
        {
            expect(ClipLibrary.Load().Count == 0, "an empty library lists nothing");
            var entry = new ClipLibrary.Entry { Title = "Clutch Win", Tags = ["win", "Ranked"] };
            expect(entry.Matches("") && entry.Matches("clutch") && entry.Matches("ranked win") && !entry.Matches("fail") && !entry.Matches("win fail"),
                "search: every word in the title or a tag, case aside");

            var vm = DogfoodTests.Open();
            var main = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
            expect(vm.CanSaveToClipLibrary(main), "a video clip can go into the library");
            vm.PositionMs = 3000;
            vm.PingToMark(0.6, 0.4);
            expect(vm.FocalIn(main) == (0.6, 0.4), "its focal point is the highlight's inside its span");

            if (Ffmpeg() is not { } ffmpeg)
            {
                Console.WriteLine("  note  no FFmpeg found: the Clip Library's copies were not made");
                return;
            }

            AppSettings.Current.FfmpegPath = ffmpeg;
            var source = Path.Combine(folder, "match.mkv");
            RunTool(ffmpeg, $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=s=320x180:r={Rate}:d=40 -f lavfi -i sine=f=330:d=40 -c:v libx264 -preset ultrafast -g 60 -bf 2 -c:a aac -b:a 64k \"{source}\"");
            var saved = ClipLibrary.Save(source, hasPicture: true, isStill: false, 13.3, 19.3, "Clutch: Win?", ["win", " ranked ", "WIN"], 0.6, 0.4);
            expect(saved is { Title: "Clutch: Win?", Tags: ["win", "ranked"], FocalX: 0.6, FocalY: 0.4, From: "match.mkv" } && File.Exists(saved.Path) && saved.File == "Clutch_ Win_.mkv",
                "saved: its file (the title made fit for a name), its title, its tags (trimmed, once each), its focal point, the file it came from");
            expect(saved is { In: > 1.2 and < 1.4, Duration: > 5.99 and < 6.01 } && Math.Abs(saved.Out - saved.In - 6) < 0.01,
                "the copy starts on the key frame at 12 s, the clip 1.3 s into it, 6 s long (In / Out)");
            if (saved is not null)
            {
                var n = (int)Math.Round(13.3 * Rate);
                var m = (int)Math.Round(saved.In * Rate);
                expect(FrameOf(ffmpeg, source, n).AsSpan().SequenceEqual(FrameOf(ffmpeg, saved.Path, m)), "the frame at its In is the source's frame at 13.3 s (lossless)");
            }

            // Dropped back from the library: the clip starts at its In and lasts its length, not from the key frame the copy begins on.
            if (saved is not null)
            {
                var dropped = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(saved.Path, 0, 7.3)));
                var placed = dropped.Document.Tracks[1].Clips[0];
                typeof(MainViewModel).GetMethod("FitToLibraryEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(dropped, [dropped.ViewOf(placed)!]);
                var now = dropped.Document.FindClip(placed.Id)!;
                expect(Math.Abs(now.MediaOffset - saved.In) < 1e-6 && Math.Abs(now.Duration - (saved.Out - saved.In)) < 1e-6 && now.Start == 0,
                    "a clip dropped from the library starts at its In and lasts Out - In, where it was dropped");
            }

            var listed = ClipLibrary.Load();
            expect(listed.Count == 1 && ClipLibrary.Find(saved!.Path)?.Id == saved.Id && ClipLibrary.Find(source) is null, "listed, and found by its file (a file elsewhere is not the library's)");
            var second = ClipLibrary.Save(source, true, false, 2, 4, "Clutch: Win?", [], null, null);
            expect(second is { File: "Clutch_ Win_ (2).mkv", FocalX: null } && ClipLibrary.Load().Count == 2, "a second clip of the same title gets a file of its own");

            expect(ClipLibrary.Remove(second!) && !File.Exists(second!.Path) && ClipLibrary.Load().Count == 1, "taken out: its line and its file");

            // A ledger this version cannot read is kept aside, never written over.
            File.WriteAllText(ClipLibrary.LedgerPath, "{ \"format\": \"handpeg-clip-ledger\", \"version\": 99, \"clips\": [] }");
            expect(ClipLibrary.Load().Count == 0 && Directory.GetFiles(ClipLibrary.Folder, ClipLibrary.LedgerName + ".unread-*").Length == 1, "a newer ledger is kept aside and the library starts again");
        }
        finally
        {
            (ClipLibrary.Folder, AppSettings.Current.FfmpegPath) = (kept, configured);
        }
    }
}
