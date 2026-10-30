using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HandPegApp.Services;

/// <summary>
/// Keeps Windows from beeping at HandPeg's keys (5.0-Release, the user's directive 2: "loud Windows system / Sticky-Keys-style
/// machine beeps fire randomly", while editing and while tests send keys).
/// <list type="bullet">
/// <item>The menu beep: a window with no menu bar still enters Windows' menu mode on Alt alone, F10 or Alt with a letter
/// (WM_SYSCHAR, then WM_SYSCOMMAND SC_KEYMENU), and beeps at the next key or click that names nothing. Every HandPeg window
/// (<see cref="Attach"/>, through a class handler on <see cref="Window"/> made in App.OnStartup) answers those itself, and a
/// menu's unknown key (WM_MENUCHAR) closes it quietly. Alt + Space still opens the system menu. HandPeg has no access keys
/// (no "_" in a header), and its Alt shortcuts are key downs (KeybindManager), which this leaves alone.</item>
/// <item>The accessibility hotkeys: Shift pressed five times (Sticky Keys), right Shift held eight seconds (Filter Keys)
/// and Num Lock held five (Toggle Keys) each start Windows' prompt with its loud sound, and trims hold and tap Shift. While
/// HandPeg is the active application (<see cref="Hush"/> / <see cref="Restore"/>, from Application.Activated and
/// Deactivated) the hotkeys of the features that are off are switched off too, in memory only (never written to the
/// user's profile, no WM_SETTINGCHANGE broadcast): the values Windows had are put back when HandPeg is left or closed, and
/// a sign-out puts them back in any case. A feature the user has switched on is never touched. While hushed, the values
/// Windows had are also in a small file (<see cref="SavedPath"/>), so that a HandPeg ended without a goodbye (a crash in a
/// driver, End task) has them put back by its next start.</item>
/// </list>
/// </summary>
internal static partial class QuietKeys
{
    private const int WmSysChar = 0x0106, WmSysCommand = 0x0112, WmMenuChar = 0x0120, ScKeyMenu = 0xF100, MncClose = 1, Space = 0x20;

    private const uint SpiGetFilterKeys = 0x0032, SpiSetFilterKeys = 0x0033, SpiGetToggleKeys = 0x0034, SpiSetToggleKeys = 0x0035,
        SpiGetStickyKeys = 0x003A, SpiSetStickyKeys = 0x003B;

    // The *KF_ flags: on, the hotkey that switches it on, the prompt the hotkey asks, the hotkey's sound (the same values for
    // all three structures).
    private const uint FeatureOn = 0x1, HotkeyActive = 0x4, ConfirmHotkey = 0x8, HotkeySound = 0x10;
    private const uint Hotkeys = HotkeyActive | ConfirmHotkey | HotkeySound;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlagsOnly
    {
        public uint Size;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys
    {
        public uint Size;
        public uint Flags;
        public uint WaitMilliseconds, DelayMilliseconds, RepeatMilliseconds, BounceMilliseconds;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint size, ref FlagsOnly value, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint size, ref FilterKeys value, uint winIni);

    private static readonly object Gate = new();

    /// <summary>The values Windows had while HandPeg has them hushed: put back by the next start when HandPeg ended without Restore.</summary>
    private static string SavedPath => System.IO.Path.Combine(AppPaths.DataRoot, "quiet-keys.txt");

    // What Windows had before Hush, while it is hushed (null otherwise: nothing to put back).
    private static FlagsOnly? _sticky, _toggle;
    private static FilterKeys? _filter;

    // ----- The menu beep -----

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

    // ----- The accessibility hotkeys -----

    /// <summary>Switches off the hotkeys of Sticky, Filter and Toggle Keys while they are off (HandPeg became the active application).</summary>
    public static void Hush()
    {
        lock (Gate)
        {
            if (_sticky is not null || _toggle is not null || _filter is not null)
                return;

            var sticky = new FlagsOnly { Size = (uint)Marshal.SizeOf<FlagsOnly>() };
            if (SystemParametersInfo(SpiGetStickyKeys, sticky.Size, ref sticky, 0) && (sticky.Flags & FeatureOn) == 0 && (sticky.Flags & Hotkeys) != 0)
            {
                var quiet = sticky with { Flags = sticky.Flags & ~Hotkeys };
                if (SystemParametersInfo(SpiSetStickyKeys, quiet.Size, ref quiet, 0))
                    _sticky = sticky;
            }

            var toggle = new FlagsOnly { Size = (uint)Marshal.SizeOf<FlagsOnly>() };
            if (SystemParametersInfo(SpiGetToggleKeys, toggle.Size, ref toggle, 0) && (toggle.Flags & FeatureOn) == 0 && (toggle.Flags & Hotkeys) != 0)
            {
                var quiet = toggle with { Flags = toggle.Flags & ~Hotkeys };
                if (SystemParametersInfo(SpiSetToggleKeys, quiet.Size, ref quiet, 0))
                    _toggle = toggle;
            }

            var filter = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
            if (SystemParametersInfo(SpiGetFilterKeys, filter.Size, ref filter, 0) && (filter.Flags & FeatureOn) == 0 && (filter.Flags & Hotkeys) != 0)
            {
                var quiet = filter with { Flags = filter.Flags & ~Hotkeys };
                if (SystemParametersInfo(SpiSetFilterKeys, quiet.Size, ref quiet, 0))
                    _filter = filter;
            }

            if (_sticky is not null || _toggle is not null || _filter is not null)
            {
                var saved = string.Join(' ', _sticky?.Flags ?? 0, _toggle?.Flags ?? 0, _filter?.Flags ?? 0);
                ThreadPool.QueueUserWorkItem(_ => Write(saved));
            }
        }
    }

    // The file is written and deleted off the interface's thread; the newest wish wins (a Restore right after a Hush deletes it).
    private static int _savedVersion;
    private static readonly object FileGate = new();

    private static void Write(string? saved)
    {
        var version = Interlocked.Increment(ref _savedVersion);
        try
        {
            lock (FileGate)
            {
                if (version != Volatile.Read(ref _savedVersion))
                    return;
                if (saved is null)
                    System.IO.File.Delete(SavedPath);
                else
                {
                    System.IO.Directory.CreateDirectory(AppPaths.DataRoot);
                    System.IO.File.WriteAllText(SavedPath, saved);
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// At start, off the interface's thread: the values a HandPeg that ended while hushed left in <see cref="SavedPath"/> are
    /// put back, each only while that feature is still off with its hotkey cleared (as the hush left it), and the file goes.
    /// </summary>
    private static void PutBackWhatACrashLeft()
    {
        try
        {
            if (!System.IO.File.Exists(SavedPath))
                return;
            var parts = System.IO.File.ReadAllText(SavedPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && uint.TryParse(parts[0], out var stickyFlags) && uint.TryParse(parts[1], out var toggleFlags) && uint.TryParse(parts[2], out var filterFlags))
            {
                lock (Gate)
                {
                    if (_sticky is null && _toggle is null && _filter is null)
                    {
                        var sticky = new FlagsOnly { Size = (uint)Marshal.SizeOf<FlagsOnly>() };
                        if (stickyFlags != 0 && SystemParametersInfo(SpiGetStickyKeys, sticky.Size, ref sticky, 0) && (sticky.Flags & (FeatureOn | Hotkeys)) == 0)
                        {
                            sticky.Flags = stickyFlags;
                            SystemParametersInfo(SpiSetStickyKeys, sticky.Size, ref sticky, 0);
                        }

                        var toggle = new FlagsOnly { Size = (uint)Marshal.SizeOf<FlagsOnly>() };
                        if (toggleFlags != 0 && SystemParametersInfo(SpiGetToggleKeys, toggle.Size, ref toggle, 0) && (toggle.Flags & (FeatureOn | Hotkeys)) == 0)
                        {
                            toggle.Flags = toggleFlags;
                            SystemParametersInfo(SpiSetToggleKeys, toggle.Size, ref toggle, 0);
                        }

                        var filter = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
                        if (filterFlags != 0 && SystemParametersInfo(SpiGetFilterKeys, filter.Size, ref filter, 0) && (filter.Flags & (FeatureOn | Hotkeys)) == 0)
                        {
                            filter.Flags = filterFlags;
                            SystemParametersInfo(SpiSetFilterKeys, filter.Size, ref filter, 0);
                        }

                        AppLog.Write("Accessibility hotkeys a HandPeg that ended without closing had switched off were put back.");
                    }
                }
            }

            Write(null);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Puts back what <see cref="Hush"/> changed (HandPeg was left, closed, or is going down); nothing when it changed nothing.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (_sticky is { } sticky)
                SystemParametersInfo(SpiSetStickyKeys, sticky.Size, ref sticky, 0);
            if (_toggle is { } toggle)
                SystemParametersInfo(SpiSetToggleKeys, toggle.Size, ref toggle, 0);
            if (_filter is { } filter)
                SystemParametersInfo(SpiSetFilterKeys, filter.Size, ref filter, 0);
            if (_sticky is not null || _toggle is not null || _filter is not null)
                ThreadPool.QueueUserWorkItem(_ => Write(null));
            (_sticky, _toggle, _filter) = (null, null, null);
        }
    }

    /// <summary>
    /// Hooks the application up (App.OnStartup, before any window): every window as it loads, Hush and Restore with the
    /// application's activation, and Restore on the way out however it goes.
    /// </summary>
    public static void Follow(Application application)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window)
                Attach(window);
        }));
        ThreadPool.QueueUserWorkItem(_ => PutBackWhatACrashLeft());
        application.Activated += (_, _) => Hush();
        application.Deactivated += (_, _) => Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
    }
}
