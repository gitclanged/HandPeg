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

// ----- Shortcuts card -----
// A '?' beside Fit and 100% opens a compact shortcuts card on hover (closes when the pointer leaves button and card) or on click (pinned
// until the '?' is clicked again, a press elsewhere in the window, or the window deactivates). Positioned with UiScale.PlaceAgainst like
// every header/button flyout.

public sealed partial class MaskDrawWindow
{
    /// <summary>Card rows: action (max four words) and key or gesture.</summary>
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

    /// <summary>'?' button tooltip.</summary>
    internal const string ShortcutsTip = "Keyboard and mouse shortcuts";

    // Segoe MDL2 Assets Help glyph (E897).
    private static readonly string HelpGlyph = char.ConvertFromUtf32(0xE897);

    // Hover-close delay after leaving button and card, allowing travel between them.
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(300);

    private readonly Popup _shortcutsCard = new() { AllowsTransparency = true, StaysOpen = true, Focusable = false };
    private readonly ToggleButton _shortcutsButton;
    private DispatcherTimer? _shortcutsGrace;

    // Hover-open flag (the next Checked is hover-driven) and click-pinned flag.
    private bool _shortcutsHovering, _shortcutsPinned;

    /// <summary>Test hook: whether the shortcuts card is open.</summary>
    internal bool ShortcutsOpen => _shortcutsCard.IsOpen;

    private ToggleButton ShortcutsButton()
    {
        var button = new ToggleButton
        {
            Content = HelpGlyph, FontFamily = Glyphs.Font, FontSize = 13, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0),
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

            // Suppress the tooltip while the card is open (it would overlap).
            ToolTipService.SetIsEnabled(button, false);
        };
        button.Unchecked += (_, _) =>
        {
            _shortcutsPinned = false;
            _shortcutsGrace?.Stop();
            ToolTipService.SetIsEnabled(button, true);
        };

        // Hover opens; a click while hover-open pins it instead of toggling closed.
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

    // Pointer left button or card: a hover-opened card closes after the delay unless the pointer returns.
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

    // A window press closes the card except on the '?' (handled by its click) or on the card (popup presses bubble into the window).
    private void CloseShortcutsUnlessOnButton()
    {
        if (_shortcutsCard.IsOpen && !_shortcutsButton.IsMouseOver && _shortcutsCard.Child is not { IsMouseOver: true })
            CloseShortcuts();
    }

    // Card UI: one row per shortcut (action left, keys right) on the inspector card surface; built lazily on first open.
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

        // Themed inspector card surface (#1E1E22 with #2A2A30 border on dark; light card on light), matching themed text.
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "InspectorCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "InspectorCardBorderBrush");
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
