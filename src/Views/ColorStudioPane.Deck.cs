using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Views;

/// <summary>Color Studio deck layouts (Pass 5.6.4B, <see cref="ColorStudioPane.ReflowColorColumns"/>).</summary>
internal enum ColorDeckMode
{
    /// <summary>Under 640 DIP: one column of cards, at most 520 DIP wide.</summary>
    Single,

    /// <summary>640..919 DIP: Wheels and Color Match beside Curves, Basics &amp; LUT (each column at most 520 DIP).</summary>
    Two,

    /// <summary>920 DIP and wider: Wheels &amp; Color Match | Curves &amp; histogram | Basics &amp; LUT, side by side, no vertical scroll.</summary>
    Wide,
}

// ----- Color Studio deck: responsive card columns, the histogram mode, player samples for the curves and Match Strength's writes -----
public partial class ColorStudioPane
{
    /// <summary>Deck breakpoints (DIPs): three columns from 920, two from 640 (user decision), one below; and the gap between columns.</summary>
    internal const double DeckWideAt = 920, DeckTwoAt = 640, DeckGap = 12;

    /// <summary>Compact Inspector Mode's column width cap, so sliders and curves stay proportional at every pane width.</summary>
    internal const double DeckCompactMaxWidth = 520;

    /// <summary>Wide deck column ranges: Curves 320..560, Basics &amp; LUT 300..460 (sliders never stretch across a wide monitor), Wheels at most 720.</summary>
    internal const double CurvesColumnMin = 320, CurvesColumnMax = 560, BasicsColumnMin = 300, BasicsColumnMax = 460, WheelsColumnMax = 720;

    /// <summary>Wide deck proportions of the room left after the gaps: Curves 36 %, Basics &amp; LUT 30 % (each then held to its range).</summary>
    internal const double CurvesShare = 0.36, BasicsShare = 0.30;

    /// <summary>Card titles (and automation names) in the deck.</summary>
    internal const string WheelsCard = "Wheels", MatchCard = "Color Match", CurvesCard = "Curves", BasicsCard = "Basics & LUT", LegacyCard = "Legacy Grade";

    private Grid? _deck;
    private readonly StackPanel[] _deckColumns = new StackPanel[3];
    private readonly Dictionary<string, Border> _cards = new(StringComparer.Ordinal);
    private ColorDeckMode? _deckMode;

    // The last pre-grade input still (the curves' input, card excluded), stamped with the target, card and playhead its capture started at:
    // Ctrl + Hover / Ctrl + Click samples read it only while all three still hold (and playback is stopped).
    private readonly record struct StampedStill(byte[] Bgra, int Width, int Height, ColorGradeTarget Target, Guid CardId, double PositionMs);

    private StampedStill? _still;

    /// <summary>The deck's current layout (goldens); Single before the first reflow.</summary>
    internal ColorDeckMode DeckMode => _deckMode ?? ColorDeckMode.Single;

    /// <summary>The deck's card titles per column, top to bottom (goldens).</summary>
    internal IReadOnlyList<string>[] DeckColumnsShown =>
        [.. _deckColumns.Select(column => (IReadOnlyList<string>)[.. (column?.Children.OfType<Border>() ?? []).Select(card => System.Windows.Automation.AutomationProperties.GetName(card))])];

    /// <summary>The deck layout for a pane width: Wide from <see cref="DeckWideAt"/>, Two from <see cref="DeckTwoAt"/>, else Single. Pure.</summary>
    internal static ColorDeckMode DeckModeFor(double width) =>
        !double.IsFinite(width) || width < DeckTwoAt ? ColorDeckMode.Single : width < DeckWideAt ? ColorDeckMode.Two : ColorDeckMode.Wide;

    /// <summary>
    /// Wide deck column widths for a pane width: Curves and Basics &amp; LUT take their shares of the room (width less two gaps) held to
    /// their ranges; Wheels &amp; Color Match take the rest, at most <see cref="WheelsColumnMax"/> (a wider pane leaves the rest empty). Pure.
    /// </summary>
    internal static (double Wheels, double Curves, double Basics) DeckColumns(double width)
    {
        var room = double.IsFinite(width) ? Math.Max(width - 2 * DeckGap, 0) : 0;
        var curves = Math.Clamp(room * CurvesShare, CurvesColumnMin, CurvesColumnMax);
        var basics = Math.Clamp(room * BasicsShare, BasicsColumnMin, BasicsColumnMax);
        return (Math.Clamp(room - curves - basics, 0, WheelsColumnMax), curves, basics);
    }

    /// <summary>Which cards each column holds, top to bottom, per layout (a card the pane lacks is skipped). Pure.</summary>
    internal static string[][] DeckPlan(ColorDeckMode mode) => mode switch
    {
        ColorDeckMode.Wide => [[WheelsCard, MatchCard], [CurvesCard], [BasicsCard, LegacyCard]],
        ColorDeckMode.Two => [[WheelsCard, MatchCard], [CurvesCard, BasicsCard, LegacyCard], []],
        _ => [[WheelsCard, CurvesCard, BasicsCard, MatchCard, LegacyCard], [], []],
    };

    // The deck grid: three card columns and a trailing star column that takes what a wide pane leaves.
    private Grid BuildDeck()
    {
        var deck = _deck = new Grid();
        for (var i = 0; i < 4; i++)
            deck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 3 ? 1 : 0, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
        {
            var column = _deckColumns[i] = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(column, i);
            deck.Children.Add(column);
        }

        deck.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                ReflowColorColumns(e.NewSize.Width);
        };
        ReflowColorColumns(0);
        return deck;
    }

    /// <summary>Registers a deck card under its title.</summary>
    private Border DeckCard(string title, UIElement content) => _cards[title] = InspectorCard(title, content);

    /// <summary>
    /// Lays the cards out for <paramref name="width"/> (modeled on MainWindow.ReflowExportColumns): column widths always, cards moved
    /// between columns only when the layout changes. Called from the deck's SizeChanged; it never sets the deck's own width.
    /// </summary>
    internal void ReflowColorColumns(double width)
    {
        if (_deck is not { } deck)
            return;

        var mode = DeckModeFor(width);
        var columns = deck.ColumnDefinitions;
        switch (mode)
        {
            case ColorDeckMode.Wide:
                var (wheels, curves, basics) = DeckColumns(width);
                (columns[0].Width, columns[1].Width, columns[2].Width) = (new GridLength(wheels + DeckGap), new GridLength(curves + DeckGap), new GridLength(basics));
                break;
            case ColorDeckMode.Two:
                var half = Math.Min((width - DeckGap) / 2, DeckCompactMaxWidth);
                (columns[0].Width, columns[1].Width, columns[2].Width) = (new GridLength(half + DeckGap), new GridLength(half), new GridLength(0));
                break;
            default:
                (columns[0].Width, columns[1].Width, columns[2].Width) = (new GridLength(1, GridUnitType.Star), new GridLength(0), new GridLength(0));
                break;
        }

        columns[3].Width = new GridLength(mode == ColorDeckMode.Single ? 0 : 1, GridUnitType.Star);
        for (var i = 0; i < 3; i++)
        {
            var column = _deckColumns[i];
            column.MaxWidth = mode == ColorDeckMode.Single ? DeckCompactMaxWidth : double.PositiveInfinity;
            column.HorizontalAlignment = HorizontalAlignment.Stretch;
            column.Margin = new Thickness(0, 0, mode == ColorDeckMode.Single || i == 2 || mode == ColorDeckMode.Two && i == 1 ? 0 : DeckGap, 0);
        }

        if (_deckMode == mode)
            return;
        _deckMode = mode;
        foreach (var column in _deckColumns)
            column.Children.Clear();
        var plan = DeckPlan(mode);
        for (var i = 0; i < 3; i++)
        {
            foreach (var title in plan[i])
            {
                if (_cards.TryGetValue(title, out var card))
                    _deckColumns[i].Children.Add(card);
            }
        }
    }

    // ----- Histogram mode (shared with Video Scopes through AppSettings) -----

    private void WireHistogramMode()
    {
        _curves.HistogramMode = YrgbHistogram.Parse(AppSettings.Current.ColorHistogramMode);
        _curves.HistogramModeChanged += mode =>
        {
            if (_adoptingMode)
                return;
            AppSettings.Update(settings => settings.ColorHistogramMode = YrgbHistogram.LabelOf(mode));
            YrgbHistogram.Announce(mode, this);
        };
        // The shared mode notification is static: subscribed only while loaded (no leak), re-read from the setting on each load.
        if (IsLoaded)
            YrgbHistogram.ModeChanged += SharedMode_Changed;
        Loaded += (_, _) =>
        {
            YrgbHistogram.ModeChanged -= SharedMode_Changed;
            YrgbHistogram.ModeChanged += SharedMode_Changed;
            AdoptMode(YrgbHistogram.Parse(AppSettings.Current.ColorHistogramMode));
        };
        Unloaded += (_, _) => YrgbHistogram.ModeChanged -= SharedMode_Changed;
    }

    private bool _adoptingMode;

    // Video Scopes' pill: adopt the mode without writing the setting back or announcing it again.
    private void SharedMode_Changed(HistogramMode mode, object? origin)
    {
        if (!ReferenceEquals(origin, this))
            AdoptMode(mode);
    }

    private void AdoptMode(HistogramMode mode)
    {
        if (_curves.HistogramMode == mode)
            return;
        _adoptingMode = true;
        try
        {
            _curves.HistogramMode = mode;
        }
        finally
        {
            _adoptingMode = false;
        }
    }

    /// <summary>Drops the sampled still and any pips (another target, playhead or play state): samples wait for the next capture.</summary>
    private void ForgetStill()
    {
        _still = null;
        ShowTonalPips(null);
    }

    // ----- Player samples for the curves (Ctrl + Hover pips, Ctrl + Click points) -----

    /// <summary>
    /// The tonal sample at an output-frame focal point (0..1) as it enters the curves: the pre-grade still's 3 x 3 mean graded through the
    /// card's other stages (ColorMatch.Graded, no curves). Null with no still yet or nothing to grade.
    /// </summary>
    internal TonalSample? SampleAt(Point focal)
    {
        if (!_built || _still is not { } still || _vm is not { IsPlaying: false } vm || _target.IsNone || !vm.CanColorGrade(_target)
            || still.Target != _target || still.CardId != (_card?.Id ?? Guid.Empty) || still.PositionMs != vm.PositionMs)
            return null;
        if (TonalSample.At(still.Bgra, still.Width, still.Height, focal.X, focal.Y) is not { } raw)
            return null;
        return TonalSample.Of(ColorMatch.Graded((raw.R, raw.G, raw.B), KeyedValues()));
    }

    /// <summary>The card's values at the playhead (keyframes interpolated at ClipTime through ValueOf); neutral with no card.</summary>
    private ColorGradeValues KeyedValues() => _card is null ? ColorGradeValues.Neutral : new(
        ValueOf(ColorGradeKeys.Temperature), ValueOf(ColorGradeKeys.Tint), ValueOf(ColorGradeKeys.Exposure),
        ValueOf(ColorGradeKeys.LiftR), ValueOf(ColorGradeKeys.LiftG), ValueOf(ColorGradeKeys.LiftB),
        ValueOf(ColorGradeKeys.GammaR), ValueOf(ColorGradeKeys.GammaG), ValueOf(ColorGradeKeys.GammaB),
        ValueOf(ColorGradeKeys.GainR), ValueOf(ColorGradeKeys.GainG), ValueOf(ColorGradeKeys.GainB),
        ValueOf(ColorGradeKeys.Contrast), ValueOf(ColorGradeKeys.Saturation), ValueOf(ColorGradeKeys.Vibrance), ValueOf(ColorGradeKeys.LutMix));

    /// <summary>Shows (or with null hides) the Ctrl + Hover tonal pips on the curves.</summary>
    internal void ShowTonalPips(TonalSample? sample)
    {
        if (_built)
            _curves.ShowPips(sample);
    }

    /// <summary>Ctrl + Click (active curve) or Ctrl + Shift + Click (all four): drops sampled points as one Undo step; false when none.</summary>
    internal bool AddCurvePoints(TonalSample sample, bool allChannels) => _built && _body.IsVisible && _curves.AddSampledPoints(sample, allChannels);

    // ----- Match Strength's live writes (Color Match bar) -----

    /// <summary>Starts a Match Strength drag: its writes until <see cref="EndValuesGesture"/> form one Undo step.</summary>
    internal void BeginValuesGesture() => BeginGesture();

    /// <summary>Ends a Match Strength drag.</summary>
    internal void EndValuesGesture() => EndGesture();

    /// <summary>
    /// Ends a Match Strength drag whose match no longer stands (Undo, another target or card, an edit from elsewhere): its pending writes are
    /// dropped, never flushed into whatever card is current.
    /// </summary>
    internal void CancelValuesGesture()
    {
        _writes?.Cancel();
        EndGesture();
    }

    /// <summary>True while the pane's own batched writes apply (a card change seen then is the pane's, not an edit from elsewhere).</summary>
    internal bool IsWriting => _writing;

    /// <summary>
    /// Writes a whole value set into the card through the pane's writer (at most 30 writes a second; a lone write is its own step). Bound to
    /// <paramref name="card"/> (a Match Strength drag), each queued value lands only while that card is still the target's (QueueNumber).
    /// </summary>
    internal void WriteValues(ColorGradeValues values, EffectInstance? card = null)
    {
        QueueNumber(ColorGradeKeys.Temperature, values.Temperature, card);
        QueueNumber(ColorGradeKeys.Tint, values.Tint, card);
        QueueNumber(ColorGradeKeys.Exposure, values.Exposure, card);
        QueueNumber(ColorGradeKeys.LiftR, values.LiftR, card);
        QueueNumber(ColorGradeKeys.LiftG, values.LiftG, card);
        QueueNumber(ColorGradeKeys.LiftB, values.LiftB, card);
        QueueNumber(ColorGradeKeys.GammaR, values.GammaR, card);
        QueueNumber(ColorGradeKeys.GammaG, values.GammaG, card);
        QueueNumber(ColorGradeKeys.GammaB, values.GammaB, card);
        QueueNumber(ColorGradeKeys.GainR, values.GainR, card);
        QueueNumber(ColorGradeKeys.GainG, values.GainG, card);
        QueueNumber(ColorGradeKeys.GainB, values.GainB, card);
        QueueNumber(ColorGradeKeys.Contrast, values.Contrast, card);
        QueueNumber(ColorGradeKeys.Saturation, values.Saturation, card);
        QueueNumber(ColorGradeKeys.Vibrance, values.Vibrance, card);
        QueueNumber(ColorGradeKeys.LutMix, values.LutMix, card);
    }
}
