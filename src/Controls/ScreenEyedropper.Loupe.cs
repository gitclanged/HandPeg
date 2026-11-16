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

// ----- Screen eyedropper loupe: magnified patch beside the pointer, excluded from screen capture -----
public sealed partial class ScreenEyedropper
{
    /// <summary>
    /// Small topmost, click-through, non-activating window shown beside the pointer, never over the sampled patch (20 px at 100 % on the
    /// side with room). Positioned and sized in physical pixels for the monitor under the pointer (its effective DPI) and excluded from
    /// capture (SetWindowDisplayAffinity WDA_EXCLUDEFROMCAPTURE) so samples never include it. Patch image and swatch update in place
    /// per sample (one bitmap, one brush).
    /// </summary>
    private sealed partial class Loupe : Window
    {
        // Picture size, swatch strip height and pointer gap in 100 % units (monitor scale applied in Place).
        private const double PictureSide = 120, SwatchHeight = 16, Gap = 20;

        private static readonly Brush Frame = FrozenBrush(0x2A, 0x2A, 0x30), Ground = FrozenBrush(0x1E, 0x1E, 0x22), CentreMark = FrozenBrush(0xFF, 0xFF, 0xFF);

        private readonly WriteableBitmap _picture = new(PatchSide, PatchSide, 96, 96, PixelFormats.Bgr32, null);

        // Swatch brush, intentionally unfrozen (the only one here): its colour is updated per sample.
        private readonly SolidColorBrush _swatch = new(Colors.Black);
        private nint _window;

        public Loupe()
        {
            // Size is set only by Place (UiScale scales content, not window size; Place keeps the picture row square).
            UiScale.SetKeepsItsSize(this, true);
            (WindowStyle, ResizeMode, ShowInTaskbar, ShowActivated, Topmost, Focusable, IsHitTestVisible) = (WindowStyle.None, ResizeMode.NoResize, false, false, true, false, false);
            (Left, Top, Width, Height) = (-32000, -32000, PictureSide, PictureSide + SwatchHeight);
            (Background, BorderBrush, BorderThickness) = (Ground, Frame, new Thickness(1));

            var image = new Image { Source = _picture, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

            // Outline the centre pixel: middle cell of a 7 : 1 : 7 grid over the picture.
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

        /// <summary>Updates the patch and colour, then shows the loupe beside the physical pointer position (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public void Present(byte[] patch, (double R, double G, double B) colour, int x, int y)
        {
            _picture.WritePixels(new Int32Rect(0, 0, PatchSide, PatchSide), patch, PatchSide * 4, 0);
            _swatch.Color = Color.FromRgb(ToByte(colour.R), ToByte(colour.G), ToByte(colour.B));
            if (_window == 0)
            {
                // Create the HWND first so capture exclusion and click-through/no-activate styles apply before it is ever shown.
                _window = new WindowInteropHelper(this).EnsureHandle();
                _ = SetWindowDisplayAffinity(_window, WdaExcludeFromCapture);
                _ = SetWindowLongPtr(_window, GwlExStyle, GetWindowLongPtr(_window, GwlExStyle) | WsExTransparent | WsExNoActivate | WsExToolWindow);
                Show();
            }

            Place(x, y);
        }

        // Positions beside the pointer on the side with room, in the monitor's physical pixels at its scale. Height exceeds width by the
        // swatch row's physical height (content pixels per DIP from VisualTreeHelper.GetDpi), keeping the picture row square at any UI
        // scale (the 1-DIP frame is equal on all sides).
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
