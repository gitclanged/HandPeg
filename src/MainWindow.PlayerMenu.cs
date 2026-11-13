using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;

namespace HandPegApp;

// ----- The player's right-click menu (5.4.4-Final, the user's) -----
// A right-click on the picture that does not move (pressed and let go within Windows' drag distance) opens a menu of what the player does
// with its frame and its view: Export Frame as Image..., Copy Frame to Clipboard, Ping-to-Mark Highlight Here (at the point clicked), then
// Full Screen Preview (F11), 3D Exploded View, Live Encoded Preview, Safe Margins and Reset Player Zoom & Pan. The picture's layer
// (PlayerOverlay, PlayerHitBrush) takes the pointer, so the menu is WPF's own, made each time it opens (nothing of it before Frame 1).
public partial class MainWindow
{
    // Where a right press on the player was, while it is held (null: none, or it moved and is no click).
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

    /// <summary>Whether a press and its release are one click, not a drag: within the drag distance both ways. Pure, for the golden tests.</summary>
    internal static bool IsStationaryClick(Point pressed, Point released, double dragX, double dragY) =>
        Math.Abs(released.X - pressed.X) <= dragX && Math.Abs(released.Y - pressed.Y) <= dragY;

    /// <summary>The names of the player's right-click menu, in order (null a separator): what the golden test reads.</summary>
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
        Add(PlayerMenuItems[4]!, ToggleFullscreen, _fullscreen is not null || VideoView.IsVisible, check: _fullscreen is not null, gesture: "F11");
        Add(PlayerMenuItems[5]!, () => ExplodedToggle.IsChecked = ExplodedToggle.IsChecked != true, ExplodedToggle.IsEnabled && _gpu is not null, check: ExplodedToggle.IsChecked == true);
        Add(PlayerMenuItems[6]!, () => _viewModel.IsExportQualityPreview = !_viewModel.IsExportQualityPreview, _gpu is not null, check: _viewModel.IsExportQualityPreview);
        Add(PlayerMenuItems[7]!, () => _viewModel.ShowSafeMargins = !_viewModel.ShowSafeMargins, check: _viewModel.ShowSafeMargins);
        Add(PlayerMenuItems[8]!, ResetPlayerView, _playerZoom != 0 || _playerPanX != 0 || _playerPanY != 0);
        menu.IsOpen = true;
    }

    /// <summary>The player's zoom and pan put back (fitted, centred), the Canvas's view left as it is.</summary>
    private void ResetPlayerView()
    {
        (_playerZoom, _playerPanX, _playerPanY) = (0, 0, 0);
        if (_playerView is { } player)
            player.Matrix = Matrix.Identity;
        _gpu?.SetView(0, 0, 0);
        PlaceLoopbackWipe();
        _viewModel.StatusText = "Player zoom and pan reset.";
    }

    /// <summary>The frame on screen, as Export Frame saves it, put on the clipboard as a picture.</summary>
    private async void CopyFrameToClipboard()
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
            // Another program holds the clipboard open (CLIPBRD_E_CANT_OPEN): said, never thrown.
            AppLog.Write("Copy Frame to Clipboard", ex);
            _viewModel.StatusText = "The clipboard is busy (another program has it open): try Copy Frame again.";
        }
    }
}
