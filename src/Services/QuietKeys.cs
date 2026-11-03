using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace HandPegApp.Services;

/// <summary>
/// Keeps Windows from beeping at HandPeg's keys (5.0-Release, the user's directive 2). A window with no menu bar still enters
/// Windows' menu mode on Alt alone, F10 or Alt with a letter (WM_SYSCHAR, then WM_SYSCOMMAND SC_KEYMENU), and beeps at the next
/// key or click that names nothing. Every HandPeg window (<see cref="Attach"/>, through a class handler on <see cref="Window"/>
/// made in App.OnStartup) answers those itself, and a menu's unknown key (WM_MENUCHAR) closes it quietly. Alt + Space still
/// opens the system menu. HandPeg has no access keys (no "_" in a header), and its Alt shortcuts are key downs
/// (KeybindManager), which this leaves alone.
/// <para>
/// HandPeg never touches Windows' accessibility settings (5.1-AutoHud-Part1, the user's diagnosis: switching the Sticky, Filter
/// and Toggle Keys hotkeys off and on as HandPeg gained and lost focus made Windows play its accessibility siren, 440, 554, 659
/// and 880 Hz). The one exception is <see cref="RepairStickyKeys"/>, which undoes what that build left behind.
/// </para>
/// </summary>
internal static partial class QuietKeys
{
    private const int WmSysChar = 0x0106, WmSysCommand = 0x0112, WmMenuChar = 0x0120, ScKeyMenu = 0xF100, MncClose = 1, Space = 0x20;

    private const uint SpiGetStickyKeys = 0x003A, SpiSetStickyKeys = 0x003B, SpifUpdateIniFile = 0x1;

    // Sticky Keys off, no audible feedback, its five-Shift hotkey off: the value the user asked to be put back.
    private const uint StickyKeysStandard = 0x3A;

    [StructLayout(LayoutKind.Sequential)]
    private struct StickyKeys
    {
        public uint Size;
        public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint size, ref StickyKeys value, uint winIni);

    /// <summary>Makes a window answer the messages that would make it beep (once a window; its handle must exist).</summary>
    public static void Attach(Window window)
    {
        if (window.GetValue(AttachedProperty) is true || PresentationSource.FromVisual(window) is not HwndSource source)
            return;
        window.SetValue(AttachedProperty, true);
        source.AddHook(Hook);
    }

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("QuietKeysAttached", typeof(bool), typeof(QuietKeys), new PropertyMetadata(false));

    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            // Alt with a letter (Alt + Space is the system menu's, and goes on).
            case WmSysChar when wParam != Space:
                handled = true;
                break;

            // Menu mode, which Alt alone, F10 and Alt with a letter start in a window without a menu bar.
            case WmSysCommand when ((int)wParam & 0xFFF0) == ScKeyMenu && lParam != Space:
                handled = true;
                break;

            // A key a menu (the system menu) has no item for: closed, without the beep.
            case WmMenuChar:
                handled = true;
                return MncClose << 16;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// At start, off the interface's thread: where the old hush left Sticky Keys' flags at zero (which Windows itself never
    /// has) they are put back, once, with no broadcast and no sound, and the old hush's file goes. Written to the user's
    /// profile only when the zero is in the profile too; a zero in memory ends with the sign-out anyway.
    /// </summary>
    private static void RepairStickyKeys()
    {
        try
        {
            System.IO.File.Delete(System.IO.Path.Combine(AppPaths.DataRoot, "quiet-keys.txt"));

            var sticky = new StickyKeys { Size = (uint)Marshal.SizeOf<StickyKeys>() };
            if (!SystemParametersInfo(SpiGetStickyKeys, sticky.Size, ref sticky, 0) || sticky.Flags != 0)
                return;

            using var saved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\StickyKeys");
            var zeroInProfile = (saved?.GetValue("Flags") as string)?.Trim() == "0";
            sticky.Flags = StickyKeysStandard;
            if (SystemParametersInfo(SpiSetStickyKeys, sticky.Size, ref sticky, zeroInProfile ? SpifUpdateIniFile : 0))
                AppLog.Write("Sticky Keys' flags, left at zero by an earlier HandPeg, were put back.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>Hooks the application up (App.OnStartup, before any window): every window as it loads, and the one-time repair.</summary>
    public static void Follow()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window)
                Attach(window);
        }));

        // A wheel turned with a button held that nothing took stops at the window: it is never left to Windows (5.1-DogfoodPolish).
        EventManager.RegisterClassHandler(typeof(Window), Mouse.MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
                e.Handled = true;
        }));
        ThreadPool.QueueUserWorkItem(_ => RepairStickyKeys());
    }
}
