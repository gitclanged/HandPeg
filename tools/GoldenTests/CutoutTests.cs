using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Gpu;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// The press on a preview's layer box (5.1-CleanupStudioAndFfmpegPurge, the user's directive): real LayerBoxes on a real Canvas, asked through
/// WPF's own hit test, so the template, the transforms and HitTestCore are all in the path. A hidden layer never takes a press; the
/// selected one keeps all of its box whatever lies over it; an unselected masked one takes only what its mask shows; and a click
/// with no drag still goes down through the stack.
/// </summary>
public static class CutoutTests
{
    private const string Template = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
            <Border IsHitTestVisible="False" Background="#01000000" />
        </ControlTemplate>
        """;

    public static IEnumerable<GraphResult> All()
    {
        var text = new StringBuilder("# A press on the preview: hidden clips, sticky selection and mask-aware hits, through WPF's hit test on real LayerBoxes.\n");
        var problems = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), $"handpeg-hits-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            Hits(text, problems, folder);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }

        yield return new GraphResult("preview-layer-hits", text.ToString(), problems, 0);

        var studio = new StringBuilder("# The Cutout Studio on a real view model: its window, zoomed to the pixels; Cutouts to a Game Preset, and to the timeline; what is not drawn.\n");
        var trouble = new List<string>();
        try
        {
            Studio(studio, trouble);
        }
        catch (Exception ex)
        {
            trouble.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("cutout-studio", studio.ToString(), trouble, 0);

        var builtIn = new StringBuilder("# The built-in Game Presets (5.1-Release): made as the Studio makes one, written where missing and never over a file, on a clip.\n");
        var wrong = new List<string>();
        try
        {
            BuiltIns(builtIn, wrong);
        }
        catch (Exception ex)
        {
            wrong.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        yield return new GraphResult("game-presets-builtin", builtIn.ToString(), wrong, 0);
    }

    private static void BuiltIns(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var names = GamePresetLibrary.All.Select(b => b.Name).ToList();
        text.Append("built-ins: ").AppendJoin(", ", names).Append('\n');
        Expect(names.SequenceEqual(["Overwatch 2 Vertical HUD", "Marvel Rivals Vertical HUD", "Valorant Vertical HUD", "CS2 Vertical HUD", "Apex Legends Vertical HUD", "The Finals Vertical HUD"]),
            "six games, Overwatch 2 first");

        foreach (var preset in GamePresetLibrary.All)
        {
            var style = GamePresetLibrary.Build(preset);
            var layers = style?.Layout?.Layers ?? [];
            text.Append($"{preset.Name}: ").AppendJoin(" | ", layers.Select(l => string.Create(CultureInfo.InvariantCulture,
                $"{l.Name} src {l.SourceX:0.###},{l.SourceY:0.###} {l.SourceWidth:0.###}x{l.SourceHeight:0.###} at {l.PositionX:0.###},{l.PositionY:0.###} w {l.SizeWidth:0.###}"))).Append('\n');
            Expect(style?.Layout is { Vertical: true, CenterZoom: GamePresetLibrary.DefaultZoom, MainVideoIndex: 0 } && style.Blur is { Radius: GamePresetLibrary.BlurRadius }
                   && layers.Count == preset.Pieces.Count, $"{preset.Name}: tall, the video zoomed, a Cutout each, the blur fill");
            Expect(layers.All(l => l is { Kind: LayerKind.Video, CustomMask: true, MaskFrame: true, Feather: true } && !string.IsNullOrEmpty(l.MaskShapes)),
                $"{preset.Name}: every Cutout masked by its outlines, so the Cutout Studio opens and edits it");
            Expect(style?.Masks is null && layers.All(l => l.MaskPath == "" && MaskStore.IsDrawn(MaskStore.SourceOf(l.MaskPath, l.MaskShapes)))
                   && layers.Select(l => MaskStore.SourceOf(l.MaskPath, l.MaskShapes)).Distinct().Count() == layers.Count,
                $"{preset.Name}: a mask of its own per Cutout, its outlines alone: no picture, no Base64 in the file");
            Expect(layers.All(l => l.SourceX >= 0 && l.SourceY >= 0 && l.SourceX + l.SourceWidth <= 1.0001 && l.SourceY + l.SourceHeight <= 1.0001 && l.SizeWidth > 0.05),
                $"{preset.Name}: every Cutout cut from inside the frame and given a size");
            Expect(layers.All(l => MaskStore.ParseShapes(l.MaskShapes).SelectMany(s => s).All(p =>
                    p.X >= l.SourceX - 1e-4 && p.X <= l.SourceX + l.SourceWidth + 1e-4 && p.Y >= l.SourceY - 1e-4 && p.Y <= l.SourceY + l.SourceHeight + 1e-4)),
                $"{preset.Name}: each Cutout's outlines lie inside the box it is cut from");
        }

        var overwatch = GamePresetLibrary.Build(GamePresetLibrary.All[0])!;
        Expect(overwatch.Layout!.Layers.Sum(l => MaskStore.ParseShapes(l.MaskShapes).Count) == 12, "Overwatch 2: the twelve islands of the user's own mask");
        Expect(overwatch.Layout.Layers.Select(l => (l.FeatherRadius, l.MaskGrow)).SequenceEqual([(16, 19), (4, 2), (2, 0)]), "Overwatch 2: feathered and grown as the user's own Game Preset");

        // Written where missing, never over a file; the user's drop-overlay list keeps its own first.
        var settings = AppSettings.Current;
        (settings.BuiltInGamePresetsVersion, settings.SplashStylePresets, settings.SplashPresetCount) = (0, ["Mine.hpstyle"], 3);
        Directory.CreateDirectory(AppPaths.Styles);
        var mine = Path.Combine(AppPaths.Styles, "Mine.hpstyle");
        File.WriteAllText(mine, """{"Layout":{"CenterZoom":1,"Layers":[]}}""");
        var theirs = Path.Combine(AppPaths.Styles, "CS2 Vertical HUD.hpstyle");
        File.WriteAllText(theirs, """{"Layout":{"CenterZoom":1.3,"Layers":[]}}""");
        var written = GamePresetLibrary.WriteMissingAsync().GetAwaiter().GetResult();
        Expect(written.Count == 5 && !written.Contains("CS2 Vertical HUD.hpstyle"), $"five written, the one already there left alone ({string.Join(", ", written)})");
        Expect(File.ReadAllText(theirs).Contains("1.3"), "a file of a built-in's name is never written over");
        Expect(written.All(f => File.GetLastWriteTime(Path.Combine(AppPaths.Styles, f)) < File.GetLastWriteTime(mine)), "the built-ins are older than the user's own, which stay first in the lists");
        GamePresetLibrary.Seeded();
        Expect(settings.SplashStylePresets.SequenceEqual(["Mine.hpstyle", "Overwatch 2 Vertical HUD.hpstyle"]) && settings.BuiltInGamePresetsVersion == GamePresetLibrary.Version,
            "the drop overlay keeps the user's own first and gets Overwatch 2 after them");
        Expect(StyleFile.ForLaunch().Select(s => s.Name).SequenceEqual(["Mine", "Overwatch 2 Vertical HUD"]), "both on the drop overlay");
        Expect(GamePresetLibrary.WriteMissingAsync().GetAwaiter().GetResult().Count == 0, "written once per version");
        File.Delete(Path.Combine(AppPaths.Styles, "Valorant Vertical HUD.hpstyle"));
        Expect(GamePresetLibrary.WriteMissingAsync().GetAwaiter().GetResult().Count == 0, "a built-in the user deleted stays deleted");

        // On a clip: the Overwatch 2 file applied as the Timeline's list and the clip menu do.
        const int W = 2560, H = 1440;
        const string main = "D:/media/overwatch.mkv";
        var vm = Opened(main, W, H, "hevc");
        vm.RefreshStylePresets();
        Expect(vm.StylePresets.Contains("Overwatch 2 Vertical HUD") && vm.StylePresets.Contains("The Finals Vertical HUD"), "in the Game Presets, the Timeline's list");
        var clip = vm.Layers.First(l => l.ClipId != Guid.Empty);
        Expect(vm.ApplyGamePreset([clip], "Overwatch 2 Vertical HUD"), "applied to a clip");
        var pieces = vm.CutoutLayers.Select(l => l.Name).Order().ToList();
        Expect(pieces.SequenceEqual(["Abilities & Ammo", "Health & Name", "Killfeed"]), $"its three Cutouts are the clip's, and the Studio's to edit ({string.Join(", ", pieces)})");
        Expect(vm.UseVerticalResolution && Math.Abs(ZoomOf(vm) - GamePresetLibrary.DefaultZoom) < 1e-6, "on a tall frame, the video zoomed");
        Expect(vm.CutoutLayers.All(l => l.MaskPath == "" && MaskStore.IsDrawn(l.MaskSource) && MaskStore.Exists(l.MaskSource)),
            "on the timeline each Cutout's mask is its outlines, there to be drawn, with no file");

        // Drawn masks are filled in memory (5.1-Release): exact coverage, nonzero winding, holes, the cut, the baker, FFmpeg's temp picture.
        static double Sum(float[] cover) => cover.Sum(v => (double)v);
        var square = MaskStore.DrawnKey("0.25,0.25 0.75,0.25 0.75,0.75 0.25,0.75");
        var filled = MaskStore.Rasterize(square, (0, 0, 1, 1), 8, 8)!;
        Expect(filled[3 * 8 + 3] == 1 && filled[0] == 0 && Math.Abs(Sum(filled) - 16) < 1e-4, $"a square fills its 16 pixels exactly ({Sum(filled):0.###})");
        var half = MaskStore.Rasterize(MaskStore.DrawnKey("0.25,0.25 0.6875,0.25 0.6875,0.75 0.25,0.75"), (0, 0, 1, 1), 8, 8)!;
        Expect(Math.Abs(half[3 * 8 + 5] - 0.5) < 1e-4, $"an edge through the middle of a pixel covers half of it ({half[3 * 8 + 5]:0.###})");
        var both = MaskStore.Rasterize(MaskStore.DrawnKey("0.25,0.25 0.75,0.25 0.75,0.75 0.25,0.75;0.5,0.5 1,0.5 1,1 0.5,1"), (0, 0, 1, 1), 8, 8)!;
        Expect(Math.Abs(Sum(both) - 28) < 1e-4 && both.All(v => v <= 1), $"overlapping outlines add up, never past white ({Sum(both):0.###})");
        var ring = MaskStore.Rasterize(MaskStore.DrawnKey("0,0 1,0 1,1 0,1;0.25,0.25 0.25,0.75 0.75,0.75 0.75,0.25"), (0, 0, 1, 1), 8, 8)!;
        Expect(Math.Abs(Sum(ring) - 48) < 1e-4 && ring[3 * 8 + 3] == 0, $"an outline turning the other way cuts a hole ({Sum(ring):0.###})");
        var cut = MaskStore.Rasterize(square, (0.5, 0.5, 0.5, 0.5), 4, 4)!;
        Expect(Math.Abs(Sum(cut) - 4) < 1e-4 && cut[0] == 1 && cut[15] == 0, "a cut fills only its part of the frame, at the size asked");
        var baked = MaskBaker.Bake(new MaskRecipe(square, false, 8, 8, (0, 0, 1, 1), false, 0, 0, 0, 0));
        Expect(baked is { } b && b[(3 * 8 + 3) * 4] == 255 && b[0] == 0, "the GPU engine's baker fills a drawn mask with no file");
        var forFfmpeg = MaskStore.FileFor(square, 64, 36);
        Expect(File.Exists(forFfmpeg) && forFfmpeg.StartsWith(SessionPaths.Masks, StringComparison.OrdinalIgnoreCase),
            "FFmpeg's fallback graph gets a picture in the session's temp folder, not the Masks folder");
        var old = new LayerState { CustomMask = true, MaskFrame = true, MaskShapes = "0.1,0.1 0.2,0.1 0.2,0.2", MaskPath = Path.Combine(AppPaths.Masks, "shape_0123456789ab.png") };
        var carried = new Dictionary<string, string>();
        MaskStore.Pack(old, carried);
        Expect(carried.Count == 0 && old.MaskPath == "", "packing a drawn mask carries nothing but its outlines, even one that named an old shape_*.png");
        var opened = new LayerState { CustomMask = true, MaskFrame = true, MaskShapes = "0.1,0.1 0.2,0.1 0.2,0.2", MaskPath = "shape_0123456789ab_cafe.png" };
        Expect(MaskStore.Unpack(opened, new Dictionary<string, string> { ["shape_0123456789ab_cafe.png"] = Convert.ToBase64String([1, 2, 3]) }) is null
               && opened.MaskPath == "" && !File.Exists(Path.Combine(AppPaths.Masks, "shape_0123456789ab_cafe.png")),
            "an older file's packed shape_*.png is never written back: the outlines stand in for it");
        var drawnFiles = Directory.Exists(AppPaths.Masks) ? Directory.GetFiles(AppPaths.Masks, "shape_*.png").Length : 0;
        Expect(drawnFiles == 0, $"no shape_*.png in the Masks folder after the Studio, six built-ins and a preset on a clip ({drawnFiles})");
    }

    private static void Studio(StringBuilder text, List<string> problems)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        const int W = 1920, H = 1080;
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations) = (false, false, false);

        // A view model holding one clip of the file opened, as GpuSceneClipEffectTests makes one: nothing is probed or decoded.
        const string main = "D:/media/game.mkv";
        var vm = Opened(main, W, H, "h264");

        // Two pieces, one of two outlines.
        const string hero = "0.02,0.8 0.2,0.8 0.2,0.95 0.02,0.95", feed = "0.75,0.05 0.98,0.05 0.98,0.2;0.75,0.25 0.98,0.25 0.98,0.3 0.75,0.3";
        List<CutoutPiece> pieces =
        [
            new("Hero Portrait & HP", hero, new Rect(0.02, 0.8, 0.18, 0.15), 0.05, 0.75, 0.5),
            new("Killfeed", feed, new Rect(0.75, 0.05, 0.23, 0.25), 0.4, 0.05, 0.55),
        ];

        // The Game Preset.
        Expect(vm.SaveCutoutPreset("  ", pieces, 1.7) is not null, "a Game Preset wants a name");
        Expect(vm.SaveCutoutPreset("OW / Test", pieces, 1.7) is null, "the Cutouts are saved as a Game Preset (the name's slash made safe)");
        var saved = Path.Combine(AppPaths.Styles, "OW _ Test" + StyleFile.Extension);
        Expect(File.Exists(saved), "it is a .hpstyle in the Styles folder");
        var style = vm.ReadStyle(saved);
        Expect(style?.Layout is { Vertical: true, CenterZoom: 1.7, MainVideoIndex: 0 } && style.Blur is { Radius: 20 }, "tall, the video zoomed, the Cutouts over it, with its blur fill");
        Expect(style?.Layout?.Layers.Select(l => l.Name).SequenceEqual(pieces.Select(p => p.Name)) == true, "its Cutouts carry their names");
        Expect(style is { Masks: null, Layout: not null } && style.Layout.Layers.All(l => l.MaskPath == "" && l.MaskShapes is { Length: > 0 }),
            "its two masks are their outlines alone: one file that works anywhere, no picture inside");
        Expect(StyleFile.ForLaunch().Any(s => s.Name == "OW _ Test"), "it is on the drop overlay");
        Expect(vm.StylePresets.Contains("OW _ Test"), "and in the Game Presets, on the Timeline's list too");

        // The pieces on the timeline, one step of Undo.
        var before = vm.Layers.Count(l => l.ClipId != Guid.Empty);
        vm.AddCutouts(pieces, 1.7);
        Expect(vm.CutoutLayers.Select(l => l.Name).Order().SequenceEqual(pieces.Select(p => p.Name).Order()), "the Cutouts are clips");
        Expect(vm.UseVerticalResolution && Math.Abs(ZoomOf(vm) - 1.7) < 1e-6, "on a tall frame, the video zoomed");
        var layer = vm.CutoutLayers.First();
        var (mainTrack, pieceTrack) = (vm.Document.Tracks.First(t => t.Clips.Any(c => c.ContentKind == LayerKind.VideoFile && c.ParentClipId is null)), vm.TrackOfRow(layer)!);
        Expect(pieceTrack.ParentTrackId == mainTrack.Id, "each a child of the video clip's track");
        Expect(vm.Document.Tracks.IndexOf(pieceTrack) > vm.Document.Tracks.IndexOf(mainTrack), "and over it, as the preset's stage shows them");
        vm.AddCutouts(pieces, 1.7);
        Expect(vm.CutoutLayers.Count() == 2, "added again they take the place of those made before");
        vm.Undo();
        vm.Undo();
        Expect(vm.Layers.Count(l => l.ClipId != Guid.Empty) == before, "two adds, two steps of Undo, and they are gone");
        vm.AddCutouts(pieces, 1.7);

        // What is not drawn cannot be pressed: the user's bug, a clip on a hidden track whose view does not say so.
        layer = vm.CutoutLayers.First();
        Expect(vm.IsPictureDrawn(layer, 1), "a Cutout on a shown track is drawn");
        var hiding = vm.TrackOfRow(layer)!;
        hiding.IsHidden = true;
        Expect(!layer.IsHidden && !vm.IsPictureDrawn(layer, 1), "on a hidden track it is not, though its view says it is not hidden");
        hiding.IsHidden = false;
        layer.Opacity = 0;
        Expect(!vm.IsPictureDrawn(layer, 1), "nor when it is wholly see-through");
        layer.Opacity = 100;

        // The window itself, off the screen, never activated: it lays out and draws at every zoom.
        var window = new MaskDrawWindow(vm, null) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        try
        {
            window.Show();
            Pump();
            var viewport = Find<Canvas>(window, c => c.ClipToBounds) ?? throw new InvalidOperationException("no viewport");
            string Zoom() => Find<TextBlock>(window, t => t.Text.EndsWith('x') && char.IsAsciiDigit(t.Text[0]))?.Text ?? "";
            var fitted = Zoom();
            Expect(fitted.Length > 0, "it opens fitted to the window");
            Expect(Find<TextBox>(window, t => t.Text == "Hero Portrait & HP") is not null && Find<TextBox>(window, t => t.Text == "Killfeed") is not null,
                "it opens on the Cutouts made earlier, each under its name, and its name can be typed");
            void Wheel(int notches)
            {
                for (var i = 0; i < Math.Abs(notches); i++)
                    viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, Math.Sign(notches) * 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                Pump();
            }

            Wheel(40);
            Expect(Zoom() == "64x", $"the wheel goes up to 64x, and no further ({Zoom()})");
            Wheel(-60);
            Expect(Zoom() == fitted, $"and back down to the whole picture, and no further ({Zoom()})");
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    /// <summary>
    /// A view model holding one clip of the file opened (5.2-AuditB: a clip of a file like any other, filling the frame, from 0 to
    /// 8 s; the Sequence sized by its picture), as DogfoodTests opens one: nothing is probed or decoded.
    /// </summary>
    private static MainViewModel Opened(string main, int width, int height, string codec)
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var vm = new MainViewModel { SourceWidth = width, SourceHeight = height, LocalMediaPath = main };
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, new MediaInfo("matroska", 8, 0, new VideoStreamInfo(width, height, codec, 60, "16:9", "1:1", "bt709", "bt709", 0), [], []));
        var document = new TimelineDocument { Width = width, Height = height, NaturalWidth = width, NaturalHeight = height, FrameRate = FrameRate.Fps30 };
        var track = new TimelineTrackModel { Kind = TimelineTrackKind.Video, TrackNumber = 1 };
        var look = new LayerState { Kind = LayerKind.VideoFile, Name = "Main", SizeWidth = 1, ImagePath = main, ImageWidth = width, ImageHeight = height, MediaDuration = 8 };
        track.Clips.Add(new TimelineClip { ContentKind = LayerKind.VideoFile, SourcePath = main, Start = 0, End = 8, MediaOffset = 0, SourceDuration = 8, State = look });
        document.Tracks.Add(track);
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [document]);
        return vm;
    }

    /// <summary>
    /// How far the file opened is zoomed across the frame: its clip's width, as its view holds it (5.2-AuditB: a Game Preset's
    /// zoom is the clip's own place, the Center controls' zoom before).
    /// </summary>
    private static double ZoomOf(MainViewModel vm) =>
        vm.Document.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.ContentKind == LayerKind.VideoFile && c.ParentClipId is null) is { } clip && vm.ViewOf(clip) is { } view
            ? view.SizeWidth
            : double.NaN;

    // What the window was asked to do, done: laid out, and drawn.
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

    private static void Hits(StringBuilder text, List<string> problems, string folder)
    {
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        // A HUD piece's mask of the whole frame: white in a triangle in the top left of the piece's box, black all round it.
        var mask = Path.Combine(folder, "piece.png");
        WriteMask(mask, 400, 300);
        var template = (ControlTemplate)XamlReader.Parse(Template);
        var hidden = new HashSet<Layer>();
        bool Drawn(Layer layer) => !hidden.Contains(layer);
        LayerBox? held = null;

        LayerBox Box(Layer layer, double x, double y, double width, double height)
        {
            var box = new LayerBox { Layer = layer, Template = template, Width = width, Height = height, Takes = (self, at) => LayerBox.TakesPoint(self, held, at, Drawn) };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            return box;
        }

        var video = Layer.FromState(new LayerState { Kind = LayerKind.Video, Name = "video" }, 400, 300, 400, 300);
        var piece = Layer.FromState(new LayerState { Kind = LayerKind.Video, Name = "piece", SourceX = 0.25, SourceY = 0.25, SourceWidth = 0.5, SourceHeight = 0.5,
            CustomMask = true, MaskPath = mask, MaskFrame = true }, 400, 300, 400, 300);
        var top = Layer.FromState(new LayerState { Kind = LayerKind.Video, Name = "top" }, 400, 300, 400, 300);
        var (under, over, third) = (Box(video, 0, 0, 400, 300), Box(piece, 100, 75, 200, 150), Box(top, 150, 100, 100, 100));
        var surface = new Canvas { Width = 400, Height = 300 };
        surface.Children.Add(under);
        surface.Children.Add(over);
        surface.Children.Add(third);
        surface.Measure(new Size(400, 300));
        surface.Arrange(new Rect(0, 0, 400, 300));
        surface.UpdateLayout();

        LayerBox? At(double x, double y)
        {
            LayerBox? found = null;
            VisualTreeHelper.HitTest(surface, o => o is UIElement { IsHitTestVisible: false } ? System.Windows.Media.HitTestFilterBehavior.ContinueSkipSelfAndChildren : System.Windows.Media.HitTestFilterBehavior.Continue,
                result =>
                {
                    if (result.VisualHit is LayerBox box)
                    {
                        found = box;
                        return HitTestResultBehavior.Stop;
                    }

                    return HitTestResultBehavior.Continue;
                }, new PointHitTestParameters(new Point(x, y)));
            return found;
        }

        string Name(LayerBox? box) => box?.Layer?.Name ?? "nothing";

        // Nothing selected: the top of the stack where it shows. The mask has to be read first (off the thread, in the program: waited for here).
        _ = LayerHitMask.Shows(piece, 0.1, 0.1, 200, 150, wait: true);
        Expect(ReferenceEquals(At(200, 150), third), $"nothing selected, the topmost box takes the press ({Name(At(200, 150))})");
        Expect(ReferenceEquals(At(110, 85), over), $"inside the Cutout's mask it takes the press ({Name(At(110, 85))})");
        Expect(ReferenceEquals(At(290, 215), under), $"in the empty corner of its mask the press falls through to what lies under it ({Name(At(290, 215))})");

        // Hidden: never.
        hidden.Add(top);
        Expect(!ReferenceEquals(At(200, 150), third), $"a hidden clip never takes a press ({Name(At(200, 150))})");
        hidden.Clear();

        // Selected: all of its box, whatever is over it.
        held = under;
        Expect(ReferenceEquals(At(200, 150), under), $"the selected clip keeps a press inside its box though others lie over it ({Name(At(200, 150))})");
        Expect(ReferenceEquals(At(110, 85), under), $"so does a press on a Cutout over it ({Name(At(110, 85))})");
        held = over;
        Expect(ReferenceEquals(At(290, 215), over), $"a selected masked clip takes all of its box, its empty corner too ({Name(At(290, 215))})");
        Expect(ReferenceEquals(At(10, 10), under), $"outside the selected box a press goes where it would ({Name(At(10, 10))})");

        // A hidden selection holds nothing back.
        held = third;
        hidden.Add(top);
        Expect(!ReferenceEquals(At(200, 150), third), $"a selected clip that is hidden holds nothing back ({Name(At(200, 150))})");
        hidden.Clear();
        held = null;

        // A click with no drag goes down the stack and comes round.
        List<LayerBox> stack = [under, over, third];
        var first = LayerBox.NextDown(stack, third, surface, new Point(155, 102), Drawn);
        var second = first is null ? null : LayerBox.NextDown(stack, first, surface, new Point(155, 102), Drawn);
        var third2 = second is null ? null : LayerBox.NextDown(stack, second, surface, new Point(155, 102), Drawn);
        Expect(ReferenceEquals(first, over) && ReferenceEquals(second, under) && ReferenceEquals(third2, third),
            $"a click goes down the stack and comes round ({Name(first)}, {Name(second)}, {Name(third2)})");
        Expect(LayerBox.NextDown(stack, under, surface, new Point(10, 10), Drawn) is null, "with nothing else there, a click stays where it is");

        // Turned: the selected box's rectangle is its own, not the frame's.
        third.RenderTransformOrigin = new Point(0.5, 0.5);
        third.RenderTransform = new RotateTransform(45);
        surface.UpdateLayout();
        held = third;
        Expect(ReferenceEquals(At(200, 150), third), "a turned selected box keeps its middle");
        Expect(!ReferenceEquals(At(152, 102), third), "and its corner, turned away, is no longer its own");
    }

    private static void WriteMask(string path, int width, int height)
    {
        // White where x + y is small within the piece's box (the middle half of the frame): a triangle in its top left.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            var outline = new StreamGeometry();
            using (var figure = outline.Open())
            {
                figure.BeginFigure(new Point(100, 75), true, true);
                figure.LineTo(new Point(200, 75), false, false);
                figure.LineTo(new Point(100, 150), false, false);
            }

            outline.Freeze();
            context.DrawGeometry(Brushes.White, null, outline);
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
