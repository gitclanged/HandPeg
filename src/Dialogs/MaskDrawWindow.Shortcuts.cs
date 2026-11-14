using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The shortcuts card (Pass 5.5.1, the user's) -----
// The Cutout Studio's four lines of instructions over the view are gone: a '?' beside Fit and 100% opens a small card of its shortcuts, on
// hover (closing as the pointer leaves the button and the card) and on a click (kept open until the '?' is clicked again, a press elsewhere
// in the window, or the window is left). Placed against its button by UiScale.PlaceAgainst, as every header and button flyout is.

public sealed partial class MaskDrawWindow
{
    /// <summary>The card's rows: what (four words at most) and its key or gesture.</summary>
    internal static readonly IReadOnlyList<(string What, string Keys)> Shortcuts =
    [
        ("Close shape", "Enter / double-click"),
        ("Take back point", "Backspace"),
        ("Add point", "Alt + click edge"),
        ("Remove point", "Right-click point"),
        ("Straight lines", "Shift"),
        ("Undo / Redo", "Ctrl+Z / Ctrl+Y"),
        ("Zoom", "Wheel / pinch"),
        ("Pan", "Two fingers / right-drag / WASD"),
    ];

    /// <summary>The '?' button's tooltip.</summary>
    internal const string ShortcutsTip = "Keyboard and mouse shortcuts";

    // Segoe MDL2 Assets: Help (E897), a question mark.
    private static readonly string HelpGlyph = char.ConvertFromUtf32(0xE897);

    // The inspector card's look (CLAUDE.md 10.3): its surface and its 1 px border.
    private static readonly Brush CardSurface = FrozenBrush(Color.FromRgb(0x1E, 0x1E, 0x22)), CardBorder = FrozenBrush(Color.FromRgb(0x2A, 0x2A, 0x30));

    // How long the card stays after the pointer leaves the button and the card, opened by hovering: time to reach the card.
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(300);

    private readonly Popup _shortcutsCard = new() { AllowsTransparency = true, StaysOpen = true, Focusable = false };
    private readonly ToggleButton _shortcutsButton;
    private DispatcherTimer? _shortcutsGrace;

    // Opened by hovering (the next Checked is that), and kept open by a click (pinned) rather than the pointer.
    private bool _shortcutsHovering, _shortcutsPinned;

    /// <summary>Whether the shortcuts card is open, for a test.</summary>
    internal bool ShortcutsOpen => _shortcutsCard.IsOpen;

    private ToggleButton ShortcutsButton()
    {
        var button = new ToggleButton
        {
            Content = HelpGlyph, FontFamily = Glyphs, FontSize = 13, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0),
            ToolTip = ShortcutsTip, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, "Shortcuts");
        _shortcutsCard.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = button, Mode = BindingMode.TwoWay });

        button.Checked += (_, _) =>
        {
            _shortcutsPinned = !_shortcutsHovering;
            _shortcutsHovering = false;
            _shortcutsCard.Child ??= ShortcutsCard();
            UiScale.PlaceAgainst(_shortcutsCard, button, UiScale.FlyoutSide.BelowEnd, 4);

            // The card says it all: the tooltip would only cover it.
            ToolTipService.SetIsEnabled(button, false);
        };
        button.Unchecked += (_, _) =>
        {
            _shortcutsPinned = false;
            _shortcutsGrace?.Stop();
            ToolTipService.SetIsEnabled(button, true);
        };

        // A hover opens it; a click on the card opened so keeps it open (rather than closing it, as a toggle's click would).
        button.MouseEnter += (_, _) =>
        {
            _shortcutsGrace?.Stop();
            if (button.IsChecked != true)
            {
                _shortcutsHovering = true;
                button.IsChecked = true;
            }
        };
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (button.IsChecked == true && !_shortcutsPinned)
            {
                _shortcutsPinned = true;
                e.Handled = true;
            }
        };
        button.MouseLeave += (_, _) => LeftShortcuts();
        _shortcutsCard.MouseLeave += (_, _) => LeftShortcuts();
        _shortcutsCard.MouseEnter += (_, _) => _shortcutsGrace?.Stop();
        return button;
    }

    // The pointer left the button or the card: a card opened by hovering closes a moment later, unless the pointer is back on either.
    private void LeftShortcuts()
    {
        if (_shortcutsPinned || _shortcutsButton.IsChecked != true)
            return;

        if (_shortcutsGrace is null)
        {
            _shortcutsGrace = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoverGrace };
            _shortcutsGrace.Tick += (_, _) =>
            {
                _shortcutsGrace.Stop();
                if (!_shortcutsPinned && !_shortcutsButton.IsMouseOver && _shortcutsCard.Child is not { IsMouseOver: true })
                    CloseShortcuts();
            };
        }

        _shortcutsGrace.Stop();
        _shortcutsGrace.Start();
    }

    private void CloseShortcuts() => _shortcutsButton.IsChecked = false;

    // A press in the window closes the card, unless it is on the '?' itself, whose own click does what it does, or on the card (a press
    // there routes up through the popup into the window).
    private void CloseShortcutsUnlessOnButton()
    {
        if (_shortcutsCard.IsOpen && !_shortcutsButton.IsMouseOver && _shortcutsCard.Child is not { IsMouseOver: true })
            CloseShortcuts();
    }

    // The card: one row a shortcut, what on the left and its keys on the right, on the inspector card's surface. Made the first time it opens.
    private static Border ShortcutsCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < Shortcuts.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (what, keys) = Shortcuts[i];
            var name = new TextBlock { Text = what, Margin = new Thickness(0, 2, 18, 2) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var key = new TextBlock { Text = keys, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetRow(name, i);
            Grid.SetRow(key, i);
            Grid.SetColumn(key, 1);
            grid.Children.Add(name);
            grid.Children.Add(key);
        }

        var card = new Border
        {
            Background = CardSurface, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8), Child = grid,
        };
        AutomationProperties.SetName(card, "Cutout Studio shortcuts");
        return card;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
