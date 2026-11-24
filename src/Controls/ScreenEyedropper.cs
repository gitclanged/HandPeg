using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.Native;
using static HandPegApp.Native.User32;
using static HandPegApp.Native.Gdi32;

namespace HandPegApp;

/// <summary>
/// Color Match Reference eyedropper, a two-click pick (<see cref="TwoClickPick"/>, Pass 5.6.4B): one click on the button arms it; the
/// pointer then moves freely over any monitor, no button held, with the topmost loupe and its live RGB/Luma readout following it (the
/// screen under the pointer is read at ~60 Hz); one left click anywhere samples the reference (<see cref="Picked"/>); Esc, a right click,
/// the button clicked again or capture loss (Alt+Tab) cancels. Pressing on the button, dragging off and releasing still picks where it is
/// released (a shortcut, never required). While armed, a low-level mouse hook takes the next left or right click before any window does
/// (so a click over another application samples instead of activating it) and the button keeps mouse capture (the window's Esc finds it).
/// Physical pixels throughout: the position comes from GetCursorPos (or the hook) and the 15 x 15 patch is copied via GDI (BitBlt into one
/// per-pick DIB), bypassing WPF DIPs (HandPeg sets its own root DPI) and per-monitor scaling. The colour is the patch's bilateral mean
/// (ColorMatch.BilateralMean). The loupe (ScreenEyedropper.Loupe.cs) is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE) so it never
/// samples itself. No per-read allocations beyond the patch buffer copy; DIB, DCs, timer and hook are released when the pick ends (Law 2).
/// On HDR desktops GDI returns the SDR-mapped image (highlights clipped); protected windows read black.
/// </summary>
public sealed partial class ScreenEyedropper : Button
{
    /// <summary>Patch side length (2 x ColorMatch.PatchRadius + 1) and minimum interval between reads in ms (~60 Hz).</summary>
    public const int PatchSide = 2 * ColorMatch.PatchRadius + 1, ReadEveryMs = 15;

    // The low-level mouse hook while a two-click pick is armed (one at a time, UI thread only), its owner, and the button-ups still owed
    // a swallow (the down was taken, so its up never reaches a window either).
    private static nint _hook;
    private static ScreenEyedropper? _hookOwner;
    private static bool _swallowLeftUp, _swallowRightUp;
    private static DispatcherTimer? _owedUpTimeout;

    // The longest the hook stays installed after its pick ended, waiting for an owed button-up (ms): one never delivered (capture or
    // desktop switch) cannot keep a global hook installed (Law 2).
    private const int OwedUpTimeoutMs = 2000;

    private readonly byte[] _patch = new byte[PatchSide * PatchSide * 4];
    private readonly TwoClickPick _pick = new();
    private nint _screenDc, _memoryDc, _dib, _oldBitmap, _bits;
    private long _lastRead;
    private bool _clickArmed;
    private DispatcherTimer? _poll;
    private Loupe? _loupe;

    public ScreenEyedropper()
    {
        // Standard Button style; presses and clicks are handled here (a keyboard click arms the pick).
        SetResourceReference(StyleProperty, typeof(Button));
        Cursor = Cursors.Cross;
        _pick.ArmedChanged += armed =>
        {
            if (!armed)
                Finish();
            DraggingChanged?.Invoke(armed);
        };
        _pick.Sampling += colour => Sampling?.Invoke(colour);
        _pick.Picked += colour => Picked?.Invoke(colour);

        // Moved off screen or closed mid-pick: nothing stays armed, hooked or held (an owed button-up included).
        Unloaded += (_, _) =>
        {
            _pick.Cancel();
            DropHook();
        };
    }

    /// <summary>Raised per sample while the pick is armed (drives the live swatch).</summary>
    public event Action<(double R, double G, double B)>? Sampling;

    /// <summary>Raised with the colour sampled by the left click: the reference.</summary>
    public event Action<(double R, double G, double B)>? Picked;

    /// <summary>Raised when the pick arms (true) or ends, picked or cancelled (false).</summary>
    public event Action<bool>? DraggingChanged;

    /// <summary>True while the pick is armed and sampling the screen (pressed on the button, or armed by a click).</summary>
    public bool IsDragging => _pick.IsArmed;

    /// <summary>Most recent sample (null before the first sample of a pick).</summary>
    public (double R, double G, double B)? Last => _pick.Last;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;

        // Armed without the hook (it could not be set): a click inside HandPeg reaches the button through its capture.
        if (_clickArmed)
        {
            if (GetCursorPos(out var at))
                ClickAt(at);
            return;
        }

        if (IsDragging || !IsEnabled)
            return;
        Focus();
        if (!Begin())
            return;
        if (!CaptureMouse())
        {
            _pick.Cancel();
            return;
        }

        _ = Read(force: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDragging)
            _ = Read(force: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsDragging || _clickArmed)
            return;

        // A click (released on the button itself, which is never the reference): arm the two-click pick; the next left click anywhere
        // samples. Without the hook the pick still runs inside HandPeg's windows, through the capture.
        if (ReleasedOnItself(RenderSize, e.GetPosition(this)))
        {
            _clickArmed = true;
            _ = Hook();
            return;
        }

        // Pressed on the button and released off it: the colour there is the reference. Nothing readable there cancels (a stale hover
        // sample is never committed), so capture and the GDI objects are released.
        if (!Read(force: true) || _pick.Input(PickInput.LeftClick) == PickOutcome.Ignored)
            _pick.Cancel();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (IsDragging)
        {
            e.Handled = true;
            _pick.Input(PickInput.RightClick);
            return;
        }

        base.OnMouseRightButtonDown(e);
    }

    /// <summary>True when a release at <paramref name="at"/> (button-local DIPs) is over the button. Pure; golden-tested.</summary>
    internal static bool ReleasedOnItself(Size size, Point at) => new Rect(size).Contains(at);

    /// <summary>Cancels an armed pick (the window's Esc handler, before the 3D view or Interactive Crop consume the key); idle, nothing.</summary>
    public void Cancel() => _pick.Input(PickInput.Escape);

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture lost while armed (another window, Alt+Tab): cancel, the hook with it (no owed button-up is waited for). The pick's own
        // end releases capture after it has disarmed, so a sampling click's button-up is still swallowed then.
        if (IsDragging)
        {
            _pick.Cancel();
            DropHook();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsDragging && e.Key == Key.Escape)
        {
            e.Handled = true;
            _pick.Input(PickInput.Escape);
            return;
        }

        base.OnKeyDown(e);
    }

    // Keyboard Enter or Space (or automation Invoke) arms the pick at the pointer; the next left click samples.
    protected override void OnClick()
    {
        if (IsDragging || !IsEnabled || !Begin())
            return;
        if (!CaptureMouse())
        {
            _pick.Cancel();
            return;
        }

        _clickArmed = true;
        _ = Hook();
        _ = Read(force: true);
    }

    // Acquires the screen DC and a memory DC with the patch DIB selected and starts the pointer poll, for the duration of the pick.
    private bool Begin()
    {
        _screenDc = GetDC(0);
        if (_screenDc == 0)
            return false;

        _memoryDc = CreateCompatibleDC(_screenDc);
        var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = PatchSide, Height = -PatchSide, Planes = 1, BitCount = 32 };
        _dib = _memoryDc == 0 ? 0 : CreateDIBSection(_memoryDc, ref info, 0, out _bits, 0, 0);
        if (_dib == 0 || _bits == 0)
        {
            Release();
            return false;
        }

        _oldBitmap = SelectObject(_memoryDc, _dib);
        (_lastRead, _clickArmed) = (0, false);
        _poll ??= NewPoll();
        _poll.Start();
        _pick.Arm();
        return true;
    }

    // The ~60 Hz pointer poll: the loupe and readout follow the pointer over any window, button held or not; Esc pressed while another
    // application has the keyboard cancels too.
    private DispatcherTimer NewPoll()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ReadEveryMs) };
        timer.Tick += (_, _) =>
        {
            if (!IsDragging)
            {
                timer.Stop();
                return;
            }

            if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
            {
                _pick.Input(PickInput.Escape);
                return;
            }

            _ = Read(force: false);
        };
        return timer;
    }

    // Samples the patch at the pointer (or at a click's point), throttled to ReadEveryMs unless forced, and updates the pick and the loupe.
    // True only when a fresh sample was taken (false when throttled, or the pointer, BitBlt or patch could not be read).
    private bool Read(bool force, NativePoint? point = null)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastRead, now).TotalMilliseconds < ReadEveryMs)
            return false;
        _lastRead = now;
        NativePoint at;
        if (point is { } given)
            at = given;
        else if (!GetCursorPos(out at))
            return false;
        if (_memoryDc == 0 || !BitBlt(_memoryDc, 0, 0, PatchSide, PatchSide, _screenDc, at.X - ColorMatch.PatchRadius, at.Y - ColorMatch.PatchRadius, SrcCopy))
            return false;

        _ = GdiFlush();
        Marshal.Copy(_bits, _patch, 0, _patch.Length);
        if (ColorMatch.BilateralMean(_patch, PatchSide, PatchSide, ColorMatch.PatchRadius, ColorMatch.PatchRadius) is not { } colour)
            return false;

        _pick.Move(colour);
        _loupe ??= new Loupe();
        _loupe.Present(_patch, colour, at.X, at.Y);
        return true;
    }

    // The armed pick's left click at a physical screen point: over the button itself it cancels (clicked again); anywhere else it samples
    // there. Nothing readable there keeps the pick armed.
    private void ClickAt(NativePoint at)
    {
        if (!IsDragging)
            return;
        if (OverItself(at))
        {
            _pick.Cancel();
            return;
        }

        // Only a fresh read at the click's point is committed: a failed one stays armed rather than taking the last hover sample.
        if (Read(force: true, at))
            _ = _pick.Input(PickInput.LeftClick);
    }

    private bool OverItself(NativePoint at) =>
        IsVisible && PresentationSource.FromVisual(this) is not null && ReleasedOnItself(RenderSize, PointFromScreen(new Point(at.X, at.Y)));

    // The pick ended (picked or cancelled): poll, capture, loupe and GDI objects released; the hook removed once no button-up is owed.
    private void Finish()
    {
        _poll?.Stop();
        _clickArmed = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _loupe?.Close();
        _loupe = null;
        Release();
        UnhookWhenDone();
    }

    // Releases the DIB, memory DC and screen DC (Law 2: no pick resources retained).
    private void Release()
    {
        if (_memoryDc != 0 && _oldBitmap != 0)
            _ = SelectObject(_memoryDc, _oldBitmap);
        if (_dib != 0)
            _ = DeleteObject(_dib);
        if (_memoryDc != 0)
            _ = DeleteDC(_memoryDc);
        if (_screenDc != 0)
            _ = ReleaseDC(0, _screenDc);
        (_screenDc, _memoryDc, _dib, _oldBitmap, _bits) = (0, 0, 0, 0, 0);
    }

    // ----- Low-level mouse hook (armed two-click pick only) -----

    // Sets the hook for this button's armed pick; false when Windows refuses it (the pick then works inside HandPeg's windows only).
    private bool Hook()
    {
        Unhook();
        unsafe
        {
            delegate* unmanaged<int, nint, nint, nint> procedure = &LowLevelMouse;
            _hook = SetWindowsHookEx(WhMouseLl, (nint)procedure, Kernel32.GetModuleHandle(null), 0);
        }

        if (_hook == 0)
            return false;
        _hookOwner = this;
        return true;
    }

    private static void Unhook()
    {
        _owedUpTimeout?.Stop();
        if (_hook != 0)
            _ = UnhookWindowsHookEx(_hook);
        (_hook, _hookOwner, _swallowLeftUp, _swallowRightUp) = (0, null, false, false);
    }

    // This button's hook removed now, whatever is still owed (capture loss, unload).
    private void DropHook()
    {
        if (ReferenceEquals(_hookOwner, this))
            Unhook();
    }

    // Removes this button's hook once its pick has ended and the taken click's button-up has been swallowed too; while one is still owed,
    // it waits at most OwedUpTimeoutMs more (an up never delivered must not keep the global hook installed: Law 2).
    private void UnhookWhenDone()
    {
        if (!ReferenceEquals(_hookOwner, this) || IsDragging)
            return;
        if (!_swallowLeftUp && !_swallowRightUp)
        {
            Unhook();
            return;
        }

        _owedUpTimeout ??= NewOwedUpTimeout();
        _owedUpTimeout.Stop();
        _owedUpTimeout.Start();
    }

    private static DispatcherTimer NewOwedUpTimeout()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(OwedUpTimeoutMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_hookOwner is { IsDragging: false })
                Unhook();
        };
        return timer;
    }

    // The hook procedure, on the UI thread (the installing thread's message loop): decides at once whether the system drops a click; the
    // work runs after it on the dispatcher. Never throws across the native boundary.
    [UnmanagedCallersOnly]
    private static unsafe nint LowLevelMouse(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && _hookOwner is { } owner && owner.Takes((int)message, *(NativePoint*)data))
                return 1;
        }
        catch (Exception)
        {
            // A failure here must never take the process down; the click passes on.
        }

        return CallNextHookEx(0, code, message, data);
    }

    // Left down while armed: swallowed, sampled at its point; right down: swallowed, cancels; each one's up swallowed after it however long
    // the button is held (its down never reached a window). The hook's life after the pick is bounded instead (UnhookWhenDone).
    private bool Takes(int message, NativePoint at)
    {
        switch (message)
        {
            case WmLButtonDown when IsDragging:
                _swallowLeftUp = true;
                _ = Dispatcher.InvokeAsync(() => ClickAt(at), DispatcherPriority.Input);
                return true;
            case WmRButtonDown when IsDragging:
                _swallowRightUp = true;
                _ = Dispatcher.InvokeAsync(() => _pick.Input(PickInput.RightClick), DispatcherPriority.Input);
                return true;
            case WmLButtonUp when _swallowLeftUp:
            case WmRButtonUp when _swallowRightUp:
                if (message == WmLButtonUp)
                    _swallowLeftUp = false;
                else
                    _swallowRightUp = false;
                _ = Dispatcher.InvokeAsync(UnhookWhenDone, DispatcherPriority.Input);
                return true;
            default:
                return false;
        }
    }

    // ----- Win32 -----

    private const int SrcCopy = 0x00CC0020, WhMouseLl = 14, VkEscape = 0x1B;
    private const int WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookEx(int hook, nint procedure, nint module, uint thread);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
