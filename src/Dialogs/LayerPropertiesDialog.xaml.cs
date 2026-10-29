using System.Windows;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// The properties of one layer: corners, opacity, soft edges and drop shadow, which every layer has, and for
/// the pictures and videos added by hand a chroma key and a custom mask. Its DataContext is the layer itself.
/// </summary>
public partial class LayerPropertiesDialog : Window
{
    public LayerPropertiesDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);

        // Ctrl+Z and Ctrl+Y take back and redo what is changed here, a press at a time (4.3E).
        _ = new BoundUndo(this);
    }

    /// <summary>Hides or shows the layer's whole track (the main window's, so it is a step of Undo); none: no eye.</summary>
    public Action<Layer>? ToggleVisibility { get; init; }

    private void Visibility_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is Layer { CanHide: true } layer)
            ToggleVisibility?.Invoke(layer);
    }

    private void PickKeyColor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is Layer layer && PickColor(layer.ChromaColor) is { } color)
            layer.ChromaColor = color;
    }

    private void PickShadowColor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is Layer layer && PickColor(layer.ShadowColor) is { } color)
            layer.ShadowColor = color;
    }

    /// <summary>The Windows color picker, started from a #RRGGBB colour; the colour chosen, or null when it was cancelled.</summary>
    private static string? PickColor(string current)
    {
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            picker.Color = System.Drawing.ColorTranslator.FromHtml(current);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Whatever is typed in the box is not a color yet; the picker starts from its own default.
        }

        return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK ? $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}" : null;
    }

    private void BrowseMask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select a black-and-white mask", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) == true && DataContext is Layer layer)
            layer.MaskPath = dialog.FileName;
    }
}