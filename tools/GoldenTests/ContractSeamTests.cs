using System.Text.Json;
using System.Windows.Input;
using HandPegApp;
using HandPegApp.Models;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.6.5 wave-0 contracts the lanes build on: the shared touchpad wheel detector and the player's wheel seam; the Layer model's
/// speed ramp, hand-drawn ducking curve, Cutout tight bounds and auto-centred anchor, saved and carried by every timing transform; the
/// speed ramp's sequence-to-file time mapping; and the shell seams (--dpi-matrix, the 4th caption button, the Color Match host, the
/// EasyCut and spring-zoom keys). Every line says what it proved.
/// </summary>
public static class ContractSeamTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("touchpad-wheel-contract", "# One touchpad wheel rule (TouchpadWheel): IMDT_TOUCHPAD, a part notch or a 250 ms gesture carry-over on either axis; raw wheel messages decoded; the player raises both windows' wheels.", TouchpadWheelRules);
        yield return Case("layer-contract-fields", "# The speed ramp, the hand-drawn ducking curve, Cutout tight bounds and the auto-centred anchor: held on read, saved only when set, carried by speed change and trims.", LayerFields);
        yield return Case("speed-ramp-mapping", "# A speed ramp maps sequence time to file time: the integral of its eased speed, exact for linear and hold, inverted to the clip moment.", SpeedRampMapping);
        yield return Case("shell-seams-contract", "# Shell seams: --dpi-matrix runs first, the 4th caption button opens a side window, the Color Match host tops the player, EasyCut digits and Z reach their seams.", ShellSeams);
    }

    // ----- touchpad-wheel-contract -----

    private static void TouchpadWheelRules(Action<bool, string> expect)
    {
        expect(!TouchpadWheel.IsTouchpad(120, false, false) && !TouchpadWheel.IsTouchpad(-240, false, false),
            "whole notches with no touchpad source and no gesture before are a mouse wheel's");
        expect(TouchpadWheel.IsTouchpad(120, true, false) && TouchpadWheel.IsTouchpad(37, false, false) && TouchpadWheel.IsTouchpad(-8, false, false)
               && TouchpadWheel.IsTouchpad(120, false, true),
            "IMDT_TOUCHPAD, a part notch, or a touchpad wheel just before make a touchpad's");
        var deltas = new[] { 120, -120, 240, 37, -8, 0 };
        expect(TouchpadWheel.CarryMs == 250 && MaskDrawWindow.TouchpadStreamMs == TouchpadWheel.CarryMs
               && deltas.All(d => new[] { false, true }.All(source => new[] { false, true }.All(before =>
                   MaskDrawWindow.IsTouchpadWheel(d, source, before) == TouchpadWheel.IsTouchpad(d, source, before)))),
            "the Cutout Studio decides with the same rule and the same 250 ms carry-over");

        var wheel = new TouchpadWheel();
        var first = wheel.Classify(37, false, 1_000);
        var carried = wheel.Classify(120, false, 1_200);
        var stillCarried = wheel.Classify(-120, false, 1_450);
        var over = wheel.Classify(120, false, 1_701);
        expect(first && carried && stillCarried && !over,
            "a whole notch within 250 ms of a touchpad wheel is the same gesture, on either axis; 251 ms after the last one it is a mouse's again");
        wheel.Classify(16, false, 5_000);
        wheel.Reset();
        expect(!wheel.Classify(120, false, 5_010), "Reset forgets the gesture");

        // Horizontal, Shift (MK_SHIFT) and Alt (from the keyboard), a negative delta and a pointer on a monitor left of the primary.
        var wParam = ((long)unchecked((ushort)(short)-60) << 16) | 0x0004;
        var lParam = ((long)unchecked((ushort)(short)300) << 16) | unchecked((ushort)(short)-1500);
        var decoded = TouchpadWheel.Decode(TouchpadWheel.WmMouseHWheel, wParam, lParam, ModifierKeys.Alt | ModifierKeys.Control, touchpad: true);
        expect(decoded is { Delta: -60, Horizontal: true, Touchpad: true } input && input.Keys == (ModifierKeys.Shift | ModifierKeys.Alt)
               && input.ScreenPixels.X == -1500 && input.ScreenPixels.Y == 300 && Math.Abs(input.Notches - -0.5) < 1e-12,
            "WM_MOUSEHWHEEL decodes to its signed delta, axis, Shift from the message (Ctrl only when the message says so), Alt from the keyboard and the signed screen point");
        var vertical = TouchpadWheel.Decode(TouchpadWheel.WmMouseWheel, (120L << 16) | 0x0008, 0, ModifierKeys.None, touchpad: false);
        expect(vertical is { Delta: 120, Horizontal: false, Keys: ModifierKeys.Control, Touchpad: false }
               && TouchpadWheel.Decode(0x0200, 0, 0, ModifierKeys.None, false) is null,
            "WM_MOUSEWHEEL with MK_CONTROL (a pinch) decodes as Ctrl; any other message is not a wheel");
        expect(TouchpadWheel.HandledResult(true) == 1 && TouchpadWheel.HandledResult(false) == 0,
            "a handled WM_MOUSEHWHEEL answers TRUE (no legacy tilt clicks), a handled WM_MOUSEWHEEL 0");

        var surface = SourceProbe.Src("Controls", "VideoSurface.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(surface, "protected override IntPtr WndProc("), "TellWheel(", "base.WndProc(")
               && SourceProbe.InOrder(SourceProbe.Member(surface, "private IntPtr OverlayMessage("), "TellWheel(", "OverlayMessageHook"),
            "the player raises PlayerWheel from the native video window and from the overlay, before their other hooks");
    }

    // ----- layer-contract-fields -----

    private static void LayerFields(Action<bool, string> expect)
    {
        var plain = new Layer().ToState();
        var json = JsonSerializer.Serialize(plain);
        expect(plain is { KeysSpeed: null, DuckOverrideKeys: null, TightBounds: null, AutoCenterAnchor: null }
               && !json.Contains("\"KeysSpeed\"", StringComparison.Ordinal) && !json.Contains("\"DuckOverrideKeys\"", StringComparison.Ordinal)
               && !json.Contains("\"TightBounds\"", StringComparison.Ordinal) && !json.Contains("\"AutoCenterAnchor\"", StringComparison.Ordinal),
            "a layer without them saves none of the four (older projects read unchanged)");

        var layer = new Layer { Kind = LayerKind.VideoFile, Duration = 6 };
        layer.SetSpeedKeys([new Keyframe(4, 40), new Keyframe(0, 1, EasingType.Linear), new Keyframe(2, 0.01), new Keyframe(double.NaN, 2)]);
        expect(layer is { HasSpeedRamp: true, SpeedKeys: [{ Time: 0, Value: 1 }, { Time: 2, Value: Layer.SlowestSpeed }, { Time: 4, Value: Layer.FastestSpeed }] },
            "speed keys are kept ascending, held to 0.1x..16x, and a key that is not a number is dropped");
        var raised = new List<string?>();
        layer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        layer.SetSpeedKeys(layer.SpeedKeys.ToList());
        expect(raised.Count == 0, "setting the same speed keys again raises nothing");

        layer.SetDuckOverrideKeys([new Keyframe(3, -55), new Keyframe(1, -12), new Keyframe(2, 5)]);
        expect(layer is { HasDuckOverrides: true, DuckOverrideKeys: [{ Time: 1, Value: -12 }, { Time: 2, Value: 0 }, { Time: 3, Value: Layer.DuckOverrideFloorDb }] }
               && raised.Contains(nameof(Layer.DuckOverrideKeys)) && raised.Contains(nameof(Layer.HasDuckOverrides)),
            "ducking keys are kept ascending and held to -40..0 dB, announced as a look change");
        expect(layer.DuckOverrideAt(0.5) is null && layer.DuckOverrideAt(1) == -12 && layer.DuckOverrideAt(3) == Layer.DuckOverrideFloorDb && layer.DuckOverrideAt(3.5) is null,
            "the drawn curve rules from its first key to its last; the automatic envelope outside");

        layer.TightBounds = SourceBounds.From([0.3, 0.5, 0.1, 0.2]);
        layer.AutoCenterAnchor = true;
        var saved = layer.ToState();
        var read = Layer.FromState(JsonSerializer.Deserialize<LayerState>(JsonSerializer.Serialize(saved))!, 1920, 1080, 1920, 1080);
        expect(saved.TightBounds is [0.1, 0.2, 0.3, 0.5] && saved.AutoCenterAnchor == true
               && read.SpeedKeys.SequenceEqual(layer.SpeedKeys) && read.DuckOverrideKeys.SequenceEqual(layer.DuckOverrideKeys)
               && read.TightBounds == new SourceBounds(0.1, 0.2, 0.3, 0.5) && read.AutoCenterAnchor,
            "all four survive a save and a read (bounds saved ordered as [UMin, VMin, UMax, VMax])");
        expect(SourceBounds.From([0.4, 0.4, 0.4, 0.9]) is null && SourceBounds.From([double.NaN, 0, 1, 1]) is null && SourceBounds.From([0, 0, 1]) is null
               && SourceBounds.From(null) is null && SourceBounds.From([-1, -1, 2, 2]) == new SourceBounds(0, 0, 1, 1),
            "saved bounds with no area, a number missing or not a number read as unknown; out-of-frame values are held to it");

        var look = new LayerState { TightBounds = [0.2, 0.2, 0.4, 0.6], AutoCenterAnchor = true, AnchorX = 0.3, AnchorY = 0.4 };
        var (looked, placed) = (new Layer(), new Layer());
        looked.ApplyLook(look);
        placed.ApplyPlace(look);
        expect(looked.TightBounds is { } bounds && bounds.UMin == 0.2 && bounds.VMax == 0.6 && looked.AutoCenterAnchor && placed is { AutoCenterAnchor: true, TightBounds: null },
            "a look brings the bounds with its mask and the auto anchor with its anchor; a placement brings only the anchor's");

        // Source rect: the frame's top-left quarter; crop: its left half's first tenth. The box shows u 0.05..0.5, v 0..0.5.
        var cutout = new Layer { SourceX = 0, SourceY = 0, SourceWidth = 0.5, SourceHeight = 0.5, CropLeft = 0.1, TightBounds = new SourceBounds(0.1, 0.1, 0.2, 0.3) };
        var box = cutout.TightBoxBounds();
        var anchor = cutout.TightAnchor();
        expect(box is { } b && Near(b.UMin, 0.05 / 0.45) && Near(b.VMin, 0.2) && Near(b.UMax, 0.15 / 0.45) && Near(b.VMax, 0.6)
               && anchor is { } a && Near(a.X, 0.1 / 0.45) && Near(a.Y, 0.4),
            "the bounds land on the box as fractions of what it shows (source rect less crop), and the auto anchor is their centre");
        expect(new Layer { TightBounds = new SourceBounds(0.6, 0.6, 0.9, 0.9), SourceWidth = 0.5, SourceHeight = 0.5 }.TightAnchor() is null
               && new Layer().TightAnchor() is null,
            "bounds outside what the box shows, or none, give no auto anchor");

        expect(LayerState.MotionChannels.Any(c => c.Name == nameof(LayerState.KeysSpeed)) && LayerState.MotionChannels.Any(c => c.Name == nameof(LayerState.DuckOverrideKeys)),
            "the speed ramp and the ducking curve are motion channels: split, join, trim and speed change carry them");
        layer.SetSpeed(2);
        expect(layer.SpeedKeys is [{ Time: 0 }, { Time: 1 }, { Time: 2 }] && layer.DuckOverrideKeys is [{ Time: 0.5 }, { Time: 1 }, { Time: 1.5 }],
            "doubling the constant speed halves both curves' times, as it does every keyframe's");
        layer.ShiftKeys(-0.5);
        expect(layer.SpeedKeys is [{ Time: -0.5 }, { Time: 0.5 }, { Time: 1.5 }] && layer.DuckOverrideKeys is [{ Time: 0 }, { Time: 0.5 }, { Time: 1 }],
            "a head trim shifts both curves with what the clip shows");
    }

    // ----- speed-ramp-mapping -----

    private static void SpeedRampMapping(Action<bool, string> expect)
    {
        expect(Near(SpeedRamp.MediaSeconds([], 2, 3), 6) && Near(SpeedRamp.ClipSeconds([], 2, 6), 3) && Near(SpeedRamp.MediaSeconds([], 99, 1), Layer.FastestSpeed),
            "without keys the file runs at the constant speed (held to 16x)");

        List<Keyframe> linear = [new(0, 1, EasingType.Linear), new(2, 3, EasingType.Linear)];
        expect(Near(SpeedRamp.MediaSeconds(linear, 1, 2), 4) && Near(SpeedRamp.MediaSeconds(linear, 1, 3), 7) && Near(SpeedRamp.MediaSeconds(linear, 1, -1), -1)
               && Near(SpeedRamp.MediaSeconds(linear, 1, 1), 1.5),
            "a linear 1x to 3x ramp over 2 s plays 4 s of file, then 3x held after it and 1x held before it");

        List<Keyframe> hold = [new(0, 2, EasingType.Hold), new(1, 4)];
        expect(Near(SpeedRamp.MediaSeconds(hold, 1, 1), 2) && Near(SpeedRamp.MediaSeconds(hold, 1, 1.5), 4),
            "a hold key keeps its speed until the next key");

        List<Keyframe> eased = [new(0, 0.25), new(1.5, 4, EasingType.EaseIn), new(3, 1)];
        var riemann = 0.0;
        const int steps = 300_000;
        for (var i = 0; i < steps; i++)
            riemann += SpeedRamp.SpeedAt(eased, 1, (i + 0.5) * 3.0 / steps) * 3.0 / steps;
        var integral = SpeedRamp.MediaSeconds(eased, 1, 3);
        expect(Math.Abs(integral - riemann) < 1e-6, "eased segments integrate to what their own speed curve adds up to (to a microsecond)");

        var increasing = true;
        var previous = double.NegativeInfinity;
        var inverse = true;
        for (var t = -1.0; t <= 4.0; t += 0.125)
        {
            var media = SpeedRamp.MediaSeconds(eased, 1, t);
            increasing &= media > previous;
            previous = media;
            inverse &= Math.Abs(SpeedRamp.ClipSeconds(eased, 1, media) - t) < 1e-6;
        }

        expect(increasing && inverse, "file time only ever moves forward, and the inverse finds the clip moment of any file time to a microsecond");
        expect(SpeedRamp.SpeedAt([new Keyframe(0, 40)], 1, 0) == Layer.FastestSpeed && SpeedRamp.SpeedAt([], 0.01, 5) == Layer.SlowestSpeed,
            "speeds outside 0.1x..16x are held to them");

        var layer = new Layer { Speed = 2 };
        expect(Near(layer.MediaSecondsAt(1.5), 3) && layer.SpeedAt(7) == 2, "a clip without a ramp maps through its constant speed");
        layer.SetSpeedKeys(linear);
        expect(Near(layer.MediaSecondsAt(2), 4) && Near(layer.SpeedAt(1), 2), "a clip with a ramp maps through it");
    }

    // ----- shell-seams-contract -----

    private static void ShellSeams(Action<bool, string> expect)
    {
        var program = SourceProbe.Member(SourceProbe.Src("Program.cs"), "public static void Main(");
        expect(HandPegApp.Shell.DpiMatrixRunner.Argument == "--dpi-matrix"
               && SourceProbe.InOrder(program, "Compositor.HardwareBenchmark.Run(args)", "Shell.DpiMatrixRunner.Argument", "Shell.DpiMatrixRunner.Run(args)", "Start();"),
            "--dpi-matrix is dispatched from Main before the normal start (no updater, migration or single-instance claim)");

        var xaml = SourceProbe.Src("MainWindow.xaml");
        expect(SourceProbe.InOrder(xaml, "x:Name=\"TitleRight\"", "x:Name=\"SideWindowButton\"", "x:Name=\"MinimizeButton\"", "x:Name=\"MaximizeButton\"", "x:Name=\"CloseButton\"")
               && System.Text.RegularExpressions.Regex.IsMatch(xaml, "x:Name=\"SideWindowButton\"[^>]*Style=\"\\{StaticResource CaptionButton\\}\"[^>]*Click=\"SideWindowButton_Click\"[^>]*WindowChrome.IsHitTestVisibleInChrome=\"True\""),
            "the 4th caption button sits left of Minimize, styled as a caption button and hit-testable in the title bar");
        expect(SourceProbe.MemberOfType("MainWindow", "private void SideWindowButton_Click(").Contains("SideWindowRow_Click(sender, e)", StringComparison.Ordinal),
            "it opens a side window through the Layout Selector row's own action (one home for it)");
        expect(SourceProbe.InOrder(xaml, "x:Name=\"PlayerOverlay\"", "x:Name=\"ExplodedLayer\"", "x:Name=\"ColorMatchHost\"", "</local:VideoSurface>"),
            "the Color Match host is the player overlay's last (topmost) layer");

        expect(MainWindow.EasyCutAngleOf(Key.D1) == 1 && MainWindow.EasyCutAngleOf(Key.D9) == 9 && MainWindow.EasyCutAngleOf(Key.NumPad5) == 5
               && MainWindow.EasyCutAngleOf(Key.D0) is null && MainWindow.EasyCutAngleOf(Key.NumPad0) is null && MainWindow.EasyCutAngleOf(Key.Z) is null,
            "EasyCut angles are digits 1-9 on the top row or the keypad");
        var keys = SourceProbe.Src("MainWindow.Keybinds.cs");
        expect(keys.Contains("partial void TakeEasyCutKey(int angle, bool repeat, ref bool handled);", StringComparison.Ordinal)
               && keys.Contains("partial void TakeSpringZoomKey(bool repeat, ref bool handled);", StringComparison.Ordinal)
               && SourceProbe.Member(keys, "private bool TakeOverlayKey(").Contains("return TakeSeamKey(e, key);", StringComparison.Ordinal),
            "the EasyCut and spring-zoom seams are offered every key ahead of the bound shortcuts");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
}
