using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HandPegApp.Native;
using HandPegApp.Services;
using static HandPegApp.Native.Gdi32;
using static HandPegApp.Native.Kernel32;
using static HandPegApp.Native.Shell32;
using static HandPegApp.Native.User32;

namespace HandPegApp.Shell;

// ----- Auto-hide taskbar backdrop -----
// A maximized window stops HiddenTaskbarStrip px short of an auto-hide edge (see the taskbar section), and Windows 11's retracted
// taskbar is transparent, so the wallpaper showed through that strip. Each such strip gets a backdrop: an owned popup in the window's
// background colour, layered and click-through (WS_EX_LAYERED | WS_EX_TRANSPARENT), never activated (WS_EX_NOACTIVATE), off Alt+Tab
// (WS_EX_TOOLWINDOW). It is never the foreground window and covers only the strip, so the shell sees no full-screen window: the
// taskbar still reveals on hover (it is topmost, above the backdrop) and the owner keeps DWM frame rendering. UI thread only.
internal static partial class WindowFrame
{
    internal const int WmWindowPosChanged = 0x0047, WmDestroy = 0x0002;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x80, WsExTransparent = 0x20, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const int GclpBackground = -10, BlackBrush = 4;
    private const uint LwaAlpha = 2;

    // Backdrops per owner window, one slot per auto-hide edge (left, top, right, bottom); 0 = not made yet.
    private static readonly Dictionary<IntPtr, IntPtr[]> Backdrops = [];
    private static readonly HashSet<IntPtr> BackdropsDue = [];

    // The class brush (one theme colour for every backdrop) and its 0x00BBGGRR colour; -1 before the first.
    private static (int Color, IntPtr Brush) _backdropBrush = (-1, IntPtr.Zero);
    private static bool _themeWatched;

    // DefWindowProcW paints the class brush on WM_ERASEBKGND: no window procedure of our own.
    private static readonly Lazy<string> BackdropClass = new(() =>
    {
        const string name = "HandPegTaskbarBackdrop";
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(),
            Procedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"), Instance = GetModuleHandle(null),
            Background = GetStockObject(BlackBrush), ClassName = Marshal.StringToHGlobalUni(name),
        };

        // Class name string lives for the process, as the class does.
        return windowClass.Procedure != IntPtr.Zero && RegisterClassEx(windowClass) != 0 ? name : "";
    });

    /// <summary>
    /// Pure backdrop rects (physical pixels) for a maximized window: per auto-hide edge bit (1 &lt;&lt; edge, LTRB), the strip between
    /// the window and the monitor edge, along the window's length (to the monitor corner where the neighbouring edge hides too).
    /// Exercised by golden window-frame-messages.
    /// </summary>
    internal static List<(int Edge, (int Left, int Top, int Right, int Bottom) Rect)> BackdropStripsOf((int Left, int Top, int Right, int Bottom) monitor,
        (int Left, int Top, int Right, int Bottom) window, int edges)
    {
        bool Hidden(int edge) => (edges & (1 << edge)) != 0;
        var (left, right) = (Hidden(AbeLeft) ? monitor.Left : window.Left, Hidden(AbeRight) ? monitor.Right : window.Right);
        var (top, bottom) = (Hidden(AbeTop) ? monitor.Top : window.Top, Hidden(AbeBottom) ? monitor.Bottom : window.Bottom);
        var strips = new List<(int, (int, int, int, int))>();
        if (Hidden(AbeLeft) && window.Left > monitor.Left)
            strips.Add((AbeLeft, (monitor.Left, top, window.Left, bottom)));
        if (Hidden(AbeTop) && window.Top > monitor.Top)
            strips.Add((AbeTop, (left, monitor.Top, right, window.Top)));
        if (Hidden(AbeRight) && window.Right < monitor.Right)
            strips.Add((AbeRight, (window.Right, top, monitor.Right, bottom)));
        if (Hidden(AbeBottom) && window.Bottom < monitor.Bottom)
            strips.Add((AbeBottom, (left, window.Bottom, right, monitor.Bottom)));
        return strips;
    }

    /// <summary>The visible backdrops of a window (golden window-frame-messages).</summary>
    internal static IReadOnlyList<IntPtr> BackdropsOf(IntPtr owner) =>
        Backdrops.TryGetValue(owner, out var slots) ? slots.Where(s => s != IntPtr.Zero && IsWindowVisible(s)).ToList() : [];

    /// <summary>
    /// WM_WINDOWPOSCHANGED: queues a backdrop update (coalesced, at Render priority, outside the message). A restored window without
    /// backdrops returns at once, so a move or resize loop costs one check per step.
    /// </summary>
    private static void BackdropAfterMove(IntPtr owner)
    {
        if (!IsZoomed(owner) && !Backdrops.ContainsKey(owner) || !BackdropsDue.Add(owner))
            return;

        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            BackdropsDue.Remove(owner);
            SyncBackdrop(owner);
        }, DispatcherPriority.Render);
    }

    /// <summary>Shows, moves or hides a window's backdrops for its state now: visible, maximized, beside an auto-hide edge.</summary>
    internal static void SyncBackdrop(IntPtr owner)
    {
        var strips = new List<(int Edge, (int Left, int Top, int Right, int Bottom) Rect)>();
        var monitorHandle = MonitorFromWindow(owner, MonitorDefaultToNearest);
        if (IsWindowVisible(owner) && IsZoomed(owner) && !IsIconic(owner) && AutoHideEdges(monitorHandle) is var edges and not 0
            && TryMonitorInfo(monitorHandle, out var info) && GetWindowRect(owner, out var rect))
        {
            var monitor = info.Monitor;
            strips = BackdropStripsOf((monitor.Left, monitor.Top, monitor.Right, monitor.Bottom), (rect.Left, rect.Top, rect.Right, rect.Bottom), edges);
        }

        if (!Backdrops.TryGetValue(owner, out var slots))
        {
            if (strips.Count == 0 || BackdropClass.Value.Length == 0)
                return;
            Backdrops[owner] = slots = new IntPtr[4];
        }

        for (var edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            var at = strips.FindIndex(s => s.Edge == edge);
            if (at < 0)
            {
                if (slots[edge] != IntPtr.Zero)
                    ShowWindow(slots[edge], SwHide);
                continue;
            }

            if (slots[edge] == IntPtr.Zero)
            {
                slots[edge] = CreateWindowEx(WsExToolWindow | WsExTransparent | WsExLayered | WsExNoActivate, BackdropClass.Value, "", WsPopup, 0, 0, 0, 0, owner,
                    IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (slots[edge] == IntPtr.Zero)
                    continue;
                SetLayeredWindowAttributes(slots[edge], 0, 255, LwaAlpha);
            }

            PaintBackdrops(slots[edge], owner);
            var (left, top, right, bottom) = strips[at].Rect;
            SetWindowPos(slots[edge], IntPtr.Zero, left, top, right - left, bottom - top, SwpNoActivate | SwpNoZOrder | SwpNoOwnerZOrder | SwpShowWindow);
        }

        if (!_themeWatched)
        {
            _themeWatched = true;
            ThemeManager.Applied += () =>
            {
                foreach (var shown in Backdrops.Keys.ToList())
                    SyncBackdrop(shown);
            };
        }
    }

    // Sets the class brush to the owner's background colour when it changed (a theme switch) and repaints every backdrop.
    private static void PaintBackdrops(IntPtr strip, IntPtr owner)
    {
        var color = HwndSource.FromHwnd(owner)?.RootVisual is Window { Background: System.Windows.Media.SolidColorBrush background }
            ? background.Color : System.Windows.Media.Colors.Black;
        var rgb = color.R | color.G << 8 | color.B << 16;
        if (rgb == _backdropBrush.Color)
            return;

        var brush = CreateSolidBrush(rgb);
        SetClassLongPtr(strip, GclpBackground, brush);
        if (_backdropBrush.Brush != IntPtr.Zero)
            DeleteObject(_backdropBrush.Brush);
        _backdropBrush = (rgb, brush);
        foreach (var slot in Backdrops.Values.SelectMany(s => s).Where(s => s != IntPtr.Zero))
            InvalidateRect(slot, IntPtr.Zero, true);
    }

    /// <summary>WM_DESTROY of an owner: Windows destroys its owned backdrops first; forget them.</summary>
    private static void ForgetBackdrops(IntPtr owner)
    {
        BackdropsDue.Remove(owner);
        if (Backdrops.Remove(owner, out var slots))
        {
            foreach (var strip in slots.Where(s => s != IntPtr.Zero))
                DestroyWindow(strip);
        }
    }
}
