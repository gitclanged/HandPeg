using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace HandPegApp.Services;

/// <summary>
/// Suppresses Windows menu-mode beeps: menu-less windows still enter menu mode on Alt, F10 or Alt+letter (WM_SYSCHAR, WM_SYSCOMMAND
/// SC_KEYMENU) and beep on the next unmatched input. Every window (<see cref="Attach"/>, class handler registered in App.OnStartup)
/// handles these itself, and unknown menu keys (WM_MENUCHAR) close silently. Alt+Space still opens the system menu; there are no access
/// keys, and Alt shortcuts are KeyDown-based (KeybindManager) and unaffected.
/// <para>
/// Never modifies Windows accessibility settings (toggling Sticky/Filter/Toggle Keys hotkeys on focus changes triggered the
/// accessibility siren). Sole exception: <see cref="RepairStickyKeys"/>, which undoes a legacy build's changes.
/// </para>
/// </summary>
internal static partial class QuietKeys
{
    private const int WmSysChar = 0x0106, WmSysCommand = 0x0112, WmMenuChar = 0x0120, ScKeyMenu = 0xF100, MncClose = 1, Space = 0x20;

    private const uint SpiGetStickyKeys = 0x003A, SpiSetStickyKeys = 0x003B, SpifUpdateIniFile = 0x1;

    // Restored Sticky Keys flags: off, no audible feedback, five-Shift hotkey off.
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
    /// Startup repair (off the UI thread): if a legacy build left Sticky Keys flags at zero (a value Windows never sets), restore them once
    /// silently without broadcast and delete the legacy marker. Persists to the profile only when the zero is persisted there too.
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

    /// <summary>Registers the window class handler and schedules the one-time repair (App.OnStartup, before any window).</summary>
    public static void Follow()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window)
                Attach(window);
        }));

        // Swallow unhandled wheel-with-button input at the window so Windows never processes it.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
                e.Handled = true;
        }));
        ThreadPool.QueueUserWorkItem(_ => RepairStickyKeys());
    }
}
