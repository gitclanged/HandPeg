using System.Globalization;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Input a two-click colour pick reacts to while armed.</summary>
public enum PickInput { LeftClick, RightClick, Escape }

/// <summary>What an input did to an armed pick.</summary>
public enum PickOutcome { Ignored, Picked, Cancelled }

/// <summary>
/// Color Match's two-click colour pick (Pass 5.6.4B), shared by Pick Target (on the player) and Reference (anywhere on screen,
/// ScreenEyedropper): one click on the button arms it; the pointer then moves freely, no button held, with a topmost zoom loupe and a live
/// RGB/Luma readout (<see cref="Readout"/>) following it; one left click samples; Esc or a right click cancels, as does the button clicked
/// again while armed. Never a click-and-hold drag. Pure state (no Win32, no WPF): the pickers feed it samples and inputs; golden-tested.
/// </summary>
public sealed class TwoClickPick
{
    /// <summary>True from the arming click until a pick or a cancel.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>The latest sample under the pointer while armed (null before the first, and after the pick ends).</summary>
    public (double R, double G, double B)? Last { get; private set; }

    /// <summary>Raised when the pick arms (true) or ends, picked or cancelled (false).</summary>
    public event Action<bool>? ArmedChanged;

    /// <summary>Raised per sample while armed (drives the live swatch and readout).</summary>
    public event Action<(double R, double G, double B)>? Sampling;

    /// <summary>Raised with the sampled colour on the left click.</summary>
    public event Action<(double R, double G, double B)>? Picked;

    /// <summary>The button's click: arms an idle pick (true), or cancels an armed one (false).</summary>
    public bool Toggle()
    {
        if (IsArmed)
        {
            Cancel();
            return false;
        }

        return Arm();
    }

    /// <summary>Arms the pick; false when it already is.</summary>
    public bool Arm()
    {
        if (IsArmed)
            return false;
        (IsArmed, Last) = (true, null);
        ArmedChanged?.Invoke(true);
        return true;
    }

    /// <summary>The colour under the pointer moved (null: nothing readable there). Ignored unless armed.</summary>
    public void Move((double R, double G, double B)? colour)
    {
        if (!IsArmed)
            return;
        Last = colour;
        if (colour is { } sample)
            Sampling?.Invoke(sample);
    }

    /// <summary>
    /// A click or key while armed: a left click with a sample picks it; a left click over nothing readable is ignored (still armed); Esc
    /// or a right click cancels.
    /// </summary>
    public PickOutcome Input(PickInput input)
    {
        if (!IsArmed)
            return PickOutcome.Ignored;
        if (input != PickInput.LeftClick)
        {
            Cancel();
            return PickOutcome.Cancelled;
        }

        if (Last is not { } colour)
            return PickOutcome.Ignored;
        (IsArmed, Last) = (false, null);
        ArmedChanged?.Invoke(false);
        Picked?.Invoke(colour);
        return PickOutcome.Picked;
    }

    /// <summary>Ends an armed pick without a colour.</summary>
    public void Cancel()
    {
        if (!IsArmed)
            return;
        (IsArmed, Last) = (false, null);
        ArmedChanged?.Invoke(false);
    }

    /// <summary>The loupe's live readout of an sRGB colour (0..1): 8-bit R, G and B, and BT.709 luma ("R 230  G 180  B 140  Y 190").</summary>
    public static string Readout((double R, double G, double B) colour)
    {
        var (r, g, b) = (Byte(colour.R), Byte(colour.G), Byte(colour.B));
        return string.Create(CultureInfo.InvariantCulture, $"R {r}  G {g}  B {b}  Y {VideoScopes.Luma(r, g, b)}");
    }

    /// <summary>The readout on two compact lines ("R 230  G 180  B 140" over "Y 190") so a loupe never clips it.</summary>
    public static string ReadoutLines((double R, double G, double B) colour)
    {
        var line = Readout(colour);
        var luma = line.LastIndexOf("  Y ", StringComparison.Ordinal);
        return line[..luma] + "\n" + line[(luma + 2)..];
    }

    private static int Byte(double c) => (int)Math.Round(Math.Clamp(double.IsFinite(c) ? c : 0, 0, 1) * 255);
}

/// <summary>
/// A pixel sampled on the player for the curves (Pass 5.6.4B; Ctrl + Hover shows tonal pips, Ctrl + Click drops a point, Ctrl + Shift +
/// Click drops matched points on all four curves): its R, G and B as they enter the curves (0..1, display-referred) and BT.709 luma.
/// </summary>
public readonly record struct TonalSample(double R, double G, double B)
{
    /// <summary>BT.709 luma (0..1).</summary>
    public double Luma => Math.Clamp(VideoScopes.LumaRed * R + VideoScopes.LumaGreen * G + VideoScopes.LumaBlue * B, 0, 1);

    /// <summary>Where the sample sits on a curve's input axis: the luma on Master (0), its own value on Red (1), Green (2) and Blue (3).</summary>
    public double ValueFor(int channel) => channel switch { 1 => R, 2 => G, 3 => B, _ => Luma };

    /// <summary>A colour held to 0..1 (non-finite channels read 0).</summary>
    public static TonalSample Of((double R, double G, double B) colour) => new(Unit(colour.R), Unit(colour.G), Unit(colour.B));

    /// <summary>
    /// The 3 x 3 mean around a frame point (<paramref name="focalX"/>, <paramref name="focalY"/> in 0..1) of a BGRA8 frame; null outside
    /// the frame or for a frame too small for its buffer.
    /// </summary>
    public static TonalSample? At(ReadOnlySpan<byte> bgra, int width, int height, double focalX, double focalY)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4 || !(focalX is >= 0 and <= 1) || !(focalY is >= 0 and <= 1))
            return null;
        var (cx, cy) = (Math.Min((int)(focalX * width), width - 1), Math.Min((int)(focalY * height), height - 1));
        var (r, g, b, n) = (0.0, 0.0, 0.0, 0);
        for (var y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, height - 1); y++)
        {
            for (var x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, width - 1); x++)
            {
                var i = (y * width + x) * 4;
                (b, g, r, n) = (b + bgra[i], g + bgra[i + 1], r + bgra[i + 2], n + 1);
            }
        }

        return new TonalSample(r / n / 255, g / n / 255, b / n / 255);
    }

    private static double Unit(double c) => double.IsFinite(c) ? Math.Clamp(c, 0, 1) : 0;
}
