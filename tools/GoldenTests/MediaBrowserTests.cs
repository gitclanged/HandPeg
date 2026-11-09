using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4C (Subagent A2): the Project Media bin (TimelineDocument.MediaPool) and the Media Browser's Grid, List and Filmstrip views with
/// the sub-clip cutter. project-media-pool: the pool on the document (left out of the file while empty, read back, copied by Clone and by
/// an undo step, changed through a transaction that rolls back, a placed clip's file taken in only where the document pools them), and a
/// real view model's bin (one step of Undo for an add or a removal, none for the New Project studio's picks, a file a clip plays refused,
/// kept after its clip is deleted, a project from before 5.4C seeded as it is opened, moved files followed, a tab with only a bin not
/// empty). media-browser-views: the views on one list (template and panel), the saved view and the tile size, the List view's preview
/// from 600 DIPs and off below 576, its columns dropped as it narrows, the Filmstrip list's width, the header and preview made only after
/// the first frame, the picker with no Filmstrip and writing nothing. media-browser-details: the details' cache text, their wording, and
/// the sort by each column with what is not known last. media-subclip-insert: the sub-clip's drag text, the still's clock, the cutter's
/// In and Out and keys, a placed clip cut to a sub-clip with no step of its own. media-source-frames: the latest-wins stills, held while a
/// drag and drop holds background work. Every line says what it proved.
/// </summary>
public static class MediaBrowserTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("project-media-pool", "# Project Media is the project's own bin: saved with it, kept by Undo, a clip's file taken in, kept after its clips go, removed only when no clip plays it.", Pool);
        yield return Case("media-browser-views", "# The Media Browser's Grid, List and Filmstrip views on one list: the preview beside the List from 600 DIPs, columns dropped as it narrows, nothing extra before the first frame.", ViewModes);
        yield return Case("media-browser-details", "# The List view's details: kept in the browser's cache as their own kind, worded as people write them, and sorted with what is not known last.", Details);
        yield return Case("media-subclip-insert", "# The cutter's sub-clip: In and Out on the file's own clock, carried by a drag as invariant text, and a placed clip cut to it with no undo step of its own.", SubClip);
        yield return Case("media-source-frames", "# The preview's stills: latest wins (one made at a time, a newer request takes a waiting one's place), none asked for while a drag and drop holds work.", SourceFramesCase);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
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
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // ----- project-media-pool -----

    private static JsonSerializerOptions FileOptions => (JsonSerializerOptions)typeof(ProjectStore).GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static TimelineClip Video(string path, double start, double end) =>
        new() { SourcePath = path, AudioStreamIndex = 0, Start = start, End = end, SourceDuration = 60 };

    private static MediaInfo Probe(double seconds = 60) =>
        new("mov,mp4", seconds, 0, new VideoStreamInfo(1920, 1080, "h264", 60, "16:9", "1:1", "bt709", "bt709", 0), [], []);

    /// <summary>A view model with an empty Sequence, as a New Project leaves it (nothing probed or decoded).</summary>
    private static MainViewModel Empty()
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetMethod("InstallDocument", Hidden)!.Invoke(vm, [new TimelineDocument()]);
        return vm;
    }

    /// <summary>Opens a file onto the view model as a load does once probed (PlaceOpenedFile).</summary>
    private static void Place(MainViewModel vm, string path, MediaInfo probe)
    {
        vm.SourceWidth = probe.Video!.Width;
        vm.SourceHeight = probe.Video.Height;
        typeof(MainViewModel).GetField("_mediaInfo", Hidden)!.SetValue(vm, probe);
        vm.LocalMediaPath = path;
        typeof(MainViewModel).GetMethod("PlaceOpenedFile", Hidden)!.Invoke(vm, [""]);
    }

    private static List<(string What, byte[] State)> StepsOf(MainViewModel vm) =>
        (List<(string What, byte[] State)>)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!;

    private static string Pooled(TimelineDocument document) => string.Join(", ", document.MediaPool);

    private static void Pool(Action<bool, string> expect)
    {
        // The model: empty and left out of the file; written once it holds files; read back.
        var document = new TimelineDocument();
        var node = JsonSerializer.SerializeToNode(document, FileOptions)!.AsObject();
        expect(document.MediaPool.Count == 0 && !node.ContainsKey("mediaPool") && !document.PoolsPlacedFiles,
            "a new document's pool is empty, left out of the file, and it pools no placed file (only the view model's do)");
        using (var put = document.BeginTransaction("put"))
        {
            expect(put.AddToMediaPool(["D:/media/a.mp4", "\"D:/media/b.png\"", " D:/MEDIA/A.MP4 ", "", "https://example.com/v.mp4"]), "AddToMediaPool says it changed something");
            put.Commit();
        }

        expect(Pooled(document) == "D:/media/a.mp4, D:/media/b.png", "each file once (case and quotes aside), in order; an empty path and a web address left out");
        var json = JsonSerializer.Serialize(document, FileOptions);
        var read = JsonSerializer.Deserialize<TimelineDocument>(json, FileOptions)!;
        expect(json.Contains("\"mediaPool\":[\"D:/media/a.mp4\",\"D:/media/b.png\"]", StringComparison.Ordinal) && Pooled(read) == Pooled(document),
            "written as \"mediaPool\" once it holds files, and read back the same");
        document.PoolsPlacedFiles = true;
        var (copy, deep) = (document.Clone(), document.Clone(deep: true));
        expect(Pooled(copy) == Pooled(document) && Pooled(deep) == Pooled(document) && copy.PoolsPlacedFiles && deep.PoolsPlacedFiles && !ReferenceEquals(copy.MediaPool, document.MediaPool),
            "Clone and Clone(deep) copy the pool (a list of their own) and whether it pools placed files");

        // A transaction: a pool-only change raises Committed; a rollback puts the list back; a removal says when nothing was there.
        var commits = new List<TimelineCommit>();
        document.Committed += commits.Add;
        using (var add = document.BeginTransaction("add"))
        {
            add.AddToMediaPool(["D:/media/c.wav"]);
            add.Commit();
        }

        expect(commits.Count == 1 && Pooled(document).EndsWith("D:/media/c.wav", StringComparison.Ordinal), "a change of the pool alone is a commit (a step of Undo like any other)");
        using (var change = document.BeginTransaction("rolled back"))
        {
            change.RemoveFromMediaPool(["d:/media/A.mp4"]);
            change.AddToMediaPool(["D:/media/d.mp4"]);
        }

        expect(Pooled(document) == "D:/media/a.mp4, D:/media/b.png, D:/media/c.wav" && commits.Count == 1, "a transaction rolled back puts the pool back as it was, and raises nothing");
        using (var none = document.BeginTransaction("nothing"))
        {
            expect(!none.RemoveFromMediaPool(["D:/media/zzz.mp4"]) && !none.AddToMediaPool(["D:/media/a.mp4"]), "removing a file it does not hold, or adding one it holds, changes nothing (false)");
            none.Commit();
        }

        // Placed clips: their files taken in only where the document pools them; a deleted clip's file stays.
        var plain = new TimelineDocument();
        var pooling = new TimelineDocument { PoolsPlacedFiles = true };
        TimelineClip placed = null!;
        foreach (var (doc, pools) in new[] { (plain, false), (pooling, true) })
        {
            using var tx = doc.BeginTransaction("place");
            var track = tx.InsertTrack(0, TimelineTrackKind.Video);
            var clip = tx.PlaceClip(track, Video("D:/media/clip.mp4", 0, 5));
            tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Image, SourcePath = "D:/media/logo.png", Start = 6, End = 8 });
            tx.PlaceClip(track, new TimelineClip { ContentKind = LayerKind.Text, Start = 9, End = 10 });
            tx.Commit();
            if (pools)
                placed = clip;
        }

        expect(plain.MediaPool.Count == 0, "a document that does not pool placed files (a golden's, a project read for its thumbnail) keeps its pool as it is");
        expect(Pooled(pooling) == "D:/media/clip.mp4, D:/media/logo.png", "one that does takes in a video's and a picture's file (a text clip has none)");
        using (var gone = pooling.BeginTransaction("delete"))
        {
            gone.DeleteClip(placed);
            gone.Commit();
        }

        expect(Pooled(pooling) == "D:/media/clip.mp4, D:/media/logo.png", "the clip deleted, its file stays in the pool");
        expect(TimelineDocument.PoolFileOf(new TimelineClip { SourcePath = "https://example.com/a.mp4" }) is null
               && TimelineDocument.PoolFileOf(new TimelineClip { ContentKind = LayerKind.Image, State = new LayerState { ImagePath = " \"D:/x/p.png\" " } }) == "D:/x/p.png",
            "PoolFileOf: no web address; a picture's file from its look when the clip names none, trimmed and unquoted");

        // A real view model: its documents pool placed files; the bin's changes are steps of Undo.
        var vm = Empty();
        expect(vm.Document.PoolsPlacedFiles && vm.HoldsNothingForTab, "the view model's document pools placed files; a blank project holds nothing for a tab");
        var steps = StepsOf(vm).Count;
        expect(vm.AddToProjectMedia(["D:/media/one.mp4", "D:/media/notes.txt", "D:/MEDIA/ONE.mp4", "D:/media/two.wav"]) == 2 && StepsOf(vm).Count == steps + 1,
            "AddToProjectMedia: the media files once each (a text file left out), one step of Undo");
        expect(vm.ProjectMediaFiles() is ["D:/media/one.mp4", "D:/media/two.wav"] && !vm.HoldsNothingForTab, "Project Media lists them in order, and the tab is not empty any more");
        vm.Undo();
        expect(vm.Document.MediaPool.Count == 0, "Undo takes them out again");
        vm.Redo();
        expect(Pooled(vm.Document) == "D:/media/one.mp4, D:/media/two.wav", "Redo puts them back");
        var packed = UndoSteps.Unpack(UndoSteps.Pack(new TimelineSnapshot(vm.Document.Clone(), [])));
        expect(packed is not null && Pooled(packed.Document) == Pooled(vm.Document), "an undo step (UndoSteps.Pack, Unpack) carries the pool");

        var picks = Empty();
        var pickSteps = StepsOf(picks).Count;
        expect(picks.AddToProjectMedia(["D:/media/picked.mp4"], undoable: false) == 1 && StepsOf(picks).Count == pickSteps && !picks.HoldsNothingForTab,
            "the New Project studio's picks: put in with no step of Undo, and the tab holds them");

        // A clip's file: in the bin with the clip's own step; refused while the clip plays it; kept after the clip is deleted.
        var cut = Empty();
        Place(cut, "D:/media/game.mp4", Probe());
        expect(Pooled(cut.Document) == "D:/media/game.mp4" && cut.IsFileInUse("d:/media/GAME.mp4") && cut.ClipsUsing("D:/media/game.mp4") == 1,
            "a file opened onto the Sequence is in Project Media, and a clip plays it");
        var before = StepsOf(cut).Count;
        expect(cut.RemoveFromProjectMedia(["D:/media/game.mp4"]) == 0 && StepsOf(cut).Count == before && cut.Document.MediaPool.Count == 1
               && cut.StatusText.Contains("1 clip on the Sequence plays it", StringComparison.Ordinal),
            "Remove from Project Media refuses a file a clip plays: no step, and the status line says how many clips use it");
        cut.DeleteClip(cut.Layers.First(l => l.IsVideoFile));
        expect(!cut.IsFileInUse("D:/media/game.mp4") && Pooled(cut.Document) == "D:/media/game.mp4", "the clip deleted, the file stays in Project Media");
        before = StepsOf(cut).Count;
        expect(cut.RemoveFromProjectMedia(["D:/media/game.mp4"]) == 1 && cut.Document.MediaPool.Count == 0 && StepsOf(cut).Count == before + 1,
            "with no clip playing it, Remove takes it out, one step of Undo");

        // A project from before 5.4C: seeded from its clips' files as it is opened, never as it is read.
        var legacy = new TimelineDocument();
        using (var tx = legacy.BeginTransaction("old"))
        {
            var track = tx.InsertTrack(0, TimelineTrackKind.Video);
            tx.PlaceClip(track, Video("D:/old/b.mp4", 5, 9));
            tx.PlaceClip(track, Video("D:/old/a.mp4", 0, 4));
            tx.PlaceClip(track, Video("D:/old/b.mp4", 10, 12));
            tx.Commit();
        }

        var state = new ProjectState { Name = "old", Timeline = legacy };
        var reread = ProjectStore.Deserialize(ProjectStore.Serialize(state));
        var opened = (TimelineDocument)typeof(MainViewModel).GetMethod("TimelineToOpen", Hidden)!.Invoke(new MainViewModel(), [reread])!;
        expect(reread.Timeline.MediaPool.Count == 0 && Pooled(opened) == "D:/old/a.mp4, D:/old/b.mp4",
            "a project with no pool reads back with none (ProjectStore), and TimelineToOpen seeds it from its clips' files, each once, in the order the track holds them");
        reread.Timeline.MediaPool.Add("D:/kept/only.wav");
        var kept = (TimelineDocument)typeof(MainViewModel).GetMethod("TimelineToOpen", Hidden)!.Invoke(new MainViewModel(), [reread])!;
        expect(Pooled(kept) == "D:/kept/only.wav", "a project that has a pool keeps it as it is");

        // Moved files (downloads kept in a folder of the user's): the bin follows, in a step read back.
        var step = UndoSteps.Unpack(UndoSteps.Pack(new TimelineSnapshot(new TimelineDocument { MediaPool = ["C:/cache/dl.mp4", "D:/media/stay.mp4"] }, [])))!;
        var moved = (bool)typeof(MainViewModel).GetMethod("MoveFiles", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [step, (Func<string, string>)(p => p == "C:/cache/dl.mp4" ? "D:/kept/dl.mp4" : p)])!;
        expect(moved && Pooled(step.Document) == "D:/kept/dl.mp4, D:/media/stay.mp4", "MoveFiles points the pool's entries at the moved files too");
        var pieces = new Dictionary<string, IReadOnlyList<VodTrim.Piece>>(StringComparer.OrdinalIgnoreCase)
        {
            ["D:/vod/long.mkv".Replace('/', '\\')] = [new VodTrim.Piece("D:/b.trim/long 1.mkv", 0, 30), new VodTrim.Piece("D:/b.trim/long 2.mkv", 600, 30)],
        };
        var bundled = ProjectBundle.PoolWithPieces(["D:/media/a.mp4", "D:/vod/long.mkv", "D:/media/only.mkv"], pieces);
        expect(string.Join(" | ", bundled) == "D:/media/a.mp4 | D:/b.trim/long 1.mkv | D:/b.trim/long 2.mkv | D:/media/only.mkv",
            "a bundle with long recordings trimmed: the bin holds each trimmed recording as its pieces, in its place; a file only in the bin goes in whole");
        var inUse = Empty();
        inUse.AddToProjectMedia(["D:/media/pool-only.mp4"]);
        expect(inUse.MediaFilesInUse().Contains("D:/media/pool-only.mp4"), "MediaFilesInUse holds the bin's files (a download only in the bin is pinned and copied too)");
    }

    // ----- media-browser-views -----

    private static T? Find<T>(DependencyObject under) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(under); i++)
        {
            var child = VisualTreeHelper.GetChild(under, i);
            if (child is T found)
                return found;
            if (Find<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    private static void Lay(FrameworkElement element, double width, double height = 700)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void ViewModes(Action<bool, string> expect)
    {
        expect(MediaBrowserPane.ViewOf("", 40) == MediaBrowserView.Grid && MediaBrowserPane.ViewOf("", 5) == MediaBrowserView.List
               && MediaBrowserPane.ViewOf("Filmstrip", 5) == MediaBrowserView.Filmstrip && MediaBrowserPane.ViewOf(" list ", 90) == MediaBrowserView.List
               && MediaBrowserPane.ViewOf("Mosaic", 40) == MediaBrowserView.Grid && MediaBrowserPane.ViewOf("7", 40) == MediaBrowserView.Grid,
            "BrowserView: Grid, List or Filmstrip by name (any case); empty or unknown, the List where the tile size was a list's (below 8), else the Grid");
        expect(!MediaBrowserPane.SplitShows(false, 599) && MediaBrowserPane.SplitShows(false, 600) && MediaBrowserPane.SplitShows(true, 580)
               && MediaBrowserPane.SplitShows(true, 576) && !MediaBrowserPane.SplitShows(true, 575),
            "the List view's preview: on from 600 DIPs, off below 576 (between, as it was): a splitter dragged across never makes it flicker");
        expect(MediaBrowserPane.FilmstripListWidth(240, 1000) == 240 && MediaBrowserPane.FilmstripListWidth(240, 400) == 200 && MediaBrowserPane.FilmstripListWidth(240, 305) == 180
               && MediaBrowserPane.FilmstripListWidth(900, 1000) == 500 && MediaBrowserPane.FilmstripListWidth(0, 1000) == 240 && MediaBrowserPane.SplitListWidth(620) == 341,
            "the Filmstrip list: the width kept (240 by default), at most half the pane, at least 180; beside the List's preview, the table keeps 55 % and at least 340");
        // 5.4D: in a narrow pane the list gives way to 140, so the preview and cutter keep 120 beside it (and the 5 DIP splitter).
        expect(MediaBrowserPane.FilmstripListWidth(240, 300) == 175 && MediaBrowserPane.FilmstripListWidth(240, 280) == 155 && MediaBrowserPane.FilmstripListWidth(240, 265) == 140
               && MediaBrowserPane.FilmstripListWidth(240, 200) == 140 && MediaBrowserPane.FilmstripListLeastAt(400) == 180,
            "below 305 DIPs the list's least narrows (300: 175, 280: 155, 265: 140), never under 140; from 305 it is 180 again");
        var widths = new[] { 900.0, 640, 560, 480, 420, 350, 300, 240 };
        expect(true, "the List view's columns by the row's width: " + string.Join("; ", widths.Select(w =>
            string.Create(CultureInfo.InvariantCulture, $"{w:0} = {string.Join(" ", MediaBrowserPane.ColumnsShown(w))}"))));
        expect(MediaBrowserPane.ColumnsShown(900).Count == 6 && MediaBrowserPane.ColumnsShown(560) is ["Resolution", "Framerate", "Duration", "Codec", "Size"]
               && MediaBrowserPane.ColumnsShown(500) is ["Resolution", "Framerate", "Duration", "Size"]
               && MediaBrowserPane.ColumnsShown(240).Count == 0,
            "every column while the name keeps 120 DIPs; then the sound streams and the codec go first, then the size, the frame rate, the resolution, the length");

        var settings = AppSettings.Current;
        var kept = (settings.BrowserView, settings.BrowserTileSize, settings.BrowserFilmstripListWidth, settings.BrowserShowsProject, settings.BrowserShowsLibrary, settings.BrowserSort);
        var before = PaneTitleSlot.Of("Media");
        try
        {
            (settings.BrowserView, settings.BrowserTileSize, settings.BrowserFilmstripListWidth) = ("Grid", 40, 240);
            var pane = new MediaBrowserPane();
            var items = pane.Items;
            pane.Items.ItemsSource = Enumerable.Range(1, 5).Select(i => new MediaBrowserItem($"D:/media/clip{i}.mp4", MediaBrowserKind.Video, $"clip{i}.mp4")).ToList();
            Lay(pane, 500);
            expect(pane.ViewMode == MediaBrowserView.Grid && ReferenceEquals(items.ItemTemplate, pane.Resources["TileTemplate"]) && Find<VirtualizingTilePanel>(items) is not null
                   && pane.ViewBox.SelectedIndex == 0 && pane.PreviewHost.Visibility == Visibility.Collapsed,
                "Grid: the tiles' template and panel, the View box on Grid, no preview");
            pane.ViewMode = MediaBrowserView.List;
            Lay(pane, 500);
            expect(ReferenceEquals(pane.Items, items) && ReferenceEquals(items.ItemTemplate, pane.Resources["DetailRowTemplate"]) && Find<VirtualizingStackPanel>(items) is not null
                   && Find<VirtualizingTilePanel>(items) is null && settings.BrowserView == "List" && pane.ViewBox.SelectedIndex == 1,
                "List: the same list with the details' rows on a virtualizing stack, kept as BrowserView \"List\", the View box following");
            expect(pane.PreviewHost.Visibility == Visibility.Collapsed && pane.PreviewColumn.Width.Value == 0, "a List 500 DIPs wide has no preview");
            Lay(pane, 620);
            expect(pane.PreviewHost.Visibility == Visibility.Visible && pane.PreviewColumn.Width.IsStar && pane.ListColumn.Width.Value == 341, "widened to 620: the preview on its left, the table 341 wide");
            Lay(pane, 590);
            var stays = pane.PreviewHost.Visibility == Visibility.Visible;
            Lay(pane, 570);
            expect(stays && pane.PreviewHost.Visibility == Visibility.Collapsed && pane.ListColumn.Width.IsStar, "narrowed to 590 it stays; to 570 it goes, the table filling the pane");
            Lay(pane, 500);
            expect(pane.Resources["ColAudio"] is GridLength { Value: 0 } && pane.Resources["ColCodec"] is GridLength { Value: 0 } && pane.Resources["ColResolution"] is GridLength { Value: 84 },
                "500 DIPs: the sound streams' and the codec's columns dropped (0 wide), the resolution's kept");

            pane.ViewMode = MediaBrowserView.Filmstrip;
            Lay(pane, 800);
            expect(ReferenceEquals(items.ItemTemplate, pane.Resources["CompactRowTemplate"]) && pane.PreviewHost.Visibility == Visibility.Visible
                   && pane.PreviewSplitter.Visibility == Visibility.Visible && Math.Abs(pane.ListColumn.ActualWidth - 240) < 0.5 && pane.SplitColumn.Width.Value == 5,
                "Filmstrip: compact rows in a 240 DIP column on the right, a 5 DIP splitter, the preview on the left");
            Lay(pane, 400);
            expect(Math.Abs(pane.ListColumn.ActualWidth - 200) < 0.5 && pane.ListColumn.MinWidth == 180, "at 400 DIPs the list is half the pane (200), never under 180");
            expect(pane.PreviewColumn.MinWidth == MediaBrowserPane.PreviewLeast, "the preview's column keeps at least 120 in the Filmstrip view");

            // 5.4D: the pane at the Media column's least in Filmstrip (280; its content a little narrower), the list giving way to the preview.
            Lay(pane, 265);
            expect(pane.ListColumn.MinWidth == MediaBrowserPane.FilmstripListNarrow && Math.Abs(pane.ListColumn.ActualWidth - 140) < 0.5
                   && Math.Abs(pane.PreviewColumn.ActualWidth - 120) < 0.5,
                string.Create(CultureInfo.InvariantCulture, $"at 265 DIPs: the list 140 (its least there), the preview and cutter 120 beside it, nothing cut (list {pane.ListColumn.ActualWidth:0.#} least {pane.ListColumn.MinWidth:0.#}, preview {pane.PreviewColumn.ActualWidth:0.#}, grid {pane.ContentGrid.ActualWidth:0.#})"));

            // A double-click on the splitter: the list back to 240 (kept), as wide as the pane lets it be.
            settings.BrowserFilmstripListWidth = 320;
            Lay(pane, 800);
            var dragged = Math.Abs(pane.ListColumn.ActualWidth - 320) < 0.5;
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, 2);
            typeof(MediaBrowserPane).GetMethod("PreviewSplitter_PreviewMouseLeftButtonDown", Hidden)!.Invoke(pane, [pane.PreviewSplitter, press]);
            Lay(pane, 800);
            expect(dragged && press.Handled && settings.BrowserFilmstripListWidth == 240 && Math.Abs(pane.ListColumn.ActualWidth - 240) < 0.5,
                "a double-click on the splitter puts the list back to 240 DIPs (from 320), kept, and starts no drag (the press handled)");

            // 5.4D: the preview's buttons (made after the first frame, so as EnsurePreview makes them, here by hand) at 100 DIPs: In, Out, Clear
            // and Step wrap, Insert on a line under them, said shorter; nothing cut. At 600, one line, Insert at the right in full.
            var bar = new DockPanel { LastChildFill = false };
            var marks = new WrapPanel();
            foreach (var word in new[] { "In", "Out", "Clear", "Step" })
                marks.Children.Add(new Button { Content = word, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) });
            var insert = new Button { Content = "Insert at Playhead", Padding = new Thickness(8, 2, 8, 2) };
            DockPanel.SetDock(marks, Dock.Left);
            DockPanel.SetDock(insert, Dock.Right);
            bar.Children.Add(marks);
            bar.Children.Add(insert);
            typeof(MediaBrowserPane).GetField("_insertButton", Hidden)!.SetValue(pane, insert);
            var fit = typeof(MediaBrowserPane).GetMethod("FitPreviewBar", Hidden)!;
            bool Inside(double width) => bar.Children.Cast<UIElement>().Concat(marks.Children.Cast<UIElement>()).OfType<Button>()
                .All(b => b.TranslatePoint(new Point(b.ActualWidth, 0), bar).X <= width + 0.5 && b.ActualWidth + b.Margin.Left + b.Margin.Right >= b.DesiredSize.Width - 0.5);
            Lay(bar, 100, 200);
            fit.Invoke(pane, [bar, marks]);
            Lay(bar, 100, 200);
            var narrow = Inside(100) && DockPanel.GetDock(insert) == Dock.Top && insert.Content as string == "Insert" && insert.TranslatePoint(new Point(), bar).Y > 0;
            Lay(bar, 600, 200);
            fit.Invoke(pane, [bar, marks]);
            Lay(bar, 600, 200);
            expect(narrow && Inside(600) && DockPanel.GetDock(insert) == Dock.Right && insert.Content as string == "Insert at Playhead" && insert.TranslatePoint(new Point(), bar).Y == 0,
                "the preview's buttons at 100 DIPs wrap, Insert under them as \"Insert\", none cut; at 600 on one line, \"Insert at Playhead\" at the right");
            typeof(MediaBrowserPane).GetField("_insertButton", Hidden)!.SetValue(pane, null);
            expect(pane.ListHeaderHost.Content is null && pane.PreviewHost.Content is null, "laid out without a window (no first frame), neither the header nor the preview has been made");

            pane.TileSize = 5;
            expect(pane.ViewMode == MediaBrowserView.List && settings.BrowserTileSize == 5 && settings.BrowserView == "List", "a tile size below 8 is the List view (kept: BrowserTileSize 5)");
            pane.TileSize = 55;
            expect(pane.ViewMode == MediaBrowserView.Grid && settings.BrowserTileSize == 55 && settings.BrowserView == "Grid", "and any other the Grid at that size");
            expect(pane.PreviewColumn.MinWidth == 0 && pane.ListColumn.MinWidth == 0, "out of the Filmstrip view the preview's and the list's columns keep no least width (5.4D)");

            // The header, made at first need (here by hand): a cell per column on the rows' widths; a column header sorts.
            pane.ViewMode = MediaBrowserView.List;
            typeof(MediaBrowserPane).GetMethod("BuildHeader", Hidden)!.Invoke(pane, null);
            Lay(pane, 500);
            var header = pane.ListHeaderHost.Content as Grid;
            expect(header is { ColumnDefinitions.Count: 8 } && header.ColumnDefinitions[5].ActualWidth == 0 && Math.Abs(header.ColumnDefinitions[2].ActualWidth - 84) < 0.5
                   && pane.ListHeaderHost.Visibility == Visibility.Visible,
                "the header: eight columns on the rows' own widths (the codec's 0 at 500 DIPs, the resolution's 84)");
            pane.Release();

            // Law 1: a pane made with the Filmstrip view saved (the Vertical & Shorts Studio) makes nothing more until after the first frame.
            settings.BrowserView = "Filmstrip";
            var shorts = new MediaBrowserPane();
            expect(shorts.ViewMode == MediaBrowserView.Filmstrip && shorts.PreviewHost.Content is null && shorts.ListHeaderHost.Content is null
                   && typeof(MediaBrowserPane).GetField("_cutter", Hidden)!.GetValue(shorts) is null && typeof(MediaBrowserPane).GetField("_sourceFrames", Hidden)!.GetValue(shorts) is null,
                "made with BrowserView Filmstrip: no preview, cutter, header or stills source in the constructor (made at ContextIdle after the first frame)");
            shorts.Release();

            // The picker: no Filmstrip, no preview beside its List, and nothing it does is written.
            settings.BrowserView = "Filmstrip";
            var docked = new PaneTitleSlot { PaneId = "Media" };
            var picker = new MediaBrowserPane(pickerMode: true);
            expect(picker.ViewMode == MediaBrowserView.Grid && picker.FilmstripChoice.Visibility == Visibility.Collapsed && ReferenceEquals(PaneTitleSlot.Of("Media"), docked),
                "the picker with Filmstrip saved: the Grid, Filmstrip not offered, the dock's title slot left alone");
            picker.ViewMode = MediaBrowserView.List;
            Lay(picker, 800);
            var listed = picker.ViewMode == MediaBrowserView.List && picker.PreviewHost.Visibility == Visibility.Collapsed;
            picker.ViewMode = MediaBrowserView.Filmstrip;
            expect(listed && picker.ViewMode == MediaBrowserView.Grid && settings.BrowserView == "Filmstrip" && settings.BrowserFilmstripListWidth == 240,
                "its List has no preview even at 800 DIPs, Filmstrip asked for is the Grid, and BrowserView is as it was");
            expect(!picker.OwnsKey(Key.Delete, ModifierKeys.None), "the picker never keeps Delete (it has no Project Media)");
            picker.Release();
        }
        finally
        {
            PaneTitleSlot.Restore("Media", before);
            (settings.BrowserView, settings.BrowserTileSize, settings.BrowserFilmstripListWidth, settings.BrowserShowsProject, settings.BrowserShowsLibrary, settings.BrowserSort) = kept;
        }
    }

    // ----- media-browser-details -----

    private static MediaBrowserItem Item(string name, long bytes = 0) => new($"D:/media/{name}", MediaBrowserKind.Video, name) { Bytes = bytes };

    private static void Details(Action<bool, string> expect)
    {
        var details = new MediaDetails(123.4567, 2560, 1440, 59.94006, "hevc", 2);
        var text = MediaBrowserFrames.FormatDetails(details);
        expect(text == "1|123.457|2560|1440|59.9401|hevc|2" && MediaBrowserFrames.ParseDetails(text) == new MediaDetails(123.457, 2560, 1440, 59.9401, "hevc", 2),
            "the \"details\" kind's text: version 1, seconds, width, height, rate, codec, sound streams (invariant), read back the same");
        expect(MediaBrowserFrames.ParseDetails("123.4|1920|1080") is null && MediaBrowserFrames.ParseDetails("2|1|1|1|1|h264|1") is null
               && MediaBrowserFrames.ParseDetails("1|x|1|1|1|h264|1") is null && MediaBrowserFrames.FormatDetails(details with { Codec = "a|b" }).Split('|').Length == 7,
            "the tiles' three-part \"probe\" text, another version, or a number that is not one: no details; a bar in a codec never breaks the text");

        var item = Item("clip.mp4");
        item.Info = "48 MB";
        expect(item.MetaLine == "48 MB" && item.ResolutionText == "" && item.AudioText == "", "before its details: the second line is what Info says, the cells empty");
        item.TakeDetails(123, 1920, 1080, 60, "h264", 1);
        expect(item is { ResolutionText: "1920x1080", FrameRateText: "60 fps", DurationText: "2:03", CodecText: "H.264", AudioText: "1", DetailsKnown: true }
               && item.MetaLine == "1920x1080 \u2022 60 fps \u2022 2:03" && item.Shape > 1.77 && item.Seconds == 123,
            "with them: 1920x1080, 60 fps, 2:03, H.264, 1, and the Filmstrip line \"1920x1080 \u2022 60 fps \u2022 2:03\"");
        expect(MediaBrowserItem.FormatFrameRate(59.94006) == "59.94 fps" && MediaBrowserItem.FormatFrameRate(360) == "360 fps" && MediaBrowserItem.FormatFrameRate(23.976) == "23.98 fps"
               && MediaBrowserItem.FormatFrameRate(0) == "" && MediaBrowserItem.FormatFrameRate(double.NaN) == "",
            "frame rates: 59.94 fps, 360 fps, 23.98 fps; none for 0 or not a number");
        expect(string.Join(" ", new[] { "h264", "hevc", "av1", "vp9", "prores", "mpeg2video", "dnxhd", "" }.Select(MediaBrowserItem.FormatCodec)) == "H.264 HEVC AV1 VP9 ProRes MPEG-2 DNXHD ",
            "codecs as people write them: H.264, HEVC, AV1, VP9, ProRes, MPEG-2, any other in capitals");
        expect(MediaBrowserItem.FormatAudio(-1) == "" && MediaBrowserItem.FormatAudio(0) == "None" && MediaBrowserItem.FormatAudio(3) == "3"
               && MediaBrowserItem.FormatResolution(0, 1080) == "" && MediaBrowserItem.JoinMeta("", "30 fps", "") == "30 fps",
            "sound streams: none known, None, 3; no resolution without both sides; the line joins only what is known");

        // The sort by each column, ascending and descending, what is not known last either way (folders first, as always).
        MediaBrowserItem With(string name, int width, int height, double fps, double seconds, string codec, int audio)
        {
            var made = Item(name);
            if (width >= 0)
                made.TakeDetails(seconds, width, height, fps, codec, audio);
            return made;
        }

        var listed = new[]
        {
            With("a.mp4", 1920, 1080, 60, 30, "h264", 1), With("b.mp4", 3840, 2160, 30, 10, "hevc", 2), With("c.mp4", 1280, 720, 144, 90, "av1", 0),
            With("u.mp4", -1, 0, 0, 0, "", -1), new MediaBrowserItem("D:/media/folder", MediaBrowserKind.Folder, "folder"),
        };
        string Order(string key, bool descending) => string.Join(" ", MediaBrowserPane.SortedBy(listed, key, descending).Select(i => i.Name.Replace(".mp4", "", StringComparison.Ordinal)));
        var orders = new[] { "Resolution", "Framerate", "Duration", "Codec", "Audio" }.Select(k => $"{k}: {Order(k, false)} / {Order(k, true)}").ToList();
        expect(true, "the orders (ascending / descending): " + string.Join("; ", orders));
        expect(orders.SequenceEqual([
                "Resolution: folder c a b u / folder b a c u", "Framerate: folder b a c u / folder c a b u", "Duration: folder b a c u / folder c a b u",
                "Codec: folder c a b u / folder b a c u", "Audio: folder c a b u / folder b a c u"]),
            "each column sorts both ways, the folder first and the file with nothing known last either way");

        // A column header sorts, and keeps it; the Sort box shows none for a key it does not list, and takes over again when chosen.
        var settings = AppSettings.Current;
        var kept = (settings.BrowserSort, settings.BrowserSortDescending, settings.BrowserView);
        var before = PaneTitleSlot.Of("Media");
        try
        {
            (settings.BrowserSort, settings.BrowserSortDescending, settings.BrowserView) = ("Name", false, "List");
            var pane = new MediaBrowserPane();
            var sortBy = typeof(MediaBrowserPane).GetMethod("SortByColumn", Hidden)!;
            sortBy.Invoke(pane, ["Resolution"]);
            var first = (settings.BrowserSort, settings.BrowserSortDescending, pane.SortBox.SelectedIndex);
            sortBy.Invoke(pane, ["Resolution"]);
            var again = (settings.BrowserSort, settings.BrowserSortDescending);
            pane.SortBox.SelectedIndex = 2;
            expect(first == ("Resolution", false, -1) && again == ("Resolution", true) && settings.BrowserSort == "Size"
                   && (string)typeof(MediaBrowserPane).GetField("_sortKey", Hidden)!.GetValue(pane)! == "Size",
                "a header click sorts by its column ascending (the Sort box blank), again descending, both kept; the Sort box's Size then takes over");
            pane.Release();
            settings.BrowserSort = "Codec";
            var reopened = new MediaBrowserPane();
            expect((string)typeof(MediaBrowserPane).GetField("_sortKey", Hidden)!.GetValue(reopened)! == "Codec" && reopened.SortBox.SelectedIndex == -1,
                "a pane made with a column's sort kept sorts by it, the Sort box blank");
            reopened.Release();
        }
        finally
        {
            PaneTitleSlot.Restore("Media", before);
            (settings.BrowserSort, settings.BrowserSortDescending, settings.BrowserView) = kept;
        }
    }

    // ----- media-subclip-insert -----

    private static void SubClip(Action<bool, string> expect)
    {
        var culture = CultureInfo.CurrentCulture;
        string text;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            text = MediaBrowserPane.FormatSubClip(12.5, 31.25);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        expect(text == "12.5|31.25" && MediaBrowserPane.ParseSubClip(text) == (12.5, 31.25), "a sub-clip's drag text is invariant (\"12.5|31.25\" under de-DE) and reads back");
        expect(MediaBrowserPane.ParseSubClip("31|12") is null && MediaBrowserPane.ParseSubClip("a|b") is null && MediaBrowserPane.ParseSubClip("5") is null
               && MediaBrowserPane.ParseSubClip(5.0) is null && MediaBrowserPane.ParseSubClip(null) is null && MediaBrowserPane.ParseSubClip("-1|3") is null,
            "Out before In, words, one number, not text, nothing, a negative In: no sub-clip");
        expect(MediaBrowserPane.SubClipFormat == "HandPeg.MediaBrowserSubClip" && MediaBrowserPane.DragFormat == "HandPeg.MediaBrowserFiles",
            "its drag format sits beside the browser's own (both carried with the file)");
        expect(SourceFrames.StillMoment(10, 1.5, 1.0) == 10.5 && SourceFrames.StillMoment(10, null, 1.0) == 10 && SourceFrames.StillMoment(10, 0.5, 1.0) == 10,
            "a still's moment: the MediaOffset clock shifted by how far the video starts after the container (never earlier), as FileMomentAt does");

        // The cutter: In and Out at the playhead, kept inside the file and apart; the keys it keeps.
        expect(MediaFilmstripCutter.ClampRange(-3, 500, 60) == (0, 60) && MediaFilmstripCutter.ClampRange(20, 10, 60) == (20, 20.1) && MediaFilmstripCutter.ClampRange(59.99, 60, 60) == (59.9, 60),
            "ClampRange: inside the file, Out at least a tenth of a second after In");
        var cutter = new MediaFilmstripCutter();
        var ranges = new List<(double, double)>();
        cutter.RangeChanged += (start, end) => ranges.Add((start, end));
        cutter.Load(60, 30);
        cutter.PositionSeconds = 10;
        cutter.MarkIn();
        var afterIn = (cutter.InSeconds, cutter.OutSeconds, cutter.HasRange);
        cutter.PositionSeconds = 20;
        cutter.MarkOut();
        expect(afterIn == (10, 60, true) && (cutter.InSeconds, cutter.OutSeconds) == (10, 20) && ranges.Count == 2, "I at 10 s: In 10, Out the end; O at 20 s: Out 20; each told (RangeChanged)");
        cutter.PositionSeconds = 5;
        cutter.MarkOut();
        expect((cutter.InSeconds, cutter.OutSeconds) == (0, 5), "O before In: In goes back to the start");
        cutter.ClearRange();
        expect((cutter.InSeconds, cutter.OutSeconds, cutter.HasRange) == (0, 60, false), "Clear: the whole file, no sub-clip marked");
        var scrubbed = new List<(double, bool)>();
        cutter.Scrubbed += (at, settled) => scrubbed.Add((at, settled));
        cutter.StepFrames(30);
        cutter.PositionSeconds = 100;
        expect(Math.Abs(scrubbed[^1].Item1 - 6) < 1e-9 && scrubbed[^1].Item2 && cutter.PositionSeconds == 60, "30 frames at 30 fps: a second later, settled; the playhead never past the file's end");
        var keys = new[] { Key.I, Key.O, Key.Space, Key.Left, Key.Right, Key.Home, Key.End, Key.J, Key.K, Key.L, Key.Enter };
        expect(keys.All(k => cutter.OwnsKey(k, ModifierKeys.None)) && cutter.OwnsKey(Key.Right, ModifierKeys.Shift)
               && !cutter.OwnsKey(Key.I, ModifierKeys.Control) && !cutter.OwnsKey(Key.Delete, ModifierKeys.None) && !cutter.OwnsKey(Key.S, ModifierKeys.None)
               && MainWindow.OwnerOfKeys(cutter) is MediaFilmstripCutter,
            "the cutter keeps I, O, Space, Left, Right, Home, End, J, K, L and Enter (Shift too), never with Ctrl, never Delete or another key; the window asks it first");
        cutter.Load(0, 0);
        cutter.MarkIn();
        expect((cutter.InSeconds, cutter.OutSeconds, cutter.HasRange) == (0, 0, false), "with no file, marking does nothing");

        // A clip placed from a sub-clip: MediaOffset at In, as long as Out - In, with no undo step of its own (the add's).
        var vm = Empty();
        Place(vm, "D:/media/long.mp4", Probe(60));
        var steps = StepsOf(vm).Count;
        var view = vm.Layers.First(l => l.IsVideoFile);
        vm.FitToSourceRange(view, 12.5, 31.25);
        var clip = vm.ClipOf(view)!;
        expect(Math.Abs(clip.MediaOffset - 12.5) < 1e-6 && Math.Abs(clip.Duration - 18.75) < 0.02 && StepsOf(vm).Count == steps,
            string.Create(CultureInfo.InvariantCulture, $"FitToSourceRange(12.5, 31.25): MediaOffset {clip.MediaOffset:0.###}, {clip.Duration:0.##} s long, no step of Undo of its own"));
        vm.FitToSourceRange(view, 50, 50.01);
        expect(Math.Abs(vm.ClipOf(view)!.MediaOffset - 12.5) < 1e-6, "a sub-clip shorter than the shortest clip changes nothing");
    }

    // ----- media-source-frames -----

    private static BitmapSource Pixel(byte shade)
    {
        var picture = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new[] { shade }, 1);
        picture.Freeze();
        return picture;
    }

    private static void SourceFramesCase(Action<bool, string> expect)
    {
        var asked = new List<string>();
        var gate = new TaskCompletionSource();
        var frames = new SourceFrames(async (path, seconds, height, fast, token) =>
        {
            lock (asked)
                asked.Add(string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#}{(fast ? " fast" : "")}"));
            if (seconds == 1)
                await gate.Task.ConfigureAwait(false);
            return Pixel((byte)seconds);
        });

        var first = frames.FrameAsync("D:/media/a.mp4", 1, 270, fast: true, CancellationToken.None);
        SpinUntil(() => { lock (asked) return asked.Count == 1; });
        var second = frames.FrameAsync("D:/media/a.mp4", 2, 270, fast: true, CancellationToken.None);
        var third = frames.FrameAsync("D:/media/a.mp4", 3, 270, fast: false, CancellationToken.None);
        var replaced = second.Wait(2000) && second.Result is null;
        gate.SetResult();
        var made = first.Wait(2000) && third.Wait(2000);
        expect(replaced && made && first.Result is { } a && Shade(a) == 1 && third.Result is { } c && Shade(c) == 3 && string.Join(", ", asked) == "1 fast, 3",
            "one still at a time: the second request, waiting, is replaced by the third (null), the first and the third made; asked for: " + string.Join(", ", asked));

        asked.Clear();
        Task<BitmapSource?> held;
        using (WorkScheduler.HoldBackground())
        {
            held = frames.FrameAsync("D:/media/a.mp4", 4, 270, fast: false, CancellationToken.None);
            Thread.Sleep(350);
            expect(asked.Count == 0 && !held.IsCompleted, "while a drag and drop holds background work, no still is asked for");
        }

        expect(held.Wait(2000) && held.Result is { } d && Shade(d) == 4 && asked.Count == 1, "let go, the still waiting is made");

        using (WorkScheduler.HoldBackground())
        {
            var dropped = frames.FrameAsync("D:/media/a.mp4", 5, 270, fast: false, CancellationToken.None);
            frames.Cancel();
            expect(dropped.Wait(2000) && dropped.Result is null, "Cancel lets go of the request waiting (null)");
        }

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = frames.FrameAsync("D:/media/a.mp4", 6, 270, fast: false, stop.Token);
        expect(cancelled.Wait(2000) && cancelled.Result is null, "a request already cancelled is never made (null)");
    }

    private static int Shade(BitmapSource picture)
    {
        var pixel = new byte[1];
        picture.CopyPixels(pixel, 1, 0);
        return pixel[0];
    }

    private static void SpinUntil(Func<bool> done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 2000)
            Thread.Sleep(5);
    }
}
