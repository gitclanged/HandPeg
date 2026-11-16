using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp.Services;
using static HandPegApp.UiBrushes;

namespace HandPegApp;

/// <summary>A card a role menu offers (Pass 5.6A): its DXGI number and name.</summary>
public readonly record struct RoleDevice(int Index, string Name);

/// <summary>
/// The GPU and NPU role cards (Pass 5.6A), shared by the first-run window's second screen and Settings' Export &amp; Hardware stage: one
/// inspector card per role (Role, the card that holds it, why), a green check on a measured card and its S-F tier pill (the figures in the
/// pill's tooltip), or a badge ("Estimated", "Measuring...", "Detected") where nothing is measured. A click on a card (or Enter / Space
/// on it) opens its menu: Auto (Recommended) and each distinct card (Background also Processor Only; the NPU Auto, NPU, Graphics Card,
/// Processor), the current choice checked; a choice raises <see cref="RoleChosen"/> and the owner writes it. Built in code, nothing until
/// its items are set; the cards are made once and updated in place (the measurement's progress updates them many times a second).
/// </summary>
public sealed class RoleCardsPanel : StackPanel
{
    // The tier pills' colours (one per grade, F to S) and the check's: frozen (Law 2), the same in every theme (they carry the grade).
    private static readonly Brush[] TierBrushes =
    [
        Frozen(0xF2, 0x5F, 0x5C), Frozen(0xF0, 0x88, 0x3E), Frozen(0xB4, 0x8C, 0xFF), Frozen(0x4E, 0xA1, 0xFF), Frozen(0x3D, 0xD6, 0x8C), Frozen(0xFF, 0xB0, 0x20),
    ];

    private static readonly Brush PillText = Frozen(0x12, 0x12, 0x14), CheckBrush = Frozen(0x3D, 0xD6, 0x8C);

    private readonly List<CardView> _views = [];
    private IEnumerable<FirstRunRoleCard>? _items;

    public RoleCardsPanel() => Focusable = false;

    /// <summary>The role cards shown, in order (Compositor, Decode, Background, Export, NPU).</summary>
    public IEnumerable<FirstRunRoleCard>? ItemsSource
    {
        get => _items;
        set
        {
            _items = value;
            Refresh();
        }
    }

    /// <summary>The cards each GPU role's menu offers (first listings only: the owner's list).</summary>
    public IReadOnlyList<RoleDevice> Devices { get; set; } = [];

    /// <summary>Whether there is an NPU to offer to Auto-Captions (its menu item is off without one).</summary>
    public bool HasNpu { get; set; }

    /// <summary>
    /// A role chosen from a card's menu: the role (<see cref="HardwareRoles"/>), and the value: a GPU role's card number ("-1" Auto,
    /// Background's "-2" Processor Only), the NPU's Auto-Captions accelerator ("auto", "npu", "gpu", "cpu").
    /// </summary>
    public event Action<string, string>? RoleChosen;

    /// <summary>The colour of a grade's pill.</summary>
    public static Brush TierBrush(TierGrade grade) => TierBrushes[(int)grade];

    /// <summary>A tier pill: the grade's letter on its colour, the tooltip given (the figures and the score).</summary>
    public static Border TierPill(PerformanceTier tier, string tip)
    {
        var letter = new TextBlock { Text = tier.Letter, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = PillText, HorizontalAlignment = HorizontalAlignment.Center };
        var pill = new Border { Child = letter, Background = TierBrush(tier.Grade), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), MinWidth = 22, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        AutomationProperties.SetName(pill, tip.Length > 0 ? tip : tier.ToString());
        return pill;
    }

    /// <summary>The menu a role offers: (header, value) in order. Pure: the goldens read it.</summary>
    public static IReadOnlyList<(string Header, string Value)> ChoicesFor(string role, IReadOnlyList<RoleDevice> devices)
    {
        if (role == HardwareRoles.Npu)
            return [("Auto (Recommended)", AppSettings.SpeechOnAuto), ("NPU", AppSettings.SpeechOnNpu), ("Graphics Card", AppSettings.SpeechOnGpu), ("Processor", AppSettings.SpeechOnCpu)];

        var choices = new List<(string, string)> { ("Auto (Recommended)", Number(HardwareRoles.Auto)) };
        if (role == HardwareRoles.Background)
            choices.Add(("Processor Only", Number(HardwareRoles.ProcessorOnly)));
        foreach (var device in devices)
            choices.Add((string.Create(CultureInfo.InvariantCulture, $"{device.Index}: {device.Name}"), Number(device.Index)));
        return choices;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The green of a measured card's check (and Settings' sync line's).</summary>
    public static Brush MeasuredBrush => CheckBrush;

    /// <summary>A card's measurement on its badge: decoding and the bus, or the bus alone; empty when nothing was measured on it.</summary>
    public static string Figures(HardwareBenchmarkAdapter card) => (card.HardwareDecodeFps, card.ReadbackMBps) switch
    {
        ( > 0, > 0) => string.Create(CultureInfo.InvariantCulture, $"{card.HardwareDecodeFps:#,0} fps · {card.ReadbackMBps:#,0} MB/s"),
        ( > 0, _) => string.Create(CultureInfo.InvariantCulture, $"{card.HardwareDecodeFps:#,0} fps"),
        (_, > 0) => string.Create(CultureInfo.InvariantCulture, $"{card.ReadbackMBps:#,0} MB/s"),
        _ => "",
    };

    /// <summary>The menu of a card, as a click opens it (made each time: nothing of it before it is wanted).</summary>
    internal ContextMenu MenuFor(FirstRunRoleCard card, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Custom };

        // Under the card's left edge, in its device pixels (never mirrored on a PC whose menus open to the left of the hand).
        menu.CustomPopupPlacementCallback = (_, targetSize, _) =>
        {
            var perDip = target.ActualHeight > 0 ? targetSize.Height / target.ActualHeight : 1;
            return [new CustomPopupPlacement(new Point(0, targetSize.Height + 2 * perDip), PopupPrimaryAxis.Horizontal)];
        };
        AutomationProperties.SetName(menu, card.Role + " Choices");
        foreach (var (header, value) in ChoicesFor(card.Role, Devices))
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value == card.Value };
            if (card.Role == HardwareRoles.Npu && value == AppSettings.SpeechOnNpu)
                item.IsEnabled = HasNpu;
            item.Click += (_, _) => RoleChosen?.Invoke(card.Role, value);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Opens a card's menu (a click, Enter or Space on it).</summary>
    internal void OpenMenu(int index)
    {
        if (index < 0 || index >= _views.Count || _views[index].Card is not { } card)
            return;
        var menu = MenuFor(card, _views[index].Root);
        menu.IsOpen = true;
    }

    /// <summary>The card elements (the goldens find each by its automation id).</summary>
    internal IReadOnlyList<Border> Cards => [.. _views.Select(v => v.Root)];

    private void Refresh()
    {
        var items = _items?.ToList() ?? [];
        while (_views.Count > items.Count)
        {
            Children.Remove(_views[^1].Root);
            _views.RemoveAt(_views.Count - 1);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i == _views.Count)
            {
                var view = new CardView(this, i);
                _views.Add(view);
                Children.Add(view.Root);
            }

            _views[i].Show(items[i]);
        }
    }

    // One card's elements, made once and updated in place.
    private sealed class CardView
    {
        private readonly TextBlock _role, _name, _detail, _badgeText;
        private readonly Border _badge;
        private readonly TextBlock _check;
        private readonly ContentControl _pill = new() { Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
        private (PerformanceTier? Tier, string Tip) _pillShown;

        public CardView(RoleCardsPanel panel, int index)
        {
            Root = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), Focusable = true, Cursor = Cursors.Hand };
            Root.SetResourceReference(Border.BackgroundProperty, "InspectorCardBrush");
            Root.SetResourceReference(Border.BorderBrushProperty, "InspectorCardBorderBrush");
            KeyboardNavigation.SetIsTabStop(Root, true);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _role = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            _role.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _name = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_name, 1);

            _check = new TextBlock { Text = Glyphs.CheckMark, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = CheckBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Measured" };
            _badgeText = new TextBlock { FontSize = 11 };
            _badge = new Border { Child = _badgeText, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 1), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            _badge.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
            _badge.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(_check);
            status.Children.Add(_pill);
            status.Children.Add(_badge);
            Grid.SetColumn(status, 2);

            var chevron = new TextBlock { Text = Glyphs.ChevronDown, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetColumn(chevron, 3);

            _detail = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetRow(_detail, 1);
            Grid.SetColumn(_detail, 1);
            Grid.SetColumnSpan(_detail, 3);

            grid.Children.Add(_role);
            grid.Children.Add(_name);
            grid.Children.Add(status);
            grid.Children.Add(chevron);
            grid.Children.Add(_detail);
            Root.Child = grid;

            // A press and its release on the card: its menu. Enter or Space on it (it takes the keyboard focus): the same.
            var pressed = false;
            Root.MouseLeftButtonDown += (_, e) =>
            {
                pressed = true;
                e.Handled = true;
            };
            Root.MouseLeave += (_, _) => pressed = false;
            Root.MouseLeftButtonUp += (_, e) =>
            {
                if (!pressed)
                    return;
                pressed = false;
                e.Handled = true;
                panel.OpenMenu(index);
            };
            Root.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    e.Handled = true;
                    panel.OpenMenu(index);
                }
            };

            // The outline in the accent colour under the pointer or the keyboard's focus, as the first run's choice cards are.
            void Outline() => Root.SetResourceReference(Border.BorderBrushProperty,
                Root.IsKeyboardFocused ? "AccentBrush" : Root.IsMouseOver ? "AccentHoverBrush" : "InspectorCardBorderBrush");
            Root.MouseEnter += (_, _) => Outline();
            Root.MouseLeave += (_, _) => Outline();
            Root.IsKeyboardFocusedChanged += (_, _) => Outline();
        }

        public Border Root { get; }

        public FirstRunRoleCard? Card { get; private set; }

        public void Show(FirstRunRoleCard card)
        {
            if (card == Card)
                return;
            Card = card;
            AutomationProperties.SetAutomationId(Root, card.AutomationId);
            AutomationProperties.SetName(Root, card.Role);
            AutomationProperties.SetHelpText(Root, card.TierTip.Length > 0 ? $"{card.Card}. {card.TierTip}" : card.Card);
            _role.Text = card.Role;
            (_name.Text, _name.ToolTip) = (card.Card, card.Card);
            (_detail.Text, _detail.ToolTip) = (card.Detail, card.Detail.Length > 0 ? card.Detail : null);
            _check.Visibility = card.Measured ? Visibility.Visible : Visibility.Collapsed;

            // A measured tier: its pill (the figures in its tooltip). Otherwise the badge, as 5.5A had it, when it says anything.
            if (_pillShown != (card.Tier, card.TierTip))
            {
                _pillShown = (card.Tier, card.TierTip);
                _pill.Content = card.Tier is { } tier ? TierPill(tier, card.TierTip.Length > 0 ? card.TierTip : card.Badge) : null;
            }

            _pill.Visibility = card.Tier is null ? Visibility.Collapsed : Visibility.Visible;
            _badgeText.Text = card.Badge;
            _badgeText.SetResourceReference(TextBlock.ForegroundProperty, card.Measured ? "AccentBrush" : "MutedTextBrush");
            _badgeText.FontWeight = card.Measured ? FontWeights.SemiBold : FontWeights.Normal;
            _badge.Visibility = card.Tier is null && card.Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
