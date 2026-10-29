using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Keybinds & Lightning Dial (4.4): the list of every action's keys, the keyboard map on the stage, and the dial's
// categories. They edit copies (a KeybindDraft, a dial) that go into the settings when they are saved; they are made when
// the page is first shown, so opening Settings costs nothing for them.
public partial class SettingsWindow
{
    private KeybindDraft? _keybindDraft;
    private DialConfiguratorControl? _dialEditor;
    private KeybindMapControl? _stageMap;
    private InputChord? _pickChord;

    /// <summary>
    /// Opens at Keybinds &amp; Lightning Dial (from the map over the player while Tab is held, 4.4.0), with a key's menu of
    /// actions open on the stage's keyboard when one was clicked there.
    /// </summary>
    public void ShowKeybind(InputChord? chord)
    {
        _pickChord = chord;
        ShowTab("Keybinds");
        if (chord is { } picked && _stageMap is { } map)
            map.Pick(picked);
    }

    /// <summary>Fills the page's hosts (DialHost, KeybindsListHost, StageKeybindsHost) and names the dial's key; called as the screen is set up.</summary>
    private void InitializeKeybindsPage()
    {
        var draft = _keybindDraft = new KeybindDraft(_settings.KeybindPreset, _settings.CustomKeybinds);
        WhenShown(KeybindsListHost, () => new KeybindListControl(draft));
        WhenShown(StageKeybindsHost, () =>
        {
            var map = _stageMap = new KeybindMapControl(draft) { Margin = new Thickness(12) };
            if (_pickChord is { } picked)
                map.Pick(picked);
            return map;
        });
        WhenShown(DialHost, () => _dialEditor = new DialConfiguratorControl(KeybindManager.Normalized(_settings.LightningDial)));

        // The dial's hint names the key that opens it as the keys are now (Mouse 5 unless it was changed).
        var keys = KeybindManager.GestureText("ui.dial");
        var opener = keys == "Mouse5" ? "The mouse's forward side button (Mouse 5)" : keys.Length > 0 ? keys : "Its key (it has none now: give it one under Keybinds)";
        DialHint.Text = $"{opener} opens the Lightning Dial under the pointer: one ring of the seven categories and Settings. "
            + "A category opens its seven actions in their place, with Back opposite. Choose what each category holds here.";

        // The preset box (bound to KeybindPreset) changes the preset the keys are shown and edited over.
        if (FindPresetBox() is { } presets)
        {
            presets.SelectionChanged += (_, _) =>
            {
                if (presets.SelectedItem is string preset)
                    draft.SetPreset(preset);
            };
        }
    }

    /// <summary>
    /// Puts the page's keys and dial into the settings being saved (called by Save). They are put into effect
    /// (<see cref="KeybindManager.Reload"/>) only once the settings are written, so a save that fails leaves the keys
    /// in use as the settings in effect have them.
    /// </summary>
    private void SaveKeybinds()
    {
        if (_keybindDraft is { } draft)
        {
            _settings.KeybindPreset = draft.Preset;
            _settings.CustomKeybinds = draft.ToSettings();
        }

        if (_dialEditor is { } dial)
            _settings.LightningDial = KeybindManager.ForSettings(dial.Dial);
    }

    // A host's content is made the first time the host is shown.
    private static void WhenShown(ContentControl host, Func<FrameworkElement> make)
    {
        if (host.IsVisible)
        {
            host.Content = make();
            return;
        }

        DependencyPropertyChangedEventHandler? shown = null;
        shown = (_, e) =>
        {
            if (e.NewValue is not true || host.Content is not null)
                return;

            host.IsVisibleChanged -= shown;
            host.Content = make();
        };
        host.IsVisibleChanged += shown;
    }

    // The combo box of the page's panel that is bound to KeybindPreset.
    private ComboBox? FindPresetBox()
    {
        for (DependencyObject? at = KeybindsListHost; at is not null; at = LogicalTreeHelper.GetParent(at))
        {
            if (at is Panel panel && FindPresetBox(panel) is { } found)
                return found;
        }

        return null;
    }

    private static ComboBox? FindPresetBox(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is ComboBox box && IsPresetBox(box))
                return box;
            if (child is not ContentControl { Content: KeybindListControl } && FindPresetBox(child) is { } found)
                return found;
        }

        return null;
    }

    private static bool IsPresetBox(ComboBox box) =>
        (BindingPath(box, Selector.SelectedItemProperty) ?? BindingPath(box, Selector.SelectedValueProperty) ?? BindingPath(box, ComboBox.TextProperty))
            is nameof(AppSettings.KeybindPreset);

    private static string? BindingPath(DependencyObject target, DependencyProperty property) =>
        BindingOperations.GetBinding(target, property)?.Path?.Path;
}
