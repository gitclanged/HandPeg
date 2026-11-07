using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The Creator Hotbar on the Timeline's row of tools (Pass 5.2.1) -----
// Five slots (AppSettings.HotbarSlots, Models.HotbarSlot): a click puts what a slot holds on the selected clips, else the video clip at
// the playhead (MainViewModel.ApplyHotbarSlot, one step of Undo); a right-click chooses what it holds: any Effect, Transition, Game
// Preset or one-click tool, saved at once. Hidden with Settings > Timeline's box (ShowCreatorHotbar) or its own menu.
public partial class MainWindow
{
    /// <summary>The hotbar's slots as the settings have them now.</summary>
    private static IReadOnlyList<HotbarSlot> HotbarSlots => HotbarSlot.FromSettings(AppSettings.Current.HotbarSlots);

    /// <summary>The slots named on the row of tools, or the hotbar hidden, as the settings say (at start-up, after Settings and after a change).</summary>
    private void ShowHotbar() => UnifiedTimeline.ShowHotbar(HotbarSlots, AppSettings.Current.ShowCreatorHotbar);

    /// <summary>A slot pressed: what it holds goes on the selected clips (the clips selected together, else the one), or the video clip at the playhead.</summary>
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
    /// What a slot holds, chosen at that slot: every Effect (by its kind), Transition, Game Preset (the Styles folder read again) and
    /// one-click tool, the one it holds ticked; then putting it back to its default, every slot back, or the hotbar hidden.
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
        menu.Items.Add(MenuAction("Reset Creator Hotbar", () => SaveHotbar(s => s.HotbarSlots = null),
            "Every slot back to Motion Blur, 3D Fly-In, Neon Rim, Whip Pan and Studio Voice."));
        menu.Items.Add(MenuAction("Hide Creator Hotbar", () => SaveHotbar(s => s.ShowCreatorHotbar = false),
            "Takes the hotbar off the Timeline's row of tools. Settings > Timeline (or the Timeline's tab menu, Timeline Settings) brings it back."));
        menu.IsOpen = true;
    }

    /// <summary>One slot given something else to hold, saved at once.</summary>
    private void SetHotbarSlot(int index, HotbarSlot slot)
    {
        var kept = HotbarSlots.Select(s => s.ToString()).ToList();
        kept[index] = slot.ToString();
        SaveHotbar(s => s.HotbarSlots = kept.SequenceEqual(HotbarSlot.Defaults) ? null : kept);
        _viewModel.StatusText = $"Creator Hotbar slot {index + 1}: {slot.Label}.";
    }

    private void SaveHotbar(Action<AppSettings> set)
    {
        _viewModel.SetViewSetting(set);
        ShowHotbar();
    }
}
