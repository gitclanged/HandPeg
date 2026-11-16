using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// video-overlay-dpi: the window carrying the player's overlay lies exactly over the native video window, in physical pixels, at
/// every UI scale (HandPeg's own scale on a high-DPI panel draws the editor at 0.5-1.0 of Windows' scale) and when an element is
/// scaled inside a window whose DPI does not show it, the disagreement that made an overlay sized from WPF layout overhang the
/// transport row below the player and the pane beside it. Measured on this PC's monitor with a real window; only the comparisons
/// are written.
/// </summary>
public static partial class OverlayDpiTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("video-overlay-dpi", "# The player's overlay over the native video window at every UI scale, in physical pixels, clear of the rows round it.", Bounds);
        yield return Case("fullscreen-cover-snap", "# The Fullscreen Preview covers its whole monitor in physical pixels, and any other size proposed for it snaps back.", FullscreenCover);
    }

    private static void FullscreenCover(Action<bool, string> expect)
    {
        var probe = new Window { Width = 200, Height = 120, Left = 60, Top = 60, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        PlayerFullscreenWindow? fullscreen = null;
        try
        {
            probe.Show();
            Pump();
            var corner = probe.PointToScreen(new Point(0, 0));
            var monitor = PlayerFullscreenWindow.MonitorAround(new Rect(corner, new Size(10, 10)));
            fullscreen = new PlayerFullscreenWindow(monitor) { Owner = probe, ShowActivated = false };
            fullscreen.Show();
            Pump();
            var handle = new WindowInteropHelper(fullscreen).Handle;
            expect(Rectangle(handle) == monitor, "shown, it covers the whole monitor it was opened on");

            // A resize to some other size (as WPF's own sizing or a DPI change's suggested rectangle would ask) and a move within the
            // monitor are both turned back into the whole monitor before they happen.
            SetWindowPos(handle, 0, monitor.X + 50, monitor.Y + 40, monitor.Width / 2, monitor.Height / 3, SwpNoZOrder | SwpNoActivate);
            Pump();
            expect(Rectangle(handle) == monitor, "a smaller rectangle proposed for it snaps back to the whole monitor");
            SetWindowPos(handle, 0, monitor.X + 30, monitor.Y + 30, 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoSize);
            Pump();
            expect(Rectangle(handle) == monitor, "a move within the monitor snaps back to its corner");
        }
        finally
        {
            fullscreen?.LetClose();
            fullscreen?.Close();
            probe.Close();
            Pump();
        }
    }

    private static Int32Rect? Rectangle(nint handle) =>
        handle != 0 && GetWindowRect(handle, out var r) ? new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    private static void Bounds(Action<bool, string> expect, StringBuilder text)
    {
        var surface = new VideoSurface { Overlay = new Border { Background = Brushes.Transparent } };
        var transport = new Border { Background = Brushes.DimGray };
        var effects = new Border { Background = Brushes.Gray };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        Grid.SetRow(transport, 1);
        Grid.SetColumnSpan(transport, 2);
        Grid.SetColumn(effects, 1);
        grid.Children.Add(surface);
        grid.Children.Add(transport);
        grid.Children.Add(effects);
        var root = new Grid { Children = { grid } };

        var settings = AppSettings.Current;
        var (follow, percent) = (settings.FollowWindowsDpiScaling, settings.UiScalePercent);
        var window = new Window
        {
            Content = root, Width = 640, Height = 400, Left = 40, Top = 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            Pump();
            text.Append("HandPeg's UI scale (each with the root DPI it gives), then a scale inside the window that no DPI shows:\n");

            // The UI scale's own settings (UiScale.Apply: a root transform and its root DPI), then a transform on an element inside
            // the window, which no DPI reports: the disagreement that made an element's size times its DPI overshoot.
            var cases = new List<(string What, Action Set)>();
            foreach (var (followWindows, uiPercent) in new[] { (false, 100), (false, 75), (false, 150), (true, 100), (true, 125) })
            {
                cases.Add((FormattableString.Invariant($"UI scale {uiPercent} %{(followWindows ? ", following Windows' scaling" : "")}"), () =>
                {
                    (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (followWindows, uiPercent);
                    grid.ClearValue(FrameworkElement.LayoutTransformProperty);
                    UiScale.Apply(window);
                }));
            }

            foreach (var inner in new[] { 0.75, 0.5 })
            {
                cases.Add((FormattableString.Invariant($"an inner scale of {inner:0.00} under the window's DPI"), () =>
                {
                    (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (follow, percent);
                    UiScale.Apply(window);
                    var transform = new ScaleTransform(inner, inner);
                    transform.Freeze();
                    grid.LayoutTransform = transform;
                }));
            }

            foreach (var (what, set) in cases)
            {
                set();
                window.UpdateLayout();
                Pump();
                surface.PlaceOverlay();
                Pump();

                var video = surface.SurfaceRectangle();
                var overlay = surface.OverlayWindow is { } shown && new WindowInteropHelper(shown).Handle is var handle && handle != 0 && GetWindowRect(handle, out var r)
                    ? new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
                    : (Int32Rect?)null;
                text.Append("  ").Append(what).Append('\n');
                expect(video is not null && overlay == video, $"{what}: the overlay is exactly the video window's rectangle");
                expect(overlay is { } o && !Overlaps(o, Physical(transport)) && !Overlaps(o, Physical(effects)),
                    $"{what}: the overlay overhangs neither the transport row below nor the pane beside it");
            }
        }
        finally
        {
            (settings.FollowWindowsDpiScaling, settings.UiScalePercent) = (follow, percent);
            window.Close();
            Pump();
        }
    }

    // An element's rectangle on the screen in physical pixels (PointToScreen applies every transform and the monitor's DPI).
    private static Int32Rect Physical(FrameworkElement element)
    {
        var (a, b) = (element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        var (left, top) = ((int)Math.Round(Math.Min(a.X, b.X)), (int)Math.Round(Math.Min(a.Y, b.Y)));
        return new Int32Rect(left, top, (int)Math.Round(Math.Abs(b.X - a.X)), (int)Math.Round(Math.Abs(b.Y - a.Y)));
    }

    private static bool Overlaps(Int32Rect a, Int32Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    // Runs the dispatcher for a moment: layout, the host's move of its native window, and the placements that move queues.
    private static void Pump()
    {
        var until = DateTime.UtcNow.AddMilliseconds(120);
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rect);
}
