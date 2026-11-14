using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HandPegApp;

/// <summary>
/// The Playback &amp; A/V Sync Test's picture (Pass 5.5.2-Release, PlaybackSyncCard): a plain native child window, black until the test's
/// engine presents on it, so its swap chain is a real one on the screen (DWM composes it, and DXGI reports when each frame was shown). No
/// overlay and no input of its own: the pointer passes through it to the card under it.
/// </summary>
public sealed partial class PlaybackTestSurface : HwndHost
{
    private const int WsChild = 0x40000000, WsClipSiblings = 0x04000000, BlackBrush = 4, WmNcHitTest = 0x0084, HtTransparent = -1;

    /// <summary>The native window the test's engine presents on; zero until it is made.</summary>
    public nint SurfaceHandle { get; private set; }

    // Windows' own window procedure, painting black (VideoSurface's way); "static" if the class cannot be had.
    private static readonly Lazy<string> SurfaceClass = new(() =>
    {
        const string name = "HandPegSyncTestSurface";
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(),
            Procedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"), Instance = GetModuleHandle(null),
            Background = GetStockObject(BlackBrush), ClassName = Marshal.StringToHGlobalUni(name),
        };
        return windowClass.Procedure != 0 && RegisterClassEx(windowClass) != 0 ? name : "static";
    });

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        SurfaceHandle = CreateWindowEx(0, SurfaceClass.Value, "", WsChild | WsClipSiblings, 0, 0, 0, 0, hwndParent.Handle, 0, GetModuleHandle(null), 0);
        return new HandleRef(this, SurfaceHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        SurfaceHandle = 0;
        DestroyWindow(hwnd.Handle);
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return HtTransparent;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public int Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(in WindowClass windowClass);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? module);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int kind);
}
