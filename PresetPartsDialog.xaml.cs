using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Asked when a video is dropped on a preset in the launch window: which groups of the preset's settings to
/// bring in, and whether to remember the answer for that preset.
/// </summary>
public partial class PresetPartsDialog : Window
{
    private readonly (CheckBox Box, PresetParts Part)[] _boxes;

    /// <param name="parts">The boxes that start out ticked.</param>
    /// <param name="always">Whether "Always use these settings" starts out ticked: it was, the last time.</param>
    public PresetPartsDialog(string presetName, string fileName, PresetParts parts, bool always)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        PresetText.Text = $"Preset: {presetName}";
        IntroText.Text = $"Which of this preset's settings should {fileName} open with? What is left unticked stays as HandPeg starts.";

        _boxes =
        [
            (SizeBox, PresetParts.Size), (VideoBox, PresetParts.Video), (AudioBox, PresetParts.Audio),
            (FiltersBox, PresetParts.Filters), (LayoutBox, PresetParts.Layout), (CaptionsBox, PresetParts.Captions),
        ];
        foreach (var (box, part) in _boxes)
            box.IsChecked = parts.HasFlag(part);
        AlwaysBox.IsChecked = always;
    }

    /// <summary>The groups that were ticked.</summary>
    public PresetParts Parts { get; private set; }

    public bool Always { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Parts = _boxes.Where(b => b.Box.IsChecked == true).Aggregate(PresetParts.None, (all, b) => all | b.Part);
        Always = AlwaysBox.IsChecked == true;
        DialogResult = true;
    }
}
