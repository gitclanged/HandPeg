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
        FillPlayerViews();

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

    // ----- Player section of the Views menu: Aspect Sandbox and EasyCut (Pass 5.6.5) -----
    // Built in code on the first open (MainWindow.xaml is shared), below the Timeline section: two session toggles, off at every start.

    private void FillPlayerViews()
    {
        if (_viewerTools.AspectSandboxBox is not null)
        {
            (_viewerTools.AspectSandboxBox.IsChecked, _viewerTools.EasyCutBox!.IsChecked) = (_viewerTools.AspectSandbox is { IsVisible: true }, _viewerTools.EasyCutArmed);
            return;
        }

        ViewsPlaybackOptions.Children.Add(new TextBlock { Text = "Player", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        _viewerTools.AspectSandboxBox = new CheckBox { Content = "Aspect Sandbox", Margin = new Thickness(0, 2, 0, 2), ToolTip = "16:9, 1:1 and 9:16 framings side by side." };
        _viewerTools.AspectSandboxBox.Click += (_, _) => ShowAspectSandbox(_viewerTools.AspectSandboxBox.IsChecked == true);
        _viewerTools.EasyCutBox = new CheckBox { Content = "EasyCut (1-9)", Margin = new Thickness(0, 2, 0, 2), ToolTip = "Keys 1-9 cut to Video 1-9 at the playhead." };
        _viewerTools.EasyCutBox.Click += (_, _) => ArmEasyCut(_viewerTools.EasyCutBox.IsChecked == true);
        System.Windows.Automation.AutomationProperties.SetName(_viewerTools.AspectSandboxBox, "Aspect Sandbox");
        System.Windows.Automation.AutomationProperties.SetName(_viewerTools.EasyCutBox, "EasyCut");
        ViewsPlaybackOptions.Children.Add(_viewerTools.AspectSandboxBox);
        ViewsPlaybackOptions.Children.Add(_viewerTools.EasyCutBox);
        FillPlayerViews();
    }

    // ----- Aspect Sandbox driver -----
    // While shown and paused, the composited frame is read back (GpuPreviewEngine.CaptureFrameAsync) 250 ms after the playhead, the
    // Sequence or a look last changed; during playback nothing is read back and the tiles dim (AspectSandboxOverlay.IsStale).

    /// <summary>Quiet time after the last change before the sandbox reads a frame back.</summary>
    private const int AspectSandboxSettleMs = 250;

    /// <summary>Shows or hides the Aspect Sandbox over the player (created on first use, in the player HUD).</summary>
    private void ShowAspectSandbox(bool shown)
    {
        if (shown && _viewerTools.AspectSandbox is null)
        {
            _viewerTools.AspectSandbox = new AspectSandboxOverlay();
            PlayerHud.Children.Add(_viewerTools.AspectSandbox);
            _viewerTools.AspectSandboxSettle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AspectSandboxSettleMs) };
            _viewerTools.AspectSandboxSettle.Tick += AspectSandboxSettle_Tick;
        }

        if (_viewerTools.AspectSandbox is not { } sandbox)
            return;

        _viewModel.PropertyChanged -= AspectSandbox_ViewModelChanged;
        sandbox.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            _viewerTools.AspectSandboxSettle?.Stop();
            sandbox.Clear();
            return;
        }

        _viewModel.PropertyChanged += AspectSandbox_ViewModelChanged;
        sandbox.IsStale = _viewModel.IsPlaying;
        QueueAspectSandbox();
        _viewModel.StatusText = "Aspect Sandbox: 16:9, 1:1 and 9:16 side by side.";
    }

    private void AspectSandbox_ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusText))
            return;
        if (e.PropertyName == nameof(MainViewModel.IsPlaying) && _viewerTools.AspectSandbox is { } sandbox)
            sandbox.IsStale = _viewModel.IsPlaying;
        QueueAspectSandbox();
    }

    // Restarts the settle timer (paused only).
    private void QueueAspectSandbox()
    {
        if (_viewerTools.AspectSandboxSettle is not { } settle || _viewModel.IsPlaying)
            return;
        settle.Stop();
        settle.Start();
    }

    private void AspectSandboxSettle_Tick(object? sender, EventArgs e)
    {
        _viewerTools.AspectSandboxSettle?.Stop();
        Faults.Forget(RefreshAspectSandboxAsync(), nameof(RefreshAspectSandboxAsync));
    }

    // One read-back at a time; a change during it queues one more.
    private async Task RefreshAspectSandboxAsync()
    {
        if (_viewerTools.AspectSandbox is not { IsVisible: true } sandbox || _gpu is not { } engine || _viewModel.IsPlaying)
            return;
        if (_viewerTools.AspectSandboxCapturing)
        {
            _viewerTools.AspectSandboxAgain = true;
            return;
        }

        _viewerTools.AspectSandboxCapturing = true;
        try
        {
            if (await engine.CaptureFrameAsync() is { Width: > 0, Height: > 0 } frame && sandbox.IsVisible && !_viewModel.IsPlaying)
                await sandbox.ShowFrameAsync(frame.Bgra, frame.Width, frame.Height);
        }
        finally
        {
            _viewerTools.AspectSandboxCapturing = false;
        }

        if (_viewerTools.AspectSandboxAgain)
        {
            _viewerTools.AspectSandboxAgain = false;
            QueueAspectSandbox();
        }
    }
}
