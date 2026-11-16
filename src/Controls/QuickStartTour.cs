using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Quick-Start Tour: a sequence of cards placed beside the UI region they describe (outlined while shown), with Skip Tour on every card
/// (beside Next on the first, which has no Back), Back, Next and progress dots. Offered automatically once per install and again from
/// Settings. Cards are Popups (window content renders beneath the player's native child window); they hide while HandPeg is inactive
/// and follow the window as it moves.
/// </summary>
public sealed class QuickStartTour
{
    /// <summary>Tour card: title, text and anchor (a null or hidden anchor centres the card on the window).</summary>
    public sealed record Step(string Title, string Text, Func<FrameworkElement?> Anchor);

    private readonly Window _owner;
    private readonly IReadOnlyList<Step> _steps;
    private readonly Popup _card, _ring;
    private readonly TextBlock _title, _text;
    private readonly TourDots _dots;
    private readonly Button _back, _next;
    private int _index = -1;
    private bool _hidden;

    public QuickStartTour(Window owner, IReadOnlyList<Step> steps)
    {
        (_owner, _steps) = (owner, steps);

        _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _dots = new TourDots { Count = steps.Count, VerticalAlignment = VerticalAlignment.Center };

        var skip = new Button { Content = "Skip Tour", Padding = new Thickness(10, 4, 10, 4) };
        AutomationProperties.SetName(skip, "Skip Tour");
        _back = new Button { Content = "Back", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        _next = new Button { Content = "Next", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        skip.Click += (_, _) => Stop(finished: true);
        _back.Click += (_, _) => Show(_index - 1);
        _next.Click += (_, _) =>
        {
            if (_index >= _steps.Count - 1)
                Stop(finished: true);
            else
                Show(_index + 1);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(skip);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_dots, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(_dots);
        footer.Children.Add(buttons);

        var body = new StackPanel();
        body.Children.Add(_title);
        body.Children.Add(_text);
        body.Children.Add(footer);
        var card = new Border { Width = 340, Padding = new Thickness(16, 14, 16, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        AutomationProperties.SetName(card, "Quick-Start Tour");

        _card = new Popup
        {
            AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Custom, Child = card,
            CustomPopupPlacementCallback = PlaceCard,
        };

        var ring = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), IsHitTestVisible = false };
        ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _ring = new Popup { AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Child = ring };

        // Popups are separate windows: track the editor's UI scale rather than the monitor's.
        Services.UiScale.Track(_card);
        Services.UiScale.Track(_ring);
    }

    /// <summary>Raised once when the tour ends: <c>true</c> if finished or skipped, <c>false</c> if stopped externally.</summary>
    public event Action<bool>? Ended;

    public bool IsRunning => _index >= 0;

    public void Start()
    {
        _owner.LocationChanged += Owner_Moved;
        _owner.SizeChanged += Owner_Moved;
        _owner.StateChanged += Owner_Moved;
        Application.Current.Activated += App_Activated;
        Application.Current.Deactivated += App_Deactivated;
        Show(0);
    }

    /// <summary>Ends the tour; <paramref name="finished"/> is true when finished or skipped by the user.</summary>
    public void Stop(bool finished = false)
    {
        if (_index < 0)
            return;

        _index = -1;
        (_card.IsOpen, _ring.IsOpen) = (false, false);
        _owner.LocationChanged -= Owner_Moved;
        _owner.SizeChanged -= Owner_Moved;
        _owner.StateChanged -= Owner_Moved;
        Application.Current.Activated -= App_Activated;
        Application.Current.Deactivated -= App_Deactivated;
        Ended?.Invoke(finished);
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _steps.Count)
            return;

        _index = index;
        var step = _steps[index];
        (_title.Text, _text.Text) = (step.Title, step.Text);
        _dots.Current = index;
        // No Back on the first card; Skip Tour sits beside Next.
        _back.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = index == _steps.Count - 1 ? "Done" : "Next";
        Place();
    }

    // Places the card below, above or over the anchor (first that fits on screen) with a highlight ring; centred on the window when
    // the anchor is hidden. Both use the target's device pixels: the ring via UiScale.PlaceRelative (Relative placement mirrors under
    // right-handed menu alignment), the card via PlaceCard. Ring size is the target's on-screen size (PointToScreen) converted with the
    // editor's pixels per DIP (VisualTreeHelper.GetDpi), the scale its popup renders at (UiScale.Track).
    private void Place()
    {
        if (_index < 0 || _hidden)
            return;

        var root = _owner.Content as FrameworkElement;
        var anchor = _steps[_index].Anchor();
        var shown = anchor is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } && Window.GetWindow(anchor) == _owner;
        var target = shown ? anchor! : root;
        if (target is null)
            return;

        (_card.IsOpen, _ring.IsOpen) = (false, false);
        _card.PlacementTarget = target;
        _card.Tag = shown;
        if (shown)
        {
            const double Pad = 4;
            var perDip = VisualTreeHelper.GetDpi(_owner).DpiScaleX;
            var (from, to) = (target.PointToScreen(new Point(0, 0)), target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight)));
            var ring = (Border)_ring.Child;
            (ring.Width, ring.Height) = (Math.Abs(to.X - from.X) / perDip + 2 * Pad, Math.Abs(to.Y - from.Y) / perDip + 2 * Pad);
            var targetPerDip = target.ActualWidth > 0 ? Math.Abs(to.X - from.X) / target.ActualWidth : perDip;
            Services.UiScale.PlaceRelative(_ring, target, -Pad * perDip / targetPerDip, -Pad * perDip / targetPerDip);
            _ring.IsOpen = true;
        }

        _card.IsOpen = true;
    }

    // Sizes and points arrive in device pixels: the 12 DIP card gap is scaled by the editor's pixels per DIP so it is constant at every
    // scale. The gap is the card's own spacing, independent of window margins (the panes are flush with the window edges).
    private CustomPopupPlacement[] PlaceCard(Size popup, Size target, Point offset)
    {
        var centredX = (target.Width - popup.Width) / 2;
        var middle = new CustomPopupPlacement(new Point(centredX, (target.Height - popup.Height) / 2), PopupPrimaryAxis.None);
        if (_card.Tag is not true)
            return [middle];

        var gap = 12 * VisualTreeHelper.GetDpi(_owner).DpiScaleX;
        return
        [
            new(new Point(centredX, target.Height + gap), PopupPrimaryAxis.Horizontal),
            new(new Point(centredX, -popup.Height - gap), PopupPrimaryAxis.Horizontal),
            middle,
        ];
    }

    private void Owner_Moved(object? sender, EventArgs e)
    {
        if (_owner.WindowState == WindowState.Minimized)
            (_card.IsOpen, _ring.IsOpen) = (false, false);
        else
            _owner.Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Popups float above all programs, so hide the tour while HandPeg is inactive.
    private void App_Deactivated(object? sender, EventArgs e)
    {
        _hidden = true;
        (_card.IsOpen, _ring.IsOpen) = (false, false);
    }

    private void App_Activated(object? sender, EventArgs e)
    {
        _hidden = false;
        Place();
    }

    /// <summary>Progress indicator: one dot per card, the current one accent-filled.</summary>
    private sealed class TourDots : FrameworkElement
    {
        private const double Size = 7, Spacing = 6;
        private int _current;

        public int Count { get; init; }

        public int Current
        {
            get => _current;
            set
            {
                _current = value;
                InvalidateVisual();
            }
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size available) =>
            new(Math.Max(Count * (Size + Spacing) - Spacing, 0), Size);

        protected override void OnRender(DrawingContext drawing)
        {
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            var rest = TryFindResource("ControlBorderBrush") as Brush ?? Brushes.Gray;
            var y = ActualHeight / 2;
            for (var i = 0; i < Count; i++)
                drawing.DrawEllipse(i == _current ? accent : rest, null, new Point(i * (Size + Spacing) + Size / 2, y), Size / 2, Size / 2);
        }
    }
}
