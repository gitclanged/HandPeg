using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Paste Selected Effects... (the Effects pane's stack menu, 4.1.1-B): the copied effects, each with a box, all ticked,
/// and whether they go onto this clip only or every clip on its track. The ticked ones are added to the stack;
/// an effect a clip has once at most (one whose values are the clip's, such as Drop Shadow) takes the pasted values.
/// </summary>
public partial class PasteEffectsDialog : Window
{
    private readonly List<(CheckBox Box, EffectState State)> _choices = [];

    /// <param name="canPasteToTrack">Whether the target is a clip on a track (not the Sequence Output).</param>
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

    /// <summary>The effects ticked, in the copied order.</summary>
    public IReadOnlyList<EffectState> Chosen => [.. _choices.Where(c => c.Box.IsChecked == true).Select(c => c.State)];

    public bool ToTrack => ToTrackBox.IsChecked == true;

    private void UpdatePasteButton() => PasteButton.IsEnabled = _choices.Any(c => c.Box.IsChecked == true);

    private void Paste_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
