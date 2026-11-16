using System.Runtime.InteropServices;

namespace HandPegApp.Native;

/// <summary>RECT: a rectangle in real pixels (screen or client coordinates).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;
}

/// <summary>POINT: a point in real pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X, Y;
}

/// <summary>MONITORINFO: a monitor's rectangle and working area in real pixels. Set <see cref="Size"/> before asking.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor, Work;
    public uint Flags;
}

/// <summary>WINDOWPOS: what WM_WINDOWPOSCHANGING proposes for a window.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowPos
{
    public nint Window, InsertAfter;
    public int X, Y, Width, Height;
    public uint Flags;
}

/// <summary>WNDCLASSEXW: a window class to register (<see cref="User32.RegisterClassEx"/>). Set <see cref="Size"/> before registering.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowClass
{
    public int Size, Style;
    public nint Procedure;
    public int ClassExtra, WindowExtra;
    public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
}

/// <summary>
/// user32's windows, monitors and window-DC painting: one declaration each (src/Native rule). Only stubs and constants: no type
/// initializer, nothing at start-up.
/// </summary>
internal static partial class User32
{
    public const uint MonitorDefaultToNull = 0, MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;
    public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020,
        SwpShowWindow = 0x0040, SwpNoOwnerZOrder = 0x0200;
    public const int GwlStyle = -16, GwlExStyle = -20;
    public const int SwHide = 0, SwShowNoActivate = 4, SwShow = 5, SwRestore = 9;

    /// <summary>A monitor's <see cref="MonitorInfo"/> (its size field set here); false for no monitor or a failed call.</summary>
    public static bool TryMonitorInfo(nint monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info);
    }

    /// <summary>A window's client area on the screen, in real pixels (with WindowChrome, all of the window it draws).</summary>
    public static bool ClientOnScreen(nint window, out NativeRect rect)
    {
        if (!GetClientRect(window, out rect))
            return false;
        MapWindowPoints(window, 0, ref rect, 2);
        return true;
    }

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(in NativeRect rect, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>GetMonitorInfoW into a caller's MONITORINFOEXW buffer (104 bytes, its cbSize set): the monitor's device name too.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetMonitorInfo(nint monitor, byte* infoEx);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    public static partial int MapWindowPoints(nint from, nint to, ref NativeRect points, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint window, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowDC(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint dc, in NativeRect rect, nint brush);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    /// <summary>EnumDisplaySettingsW into a caller's DEVMODEW buffer (its dmSize set); <paramref name="device"/> null for the main display.</summary>
    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumDisplaySettings(char* device, int mode, byte* devMode);
}

/// <summary>gdi32's brushes, clipping and bitmaps: one declaration each (src/Native rule).</summary>
internal static partial class Gdi32
{
    [LibraryImport("gdi32.dll")]
    public static partial int ExcludeClipRect(nint dc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(int color);

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint gdiObject);
}

/// <summary>The shell's app bars (taskbars): one declaration each (src/Native rule).</summary>
internal static partial class Shell32
{
    public const uint AbmGetState = 4, AbmGetTaskbarPos = 5, AbmGetAutoHideBarEx = 0x0B, AbsAutoHide = 1;
    public const int AbeLeft = 0, AbeTop = 1, AbeRight = 2, AbeBottom = 3;

    /// <summary>APPBARDATA.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AppBarData
    {
        public int Size;
        public nint Window;
        public uint CallbackMessage, Edge;
        public NativeRect Rect;
        public nint Param;
    }

    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint message, ref AppBarData data);
}

/// <summary>The desktop window manager's window attributes: one declaration each (src/Native rule).</summary>
internal static partial class Dwmapi
{
    public const int DwmwaNcRenderingEnabled = 1;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
