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

// ----- Views button and menu on the transport row (placement, open/close, Reset Panes Layout) -----
public partial class MainWindow
{
    // ----- Views button -----
    // Timeline I-frame options live in the Views menu and Timeline Settings. The Views button is the single control at the bar's left for
    // pane views and the bar itself; bar contents and Dynamic Preview Scaling are in the menu's Playback Bar column.

    private void TimelineOptionsBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTransportMinWidth();

    /// <summary>
    /// Reserves the Views button's column at the bar's left at any player width (it is the entry to panes, I-frame options and bar
    /// contents). Icon-only: assumes 34 DIPs (16 DIP glyph plus padding and border) until laid out.
    /// </summary>
    private void UpdateTransportMinWidth() =>
        TransportRow.ColumnDefinitions[0].MinWidth = TimelineOptionsBar.IsVisible
            ? Math.Ceiling((ViewsButton.ActualWidth > 0 ? ViewsButton.ActualWidth : 34) + 22)
            : 0;

    /// <summary>
    /// Places the Views menu above its button, left edges aligned, via UiScale.PlaceAgainst (immune to right-handed menu mirroring).
    /// Re-run on every pane layout, measured from the button's current window (the playback row may be in a side window).
    /// </summary>
    private void PlaceViewsPopup() => UiScale.PlaceAgainst(ViewsPopup, ViewsButton, UiScale.FlyoutSide.AboveStart, 4);

    // Reset Panes Layout (sole home in the Views menu): restores the configured default layout.
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

        // Sync with the current setting (the Settings window can also change it).
        ViewsPreviewScalingBox.IsChecked = AppSettings.Current.DynamicPreviewScaling;
        ViewsButton.IsHitTestVisible = false;
    }

    /// <summary>Views menu "Dynamic Preview Scaling" (same setting as in Settings): saved immediately, read by the player per frame.</summary>
    private void ViewsPreviewScalingBox_Click(object sender, RoutedEventArgs e)
    {
        var on = ViewsPreviewScalingBox.IsChecked == true;
        _viewModel.SavePaneLayout(s => s.DynamicPreviewScaling = on);
        _viewModel.StatusText = on ? "Dynamic Preview Scaling on: the player draws fewer pixels while it plays or scrubs when it must." : "Dynamic Preview Scaling off: the player always draws every pixel.";
    }

    private void ViewsPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ViewsButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
}
