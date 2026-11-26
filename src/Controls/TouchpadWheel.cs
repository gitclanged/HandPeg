using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// Shared precision-touchpad wheel detector (Pass 5.6.5 contract; the one copy of what MaskDrawWindow.IsTouchpadWheel decided). A wheel
/// message is a touchpad's when GetCurrentInputMessageSource reports IMDT_TOUCHPAD, when its delta is not a whole number of
/// <see cref="Notch"/> units (precision touchpads send finger distance), or when a touchpad wheel arrived at most <see cref="CarryMs"/>
/// before it (the same gesture, carried across WM_MOUSEWHEEL and WM_MOUSEHWHEEL). One instance per input surface keeps that gesture
/// memory (UI thread only); <see cref="IsTouchpad"/> and <see cref="Decode"/> are pure (golden touchpad-wheel-contract).
/// </summary>
public sealed partial class TouchpadWheel
{
    /// <summary>A touchpad wheel this recent (ms) makes the next one, on either axis, part of the same gesture.</summary>
    public const long CarryMs = 250;

    /// <summary>Wheel units per mouse-wheel notch (WHEEL_DELTA).</summary>
    public const int Notch = 120;

    /// <summary>The two wheel messages (vertical, horizontal). WPF surfaces only the first, and only after its own routing.</summary>
    public const int WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E;

    private const int MkShift = 0x0004, MkControl = 0x0008, ImdtTouchpad = 0x10;

    // Tick (ms) of the last wheel classified as a touchpad's; far past = no gesture.
    private long _touchpadAt = long.MinValue / 2;

    /// <summary>
    /// Pure classification: the message source says touchpad, a touchpad wheel came just before (same gesture), or the delta is not a
    /// multiple of <see cref="Notch"/>.
    /// </summary>
    public static bool IsTouchpad(int delta, bool sourceIsTouchpad, bool touchpadJustBefore) =>
        sourceIsTouchpad || touchpadJustBefore || delta % Notch != 0;

    /// <summary>True when the input message being processed came from a touchpad (GetCurrentInputMessageSource, Windows 8.1+).</summary>
    public static bool FromMessageSource() => GetCurrentInputMessageSource(out var source) && (source.DeviceType & ImdtTouchpad) != 0;

    /// <summary>
    /// Classifies one wheel delta at <paramref name="nowMs"/> (Environment.TickCount64 clock), remembering a touchpad hit so the rest of
    /// the gesture matches, on either axis.
    /// </summary>
    public bool Classify(int delta, bool sourceIsTouchpad, long nowMs)
    {
        var touchpad = IsTouchpad(delta, sourceIsTouchpad, nowMs - _touchpadAt <= CarryMs);
        if (touchpad)
            _touchpadAt = nowMs;
        return touchpad;
    }

    /// <summary>Classifies the wheel message being processed now (its message source, the tick clock).</summary>
    public bool ClassifyNow(int delta) => Classify(delta, FromMessageSource(), Environment.TickCount64);

    /// <summary>Forgets the current gesture (mode change, focus lost).</summary>
    public void Reset() => _touchpadAt = long.MinValue / 2;

    /// <summary>
    /// Decodes and classifies the wheel message being processed now (Alt read from the keyboard: the message does not carry it); null for
    /// any other message.
    /// </summary>
    public WheelInput? Read(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is not (WmMouseWheel or WmMouseHWheel))
            return null;
        var word = wParam.ToInt64();
        return Decode(msg, word, lParam.ToInt64(), Keyboard.Modifiers & ModifierKeys.Alt, ClassifyNow(DeltaOf(word)));
    }

    /// <summary>
    /// Pure decode of a raw wheel message: signed delta (high word of wParam), axis, Shift and Ctrl from its MK_ flags plus
    /// <paramref name="otherKeys"/> (Alt), and the pointer in screen pixels (signed words of lParam). Null for any other message.
    /// </summary>
    public static WheelInput? Decode(int msg, long wParam, long lParam, ModifierKeys otherKeys, bool touchpad)
    {
        if (msg is not (WmMouseWheel or WmMouseHWheel))
            return null;

        var keys = otherKeys & ~(ModifierKeys.Shift | ModifierKeys.Control);
        if ((wParam & MkShift) != 0)
            keys |= ModifierKeys.Shift;
        if ((wParam & MkControl) != 0)
            keys |= ModifierKeys.Control;
        var at = new Point(unchecked((short)(lParam & 0xFFFF)), unchecked((short)((lParam >> 16) & 0xFFFF)));
        return new WheelInput(DeltaOf(wParam), msg == WmMouseHWheel, keys, at, touchpad);
    }

    /// <summary>
    /// What a hook returns for a wheel message it handled: TRUE for WM_MOUSEHWHEEL (otherwise Windows re-sends it as legacy tilt-wheel
    /// clicks), 0 for WM_MOUSEWHEEL.
    /// </summary>
    public static IntPtr HandledResult(bool horizontal) => horizontal ? new IntPtr(1) : IntPtr.Zero;

    private static int DeltaOf(long wParam) => unchecked((short)((wParam >> 16) & 0xFFFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public int DeviceType, OriginId;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCurrentInputMessageSource(out InputMessageSource source);
}

/// <summary>
/// One decoded wheel message (Pass 5.6.5 contract): <paramref name="Delta"/> in 120 units per notch (positive = away from the user, or
/// right for <paramref name="Horizontal"/>), modifiers (a touchpad pinch arrives as Ctrl), the pointer in screen pixels, and whether it
/// belongs to a touchpad gesture (<see cref="TouchpadWheel"/>).
/// </summary>
public readonly record struct WheelInput(int Delta, bool Horizontal, ModifierKeys Keys, Point ScreenPixels, bool Touchpad)
{
    /// <summary>The delta in notches (fractional for touchpads).</summary>
    public double Notches => Delta / (double)TouchpadWheel.Notch;
}

/// <summary>
/// <see cref="VideoSurface.PlayerWheel"/> arguments: the decoded wheel and the window that received it (the native video HWND or the
/// overlay). Set <see cref="Handled"/> to consume the message before WPF and the window's other hooks see it.
/// </summary>
public sealed class PlayerWheelEventArgs(WheelInput input, IntPtr window) : EventArgs
{
    public WheelInput Input { get; } = input;

    public IntPtr Window { get; } = window;

    public bool Handled { get; set; }
}
