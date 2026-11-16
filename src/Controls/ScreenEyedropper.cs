using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Services;
using HandPegApp.Native;
using static HandPegApp.Native.User32;
using static HandPegApp.Native.Gdi32;

namespace HandPegApp;

/// <summary>
/// Color Match Reference eyedropper: press and drag anywhere on any monitor (mouse capture extends the drag beyond HandPeg's windows);
/// the colour under the pointer is sampled from the screen at ~60 Hz, and release sets the reference (<see cref="Picked"/>). Esc,
/// capture loss, or release over the button itself cancels. Physical pixels throughout: the position comes from GetCursorPos and the
/// 15 x 15 patch is copied via GDI (BitBlt into one per-drag DIB), bypassing WPF DIPs (HandPeg sets its own root DPI) and per-monitor
/// scaling. The colour is the patch's bilateral mean (ColorMatch.BilateralMean). A loupe window beside the pointer
/// (ScreenEyedropper.Loupe.cs) magnifies the patch and is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE) so it never samples
/// itself. No per-move allocations beyond the patch buffer copy; DIB, memory DC and screen DC are released when the drag ends. On HDR
/// desktops GDI returns the SDR-mapped image (highlights clipped); protected windows read black.
/// </summary>
public sealed partial class ScreenEyedropper : Button
{
    /// <summary>Patch side length (2 x ColorMatch.PatchRadius + 1) and minimum interval between reads in ms (~60 Hz).</summary>
    public const int PatchSide = 2 * ColorMatch.PatchRadius + 1, ReadEveryMs = 15;

    private readonly byte[] _patch = new byte[PatchSide * PatchSide * 4];
    private nint _screenDc, _memoryDc, _dib, _oldBitmap, _bits;
    private long _lastRead;
    private bool _dragging;
    private Loupe? _loupe;

    public ScreenEyedropper()
    {
        // Standard Button style; press and drag are handled here (Click is a no-op).
        SetResourceReference(StyleProperty, typeof(Button));
        Cursor = Cursors.Cross;
    }

    /// <summary>Raised per sample during the drag (drives the live swatch).</summary>
    public event Action<(double R, double G, double B)>? Sampling;

    /// <summary>Raised with the colour under the pointer at release: the reference.</summary>
    public event Action<(double R, double G, double B)>? Picked;

    /// <summary>Raised when a drag starts (true) or ends, picked or cancelled (false).</summary>
    public event Action<bool>? DraggingChanged;

    /// <summary>True while a drag is sampling the screen.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Most recent sample (null before the first sample of a drag).</summary>
    public (double R, double G, double B)? Last { get; private set; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_dragging || !IsEnabled)
            return;
        Focus();
        if (!Begin())
            return;
        if (!CaptureMouse())
        {
            End(picked: false);
            return;
        }

        Read(force: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
            Read(force: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_dragging)
            return;

        // A plain click or release over the button would sample its own face: cancel without a reference.
        if (ReleasedOnItself(RenderSize, e.GetPosition(this)))
        {
            End(picked: false);
            return;
        }

        Read(force: true);
        End(picked: true);
    }

    /// <summary>True when a release at <paramref name="at"/> (button-local DIPs) is over the button. Pure; golden-tested.</summary>
    internal static bool ReleasedOnItself(Size size, Point at) => new Rect(size).Contains(at);

    /// <summary>Cancels an active drag (called by the window's Esc handler before the 3D view or Interactive Crop consume the key).</summary>
    public void Cancel() => End(picked: false);

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture lost before release (another window, Alt+Tab): cancel.
        if (_dragging)
            End(picked: false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            e.Handled = true;
            End(picked: false);
            return;
        }

        base.OnKeyDown(e);
    }

    // Keyboard Enter or Space does nothing: sampling requires a drag.
    protected override void OnClick()
    {
    }

    // Acquires the screen DC and a memory DC with the patch DIB selected, for the duration of the drag.
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
        (_dragging, Last, _lastRead) = (true, null, 0);
        DraggingChanged?.Invoke(true);
        return true;
    }

    // Samples the patch under the pointer (throttled to ReadEveryMs unless forced), computes its colour and updates the loupe.
    private void Read(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastRead, now).TotalMilliseconds < ReadEveryMs)
            return;
        _lastRead = now;
        if (!GetCursorPos(out var at) || !BitBlt(_memoryDc, 0, 0, PatchSide, PatchSide, _screenDc, at.X - ColorMatch.PatchRadius, at.Y - ColorMatch.PatchRadius, SrcCopy))
            return;

        _ = GdiFlush();
        Marshal.Copy(_bits, _patch, 0, _patch.Length);
        if (ColorMatch.BilateralMean(_patch, PatchSide, PatchSide, ColorMatch.PatchRadius, ColorMatch.PatchRadius) is not { } colour)
            return;

        Last = colour;
        Sampling?.Invoke(colour);
        _loupe ??= new Loupe();
        _loupe.Present(_patch, colour, at.X, at.Y);
    }

    private void End(bool picked)
    {
        if (!_dragging)
            return;

        _dragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _loupe?.Close();
        _loupe = null;
        Release();
        DraggingChanged?.Invoke(false);
        if (picked && Last is { } colour)
            Picked?.Invoke(colour);
    }

    // Releases the DIB, memory DC and screen DC (Law 2: no drag resources retained).
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

    // ----- Win32 -----

    private const int SrcCopy = 0x00CC0020;

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
}
