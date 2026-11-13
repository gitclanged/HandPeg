using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// ----- MainWindow, continued: the Views button of the transport row and its menu (placed, opened, Reset Panes Layout) -----
public partial class MainWindow
{
    // ----- The Views button in the transport row -----
    // The timeline's I-frame checkboxes that followed it are in the Views menu and Timeline Settings since 5.3-Stage2 (the user's de-clutter).
    // 5.4.4 (the user's): the one button at the left of the bar for the views and the bar itself; what the bar shows and Dynamic Preview
    // Scaling are the menu's Playback Bar column (the bar's own settings button at its right end is retired).

    private void TimelineOptionsBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTransportMinWidth();

    /// <summary>
    /// However narrow the player gets, the Views button keeps its place at the left of the playback bar: it is the way to
    /// the panes, the timeline's I-frame checkboxes and (5.4.4) what the bar shows. An icon alone since 5.4.4: 34 DIPs until
    /// it is laid out (its 16 DIP picture, its padding and its border).
    /// </summary>
    private void UpdateTransportMinWidth() =>
        TransportRow.ColumnDefinitions[0].MinWidth = TimelineOptionsBar.IsVisible
            ? Math.Ceiling((ViewsButton.ActualWidth > 0 ? ViewsButton.ActualWidth : 34) + 22)
            : 0;

    /// <summary>
    /// Puts the Views menu over the button that opens it, at the left of the transport row, their left edges level: placed by
    /// UiScale.PlaceAgainst (5.3-Stage3), so a touch PC's mirrored menus never put it a menu's width to the left. Again at every
    /// layout of the panes: measured from the window its button is in now (the playback row may be in the side window).
    /// </summary>
    private void PlaceViewsPopup() => UiScale.PlaceAgainst(ViewsPopup, ViewsButton, UiScale.FlyoutSide.AboveStart, 4);

    // Reset Panes Layout (the Views menu; 5.4D: its one home, the Layout menu's Reset Layout retired): the user's Default layout put back.
    private void ResetPanesLayout_Click(object sender, RoutedEventArgs e)
    {
        _layoutHover.Close();
        _viewModel.ResetPaneLayout();
        ResetToDefaultLayout();
        ViewsButton.IsChecked = false;
    }

    private void ViewsPopup_Opened(object? sender, EventArgs e)
    {
        FillViewsLists();

        // The setting as it is now (5.4.4: the Settings window sets it too).
        ViewsPreviewScalingBox.IsChecked = AppSettings.Current.DynamicPreviewScaling;
        ViewsButton.IsHitTestVisible = false;
    }

    /// <summary>The Views menu's "Dynamic Preview Scaling" (5.4.4; the same setting as the Settings window's): saved at once, read by the player as it draws.</summary>
    private void ViewsPreviewScalingBox_Click(object sender, RoutedEventArgs e)
    {
        var on = ViewsPreviewScalingBox.IsChecked == true;
        _viewModel.SavePaneLayout(s => s.DynamicPreviewScaling = on);
        _viewModel.StatusText = on ? "Dynamic Preview Scaling on: the player draws fewer pixels while it plays or scrubs when it must." : "Dynamic Preview Scaling off: the player always draws every pixel.";
    }

    private void ViewsPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ViewsButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
}
