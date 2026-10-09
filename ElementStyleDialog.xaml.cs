using System.Windows;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// The properties of one layer: corners, opacity, soft edges and drop shadow, which every layer has, and for
/// the pictures and videos added by hand a chroma key and a custom mask. Its DataContext is the layer itself.
/// </summary>
public partial class ElementStyleDialog : Window
{
    public ElementStyleDialog()
    {
        InitializeComponent();
    }

    private void PickKeyColor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlayRegion layer)
            return;

        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            picker.Color = System.Drawing.ColorTranslator.FromHtml(layer.ChromaColor);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Whatever is typed in the box is not a color yet; the picker starts from its own default.
        }

        if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            layer.ChromaColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
    }

    private void BrowseMask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select a black-and-white mask", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) == true && DataContext is OverlayRegion layer)
            layer.MaskPath = dialog.FileName;
    }
}