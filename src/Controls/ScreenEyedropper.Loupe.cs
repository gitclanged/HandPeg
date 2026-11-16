using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HandPegApp.Services;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp;

// ----- The screen eyedropper's loupe (Pass 5.6A): the patch magnified beside the pointer, never in a screen capture -----
public sealed partial class ScreenEyedropper
{
    /// <summary>
    /// The loupe: a small top-most window that takes neither the pointer nor the focus, shown beside the pointer and never over the patch
    /// it reads (20 of the monitor's pixels away at 100 %, on the side with room), placed and sized in physical pixels for the monitor
    /// under the pointer (its effective DPI), and left out of screen captures (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE), so the
    /// reads never see it. Its patch picture and swatch are written in place on every read (one bitmap, one brush).
    /// </summary>
    private sealed partial class Loupe : Window
    {
        // The loupe's picture and its swatch strip, in DIPs at 100 % (the monitor's scale applied in Place).
        private const double PictureSide = 120, SwatchHeight = 16, Gap = 20;

        private static readonly Brush Frame = FrozenBrush(0x2A, 0x2A, 0x30), Ground = FrozenBrush(0x1E, 0x1E, 0x22), CentreMark = FrozenBrush(0xFF, 0xFF, 0xFF);

        private readonly WriteableBitmap _picture = new(PatchSide, PatchSide, 96, 96, PixelFormats.Bgr32, null);

        // The swatch's brush: its colour follows the reads (mutable by design, the one brush of the loupe not frozen).
        private readonly SolidColorBrush _swatch = new(Colors.Black);
        private nint _window;

        public Loupe()
        {
            // 5.6A review: sized by Place alone (UiScale scales its content, never its size: Place squares the picture row).
            UiScale.SetKeepsItsSize(this, true);
            (WindowStyle, ResizeMode, ShowInTaskbar, ShowActivated, Topmost, Focusable, IsHitTestVisible) = (WindowStyle.None, ResizeMode.NoResize, false, false, true, false, false);
            (Left, Top, Width, Height) = (-32000, -32000, PictureSide, PictureSide + SwatchHeight);
            (Background, BorderBrush, BorderThickness) = (Ground, Frame, new Thickness(1));

            var image = new Image { Source = _picture, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

            // The centre pixel outlined: the middle of a 7 : 1 : 7 grid over the picture.
            var marks = new Grid();
            foreach (var share in new[] { 7.0, 1, 7 })
            {
                marks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
                marks.RowDefinitions.Add(new RowDefinition { Height = new GridLength(share, GridUnitType.Star) });
            }

            var centre = new Rectangle { Stroke = CentreMark, StrokeThickness = 1 };
            Grid.SetRow(centre, 1);
            Grid.SetColumn(centre, 1);
            marks.Children.Add(centre);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SwatchHeight) });
            root.Children.Add(image);
            root.Children.Add(marks);
            var swatch = new Border { Background = _swatch };
            Grid.SetRow(swatch, 1);
            root.Children.Add(swatch);
            Content = root;
        }

        /// <summary>The patch and its colour drawn, the loupe shown beside the pointer's physical place (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public void Present(byte[] patch, (double R, double G, double B) colour, int x, int y)
        {
            _picture.WritePixels(new Int32Rect(0, 0, PatchSide, PatchSide), patch, PatchSide * 4, 0);
            _swatch.Color = Color.FromRgb(ToByte(colour.R), ToByte(colour.G), ToByte(colour.B));
            if (_window == 0)
            {
                // Its window made first, so it is left out of captures and takes no pointer or focus before it is ever shown.
                _window = new WindowInteropHelper(this).EnsureHandle();
                _ = SetWindowDisplayAffinity(_window, WdaExcludeFromCapture);
                _ = SetWindowLongPtr(_window, GwlExStyle, GetWindowLongPtr(_window, GwlExStyle) | WsExTransparent | WsExNoActivate | WsExToolWindow);
                Show();
            }

            Place(x, y);
        }

        // Beside the pointer on the side with room, in the pixels of the monitor under it, at its scale. 5.6A review: the window is as much
        // taller than wide as its swatch row is in real pixels (its content's pixels per DIP, UiScale's: VisualTreeHelper.GetDpi), so the
        // picture row stays square whatever the UI Scale (the 1-DIP frame takes the same pixels on every side).
        private void Place(int x, int y)
        {
            var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
            var content = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var width = (int)Math.Round(PictureSide * scale);
            var (height, gap) = (width + (int)Math.Round(SwatchHeight * (content > 0 ? content : scale)), (int)Math.Round(Gap * scale));
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var work = GetMonitorInfo(monitor, ref info) ? info.Work : new NativeRect { Left = int.MinValue / 2, Top = int.MinValue / 2, Right = int.MaxValue / 2, Bottom = int.MaxValue / 2 };
            var left = x + gap + width <= work.Right ? x + gap : x - gap - width;
            var top = y + gap + height <= work.Bottom ? y + gap : y - gap - height;
            _ = SetWindowPos(_window, HwndTopmost, left, top, width, height, SwpNoActivate | SwpShowWindow);
        }

        private static byte ToByte(double c) => (byte)Math.Round(Math.Max(0, Math.Min(c, 1)) * 255);

        private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // ----- Win32 -----

        private const uint WdaExcludeFromCapture = 0x11, MonitorDefaultToNearest = 2, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
        private const int GwlExStyle = -20;
        private const nint WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
        private static readonly nint HwndTopmost = -1;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

        [LibraryImport("shcore.dll")]
        private static partial int GetDpiForMonitor(nint monitor, int kind, out uint dpiX, out uint dpiY);
    }
}
