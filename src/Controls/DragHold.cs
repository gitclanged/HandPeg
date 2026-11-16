using System.Windows;

namespace HandPegApp;

/// <summary>
/// Controls which Thumb drags freeze the player boxes and Crop target (MainWindow.HoldForDrag): all thumbs except those under an
/// element marked <see cref="ExemptProperty"/> (inherited), which is set on the seek bar and on every scroll bar style in App.xaml.
/// </summary>
public static class DragHold
{
    /// <summary>Inherited flag: thumbs at or under this element drag without freezing anything.</summary>
    public static readonly DependencyProperty ExemptProperty = DependencyProperty.RegisterAttached(
        "Exempt", typeof(bool), typeof(DragHold), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetExempt(DependencyObject element) => (bool)element.GetValue(ExemptProperty);
    public static void SetExempt(DependencyObject element, bool value) => element.SetValue(ExemptProperty, value);
}
