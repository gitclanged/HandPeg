using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using HandPegApp.Services;

namespace HandPegApp;

// Settings > Keybinds & Lightning Dial: action key list, stage keyboard map and dial categories. They edit copies (KeybindDraft, dial)
// committed on Save, and are built lazily on first page show so opening Settings stays cheap.
public partial class SettingsWindow
{
    private KeybindDraft? _keybindDraft;
    private DialConfiguratorControl? _dialEditor;
    private KeybindMapControl? _stageMap;
    private InputChord? _pickChord;

    /// <summary>
    /// Opens on Keybinds &amp; Lightning Dial (from the Tab-hold player overlay map), with the clicked key's action menu open on the stage
    /// keyboard when given.
    /// </summary>
    public void ShowKeybind(InputChord? chord)
    {
        _pickChord = chord;
        ShowTab("Keybinds");
        if (chord is { } picked && _stageMap is { } map)
            map.Pick(picked);
    }

    /// <summary>Populates DialHost, KeybindsListHost and StageKeybindsHost and labels the dial key; called during screen setup.</summary>
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

        // Dial hint shows its current open key (default Mouse 5).
        var keys = KeybindManager.GestureText("ui.dial");
        var opener = keys == "Mouse5" ? "The mouse's forward side button (Mouse 5)" : keys.Length > 0 ? keys : "Its key (it has none now: give it one under Keybinds)";
        DialHint.Text = $"{opener} opens the Lightning Dial under the pointer: one ring of eight slices, each an action run with one flick "
            + "(as it comes) or a category, which opens its seven actions in their place, with Back opposite. Choose what each slice holds here.";

        // The KeybindPreset box switches the base preset shown and edited.
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
    /// Writes keys and dial into the settings being saved (called by Save). Applied via <see cref="KeybindManager.Reload"/> only after
    /// a successful write, so a failed save leaves active bindings unchanged.
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

    // Host content is created lazily on first show.
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

    // The page's combo box bound to KeybindPreset.
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
