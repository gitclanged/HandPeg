using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Shell;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// The window's controllers and seams: shell-controllers (WindowFrame, the heat lamp, the Live Encoded Preview, the Fullscreen Preview
/// and the drop router in src/Shell, owning their state and timers); clip-pictures-keyed (one keyed store of the Timeline's pictures, no
/// picture on a model); numeric-edge-kind (the loupe's edge declared on each control, no class-wide binding reflection); ui-brushes-glyphs
/// (one Frozen helper, one wipe handle, named glyphs); key-tip-progressive (name and keys first, the description after 1 s, twelve words at
/// most); light-theme-contrast; effects-pane-attach (Detach mirrors Attach); native-one-declaration (one declaration per Win32 function, COM
/// slots by name); engine-teardown-off-ui.
/// </summary>
public static class ShellArchitectureTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("shell-controllers", "# MainWindow's feature state in controllers under src/Shell: the frame, the heat lamp, the Live Encoded Preview, the Fullscreen Preview, the drop router; each owns its timers.", Controllers);
        yield return Case("clip-pictures-keyed", "# The Timeline's strips and waveforms in one keyed store (ClipPictures): shared by file, kept while used, the rest bounded; no model holds a picture.", ClipPicturesKeyed);
        yield return Case("numeric-edge-kind", "# The loupe's edge declared on each control (NumericInput.EdgeKind); drags exempt from holding the boxes declared too (DragHold.Exempt).", EdgeKind);
        yield return Case("ui-brushes-glyphs", "# One Frozen helper (UiBrushes), one split-wipe handle for both wipes, and icons by name (Glyphs): no raw private-use glyph in source.", BrushesAndGlyphs);
        yield return Case("key-tip-progressive", "# KeyTips in two stages: the name and keys at once, the description after 1.0 s of hover, twelve words at most.", KeyTipProgressive);
        yield return Case("light-theme-contrast", "# Text drawn by code reads on a light theme: the Cutout shortcuts card, the keyframe lanes, the keybind map's accent keys and conflicts.", LightTheme);
        yield return Case("effects-pane-attach", "# The Effects pane's Detach takes off every handler Attach and its target hooked; a hidden pane draws nothing until shown.", EffectsPaneAttach);
        yield return Case("native-one-declaration", "# One declaration per Win32 function (src/Native, overloads beside each other); every COM call names its vtable slot.", NativeOnce);
        yield return Case("engine-teardown-off-ui", "# The GPU engine is torn down on the pool for a Reload or a fault (the next start waits for it); the window's close stays at once.", EngineTeardown);
    }

    private static string Src(params string[] parts) => SourceProbe.Src(parts);

    // ----- shell-controllers -----

    private static void Controllers(Action<bool, string> expect)
    {
        var shell = Path.Combine(SourceProbe.Root, "src", "Shell");
        string[] controllers = ["WindowFrame.cs", "HeatLampController.cs", "LoopbackPreviewController.cs", "FullscreenController.cs", "DropRouter.cs"];
        expect(controllers.All(c => File.Exists(Path.Combine(shell, c))), "the five live in src/Shell");
        expect(!new[] { "MainWindow.HeatLamp.cs", "MainWindow.LoopbackPreview.cs", "MainWindow.Fullscreen.cs" }.Any(f => File.Exists(Path.Combine(SourceProbe.Root, "src", f))),
            "and the partial files they came from are gone");

        var code = controllers.Skip(1).ToDictionary(c => c, c => HotPathTests.WithoutComments(File.ReadAllText(Path.Combine(shell, c))));
        expect(code.Values.All(c => !c.Contains("new DispatcherTimer(", StringComparison.Ordinal) && c.Contains("TimerScope", StringComparison.Ordinal)),
            "each controller makes its timers through its own TimerScope (Pacing.cs), never a raw DispatcherTimer");
        var titleBar = HotPathTests.WithoutComments(Src("MainWindow.TitleBar.cs"));
        expect(!titleBar.Contains("LibraryImport", StringComparison.Ordinal) && !titleBar.Contains("static IntPtr FrameHook", StringComparison.Ordinal)
               && titleBar.Contains("WindowFrame.FrameHook(hwnd, message, wParam, lParam, ref handled)", StringComparison.Ordinal),
            "the title bar keeps only its own buttons and hook; the frame's messages, taskbar and hit test are WindowFrame's");
        expect(typeof(WindowFrame) is { IsAbstract: true, IsSealed: true }, "WindowFrame is a static class: no window's state, one hook for every window that draws its frame");

        var window = HotPathTests.WithoutComments(Src("MainWindow.xaml.cs"));
        expect(window.Contains("_heatLamp ??= new Shell.HeatLampController(", StringComparison.Ordinal) && window.Contains("System.Windows.Threading.DispatcherPriority.ApplicationIdle", StringComparison.Ordinal),
            "the heat lamp is made at idle after Frame 1 (Law 1)");
        var fullscreen = SourceProbe.Member(HotPathTests.WithoutComments(File.ReadAllText(Path.Combine(shell, "FullscreenController.cs"))), "public FullscreenController(FullscreenHost host)");
        expect(fullscreen.Contains("ContentRendered", StringComparison.Ordinal) && !fullscreen.Contains("PreviewMouseMove", StringComparison.Ordinal),
            "the Fullscreen Preview hooks only the first frame when made; its HUD hooks follow at idle");

        // The drop router's own parsing: files, a link in a browser's format, nothing.
        var router = new DropRouter(new MainViewModel(), new Window(), new DropOverlayParts(new Grid(), new ItemsControl(), new ItemsControl(), new TextBlock(), new Border(), new Border(), new Border(), () => { }), () => null);
        var files = new DataObject(DataFormats.FileDrop, new[] { @"D:\clips\a.mp4", @"D:\clips\b.mp4" });
        var link = new DataObject(DataFormats.UnicodeText, "https://www.youtube.com/watch?v=abc\nA title");
        var words = new DataObject(DataFormats.UnicodeText, "just words");
        expect(router.Dragged(files) == (@"D:\clips\a.mp4", null) && router.Dragged(link) == (null, "https://www.youtube.com/watch?v=abc") && !router.CanOpen(words),
            "the router reads a drag's first file, a link's first line, and refuses plain words");
        expect(DropRouter.RouteOf(true, true, false, false, false, false, false, [@"D:\clips\a.mp4"]) == DropRoute.AddAtPlayhead && DropRouter.IsProjectFile("x.hproj"),
            "and its routing policy is the one the drops follow (clips on the Sequence: at the playhead)");
    }

    // ----- clip-pictures-keyed -----

    private static void ClipPicturesKeyed(Action<bool, string> expect)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\a.mp4" };
        var pictures = new ClipPictures(2, () => used);
        var changes = 0;
        pictures.Changed += () => changes++;
        static BitmapSource Pixel()
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            return bitmap;
        }

        var (strip, sound, stream2) = (Pixel(), Pixel(), Pixel());
        pictures.PutFilm(@"D:\a.mp4", strip);
        pictures.PutSound(@"D:\a.mp4", 0, sound);
        pictures.PutSound(@"D:\a.mp4", 2, stream2);
        expect(ReferenceEquals(pictures.Film(@"d:\A.MP4"), strip) && ReferenceEquals(pictures.Sound(ClipPictures.SoundKey(@"D:\a.mp4", 0)), sound)
               && ReferenceEquals(pictures.Sound(ClipPictures.SoundKey(@"D:\a.mp4", 2)), stream2) && ClipPictures.SoundKey(@"D:\a.mp4", 2) == @"D:\a.mp4|a2",
            "a file's strip by its path (any case), each audio stream's waveform by its key (the file for stream 0)");
        expect(changes == 3 && pictures.OfFile(@"D:\a.mp4").Count() == 3, "each picture stored says so (the Timeline draws again, no polling), and a tab's estimate finds all three");

        // Over the bound: pictures of used files stay, the oldest of the unused go until two are left.
        for (var i = 0; i < 4; i++)
            pictures.PutFilm($@"D:\old{i}.mp4", Pixel());
        expect(pictures.Film(@"D:\a.mp4") is not null && pictures.Film(@"D:\old0.mp4") is null && pictures.Film(@"D:\old1.mp4") is null
               && pictures.Film(@"D:\old2.mp4") is not null && pictures.Film(@"D:\old3.mp4") is not null,
            "over its bound the oldest unused pictures go; a used file's never");
        used.Clear();
        expect(pictures.Release() == 3 && pictures.Count == 0, "Release (Empty Bin) drops every picture of a file no Sequence uses");
        pictures.OtherFilesInUse = () => [@"D:\tab.mp4"];
        pictures.PutFilm(@"D:\tab.mp4", Pixel());
        expect(pictures.Release() == 0 && pictures.Film(@"D:\tab.mp4") is not null, "a file a background tab uses keeps its pictures");

        string[] gone = ["Waveform", "Filmstrip", "TrackWaveform"];
        expect(gone.All(name => typeof(Layer).GetProperty(name) is null) && typeof(AudioTrack).GetProperty("Waveform") is null && typeof(AudioTrack).GetProperty("TrackWaveform") is null,
            "no Layer or AudioTrack carries a picture any more");
        var vm = new MainViewModel();
        expect(vm.ClipPictures is not null && vm.SourceWaveforms.Count == 0 && typeof(MainViewModel).GetField("_layerFilmstrips", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "the view model owns the store (and the opened file's track waveforms as a list), its old per-file strip cache folded in");
    }

    // ----- numeric-edge-kind -----

    private static void EdgeKind(Action<bool, string> expect)
    {
        var layer = new Layer();
        var told = new List<(FrameworkElement Control, EdgeTuningKind Kind, bool Held)>();
        void Heard(FrameworkElement control, EdgeTuningKind kind, bool held) => told.Add((control, kind, held));
        NumericInput.EdgeHeld += Heard;
        try
        {
            var marked = new Slider { DataContext = layer };
            NumericInput.SetEdgeKind(marked, EdgeTuningKind.Mask);
            var plain = new Slider { DataContext = layer };
            foreach (var slider in new[] { marked, plain })
            {
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            }

            expect(told.Count == 2 && told.All(t => ReferenceEquals(t.Control, marked) && t.Kind == EdgeTuningKind.Mask) && told[0].Held && !told[1].Held,
                "a slider that declares its edge says when it is held and let go; one that declares none says nothing");
        }
        finally
        {
            NumericInput.EdgeHeld -= Heard;
        }

        var exempt = new Border { Child = new StackPanel() };
        DragHold.SetExempt(exempt, true);
        var thumb = new Thumb();
        ((StackPanel)exempt.Child).Children.Add(thumb);
        expect(DragHold.GetExempt(thumb) && !DragHold.GetExempt(new Thumb()), "DragHold.Exempt is inherited by the thumbs under the element that sets it");

        var layout = HotPathTests.WithoutComments(Src("MainWindow.PlayerLayout.cs"));
        expect(!layout.Contains("RegisterClassHandler(typeof(Slider)", StringComparison.Ordinal) && !layout.Contains("GetBindingExpression", StringComparison.Ordinal)
               && layout.Contains("NumericInput.EdgeHeld += HoldBoundEdge;", StringComparison.Ordinal) && layout.Contains("DragHold.GetExempt(thumb)", StringComparison.Ordinal),
            "the window listens to the declared edges only: no class handler on every slider, no binding-path reflection, no seek-bar identity test");
        var dialogs = Src("Dialogs", "LayerPropertiesDialog.xaml") + Src("Dialogs", "LayerToolDialog.xaml");
        var marks = Regex.Matches(dialogs, "NumericInput\\.EdgeKind=\"(Feather|Mask|ChromaKey)\"").Count;
        expect(marks == 18, "the 9 edge sliders of Layer Properties and the mask tools and their 9 value boxes declare their edge" + (marks == 18 ? "" : $" ({marks})"));
        expect(Src("App.xaml").Contains("<Setter Property=\"local:DragHold.Exempt\" Value=\"True\" />", StringComparison.Ordinal)
               && Src("MainWindow.xaml").Contains("x:Name=\"TimelineSlider\" local:DragHold.Exempt=\"True\"", StringComparison.Ordinal),
            "every scroll bar (its style) and the seek bar are exempt");
    }

    // ----- ui-brushes-glyphs -----

    private static void BrushesAndGlyphs(Action<bool, string> expect)
    {
        var src = Path.Combine(SourceProbe.Root, "src");
        var files = Directory.GetFiles(src, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();
        var raw = files.Where(f => Path.GetFileName(f) != "CrashSanitizer.cs" && File.ReadAllText(f).Any(c => c is >= '\uE000' and <= '\uF8FF')).Select(Path.GetFileName).ToList();
        expect(raw.Count == 0, "no raw private-use glyph in source but CrashSanitizer's markers" + (raw.Count == 0 ? "" : ": " + string.Join(", ", raw)));
        var glyphs = typeof(Glyphs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        expect(glyphs.Count >= 30 && glyphs.All(g => g.Length == 1 && g[0] is >= '\uE000' and <= '\uF8FF') && glyphs.Distinct().Count() == glyphs.Count,
            "Glyphs names each icon once, each one Segoe MDL2 code point");

        var helpers = files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}Services{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}ShellThumbnails{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && Path.GetFileName(f) != "UiBrushes.cs")
            .Where(f => Regex.IsMatch(HotPathTests.WithoutComments(File.ReadAllText(f)), @"private\s+static\s+\w+\s+Frozen\((Color|byte|Brush)\b")).Select(Path.GetFileName).ToList();
        expect(helpers.Count == 0, "no private Frozen(Color) copy outside Services: UiBrushes.Frozen" + (helpers.Count == 0 ? "" : ": " + string.Join(", ", helpers)));
        var brush = UiBrushes.Frozen(0x10, 0x20, 0x30);
        var handle = UiBrushes.WipeHandle("tip", "Name");
        expect(brush.IsFrozen && brush.Color == Color.FromRgb(0x10, 0x20, 0x30) && handle is { Width: 18, Child: Border { Width: 18, Height: 34, Child: TextBlock { Text: Glyphs.GripperBarHorizontal } } },
            "Frozen brushes are frozen; the wipe handle is an 18-DIP column with the 18 x 34 grip");
        expect(SourceProbe.Member(Src("MainWindow.ColorStudio.cs"), "private void EnsureGradeWipe()").Contains("UiBrushes.WipeHandle(", StringComparison.Ordinal)
               && Src("Shell", "LoopbackPreviewController.cs").Contains("UiBrushes.WipeHandle(", StringComparison.Ordinal),
            "the Live Encoded Preview's wipe and the Color Studio's Before/After share it");
    }

    // ----- key-tip-progressive -----

    private static void KeyTipProgressive(Action<bool, string> expect)
    {
        expect(KeyTip.DetailDwellMs == 1000, "the description opens after 1.0 s of hover");
        expect(KeyTip.Split("Fit (F): the whole sequence.") == ("Fit (F)", "The whole sequence.") && KeyTip.Split("Go to Start (Home)") == ("Go to Start (Home)", "")
               && KeyTip.Split("Play / Pause (Space).") == ("Play / Pause (Space)", "") && KeyTip.Split("Timeline zoom: left shows it all.") == ("Timeline zoom", "Left shows it all.")
               && KeyTip.Split("A ripple edit (Ctrl-drag) moves them. Off: only its track.").Detail == "",
            "split at the first \": \" after a name (it ends with its keys, or is short with no parenthesis); a sentence stays whole");

        var button = new Button();
        KeyTip.SetText(button, "Fit{keys}: the whole sequence, with room after its end.");
        var card = KeyTip.Prepare(button) as ToolTip;
        var parts = card?.Content is StackPanel { Children: [TextBlock name, TextBlock detail] } ? (name, detail) : default;
        expect(parts.name?.Text.StartsWith("Fit", StringComparison.Ordinal) == true && parts.detail is { Visibility: Visibility.Collapsed, Text: "The whole sequence, with room after its end." },
            "first stage: the name and its keys, the description closed");
        KeyTip.Expand(card!);
        expect(parts.detail?.Visibility == Visibility.Visible, "second stage: the description open under them");
        var plain = new Button();
        KeyTip.SetText(plain, "Next Marker{keys}");
        expect(KeyTip.Prepare(plain) is string, "a name alone is one stage");

        var texts = Directory.GetFiles(Path.Combine(SourceProbe.Root, "src"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "KeyTip\\.Text=\"([^\"]*)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)))
            .Concat(Directory.GetFiles(Path.Combine(SourceProbe.Root, "src"), "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), "KeyTip\\.SetText\\([^,]+,\\s*\"([^\"]*)\"").Select(m => m.Groups[1].Value))).ToList();
        var long_ = texts.Select(KeyTip.Split).Where(s => s.Detail.Length > 0 && KeyTip.Words(s.Detail) > 12).Select(s => s.Name).ToList();
        expect(texts.Count >= 35 && long_.Count == 0, "every KeyTip's description is twelve words at most" + (long_.Count == 0 ? "" : ": " + string.Join(", ", long_)));
    }

    // ----- light-theme-contrast -----

    private static void LightTheme(Action<bool, string> expect)
    {
        expect(KeybindMapControl.OnAccent(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7))) == Brushes.White
               && KeybindMapControl.OnAccent(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4D))) != Brushes.White,
            "keys and layers drawn on the accent: white on a dark accent, dark on a light one");
        var list = new KeybindListControl(new KeybindDraft(KeybindManager.KnownPreset(null), null));
        list.Resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var onLight = ((SolidColorBrush)list.ConflictText).Color;
        list.Resources["TextBrush"] = Brushes.White;
        var onDark = ((SolidColorBrush)list.ConflictText).Color;
        expect(onLight == Color.FromRgb(0xB4, 0x23, 0x18) && onDark == Color.FromRgb(0xFF, 0x7A, 0x7A), "a conflicting key's text: deep red on a light theme, light red on a dark one");

        var shortcuts = Src("Dialogs", "MaskDrawWindow.Shortcuts.cs");
        expect(shortcuts.Contains("SetResourceReference(Border.BackgroundProperty, \"InspectorCardBrush\")", StringComparison.Ordinal) && !shortcuts.Contains("0x1E, 0x1E, 0x22", StringComparison.Ordinal),
            "the Cutout Studio's shortcuts card takes the theme's inspector card, as its text takes the theme's");
        var lanes = HotPathTests.WithoutComments(Src("Controls", "KeyLanesView.cs"));
        expect(!lanes.Contains("Color.FromArgb(220, 255, 255, 255)", StringComparison.Ordinal) && lanes.Contains("TryFindResource(\"TextBrush\")", StringComparison.Ordinal)
               && lanes.Contains("TryFindResource(\"MutedTextBrush\")", StringComparison.Ordinal),
            "the keyframe lanes' names, labels and lines are drawn in the theme's text colours, not white");
    }

    // ----- effects-pane-attach -----

    private static void EffectsPaneAttach(Action<bool, string> expect)
    {
        // The pane's XAML takes the flyout chrome from the application's resources (App.xaml, not loaded here): a stand-in for this case.
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var addedChrome = !app.Resources.Contains("FlyoutChrome");
        if (addedChrome)
            app.Resources["FlyoutChrome"] = new Style(typeof(ButtonChrome));
        try
        {
            EffectsPaneHooks(expect);
        }
        finally
        {
            if (addedChrome)
                app.Resources.Remove("FlyoutChrome");
        }
    }

    private static void EffectsPaneHooks(Action<bool, string> expect)
    {
        var vm = new MainViewModel();
        var pane = new EffectsPane();
        int Hooked()
        {
            int Count(Delegate? handlers) => handlers?.GetInvocationList().Count(d => ReferenceEquals(d.Target, pane)) ?? 0;
            var changed = typeof(ObservableObject).GetField(nameof(ObservableObject.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm) as Delegate;
            var layers = typeof(System.Collections.ObjectModel.ObservableCollection<Layer>).GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm.Layers) as Delegate;
            var target = typeof(MainViewModel).GetField(nameof(MainViewModel.EditTargetChanged), BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm) as Delegate;
            return Count(changed) + Count(layers) + Count(target);
        }

        pane.Attach(vm);
        var attached = Hooked();
        pane.Attach(vm);
        var again = Hooked();
        pane.Detach();
        var detached = Hooked();
        pane.Attach(vm);
        expect(attached == 3 && again == 3 && detached == 0 && Hooked() == 3, "Attach hooks the view model's three handlers once (again: no second set); Detach takes every one off");
        pane.Detach();

        var code = HotPathTests.WithoutComments(Src("Views", "EffectsPane.xaml.cs"));
        expect(SourceProbe.InOrder(SourceProbe.Member(code, "private void Unhook()"), "viewModel.PropertyChanged -= ViewModel_PropertyChanged;", "viewModel.Layers.CollectionChanged -= Layers_CollectionChanged;",
                   "viewModel.EditTargetChanged -= FollowSequenceOutput;", "_stack.CollectionChanged -= Stack_CollectionChanged;", "_layer.PropertyChanged -= Layer_PropertyChanged;")
               && code.Contains("Unloaded += (_, _) => Unhook();", StringComparison.Ordinal) && code.Contains("Loaded += (_, _) => Hook();", StringComparison.Ordinal),
            "the pane's hooks live while it is loaded: unloaded (closed, moved), it holds none");
        expect(SourceProbe.Member(code, "private void Rebuild()").Contains("if (HiddenNow)", StringComparison.Ordinal) && SourceProbe.Member(code, "private void OnPlayheadMoved()").Contains("if (HiddenNow)", StringComparison.Ordinal)
               && !Regex.IsMatch(Src("Views", "EffectsPane.Audio.cs") + Src("Views", "EffectsPane.Speed.cs") + code, @"_rebuildQueued|_refreshQueued"),
            "loaded but hidden it rebuilds and refreshes nothing until it shows; its queued runs are RunGates, not flags");
        expect(typeof(ReorderBehavior).GetEvent("Reordered", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is null
               && ReorderBehavior.ReorderedEvent.RoutingStrategy == RoutingStrategy.Bubble
               && code.Contains("AddHandler(ReorderBehavior.ReorderedEvent,", StringComparison.Ordinal),
            "card reorders reach the pane as a bubbling routed event on its own panels: no static event roots a pane ever made (BAD-03)");
        var playhead = SourceProbe.Member(code, "private void OnPlayheadMoved()");
        expect(!playhead.Contains("_rebuildWhenShown", StringComparison.Ordinal) && !SourceProbe.Member(code, "private void RefreshParams()").Contains("_rebuildWhenShown", StringComparison.Ordinal)
               && playhead.Contains("_refreshWhenShown |= _params.Any(model => model.Animated);", StringComparison.Ordinal)
               && SourceProbe.InOrder(code, "if (_rebuildWhenShown)", "Rebuild();", "else if (_refreshWhenShown)", "RefreshParams();"),
            "hidden playback or value changes only refresh the cards' values when shown (and only with animated values for playback); cards are rebuilt only when their set changed (BAD-03)");
    }

    // ----- native-one-declaration -----

    private static void NativeOnce(Action<bool, string> expect)
    {
        var src = Path.Combine(SourceProbe.Root, "src");
        var native = Path.Combine(src, "Native");
        var import = new Regex(@"\[(?:LibraryImport|DllImport)\(\s*""([^""]+)""([^\]]*)\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|static|extern|partial|unsafe)\s+)+[\w<>\[\]\*\?\.]+\s+(\w+)\s*\(");
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}ShellThumbnails{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match m in import.Matches(File.ReadAllText(file)))
            {
                var entry = Regex.Match(m.Groups[2].Value, @"EntryPoint\s*=\s*""(\w+)""") is { Success: true } e ? e.Groups[1].Value : m.Groups[3].Value;
                var key = m.Groups[1].Value.ToLowerInvariant().Replace(".dll", "", StringComparison.Ordinal) + "!" + (entry.EndsWith('W') && entry.Length > 2 && char.IsLower(entry[^2]) ? entry[..^1] : entry);
                (seen.TryGetValue(key, out var where) ? where : seen[key] = new HashSet<string>(StringComparer.Ordinal)).Add(file.StartsWith(native, StringComparison.Ordinal) ? "src/Native" : Path.GetFileName(file));
            }
        }

        var twice = seen.Where(p => p.Value.Count > 1).Select(p => $"{p.Key} ({string.Join(", ", p.Value)})").ToList();
        expect(seen.Count > 100 && twice.Count == 0, "no Win32 function is declared in two files (overloads sit together in src/Native)" + (twice.Count == 0 ? "" : ": " + string.Join("; ", twice)));
        var numeric = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => Regex.IsMatch(File.ReadAllText(f), @"Com\.Table\([^)]*\)\[\d+\]|\bT\[\d+\]\)\(")).Select(Path.GetFileName).ToList();
        expect(numeric.Count == 0, "every COM call names its slot (ComSlots.cs), none by a bare number" + (numeric.Count == 0 ? "" : ": " + string.Join(", ", numeric)));
        expect(HandPegApp.Compositor.Native.ID3D11DeviceContextSlot.Map == 14 && HandPegApp.Compositor.Native.IDXGISwapChainSlot.Present == 8
               && HandPegApp.Compositor.Native.IAudioClientSlot.GetService == 14 && HandPegApp.Compositor.Native.IDXGIDevice3Slot.Trim == 17
               && HandPegApp.Compositor.Native.IDXGIFactory6Slot.EnumAdapterByGpuPreference == 29,
            "the named slots are the SDK's: Map 14, Present 8, GetService 14, Trim 17, EnumAdapterByGpuPreference 29");
        var kernel = Src("Native", "Kernel32.cs");
        expect(kernel.Contains("public static partial SafeWaitHandle OpenProcess(", StringComparison.Ordinal) && kernel.Contains("public static partial SafeFileHandle CreateFile(", StringComparison.Ordinal)
               && Src("Services", "DriveKind.cs").Contains("using var file = CreateFile(", StringComparison.Ordinal)
               && Regex.Matches(Src("Services", "CrashSentinel.cs") + Src("Services", "JobObjects.cs"), @"using var owned = OpenProcess\(").Count == 4,
            "kernel handles opened by a process or a path are owned by SafeHandles, released by using");
    }

    // ----- engine-teardown-off-ui -----

    private static void EngineTeardown(Action<bool, string> expect)
    {
        var engine = HotPathTests.WithoutComments(Src("Services", "GpuPreviewEngine.cs"));
        var preview = HotPathTests.WithoutComments(Src("MainWindow.GpuPreview.cs"));
        expect(SourceProbe.Member(engine, "public Task DisposeInBackground()").Contains("Task.Run(", StringComparison.Ordinal)
               && SourceProbe.Member(engine, "public static async Task<GpuPreviewEngine?> StartAsync(").Contains("await Volatile.Read(ref _teardown);", StringComparison.Ordinal),
            "the engine tears itself down on the pool after any earlier teardown, and a new engine waits for it (one swap chain per window)");
        expect(SourceProbe.Member(preview, "private void ReleaseGpuEngine()").Contains("DisposeInBackground()", StringComparison.Ordinal)
               && SourceProbe.Member(preview, "private void OnGpuFaulted(").Contains("DisposeInBackground()", StringComparison.Ordinal)
               && SourceProbe.Member(preview, "private void CloseGpuEngine()").Contains("engine?.Dispose();", StringComparison.Ordinal)
               && Src("MainWindow.Player.cs").Contains("ReleaseGpuEngine();", StringComparison.Ordinal),
            "Reload Player and a worker given up on never wait for the old engine; the window's close disposes at once");
        expect(SourceProbe.Member(Src("Services", "GpuPreviewEngine.cs"), "public void Apply(PlayerInputs inputs)").Contains("SetGradeCompare(", StringComparison.Ordinal)
               && typeof(PlayerInputs).GetProperties().Length == 8,
            "a new engine is told every window input at once (PlayerInputs: volume, mute, rate, zoom and pan, Before/After)");
    }
}
