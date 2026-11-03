using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// The user's dogfooding list (5.1-DogfoodPolish), on a real view model: a mask is one clip's alone, a template belongs to its base clip, a
/// Game Preset goes on any clip, a clip landing on a template's track takes it, clips dropped together follow in line with it; the frame
/// cache's share of Windows' commit; hardware encoders start at their best step. Every line says what it proved.
/// </summary>
public static class DogfoodTests
{
    private const int W = 1920, H = 1080;
    private const string Main = "D:/media/game.mkv", Second = "D:/media/second.mkv", Third = "D:/media/third.mkv";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    // A piece wider than a crop can leave (the hero), one narrower than a crop's least remainder of 5 % (an icon).
    private const string HeroShape = "0.02,0.8 0.2,0.8 0.2,0.95 0.02,0.95", IconShape = "0.5,0.5 0.53,0.5 0.53,0.54 0.5,0.54";
    private static readonly List<CutoutPiece> Pieces =
    [
        new("Hero", HeroShape, new Rect(0.02, 0.8, 0.18, 0.15), 0.05, 0.75, 0.5),
        new("Icon", IconShape, new Rect(0.5, 0.5, 0.03, 0.04), 0.4, 0.05, 0.2),
    ];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("clip-instance-masks", "# A mask, a matte and an effect are one clip's alone: never its track's, never its file's.", ClipMasks);
        yield return Case("game-preset-any-clip", "# A Game Preset on the 2nd or Nth clip: its pieces are that clip's children, over its span, on its file's clock.", PresetOnAnyClip);
        yield return Case("template-inheritance", "# A clip landing on a template's track takes it: its base look and place, and pieces of its own.", Inheritance);
        yield return Case("preset-sequential-drop", "# Clips dropped together follow one another in line, each with the template.", SequentialDrop);
        yield return Case("frame-cache-commit", "# The frame cache leaves Windows' commit a margin: the crash of 2026-11-01, by its own numbers.", CommitRoom);
        yield return Case("encoder-best-steps", "# Hardware encoders start at their slowest, best step; a choice made stands.", EncoderSteps);
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

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static LayerState FileLook(string name, string path) => new()
    {
        Kind = LayerKind.VideoFile, Name = name, ImagePath = path, ImageWidth = W, ImageHeight = H, MediaDuration = 60, SizeWidth = 1,
    };

    internal static TimelineClip FileClip(string path, double start, double end, double offset = 0) => new()
    {
        ContentKind = LayerKind.VideoFile, SourcePath = path, Start = start, End = end, MediaOffset = offset, SourceDuration = 60, State = FileLook(Path.GetFileName(path), path),
    };

    /// <summary>A view model holding a main video from 0 to 8 s and, on tracks of their own or the same one, clips of files: nothing is probed or decoded.</summary>
    internal static MainViewModel Open(params TimelineTrackModel[] more)
    {
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations, settings.DroppedClipsJoinTrack) = (false, false, false, false);
        var vm = new MainViewModel { SourceWidth = W, SourceHeight = H, LocalMediaPath = Main, FrameEngine = true };
        var probed = new MediaInfo("matroska", 8, 0, new VideoStreamInfo(W, H, "h264", 60, "16:9", "1:1", "bt709", "bt709", 0), [], []);
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, probed);
        var document = new TimelineDocument { Width = W, Height = H, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        var look = new LayerState { Kind = LayerKind.MainVideo, Name = "Main", SizeWidth = 1, SourceWidth = 0.25, SourceHeight = 0.25, ImagePath = Main, ImageWidth = W, ImageHeight = H, MediaDuration = 8 };
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.MainVideo, SourcePath = Main, Start = 0, End = 8, SourceDuration = 8, State = look });
        document.Tracks.Add(track);
        var number = 2;
        foreach (var other in more)
        {
            other.TrackNumber = number++;
            document.Tracks.Add(other);
        }

        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        return vm;
    }

    internal static TimelineTrackModel Lane(params TimelineClip[] clips)
    {
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video };
        track.Clips.AddRange(clips);
        return track;
    }

    private static void ClipMasks(Action<bool, string> expect, StringBuilder text)
    {
        // Two copies of one file on one track, and a copy on another.
        var vm = Open(Lane(FileClip(Second, 0, 4), FileClip(Second, 4, 8, 4)), Lane(FileClip(Second, 0, 8)));
        var clips = vm.Document.Tracks.Skip(1).SelectMany(t => t.Clips).Select(c => vm.ViewOf(c)!).ToList();
        var (masked, beside, elsewhere) = (clips[0], clips[1], clips[2]);

        (masked.CustomMask, masked.MaskPath, masked.MaskShapes) = (true, "D:/masks/a.png", HeroShape);
        (masked.MaskInvert, masked.MaskGrow, masked.MaskOffsetX, masked.MaskOffsetY) = (true, 3, 2, 4);
        (masked.ChromaKey, masked.ChromaColor) = (true, "#0000FF");
        (masked.Feather, masked.CornerRadius, masked.Opacity) = (true, 12, 50);
        foreach (var other in new[] { beside, elsewhere })
        {
            var which = ReferenceEquals(other, beside) ? "the next clip on its track" : "a copy of its file on another track";
            expect(!other.CustomMask && other.MaskPath.Length == 0 && other.MaskShapes.Length == 0, $"a mask on a clip: {which} has none");
            expect(!other.MaskInvert && other.MaskGrow == 0 && other.MaskOffsetX == 0 && other.MaskOffsetY == 0, $"its invert, grow and offset: nor {which}");
            expect(!other.ChromaKey && !other.Feather && other.CornerRadius == 0 && other.Opacity == 100, $"its key, edges and opacity: nor {which}");
        }

        typeof(MainViewModel).GetMethod("FlushLooks", Hidden)!.Invoke(vm, null);
        expect(vm.ClipOf(masked)!.State.CustomMask, "the look written into its clip has the mask");
        expect(!vm.ClipOf(beside)!.State.CustomMask && !vm.ClipOf(elsewhere)!.State.CustomMask, "the other clips' looks do not");

        beside.ResetMask();
        expect(masked.CustomMask, "taking something off the next clip leaves this one's");
    }

    private static string Preset(MainViewModel vm)
    {
        const string name = "Dogfood Preset";
        if (vm.SaveCutoutPreset(name, Pieces, 1.7) is { } why)
            throw new InvalidOperationException(why);
        return name;
    }

    private static List<TimelineClip> Children(MainViewModel vm, TimelineClip parent) =>
        vm.Document.ChildrenOf(parent).Where(c => c.Track.Kind == TimelineTrackKind.Video).Select(c => c.Clip).ToList();

    private static void PresetOnAnyClip(Action<bool, string> expect, StringBuilder text)
    {
        // The main video, then two clips of other files on a track of their own.
        var vm = Open(Lane(FileClip(Second, 8, 12, 3), FileClip(Third, 12, 20)));
        var name = Preset(vm);
        var main = vm.Document.Tracks[0].Clips[0];
        var file = vm.Document.Tracks[1].Clips[0];
        var third = vm.Document.Tracks[1].Clips[1];
        var (fileId, thirdId) = (file.Id, third.Id);
        var fileView = vm.ViewOf(file)!;
        var tracksBefore = vm.Document.Tracks.Count;
        var undoBefore = vm.Layers.Count(l => l.ClipId != Guid.Empty);

        expect(vm.ApplyGamePreset([fileView], name), "a preset goes on the 2nd clip");
        file = vm.Document.FindClip(fileId)!;
        var pieces = Children(vm, file);
        expect(pieces.Count == 2, "it has a piece for each of the preset's");
        expect(pieces.All(p => p.Start == file.Start && p.End == file.End), "each over that clip's span, 8 to 12 s");
        expect(pieces.All(p => p.ParentClipId == file.Id), "each a child of that clip");
        expect(Children(vm, main).Count == 0 && Children(vm, vm.Document.FindClip(thirdId)!).Count == 0, "no other clip took any");
        expect(pieces.All(p => p.ContentKind == LayerKind.VideoFile && p.SourcePath == Second), "cut from that clip's own file, not the main video's");
        expect(pieces.All(p => p.MediaOffset == file.MediaOffset && p.Speed == file.Speed), "on that clip's clock");
        expect(pieces.All(p => p.AudioStreamIndex < 0), "and silent: nothing is heard twice");
        expect(pieces.All(p => vm.Document.TrackOf(p)!.ParentTrackId == vm.Document.TrackOf(file)!.Id), "on tracks that are children of its track");
        expect(pieces.All(p => vm.Document.Tracks.IndexOf(vm.Document.TrackOf(p)!) > vm.Document.Tracks.IndexOf(vm.Document.TrackOf(file)!)), "and over it");

        // The picture: what a piece of the main video shows is what the file's piece shows, in the same box.
        var hero = pieces.First(p => p.State.Name == "Hero");
        var heroView = vm.ViewOf(hero)!;
        var (left, top, right, bottom) = heroView.CropFractions;
        const double Near = 1e-6;
        expect(Math.Abs(left - 0.02) < Near && Math.Abs(top - 0.8) < Near && Math.Abs(1 - left - right - 0.18) < Near && Math.Abs(1 - top - bottom - 0.15) < Near,
            "the hero is cut to its part of the picture by its crop");
        var heroBox = heroView.GetOutputRect(vm.FrameWidth, vm.FrameHeight, W, H);
        var wantedWidth = (int)Math.Round(0.5 * vm.FrameWidth / 2) * 2;
        expect(Math.Abs(heroBox.Width - wantedWidth) <= 2 && Math.Abs(heroBox.X - 0.05 * vm.FrameWidth) <= 1, "in the box the preset laid it out in");
        expect(heroView is { CustomMask: true, UsesFrameMask: false } && heroView.MaskPath.Length > 0, "shaped by its mask, cut as its picture is");

        // A piece narrower than a crop's least remainder: cut wider, its box as much larger, the same part of the picture in the same place.
        var icon = vm.ViewOf(pieces.First(p => p.State.Name == "Icon"))!;
        var (iconLeft, iconTop, iconRight, iconBottom) = icon.CropFractions;
        var (cutWidth, cutHeight) = (1 - iconLeft - iconRight, 1 - iconTop - iconBottom);
        expect(cutWidth >= 1 - Layer.LargestCropTotal - Near && cutHeight >= 1 - Layer.LargestCropTotal - Near, "an icon of 3 % keeps a cut a crop can hold");
        var iconBox = icon.GetOutputRect(vm.FrameWidth, vm.FrameHeight, W, H);
        var perSource = iconBox.Width / cutWidth;
        var drawnLeft = iconBox.X + (0.5 - iconLeft) * perSource;
        var drawnWidth = 0.03 * perSource;
        var wantedIcon = (int)Math.Round(0.2 * vm.FrameWidth / 2) * 2;
        expect(Math.Abs(drawnWidth - wantedIcon) <= 3 && Math.Abs(drawnLeft - 0.4 * vm.FrameWidth) <= 3, "and its part of the picture lands where the preset put it, as large");

        // Again on the same clip: one template to a clip.
        vm.ApplyGamePreset([vm.ViewOf(vm.Document.FindClip(fileId)!)!], name);
        expect(Children(vm, vm.Document.FindClip(fileId)!).Count == 2, "applied again it takes the place of the first");

        // The Nth clip: the same lanes when they have room, so a template's stack stays one stack.
        var lanes = vm.Document.ChildTracksOf(vm.Document.TrackOf(vm.Document.FindClip(fileId)!)!).Count();
        vm.ApplyGamePreset([vm.ViewOf(vm.Document.FindClip(thirdId)!)!], name);
        var thirdPieces = Children(vm, vm.Document.FindClip(thirdId)!);
        expect(thirdPieces.Count == 2 && thirdPieces.All(p => p.Start == 12 && p.End == 20 && p.SourcePath == Third), "the 3rd clip has its own, over its span, from its own file");
        expect(vm.Document.ChildTracksOf(vm.Document.TrackOf(vm.Document.FindClip(thirdId)!)!).Count() == lanes, "on the lanes the 2nd clip's lie on");
        expect(vm.Document.FindProblems().Count == 0, "the document holds together");

        // Its pieces go with it.
        vm.SplitLayer(vm.ViewOf(vm.Document.FindClip(fileId)!)!, 10);
        file = vm.Document.FindClip(fileId)!;
        expect(Children(vm, file).All(p => p.End <= file.End + 1e-6) && Children(vm, file).Count == 2, "a split of the clip splits its pieces");
        var after = vm.Document.Tracks[1].Clips.First(c => Math.Abs(c.Start - 10) < 1e-6);
        expect(Children(vm, after).Count == 2 && Children(vm, after).All(p => Math.Abs(p.MediaOffset - after.MediaOffset) < 1e-6), "the second half's are its children, at its moment of the file");
        vm.DeleteClip(vm.ViewOf(after)!);
        expect(vm.Document.Tracks.SelectMany(t => t.Clips).All(c => c.ParentClipId != after.Id), "and a clip deleted takes them with it");
        expect(vm.Document.FindProblems().Count == 0, "the document holds together after all of it");

        // One step of Undo each: back to where it began.
        for (var i = 0; i < 6 && vm.Layers.Count(l => l.ClipId != Guid.Empty) != undoBefore; i++)
            vm.Undo();
        expect(vm.Layers.Count(l => l.ClipId != Guid.Empty) == undoBefore && vm.Document.Tracks.Count == tracksBefore, "and Undo takes every piece and lane away again");

        // The main video, cut in two: a whole-project style gives each clip its own set.
        var cut = Open();
        cut.SplitLayer(cut.ViewOf(cut.Document.Tracks[0].Clips[0])!, 3);
        var presetName = Preset(cut);
        var style = cut.ReadStyle(Path.Combine(AppPaths.Styles, presetName + StyleFile.Extension))!;
        cut.ApplyStyle(style, layout: true, color: false, blur: true, subtitles: false);
        var mains = cut.Document.Tracks.SelectMany(t => t.Clips).Where(c => c.ContentKind == LayerKind.MainVideo).ToList();
        expect(mains.Count == 2 && mains.All(m => Children(cut, m).Count == 2), "a style on a video already cut: a set of pieces for each clip");
        expect(mains.All(m => Children(cut, m).All(p => p.Start == m.Start && p.End == m.End && p.ContentKind == LayerKind.Video)), "each set over its clip's span, cut from the main video");
    }

    private static void Inheritance(Action<bool, string> expect, StringBuilder text)
    {
        // A style on the main video, and a clip on a track of its own, placed and masked as nothing else is.
        var vm = Open(Lane(FileClip(Second, 8, 12)));
        var name = Preset(vm);
        var main = vm.Document.Tracks.SelectMany(t => t.Clips).First(c => c.ContentKind == LayerKind.MainVideo);
        var mainView = vm.ViewOf(main)!;
        expect(vm.ApplyGamePreset([mainView], name), "the preset goes on the main video's clip");
        main = vm.ClipOf(mainView)!;
        expect(Children(vm, main).Count == 2, "which has the preset's pieces");

        var file = vm.Document.Tracks.SelectMany(t => t.Clips).First(c => c.SourcePath == Second);
        var fileId = file.Id;
        var view = vm.ViewOf(file)!;
        expect(vm.MoveClipToTrack(view, mainView, 8), "a clip dragged onto the template's track lands after the base clip");
        file = vm.Document.FindClip(fileId)!;
        expect(ReferenceEquals(vm.Document.TrackOf(file), vm.Document.TrackOf(main)), "on its track");
        var pieces = Children(vm, file);
        expect(pieces.Count == 2 && pieces.All(p => p.Start == 8 && p.End == 12 && p.ParentClipId == file.Id), "with pieces of its own over its own span");
        expect(pieces.All(p => p.SourcePath == Second && p.ContentKind == LayerKind.VideoFile), "cut from its own file");
        expect(Children(vm, vm.Document.FindClip(main.Id)!).Count == 2, "the base clip keeps its own");
        expect(Math.Abs(view.SizeWidth - mainView.SizeWidth) < 1e-3, "and takes the base clip's size");
        expect(vm.IsBlurFilled(view) == vm.IsBlurFilled(mainView), "and its blur fill");
        expect(vm.Document.FindProblems().Count == 0, "the document holds together");

        // A clip dragged onto a plain track keeps its own look: there is no template to take, and no track's look any more.
        var plain = Open(Lane(FileClip(Second, 0, 4)), Lane(FileClip(Third, 4, 8)));
        var moving = plain.ViewOf(plain.Document.Tracks[2].Clips[0])!;
        var lead = plain.ViewOf(plain.Document.Tracks[1].Clips[0])!;
        (lead.CustomMask, lead.MaskPath, lead.CornerRadius) = (true, "D:/masks/a.png", 20);
        expect(plain.MoveClipToTrack(moving, lead, 4), "a clip dragged onto a plain track lands");
        expect(!moving.CustomMask && moving.CornerRadius == 0, "and takes nobody's mask or look");
    }

    private static void SequentialDrop(Action<bool, string> expect, StringBuilder text)
    {
        // The first of the files opened with a style; the others put after it as a drop does, through the real probe, when FFmpeg can make files.
        var vm = Open();
        var name = Preset(vm);
        vm.ApplyStyle(vm.ReadStyle(Path.Combine(AppPaths.Styles, name + StyleFile.Extension))!, layout: true, color: false, blur: true, subtitles: false);
        var main = vm.Document.Tracks.SelectMany(t => t.Clips).First(c => c.ContentKind == LayerKind.MainVideo);
        var mainView = vm.ViewOf(main)!;
        if (Ffmpeg() is not { } ffmpeg)
        {
            text.Append("skipped: no FFmpeg to make the files with\n");
            return;
        }

        // The test's data folder has no tools of its own: the real probe reads with the installed one.
        AppSettings.Current.FfprobePath = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var files = new[] { Path.Combine(folder, "two.mp4"), Path.Combine(folder, "three.mp4") };
            foreach (var file in files)
            {
                using var making = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=2", "-pix_fmt", "yuv420p", file]) { CreateNoWindow = true, UseShellExecute = false })!;
                making.WaitForExit();
            }

            var (at, row, follows) = (8.0, mainView, true);
            foreach (var file in files)
            {
                var adding = vm.AddVideoClipAsync(file, at, row, playOnce: true, follows: follows);
                while (!adding.IsCompleted)
                    Pump();
                var clip = adding.GetAwaiter().GetResult();
                expect(clip is not null, $"{Path.GetFileName(file)} is added" + (clip is null ? $" ({vm.StatusText})" : ""));
                if (clip is null)
                    return;
                vm.InheritTemplate(clip, mainView, at);
                (at, row) = (clip!.StartTime + clip.Duration, clip);
            }

            var line = vm.Document.TrackOf(main)!.Clips.ToList();
            expect(line.Count == 3, "all three on one track: Clip 1, Clip 2, Clip 3");
            expect(Math.Abs(line[1].Start - 8) < 1e-3 && Math.Abs(line[2].Start - line[1].End) < 1e-3, "each where the one before it ends");
            expect(line.All(c => Children(vm, c).Count == 2), "each with the preset's pieces");
            expect(line.All(c => Children(vm, c).All(p => Math.Abs(p.Start - c.Start) < 1e-3 && Math.Abs(p.End - c.End) < 1e-3)), "over its own span, which its file's length gave it");
            expect(line.Skip(1).All(c => Children(vm, c).All(p => p.SourcePath == c.SourcePath)), "the later two's cut from their own files");
            expect(vm.Document.FindProblems().Count == 0, "the document holds together");
        }
        finally
        {
            AppSettings.Current.FfprobePath = "";
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Its pictures are still being made in the background: the folder is in the temp folder, and goes with it.
            }
        }
    }

    private static string? Ffmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[] { Path.Combine(local, "HandPeg-Data", "deps", "ffmpeg", "ffmpeg.exe"), Path.Combine(local, "HandPeg", "deps", "ffmpeg", "ffmpeg.exe") }.FirstOrDefault(File.Exists);
    }

    private static void CommitRoom(Action<bool, string> expect, StringBuilder text)
    {
        var assembly = typeof(MainViewModel).Assembly;
        var cache = assembly.GetType("HandPegApp.Compositor.FrameCache") ?? throw new InvalidOperationException("no FrameCache");
        var room = cache.GetMethod("CommitRoom", BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("no CommitRoom");
        double Room(double held, double left, double limit) =>
            (long)room.Invoke(null, [(long)(held * (1L << 30)), (long)(left * (1L << 30)), (long)(limit * (1L << 30))])! / (double)(1L << 30);

        // 2026-11-01: 48 GB + a 4 GB page file (51.6 GB), a game at 17.7, the worker at 14.8, HandPeg at 2.8, the rest 16.3: nothing left.
        const double Limit = 51.6, Game = 17.7, HandPeg = 2.8, Others = 16.3, Overhead = 2.8;
        var left = Limit - Game - HandPeg - Others - Overhead - 12;
        var allowed = Room(12, left, Limit);
        expect(allowed < 12, "with a game running, a 12 GB cache is more than Windows can spare");
        var holding = Limit - Game - HandPeg - Others - Overhead - allowed;
        expect(holding >= Limit / 8 - 1e-6, "held to what it may keep, an eighth of the limit stays free for everything else");
        expect(Room(0, 40, 51.6) >= 12, "with nothing else running the 12 GB cache has all its room");
        expect(Room(0, 1, 8) == 0, "with less left than the margin, it may hold nothing beyond its least");
        expect(Math.Abs(Room(1, 10, 16) - 8) < 1e-9, "a small limit keeps at least 3 GB free");
        expect(Math.Abs(Room(1, 100, 128) - 93) < 1e-9, "a large one at most 8 GB");

        var commit = assembly.GetType("HandPegApp.Compositor.SystemCommit") ?? throw new InvalidOperationException("no SystemCommit");
        object?[] asked = [0L, 0L];
        var read = (bool)commit.GetMethod("TryRead", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, asked)!;
        expect(read && (long)asked[1]! > 0 && (long)asked[0]! > 0 && (long)asked[0]! <= (long)asked[1]!, "Windows says how much is left, within its limit");
    }

    private static void EncoderSteps(Action<bool, string> expect, StringBuilder text)
    {
        expect(MainViewModel.BestStepOf(EncoderFamily.Nvenc) == "p7", "NVENC's best is p7");
        expect(MainViewModel.BestStepOf(EncoderFamily.Amf) == "quality", "AMF's is quality");
        expect(MainViewModel.BestStepOf(EncoderFamily.Qsv) == "veryslow", "QSV's is veryslow");
        expect(MainViewModel.BestStepOf(EncoderFamily.Software) is null, "software has no such default");

        var vm = Open();
        void Choose(string name)
        {
            var option = vm.VideoEncoders.FirstOrDefault(e => e.Name == name);
            if (option is null)
                vm.VideoEncoders.Add(option = EncoderOption.FromHardwareName(name));
            vm.VideoEncoder = option;
        }

        expect(vm.EncoderPreset == "medium", "software starts at medium");
        Choose("hevc_qsv");
        expect(vm.EncoderPreset == "veryslow", "QSV starts at veryslow, though it has a medium too");
        vm.EncoderPreset = "fast";
        expect(vm.EncoderPreset == "fast", "a step chosen by hand stands");
        Choose("h264_nvenc");
        expect(vm.EncoderPreset == "p7", "NVENC starts at p7");
        Choose("hevc_amf");
        expect(vm.EncoderPreset == "quality", "AMF starts at quality");
        Choose("libx264");
        expect(vm.EncoderPresets.Contains(vm.EncoderPreset), "back on software it has one of its own steps");

        expect(new EncodingPreset().EncoderPreset.Length == 0, "a preset that names no step leaves it to the encoder");
        expect(KeybindManager.Action("edit.in") is null && KeybindManager.Action("edit.out") is null && KeybindManager.Action("transport.playInToOut") is null,
            "and the in and out points are gone from the key bindings");
    }
}
