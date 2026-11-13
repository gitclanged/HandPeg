using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Compositor;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4D (Subagent B): the Live Encoded Preview on the player bar by default, its Magnifier, the title-bar rail over
/// the tabs, and the 5.4C answers (Fast Proxy at the fastest step, drops on the Media Browser, removing used Project Media, Game Presets).
/// live-encoded-preview-bar: the bar's list (shown by default after the 3D view, list version 3), the eye glyph, the flyout made on first
/// opening and no pointer hook before the Magnifier is on, the preview itself never saved. live-encoded-preview-magnifier: the pointer
/// to the output's fractions through the picture's rectangle (the player's zoom and pan), the header's fields at 176 and 180 and its flag,
/// the loupe's side and corner round the point (SceneRenderer.LoupeAt), the flyout's CheckBox, and a real worker drawing it over the
/// compare (an edge being tuned first). fast-proxy-fastest-step: NVENC p1, QuickSync veryfast, AMF speed, x264 ultrafast, Media
/// Foundation -compression_level 0, the probe race, the software retry, and a pick by hand or another preset ending it.
/// drop-media-bin-blank: a drop on the Media Browser with nothing open pools its media, and the pool outlasts the first file opened.
/// project-media-remove-with-clips: a used file out of Project Media with its clips, one step, asked first (Cancel the default).
/// subtitle-editor-flag-gone: the modal editor's flag and its visit removed. title-rail-game-presets: the title bar's progress over the
/// tabs (its rail and washes) and the Game Presets button in the Settings flyout. Every line says what it proved.
/// </summary>
public static class EncodedPreviewQaTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The window-free cases (and those on a window of their own), before the UI-scale tests.</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("live-encoded-preview-bar", "# The Live Encoded Preview on the player bar by default (5.4D): after the 3D view, its own glyph, its flyout made on first opening.", Bar);
        yield return Case("live-encoded-preview-magnifier", "# The Live Encoded Preview's Magnifier (5.4D, Q10 b): the pointer to the output, the header, the loupe's place, a real worker drawing it.", Magnifier);
        yield return Case("fast-proxy-fastest-step", "# Fast Proxy at the encoder's fastest step (5.4D, Q2 b): p1, veryfast, speed, ultrafast, -compression_level 0; every other preset the slowest.", FastProxy);
        yield return Case("drop-media-bin-blank", "# A drop on the Media Browser with nothing open goes into Project Media (5.4D, Q4 b), and the blank window keeps it for its first file.", Quietly(DropOnBlank));
        yield return Case("project-media-remove-with-clips", "# A used file out of Project Media with its clips (5.4D, Q5 b): asked first, one step, Undo brings both back, a locked track refuses.", Quietly(RemoveWithClips));
        yield return Case("subtitle-editor-flag-gone", "# The modal subtitle editor's flag is gone (5.4D, Q6 a): no IsSubtitleEditorOpen, no visit, no guard left.", FlagGone);
        yield return Case("title-rail-game-presets", "# The title bar's progress over the project tabs (a 2 DIP rail, 16 % wash) and Game Presets in the Settings flyout (5.4D, Q12).", TitleRail);
    }

    /// <summary>The cases that need the WPF Application the UI-scale tests make: run last, after header-flyouts-placement.</summary>
    public static IEnumerable<GraphResult> Live()
    {
        yield break;
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

    // ----- Helpers -----

    private static string Root()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "src", .. parts]));

    /// <summary>A method's text in a source file: from its signature to the first line that closes it at its own indent.</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    private static string? Attr(XElement element, XName name) => (string?)element.Attribute(name);

    private static XElement? Named(XDocument xaml, string name) => xaml.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == name);

    /// <summary>
    /// Runs a case whose view model opens files (PlaceOpenedFile, a clip's view) under a context that drops what an await would come back
    /// to: in the app that is the window's dispatcher; here, with none, a file's work (its probe, its pictures) would come back on the thread
    /// pool and change the Sequence under the test's feet. What the case checks is the edit itself, never that work.
    /// </summary>
    private static Action<Action<bool, string>, StringBuilder> Quietly(Action<Action<bool, string>, StringBuilder> run) => (expect, text) =>
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DroppingContext());
        try
        {
            run(expect, text);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    };

    private sealed class DroppingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    private static MainViewModel Empty()
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [new TimelineDocument()]);
        return vm;
    }

    private static SequenceExportState ExportState(MainViewModel vm) =>
        (SequenceExportState)typeof(MainViewModel).GetMethod("CompileExportState", Hidden)!.Invoke(vm, null)!;

    private static EncodingPreset BuiltIn(string name) => ExportPresetCatalog.BuiltIns.First(p => p.Name == name);

    private static List<(string What, byte[] State)> StepsOf(MainViewModel vm) =>
        (List<(string What, byte[] State)>)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!;

    private static TimelineClip Video(string path, double start, double end) =>
        new() { SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, SourceDuration = 60 };

    private static MediaInfo Probe(double seconds = 60) =>
        new("mov,mp4", seconds, 0, new VideoStreamInfo(1920, 1080, "h264", 60, "16:9", "1:1", "bt709", "bt709", 0), [], []);

    /// <summary>Opens a file onto the view model as a load does once probed (PlaceOpenedFile).</summary>
    private static void Place(MainViewModel vm, string path, MediaInfo probe)
    {
        vm.SourceWidth = probe.Video!.Width;
        vm.SourceHeight = probe.Video.Height;
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, probe);
        vm.LocalMediaPath = path;
        typeof(MainViewModel).GetMethod("PlaceOpenedFile", Hidden)!.Invoke(vm, [""]);
    }

    // ----- live-encoded-preview-bar (B-1) -----

    private static void Bar(Action<bool, string> expect, StringBuilder text)
    {
        var bar = new PlaybackControlSet();
        bar.Load(null);
        var ids = bar.Options.Select(o => o.Id).ToList();
        text.Append("   the playback bar's defaults: ").Append(bar.Save()).Append('\n');
        expect(bar["ExportQualityPreview"] && ids.IndexOf("ExportQualityPreview") == ids.IndexOf("Exploded3D") + 1
               && bar.Options.First(o => o.Id == "ExportQualityPreview") is { Label: "Live Encoded Preview", Group: "Viewer" },
            "the Live Encoded Preview is one of the bar's defaults, listed right after the 3D Exploded View under Viewer");
        bar.Load("PrevIFrame,Exploded3D,Volume,@2");
        var upgraded = bar.Save();
        bar.Load("PrevIFrame,Volume,@1");
        expect(upgraded == "PrevIFrame,Exploded3D,ExportQualityPreview,Volume,@3" && bar["Exploded3D"] && bar["ExportQualityPreview"],
            "a bar saved before 5.4D shows it once (list version 3), and one from before 5.0 gains the 3D view and it together");
        bar.Load("PrevIFrame,@3");
        expect(!bar["ExportQualityPreview"], "a bar saved at version 3 without it keeps it hidden: the user's choice stands");

        // The XAML: the slot right after the 3D view's toggle; the eye is the bar's only E890 (5.4.1A: Render Preview, E7B3, is retired).
        var xaml = XDocument.Load(Path.Combine(Root(), "src", "MainWindow.xaml"));
        var exploded = Named(xaml, "ExplodedToggle");
        expect(exploded?.ElementsAfterSelf().FirstOrDefault() is { } next && Attr(next, X + "Name") == "ExportQualityPreviewBarSlot",
            "MainWindow.xaml: ExportQualityPreviewBarSlot is the next control after ExplodedToggle in the transport");
        var render = xaml.Descendants().FirstOrDefault(e => Attr(e, "AutomationProperties.Name") == "Render Preview");
        var strayEyes = xaml.Descendants().Count(e => (Attr(e, "Content") == "\uE890" || Attr(e, "Text") == "\uE890")
            && !e.AncestorsAndSelf().Any(a => Attr(a, "AutomationProperties.Name") == "Live Encoded Preview"));
        expect(MainWindow.LoopbackGlyph == "\uE890" && MainWindow.LoopbackName == "Live Encoded Preview" && render is null && strayEyes == 0,
            "the toggle is the eye (Segoe MDL2 E890, View) and named Live Encoded Preview; no other control shows that glyph, and none is named Render Preview (retired)");

        // Law 1: the toggle is made with the bar, its flyout on first opening; no pointer hook until the preview and the Magnifier are on.
        var loopback = Read("MainWindow.LoopbackPreview.cs");
        var build = Method(loopback, "private void BuildLoopbackToggle()");
        expect(build.Contains("flyout.Child ??= BuildLoopbackFlyout();", StringComparison.Ordinal) && !build.Contains("Child = BuildLoopbackFlyout()", StringComparison.Ordinal)
               && build.Contains("Content = LoopbackGlyph", StringComparison.Ordinal),
            "BuildLoopbackToggle makes the popup empty and its flyout the first time it opens (Law 1: nothing of the flyout before Frame 1)");
        expect(build.Contains("AutomationProperties.SetName(_loopbackToggle, LoopbackName)", StringComparison.Ordinal)
               && build.Contains("LoopbackName + \" (On)\"", StringComparison.Ordinal) && !loopback.Contains("\"Export Quality Preview", StringComparison.Ordinal),
            "its name, tooltip and overflow menu entry say Live Encoded Preview; no text of the player bar's says Export Quality Preview any more");
        var hook = Method(loopback, "private void HookLoopback()");
        var sync = Method(loopback, "private void SyncLoopbackLoupe()");
        expect(hook.Length > 0 && !hook.Contains("PointerMoved", StringComparison.Ordinal) && loopback.Split("PointerMoved +=").Length == 2
               && sync.Contains("_viewModel.IsExportQualityPreview && _viewModel.ExportQualityPreviewLoupe", StringComparison.Ordinal)
               && sync.Contains("VideoView.PointerMoved += LoopbackPointerMoved", StringComparison.Ordinal),
            "the pointer hook is attached in one place only, SyncLoopbackLoupe, while the preview and the Magnifier are both on (none at start-up)");

        // The preview itself is never saved and starts off; the Magnifier is a saved viewer preference.
        var vm = new MainViewModel();
        expect(!vm.IsExportQualityPreview && typeof(AppSettings).GetProperties().All(p => !p.Name.Contains("IsExportQualityPreview", StringComparison.Ordinal)),
            "the preview is off at every start and AppSettings holds nothing of it");
        var was = AppSettings.Current.ExportQualityPreviewLoupe;
        try
        {
            vm.ExportQualityPreviewLoupe = !was;
            var saved = AppSettings.Current.ExportQualityPreviewLoupe == !was;
            AppSettings.Current.ExportQualityPreviewLoupe = true;
            expect(saved && new MainViewModel().ExportQualityPreviewLoupe, "the Magnifier is saved as it is ticked (AppSettings.ExportQualityPreviewLoupe) and a new window starts with it");
        }
        finally
        {
            AppSettings.Current.ExportQualityPreviewLoupe = was;
        }
    }

    // ----- live-encoded-preview-magnifier (B-2) -----

    private static void Magnifier(Action<bool, string> expect, StringBuilder text)
    {
        // The protocol: two floats in bytes the header left free, and a flag of their own.
        int[] others = [FrameSceneHeader.FlagTimePasses, FrameSceneHeader.FlagWaitForTimings, FrameSceneHeader.FlagRawOutput, FrameSceneHeader.FlagExactFrames,
            FrameSceneHeader.FlagLetterbox, FrameSceneHeader.FlagReadback, FrameSceneHeader.FlagReadbackNv12, FrameSceneHeader.FlagDepthRails,
            FrameSceneHeader.FlagLoopback, FrameSceneHeader.FlagReadbackP010];
        expect(Marshal.OffsetOf<FrameSceneHeader>(nameof(FrameSceneHeader.LoopbackLoupeX)) == 176 && Marshal.OffsetOf<FrameSceneHeader>(nameof(FrameSceneHeader.LoopbackLoupeY)) == 180
               && Marshal.OffsetOf<FrameSceneHeader>(nameof(FrameSceneHeader.LoopbackTestFlags)) == 172 && Marshal.SizeOf<FrameSceneHeader>() == FrameScene.HeaderBytes,
            "FrameSceneHeader.LoopbackLoupeX and Y are at bytes 176 and 180, after LoopbackTestFlags (172); the header is still 256 bytes");
        expect(FrameSceneHeader.FlagLoopbackLoupe == 1024 && others.All(f => (f & FrameSceneHeader.FlagLoopbackLoupe) == 0),
            "FlagLoopbackLoupe is 1024, a bit no other flag uses");

        // The picture's rectangle on the player is SceneRenderer.Letterbox in the client's units, zoomed and panned alike.
        var close = true;
        foreach (var (zoom, panX, panY, width, height) in new[] { (1.0, 0.0, 0.0, 1000, 1000), (2.0, 0.25, 0.0, 1000, 1000), (3.0, -0.1, 0.2, 1600, 900), (0.5, 0.0, 0.0, 640, 360) })
        {
            var rect = GpuPreviewEngine.PictureRectOf(1920, 1080, zoom, panX, panY, width, height);
            var header = new FrameSceneHeader { CanvasWidth = 1920, CanvasHeight = 1080, ViewScale = (float)zoom, ViewPanX = (float)panX, ViewPanY = (float)panY };
            var (x, y, scale) = SceneRenderer.Letterbox(header, width, height);
            close &= Math.Abs(rect.Left - x) < 1e-2 && Math.Abs(rect.Top - y) < 1e-2 && Math.Abs(rect.Width - 1920 * scale) < 1e-2 && Math.Abs(rect.Height - 1080 * scale) < 1e-2;
        }

        var fitted = GpuPreviewEngine.PictureRectOf(1920, 1080, 1, 0, 0, 1000, 1000);
        expect(close && Math.Abs(fitted.Left) < 1e-9 && Math.Abs(fitted.Top - 218.75) < 1e-9 && Math.Abs(fitted.Width - 1000) < 1e-9 && Math.Abs(fitted.Height - 562.5) < 1e-9,
            "PictureRectOf is the worker's letterbox (fitted, zoomed and panned) on the client's side: a 1920 x 1080 canvas in 1000 x 1000 lies at (0, 218.75), 1000 x 562.5");

        // The pointer to fractions of the output: none over the bars, nor off the zoomed picture, nor on a player with no size.
        var middle = GpuPreviewEngine.LoupePointOf(500, 500, 1000, 1000, fitted);
        var bar = GpuPreviewEngine.LoupePointOf(500, 100, 1000, 1000, fitted);
        var zoomed = GpuPreviewEngine.PictureRectOf(1920, 1080, 2, 0.25, 0, 1000, 1000);
        var corner = GpuPreviewEngine.LoupePointOf(999, 5, 1000, 1000, zoomed);
        var edge = GpuPreviewEngine.LoupePointOf(1000, 5, 1000, 1000, zoomed);
        var sizeless = GpuPreviewEngine.LoupePointOf(1, 1, 0, 0, fitted);
        var nan = GpuPreviewEngine.LoupePointOf(double.NaN, 1, 1000, 1000, fitted);
        expect(middle == (0.5, 0.5) && bar is null && corner is { } c && Math.Abs(c.X - 0.999) < 1e-9 && Math.Abs(c.Y - 0.005) < 1e-9 && edge is null && sizeless is null && nan is null,
            "LoupePointOf: the middle is (0.5, 0.5); the letterbox's bar has none; zoomed 2x, a corner of the player is on the picture, its far edge not; no size or no number, none");

        // DIPs to pixels: the fraction made the worker's output pixels lands on the canvas point under the pointer (200 % scale, zoom and pan).
        var dips = GpuPreviewEngine.PictureRectOf(1920, 1080, 2, 0.1, -0.05, 800, 450);
        var pointer = (X: 300.0, Y: 200.0);
        var fraction = GpuPreviewEngine.LoupePointOf(pointer.X, pointer.Y, 800, 450, dips)!.Value;
        var zoomedHeader = new FrameSceneHeader { CanvasWidth = 1920, CanvasHeight = 1080, ViewScale = 2, ViewPanX = 0.1f, ViewPanY = -0.05f, LoupeZoom = 4 };
        var plan = SceneRenderer.LoupeAt(zoomedHeader, (float)(fraction.X * 1600), (float)(fraction.Y * 900), 1600, 900);
        var expected = ((pointer.X - dips.Left) / (dips.Width / 1920), (pointer.Y - dips.Top) / (dips.Height / 1080));
        text.Append(FormattableString.Invariant($"   a pointer at (300, 200) DIPs on an 800 x 450 player (1600 x 900 pixels, zoom 2, pan 0.1, -0.05): output ({fraction.X:0.####}, {fraction.Y:0.####}), canvas ({plan?.PointX:0.##}, {plan?.PointY:0.##})\n"));
        expect(plan is { } p && Math.Abs(p.PointX - expected.Item1) < 0.05 && Math.Abs(p.PointY - expected.Item2) < 0.05 && Math.Abs(p.Scale - 2 * 1600f / 1920 * 4) < 1e-3,
            "through the picture's rectangle in DIPs and the worker's in pixels, the loupe looks at the canvas point under the pointer, at the player's scale x 4");

        // The loupe's side and corner: a third of the picture's shorter side, in the corner farthest from the point; none off the picture.
        var flat = new FrameSceneHeader { CanvasWidth = 640, CanvasHeight = 360, ViewScale = 1, LoupeZoom = 4 };
        var nearTopLeft = SceneRenderer.LoupeAt(flat, 160, 90, 640, 360);
        var nearBottomRight = SceneRenderer.LoupeAt(flat, 500, 300, 640, 360);
        var offPicture = SceneRenderer.LoupeAt(flat, 320, 50, 640, 640);
        expect(nearTopLeft is { Side: 130, X: 498, Y: 218, PointX: 160, PointY: 90, Scale: 4 } && nearBottomRight is { Side: 130, X: 12, Y: 12 } && offPicture is null
               && SceneRenderer.LoupeSide((0, 0, 640, 360)) == 130 && SceneRenderer.LoupeCorner((0, 0, 640, 360), 640, 360, 130, 160, 90) == (498, 218),
            "LoupeAt: a pointer near the top left puts a 130-pixel loupe at (498, 218), one near the bottom right at (12, 12); over a 640 x 640 output's bar, none");

        // The engine, the worker, the surface and the window: a loupe moved is drawn again, never encoded again; an edge's loupe first.
        var engine = Read("Services", "GpuPreviewEngine.Loopback.cs");
        var set = Method(engine, "public void SetLoopbackLoupe(");
        var write = Method(engine, "private void WriteLoopback(");
        expect(set.Contains("Volatile.Write(ref _sceneDirty, 1)", StringComparison.Ordinal) && !set.Contains("Invalidate()", StringComparison.Ordinal)
               && write.Contains("header.Flags |= FrameSceneHeader.FlagLoopbackLoupe", StringComparison.Ordinal),
            "SetLoopbackLoupe marks the scene dirty only (the content stamp stays: no new encode, Law 3); WriteLoopback sets the flag and the point");
        var worker = Read("Compositor", "CompositorWorker.cs");
        var draw = Method(worker, "private static bool DrawLoopback(");
        var edgeFirst = draw.IndexOf("renderer.PrepareLoupe(header", StringComparison.Ordinal);
        var pointerNext = draw.IndexOf("renderer.PrepareLoupeAt(header", StringComparison.Ordinal);
        expect(edgeFirst > 0 && pointerNext > edgeFirst && pointerNext < draw.IndexOf("Dxgi.AcquireSync", StringComparison.Ordinal)
               && draw.Contains("header.LoopbackLoupeX * width, header.LoopbackLoupeY * height", StringComparison.Ordinal),
            "DrawLoopback prepares an edge's loupe first, else the Magnifier's at the fractions made its output's pixels, both before it takes the output");
        var surface = Read("Controls", "VideoSurface.cs");
        expect(Method(surface, "protected override IntPtr WndProc(").Contains("msg is WmMouseMove or WmMouseLeave && PointerMoved is not null", StringComparison.Ordinal)
               && Method(surface, "private IntPtr OverlayMessage(").Contains("TellPointer(hwnd, msg, lParam)", StringComparison.Ordinal)
               && surface.Contains("WmMouseMove = 0x0200, WmMouseLeave = 0x02A3", StringComparison.Ordinal),
            "VideoSurface.PointerMoved is raised from the native window's WM_MOUSEMOVE and WM_MOUSELEAVE and from the layer above it, only while something listens");
        var window = Read("MainWindow.LoopbackPreview.cs");
        var moved = Method(window, "private void LoopbackPointerMoved(");
        expect(moved.Contains("_explodedUi", StringComparison.Ordinal) && moved.Contains("_loopbackWipeHandle?.IsMouseCaptured == true", StringComparison.Ordinal)
               && moved.Contains("engine.PictureRect(width, height)", StringComparison.Ordinal) && moved.Contains("engine.SetLoopbackLoupe(null, null)", StringComparison.Ordinal),
            "the window puts the loupe away in the 3D view, while the wipe is dragged and when the pointer leaves; else maps it through the picture's rectangle");
        var flyout = Method(window, "private FrameworkElement BuildLoopbackFlyout()");
        expect(flyout.Contains("Content = \"Magnifier (Loupe)\"", StringComparison.Ordinal)
               && flyout.Contains("new Binding(nameof(ViewModels.MainViewModel.ExportQualityPreviewLoupe)) { Mode = BindingMode.TwoWay }", StringComparison.Ordinal),
            "the flyout has a \"Magnifier (Loupe)\" CheckBox bound both ways to ExportQualityPreviewLoupe");

        RealWorker(expect);
    }

    // A real worker: the compare with the codec bypassed (any machine), the Magnifier at a point, and an edge's loupe winning over it.
    private static void RealWorker(Action<bool, string> expect)
    {
        const int W = 640, H = 360;
        using var client = new CompositorClient(adapter: -1, keepStandby: true);
        using var reader = new CompositorFrameReader(client);

        byte[]? View(bool magnifier, float x, float y, bool edge, string what)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (W, H, FrameSceneHeader.FlagLetterbox | FrameSceneHeader.FlagExactFrames);
            (scene.OutputWidth, scene.OutputHeight, scene.ViewScale) = (W, H, 1);
            ref var layer = ref client.Layer(0);
            layer.SourceKind = (int)GpuSourceKind.TestPattern;
            layer.PatternKind = 1;
            (layer.X, layer.Y, layer.Width, layer.Height) = (0, 0, W, H);
            (layer.AnchorX, layer.AnchorY, layer.Opacity) = (0.5f, 0.5f, 1);
            (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
            (layer.CropRight, layer.CropBottom) = (1, 1);
            layer.Flags = GpuLayerDescriptor.FlagVisible;
            layer.ClipKey = 77;
            scene.ActiveLayerCount = 1;
            scene.Flags |= FrameSceneHeader.FlagLoopback;
            (scene.LoopbackCodec, scene.LoopbackRateControl, scene.LoopbackBitrateKbps, scene.LoopbackQuality, scene.LoopbackQualityVsSpeed) = (1, 0, 8000, 55, 100);
            (scene.LoopbackMode, scene.LoopbackWipeX, scene.LoopbackFrameRate, scene.LoopbackContentStamp, scene.LoopbackHeatGain) = (0, 0.5f, 30, 41, 10);
            scene.LoopbackTestFlags = 1;
            if (magnifier)
            {
                scene.Flags |= FrameSceneHeader.FlagLoopbackLoupe;
                (scene.LoopbackLoupeX, scene.LoopbackLoupeY, scene.LoupeZoom) = (x, y, 4);
            }

            if (edge)
                (scene.LoupeClipKey, scene.LoupeZoom) = (77, 4);

            var sequence = client.Publish();
            if (!client.WaitForFrame(sequence, 10_000))
            {
                expect(false, $"{what}: a frame within 10 s ({client.State}: {client.Message})");
                return null;
            }

            if (!reader.TryRead(out var bgra, out var width, out var height) || (width, height) != (W, H))
            {
                expect(false, $"{what}: the frame read back at {W} x {H}");
                return null;
            }

            return bgra;
        }

        var plain = View(false, 0, 0, false, "the compare without the Magnifier");
        var nearTopLeft = View(true, 0.25f, 0.25f, false, "the Magnifier at (0.25, 0.25)");
        var nearBottomRight = View(true, 0.75f, 0.75f, false, "the Magnifier at (0.75, 0.75)");
        var edgeFirst = View(true, 0.75f, 0.75f, true, "an edge's loupe with the Magnifier on");
        if (plain is null || nearTopLeft is null || nearBottomRight is null || edgeFirst is null)
            return;

        const int side = 130;
        int Away(byte[] a, byte[] b, int at) => Math.Max(Math.Abs(a[at] - b[at]), Math.Max(Math.Abs(a[at + 1] - b[at + 1]), Math.Abs(a[at + 2] - b[at + 2])));
        (int Changed, int OutsideWorst) Compare(byte[] frame, int left, int top)
        {
            var (changed, outside) = (0, 0);
            for (var py = 0; py < H; py++)
            {
                for (var px = 0; px < W; px++)
                {
                    var away = Away(plain, frame, (py * W + px) * 4);
                    if (px >= left && px < left + side && py >= top && py < top + side)
                        changed += away > 8 ? 1 : 0;
                    else
                        outside = Math.Max(outside, away);
                }
            }

            return (changed, outside);
        }

        int[] Pixel(byte[] frame, int px, int py) => [frame[(py * W + px) * 4 + 2], frame[(py * W + px) * 4 + 1], frame[(py * W + px) * 4]];
        var far = Compare(nearTopLeft, 498, 218);
        var centre = Pixel(nearTopLeft, 498 + side / 2, 218 + side / 2).Zip(Pixel(plain, 160, 90), (a, b) => Math.Abs(a - b)).Max();
        Console.WriteLine(FormattableString.Invariant($"  (Magnifier at (160, 90): {far.Changed} of {side * side} pixels of its box changed, the rest within {far.OutsideWorst}/255; its middle {centre}/255 from the picture there)"));
        expect(far.OutsideWorst <= 1 && far.Changed > side * side / 4 && centre <= 3,
            "a real worker draws the Magnifier over the compare in the corner farthest from the point (498, 218, 130 a side), its middle the picture at the point; the rest as it was");
        var near = Compare(nearBottomRight, 12, 12);
        expect(near.OutsideWorst <= 1 && near.Changed > side * side / 4, "the pointer near the bottom right puts it in the top left corner (12, 12)");
        var edge = Compare(edgeFirst, 498, 218);
        expect(edge.OutsideWorst <= 1 && edge.Changed > side * side / 4,
            "an edge being tuned comes first: its loupe (looking at the layer's top left corner) stands at (498, 218), and none at the pointer's (12, 12)");
    }

    // ----- fast-proxy-fastest-step (B-3) -----

    private static MainViewModel FastProxyOn(string? hardware, bool adreno = false)
    {
        var vm = Empty();
        if (hardware is not null)
            vm.VideoEncoders.Add(EncoderOption.FromHardwareName(hardware, qualcommAdreno: adreno));
        vm.SelectedPreset = BuiltIn("Fast Proxy");
        return vm;
    }

    private static void FastProxy(Action<bool, string> expect, StringBuilder text)
    {
        expect(MainViewModel.FastestStepOf(EncoderFamily.Nvenc) == "p1" && MainViewModel.FastestStepOf(EncoderFamily.Qsv) == "veryfast"
               && MainViewModel.FastestStepOf(EncoderFamily.Amf) == "speed" && MainViewModel.FastestStepOf(EncoderFamily.MediaFoundation) is null
               && MainViewModel.FastestStepOf(EncoderOption.FromHardwareName("hevc_nvenc", qualcommAdreno: false)) == "p1",
            "FastestStepOf: NVENC p1, QuickSync veryfast, AMF speed; Media Foundation none of its steps (its -compression_level 0 instead)");

        foreach (var (encoder, step, argument) in new[] { ("h264_nvenc", "p1", "-preset p1"), ("h264_qsv", "veryfast", "-preset veryfast"), ("h264_amf", "speed", "-quality speed") })
        {
            var vm = FastProxyOn(encoder);
            var args = ExportState(vm).VideoCodecArguments;
            text.Append("   Fast Proxy on ").Append(encoder).Append(": ").Append(args).Append('\n');
            expect(vm.VideoEncoder.Name == encoder && vm.EncoderPreset == step && args.Contains(argument, StringComparison.Ordinal) && vm.SpeedFirstHolds,
                $"Fast Proxy with {encoder} here: {encoder} at {step} ({argument}), the fastest step");
        }

        var software = FastProxyOn(null);
        var x264 = ExportState(software).VideoCodecArguments;
        expect(software.VideoEncoder.Name == "libx264" && software.EncoderPreset == "ultrafast" && x264.Contains("-preset ultrafast", StringComparison.Ordinal),
            "Fast Proxy with no H.264 hardware encoder: x264 at ultrafast");

        var arm = FastProxyOn("h264_mf", adreno: true);
        var mf = ExportState(arm).VideoCodecArguments;
        text.Append("   Fast Proxy on h264_mf: ").Append(mf).Append('\n');
        expect(arm.VideoEncoder.Name == "h264_mf" && mf.Contains(MediaFoundationEncoder.FastestStepArgument, StringComparison.Ordinal) && !mf.Contains(MediaFoundationEncoder.SlowestStepArgument, StringComparison.Ordinal)
               && MediaFoundationEncoder.FastestStepArgument == "-compression_level 0",
            "Fast Proxy on a Snapdragon (Media Foundation only): h264_mf at -compression_level 0, in place of 100");
        arm.SelectedPreset = BuiltIn(ExportPresetCatalog.MasterQuality + " (H.264)");
        expect(!arm.SpeedFirstHolds && ExportState(arm).VideoCodecArguments.Contains(MediaFoundationEncoder.SlowestStepArgument, StringComparison.Ordinal),
            "another preset applied after it ends it: Master Quality is back at -compression_level 100");

        var other = FastProxyOn("hevc_nvenc");
        other.SelectedPreset = BuiltIn("Small File 720p");
        expect(other.VideoEncoder.Name == "hevc_nvenc" && other.EncoderPreset == "p7" && !other.SpeedFirstHolds, "every other preset stays at the slowest step: Small File 720p after it is hevc_nvenc at p7");

        // The probe race: applied before the hardware probe answered, the encoder it resolves to then takes its fastest step, after the
        // default step the settings name for that encoder.
        var apply = typeof(MainViewModel).GetMethod("ApplyHardwareEncoders", Hidden)!;
        var (defaultEncoder, defaultStep) = (AppSettings.Current.DefaultVideoEncoder, AppSettings.Current.DefaultEncoderPreset);
        try
        {
            (AppSettings.Current.DefaultVideoEncoder, AppSettings.Current.DefaultEncoderPreset) = ("h264_nvenc", "p5");
            var early = Empty();
            early.SelectedPreset = BuiltIn("Fast Proxy");
            var before = (early.VideoEncoder.Name, early.EncoderPreset);
            apply.Invoke(early, [new List<string> { "h264_nvenc" }, new List<string>()]);
            expect(before == ("libx264", "ultrafast") && early.VideoEncoder.Name == "h264_nvenc" && early.EncoderPreset == "p1" && early.SpeedFirstHolds,
                "Fast Proxy picked before the probe answered: x264 at ultrafast at first, then h264_nvenc at p1 when the probe lists it (over the settings' default step, p5)");

            // A step picked by hand ends it: the probe then leaves the encoder's own best step.
            var picked = Empty();
            picked.SelectedPreset = BuiltIn("Fast Proxy");
            picked.EncoderPreset = "medium";
            apply.Invoke(picked, [new List<string> { "h264_qsv" }, new List<string>()]);
            expect(!picked.SpeedFirstHolds && picked.VideoEncoder.Name == "h264_qsv" && picked.EncoderPreset == "veryslow",
                "a step picked by hand ends it: the probe's h264_qsv then starts at its slowest step (veryslow), not its fastest");
        }
        finally
        {
            (AppSettings.Current.DefaultVideoEncoder, AppSettings.Current.DefaultEncoderPreset) = (defaultEncoder, defaultStep);
        }

        var byHand = FastProxyOn("h264_nvenc");
        byHand.EncoderPreset = "p4";
        var handArgs = ExportState(byHand).VideoCodecArguments;
        var encoderByHand = FastProxyOn("h264_nvenc");
        encoderByHand.VideoEncoder = encoderByHand.VideoEncoders.First(e => e.Name == "libx264");
        expect(!byHand.SpeedFirstHolds && handArgs.Contains("-preset p4", StringComparison.Ordinal)
               && !encoderByHand.SpeedFirstHolds && encoderByHand.EncoderPreset == "veryslow",
            "a pick by hand ends it: p4 chosen stays p4; x264 chosen in place of NVENC starts at veryslow");

        // The software retry of a hardware failure: a vendor's fastest step retries at ultrafast, every other at veryslow.
        (string Command, string Expected)[] retries =
        [
            ("ffmpeg -i in.mp4 -c:v h264_nvenc -preset p1 -rc vbr -cq 28 -b:v 0 -tune hq out.mp4", "ffmpeg -i in.mp4 -c:v libx264 -preset ultrafast -crf 28 out.mp4"),
            ("ffmpeg -i in.mp4 -c:v h264_qsv -preset veryfast -q:v 28 out.mp4", "ffmpeg -i in.mp4 -c:v libx264 -preset ultrafast -crf 28 out.mp4"),
            ("ffmpeg -i in.mp4 -c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -usage transcoding out.mp4", "ffmpeg -i in.mp4 -c:v libx264 -preset ultrafast -crf 28 out.mp4"),
            ("ffmpeg -i in.mp4 -c:v h264_mf -rate_control quality -quality 45 -compression_level 0 -hw_encoding 1 -pix_fmt nv12 out.mp4",
                "ffmpeg -i in.mp4 -c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p out.mp4"),
            ("ffmpeg -i in.mp4 -c:v hevc_nvenc -preset p7 -rc vbr -cq 20 -b:v 0 out.mp4", "ffmpeg -i in.mp4 -c:v libx265 -preset veryslow -crf 20 out.mp4"),
            ("ffmpeg -i in.mp4 -c:v h264_mf -rate_control quality -quality 45 -compression_level 100 -hw_encoding 1 out.mp4", "ffmpeg -i in.mp4 -c:v libx264 -preset veryslow -crf 28 out.mp4"),
        ];
        foreach (var (command, wanted) in retries)
        {
            var rewrote = HardwareFallback.TryRewrite(command, out var rewritten, out var hardware, out var softwareName);
            text.Append("   ").Append(hardware).Append(" -> ").Append(softwareName).Append(": ").Append(rewritten).Append('\n');
            expect(rewrote && rewritten == wanted, $"the retry of {hardware} at {(wanted.Contains("ultrafast", StringComparison.Ordinal) ? "its fastest step is ultrafast" : "its slowest step is veryslow")}");
        }

        var window = Read("ViewModels", "MainViewModel.EncodeAndLoad.cs");
        var retry = window[window.IndexOf("VideoEncoders.FirstOrDefault(e => e.Name == software) is { } replacement", StringComparison.Ordinal)..];
        retry = retry[..retry.IndexOf("retry = CommandPreview;", StringComparison.Ordinal)];
        expect(retry.Contains("PlaceSoftwareRetry(replacement, command);", StringComparison.Ordinal),
            "the Export tab follows a retry: the software encoder is put in place (PlaceSoftwareRetry) with the failed command");

        // 5.4D review (R13): one rule in both retry paths. A vendor's fastest step in the failed command retries at the software encoder's
        // fastest, speed-first or picked by hand; every other step at the slowest. The Export tab's retry is HandPeg's pick, not one by hand.
        var place = typeof(MainViewModel).GetMethod("PlaceSoftwareRetry", Hidden)!;
        expect(HardwareFallback.HadFastestStep(" -c:v h264_nvenc -preset p1 -cq 28") && HardwareFallback.HadFastestStep(" -c:v h264_qsv -preset veryfast")
               && HardwareFallback.HadFastestStep(" -c:v h264_amf -quality speed") && HardwareFallback.HadFastestStep(" -c:v h264_mf -compression_level 0 -hw_encoding 1")
               && !HardwareFallback.HadFastestStep(" -c:v h264_nvenc -preset p7") && !HardwareFallback.HadFastestStep(" -c:v h264_qsv -preset veryslow")
               && !HardwareFallback.HadFastestStep(" -c:v h264_amf -quality high_quality") && !HardwareFallback.HadFastestStep(" -c:v h264_mf -compression_level 100"),
            "HadFastestStep: p1, veryfast, speed and -compression_level 0 are a vendor's fastest step; p7, veryslow, high_quality and -compression_level 100 are not");
        foreach (var (step, failed, wanted) in new[]
                 {
                     ("p1", "ffmpeg -i in.mp4 -c:v h264_nvenc -preset p1 -rc vbr -cq 28 -b:v 0 out.mp4", "ultrafast"),
                     ("p7", "ffmpeg -i in.mp4 -c:v h264_nvenc -preset p7 -rc vbr -cq 28 -b:v 0 out.mp4", "veryslow"),
                 })
        {
            var hand = Empty();
            hand.VideoEncoders.Add(EncoderOption.FromHardwareName("h264_nvenc", qualcommAdreno: false));
            hand.VideoEncoder = hand.VideoEncoders.First(e => e.Name == "h264_nvenc");
            hand.EncoderPreset = step;
            var handFirst = hand.SpeedFirstHolds;
            HardwareFallback.TryRewrite(failed, out var rewritten, out _, out _);
            place.Invoke(hand, [hand.VideoEncoders.First(e => e.Name == "libx264"), failed]);
            text.Append("   NVENC at ").Append(step).Append(" by hand, failed: the Export tab retries at ").Append(hand.EncoderPreset)
                .Append(", the rewritten command at ").Append(rewritten.Contains("-preset ultrafast", StringComparison.Ordinal) ? "ultrafast" : "veryslow").Append('\n');
            expect(!handFirst && !hand.SpeedFirstHolds && hand.VideoEncoder.Name == "libx264" && hand.EncoderPreset == wanted
                   && rewritten.Contains($"-preset {wanted}", StringComparison.Ordinal),
                $"NVENC picked by hand at {step} (not speed-first) fails: the Export tab retries x264 at {wanted}, as the rewritten command does");
        }

        var speedFirst = FastProxyOn("h264_nvenc");
        place.Invoke(speedFirst, [speedFirst.VideoEncoders.First(e => e.Name == "libx264"), "ffmpeg -i in.mp4 -c:v h264_nvenc -preset p1 out.mp4"]);
        expect(speedFirst.SpeedFirstHolds && speedFirst.EncoderPreset == "ultrafast",
            "Fast Proxy's NVENC fails: the Export tab retries x264 at ultrafast, and the speed-first state holds (HandPeg's pick, not one by hand)");

        // 5.4D review (R11): + New Project puts the encoding settings back to the defaults, the slowest step; a Fast Proxy before it ends.
        var proxied = FastProxyOn("h264_mf", adreno: true);
        var proxiedFirst = proxied.SpeedFirstHolds;
        var started = false;
        Quietly((_, _) => started = proxied.NewProject())(expect, text);
        var after = ExportState(proxied).VideoCodecArguments;
        text.Append("   Fast Proxy on h264_mf, then + New Project: ").Append(after).Append('\n');
        expect(proxiedFirst && started && !proxied.SpeedFirstHolds && proxied.VideoEncoder.Name == "h264_mf" && proxied.SelectedPreset is null
               && after.Contains(MediaFoundationEncoder.SlowestStepArgument, StringComparison.Ordinal) && !after.Contains(MediaFoundationEncoder.FastestStepArgument, StringComparison.Ordinal),
            "Fast Proxy on h264_mf, then + New Project: the speed-first state ends and the new project exports at -compression_level 100");
        var defaults = Method(Read("ViewModels", "MainViewModel.Media.cs"), "private void ApplyEncodingDefaults()");
        expect(defaults.Contains("_speedFirst = false;", StringComparison.Ordinal),
            "the encoding defaults (launch, + New Project, a style preset when they override styles) always end a speed-first state");
    }

    // ----- drop-media-bin-blank (B-4) -----

    private static void DropOnBlank(Action<bool, string> expect, StringBuilder text)
    {
        expect(MainWindow.RouteOf(false, false, false, false, false, false, false, ["C:/clips/a.mp4"], onMediaBin: true) == MainWindow.DropRoute.AddToProjectMedia
               && MainWindow.RouteOf(false, false, false, false, false, false, false, ["C:/clips/a.mp4"], onMediaBin: false) == MainWindow.DropRoute.OpenOrReplace,
            "with nothing open, a video let go over the Media Browser goes into Project Media; anywhere else it is opened, as before");

        var vm = Empty();
        var nothingBefore = vm.HoldsNothingForTab;
        var pooled = vm.AddToProjectMedia(["D:/media/first.mp4", "D:/media/voice.wav"]);
        expect(nothingBefore && pooled == 2 && !vm.HasSource && !vm.HoldsNothingForTab
               && !MainWindow.StripHidden(1, false, vm.HoldsNothingForTab, vm.CurrentProjectPath, false),
            "the blank window's bin takes them (one step of Undo), with no video open; the tab now holds something, so the strip shows it and a tab switch keeps it");

        // 5.4D review (R12): a project dropped on that window opens in a tab of its own (OpenProjectInTabAsync, as File > Open), never in
        // place of the pool; on a window that holds nothing it is still opened there.
        string[] project = ["C:/clips/edit.hproj"], bundle = ["C:/clips/edit.hpb"];
        static MainWindow.DropRoute Blank(string[] files, bool holds, bool busy = false, bool onMediaBin = false) =>
            MainWindow.RouteOf(false, false, busy, false, false, false, false, files, onMediaBin, holdsSomething: holds);
        expect(Blank(project, !vm.HoldsNothingForTab) == MainWindow.DropRoute.OpenProject && Blank(bundle, true) == MainWindow.DropRoute.OpenProject
               && Blank(project, true, onMediaBin: true) == MainWindow.DropRoute.OpenProject,
            "a project or a bundle dropped on the blank window that holds pooled files (on the player or the bin): opened in a Project Tab of its own");
        expect(Blank(project, false) == MainWindow.DropRoute.OpenOrReplace && Blank(project, true, busy: true) == MainWindow.DropRoute.OpenOrReplace
               && Blank(["C:/clips/a.mp4"], true) == MainWindow.DropRoute.OpenOrReplace,
            "a window holding nothing opens the project in place, as before; while busy the open path (which says to wait); a video still opens as the source");
        var drop = Read("MainWindow.Drops.cs");
        expect(drop.Contains("holdsSomething: !_viewModel.HoldsNothingForTab", StringComparison.Ordinal),
            "the window's drop asks the tab in front whether it holds something (MainViewModel.HoldsNothingForTab)");

        typeof(MainViewModel).GetMethod("ResetForNewSource", Hidden)!.Invoke(vm, null);
        Place(vm, "D:/media/game.mp4", Probe());
        expect(string.Join(", ", vm.Document.MediaPool) == "D:/media/first.mp4, D:/media/voice.wav, D:/media/game.mp4" && vm.ClipsUsing("D:/media/game.mp4") == 1,
            "the first file opened later (a new source, then its clip placed) keeps the pool: the two pooled files, then the opened one's");
        var load = Method(Read("ViewModels", "MainViewModel.EncodeAndLoad.cs"), "private async Task<(bool Loaded, string Message)> LoadMediaAsync(");
        expect(load.Length > 0 && !load.Contains("InstallDocument", StringComparison.Ordinal) && !load.Contains("MediaPool", StringComparison.Ordinal),
            "opening a file (LoadMediaAsync) never puts a new document in place nor touches the pool");
        var menu = Method(Read("Views", "MediaBrowserPane.ProjectMedia.cs"), "private void AddProjectMediaItems(");
        expect(menu.Length > 0 && !menu.Contains("HasVideo", StringComparison.Ordinal) && !menu.Contains("IsEnabled", StringComparison.Ordinal),
            "the menu's Add to Project Media (and Remove) is no longer greyed out with nothing open");
    }

    // ----- project-media-remove-with-clips (B-5) -----

    private static MainViewModel WithClips()
    {
        var vm = Empty();
        Place(vm, "D:/media/game.mp4", Probe());
        using var tx = vm.Document.BeginTransaction("more clips");
        var track = tx.InsertTrack(0, TimelineTrackKind.Video);
        tx.PlaceClip(track, Video("D:/media/game.mp4", 70, 75));
        tx.PlaceClip(track, Video("D:/media/other.mp4", 80, 85));
        tx.Commit();
        return vm;
    }

    private static void RemoveWithClips(Action<bool, string> expect, StringBuilder text)
    {
        // Declined: refused as before.
        var vm = WithClips();
        (string Title, string Message, string Confirm) asked = default;
        vm.Confirm = (title, message, confirm) =>
        {
            asked = (title, message, confirm);
            return false;
        };
        var steps = StepsOf(vm).Count;
        text.Append(FormattableString.Invariant($"   on the Sequence: {vm.ClipsUsing("D:/media/game.mp4")} clips of game.mp4, {vm.ClipsUsing("D:/media/other.mp4")} of other.mp4; ")
            + string.Join(" | ", vm.Document.Tracks.Select(t => string.Join(", ", t.Clips.Select(c => FormattableString.Invariant($"{Path.GetFileName(c.SourcePath)} {c.Start:0.##}-{c.End:0.##}{(c.ParentClipId is null ? "" : " (child)")}"))))) + "\n");
        var declined = vm.RemoveFromProjectMedia(["D:/media/game.mp4"]);
        text.Append("   asked: ").Append(asked.Title).Append(" | ").Append(asked.Message).Append(" | ").Append(asked.Confirm).Append('\n');
        expect(declined == 0 && StepsOf(vm).Count == steps && vm.Document.PoolHolds("D:/media/game.mp4") && vm.ClipsUsing("D:/media/game.mp4") == 2
               && vm.StatusText.Contains("2 clips on the Sequence play it", StringComparison.Ordinal),
            "declined: nothing changes, no step, and the status line says how many clips play it (as before)");
        expect(asked.Title == "Remove from Project Media" && asked.Confirm == "Remove from Project Media & Delete 2 Clips" && asked.Message?.Contains("game.mp4", StringComparison.Ordinal) == true,
            "the question names the file, and its button says \"Remove from Project Media & Delete 2 Clips\"");

        // Confirmed: one step deletes exactly those clips (into the recycle bin) and takes the file out.
        vm.Confirm = (_, _, _) => true;
        var bin = vm.RecycleBin.Count;
        steps = StepsOf(vm).Count;
        var removed = vm.RemoveFromProjectMedia(["D:/media/game.mp4"]);
        var label = StepsOf(vm).Count > 0 ? StepsOf(vm)[^1].What : "";
        text.Append(FormattableString.Invariant($"   confirmed: {removed} file out, {StepsOf(vm).Count - steps} step (\"{label}\"), {vm.ClipsUsing("D:/media/game.mp4")} clips of it left, {vm.ClipsUsing("D:/media/other.mp4")} of the other, {vm.RecycleBin.Count - bin} in the recycle bin, pooled: {string.Join(", ", vm.Document.MediaPool)}\n"));
        expect(removed == 1 && StepsOf(vm).Count == steps + 1 && label == "remove game.mp4 from Project Media and delete 2 clips"
               && !vm.Document.PoolHolds("D:/media/game.mp4") && vm.ClipsUsing("D:/media/game.mp4") == 0 && vm.ClipsUsing("D:/media/other.mp4") == 1
               && vm.Document.PoolHolds("D:/media/other.mp4") && vm.RecycleBin.Count == bin + 2,
            "confirmed: one step (\"remove game.mp4 from Project Media and delete 2 clips\") takes the file out and deletes its two clips into the recycle bin; the other file and its clip stay");
        vm.Undo();
        var undone = vm.Document.PoolHolds("D:/media/game.mp4") && vm.ClipsUsing("D:/media/game.mp4") == 2;
        vm.Redo();
        expect(undone && !vm.Document.PoolHolds("D:/media/game.mp4") && vm.ClipsUsing("D:/media/game.mp4") == 0, "Undo brings back the file and both clips; Redo takes them out again");

        // A question that defaults to Cancel asks first when the window gives one; one clip is "Clip".
        var one = Empty();
        Place(one, "D:/media/solo.mp4", Probe());
        var (warned, confirmText) = (false, "");
        one.Confirm = (_, _, _) => true;
        one.ConfirmWarning = (_, _, confirm) =>
        {
            (warned, confirmText) = (true, confirm);
            return false;
        };
        expect(one.RemoveFromProjectMedia(["D:/media/solo.mp4"]) == 0 && warned && confirmText == "Remove from Project Media & Delete 1 Clip" && one.ClipsUsing("D:/media/solo.mp4") == 1,
            "the window's question (ConfirmWarning, Cancel the default) is asked in place of Confirm; one clip says \"Delete 1 Clip\"");
        var wiring = Method(Read("MainWindow.MediaBrowser.cs"), "private void InitializeMediaBrowser()");
        expect(wiring.Contains("_viewModel.ConfirmWarning = ", StringComparison.Ordinal) && wiring.Contains("DefaultsToCancel = true", StringComparison.Ordinal),
            "the window asks it with the ConfirmDialog, Cancel the button Enter presses");

        // A locked track refuses the whole step.
        var locked = Empty();
        Place(locked, "D:/media/held.mp4", Probe());
        locked.Confirm = (_, _, _) => true;
        foreach (var track in locked.Document.Tracks.Where(t => t.Clips.Count > 0))
            track.IsLocked = true;
        var lockedSteps = StepsOf(locked).Count;
        expect(locked.RemoveFromProjectMedia(["D:/media/held.mp4"]) == 0 && StepsOf(locked).Count == lockedSteps && locked.Document.PoolHolds("D:/media/held.mp4")
               && locked.ClipsUsing("D:/media/held.mp4") == 1 && locked.StatusText.Contains("is locked", StringComparison.Ordinal),
            "a clip on a locked track refuses the whole step: the file and its clip stay, and the status line says the track is locked");
    }

    // ----- subtitle-editor-flag-gone (B-6) -----

    private static void FlagGone(Action<bool, string> expect, StringBuilder text)
    {
        expect(typeof(MainViewModel).GetProperty("IsSubtitleEditorOpen") is null && typeof(MainViewModel).GetField("_isSubtitleEditorOpen", Hidden) is null
               && typeof(MainViewModel).GetField("_subtitleVisitStep", Hidden) is null && typeof(MainViewModel).GetMethod("BeginSubtitleVisit", Hidden) is null,
            "the view model has no IsSubtitleEditorOpen, nor the modal editor's one-step visit");
        var timeline = Read("ViewModels", "MainViewModel.Timeline.cs");
        var captions = Read("ViewModels", "MainViewModel.Captions.cs");
        var subtitles = Read("ViewModels", "MainViewModel.Subtitles.cs");
        expect(!timeline.Contains("Close the Subtitle Studio first", StringComparison.Ordinal) && !Method(captions, "GpuCaptions()").Contains("SubtitleEditor", StringComparison.Ordinal)
               && !subtitles.Contains("InSubtitleVisit", StringComparison.Ordinal) && !(Read("ViewModels", "MainViewModel.cs") + Read("ViewModels", "MainViewModel.EncodeAndLoad.cs")).Contains("nameof(IsSubtitleEditorOpen)", StringComparison.Ordinal),
            "Undo has no \"Close the Subtitle Studio first\", the captions no editor guard, and NonCommandProperties no entry for it");
    }

    // ----- title-rail-game-presets (B-7, done before the wave) -----

    private static void TitleRail(Action<bool, string> expect, StringBuilder text)
    {
        var xaml = XDocument.Load(Path.Combine(Root(), "src", "MainWindow.xaml"));
        var progress = Named(xaml, "TitleProgress");
        var rail = Named(xaml, "TitleProgressRail");
        var fill = Named(xaml, "TitleProgressFill");
        var tabs = Named(xaml, "ProjectTabStrip");
        expect(progress is not null && Attr(progress, "Panel.ZIndex") == "10" && Attr(progress, "IsHitTestVisible") == "False" && tabs is not null
               && progress.Parent == tabs.Parent,
            "TitleProgress lies over the project tabs (Panel.ZIndex 10, in the same title-bar grid) and takes no pointer (IsHitTestVisible False)");
        expect(rail is not null && rail.Parent == progress && Attr(rail, "Height") == "2" && Attr(rail, "Canvas.Bottom") == "0" && Attr(rail, "Canvas.Left") == "0"
               && fill is not null && Attr(fill, "Height") == "{StaticResource CaptionBarHeight}" && Attr(fill, "Opacity") == "0.16",
            "TitleProgressRail is a 2 DIP accent line on the bar's bottom edge; the wash fills the bar's height at 0.16");
        double Constant(string name) => (double)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var update = Method(Read("MainWindow.TitleBar.cs"), "private void UpdateTitleProgress()");
        expect(Constant("TitleProgressStrength") == 0.16 && Constant("TitleProgressFaint") == 0.08
               && update.Contains("TitleProgressRail.Width = TitleProgressFill.Width", StringComparison.Ordinal),
            "the wash is 0.16 (0.08 while the work's length is unknown), and the rail follows the fill's width");

        var panel = Named(xaml, "SettingsFlyoutPanel");
        var button = panel?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button" && Attr(e, "Click") == "FlyoutGamePresets_Click");
        var handler = Method(Read("MainWindow.xaml.cs"), "private void FlyoutGamePresets_Click(");
        expect(button is not null && Attr(button, "AutomationProperties.Name") == "Game Presets" && (Attr(button, "Content") ?? "").Contains("Game Presets", StringComparison.Ordinal)
               && handler.Contains("ManageStyles_Click(UnifiedTimeline, e)", StringComparison.Ordinal) && handler.Contains("_settingsHover.Close()", StringComparison.Ordinal),
            "the Settings flyout has the Game Presets button (\"Save, Import or Export Game Presets...\"); it closes the flyout and runs ManageStyles_Click");
    }
}
