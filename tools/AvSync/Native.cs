using System.Reflection;
using System.Runtime.InteropServices;
using HandPegApp.Services.Audio;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// The few native calls the session's measurement needs, made by hand (COM through vtables, as HandPeg's own code does):
/// DXGI's frame statistics of the engine's swap chain, DWM's vblank grid, and the sound stream's latency.
/// </summary>
internal static unsafe partial class Native
{
    // IDXGISwapChain: IUnknown (0-2), IDXGIObject (3-6), IDXGIDeviceSubObject (7), Present 8 ... GetFrameStatistics 16, GetLastPresentCount 17.
    private const int GetFrameStatisticsSlot = 16, GetLastPresentCountSlot = 17;

    // IAudioClient: IUnknown (0-2), Initialize 3, GetBufferSize 4, GetStreamLatency 5.
    private const int GetStreamLatencySlot = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameStatisticsData
    {
        public uint PresentCount;
        public uint PresentRefreshCount;
        public uint SyncRefreshCount;
        public long SyncQpcTime;
        public long SyncGpuTime;
    }

    /// <summary>The count of the swap chain's last Present (0 when it has none or cannot say).</summary>
    public static uint LastPresentCount(nint swapChain)
    {
        if (swapChain == 0)
            return 0;
        uint count = 0;
        var hr = ((delegate* unmanaged<nint, uint*, int>)(*(void***)swapChain)[GetLastPresentCountSlot])(swapChain, &count);
        return hr >= 0 ? count : 0;
    }

    /// <summary>The present shown at the last vblank and that vblank's QPC (the swap chain's GetFrameStatistics); false when it gives none.</summary>
    public static bool FrameStatistics(nint swapChain, out uint presentCount, out long syncQpc)
    {
        (presentCount, syncQpc) = (0, 0);
        if (swapChain == 0)
            return false;
        FrameStatisticsData data;
        var hr = ((delegate* unmanaged<nint, FrameStatisticsData*, int>)(*(void***)swapChain)[GetFrameStatisticsSlot])(swapChain, &data);
        if (hr < 0 || data.PresentCount == 0 || data.SyncQpcTime == 0)
            return false;
        (presentCount, syncQpc) = (data.PresentCount, data.SyncQpcTime);
        return true;
    }

    /// <summary>DWM's last vblank and its refresh period, in QPC ticks (DwmGetCompositionTimingInfo for the whole desktop).</summary>
    public static bool VBlank(out long vblank, out long period)
    {
        // DWM_TIMING_INFO, packed to the byte (dwmapi.h): cbSize 0, rateRefresh 4, qpcRefreshPeriod 12, rateCompose 20, qpcVBlank 28 ... 292 bytes.
        const int size = 292;
        var info = stackalloc byte[512];
        new Span<byte>(info, 512).Clear();
        *(uint*)info = size;
        (vblank, period) = (0, 0);
        if (DwmGetCompositionTimingInfo(0, info) < 0)
            return false;
        (period, vblank) = (*(long*)(info + 12), *(long*)(info + 28));
        return period > 0 && vblank > 0;
    }

    /// <summary>The sound stream's latency as WASAPI reports it (IAudioClient::GetStreamLatency), seconds; null without a device.</summary>
    public static double? StreamLatency(WasapiAudioEngine audio)
    {
        if (!audio.HasDevice || typeof(WasapiAudioEngine).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(audio) is not nint client || client == 0)
            return null;
        long latency = 0;
        var hr = ((delegate* unmanaged<nint, long*, int>)(*(void***)client)[GetStreamLatencySlot])(client, &latency);
        return hr >= 0 ? latency / 1e7 : null;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(nint window, byte* info);
}

/// <summary>
/// The session player's window: a pop-up of the harness's own, off every screen (beyond the virtual desktop's left edge),
/// shown without being activated (no focus taken, no taskbar button: a tool window that never activates), with a black
/// class brush. The engine's swap chain needs a real window with a size; nothing of it is ever on a screen.
/// </summary>
internal sealed unsafe partial class OffscreenWindow : IDisposable
{
    private const uint WsPopup = 0x80000000, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4, SmXVirtualScreen = 76, SmYVirtualScreen = 77, BlackBrush = 4;

    private static readonly WindowProcedure Procedure = DefWindowProcW;
    private static ushort _class;

    private OffscreenWindow(nint handle) => Handle = handle;

    public nint Handle { get; }

    public static OffscreenWindow Create(int width, int height)
    {
        var instance = GetModuleHandleW(null);
        if (_class == 0)
        {
            fixed (char* name = "HandPegAvSyncPlayer")
            {
                var description = new WindowClass
                {
                    Size = (uint)sizeof(WindowClass),
                    Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                    Instance = instance,
                    Background = GetStockObject(BlackBrush),
                    ClassName = name,
                };
                _class = RegisterClassExW(&description);
            }

            if (_class == 0)
                throw new InvalidOperationException($"The player's window class could not be registered ({Marshal.GetLastPInvokeError()}).");
        }

        var (x, y) = (GetSystemMetrics(SmXVirtualScreen) - width - 4000, GetSystemMetrics(SmYVirtualScreen) - height - 4000);
        var handle = CreateWindowExW(WsExToolWindow | WsExNoActivate, "HandPegAvSyncPlayer", "HandPeg A/V sync", WsPopup, x, y, width, height, 0, 0, instance, 0);
        if (handle == 0)
            throw new InvalidOperationException($"The player's window could not be made ({Marshal.GetLastPInvokeError()}).");
        ShowWindow(handle, SwShowNoActivate);
        return new OffscreenWindow(handle);
    }

    public void Dispose() => DestroyWindow(Handle);

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WindowClass* description);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int index);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
