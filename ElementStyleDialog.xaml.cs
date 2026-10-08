using System.Windows;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Corners, opacity, soft edges and drop shadow of one layer: a piece of the video, an image, or the caption
/// box. Every layer has all of them. Its DataContext is the layer itself.
/// </summary>
public partial class ElementStyleDialog : Window
{
    public ElementStyleDialog()
    {
        InitializeComponent();
    }
}