using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HandPegApp;

namespace HandPegGoldenTests;

/// <summary>
/// window-maximized-edge (Pass 5.2.1, the user's bugs): a window that draws its own frame (the main window's WindowChrome) never
/// shows a frame painted by Windows. Maximized beside a taskbar that hides itself, its client area stops two pixels short of that
/// edge and Windows filled that strip of frame white (a bright line along the bottom of the screen); and Windows' classic caption
/// and border, painted through the window's whole DC over its picture, flashed as it was maximized, restored, minimized or
/// retitled. A real window with HandPeg's window hook (MainWindow.KeepClearOfHiddenTaskbar, the Settings screen's) is put
/// maximized, restored, and restored from minimized; in each, what makes Windows paint a frame is done (a new title, the frame
/// worked out anew, WM_NCPAINT, the classic caption's and frame's own messages), and every pixel of the window on its monitor
/// is read back from the window, drawn by the card and by the processor: none may be light. With those messages left to Windows
/// (as before 5.2.1) the same window shows the white strip, maximized (printed, not compared: it depends on the monitor's taskbar).
/// Restored, what Windows paints mid-switch (the classic caption over the picture, until the window's next frame) leaves nothing
/// to read back: the hook answering every one of those messages in every state is what keeps it away.
/// </summary>
public static class WindowFrameTests
{
    private static readonly string[] States = ["maximized", "restored", "restored from minimized"];

    public static GraphResult MaximizedEdge()
    {
        var text = new StringBuilder("# A window that draws its own frame (5.2.1): never a frame painted by Windows, white or classic, in any state.\n");
        var problems = new List<string>();
        try
        {
            // Drawn by the card, WPF's picture covers what Windows paints at its next frame; drawn by the processor (software
            // rendering: a remote session, a card WPF turns down), Windows' paint stays on the screen.
            foreach (var software in new[] { false, true })
            {
                var drawn = software ? "drawn by the processor" : "drawn by the card";
                RenderOptions.ProcessRenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
                foreach (var state in States)
                {
                    var bare = LightPixels(state, paintsFrame: false, out _);
                    var light = LightPixels(state, paintsFrame: true, out var stripRows);
                    Console.WriteLine($"        {state}, {drawn}: {(stripRows == 0 ? "no strip" : $"a {stripRows}-pixel strip")}; " +
                        $"light pixels: {bare} with Windows' own frame paint, {light} with HandPeg's");
                    text.Append($"{state}, {drawn}, after a new title, the frame worked out anew and Windows' frame messages: no pixel of the window white or light\n");
                    if (light > 0)
                        problems.Add($"{state}, {drawn}: {light} light pixels in the window");
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
        }

        return new GraphResult("window-maximized-edge", text.ToString(), problems, 0);
    }

    private static void Idle()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(150);
    }

    /// <summary>
    /// A window with the main window's chrome and dark background, put in the state named and given what makes Windows paint a
    /// frame: how many pixels of it on its monitor are light, read back from the window itself (client area and frame); how many
    /// rows its client area stops short of the monitor's bottom.
    /// </summary>
    private static int LightPixels(string state, bool paintsFrame, out int stripRows)
    {
        var background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        background.Freeze();
        var window = new Window
        {
            Left = 100, Top = 100, Width = 600, Height = 400, Background = background, ShowActivated = false, ShowInTaskbar = false,
            Title = "HandPeg golden: window frame",
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 32, GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = default, UseAeroCaptionButtons = false,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
        });

        try
        {
            window.SourceInitialized += (_, _) =>
            {
                // Without the frame paint: the hook as it was before 5.2.1 (the strip kept clear, Windows' frame messages left to it).
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(paintsFrame ? MainWindow.KeepClearOfHiddenTaskbar
                    : (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                        message is 0x0085 or 0x00AE or 0x00AF ? IntPtr.Zero : MainWindow.KeepClearOfHiddenTaskbar(hwnd, message, wParam, lParam, ref handled));
            };
            window.Show();
            Idle();
            if (state == "maximized")
                window.WindowState = WindowState.Maximized;
            else if (state == "restored from minimized")
            {
                window.WindowState = WindowState.Minimized;
                Idle();
                window.WindowState = WindowState.Normal;
            }
            Idle();

            // What makes Windows paint a frame, read back at once (before WPF draws the window again).
            var hwnd = new WindowInteropHelper(window).Handle;
            window.Title = "HandPeg golden: window frame, retitled";
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            SendMessage(hwnd, 0x0085, 1, IntPtr.Zero);
            SendMessage(hwnd, 0x00AE, 0, IntPtr.Zero);
            SendMessage(hwnd, 0x00AF, 0, IntPtr.Zero);

            GetWindowRect(hwnd, out var frame);
            GetClientRect(hwnd, out var client);
            MapWindowPoints(hwnd, IntPtr.Zero, ref client, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
            var monitor = info.Monitor;
            stripRows = state == "maximized" ? Math.Max(0, monitor.Bottom - client.Bottom) : 0;

            // The part of the window on its monitor, from the window's own DC (what Windows and the hook painted into it).
            var left = Math.Max(frame.Left, monitor.Left);
            var top = Math.Max(frame.Top, monitor.Top);
            var right = Math.Min(frame.Right, monitor.Right);
            var bottom = Math.Min(frame.Bottom, monitor.Bottom);
            var (width, height) = (right - left, bottom - top);
            if (width <= 0 || height <= 0)
                return 0;

            var pixels = new int[width * height];
            var windowDc = GetWindowDC(hwnd);
            var memoryDc = CreateCompatibleDC(windowDc);
            var bitmap = CreateCompatibleBitmap(windowDc, width, height);
            var old = SelectObject(memoryDc, bitmap);
            BitBlt(memoryDc, 0, 0, width, height, windowDc, left - frame.Left, top - frame.Top, 0x00CC0020);
            SelectObject(memoryDc, old);
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref header, 0);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(hwnd, windowDc);

            var light = 0;
            foreach (var p in pixels)
            {
                if (((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF) > 3 * 0x80)
                    light++;
            }

            return light;
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020,
        SwpNoOwnerZOrder = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, nint wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref NativeRect points, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] int[] bits, ref BitmapInfoHeader header, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
