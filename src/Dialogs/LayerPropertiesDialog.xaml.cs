using System.Windows;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Layer properties: corners, opacity, soft edges and drop shadow (all layers), plus chroma key and custom mask for manually added
/// images and videos. DataContext is the layer.
/// </summary>
public partial class LayerPropertiesDialog : Window
{
    public LayerPropertiesDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);

        // Dialog-local Ctrl+Z/Ctrl+Y, one step per press.
        _ = new BoundUndo(this);
    }

    /// <summary>Toggles the layer's track visibility via the main window (undoable); null hides the eye button.</summary>
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

    /// <summary>Opens the Windows color picker at a #RRGGBB colour; returns the choice, or null on cancel.</summary>
    private static string? PickColor(string current)
    {
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            picker.Color = System.Drawing.ColorTranslator.FromHtml(current);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Unparseable box text: start the picker at its default.
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