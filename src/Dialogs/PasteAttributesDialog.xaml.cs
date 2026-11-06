using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Paste Attributes (Ctrl+Alt+V, 5.2A): a box for each part of the copied clip's look, ticked as they were the last time (transform, crop,
/// opacity and effects at first), and Paste gives the selected clips the ticked parts in one step of Undo (MainViewModel.PasteAttributes).
/// </summary>
public partial class PasteAttributesDialog : Window
{
    // The parts ticked the last time this session: what the boxes start as.
    private static MainViewModel.ClipAttributes _last =
        MainViewModel.ClipAttributes.Transform | MainViewModel.ClipAttributes.Crop | MainViewModel.ClipAttributes.Opacity | MainViewModel.ClipAttributes.Effects;

    private readonly (CheckBox Box, MainViewModel.ClipAttributes Part)[] _boxes;

    public PasteAttributesDialog(string copied, int targets)
    {
        InitializeComponent();
        IntroText.Text = $"Tick what of {copied} goes onto the {(targets == 1 ? "selected clip" : $"{targets} selected clips")}. What is not ticked stays as each clip has it.";
        _boxes =
        [
            (TransformBox, MainViewModel.ClipAttributes.Transform), (CropBox, MainViewModel.ClipAttributes.Crop), (OpacityBox, MainViewModel.ClipAttributes.Opacity),
            (EffectsBox, MainViewModel.ClipAttributes.Effects), (KeyframesBox, MainViewModel.ClipAttributes.Keyframes), (MaskBox, MainViewModel.ClipAttributes.MaskAndEdges),
            (FadesBox, MainViewModel.ClipAttributes.Fades), (SoundBox, MainViewModel.ClipAttributes.Sound),
        ];
        foreach (var (box, part) in _boxes)
        {
            box.IsChecked = (_last & part) != 0;
            box.Checked += (_, _) => UpdatePasteButton();
            box.Unchecked += (_, _) => UpdatePasteButton();
        }

        UpdatePasteButton();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>The parts ticked.</summary>
    public MainViewModel.ClipAttributes Chosen => _boxes.Where(b => b.Box.IsChecked == true).Aggregate(MainViewModel.ClipAttributes.None, (all, b) => all | b.Part);

    private void UpdatePasteButton() => PasteButton.IsEnabled = Chosen != MainViewModel.ClipAttributes.None;

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        _last = Chosen;
        DialogResult = true;
    }
}
