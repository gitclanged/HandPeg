using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Save Current Layout (Layout button flyout): names the current pane layout and picks one of the mode's three slots, each showing
/// its stored layout (overwriting a full one). Defaults to the first empty slot, else the first.
/// </summary>
public partial class SaveLayoutDialog : Window
{
    private readonly List<RadioButton> _slots = [];

    // Slot-suggested name, until overwritten by typing.
    private string _offered = "";

    /// <param name="kept">Saved layouts for this mode by slot (1 to <see cref="AppSettings.SavedLayoutSlots"/>).</param>
    public SaveLayoutDialog(IReadOnlyList<SavedDockLayout> kept)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        ModeText.Text = $"The panes as they are now, kept as a layout of your own, in one of {AppSettings.SavedLayoutSlots} places.";
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
                // Each slot suggests a name (its stored one when full) until a custom name is typed.
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

    /// <summary>Entered name (after save).</summary>
    public string LayoutName => NameBox.Text.Trim();

    /// <summary>Chosen slot, 1 to <see cref="AppSettings.SavedLayoutSlots"/>.</summary>
    public int Slot => _slots.FirstOrDefault(s => s.IsChecked == true)?.Tag as int? ?? 1;

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => SaveButton.IsEnabled = NameBox.Text.Trim().Length > 0;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
