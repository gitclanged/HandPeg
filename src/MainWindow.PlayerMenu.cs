using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Player context menu -----
// A stationary right-click on the picture (within the system drag distance) opens frame and view actions: Export Frame as Image...,
// Copy Frame to Clipboard, Ping-to-Mark Highlight Here (at the click point), then Full Screen Preview (F11), 3D Exploded View, Live
// Encoded Preview, Safe Margins and Reset Player Zoom & Pan. PlayerOverlay (with PlayerHitBrush) receives the input, so this is a
// standard WPF menu built on each open (nothing before Frame 1).
public partial class MainWindow
{
    // Overlay position of a pending right press; null when none.
    private Point? _playerRightPressAt;

    private void HookPlayerContextMenu()
    {
        PlayerOverlay.PreviewMouseRightButtonDown += (_, e) => _playerRightPressAt = e.GetPosition(PlayerOverlay);
        PlayerOverlay.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (_playerRightPressAt is not { } pressed)
                return;
            _playerRightPressAt = null;
            var at = e.GetPosition(PlayerOverlay);
            if (!IsStationaryClick(pressed, at, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
                return;
            e.Handled = true;
            OpenPlayerContextMenu(at);
        };
    }

    /// <summary>True when press and release stay within the drag distance on both axes (a click, not a drag). Pure; golden-tested.</summary>
    internal static bool IsStationaryClick(Point pressed, Point released, double dragX, double dragY) =>
        Math.Abs(released.X - pressed.X) <= dragX && Math.Abs(released.Y - pressed.Y) <= dragY;

    /// <summary>Player context menu labels in order (null = separator); read by the golden test.</summary>
    internal static readonly string?[] PlayerMenuItems =
    [
        "Export Frame as Image...", "Copy Frame to Clipboard", "Ping-to-Mark Highlight Here", null,
        "Full Screen Preview", "3D Exploded View", "Live Encoded Preview", "Safe Margins", "Reset Player Zoom & Pan",
    ];

    private void OpenPlayerContextMenu(Point at)
    {
        var menu = new ContextMenu { PlacementTarget = PlayerOverlay, Placement = PlacementMode.MousePoint };
        var hasPicture = _gpu is not null && _viewModel.HasSource;
        var focal = PictureFocalAt(at);
        MenuItem Add(string header, Action action, bool enabled = true, bool? check = null, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            if (check is { } on)
                (item.IsCheckable, item.IsChecked) = (true, on);
            item.Click += (_, _) => RunGuarded(header, action);
            menu.Items.Add(item);
            return item;
        }

        Add(PlayerMenuItems[0]!, ExportFrame, hasPicture);
        Add(PlayerMenuItems[1]!, CopyFrameToClipboard, hasPicture);
        Add(PlayerMenuItems[2]!, () => _viewModel.PingToMark(focal!.Value.X, focal.Value.Y), hasPicture && focal is not null && !_explodedUi);
        menu.Items.Add(new Separator());
        Add(PlayerMenuItems[4]!, _fullscreen.Toggle, _fullscreen.IsOpen || VideoView.IsVisible, check: _fullscreen.IsOpen, gesture: "F11");
        Add(PlayerMenuItems[5]!, () => ExplodedToggle.IsChecked = ExplodedToggle.IsChecked != true, ExplodedToggle.IsEnabled && _gpu is not null, check: ExplodedToggle.IsChecked == true);
        Add(PlayerMenuItems[6]!, () => _viewModel.IsExportQualityPreview = !_viewModel.IsExportQualityPreview, _gpu is not null, check: _viewModel.IsExportQualityPreview);
        Add(PlayerMenuItems[7]!, () => _viewModel.ShowSafeMargins = !_viewModel.ShowSafeMargins, check: _viewModel.ShowSafeMargins);
        Add(PlayerMenuItems[8]!, ResetPlayerView, _playerZoom != 0 || _playerPanX != 0 || _playerPanY != 0);
        menu.IsOpen = true;
    }

    /// <summary>Resets player zoom and pan to fit-centred; the Canvas view is untouched.</summary>
    private void ResetPlayerView()
    {
        (_playerZoom, _playerPanX, _playerPanY) = (0, 0, 0);
        if (_playerView is { } player)
            player.Matrix = Matrix.Identity;
        _gpu?.SetView(0, 0, 0);
        _loopback.PlaceLoopbackWipe();
        _viewModel.StatusText = "Player zoom and pan reset.";
    }

    /// <summary>Copies the displayed composite frame (as Export Frame saves it) to the clipboard as an image.</summary>
    private void CopyFrameToClipboard() => Faults.Forget(CopyFrameToClipboardAsync(), nameof(CopyFrameToClipboard));

    private async Task CopyFrameToClipboardAsync()
    {
        if (_gpu is not { } engine)
            return;
        if (await engine.CaptureFrameAsync() is not { } frame || frame.Width <= 0 || frame.Height <= 0)
        {
            _viewModel.StatusText = "The frame could not be read from the GPU engine.";
            return;
        }

        var (bgra, width, height) = frame;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        bitmap.Freeze();
        try
        {
            Clipboard.SetImage(bitmap);
            _viewModel.StatusText = $"Frame copied to the clipboard ({width} x {height}).";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard locked by another process (CLIPBRD_E_CANT_OPEN): report via status, never throw.
            AppLog.Write("Copy Frame to Clipboard", ex);
            _viewModel.StatusText = "The clipboard is busy (another program has it open): try Copy Frame again.";
        }
    }
}
