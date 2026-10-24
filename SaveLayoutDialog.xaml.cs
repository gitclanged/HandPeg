using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Save Current Layout (the Layout button's flyout): a name for the panes as they are now, and which of the mode's
/// three places to keep them in, each shown with the layout it holds (choosing a full one writes over it). The first
/// empty place is chosen to start with, or the first one when all are full.
/// </summary>
public partial class SaveLayoutDialog : Window
{
    private readonly List<RadioButton> _slots = [];

    // The name the place chosen offered, while it has not been typed over.
    private string _offered = "";

    /// <param name="kept">The layouts kept in this mode, by place (1 to <see cref="AppSettings.SavedLayoutSlots"/>).</param>
    public SaveLayoutDialog(string mode, IReadOnlyList<SavedDockLayout> kept)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        ModeText.Text = $"The panes as they are now, kept for {mode}. Each mode has its own {AppSettings.SavedLayoutSlots} places.";
        for (var slot = 1; slot <= AppSettings.SavedLayoutSlots; slot++)
        {
            var held = kept.FirstOrDefault(k => k.Slot == slot);
            var choice = new RadioButton
            {
                GroupName = "LayoutSlot", Margin = new Thickness(0, 3, 0, 3), Tag = slot,
                Content = held is null ? $"Place {slot}: empty" : $"Place {slot}: {held.Name}",
                ToolTip = held is null ? "An empty place." : $"Holds \"{held.Name}\": saving here writes over it.",
            };
            System.Windows.Automation.AutomationProperties.SetName(choice, $"Place {slot}");
            var name = held?.Name ?? $"My Layout {slot}";
            choice.Checked += (_, _) =>
            {
                // Each place offers a name (the one it holds, when full), until a name of one's own is typed.
                if (NameBox.Text.Trim().Length == 0 || NameBox.Text == _offered)
                    NameBox.Text = _offered = name;
            };
            _slots.Add(choice);
            SlotChoices.Children.Add(choice);
        }

        var first = Enumerable.Range(1, AppSettings.SavedLayoutSlots).FirstOrDefault(s => kept.All(k => k.Slot != s), 1);
        _slots[first - 1].IsChecked = true;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The name given, once saved.</summary>
    public string LayoutName => NameBox.Text.Trim();

    /// <summary>The place chosen, 1 to <see cref="AppSettings.SavedLayoutSlots"/>.</summary>
    public int Slot => _slots.FirstOrDefault(s => s.IsChecked == true)?.Tag as int? ?? 1;

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => SaveButton.IsEnabled = NameBox.Text.Trim().Length > 0;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
