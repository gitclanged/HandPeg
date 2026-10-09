using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Asks which parts of the project settings to take: from a file when importing, from the current
/// settings when exporting. Everything that is there is ticked to begin with.
/// </summary>
public partial class StyleImportDialog : Window
{
    /// <param name="settings">The file's contents (import) or the current settings (export).</param>
    /// <param name="fileName">The file being imported; null when exporting.</param>
    public StyleImportDialog(StylePreset settings, string? fileName)
    {
        InitializeComponent();

        var isExport = fileName is null;
        Title = isExport ? "Export Style Preset" : "Import Style Preset";
        ConfirmButton.Content = isExport ? "Export..." : "Import";
        FileText.Text = isExport ? "The current style" : fileName;
        IntroText.Text = isExport
            ? "Choose what to write into the file. Only the ticked parts are saved, so the file can carry just a look, or just a layout."
            : "Choose what to take from this file. Each part replaces what is set now; parts left unticked stay as they are.";

        var layers = settings.Layout?.Layers?.Count ?? 0;
        // The masks travel inside the file; named here, so it is clear what comes with the layers.
        var masks = settings.Masks is { Count: > 0 } embedded ? $" Masks: {string.Join(", ", embedded.Keys)}." : "";
        Offer(LayoutBox, LayoutText, settings.Layout is not null,
            $"Main video position and zoom, and {layers} layer{(layers == 1 ? "" : "s")} with their masks.{masks}");
        Offer(ColorBox, ColorText, settings.Color is not null, "Contrast, brightness, saturation, gamma, hue, RGB balance and sharpening.");
        Offer(BlurBox, BlurText, settings.Blur is not null, "Radius, passes and dimming of the blurred background.");
        Offer(SubtitleBox, SubtitleText, settings.Subtitles is not null,
            "Auto-captions on or off, whisper's language, prompt and translation, the caption style, and where the caption box sits.");
    }

    public bool ImportLayout => LayoutBox.IsChecked == true;
    public bool ImportColor => ColorBox.IsChecked == true;
    public bool ImportBlur => BlurBox.IsChecked == true;
    public bool ImportSubtitles => SubtitleBox.IsChecked == true;

    private static void Offer(CheckBox box, TextBlock text, bool isAvailable, string description)
    {
        box.IsEnabled = isAvailable;
        box.IsChecked = isAvailable;
        text.Text = isAvailable ? description : "Not in this file.";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}