using System.Windows;

namespace HandPegApp;

/// <summary>
/// Which thumbs' drags hold the player's boxes and the Crop boxes' clip (MainWindow.HoldForDrag): every thumb but those under an element
/// marked <see cref="ExemptProperty"/> (inherited down the tree), set on the seek bar and on every scroll bar's style in App.xaml.
/// </summary>
public static class DragHold
{
    /// <summary>A thumb under this element (or the element itself) is dragged without holding anything.</summary>
    public static readonly DependencyProperty ExemptProperty = DependencyProperty.RegisterAttached(
        "Exempt", typeof(bool), typeof(DragHold), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetExempt(DependencyObject element) => (bool)element.GetValue(ExemptProperty);
    public static void SetExempt(DependencyObject element, bool value) => element.SetValue(ExemptProperty, value);
}
