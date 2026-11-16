using System.Runtime.InteropServices;
using System.Windows.Interop;
using HandPegApp.Native;
using static HandPegApp.Native.User32;
using static HandPegApp.Native.Gdi32;
using static HandPegApp.Native.Kernel32;

namespace HandPegApp;

/// <summary>
/// Playback &amp; A/V Sync Test surface (PlaybackSyncCard): a plain native child window, black until the test engine presents, so the
/// swap chain is a real on-screen one (DWM composes it and DXGI reports per-frame present times). No overlay or input: hit testing
/// returns HTTRANSPARENT so the pointer reaches the card beneath.
/// </summary>
public sealed partial class PlaybackTestSurface : HwndHost
{
    private const int WsChild = 0x40000000, WsClipSiblings = 0x04000000, BlackBrush = 4, WmNcHitTest = 0x0084, HtTransparent = -1;

    /// <summary>HWND the test engine presents to; zero until created.</summary>
    public nint SurfaceHandle { get; private set; }

    // Window class using DefWindowProc with a black background brush (as VideoSurface does); falls back to "static" if registration fails.
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

}
