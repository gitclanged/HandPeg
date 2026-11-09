using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.3.1-Hotfix:
/// time-display-tabular: the times read HH:MM:SS.mmm (a period before the milliseconds; a colon before frames only), and HH:MM:SS:mmm
/// is still read; 5.3.2: frame numbers three digits above 100 fps (one length across 0..359 at 360 fps); a real TimeReadout never
/// narrower than a twelve-character time nor than the widest text it has shown, whatever its length (so nothing beside it moves
/// during playback), the scrubber's with 8 DIPs spare, and gives its room back when its text is collapsed; its TextBlock gets tabular figures; the
/// four running times are TimeReadouts (or tabular) in the XAML, the scrubber's gutter is 6 DIPs and its filmstrip rounds to pixels.
/// media-browser-autofill-columns: VirtualizingTilePanel.Columns as numbers (two or more abreast from 190 DIPs, sharing the width
/// exactly), then the real Media Browser pane laid out with no window at four widths: its tiles fill the row edge to edge, equal,
/// their pictures 16:9 at the width they got, and the pane decodes its pictures for that width.
/// Every line says what it proved.
/// </summary>
public static class ReadoutAndTileTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("time-display-tabular", "# The running times: frame numbers as wide as the rate needs, held still by tabular figures and a width that never shrinks.", Readouts);
        yield return Case("media-browser-autofill-columns", "# The Media Browser's tiles: two or more abreast from 190 DIPs, stretched to fill the pane edge to edge, pictures 16:9.", Tiles);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>, StringBuilder> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "FAILED: ").Append(what).Append('\n');
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

    // ----- time-display-tabular -----

    private static void Readouts(Action<bool, string> expect, StringBuilder text)
    {
        var (frames, rate) = (TimeDisplay.UseFrames, TimeDisplay.FrameRate);
        try
        {
            TimeDisplay.UseFrames = false;
            var written = TimeDisplay.Format(1921.359);
            expect(written == "00:32:01.359", $"milliseconds follow a period: 1921.359 s is {written}");
            expect(TimeDisplay.TryParse("00:32:01.359", out var read, out _) && Math.Abs(read - 1921.359) < 1e-9, "and are read back");
            expect(TimeDisplay.TryParse("00:32:01:359", out read, out _) && Math.Abs(read - 1921.359) < 1e-9, "a colon before three digits of milliseconds is still read (00:32:01:359)");
            expect(!TimeDisplay.TryParse("00:32:01:35", out _, out _), "but not a colon before two digits, in the milliseconds format");
            TimeDisplay.UseFrames = true;
            TimeDisplay.FrameRate = 30;
            written = TimeDisplay.Format(1921.5);
            expect(written == "00:32:01:15", $"a colon comes before frames only, in the frames format: {written}");

            // 5.3.2: above 100 fps every frame number is three digits, so the text never changes length within a second.
            TimeDisplay.FrameRate = 120;
            written = TimeDisplay.Format(1921 + 5 / 120.0);
            expect(written == "00:32:01:005", $"at 120 fps frame 5 is three digits: {written}");
            TimeDisplay.FrameRate = 360;
            written = TimeDisplay.Format(1921 + 359 / 360.0);
            expect(written == "00:32:01:359", $"at 360 fps frame 359 is {written}");
            var lengths = Enumerable.Range(0, 360).Select(f => TimeDisplay.Format(1921 + f / 360.0).Length).Distinct().ToList();
            expect(lengths is [12], $"at 360 fps frames 0 to 359 are all one length ({string.Join(", ", lengths)} characters)");
            expect(TimeDisplay.TryParse("00:32:01:059", out read, out _) && Math.Abs(read - (1921 + 59 / 360.0)) < 1e-9, "and a three-digit frame number is read back");
            expect(Timecode.FrameDigits(30) == "D2" && Timecode.FrameDigits(100) == "D2" && Timecode.FrameDigits(101) == "D3" && Timecode.FrameDigits(1001) == "D4",
                "two digits up to 100 fps, three above, four above 1000");
        }
        finally
        {
            (TimeDisplay.UseFrames, TimeDisplay.FrameRate) = (frames, rate);
        }

        // A real readout: letters of very different widths stand in for digits a proportional face would set differently.
        // Laid out as a window lays it out: a text changed re-measures the TextBlock, and the readout only when that one's size changed.
        var shown = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
        var readout = new TimeReadout { Child = shown };
        readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        readout.Arrange(new Rect(readout.DesiredSize));
        double Measure(string value)
        {
            shown.Text = value;
            readout.UpdateLayout();
            return readout.DesiredSize.Width;
        }

        expect(Typography.GetNumeralAlignment(shown) == FontNumeralAlignment.Tabular, "its TextBlock gets tabular figures");
        var reserve = new TextBlock { Text = "00:00:00:000", FontFamily = shown.FontFamily, FontSize = shown.FontSize };
        Typography.SetNumeralAlignment(reserve, FontNumeralAlignment.Tabular);
        reserve.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var floor = reserve.DesiredSize.Width;
        expect(Math.Abs(Measure("ii") - floor) < 0.01, "it is never narrower than a twelve-character time (00:00:00:000) in its face");
        var wide = Measure("WWWWWWWWWWWWWWWW");
        expect(wide > floor + 1, "a wider text widens it");
        expect(Math.Abs(Measure("iiiiii") - wide) < 0.01 && Math.Abs(Measure("ii") - wide) < 0.01, "and a narrower text of any length never makes it narrower again");
        shown.Visibility = Visibility.Collapsed;
        readout.UpdateLayout();
        expect(readout.DesiredSize.Width == 0, "collapsed, it takes no room");
        shown.Visibility = Visibility.Visible;
        expect(Math.Abs(Measure("ii") - floor) < 0.01, "and shown again it starts again from the twelve-character time");

        // The scrubber's readouts as the window has them (Consolas, 8 DIPs spare): one width across 11, 12 and 13 characters,
        // in frames at 30 and 360 fps and in milliseconds.
        var scrubber = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        var held = new TimeReadout { Child = scrubber, Spare = 8 };
        held.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        held.Arrange(new Rect(held.DesiredSize));
        double Held(string value)
        {
            scrubber.Text = value;
            held.UpdateLayout();
            return held.DesiredSize.Width;
        }

        var widths = new[] { "00:00:01:05", "00:00:01:105", "00:00:01.105", "00:00:01:05", "00:00:01:359", "00:00:01:00" }.Select(Held).ToList();
        Held("00:00:01:105");
        var twelve = scrubber.DesiredSize.Width;
        expect(widths.All(w => Math.Abs(w - widths[0]) < 0.01) && Math.Abs(widths[0] - (twelve + 8)) < 0.01,
            "the scrubber's readout keeps one width from 00:00:01:05 to 00:00:01:105, 00:00:01.105 and back: a twelve-character time and 8 DIPs");
        var longer = Held("100:00:01:105");
        expect(longer > widths[0] && Math.Abs(Held("00:00:01:05") - longer) < 0.01, "a thirteen-character time widens it once, and it stays that wide");

        // Where it is used.
        var src = Path.Combine(RepositoryRoot(), "src");
        var main = XDocument.Load(Path.Combine(src, "MainWindow.xaml"));
        var readouts = main.Descendants().Where(e => e.Name.LocalName == "TimeReadout").ToList();
        var position = Named(main, "PositionTextBlock")?.Parent;
        expect(position?.Name.LocalName == "TimeReadout", "the position by the Sequence Overview Scrubber (PositionTextBlock) is held in a TimeReadout");
        expect(readouts.Count(r => r.Descendants().Any(t => Attr(t, "Text") == "{Binding DurationText}")) == 1, "so is the duration at the scrubber's right end");
        expect(readouts.Count(r => r.Descendants().Any(t => t.Name.LocalName == "Run" && Attr(t, "Text") == "{Binding PositionText, Mode=OneWay}")) == 1,
            "so is the playback bar's position and duration");
        var subtitles = XDocument.Load(Path.Combine(src, "Views", "SubtitleStudioPane.xaml"));
        expect(Named(subtitles, "TimeText")?.Parent?.Name.LocalName == "TimeReadout", "so is the Subtitle Studio's time (TimeText)");
        var voiceover = XDocument.Load(Path.Combine(src, "Views", "VoiceoverStudioPane.xaml"));
        expect(Attr(Named(voiceover, "RecordTimeText"), "Typography.NumeralAlignment") == "Tabular", "the voiceover's count (RecordTimeText, a sentence) has tabular figures");
        expect(Attr(position, "Margin") == "12,0,6,0", "the gutter between the position and the scrubber is 6 DIPs (it was 12)");
        var duration = readouts.Single(r => r.Descendants().Any(t => Attr(t, "Text") == "{Binding DurationText}"));
        expect(Attr(position, "Spare") == "8" && Attr(duration, "Spare") == "8", "both of the scrubber's readouts keep 8 DIPs spare (5.3.2)");
        expect(Attr(Named(main, "PositionTextBlock"), "TextAlignment") == "Right", "the position sits against the scrubber, its spare room on its left");
        expect(Attr(Named(main, "FilmstripList"), "UseLayoutRounding") == "True", "the scrubber's filmstrip lays its cells out on whole device pixels");
    }

    // ----- media-browser-autofill-columns -----

    private static void Tiles(Action<bool, string> expect, StringBuilder text)
    {
        // The numbers: the slider's three tile sizes (with the tile's 22 DIPs round its picture) across a range of widths.
        foreach (var tile in new[] { 118.0, 230, 442 })
        {
            var row = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"   a tile of {tile:0}:"));
            foreach (var width in new[] { 150.0, 189, 190, 240, 300, 460, 700, 1000 })
            {
                var (columns, cell) = VirtualizingTilePanel.Columns(width, tile);
                row.Append(string.Create(CultureInfo.InvariantCulture, $"  {width:0} = {columns} x {cell:0.##}"));
                var shares = Math.Abs(columns * cell - width) < 1e-9;
                var abreast = width < VirtualizingTilePanel.TwoColumnsFrom ? columns == 1 : columns >= 2;
                var fits = columns <= 2 || cell >= tile;
                if (!shares || !abreast || !fits)
                    expect(false, string.Create(CultureInfo.InvariantCulture, $"{width:0} DIPs, a tile of {tile:0}: {columns} columns of {cell:0.##}"));
            }

            text.Append(row).Append('\n');
        }

        expect(true, "the columns share the width exactly; one below 190 DIPs, two or more from 190; past two, none narrower than asked");

        // The real pane, with no window: seven videos, the size slider at 40 (a tile of about 209).
        var settings = AppSettings.Current;
        var (size, view) = (settings.BrowserTileSize, settings.BrowserView);
        try
        {
            (settings.BrowserTileSize, settings.BrowserView) = (40, "Grid");
            var pane = new MediaBrowserPane();
            pane.Items.ItemsSource = Enumerable.Range(1, 7).Select(i => new MediaBrowserItem($"D:/media/clip{i}.mp4", MediaBrowserKind.Video, $"clip{i}.mp4")).ToList();
            foreach (var width in new[] { 180.0, 250, 330, 700 })
            {
                pane.Measure(new Size(width, 900));
                pane.Arrange(new Rect(0, 0, width, 900));
                pane.UpdateLayout();
                var panel = Find<VirtualizingTilePanel>(pane, _ => true);
                if (panel is null)
                {
                    expect(false, $"the tiles' panel is laid out at {width:0}");
                    continue;
                }

                var tiles = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(panel)).Select(i => (FrameworkElement)VisualTreeHelper.GetChild(panel, i))
                    .Select(t => (Tile: t, Slot: LayoutInformation.GetLayoutSlot(t))).ToList();
                var firstRow = tiles.Where(t => t.Slot.Top < 0.5).ToList();
                var columns = firstRow.Count;
                var right = firstRow.Count > 0 ? firstRow.Max(t => t.Slot.Right) : 0;
                var equal = tiles.All(t => Math.Abs(t.Slot.Width - panel.CellWidth) < 0.01);
                var pictures = tiles.Select(t => Find<AspectBox>(t.Tile, _ => true)).ToList();
                var shaped = pictures.All(p => p is not null && p.ActualWidth > 0 && Math.Abs(p.ActualHeight - Math.Round(p.ActualWidth * 9 / 16)) < 0.01);
                var decoded = (double)typeof(MediaBrowserPane).GetField("_shownTileWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pane)!;
                var wanted = panel.ActualWidth < VirtualizingTilePanel.TwoColumnsFrom ? 1 : 2;
                var holds = columns >= wanted && Math.Abs(right - panel.ActualWidth) < 0.5 && equal && shaped && Math.Abs(decoded - (panel.CellWidth - 22)) < 0.01;
                expect(holds, string.Create(CultureInfo.InvariantCulture,
                    $"a pane {width:0} wide: {(wanted == 1 ? "one column" : "two or more abreast")}, the row filled to its right edge, the tiles equal, their pictures 16:9, decoded for the width shown")
                    + (holds ? "" : string.Create(CultureInfo.InvariantCulture,
                        $" (panel {panel.ActualWidth:0.##}, cell {panel.CellWidth:0.##}, {tiles.Count} tiles, {columns} in the first row ending at {right:0.##}, equal {equal}, 16:9 {shaped} [{string.Join(" ", pictures.Select(p => p is null ? "none" : $"{p.ActualWidth:0.#}x{p.ActualHeight:0.#}"))}], decoded {decoded:0.##})")));
            }
        }
        finally
        {
            (settings.BrowserTileSize, settings.BrowserView) = (size, view);
        }
    }

    // ----- Helpers -----

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

    private static XElement? Named(XContainer xaml, string name) => xaml.Descendants().FirstOrDefault(e => Attr(e, X + "Name") == name);

    private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

    /// <summary>The repository's folder (where HandPegApp.csproj is), found upwards from the built program.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }
}
