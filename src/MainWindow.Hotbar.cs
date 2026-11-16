using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Creator Hotbar (Quick FX) on the Timeline tool row -----
// Five slots (AppSettings.HotbarSlots, Models.HotbarSlot): click applies the slot to the selected clips, else the video clip at the
// playhead (MainViewModel.ApplyHotbarSlot, one Undo step); right-click assigns any Effect, Transition, Game Preset or one-click tool,
// saved immediately. Slots live in the Quick FX button's flyout (one button on the row instead of five). Hidden via Settings >
// Timeline (ShowCreatorHotbar) or its own menu. The row's Undo/Redo buttons are also applied here (ShowTimelineUndoRedo, off by
// default).
public partial class MainWindow
{
    /// <summary>Current hotbar slots from settings.</summary>
    private static IReadOnlyList<HotbarSlot> HotbarSlots => HotbarSlot.FromSettings(AppSettings.Current.HotbarSlots);

    /// <summary>
    /// Applies tool-row settings (at start-up before Frame 1, after Settings, and after changes): Quick FX button and slot labels, or
    /// hidden; Undo/Redo buttons shown or hidden.
    /// </summary>
    private void ShowHotbar()
    {
        UnifiedTimeline.ShowHotbar(HotbarSlots, AppSettings.Current.ShowCreatorHotbar);
        UnifiedTimeline.ShowUndoRedo(AppSettings.Current.ShowTimelineUndoRedo);
    }

    /// <summary>Applies slot <paramref name="index"/> to the group selection, else the single selection, else the video clip at the playhead.</summary>
    private void ApplyHotbar(int index)
    {
        if (index < 0 || index >= HotbarSlot.Count)
            return;

        LetGoOfDrag();
        IReadOnlyList<Layer> clips = GroupHolds ? _viewModel.SelectedClips : _selectedClip switch
        {
            Layer one => [one],
            LayerSound sound => [sound.Layer],
            _ when _viewModel.Layers.Contains(_viewModel.PlayheadVideo) => [_viewModel.PlayheadVideo],
            _ => [],
        };
        if (_viewModel.ApplyHotbarSlot(HotbarSlots[index], clips))
            ScheduleClipRedraw();
    }

    /// <summary>
    /// Slot assignment menu: every Effect (by category), Transition, Game Preset (Styles folder rescanned) and one-click tool, with the
    /// current choice checked; plus Reset This Slot, Reset Quick FX Slots and Hide Quick FX Button.
    /// </summary>
    private void OpenHotbarSlotMenu(int index, FrameworkElement at)
    {
        var slots = HotbarSlots;
        var current = slots[index];
        var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = $"Slot {index + 1}: {current.Label}", IsEnabled = false, FontWeight = FontWeights.SemiBold });

        MenuItem Choice(string header, HotbarSlot slot, string? about = null)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = slot == current, ToolTip = about };
            item.Click += (_, _) => RunGuarded("a hotbar slot", () => SetHotbarSlot(index, slot));
            return item;
        }

        var effects = new MenuItem { Header = "Effects" };
        foreach (var kind in EffectRegistry.All.Where(d => !d.Pinned).GroupBy(d => d.Category).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var group = new MenuItem { Header = kind.Key };
            foreach (var definition in kind.OrderBy(d => d.Name, StringComparer.CurrentCulture))
                group.Items.Add(Choice(definition.Name, new HotbarSlot(HotbarKind.Effect, definition.Type), definition.Description.Length > 0 ? definition.Description : null));
            effects.Items.Add(group);
        }

        menu.Items.Add(effects);

        var transitions = new MenuItem { Header = "Transitions" };
        foreach (var style in TransitionStyles.All)
        {
            var item = Choice(TransitionStyles.Label(style), new HotbarSlot(HotbarKind.Transition, style.ToString()), "As the clip's fade in.");
            item.Icon = TransitionIcon(style, item);
            transitions.Items.Add(item);
        }

        menu.Items.Add(transitions);

        _viewModel.RefreshStylePresets();
        var presets = new MenuItem { Header = "Game Presets" };
        foreach (var name in _viewModel.StylePresets)
            presets.Items.Add(Choice(name, new HotbarSlot(HotbarKind.GamePreset, name)));
        if (presets.Items.Count == 0)
            presets.Items.Add(new MenuItem { Header = "No Game Presets in the Styles folder", IsEnabled = false });
        menu.Items.Add(presets);

        var tools = new MenuItem { Header = "One-Click Tools" };
        foreach (var (id, label) in HotbarSlot.Tools)
        {
            var slot = new HotbarSlot(HotbarKind.Tool, id);
            tools.Items.Add(Choice(label, slot, slot.About));
        }

        menu.Items.Add(tools);
        menu.Items.Add(new Separator());

        var standard = HotbarSlot.Parse(HotbarSlot.Defaults[index])!.Value;
        var reset = MenuAction($"Reset This Slot ({standard.Label})", () => SetHotbarSlot(index, standard), "Puts this slot back to what it holds in a new install.");
        reset.IsEnabled = current != standard;
        menu.Items.Add(reset);
        menu.Items.Add(MenuAction("Reset Quick FX Slots", () => SaveHotbar(s => s.HotbarSlots = null),
            "Every slot back to Motion Blur, 3D Fly-In, Neon Rim, Whip Pan and Studio Voice."));
        menu.Items.Add(MenuAction("Hide Quick FX Button", () => SaveHotbar(s => s.ShowCreatorHotbar = false),
            "Takes the Quick FX button off the Timeline's row of tools. Settings > Timeline, or Timeline Settings (the Timeline's tab menu, or the gear on its row of tools), brings it back."));

        // Keep the Quick FX flyout open while this menu is open (moving into the menu leaves the flyout).
        UnifiedTimeline.HoldQuickFxFor(menu);
        menu.IsOpen = true;
    }

    /// <summary>Assigns a slot and saves immediately.</summary>
    private void SetHotbarSlot(int index, HotbarSlot slot)
    {
        var kept = HotbarSlots.Select(s => s.ToString()).ToList();
        kept[index] = slot.ToString();
        SaveHotbar(s => s.HotbarSlots = kept.SequenceEqual(HotbarSlot.Defaults) ? null : kept);
        _viewModel.StatusText = $"Quick FX slot {index + 1}: {slot.Label}.";
    }

    private void SaveHotbar(Action<AppSettings> set)
    {
        _viewModel.SetViewSetting(set);
        ShowHotbar();
    }
}
