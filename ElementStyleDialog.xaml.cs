using System.Windows;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Corners, opacity, soft edges and drop shadow of one layer: a piece of the video, an image, or the caption
/// box (which has only the opacity). Its DataContext is the layer itself.
/// </summary>
public partial class ElementStyleDialog : Window
{
    public ElementStyleDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is OverlayRegion { HasShapeStyle: false })
            {
                CornerRow.Height = new GridLength(0);
                ShapePanel.Visibility = Visibility.Collapsed;
            }
        };
    }
}