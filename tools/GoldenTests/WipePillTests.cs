using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HandPegApp;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5A lane A: the Live Encoded Preview's split-wipe pills, collapsed to ENCODED when short of room and slid off the picture's edge (clipped to it), never hidden.
/// <para>live-encoded-wipe-pills: the pure plan (MainWindow.PlanWipePills) swept across a fitted picture, a pillarboxed one and a zoomed one at four
/// display scales; the short words (ShortEncodedPillText); the plan applied to real pills (ApplyWipePills: both always visible, the label's words, the
/// frozen clip); a pill's width measured from its words (PillWidth) against the pill's own layout; and the source of PlaceLoopbackWipe and its
/// neighbours. Every line says what it proved; the numbers printed come from fixed inputs, never this PC's fonts.</para>
/// </summary>
public static class WipePillTests
{
    /// <summary>The cases that need no WPF Application (run before UiScaleTests makes one).</summary>
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("live-encoded-wipe-pills", "# The wipe's pills never vanish: each slides off the picture's edge with the line, clipped to the picture as the player shows it; ENCODED keeps its whole words while they fit and says ENCODED when they do not.", Pills);
    }

    /// <summary>The cases on the WPF Application UiScaleTests made (run after it).</summary>
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

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // The plan for the wipe at 0..1 over a picture (GpuPreviewEngine.PictureRectOf) in a player, as PlaceLoopbackWipe makes it.
    private static (double X, MainWindow.WipePillPlan Plan) PlanAt(double wipe, (double Left, double Top, double Width, double Height) picture, double viewWidth,
        double viewHeight, double rawWidth, double fullWidth, double scale)
    {
        var x = GpuPreviewEngine.WipeLineX(wipe, (picture.Left, picture.Width));
        return (x, MainWindow.PlanWipePills(x, picture, viewWidth, viewHeight, rawWidth, fullWidth, scale));
    }

    private static void Pills(Action<bool, string> expect, StringBuilder text)
    {
        Sweep(expect);
        PillarboxZoomAndScales(expect);
        ShortWords(expect);
        Elements(expect);
        Sources(expect);
    }

    // Two lengths alike to a millionth of a DIP (a device pixel over a scale is seldom an exact binary fraction).
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static bool Near(Rect a, Rect b) => Near(a.Left, b.Left) && Near(a.Top, b.Top) && Near(a.Right, b.Right) && Near(a.Bottom, b.Bottom);

    // ----- 1. The sweep: a fitted 16:9 picture in a 1024 x 576 player (1280 x 720 pixels) at 125 % -----

    private static void Sweep(Action<bool, string> expect)
    {
        const double rawWidth = 33.5, fullWidth = 160.25, scale = 1.25;
        var picture = GpuPreviewEngine.PictureRectOf(1920, 1080, 1, 0, 0, 1024, 576);
        var plans = Enumerable.Range(0, 101).Select(i => PlanAt(i / 100.0, picture, 1024, 576, rawWidth, fullWidth, scale)).ToArray();
        var rising = plans.Zip(plans.Skip(1)).All(p => p.Second.Plan.RawLeft > p.First.Plan.RawLeft && p.Second.Plan.EncodedLeft > p.First.Plan.EncodedLeft);
        expect(rising, "the sweep, wipe 0 to 1 in 101 steps over a fitted 1920 x 1080 picture: both pills move right with the line at every step (never hidden, never parked)");
        expect(plans.All(p => Near(p.Plan.Clip, new Rect(0, 0, 1024, 576)) && Near(p.Plan.Top, 14.4)),
            F($"the pills' clip is the picture within the player (0,0 1024 x 576) and their top 14 DIPs below the picture's, on a device pixel ({plans[0].Plan.Top:0.###} at 125 %)"));

        var flips = plans.Zip(plans.Skip(1)).Count(p => p.First.Plan.EncodedShort != p.Second.Plan.EncodedShort);
        var firstShort = Array.FindIndex(plans, p => p.Plan.EncodedShort);
        var expected = Array.FindIndex(plans, p => 1024 - 4 - (p.X + 10) < fullWidth);
        expect(flips == 1 && !plans[0].Plan.EncodedShort && firstShort == expected && firstShort == 83,
            F($"ENCODED's whole words (160.25 DIPs) until the room right of the line runs out, then the short ones: one change only, at wipe {firstShort / 100.0:0.00} (the line at {plans[firstShort].X:0.##} DIPs: 1024 - 4 - {plans[firstShort].X + 10:0.##} < 160.25)"));

        var (_, zero) = plans[0];
        var (_, two) = plans[2];
        var (_, one) = plans[100];
        expect(zero.RawLeft + rawWidth <= zero.Clip.Left && Near(zero.RawLeft, -43.2),
            F($"wipe 0: RAW still placed ({zero.RawLeft:0.###} DIPs), wholly left of the picture's edge, so clipped away rather than hidden"));
        expect(two.RawLeft < two.Clip.Left && two.Clip.Left < two.RawLeft + rawWidth,
            F($"wipe 0.02: RAW part on the picture, part past its edge ({two.RawLeft:0.###} to {two.RawLeft + rawWidth:0.###} DIPs): it slides off, cut by the clip"));
        expect(one.EncodedLeft >= one.Clip.Right && one.EncodedShort && Near(one.EncodedLeft, 1033.6),
            F($"wipe 1: ENCODED (its short words) placed at {one.EncodedLeft:0.###} DIPs, wholly right of the picture, so clipped away rather than hidden"));
        var (_, half) = plans[50];
        expect(Near(half.RawLeft, 468.8) && Near(half.EncodedLeft, 521.6) && !half.EncodedShort,
            F($"wipe 0.5 (the line at 512): RAW ends 10 DIPs left of it ({half.RawLeft:0.###} + 33.5) and ENCODED starts 10 right of it ({half.EncodedLeft:0.###}), each on a device pixel; whole words"));
    }

    // ----- 2. A pillarboxed picture, a zoomed one, and four display scales -----

    private static void PillarboxZoomAndScales(Action<bool, string> expect)
    {
        const double rawWidth = 33.5, fullWidth = 160.25;

        // A 1080 x 1920 canvas (portrait) in a 16:9 player: the picture a strip in the middle, the pillarbox either side.
        var tall = GpuPreviewEngine.PictureRectOf(1080, 1920, 1, 0, 0, 1024, 576);
        var (_, tallZero) = PlanAt(0, tall, 1024, 576, rawWidth, fullWidth, 1);
        expect(Near(tallZero.Clip.Left, 350) && Near(tallZero.Clip.Right, 674) && tallZero.RawLeft + rawWidth <= tallZero.Clip.Left && Near(tallZero.RawLeft, 306),
            F($"a portrait picture pillarboxed: the clip is the picture alone ({tallZero.Clip.Left:0.###} to {tallZero.Clip.Right:0.###} at 100 %), and at wipe 0 RAW slides into the pillarbox ({tallZero.RawLeft:0.###}), clipped there, not drawn over the bars"));
        var (xTallHalf, tallHalf) = PlanAt(0.5, tall, 1024, 576, rawWidth, fullWidth, 1);
        expect(tallHalf.EncodedShort && Near(tallHalf.EncodedLeft, 522),
            F($"the same picture at wipe 0.5 (the line at {xTallHalf:0.##}): {674 - 4 - (xTallHalf + 10):0.##} DIPs to the picture's edge, too few for the whole words (160.25): ENCODED"));

        // Zoomed 2x and panned a quarter right: the picture runs past the player on every side; the clip is the player.
        var zoomed = GpuPreviewEngine.PictureRectOf(1920, 1080, 2, 0.25, 0, 1024, 576);
        var (xFits, fits) = PlanAt(0.4, zoomed, 1024, 576, rawWidth, fullWidth, 1);
        var (xTight, tight) = PlanAt(0.45, zoomed, 1024, 576, rawWidth, fullWidth, 1);
        var zoomedRoom = zoomed.Left + zoomed.Width - 4 - (xTight + 10);
        expect(Near(fits.Clip, new Rect(0, 0, 1024, 576)) && Near(fits.Top, 14) && !fits.EncodedShort,
            F($"zoomed 2x, panned a quarter: the picture at {zoomed.Left:0.##},{zoomed.Top:0.##}, {zoomed.Width:0.##} x {zoomed.Height:0.##}; the clip is the player (0,0 1024 x 576), the pills 14 DIPs below the player's top; the line at {xFits:0.##}: whole words"));
        expect(tight.EncodedShort && zoomedRoom >= fullWidth,
            F($"the line at {xTight:0.##}: the whole words would fit before the zoomed picture's right edge ({zoomedRoom:0.##} DIPs) but not before the player's ({1024 - 4 - (xTight + 10):0.##}): the visible edge decides, ENCODED"));

        // Every left, top and clip edge on a device pixel at 100, 125, 150 and 200 %.
        (double W, double H, (double Left, double Top, double Width, double Height) Picture)[] views =
        [
            (1024, 576, GpuPreviewEngine.PictureRectOf(1920, 1080, 1, 0, 0, 1024, 576)),
            (1024, 576, tall),
            (1024, 576, zoomed),
            (1001.6, 563.3, GpuPreviewEngine.PictureRectOf(1920, 1080, 1, 0, 0, 1001.6, 563.3)),
        ];
        var snapped = true;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            foreach (var (w, h, picture) in views)
            {
                for (var i = 0; i <= 20; i++)
                {
                    var (_, plan) = PlanAt(i / 20.0, picture, w, h, 33.3, fullWidth, scale);
                    foreach (var value in new[] { plan.RawLeft, plan.EncodedLeft, plan.Top, plan.Clip.Left, plan.Clip.Top, plan.Clip.Right, plan.Clip.Bottom })
                        snapped &= Math.Abs(value * scale - Math.Round(value * scale)) < 1e-6;
                }
            }
        }

        expect(snapped, "at 100, 125, 150 and 200 %, over a fitted, a pillarboxed, a zoomed and an odd-sized picture: every pill's left, their top and the clip's edges lie on device pixels");
        var off = MainWindow.PlanWipePills(500, (1200, 0, 300, 200), 1024, 576, rawWidth, fullWidth, 1.25);
        expect(off.Clip == new Rect(0, 0, 0, 0), "a picture panned wholly off the player: an empty clip (never a rectangle of negative size), nothing of the pills shown");
        var bad = MainWindow.PlanWipePills(500, (0, 0, 1024, 576), 1024, 576, rawWidth, fullWidth, double.NaN);
        expect(Near(bad.RawLeft, 456) && Near(bad.EncodedLeft, 510) && Near(bad.Top, 14), "a display scale not known (NaN): placed as at 100 %");
    }

    // ----- 3. The short words -----

    private static void ShortWords(Action<bool, string> expect)
    {
        (string Whole, string Short)[] words =
        [
            ("ENCODED \u00B7 HEVC QSV \u00B7 12 Mbps", "ENCODED"),
            ("ENCODED \u00B7 x264 veryslow \u00B7 CRF 22", "ENCODED"),
            ("ENCODED \u00B7 HEVC \u00B7 STAND-IN \u00B7 12 Mbps", "ENCODED"),
            ("ENCODED \u00B7 HEVC QSV \u00B7 STARTING", "STARTING"),
            ("ENCODED \u00B7 x264 veryslow \u00B7 STARTING", "STARTING"),
            ("RAW WHILE PLAYING \u00B7 x264 veryslow", "RAW WHILE PLAYING"),
            ("ENCODER FAILED 0x80004005", "ENCODER FAILED"),
            ("NO ENCODER", "NO ENCODER"),
            ("NO DECODER", "NO DECODER"),
            ("NO LOOPBACK", "NO LOOPBACK"),
            ("ENCODED", "ENCODED"),
        ];
        foreach (var (whole, shortWords) in words)
        {
            var made = MainWindow.ShortEncodedPillText(whole);
            expect(made == shortWords, F($"\"{whole}\" short: \"{made}\""));
        }

        var qsv = ExportLoopbackSettings.From("hevc_qsv", false, false, 12000, 22, "veryslow", 60, out _)!;
        expect(MainWindow.ShortEncodedPillText(qsv.PillText) == "ENCODED", F($"the settings' own pill words (\"{qsv.PillText}\") shorten to ENCODED"));

        // 5.5A review (A2-1): the words a starting encoder gets (EncodedPillText) shorten to STARTING, never ENCODED over the plain picture;
        // a stand-in that runs still shortens to ENCODED (it is encoding: the flyout says which card).
        var starting = MainWindow.EncodedPillText(qsv, new ExportLoopbackStatus(ExportLoopbackState.Starting, "", 0, 0, 0, 0), false);
        var standIn = MainWindow.EncodedPillText(qsv, new ExportLoopbackStatus(ExportLoopbackState.Running, "HEVC QSV", 8, 30, 0, 0, OnAskedCard: false), false);
        expect(MainWindow.ShortEncodedPillText(starting) == "STARTING" && MainWindow.ShortEncodedPillText(standIn) == "ENCODED",
            F($"a starting encoder's words (\"{starting}\") shorten to STARTING; a running stand-in's (\"{standIn}\") to ENCODED"));

        // 5.5A review (A2-2): the words follow play and pause (a paused-only software encoder), which the view model's IsPlaying now
        // applies at once (Sources checks the handler).
        var x264 = ExportLoopbackSettings.From("libx264", true, false, 12000, 22, "veryslow", 60, out _)!;
        var pausedOnly = new ExportLoopbackStatus(ExportLoopbackState.Running, "x264", 40, 1, 0, 0, PausedOnly: true);
        var (whilePlaying, whilePaused) = (MainWindow.EncodedPillText(x264, pausedOnly, true), MainWindow.EncodedPillText(x264, pausedOnly, false));
        expect(whilePlaying.StartsWith("RAW WHILE PLAYING · ", StringComparison.Ordinal) && whilePaused == x264.PillText,
            F($"a paused-only encoder: \"{whilePlaying}\" while playing, \"{whilePaused}\" when paused"));
    }

    // ----- 4. Real pills (STA, no Application) -----

    private static void Elements(Action<bool, string> expect)
    {
        const string whole = "ENCODED \u00B7 HEVC QSV \u00B7 12 Mbps";
        var raw = MainWindow.WipePill("RAW");
        var encoded = MainWindow.WipePill(whole);
        var pills = new Canvas { IsHitTestVisible = false };
        pills.Children.Add(raw);
        pills.Children.Add(encoded);
        var label = (TextBlock)encoded.Child;
        raw.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        const double scale = 1.25;
        var full = MainWindow.PillWidth(label, whole, scale);
        var picture = GpuPreviewEngine.PictureRectOf(1920, 1080, 1, 0, 0, 1024, 576);

        var (placed, wordsRight, clipRight, frozen, shortSeen, wholeSeen) = (true, true, true, true, false, false);
        Geometry? lastClip = null;
        var clipKept = true;
        foreach (var wipe in new[] { 0, 0.02, 0.5, 0.9, 1.0 })
        {
            var (_, plan) = PlanAt(wipe, picture, 1024, 576, raw.DesiredSize.Width, full, scale);
            MainWindow.ApplyWipePills(pills, raw, encoded, plan, whole);
            placed &= raw.Visibility == Visibility.Visible && encoded.Visibility == Visibility.Visible && Canvas.GetLeft(raw) == plan.RawLeft && Canvas.GetLeft(encoded) == plan.EncodedLeft
                      && Canvas.GetTop(raw) == plan.Top && Canvas.GetTop(encoded) == plan.Top;
            wordsRight &= label.Text == (plan.EncodedShort ? "ENCODED" : whole);
            (shortSeen, wholeSeen) = (shortSeen || plan.EncodedShort, wholeSeen || !plan.EncodedShort);
            clipRight &= pills.Clip is RectangleGeometry geometry && geometry.Rect == plan.Clip;
            frozen &= pills.Clip.IsFrozen;
            clipKept &= lastClip is null || ReferenceEquals(lastClip, pills.Clip);
            lastClip = pills.Clip;
        }

        expect(placed, "real pills at wipes 0, 0.02, 0.5, 0.9 and 1: both Visible every time, each at its planned left and top");
        expect(wordsRight && shortSeen && wholeSeen, "the label shows the whole words where the plan has room and ENCODED where it has not (both seen across the wipes)");
        expect(clipRight && frozen && clipKept, "the pills' layer is clipped by a frozen RectangleGeometry equal to the plan's rectangle, made once while the picture stays where it is");
        var zoomed = GpuPreviewEngine.PictureRectOf(1920, 1080, 2, 0.25, 0, 1024, 576);
        var tall = GpuPreviewEngine.PictureRectOf(1080, 1920, 1, 0, 0, 1024, 576);
        MainWindow.ApplyWipePills(pills, raw, encoded, PlanAt(0.5, tall, 1024, 576, raw.DesiredSize.Width, full, scale).Plan, whole);
        expect(!ReferenceEquals(lastClip, pills.Clip) && pills.Clip.IsFrozen && ((RectangleGeometry)pills.Clip).Rect.Left > 300,
            "a picture that moved (a portrait canvas): a new frozen clip, the picture's own rectangle");
        MainWindow.ApplyWipePills(pills, raw, encoded, PlanAt(0.3, zoomed, 1024, 576, raw.DesiredSize.Width, full, scale).Plan, "NO ENCODER");
        expect(label.Text == "NO ENCODER" && raw.Visibility == Visibility.Visible && encoded.Visibility == Visibility.Visible, "other words (NO ENCODER) shown as they are, the pills still visible");

        // A pill's width from its words, as its own layout measures it (padding 7 + 7, border 1 + 1), at 100 % and 150 %.
        var agrees = true;
        foreach (var words in new[] { "RAW", "ENCODED", whole, "RAW WHILE PLAYING \u00B7 x264 veryslow" })
        {
            var pill = MainWindow.WipePill(words);
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            foreach (var pixelsPerDip in new[] { 1.0, 1.5 })
                agrees &= Math.Abs(MainWindow.PillWidth((TextBlock)pill.Child, words, pixelsPerDip) - pill.DesiredSize.Width) <= 1;
        }

        expect(agrees, "PillWidth (the words' FormattedText in the label's own font, plus 16) agrees with the pill's own measured width within 1 DIP, for four texts at 100 % and 150 %");
    }

    // ----- 5. The source -----

    private static void Sources(Action<bool, string> expect)
    {
        var window = Read("MainWindow.LoopbackPreview.cs");
        var place = Method(window, "private void PlaceLoopbackWipe()");
        var ensure = Method(window, "private void EnsureLoopbackWipe()");
        var set = Method(window, "private void SetEncodedPill(string text)");
        expect(place.Contains("PlanWipePills(", StringComparison.Ordinal) && place.Contains("ApplyWipePills(", StringComparison.Ordinal)
               && CountOf(place, "Visibility.Collapsed") == 1 && place.Contains("_loopbackWipeLayer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;", StringComparison.Ordinal),
            "PlaceLoopbackWipe plans and applies the pills; the only Collapsed left is the wipe layer's own (the preview off, or not the wipe)");
        expect(place.Contains("PillWidth(words, _loopbackEncodedText, scale)", StringComparison.Ordinal) && !place.Contains("encoded.Child as UIElement", StringComparison.Ordinal),
            "the whole words' width is measured from the words kept, never the label's current (maybe short) text");
        expect(InOrder(ensure, "_loopbackPillLayer = new Canvas { IsHitTestVisible = false };", "_loopbackPillLayer.Children.Add(_loopbackRawPill);", "_loopbackPillLayer.Children.Add(_loopbackEncodedPill);",
                   "_loopbackWipeLayer.Children.Add(_loopbackDivider);", "_loopbackWipeLayer.Children.Add(_loopbackPillLayer);", "_loopbackWipeLayer.Children.Add(_loopbackWipeHandle);")
               && !ensure.Contains("_loopbackPillLayer = new Canvas { ClipToBounds", StringComparison.Ordinal) && ensure.Contains("WipePill(_loopbackEncodedText)", StringComparison.Ordinal),
            "the pills live in a layer of their own (no ClipToBounds: its clip is the picture), between the divider and the handle, which it never clips; ENCODED made with the words kept");
        expect(set.Contains("if (text == _loopbackEncodedText)", StringComparison.Ordinal) && set.Contains("PlaceLoopbackWipe();", StringComparison.Ordinal) && !set.Contains("label.Text != text", StringComparison.Ordinal),
            "SetEncodedPill compares with the whole words kept (no full/short flicker each second) and places the pills again at once");
        expect(!window.Contains("RawPillRoom", StringComparison.Ordinal) && !window.Contains("EncodedPillRoom", StringComparison.Ordinal),
            "no pill hides for want of room any more (RawPillRoom and EncodedPillRoom gone)");
        var changed = Method(window, "private void LoopbackViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)");
        var playing = changed.IndexOf("case nameof(ViewModels.MainViewModel.IsPlaying):", StringComparison.Ordinal);
        var playCase = playing < 0 ? "" : changed[playing..changed.IndexOf("break;", playing, StringComparison.Ordinal)];
        expect(playCase.Contains("_viewModel.IsExportQualityPreview", StringComparison.Ordinal)
               && playCase.Contains("SetEncodedPill(EncodedPillText(", StringComparison.Ordinal) && playCase.Contains(".LoopbackStatus, _viewModel.IsPlaying))", StringComparison.Ordinal),
            "play and pause set the ENCODED pill's words at once (LoopbackViewModel_PropertyChanged handles IsPlaying while the preview is on), not on the next 1 s status tick");
    }

    private static int CountOf(string text, string part)
    {
        var (count, at) = (0, 0);
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
            (count, at) = (count + 1, at + part.Length);
        return count;
    }

    // ----- Source text -----

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts])).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HandPegApp.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }

    /// <summary>A member's text: from its signature to the end of its body (the next line that closes at its indent).</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..(end + 6)];
    }

    /// <summary>Whether every one of <paramref name="parts"/> is in <paramref name="text"/>, each after the one before.</summary>
    private static bool InOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, at, StringComparison.Ordinal);
            if (found < 0)
                return false;
            at = found + part.Length;
        }

        return true;
    }
}
