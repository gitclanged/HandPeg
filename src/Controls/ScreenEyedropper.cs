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
/// Color Match's Reference eyedropper (Pass 5.6A): press it and drag anywhere, on any monitor (the mouse captured, so the drag reaches
/// past HandPeg's windows), and the colour under the pointer is read from the screen about 60 times a second; let go and that colour is
/// the reference (<see cref="Picked"/>); Esc, or losing the capture, or (5.6A review) letting go on the button itself, cancels. Physical
/// pixels only: the pointer's place is GetCursorPos's
/// and the 15 x 15 patch about it is copied from the screen with GDI (BitBlt into one DIB made for the drag), never through WPF's DIPs
/// (HandPeg sets its own root DPI) and whatever each monitor's scale. The colour is the patch's bilateral mean (ColorMatch.BilateralMean).
/// A loupe beside the pointer (a small window of its own, ScreenEyedropper.Loupe.cs) magnifies the patch; it is left out of every screen
/// capture (WDA_EXCLUDEFROMCAPTURE), so it never reads itself, and it sits off the pointer anyway. Nothing is allocated per pointer move
/// beyond the patch read into its buffer; the DIB, its DC and the screen's DC are let go when the drag ends. On an HDR desktop GDI reads
/// the SDR-mapped picture (highlights clipped); a protected window reads black.
/// </summary>
public sealed partial class ScreenEyedropper : Button
{
    /// <summary>The patch's side (2 x ColorMatch.PatchRadius + 1) and the least time between two reads, ms (about 60 Hz).</summary>
    public const int PatchSide = 2 * ColorMatch.PatchRadius + 1, ReadEveryMs = 15;

    private readonly byte[] _patch = new byte[PatchSide * PatchSide * 4];
    private nint _screenDc, _memoryDc, _dib, _oldBitmap, _bits;
    private long _lastRead;
    private bool _dragging;
    private Loupe? _loupe;

    public ScreenEyedropper()
    {
        // A button's look (HandPeg's Button style), its press and drag its own (no Click).
        SetResourceReference(StyleProperty, typeof(Button));
        Cursor = Cursors.Cross;
    }

    /// <summary>The colour read as the drag goes (the swatch follows it).</summary>
    public event Action<(double R, double G, double B)>? Sampling;

    /// <summary>The colour under the pointer when the drag ended: the reference.</summary>
    public event Action<(double R, double G, double B)>? Picked;

    /// <summary>The drag began (true) or ended, picked or cancelled (false).</summary>
    public event Action<bool>? DraggingChanged;

    /// <summary>Whether a drag is reading the screen.</summary>
    public bool IsDragging => _dragging;

    /// <summary>The last colour read (null before the first read of a drag).</summary>
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

        // 5.6A review: a plain click, or a release back on the button, would read the button's own face: cancelled, no reference.
        if (ReleasedOnItself(RenderSize, e.GetPosition(this)))
        {
            End(picked: false);
            return;
        }

        Read(force: true);
        End(picked: true);
    }

    /// <summary>Whether a release at <paramref name="at"/> (in the button's own DIPs) is on the button itself (5.6A review; pure: the goldens check it).</summary>
    internal static bool ReleasedOnItself(Size size, Point at) => new Rect(size).Contains(at);

    /// <summary>Cancels a drag under way (5.6A review: the window's Esc, which takes the key before the 3D view or Interactive Crop do).</summary>
    public void Cancel() => End(picked: false);

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Taken away (another window, Alt+Tab) before the release: cancelled.
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

    // The keyboard's Enter or Space on it reads nothing (the eyedropper is a drag).
    protected override void OnClick()
    {
    }

    // The screen's DC, a DC with the patch's DIB selected into it, for the drag.
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

    // One read of the patch under the pointer (at most every ReadEveryMs unless forced), its colour, the loupe beside it.
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

    // The DIB, its DC and the screen's DC let go (Law 2: nothing of the drag is kept).
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
