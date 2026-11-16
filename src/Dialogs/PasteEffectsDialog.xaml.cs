using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Paste Selected Effects... (Effects pane stack menu): checkbox per copied effect (all checked) and scope (this clip or whole track).
/// Checked effects are appended; single-instance effects (clip-owned values, e.g. Drop Shadow) take the pasted values.
/// </summary>
public partial class PasteEffectsDialog : Window
{
    private readonly List<(CheckBox Box, EffectState State)> _choices = [];

    /// <param name="canPasteToTrack">Target is a track clip (not the Sequence Output).</param>
    public PasteEffectsDialog(IReadOnlyList<EffectState> copied, string target, bool canPasteToTrack, bool toTrack)
    {
        InitializeComponent();
        IntroText.Text = $"Tick the effects to paste onto {target}. They are added to its stack; an effect it can have only once takes the pasted values.";
        foreach (var state in copied)
        {
            var name = EffectRegistry.Find(state.Type)?.Name ?? $"{state.Type} (not in this version)";
            var box = new CheckBox { Content = name, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            box.Checked += (_, _) => UpdatePasteButton();
            box.Unchecked += (_, _) => UpdatePasteButton();
            EffectChoices.Children.Add(box);
            _choices.Add((box, state));
        }

        (ToTrackBox.IsEnabled, ToTrackBox.IsChecked) = (canPasteToTrack, canPasteToTrack && toTrack);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Checked effects in copied order.</summary>
    public IReadOnlyList<EffectState> Chosen => [.. _choices.Where(c => c.Box.IsChecked == true).Select(c => c.State)];

    public bool ToTrack => ToTrackBox.IsChecked == true;

    private void UpdatePasteButton() => PasteButton.IsEnabled = _choices.Any(c => c.Box.IsChecked == true);

    private void Paste_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
