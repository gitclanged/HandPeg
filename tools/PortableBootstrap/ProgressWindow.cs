using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>
/// The window shown while .NET is downloaded (Pass 5.2.1): dark (HandPeg's #0F0F12, a dark title bar), drawn with GDI (no WPF yet: there
/// is no .NET to run it), a line of what is happening and a bar of how far along, at the monitor's DPI (its thread per-monitor aware;
/// put back after, so HandPeg's own process settings are as its apphost leaves them). The work runs on a thread of its own and posts
/// here; closing the window cancels it. <see cref="Run"/> returns once the window has gone.
/// </summary>
internal static unsafe class ProgressWindow
{
    private const uint Changed = Native.WmApp + 1, Finished = Native.WmApp + 2;
    private const int Background = 0x12_0F_0F, Track = 0x30_2A_2A, Text = 0xEE_EA_EA, Muted = 0xA6_A0_A0, Bar = 0xF6_82_3B; // COLORREFs: 0x00BBGGRR.
    private const int Width = 560, Height = 176;

    private static IntPtr _window;
    private static bool _closedByUser;
    private static readonly object Gate = new();
    private static string _line = "";
    private static double _done;
    private static string _title = "";

    /// <summary>Shows the window and runs <paramref name="work"/> on another thread until it ends or the window is closed; whether it was closed first.</summary>
    public static bool Run(string title, Action<PortableDotNet.Progress, CancellationToken> work, out Exception? failure)
    {
        _title = title;
        var previous = Native.SetThreadDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);
        using var cancel = new CancellationTokenSource();
        Exception? failed = null;
        var closed = false;
        try
        {
            _window = Create($"HandPeg Portable — {title}...");
            var worker = new Thread(() =>
            {
                try
                {
                    work((line, done) =>
                    {
                        lock (Gate)
                            (_line, _done) = (line, done);
                        Native.PostMessage(_window, Changed, IntPtr.Zero, IntPtr.Zero);
                    }, cancel.Token);
                }
                catch (Exception ex)
                {
                    failed = ex;
                }
                finally
                {
                    Native.PostMessage(_window, Finished, IntPtr.Zero, IntPtr.Zero);
                }
            }) { IsBackground = true, Name = "HandPeg portable .NET download" };
            worker.Start();

            while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == Finished)
                {
                    Native.DestroyWindow(_window);
                    continue;
                }

                Native.TranslateMessage(message);
                Native.DispatchMessage(message);
            }

            // Closed by the user: the download stops at its next piece and cleans up after itself.
            closed = _closedByUser;
            if (worker.IsAlive)
            {
                closed = true;
                cancel.Cancel();
                worker.Join(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            Native.SetThreadDpiAwarenessContext(previous);
        }

        failure = closed ? null : failed;
        return closed || failed is OperationCanceledException;
    }

    private static IntPtr Create(string title)
    {
        var instance = Native.GetModuleHandle(IntPtr.Zero);
        fixed (char* name = "HandPegPortableDotNet")
        {
            var windowClass = new Native.WndClassEx
            {
                Size = (uint)sizeof(Native.WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                Cursor = Native.LoadCursor(IntPtr.Zero, 32512),
                // HandPeg.exe's own icon (the first in its resources, as the SDK puts ApplicationIcon there).
                Icon = Native.LoadImage(instance, 32512, 1, 0, 0, 0x8040),
                ClassName = name,
            };
            Native.RegisterClassEx(windowClass);
        }

        var dpi = Native.GetDpiForSystem();
        var (width, height) = (Scale(Width, dpi), Scale(Height, dpi));
        Native.SystemParametersInfoW(0x0030, 0, out var work, 0); // SPI_GETWORKAREA: the main monitor's, where it opens.
        var window = Native.CreateWindowEx(0, "HandPegPortableDotNet", title, Native.WsOverlapped | Native.WsCaption | Native.WsSysMenu | Native.WsMinimizeBox,
            work.Left + (work.Right - work.Left - width) / 2, work.Top + (work.Bottom - work.Top - height) / 2, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        // The dark title bar, in the window's own colour (Windows 11; earlier versions keep theirs).
        var on = 1;
        Native.DwmSetWindowAttribute(window, 20, ref on, sizeof(int));
        var caption = Background;
        Native.DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
        Native.ShowWindow(window, Native.SwShow);
        Native.SetForegroundWindow(window);
        return window;
    }

    private static int Scale(int value, uint dpi) => (int)Math.Round(value * dpi / 96.0);

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Changed:
                Native.InvalidateRect(window, IntPtr.Zero, false);
                return IntPtr.Zero;
            case Native.WmEraseBkgnd:
                return 1;
            case Native.WmPaint:
                Paint(window);
                return IntPtr.Zero;
            case Native.WmDpiChanged:
                {
                    // The size Windows suggests for the new monitor's DPI.
                    var suggested = (Native.Rect*)lParam;
                    Native.SetWindowPos(window, IntPtr.Zero, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, 0x0014);
                    return IntPtr.Zero;
                }

            case Native.WmClose:
                _closedByUser = true;
                break;
            case Native.WmDestroy:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>The window drawn whole into a picture first, then put on the screen at once (no flicker as the bar moves).</summary>
    private static void Paint(IntPtr window)
    {
        string line;
        double done;
        lock (Gate)
            (line, done) = (_line, _done);

        var screen = Native.BeginPaint(window, out var paint);
        Native.GetClientRect(window, out var client);
        var (width, height) = (client.Right, client.Bottom);
        var dc = Native.CreateCompatibleDC(screen);
        var picture = Native.CreateCompatibleBitmap(screen, width, height);
        var old = Native.SelectObject(dc, picture);
        var dpi = Native.GetDpiForWindow(window);
        int S(int value) => Scale(value, dpi);

        Fill(dc, client, Background);
        Native.SetBkMode(dc, Native.Transparent);
        var heading = Native.CreateFont(-S(17), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var body = Native.CreateFont(-S(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var font = Native.SelectObject(dc, heading);
        Native.SetTextColor(dc, Text);
        var at = new Native.Rect { Left = S(24), Top = S(20), Right = width - S(24), Bottom = S(48) };
        Native.DrawText(dc, _title, -1, ref at, Native.DtLeft | Native.DtSingleLine | Native.DtVCenter | Native.DtEndEllipsis | Native.DtNoPrefix);

        Native.SelectObject(dc, body);
        Native.SetTextColor(dc, Muted);
        at = new Native.Rect { Left = S(24), Top = S(52), Right = width - S(24), Bottom = S(92) };
        Native.DrawText(dc, "This copy of HandPeg runs on .NET, which this PC does not have. It is put in this folder (portable\\dotnet): nothing is installed and no admin rights are needed.",
            -1, ref at, Native.DtLeft | Native.DtWordBreak | Native.DtNoPrefix);

        var track = new Native.Rect { Left = S(24), Top = S(104), Right = width - S(24), Bottom = S(110) };
        Fill(dc, track, Track);
        Fill(dc, track with { Right = track.Left + (int)Math.Round((track.Right - track.Left) * Math.Clamp(done, 0, 1)) }, Bar);

        Native.SetTextColor(dc, Text);
        at = new Native.Rect { Left = S(24), Top = S(118), Right = width - S(24), Bottom = S(142) };
        Native.DrawText(dc, line, -1, ref at, Native.DtLeft | Native.DtSingleLine | Native.DtVCenter | Native.DtEndEllipsis | Native.DtNoPrefix);

        Native.BitBlt(screen, 0, 0, width, height, dc, 0, 0, 0x00CC0020);
        Native.SelectObject(dc, font);
        Native.DeleteObject(heading);
        Native.DeleteObject(body);
        Native.SelectObject(dc, old);
        Native.DeleteObject(picture);
        Native.DeleteDC(dc);
        Native.EndPaint(window, paint);
    }

    private static void Fill(IntPtr dc, Native.Rect rect, int color)
    {
        var brush = Native.CreateSolidBrush(color);
        Native.FillRect(dc, rect, brush);
        Native.DeleteObject(brush);
    }
}
