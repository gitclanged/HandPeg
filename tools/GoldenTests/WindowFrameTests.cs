using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HandPegApp;
using Answer = HandPegApp.MainWindow.FrameAnswer;

namespace HandPegGoldenTests;

/// <summary>
/// window-maximized-edge (Pass 5.2.1, the user's bugs): a window that draws its own frame (the main window's WindowChrome) never
/// shows a frame painted by Windows. Maximized beside a taskbar that hides itself, it stops two pixels short of that edge (5.2.1:
/// its client area did, and Windows filled that strip of frame white; since 5.3 the window itself, MainWindow.MaximizedPlace, so
/// the strip is the desktop's and "rows short" below counts the window's gap to the monitor's edge); and Windows' classic caption
/// and border, painted through the window's whole DC over its picture, flashed as it was maximized, restored, minimized or
/// retitled. A real window with HandPeg's window hook (MainWindow.FrameHook, the main window's and the Settings screen's) is put
/// maximized, restored, and restored from minimized; in each, what makes Windows paint a frame is done (a new title, the frame
/// worked out anew, WM_NCPAINT, the classic caption's and frame's own messages), and every pixel of the window on its monitor
/// is read back from the window, drawn by the card and by the processor: none may be light. With those messages left to Windows
/// (as before 5.2.1) the same window shows the white strip, maximized (printed, not compared: it depends on the monitor's taskbar).
/// Restored, the client area is the whole window (WindowChrome's), so since 5.3 WM_NCPAINT goes on to Windows there with an empty
/// frame to paint; what Windows paints mid-switch (the classic caption over the picture, until the window's next frame) leaves
/// nothing to read back, which window-frame-messages (<see cref="Messages"/>) covers message by message.
/// <para>
/// window-frame-messages (Pass 5.3, the classic title bar flashing as the window was moved or sized): what the frame hook does
/// with each of the frame's messages, and what of them goes on to Windows. (1) The pure decision (MainWindow.FrameDecision) as a
/// table, message by window state. (2) A real window, drawn by the card and by the processor, with HandPeg's hook, WindowChrome's
/// own hook under it, and a spy under that (added first: a window's hooks are called from the last added to the first), which
/// hears only what would reach the Window and DefWindowProc: WM_NCACTIVATE, WM_SETTEXT, WM_SETICON, WM_NCCALCSIZE and the classic
/// caption's own messages never reach it, restored, maximized, minimized and restored again, nor as the window changes between
/// them; WM_NCPAINT does in every state (the window never has frame outside its client area since 5.3). In every state the desktop
/// still draws the window's frame (DWMWA_NCRENDERING_ENABLED), Windows has clipped it with no region, and deactivated it shows nothing
/// light in its caption band: the user's bug was those turning off for good. WM_NCACTIVATE with wParam 0 is
/// answered TRUE (else the window could never go inactive), and WS_VISIBLE is as it was after a new title and icon, on a shown
/// window and on one never shown (never made visible). (3) The caption band read back after activation, a new title and icon,
/// the classic caption's messages and each of ten move-and-size steps: no more light than a control window that leaves Windows'
/// classic paths open shows (pixels the moves uncover; with the desktop drawing the frame neither paints a caption, so this layer
/// alone proves little: layer 2 carries the guarantee, and the console says which case this PC is). (4) The maximized place of
/// made-up monitors (MainWindow.MaximizedPlaceOf): a larger and a smaller monitor than the main one, one at a negative origin, one
/// with an ordinary taskbar and one that hides itself, and none. (5) A window made maximized, which Windows places before any hook is
/// there: put at its maximized place before it is shown (KeepClearOfHiddenTaskbarNow at SourceInitialized), the desktop drawing its frame.
/// </para>
/// </summary>
public static partial class WindowFrameTests
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
                    Console.WriteLine($"        {state}, {drawn}: {(stripRows == 0 ? "reaching the edge" : $"{stripRows} rows short of the edge")}; " +
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

    /// <summary>A window as the tests make it: the main window's dark background, placed on the main monitor, never activated by being shown.</summary>
    private static Window NewWindow(string title)
    {
        var background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        background.Freeze();
        return new Window
        {
            Left = 100, Top = 100, Width = 600, Height = 400, Background = background, ShowActivated = false, ShowInTaskbar = false,
            Title = title,
        };
    }

    /// <summary>The main window's chrome (MainWindow.xaml), and the Settings screen's.</summary>
    private static WindowChrome NewChrome() => new()
    {
        CaptionHeight = TitleBarHeight, GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = default, UseAeroCaptionButtons = false,
        ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
    };

    // The title bar's height in the windows' XAML, in DIPs.
    private const double TitleBarHeight = 32;

    /// <summary>
    /// A window with the main window's chrome and dark background, put in the state named and given what makes Windows paint a
    /// frame: how many pixels of it on its monitor are light, read back from the window itself (client area and frame); how many
    /// rows its client area stops short of the monitor's bottom.
    /// </summary>
    private static int LightPixels(string state, bool paintsFrame, out int stripRows)
    {
        var window = NewWindow("HandPeg golden: window frame");
        WindowChrome.SetWindowChrome(window, NewChrome());

        try
        {
            window.SourceInitialized += (_, _) =>
            {
                // Without the frame paint: the hook as it was before 5.2.1 (the strip kept clear, Windows' frame messages left to it).
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(paintsFrame ? MainWindow.FrameHook
                    : (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                        message is 0x0085 or 0x00AE or 0x00AF ? IntPtr.Zero : MainWindow.FrameHook(hwnd, message, wParam, lParam, ref handled));
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

            return LightIn(hwnd, left - frame.Left, top - frame.Top, width, height);
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    /// <summary>How many pixels of a rectangle of a window are light, read from the window's own DC (counted from its top left corner, in real pixels).</summary>
    private static int LightIn(IntPtr hwnd, int x, int y, int width, int height)
    {
        var pixels = new int[width * height];
        var windowDc = GetWindowDC(hwnd);
        var memoryDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(windowDc, width, height);
        var old = SelectObject(memoryDc, bitmap);
        BitBlt(memoryDc, 0, 0, width, height, windowDc, x, y, 0x00CC0020);
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

    // ----- window-frame-messages (5.3) -----

    private const int WmSetText = 0x000C, WmWindowPosChanged = 0x0047, WmSetIcon = 0x0080, WmNcCalcSize = 0x0083, WmNcPaint = 0x0085,
        WmNcActivate = 0x0086, WmNcUahDrawCaption = 0x00AE, WmNcUahDrawFrame = 0x00AF;
    private const int GwlStyle = -16, IdiApplication = 32512, IconSmall = 0, IconBig = 1;
    private const long WsVisible = 0x10000000;

    // How many move-and-size steps the simulated drag takes.
    private const int DragSteps = 10;

    // The messages that must never go on past WindowChrome's hook: the hook answers them, or WindowChrome's does.
    private static readonly int[] NeverPassed = [WmNcActivate, WmSetText, WmSetIcon, WmNcCalcSize, WmNcUahDrawCaption, WmNcUahDrawFrame];

    private static readonly (int Message, int WParam, string Name)[] TableMessages =
    [
        (WmNcActivate, 0, "WM_NCACTIVATE, wParam 0"),
        (WmNcActivate, 1, "WM_NCACTIVATE, wParam 1"),
        (WmSetText, 0, "WM_SETTEXT"),
        (WmSetIcon, 0, "WM_SETICON"),
        (WmNcCalcSize, 0, "WM_NCCALCSIZE, wParam 0"),
        (WmNcCalcSize, 1, "WM_NCCALCSIZE, wParam 1"),
        (0x0024, 0, "WM_GETMINMAXINFO"),
        (0x0046, 0, "WM_WINDOWPOSCHANGING"),
        (WmNcPaint, 1, "WM_NCPAINT"),
        (WmNcUahDrawCaption, 0, "WM_NCUAHDRAWCAPTION"),
        (WmNcUahDrawFrame, 0, "WM_NCUAHDRAWFRAME"),
        (0x0084, 0, "WM_NCHITTEST"),
        (0x00A0, 0, "WM_NCMOUSEMOVE"),
        (0x02A2, 0, "WM_NCMOUSELEAVE"),
    ];

    private static readonly (string Name, bool Zoomed, bool Iconic, bool Strip)[] TableStates =
    [
        ("restored", false, false, false),
        ("maximized", true, false, false),
        ("maximized, strip", true, false, true),
        ("minimized", false, true, false),
    ];

    // The real window's states, in the order it is put in them.
    private static readonly string[] SpyStates = ["restored", "maximized", "restored again", "minimized", "restored from minimized"];

    public static GraphResult Messages()
    {
        var text = new StringBuilder("# The frame's messages (5.3): what the hook of a window that draws its own frame (MainWindow.FrameHook: the main window's,\n" +
            "# through TitleBarHook, and the Settings screen's) does with each, and what of them goes on to Windows.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "FAILED: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            DecisionTable(text, Expect);
            MaximizedPlaces(text, Expect);
            MadeMaximized(text, Expect);

            // Drawn by the card and by the processor (software rendering: a remote session, a card WPF turns down), where whatever
            // Windows paints into the window stays until WPF draws there again.
            foreach (var software in new[] { false, true })
            {
                var drawn = software ? "drawn by the processor" : "drawn by the card";
                RenderOptions.ProcessRenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
                text.Append($"## A real window {drawn}: HandPeg's hook, WindowChrome's under it, and under that a spy (what goes on to Windows)\n");
                var light = SpyWindow(drawn, Expect);
                NeverShown(drawn, Expect);

                var control = ControlWindow();
                Console.WriteLine($"        {drawn}: light pixels in the caption band after activation, a new title and icon, the classic caption's messages " +
                    $"and {DragSteps} move-and-size steps: {light} with HandPeg's hook, {control} with Windows' classic paths left open (the control)" +
                    (control == 0 ? "; the readback sees nothing light either way" : light < control ? "; the hook keeps away what the control shows" : "; as many either way: edge pixels the moves uncover, no caption"));
                // Since 5.3 the desktop keeps drawing the frame (no frame outside the client area; WM_NCPAINT never swallowed), so even
                // Windows' classic paths paint no caption: HandPeg's hook may show no more light than they do.
                Expect(light <= control, $"{drawn}: after activation, a new title and icon, the classic caption's messages and each of {DragSteps} " +
                    "move-and-size steps, no more light in the caption band than with Windows' classic paths left open");
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

        return new GraphResult("window-frame-messages", text.ToString(), problems, 0);
    }

    private static Answer Decide(int message, int wParam, (string Name, bool Zoomed, bool Iconic, bool Strip) state) =>
        MainWindow.FrameDecision(message, wParam, state.Zoomed, state.Iconic, state.Strip);

    /// <summary>(1) The hook's decision for each message in each state, as a table, and what the table must say.</summary>
    /// <summary>
    /// (4) Where a window maximizes beside a taskbar that hides itself (MainWindow.MaximizedPlaceOf), for monitors made up here: the
    /// working area with each such edge two pixels in, whatever the monitor's size or origin (WM_WINDOWPOSCHANGING holds the window there).
    /// </summary>
    private static void MaximizedPlaces(StringBuilder text, Action<bool, string> expect)
    {
        text.Append("## The maximized place beside a taskbar that hides itself (MainWindow.MaximizedPlaceOf), made-up monitors\n");
        const int left = 1, top = 2, right = 4, bottom = 8;
        var cases = new (string Name, (int, int, int, int) Monitor, (int, int, int, int) Work, int Edges, (int, int, int, int) Place)[]
        {
            ("2880 x 1800 main monitor, hidden taskbar at the bottom", (0, 0, 2880, 1800), (0, 0, 2880, 1800), bottom, (0, 0, 2880, 1798)),
            ("3840 x 2160 monitor right of the main one, hidden taskbar on the left", (2880, 0, 6720, 2160), (2880, 0, 6720, 2160), left, (2882, 0, 6720, 2160)),
            ("1920 x 1080 monitor left of the main one (negative origin), hidden taskbar at the top", (-1920, 200, 0, 1280), (-1920, 200, 0, 1280), top, (-1920, 202, 0, 1280)),
            ("a taskbar at the bottom (48 px) and one that hides itself on the right", (0, 0, 2560, 1440), (0, 0, 2560, 1392), right, (0, 0, 2558, 1392)),
            ("hidden taskbars on every edge", (0, 0, 1920, 1080), (0, 0, 1920, 1080), left | top | right | bottom, (2, 2, 1918, 1078)),
        };
        foreach (var (name, monitor, work, edges, wanted) in cases)
        {
            var place = MainWindow.MaximizedPlaceOf(monitor, work, edges);
            text.Append($"  {name}: placed {place.Left},{place.Top} - {place.Right},{place.Bottom}\n");
            expect(place == wanted, $"{name}: the working area with each hidden taskbar's edge two pixels in");
        }

        var none = MainWindow.MaximizedPlaceOf((0, 0, 1920, 1080), (0, 0, 1920, 1040), 0);
        expect(none == (0, 0, 1920, 1040), "no taskbar that hides itself: the working area itself (the window is then left where Windows maximizes it)");
    }

    /// <summary>
    /// (5) A window made maximized (WPF creates it with WS_MAXIMIZE, so Windows places it as its handle is made, before any hook): put at
    /// its maximized place at SourceInitialized, while still hidden, as the main window and Settings do (KeepClearOfHiddenTaskbarNow), so
    /// its first frame is already right; the desktop draws its frame and there is no region. Where it lands is printed on the console
    /// (it depends on this PC's taskbar).
    /// </summary>
    private static void MadeMaximized(StringBuilder text, Action<bool, string> expect)
    {
        text.Append("## A window made maximized: placed before it is shown\n");
        var window = NewWindow("HandPeg golden: frame messages, made maximized");
        // WPF shows a window made maximized only activated.
        (window.ShowActivated, window.WindowState) = (true, WindowState.Maximized);
        WindowChrome.SetWindowChrome(window, NewChrome());
        var placedAtSource = default(NativeRect);
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(MainWindow.FrameHook);
            MainWindow.KeepClearOfHiddenTaskbarNow(handle);
            GetWindowRect(handle, out placedAtSource);
        };
        try
        {
            window.Show();
            Idle();
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var shown);
            var desktopFrame = DwmGetWindowAttribute(hwnd, DwmwaNcRenderingEnabled, out var enabled, sizeof(int)) == 0 && enabled != 0;
            var region = CreateRectRgn(0, 0, 0, 0);
            var clipped = GetWindowRgn(hwnd, region) != 0;
            DeleteObject(region);
            Console.WriteLine($"        made maximized: placed at {placedAtSource.Left},{placedAtSource.Top} - {placedAtSource.Right},{placedAtSource.Bottom} before it was shown, " +
                $"shown at {shown.Left},{shown.Top} - {shown.Right},{shown.Bottom}");
            expect(IsZoomed(hwnd) && shown.Left == placedAtSource.Left && shown.Top == placedAtSource.Top && shown.Right == placedAtSource.Right
                && shown.Bottom == placedAtSource.Bottom, "made maximized: shown maximized exactly where it was placed before it was shown (nothing moves after its first frame)");
            expect(desktopFrame && !clipped, "made maximized: the desktop draws its frame, with no region");
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    private static void DecisionTable(StringBuilder text, Action<bool, string> expect)
    {
        text.Append("## What the hook decides (MainWindow.FrameDecision), by the window's state\n");
        var header = new StringBuilder("message".PadRight(26));
        foreach (var state in TableStates)
            header.Append(state.Name.PadRight(22));
        text.Append(header.ToString().TrimEnd()).Append('\n');
        foreach (var (message, wParam, name) in TableMessages)
        {
            var line = new StringBuilder(name.PadRight(26));
            foreach (var state in TableStates)
                line.Append(Decide(message, wParam, state).ToString().PadRight(22));
            text.Append(line.ToString().TrimEnd()).Append('\n');
        }

        Answer At(int message, int wParam, int state) => Decide(message, wParam, TableStates[state]);
        var every = Enumerable.Range(0, TableStates.Length).ToArray();
        expect(every.All(s => At(WmNcActivate, 0, s) == Answer.ActivateNoPaint && At(WmNcActivate, 1, s) == Answer.ActivateNoPaint),
            "WM_NCACTIVATE goes to DefWindowProc with lParam -1 in every state, its answer returned (swallowed, answering 0, it would refuse to let the window go inactive)");
        expect(every.All(s => At(WmSetText, 0, s) == Answer.DefaultHidden && At(WmSetIcon, 0, s) == Answer.DefaultHidden),
            "WM_SETTEXT and WM_SETICON go to DefWindowProc with WS_VISIBLE cleared around them, in every state");
        expect(every.All(s => At(WmNcCalcSize, 0, s) == Answer.PassOn && At(WmNcCalcSize, 1, s) == Answer.PassOn),
            "WM_NCCALCSIZE is never answered nor changed by HandPeg's hook, in any state (WindowChrome's answers it: the client area is the whole window, so there is never frame outside it)");
        expect(every.All(s => At(0x0024, 0, s) == Answer.PassOn && At(0x0046, 0, s) == Answer.KeepShortThenPassOn),
            "WM_WINDOWPOSCHANGING: the maximized place is kept short of a taskbar that hides itself, then passed on; WM_GETMINMAXINFO is Windows' own (it reads those numbers for whichever monitor the window maximizes on)");
        expect(At(WmNcPaint, 1, 0) == Answer.PassOn && At(WmNcPaint, 1, 1) == Answer.PassOn && At(WmNcPaint, 1, 2) == Answer.PaintStrip
            && At(WmNcPaint, 1, 3) == Answer.PassOn,
            "WM_NCPAINT is answered only maximized with frame outside the client area (painted first); otherwise it goes on to Windows, minimized too (answered there, the desktop stops drawing the frame for good)");
        expect(every.All(s => At(WmNcUahDrawCaption, 0, s) == Answer.Swallow && At(WmNcUahDrawFrame, 0, s) == Answer.Swallow),
            "WM_NCUAHDRAWCAPTION and WM_NCUAHDRAWFRAME (the classic caption's and frame's own) are swallowed in every state");
    }

    /// <summary>
    /// (2) A real window with HandPeg's hook, WindowChrome's under it and a spy under that, put in each state and sent the frame's
    /// messages; then (3) the caption band read back through the simulated drag. How many light pixels the readback found.
    /// </summary>
    private static int SpyWindow(string drawn, Action<bool, string> expect)
    {
        var reached = new Dictionary<int, int>();
        var listening = false;
        HwndSourceHook spy = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (listening)
                reached[message] = reached.GetValueOrDefault(message) + 1;
            return IntPtr.Zero;
        };

        var window = NewWindow("HandPeg golden: frame messages");
        try
        {
            // The spy's SourceInitialized handler before WindowChrome's (subscribed as the chrome is set), HandPeg's after it, as the
            // XAML and the code-behind order them: the handlers run in that order, and the hooks are called the other way round.
            window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(spy);
            WindowChrome.SetWindowChrome(window, NewChrome());
            window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(MainWindow.FrameHook);
            listening = true;
            window.Show();
            Idle();
            var hwnd = new WindowInteropHelper(window).Handle;

            // The hook called directly, as Windows calls a window: deactivating, it answers TRUE.
            var handled = false;
            var answer = MainWindow.FrameHook(hwnd, WmNcActivate, 0, IntPtr.Zero, ref handled);
            expect(handled && answer != IntPtr.Zero, $"{drawn}: the hook given WM_NCACTIVATE with wParam 0 answers it, non-zero (TRUE: the window may go inactive)");

            var ownPlace = default(NativeRect);
            foreach (var state in SpyStates)
            {
                if (state == "maximized")
                    window.WindowState = WindowState.Maximized;
                else if (state == "minimized")
                    window.WindowState = WindowState.Minimized;
                else if (state is "restored again" or "restored from minimized")
                    window.WindowState = WindowState.Normal;
                Idle();
                // Windows restores a minimized window to the state it had before (maximized, had it been): so it is minimized from
                // restored, and each state is checked to be the one named.
                var placed = IsIconic(hwnd) ? "minimized" : IsZoomed(hwnd) ? "maximized" : "restored";
                expect(state.StartsWith(placed, StringComparison.Ordinal), $"{drawn}, {state}: the window is {placed}");

                // The desktop still draws the window's frame, and Windows has clipped it with no region (5.3, the user's bug: frame left
                // outside the client area beside a taskbar that hides itself turned both on for good, and Windows then painted its
                // classic caption over the picture as the window went inactive). Deactivated, nothing light appears in its caption band.
                var desktopFrame = DwmGetWindowAttribute(hwnd, DwmwaNcRenderingEnabled, out var enabled, sizeof(int)) == 0 && enabled != 0;
                var region = CreateRectRgn(0, 0, 0, 0);
                var clipped = GetWindowRgn(hwnd, region) != 0;
                DeleteObject(region);
                SendMessage(hwnd, WmNcActivate, 0, IntPtr.Zero);
                var lightInactive = placed == "minimized" ? 0 : CaptionLight(hwnd);
                SendMessage(hwnd, WmNcActivate, 1, IntPtr.Zero);
                expect(desktopFrame && !clipped && lightInactive == 0, $"{drawn}, {state}: the desktop draws the window's frame, Windows clips it with no region, " +
                    "and deactivated it shows no light pixel in its caption band" +
                    (desktopFrame && !clipped && lightInactive == 0 ? "" : $" (desktop frame {(desktopFrame ? "on" : "off")}, {(clipped ? "a region" : "no region")}, {lightInactive} light pixels)"));
                // Restored (again, or from minimized) it comes back to its own place: holding the maximized place never catches a restore.
                if (placed == "restored" && GetWindowRect(hwnd, out var restoredAt))
                {
                    if (state == "restored")
                        ownPlace = restoredAt;
                    else
                        expect(restoredAt.Left == ownPlace.Left && restoredAt.Top == ownPlace.Top && restoredAt.Right == ownPlace.Right && restoredAt.Bottom == ownPlace.Bottom,
                            $"{drawn}, {state}: back at its own place, as before it was maximized");
                }

                if (placed == "maximized" && GetWindowRect(hwnd, out var maximizedAt))
                    Console.WriteLine($"        {drawn}, maximized at {maximizedAt.Left},{maximizedAt.Top} - {maximizedAt.Right},{maximizedAt.Bottom}");

                var before = NeverPassed.ToDictionary(m => m, m => reached.GetValueOrDefault(m));
                var shownBefore = (Style(hwnd) & WsVisible) != 0;
                var deactivated = SendMessage(hwnd, WmNcActivate, 0, IntPtr.Zero);
                SendMessage(hwnd, WmNcActivate, 1, IntPtr.Zero);
                var title = $"HandPeg golden: frame messages, {state}";
                SetText(hwnd, title);
                SetIcons(hwnd);
                SendNcCalcSize(hwnd);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                SendMessage(hwnd, WmNcUahDrawCaption, 0, IntPtr.Zero);
                SendMessage(hwnd, WmNcUahDrawFrame, 0, IntPtr.Zero);
                var shownAfter = (Style(hwnd) & WsVisible) != 0;
                var passed = NeverPassed.Where(m => reached.GetValueOrDefault(m) > before[m]).ToList();
                expect(passed.Count == 0, $"{drawn}, {state}: WM_NCACTIVATE, WM_SETTEXT, WM_SETICON, WM_NCCALCSIZE, WM_NCUAHDRAWCAPTION and WM_NCUAHDRAWFRAME sent, " +
                    "none reached the spy" + (passed.Count == 0 ? "" : $" ({string.Join(", ", passed.Select(m => $"0x{m:X4}"))} did)"));

                var paints = reached.GetValueOrDefault(WmNcPaint);
                SendMessage(hwnd, WmNcPaint, 1, IntPtr.Zero);
                var painted = reached.GetValueOrDefault(WmNcPaint) > paints;
                switch (state)
                {
                    case "maximized":
                        var strip = FrameOutsideClient(hwnd);
                        Console.WriteLine($"        {drawn}, maximized: {(strip ? "frame outside the client area (a taskbar that hides itself)" : "no frame outside the client area")}; " +
                            $"WM_NCPAINT {(painted ? "reached" : "did not reach")} the spy");
                        expect(painted == !strip, $"{drawn}, maximized: WM_NCPAINT reached the spy exactly when no frame lies outside the client area " +
                            "(the strip beside a taskbar that hides itself is painted and answered)");
                        break;
                    default:
                        expect(painted, $"{drawn}, {state}: WM_NCPAINT reached the spy (the client area is the whole window: an empty frame left to Windows)");
                        break;
                }

                expect(deactivated != IntPtr.Zero, $"{drawn}, {state}: WM_NCACTIVATE with wParam 0 answered non-zero (TRUE)");
                expect(shownBefore && shownAfter && WindowText(hwnd) == title, $"{drawn}, {state}: WS_VISIBLE still set after WM_SETTEXT and WM_SETICON, the new title kept");
            }

            // (3) Restored again: the caption band read back after each of the frame's paint triggers and through the drag.
            var light = CaptionLightThroughDrag(hwnd);

            listening = false;
            var leaked = NeverPassed.Where(m => reached.ContainsKey(m)).ToList();
            expect(leaked.Count == 0, $"{drawn}: in every state, every change between them and the drag, none of those six messages reached the spy" +
                (leaked.Count == 0 ? "" : $" ({string.Join(", ", leaked.Select(m => $"0x{m:X4} {reached[m]} times"))})"));
            expect(reached.GetValueOrDefault(WmWindowPosChanged) > 0, $"{drawn}: the spy hears the window under WindowChrome's hook " +
                "(WM_WINDOWPOSCHANGED, which that hook lets through, reached it)");
            return light;
        }
        finally
        {
            listening = false;
            window.Close();
            Idle();
        }
    }

    /// <summary>(2) A window whose handle is made but which is never shown: a new title and icon never make it visible.</summary>
    private static void NeverShown(string drawn, Action<bool, string> expect)
    {
        var window = NewWindow("HandPeg golden: never shown");
        WindowChrome.SetWindowChrome(window, NewChrome());
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(MainWindow.FrameHook);
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var hiddenBefore = (Style(hwnd) & WsVisible) == 0;
            SetText(hwnd, "HandPeg golden: never shown, retitled");
            SetIcons(hwnd);

            // And given to the hook directly, as Windows gives it.
            const string again = "HandPeg golden: never shown, retitled again";
            var handled = false;
            var words = Marshal.StringToHGlobalUni(again);
            try
            {
                MainWindow.FrameHook(hwnd, WmSetText, 0, words, ref handled);
            }
            finally
            {
                Marshal.FreeHGlobal(words);
            }

            expect(hiddenBefore && (Style(hwnd) & WsVisible) == 0 && handled && WindowText(hwnd) == again,
                $"{drawn}: a window never shown keeps WS_VISIBLE clear after WM_SETTEXT and WM_SETICON (sent, and given to the hook directly), its new title kept");
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    /// <summary>
    /// (3) The control: the same window with Windows' classic paths left open (WM_NCACTIVATE painting, lParam 0; the title and icon
    /// set with the window visible; WM_NCPAINT and the classic caption's own messages left to DefWindowProc). How many light pixels
    /// the caption band shows through the same triggers and drag: none means the readback cannot see a classic caption here.
    /// </summary>
    private static int ControlWindow()
    {
        var window = NewWindow("HandPeg golden: frame messages, control");
        WindowChrome.SetWindowChrome(window, NewChrome());
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(
            (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                switch (message)
                {
                    case WmNcActivate:
                        handled = true;
                        return DefWindowProc(hwnd, message, wParam, IntPtr.Zero);
                    case WmSetText or WmSetIcon or WmNcPaint or WmNcUahDrawCaption or WmNcUahDrawFrame:
                        handled = true;
                        return DefWindowProc(hwnd, message, wParam, lParam);
                    default:
                        return MainWindow.FrameHook(hwnd, message, wParam, lParam, ref handled);
                }
            });
        try
        {
            window.Show();
            Idle();
            return CaptionLightThroughDrag(new WindowInteropHelper(window).Handle);
        }
        finally
        {
            window.Close();
            Idle();
        }
    }

    /// <summary>
    /// The caption band (the title bar's height at the window's DPI) read back at once after activation, a new title and icon, the
    /// classic caption's own messages and WM_NCPAINT, then after each of <see cref="DragSteps"/> steps that move the window and make
    /// it smaller (nothing of it newly uncovered, so whatever is light there was painted by Windows): how many light pixels in all.
    /// </summary>
    private static int CaptionLightThroughDrag(IntPtr hwnd)
    {
        SendMessage(hwnd, WmNcActivate, 0, IntPtr.Zero);
        SendMessage(hwnd, WmNcActivate, 1, IntPtr.Zero);
        SetText(hwnd, "HandPeg golden: frame messages, dragged");
        SetIcons(hwnd);
        SendMessage(hwnd, WmNcUahDrawCaption, 0, IntPtr.Zero);
        SendMessage(hwnd, WmNcUahDrawFrame, 0, IntPtr.Zero);
        SendMessage(hwnd, WmNcPaint, 1, IntPtr.Zero);
        var light = CaptionLight(hwnd);

        GetWindowRect(hwnd, out var start);
        var (width, height) = (start.Right - start.Left, start.Bottom - start.Top);
        for (var step = 1; step <= DragSteps; step++)
        {
            SetWindowPos(hwnd, IntPtr.Zero, start.Left + 12 * step, start.Top + 8 * step, width - 6 * step, height - 4 * step,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            light += CaptionLight(hwnd);
        }

        return light;
    }

    /// <summary>How many pixels of the window's caption band (its top rows, as tall as the title bar at its DPI) are light.</summary>
    private static int CaptionLight(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var frame))
            return 0;

        var dpi = GetDpiForWindow(hwnd);
        var width = frame.Right - frame.Left;
        var height = Math.Min(frame.Bottom - frame.Top, (int)Math.Ceiling(TitleBarHeight * (dpi > 0 ? dpi : 96) / 96.0));
        return width > 0 && height > 0 ? LightIn(hwnd, 0, 0, width, height) : 0;
    }

    /// <summary>Whether any of the window's frame lies outside its client area (maximized beside a taskbar that hides itself).</summary>
    private static bool FrameOutsideClient(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var frame);
        GetClientRect(hwnd, out var client);
        MapWindowPoints(hwnd, IntPtr.Zero, ref client, 2);
        return client.Left > frame.Left || client.Top > frame.Top || client.Right < frame.Right || client.Bottom < frame.Bottom;
    }

    private static long Style(IntPtr hwnd) => GetWindowLongPtr(hwnd, GwlStyle).ToInt64();

    private static void SetText(IntPtr hwnd, string text)
    {
        var words = Marshal.StringToHGlobalUni(text);
        try
        {
            SendMessage(hwnd, WmSetText, 0, words);
        }
        finally
        {
            Marshal.FreeHGlobal(words);
        }
    }

    // Windows' own application icon (shared: never destroyed), as the window's small and big icon.
    private static void SetIcons(IntPtr hwnd)
    {
        var icon = LoadIcon(IntPtr.Zero, IdiApplication);
        SendMessage(hwnd, WmSetIcon, IconSmall, icon);
        SendMessage(hwnd, WmSetIcon, IconBig, icon);
    }

    // WM_NCCALCSIZE in its short form (wParam 0: the window's rectangle alone), as Windows sends it outside a move or size.
    private static void SendNcCalcSize(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(rect, buffer, false);
            SendMessage(hwnd, WmNcCalcSize, 0, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string WindowText(IntPtr hwnd)
    {
        const int capacity = 512;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            var length = GetWindowText(hwnd, buffer, capacity);
            return Marshal.PtrToStringUni(buffer, Math.Max(0, length));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowText(IntPtr window, IntPtr text, int capacity);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);

    private const int DwmwaNcRenderingEnabled = 1;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRgn(IntPtr window, IntPtr region);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr window);
}
