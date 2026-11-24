using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6.4B-Release Wave 0 executable contract. The Color Studio deck's reflow (three columns from 920 DIP, two from 640, one below at
/// most 520 wide) and the retired Output fold (Deinterlace now in Export's Advanced drawer); wheel pucks held inside the rim with a drag's
/// R/G/B delta; the two-click colour pick with its RGB/Luma readout; the YRGB overlay histogram (modes, crush/clip edges, persisted mode,
/// Video Scopes' overlay); Color Match's natural bounds and Match Strength; player samples feeding the curves (Ctrl + Hover pips,
/// Ctrl + Click / Ctrl + Shift + Click points) and the hover readout. Cases marked "Wave 1" read wiring in files Wave 0 does not own
/// (ScreenEyedropper, ColorMatch.SolveFrom, VideoScopes' skin-tone I-bar) and stay red until it lands.
/// </summary>
public static class WaveContract5_6_4B_ReleaseTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return GoldenKit.Case("wave-contract-5-6-4b-color-deck", "# The Color Studio deck reflows to 3 columns from 920 DIP, 2 from 640, else 1 (520 max); the Output fold is retired.", DeckCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-wheel-clamp", "# Wheel pucks, stems and drags stay inside the rim; a drag shows its R/G/B delta.", WheelCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-two-click-pick", "# Pick Target and Reference are two-click picks: arm, move freely, click to sample; Esc or right click cancels.", PickCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-yrgb-histogram", "# The YRGB overlay histogram: modes, crush/clip edges, a shared persisted mode, Video Scopes' overlay, the I-bar.", HistogramCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-match-strength", "# Color Match stays within natural bounds (0.75 of each reach) and Match Strength blends live from the grade before.", StrengthCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-curve-samples", "# Player samples feed the curves: Ctrl + Hover pips, Ctrl + Click (active) and Ctrl + Shift + Click (all four) points.", SamplesCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-scrub-deferral", "# A held scrub only seeks and slides lines: no PrepareAround, Color Studio or Video Scopes work until release.", ScrubDeferralCase);
        yield return GoldenKit.Case("wave-contract-5-6-4b-scrub-decode", "# Scrub decode: landing key frames shown at once, chase on dwell, no pre-roll or scrub-ahead contention, held reads yield.", ScrubDecodeTests.Case);
        yield return GoldenKit.Case("wave-contract-5-6-4b-rev7-sync", "# Overlay under owned windows, ghost-scrub dwell and prime under GhostDwell, single-video priming hold, cache cap in the play log.", SyncRev7Tests.Case);
    }

    // W3-REV-2: playhead scrub lag. Raw moves only seek (lock-free) and translate playhead visuals; everything else waits for release.
    private static void ScrubDeferralCase(Action<bool, string> expect)
    {
        var scrub = SourceProbe.MemberOfType("MainWindow", "private void ScrubPlayheadTo(");
        var seek = SourceProbe.MemberOfType("MainWindow", "private void ScrubSeek(");
        expect(Has(scrub, "ScrubSeek(") && !Has(scrub, "PlayerSeek(") && !Has(scrub, "PrepareAround") && Has(seek, "engine.Seek(") && !Has(seek, "PrepareAround("),
            "a raw scrub move seeks the engine lock-free and never calls PrepareAround");
        var end = SourceProbe.MemberOfType("MainWindow", "private void EndScrub(");
        expect(SourceProbe.InOrder(end, "ReleaseScrubGate()", "engine.PrepareAround(", "EndScrubScaling()"), "release runs PrepareAround once, after opening the gate");
        var dwell = SourceProbe.MemberOfType("MainWindow", "private DispatcherTimer NewGhostDwell(");
        expect(SourceProbe.InOrder(dwell, "IsScrubHeld", "engine.PrepareAround(", "GhostResume()"), "a GhostDwell resume runs PrepareAround once before playing on");
        var release = SourceProbe.MemberOfType("MainWindow", "private void ReleaseScrubGate(");
        expect(SourceProbe.InOrder(release, "ScrubGate.Release()", "MoveClipPlayheads()", "KeyLanes.MovePlayhead(", "KeyLanes.InvalidateVisual()", "_scrubState.RedrawOwed"),
            "the gate's release moves the clip and key-lane playheads and repaints the key diamonds once");
        var shell = SourceProbe.Src("MainWindow.xaml.cs");
        expect(SourceProbe.InOrder(shell, "FollowPlayheadInPlayer();", "if (!_scrubState.Flushing && !ScrubGate.IsHeld)", "MoveClipPlayheads();", "KeyLanes.InvalidateVisual();"),
            "held 60 Hz commits skip MoveClipPlayheads, KeyLanes.MovePlayhead and the key diamonds' repaint");

        var color = SourceProbe.Src("Views", "ColorStudioPane.xaml.cs");
        var colorChanged = SourceProbe.MemberOfType("ColorStudioPane", "private void ViewModel_PropertyChanged(");
        expect(SourceProbe.InOrder(colorChanged, "case nameof(MainViewModel.PositionMs):", "ScrubGate.IsHeld", "if (_still is not null)", "ForgetStill();", "_playheadAfterScrub = true;", "break;", "Want(afterRest: true)"),
            "Color Studio: a held scrub's commits drop the still once and skip the keyed refresh and the recapture");
        expect(Has(color, "ScrubGate.Released += ScrubGate_Released;") && Has(color, "ScrubGate.Released -= ScrubGate_Released;")
               && SourceProbe.InOrder(SourceProbe.MemberOfType("ColorStudioPane", "private void CatchUpAfterScrub("), "QueueRefresh()", "Want()"),
            "Color Studio subscribes to ScrubGate.Released while listening and refreshes and recaptures once on release");

        var scopes = SourceProbe.Src("Views", "VideoScopesPane.cs");
        var scopesChanged = SourceProbe.MemberOfType("VideoScopesPane", "private void ViewModel_PropertyChanged(");
        expect(SourceProbe.InOrder(scopesChanged, "case nameof(MainViewModel.PositionMs) when ScrubGate.IsHeld:", "_captureAfterScrub = true;", "case nameof(MainViewModel.PositionMs) when !_viewModel.IsPlaying:"),
            "Video Scopes: a held scrub's commits skip the capture");
        expect(Has(scopes, "ScrubGate.Released += ScrubGate_Released;") && Has(scopes, "ScrubGate.Released -= ScrubGate_Released;")
               && Has(SourceProbe.MemberOfType("VideoScopesPane", "private void ScrubGate_Released("), "Want"),
            "Video Scopes subscribes to ScrubGate.Released while attached and captures once on release");

        var parts = SourceProbe.MemberOfType("TimelineBar", "private void PlacePlayhead(");
        expect(Has(parts, "shown ? height != _playheadHeight : _playheadHeight != 0"), "a ruler or key-lane playhead redraws only on a height change or its first hide; moves translate it");
    }

    private static bool Has(string text, string token) => SourceProbe.IndexOf(text, token) >= 0;

    // The XAML from the Expander whose opening tag holds `header` to its matching </Expander> (nested Expanders counted); empty when absent.
    private static string ExpanderBlock(string xaml, string header)
    {
        var at = xaml.IndexOf(header, StringComparison.Ordinal);
        var start = at < 0 ? -1 : xaml.LastIndexOf("<Expander", at, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var (depth, i) = (0, start);
        while (i < xaml.Length)
        {
            var open = xaml.IndexOf("<Expander", i, StringComparison.Ordinal);
            var close = xaml.IndexOf("</Expander>", i, StringComparison.Ordinal);
            if (close < 0)
                return "";
            if (open >= 0 && open < close)
            {
                (depth, i) = (depth + 1, open + 9);
                continue;
            }

            depth--;
            i = close + "</Expander>".Length;
            if (depth == 0)
                return xaml[start..i];
        }

        return "";
    }

    private static bool Near(double a, double b, double within = 1e-9) => Math.Abs(a - b) <= within;

    private static void DeckCase(Action<bool, string> expect)
    {
        expect(ColorStudioPane.DeckModeFor(639) == ColorDeckMode.Single && ColorStudioPane.DeckModeFor(640) == ColorDeckMode.Two && ColorStudioPane.DeckModeFor(919) == ColorDeckMode.Two
               && ColorStudioPane.DeckModeFor(920) == ColorDeckMode.Wide && ColorStudioPane.DeckModeFor(double.NaN) == ColorDeckMode.Single,
            "breakpoints: 1 column under 640 DIP, 2 from 640 to 919, 3 from 920 (user decision)");
        expect(ColorStudioPane.DeckCompactMaxWidth == 520, "Compact Inspector Mode caps each column at 520 DIP");

        var narrow = ColorStudioPane.DeckColumns(920);
        var wide = ColorStudioPane.DeckColumns(2500);
        expect(narrow.Curves is >= 320 and <= 560 && narrow.Basics is >= 300 and <= 460 && narrow.Wheels > 0
               && narrow.Wheels + narrow.Curves + narrow.Basics <= 920 - 2 * ColorStudioPane.DeckGap + 1e-9,
            "at 920 DIP the three columns fit beside each other: Curves 320..560, Basics & LUT 300..460, the rest to Wheels");
        expect(Near(wide.Curves, 560) && Near(wide.Basics, 460) && Near(wide.Wheels, ColorStudioPane.WheelsColumnMax),
            "on a 2,500 DIP pane Curves stop at 560, Basics & LUT at 460 and Wheels at 720: sliders never stretch across");

        var plan = ColorStudioPane.DeckPlan(ColorDeckMode.Wide);
        expect(plan[0] is [ColorStudioPane.WheelsCard, ColorStudioPane.MatchCard] && plan[1] is [ColorStudioPane.CurvesCard] && plan[2] is [ColorStudioPane.BasicsCard, ColorStudioPane.LegacyCard],
            "wide deck: Wheels over Color Match | Curves & histogram | Basics & LUT");
        var two = ColorStudioPane.DeckPlan(ColorDeckMode.Two);
        var one = ColorStudioPane.DeckPlan(ColorDeckMode.Single);
        expect(two[0] is [ColorStudioPane.WheelsCard, ColorStudioPane.MatchCard] && two[1].Contains(ColorStudioPane.CurvesCard) && two[2].Length == 0
               && one[0].Length == 5 && one[1].Length == 0 && one[2].Length == 0,
            "two columns keep Wheels over Color Match; one column stacks every card");

        var pane = SourceProbe.Src("Views", "ColorStudioPane.xaml.cs") + SourceProbe.Src("Views", "ColorStudioPane.Folds.cs") + SourceProbe.Src("Views", "ColorStudioPane.Deck.cs")
            + SourceProbe.Src("Views", "ColorStudioPane.Writes.cs");
        expect(!Has(pane, "OutputFold") && !Has(pane, "MainViewModel.Deinterlace") && !Has(pane, "MainViewModel.FadeIn") && !Has(pane, "MainViewModel.FadeOut") && !Has(pane, "MainViewModel.Denoise"),
            "the Color Studio shows no Fade In, Fade Out, Denoise or Deinterlace switch");
        var advanced = ExpanderBlock(SourceProbe.Repo("src", "MainWindow.xaml"), "Header=\"Advanced Codec &amp; Chapter Options\"");
        expect(Has(advanced, "IsChecked=\"{Binding Deinterlace}\"") && advanced.EndsWith("</Expander>", StringComparison.Ordinal),
            "Deinterlace lives inside Export's Advanced Codec & Chapter Options drawer (its own Expander block)");
        expect(Has(SourceProbe.Src("ViewModels", "MainViewModel.Project.cs"), "FadeIn = FadeIn") && Has(SourceProbe.Src("ViewModels", "MainViewModel.Project.cs"), "Denoise = Denoise"),
            "legacy projects' fades and Denoise are still read and written silently");
        expect(SourceProbe.InOrder(SourceProbe.Member(SourceProbe.Src("Views", "ColorStudioPane.Deck.cs"), "private Grid BuildDeck("), "deck.SizeChanged +=", "ReflowColorColumns(e.NewSize.Width)"),
            "the deck reflows on its own width changes");
    }

    private static void WheelCase(Action<bool, string> expect)
    {
        var (x, y) = WheelPush.ClampToDisc(0.6, 0.8);
        expect(Near(Math.Sqrt(x * x + y * y), WheelPush.Reach) && Near(x / y, 0.75) && WheelPush.ClampToDisc(0.1, -0.2) == (0.1, -0.2) && WheelPush.ClampToDisc(double.NaN, 1) == (0, 0),
            "an offset beyond the rim scales back along its direction; inside it is kept; a non-finite one is the centre");

        var centre = new Point(50, 50);
        var outside = (1.0, -1.0, -1.0);
        expect(WheelPush.RimShare(outside) > 1 && (ColorWheelsControl.PuckAt(centre, 40, outside) - centre).Length <= 40 + 1e-9,
            "a card value beyond the rim draws its puck and stem on the rim, never past it");

        var wheel = new ColorWheelsControl { Title = "Lift" };
        wheel.Measure(new Size(120, 200));
        wheel.Arrange(new Rect(0, 0, 120, 170));
        wheel.Value = outside;
        var press = wheel.DiscCentre;
        var began = wheel.BeginDrag(press);
        wheel.DragTo(new Point(press.X + 400, press.Y + 400));
        var during = wheel.DragDelta;
        expect(began && WheelPush.RimShare(wheel.Value) <= 1 + 1e-9, "a drag from beyond the rim, pulled far outside, keeps the push within the rim");
        wheel.EndDrag();
        expect(during is not null && wheel.DragDelta is null, "the drag delta shows only while dragging");
        expect(ColorWheelsControl.DeltaText((0, 0, 0), (0.05, -0.02, 0.01)) == "R +0.05  G -0.02  B +0.01" && ColorWheelsControl.DeltaText((0.2, 0.2, 0.2), (0.2, 0.2, 0.2)) == "R 0.00  G 0.00  B 0.00",
            "the delta reads +R/+G/+B with signs and two decimals");

        var render = SourceProbe.MemberOfType("ColorWheelsControl", "protected override void OnRender(");
        var drag = SourceProbe.MemberOfType("ColorWheelsControl", "public void DragTo(");
        expect(Has(render, "PuckAt(") && Has(render, "DeltaText(") && Has(drag, "WheelPush.ClampToDisc("), "OnRender places the puck through PuckAt and shows DeltaText; DragTo clamps through ClampToDisc");
    }

    private static void PickCase(Action<bool, string> expect)
    {
        var pick = new TwoClickPick();
        var (armed, sampled, picked) = (new List<bool>(), 0, ((double R, double G, double B)?)null);
        pick.ArmedChanged += on => armed.Add(on);
        pick.Sampling += _ => sampled++;
        pick.Picked += colour => picked = colour;

        expect(pick.Input(PickInput.LeftClick) == PickOutcome.Ignored && !pick.IsArmed, "an idle pick ignores clicks");
        expect(pick.Toggle() && pick.IsArmed && armed is [true], "one click on the button arms it (no button held afterwards)");
        pick.Move(null);
        expect(pick.Input(PickInput.LeftClick) == PickOutcome.Ignored && pick.IsArmed, "a left click over nothing readable keeps it armed");
        pick.Move((0.2, 0.4, 0.6));
        pick.Move((1.0, 0.5, 0.0));
        expect(sampled == 2 && pick.Last == (1.0, 0.5, 0.0), "moving the pointer samples live");
        expect(pick.Input(PickInput.LeftClick) == PickOutcome.Picked && picked == (1.0, 0.5, 0.0) && !pick.IsArmed && armed is [true, false],
            "one left click samples the colour under the pointer and ends the pick");

        pick.Arm();
        expect(pick.Input(PickInput.RightClick) == PickOutcome.Cancelled && !pick.IsArmed, "a right click cancels");
        pick.Arm();
        expect(pick.Input(PickInput.Escape) == PickOutcome.Cancelled && !pick.IsArmed, "Esc cancels");
        pick.Arm();
        expect(!pick.Toggle() && !pick.IsArmed && picked == (1.0, 0.5, 0.0), "the button clicked again while armed cancels without a colour");
        expect(TwoClickPick.Readout((1.0, 0.5, 0.0)) == $"R 255  G 128  B 0  Y {VideoScopes.Luma(255, 128, 0)}", "the loupe's readout: 8-bit R, G, B and BT.709 luma");

        expect(TwoClickPick.ReadoutLines((1, 1, 1)) == "R 255  G 255  B 255\nY 255", "the loupes show the readout on two compact lines");
        var match = SourceProbe.Src("MainWindow.ColorMatch.cs");
        var loupes = match + SourceProbe.Src("Controls", "ScreenEyedropper.Loupe.cs");
        expect(Has(loupes, "TwoClickPick.ReadoutLines(") && !Has(SourceProbe.Member(match, "private Popup MakeLoupe("), "CharacterEllipsis"),
            "neither loupe clips its readout with an ellipsis");
        expect(Has(match, "PreviewMouseRightButtonDown += Overlay_PreviewMouseRightButtonDown") && Has(match, "TwoClickPick.ReadoutLines("),
            "Pick Target: a right click cancels and the player loupe shows the live readout");

        // Wave 1: Reference is the same two-click pick on any monitor, its topmost loupe with the readout.
        var dropper = SourceProbe.Src("Controls", "ScreenEyedropper.cs") + SourceProbe.Src("Controls", "ScreenEyedropper.Loupe.cs");
        expect(Has(dropper, "TwoClickPick") && Has(dropper, "PickInput.RightClick") && Has(dropper, "PickInput.Escape") && Has(dropper, "TwoClickPick.ReadoutLines("),
            "Wave 1: ScreenEyedropper arms on a click and samples on the next left click (TwoClickPick); its loupe shows the readout");
    }

    private static void HistogramCase(Action<bool, string> expect)
    {
        // 100 pixels: 10 black, 10 white, 80 at (128, 64, 32).
        var frame = new byte[100 * 4];
        for (var i = 0; i < 100; i++)
        {
            var (r, g, b) = i < 10 ? (0, 0, 0) : i < 20 ? (255, 255, 255) : (128, 64, 32);
            (frame[i * 4], frame[i * 4 + 1], frame[i * 4 + 2], frame[i * 4 + 3]) = ((byte)b, (byte)g, (byte)r, 255);
        }

        var histogram = YrgbHistogram.Of(frame, 10, 10);
        expect(histogram.Pixels == 100 && histogram.Count(YrgbHistogram.Red, 128) == 80 && histogram.Count(YrgbHistogram.Luma, VideoScopes.Luma(128, 64, 32)) == 80,
            "the YRGB histogram counts red, green, blue and BT.709 luma per level");
        expect(Enumerable.Range(0, 4).All(c => histogram.Crushed(c) && histogram.Clipped(c)) && YrgbHistogram.EdgeShare == 0.005,
            "a channel with at least 0.5 % of its pixels at 0 % or 100 % lights its crush or clip edge");
        var clean = YrgbHistogram.Of(frame.AsSpan(80), 5, 4);
        expect(Enumerable.Range(0, 4).All(c => !clean.Crushed(c) && !clean.Clipped(c)) && histogram.Peak(HistogramMode.Yrgb) == 80 && histogram.Peak(HistogramMode.Luma) == 80,
            "no extremes, no edges; the peak ignores pure black and white");
        expect(YrgbHistogram.ChannelsOf(HistogramMode.Yrgb) is [0, 1, 2, 3] && YrgbHistogram.ChannelsOf(HistogramMode.Rgb) is [0, 1, 2] && YrgbHistogram.ChannelsOf(HistogramMode.Luma) is [3],
            "YRGB draws R, G, B and the luma trace; RGB no luma; Luma alone");
        expect(YrgbHistogram.Modes.All(m => YrgbHistogram.Parse(YrgbHistogram.LabelOf(m)) == m) && YrgbHistogram.Parse("luma") == HistogramMode.Luma && YrgbHistogram.Parse("bogus") == HistogramMode.Yrgb
               && string.Join("|", YrgbHistogram.Modes.Select(YrgbHistogram.LabelOf)) == "YRGB|RGB|Luma",
            "the pills read YRGB | RGB | Luma and round-trip through their labels");
        var fresh = (AppSettings)Activator.CreateInstance(typeof(AppSettings), nonPublic: true)!;
        expect(fresh.ColorHistogramMode == "YRGB", "AppSettings.ColorHistogramMode persists the pill, YRGB in a new install");
        expect(Has(SourceProbe.Src("Views", "ColorStudioPane.Deck.cs"), "settings.ColorHistogramMode = YrgbHistogram.LabelOf(mode)") && Has(SourceProbe.Src("Views", "VideoScopesPane.cs"), "settings.ColorHistogramMode = label"),
            "a pill click in the Color Studio or Video Scopes saves the mode");

        var curves = new ColorCurvesControl();
        var pre = YrgbHistogram.FromLuma([.. Enumerable.Range(0, 256)]);
        curves.HistogramMode = HistogramMode.Rgb;
        curves.SetHistograms(histogram, pre);
        expect(curves.Surface.Mode == HistogramMode.Rgb && ReferenceEquals(curves.Surface.Histogram, histogram) && ReferenceEquals(curves.Surface.Ghost, pre),
            "the curves draw the post-grade histogram with the pre-grade one as their ghost, in the chosen mode");
        curves.SetHistogram([.. Enumerable.Repeat(1, 256)]);
        expect(curves.Surface.Histogram is { Pixels: 256 } legacy && legacy.Count(YrgbHistogram.Red, 7) == 1, "a legacy luma histogram still draws, as a grey picture's");

        var painter = new HistogramPainter { Histogram = histogram, Ghost = pre, Mode = HistogramMode.Yrgb, Emphasis = YrgbHistogram.Red };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            painter.Draw(context, new Rect(0, 0, 256, 128), ghost: true);
        expect(visual.Drawing is { Children.Count: > 0 } && HistogramPainter.TraceWidth == 1.5, "the overlay draws fills, 1.5 px traces, the ghost and the edges");

        expect(HistogramPainter.SpansOf(0.5, 0.5, 0.5) is [(7, 0, 0.5)] && HistogramPainter.SpansOf(0.2, 0.6, 0.4) is [(7, 0, 0.2), (6, 0.2, 0.4), (2, 0.4, 0.6)]
               && HistogramPainter.SpanHues[7] is { R: 255, G: 255, B: 255 } && HistogramPainter.SpansOf(0, 0, 0) is [],
            "R/G/B fills are additive height-sorted spans: equal channels fill once with the neutral sum, never stacked translucency");
        var scopesSource = SourceProbe.Src("Views", "VideoScopesPane.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(scopesSource, "private async Task CaptureAsync("), "!_viewModel.HasSource", "_drawn++", "_histogram.Show(null)", "_modePills.Visibility", "(_frame, _picture.Source) = (null, null)", "ShowNote("),
            "with no source Video Scopes drops in-flight draws, the histogram and its pills before the note");
        expect(Has(SourceProbe.Member(scopesSource, "private void Attach("), "YrgbHistogram.ModeChanged += SharedMode_Changed") && Has(SourceProbe.Member(scopesSource, "private void Detach("), "YrgbHistogram.ModeChanged -= SharedMode_Changed")
               && Has(SourceProbe.Src("Views", "ColorStudioPane.Deck.cs"), "Unloaded += (_, _) => YrgbHistogram.ModeChanged -= SharedMode_Changed"),
            "open panes share the histogram mode live through YrgbHistogram.ModeChanged, subscribed only while loaded");
        var (heard, origin) = (new List<HistogramMode>(), new object());
        Action<HistogramMode, object?> listener = (mode, from) => { if (!ReferenceEquals(from, origin)) heard.Add(mode); };
        YrgbHistogram.ModeChanged += listener;
        YrgbHistogram.Announce(HistogramMode.Rgb, null);
        YrgbHistogram.Announce(HistogramMode.Luma, origin);
        YrgbHistogram.ModeChanged -= listener;
        expect(heard is [HistogramMode.Rgb], "a pane hears another pane's mode, never its own");

        var scopes = new VideoScopesPane(new MainViewModel(), () => Task.FromResult<(byte[] Bgra, int Width, int Height)?>(null)) { HistogramMode = HistogramMode.Luma };
        expect(scopes.HistogramMode == HistogramMode.Luma && Has(SourceProbe.Member(SourceProbe.Src("Views", "VideoScopesPane.cs"), "private async Task DrawAsync("), "YrgbHistogram.Of("),
            "Video Scopes draws its Histogram as the same YRGB overlay with its own pills");

        // Wave 1: the Vectorscope's skin-tone line runs through the centre as an I-bar (both sides of the centre).
        var grey = new byte[64 * 36 * 4];
        Array.Fill(grey, (byte)128);
        var (vector, _, _) = VideoScopes.Render(VideoScopeKind.Vectorscope, grey, 64, 36);
        bool Skin(double degrees)
        {
            var (cx, cy) = VideoScopes.VectorscopeCell(0.3 * Math.Cos(degrees * Math.PI / 180), 0.3 * Math.Sin(degrees * Math.PI / 180));
            var pixel = vector[cy * VideoScopes.VectorscopeSize + cx];
            return ((pixel >> 16) & 0xFF) > (pixel & 0xFF) + 16;
        }

        expect(Skin(VideoScopes.SkinToneDegrees) && Skin(VideoScopes.SkinToneDegrees + 180), "Wave 1: the Vectorscope draws the skin-tone I-bar through the centre");
    }

    private static void StrengthCase(Action<bool, string> expect)
    {
        var wild = ColorGradeValues.Neutral with
        {
            Temperature = 100, Tint = -100, LiftR = 1, LiftG = -1, LiftB = -1, GammaR = -1, GammaG = 1, GammaB = 1, GainR = 1, GainG = 1, GainB = -1,
        };
        var held = ColorMatchBounds.Regularize(wild);
        double Mean(double r, double g, double b) => (r + g + b) / 3;
        expect(!ColorMatchBounds.Within(wild) && ColorMatchBounds.Within(held) && held.Temperature == 75 && held.Tint == -75,
            "Temperature and Tint stay within 0.75 of their reach");
        expect(Near(Mean(held.LiftR, held.LiftG, held.LiftB), Mean(1, -1, -1)) && Near(Mean(held.GainR, held.GainG, held.GainB), Mean(1, 1, -1))
               && WheelPush.RimShare((held.GammaR, held.GammaG, held.GammaB)) <= ColorMatchBounds.WheelLimit + 1e-9,
            "each wheel's push stays within 0.75 of the radius, its master kept");
        var calm = ColorGradeValues.Neutral with { Temperature = 20, LiftR = 0.05, LiftB = -0.05 };
        expect(ColorMatchBounds.Regularize(calm) == calm && ColorMatchBounds.Within(ColorGradeValues.Neutral), "values within the bounds are untouched");

        var before = ColorGradeValues.Neutral;
        expect(ColorMatchBounds.Blend(before, held, 0) == before && ColorMatchBounds.Blend(before, held, 1) == held && ColorMatchBounds.Blend(before, held, double.NaN) == held
               && Near(ColorMatchBounds.Blend(before, held, 0.5).Temperature, 37.5) && ColorMatchBounds.Blend(before, held, 2) == held,
            "Match Strength 0 % keeps the grade before, 100 % (and NaN) the solved grade, between them a blend");
        expect(ColorMatchBounds.StrengthText(0.75) == "75%" && ColorMatchBounds.StrengthText(3) == "100%" && ColorMatchBounds.FullStrength == 1, "the strength reads as a whole percentage");

        var match = SourceProbe.Src("MainWindow.ColorMatch.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(match, "private void Match("), "ApplyColorGrade(", "ResetStrength(") && Has(match, "ColorMatchBounds.Blend(")
               && Has(match, "slider.MouseDoubleClick += (_, _) => slider.Value = 100;"),
            "a new match resets the strength to 100 % (user decision); double-click resets it; moves blend live");
        expect(Has(SourceProbe.Src("Views", "ColorStudioPane.Deck.cs"), "internal void WriteValues(") && SourceProbe.InOrder(SourceProbe.Member(match, "private void ApplyStrength("), "BeginValuesGesture()", "WriteValues(", "EndValuesGesture()"),
            "the blend writes through the Color Studio's writer: one Undo step per drag or click");

        // Wave 1: the solver's own solution sits within the bounds, however far apart target and reference are.
        var solution = ColorMatch.SolveFrom((0.9, 0.2, 0.1), (0.1, 0.3, 0.95), ColorGradeValues.Neutral);
        expect(ColorMatchBounds.Within(solution.Values, 1e-6) && Has(SourceProbe.Src("Services", "ColorMatch.cs"), "ColorMatchBounds.Regularize("),
            "Wave 1: ColorMatch.SolveFrom regularizes its solution within the natural bounds");
    }

    private static void SamplesCase(Action<bool, string> expect)
    {
        var sample = new TonalSample(0.2, 0.5, 0.8);
        var luma = VideoScopes.LumaRed * 0.2 + VideoScopes.LumaGreen * 0.5 + VideoScopes.LumaBlue * 0.8;
        expect(Near(sample.Luma, luma) && Near(sample.ValueFor(0), luma) && sample.ValueFor(1) == 0.2 && sample.ValueFor(2) == 0.5 && sample.ValueFor(3) == 0.8,
            "Master takes the sample's luma, Red/Green/Blue their own value (user decision)");
        var pixels = new byte[3 * 3 * 4];
        for (var i = 0; i < 9; i++)
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2]) = (204, 102, 51);
        expect(TonalSample.At(pixels, 3, 3, 0.5, 0.5) is { } read && Near(read.R, 0.2) && Near(read.G, 0.4) && Near(read.B, 0.8) && TonalSample.At(pixels, 3, 3, 1.2, 0.5) is null,
            "a sample is the 3 x 3 mean at a focal point; outside the frame there is none");

        var identity = new List<(double X, double Y)> { (0, 0), (1, 1) };
        expect(ColorCurvesControl.CurveSurface.WithSample(identity, 0.4, "") is [(0, 0), (0.4, 0.4), (1, 1)] && ColorCurvesControl.CurveSurface.WithSample(identity, 0.004, "") is null
               && ColorCurvesControl.CurveSurface.WithSample(identity, double.NaN, "") is null,
            "a sampled point sits on the curve as it is; none next to an existing point or for no value");

        var curves = new ColorCurvesControl { Channel = 2 };
        var (changed, started, ended) = (new List<int>(), 0, 0);
        curves.CurveChanged += (channel, _) => changed.Add(channel);
        curves.EditStarted += () => started++;
        curves.EditEnded += () => ended++;
        expect(curves.AddSampledPoints(sample, allChannels: false) && changed is [2] && started == 1 && ended == 1 && curves.Surface.TextOf(2).Contains("0.5/", StringComparison.Ordinal),
            "Ctrl + Click drops one point on the active curve at its channel's value, as one gesture");
        var all = new ColorCurvesControl();
        changed.Clear();
        all.CurveChanged += (channel, _) => changed.Add(channel);
        expect(all.AddSampledPoints(sample, allChannels: true) && changed is [0, 1, 2, 3] && all.Surface.TextOf(1).Contains("0.2/", StringComparison.Ordinal),
            "Ctrl + Shift + Click drops matched points on Master, Red, Green and Blue in one click");

        curves.ShowPips(sample);
        expect(curves.Surface.Pips == sample, "Ctrl + Hover shows the sample's tonal pips");
        expect(MainWindow.CurveClickScope(ModifierKeys.Control) == false && MainWindow.CurveClickScope(ModifierKeys.Control | ModifierKeys.Shift) == true
               && MainWindow.CurveClickScope(ModifierKeys.None) is null && MainWindow.CurveClickScope(ModifierKeys.Alt) is null,
            "Ctrl feeds the active curve, Ctrl + Shift all four; other clicks stay the player's");
        expect(ColorCurvesControl.CurveSurface.Readout(0, 0.5, "", null) == "Master: in 128, out 128"
               && ColorCurvesControl.CurveSurface.Readout(1, 1, "", YrgbHistogram.FromLuma([.. Enumerable.Repeat(1, 256)])) == "Red: in 255, out 255, 0.4% of pixels",
            "hovering the curves reads the input, the output and the histogram's share there");

        var deck = SourceProbe.Src("Views", "ColorStudioPane.Deck.cs");
        var sampleAt = SourceProbe.Member(deck, "internal TonalSample? SampleAt(");
        expect(Has(sampleAt, "IsPlaying: false") && Has(sampleAt, "still.Target != _target") && Has(sampleAt, "still.CardId !=") && Has(sampleAt, "still.PositionMs != vm.PositionMs")
               && Has(sampleAt, "KeyedValues()") && !Has(sampleAt, "ColorGradeValues.From(") && Has(deck, "ValueOf(ColorGradeKeys.Temperature)"),
            "samples read only a still stamped with this target, card and playhead, paused, graded with the keyed values at the playhead");
        var studio = SourceProbe.Src("Views", "ColorStudioPane.xaml.cs");
        expect(Has(SourceProbe.Src("Views", "ColorStudioPane.Folds.cs"), "new StampedStill(still.Bgra, still.Width, still.Height, target, cardId, position ?? 0)")
               && SourceProbe.InOrder(SourceProbe.Member(studio, "private void ViewModel_PropertyChanged("), "nameof(MainViewModel.PositionMs)", "ForgetStill()", "nameof(MainViewModel.IsPlaying)", "ForgetStill()")
               && Has(SourceProbe.Member(studio, "private void SetTarget("), "ForgetStill()"),
            "the still is stamped at capture start and dropped (with its pips) on a new target, playhead or play state");

        // In MainWindow.ColorStudio.cs (no partial file of its own: the shell budget).
        var hooks = SourceProbe.Src("MainWindow.ColorStudio.cs");
        expect(Has(hooks, "pane.ShowTonalPips(") && Has(hooks, "pane.AddCurvePoints(sample, allChannels)") && Has(SourceProbe.MemberOfType("MainWindow", "private void AttachColorStudio("), "HookCurveSampling()"),
            "the player's Ctrl + Hover and Ctrl + Click reach the Color Studio curves");
    }
}
