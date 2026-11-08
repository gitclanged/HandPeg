using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using Pose = HandPegApp.Services.GpuPreviewEngine.CameraPose;
using Wheel = HandPegApp.MainWindow.ExplodedWheelAction;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.3-Stage3's 3D Exploded View (the user's bugs on the Snapdragon, and touch).
/// <para>exploded-3d-transition-pacing: (1) the camera's way in, out and Reset (GpuPreviewEngine.StepPose): 240 ms along
/// EasingCurves.CubicInOut, each step at most one 60 Hz refresh, so a cold first frame of 70 ms moves it a hair (the 5.3-Stage2 chase
/// moved it more than half the way), no step moves it far, it lands exactly on its target on a set step at 60 and 120 Hz, a way in
/// turned back halfway leaves from where it is with no jump, and a turn asked for during the way in is where it lands; the user's own
/// moves are still chased, a step at most a refresh. (2) A real compositor worker draws a 3D scene of four planes (a selected pattern,
/// a blurred one, a blended one and an adjustment layer's glass) through the whole way in and the whole way out, frame by frame as
/// the player would, and its device makes no texture after the way in's first frame (ControlBlock.TexturesMade).</para>
/// <para>exploded-3d-gestures: what the wheel does by its keys, its direction and its device (MainWindow.ExplodedWheelDoes: a pinch
/// zooms, Alt picks the layers, Shift the spacing, a touchpad's two-finger scroll orbits, a mouse's wheel still zooms), what a touch
/// on the empty space does (MainWindow.ExplodedTouchDoes), and the hints the view shows (MainWindow.xaml).</para>
/// </summary>
public static class ExplodedViewTests
{
    private static void Line(StringBuilder text, FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body)
    {
        var text = new StringBuilder(header).Append('\n');
        var problems = new List<string>();
        try
        {
            body(text, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }

    // ----- exploded-3d-transition-pacing -----

    public static GraphResult Pacing() => Case("exploded-3d-transition-pacing",
        "# The 3D view's way in and out (5.3-Stage3): 240 ms along a cubic ease, each step at most a 60 Hz refresh; no texture made after its first frame.", (text, problems) =>
        {
            Camera(text, problems);
            Worker(text, problems);
        });

    private static readonly Pose Into = new(GpuPreviewEngine.DefaultYaw, GpuPreviewEngine.DefaultPitch, GpuPreviewEngine.DefaultZoom, GpuPreviewEngine.DefaultSpacing);

    // How far along the way from one pose to another a pose is (by the turn: every part moves by the same share).
    private static double Share(Pose from, Pose to, Pose at) => (at.Yaw - from.Yaw) / (to.Yaw - from.Yaw);

    /// <summary>A transition run with these frame times (seconds; the last repeated) until it lands: every pose, the clock done.</summary>
    private static List<Pose> Run(Pose from, Func<int, Pose> target, Func<int, double> frame, int most = 400)
    {
        var poses = new List<Pose>();
        double? clock = 0;
        var drawn = from;
        for (var step = 0; step < most && clock is not null; step++)
        {
            drawn = GpuPreviewEngine.StepPose(drawn, target(step), frame(step), from, ref clock);
            poses.Add(drawn);
        }

        return poses;
    }

    private static void Camera(StringBuilder text, List<string> problems)
    {
        Line(text, $"the camera (GpuPreviewEngine.StepPose), from the flat picture to the 3D view's start ({Into.Yaw:0} degrees, {Into.Pitch:0} up, zoom {Into.Zoom:0.##}, spacing {Into.Spacing:0.##}):");

        // At 60 and 120 Hz: lands on a set step, every step forward, none far.
        foreach (var hertz in new[] { 60, 120 })
        {
            var poses = Run(Pose.Flat, _ => Into, _ => 1.0 / hertz);
            var shares = poses.Select(p => Share(Pose.Flat, Into, p)).ToList();
            var widest = shares.Zip(shares.Skip(1), (a, b) => b - a).Prepend(shares[0]).Max();
            var wanted = (int)Math.Ceiling(GpuPreviewEngine.CameraTransitionSeconds * hertz - 1e-9);
            Line(text, $"  at {hertz} Hz: lands exactly on the target on step {poses.Count} ({wanted} wanted), every step forward, none more than {Math.Ceiling(widest * 100):0} % of the way");
            if (poses.Count != wanted || poses[^1] != Into)
                problems.Add($"at {hertz} Hz the way in took {poses.Count} steps (not {wanted}) or did not land on the target");
            if (shares.Zip(shares.Skip(1)).Any(p => p.Second < p.First))
                problems.Add($"at {hertz} Hz the camera stepped back on its way in");
        }

        // The Snapdragon's cold first frames (the card waking, the planes made): 70, 45 and 30 ms, then 60 Hz.
        double[] cold = [0.070, 0.045, 0.030];
        var slow = Run(Pose.Flat, _ => Into, step => step < cold.Length ? cold[step] : 1 / 60.0);
        var first = Share(Pose.Flat, Into, slow[0]);
        var before = 1 - Math.Exp(-cold[0] / 0.085);
        Line(text, $"  cold first frames of 70, 45 and 30 ms: the first step moves it {first * 100:0.0} % of the way (5.3-Stage2's chase: {before * 100:0} %), and it lands on step {slow.Count}");
        if (first > 0.005 || slow.Count != 15)
            problems.Add($"a cold first frame moved the camera {first * 100:0.0} % of the way (or the way in took {slow.Count} steps, not 15)");

        // Turned back after six steps: from where it is, no jump.
        var halfway = Run(Pose.Flat, _ => Into, _ => 1 / 60.0, most: 6)[^1];
        var back = Run(halfway, _ => Pose.Flat, _ => 1 / 60.0);
        var jump = (halfway.Yaw - back[0].Yaw) / Into.Yaw;
        Line(text, $"  turned back after 6 steps ({Share(Pose.Flat, Into, halfway) * 100:0} % of the way in): the first step back moves it {jump * 100:0.0} % of the whole way, and it lands on the flat picture on step {back.Count}");
        if (jump is < 0 or > 0.01 || back[^1] != Pose.Flat || back.Count != 15)
            problems.Add($"turned back: a first step of {jump * 100:0.0} %, {back.Count} steps, landing at {back[^1]}");

        // A turn asked for during the way in (an orbit at step 5): it lands there.
        var turned = Into with { Yaw = Into.Yaw + 20 };
        var orbited = Run(Pose.Flat, step => step < 5 ? Into : turned, _ => 1 / 60.0);
        Line(text, $"  turned 20 degrees more at step 5 of the way in: lands on the new turn, on step {orbited.Count}");
        if (orbited[^1] != turned || orbited.Count != 15)
            problems.Add($"a turn during the way in: landed at {orbited[^1]} on step {orbited.Count}");

        // The user's own moves are chased, a step at most a refresh: a frame a second late moves the camera no further than one.
        double? none = null;
        var chased = GpuPreviewEngine.StepPose(Into, turned, 1.0, default, ref none);
        var chasedShare = (chased.Yaw - Into.Yaw) / (turned.Yaw - Into.Yaw);
        Line(text, $"  an orbit chased (no transition), its step a second late: {chasedShare * 100:0} % of the way, as at one 60 Hz refresh");
        if (none is not null || Math.Abs(chasedShare - (1 - Math.Exp(-1 / 60.0 / 0.085))) > 1e-9)
            problems.Add($"a chase step a second late moved the camera {chasedShare * 100:0.0} % of the way");

        // The curve: from rest, through the middle, to rest.
        var curve = new[] { 0, 0.1, 0.25, 0.5, 0.75, 0.9, 1 }.Select(u => EasingCurves.CubicInOut(u)).ToList();
        Line(text, $"  EasingCurves.CubicInOut at 0, 0.1, 0.25, 0.5, 0.75, 0.9, 1: {string.Join(", ", curve.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)))}");
    }

    private static void Worker(StringBuilder text, List<string> problems)
    {
        const int cw = 1280, ch = 720, ow = 960, oh = 540;
        using var client = new CompositorClient(adapter: -1, keepStandby: false);

        void Scene(int viewMode, Pose pose)
        {
            client.ClearScene();
            ref var scene = ref client.Scene;
            (scene.CanvasWidth, scene.CanvasHeight, scene.Flags) = (cw, ch, FrameSceneHeader.FlagExactFrames | (viewMode == 1 ? FrameSceneHeader.FlagDepthRails : 0));
            (scene.OutputWidth, scene.OutputHeight) = (ow, oh);
            (scene.ViewMode, scene.CameraYaw, scene.CameraPitch, scene.CameraZoom, scene.ExplodedZSpread) =
                (viewMode, (float)pose.Yaw, (float)pose.Pitch, (float)pose.Zoom, (float)(pose.Spacing * Math.Max(cw, ch)));
            (int Kind, float X, float Y, float W, float H)[] boxes = [(1, 0, 0, cw, ch), (1, 160, 90, 480, 270), (2, 640, 300, 400, 300), (3, 300, 400, 500, 200)];
            for (var k = 0; k < boxes.Length; k++)
            {
                var (kind, x, y, w, h) = boxes[k];
                ref var layer = ref client.Layer(k);
                layer.SourceKind = (int)(kind == 2 ? GpuSourceKind.SolidColor : GpuSourceKind.TestPattern);
                layer.PatternKind = 1 + k % 2;
                (layer.X, layer.Y, layer.Width, layer.Height) = (x, y, w, h);
                (layer.AnchorX, layer.AnchorY, layer.Opacity, layer.ZDepth) = (0.5f, 0.5f, 1, k);
                (layer.MaskResourceId, layer.LutResourceId, layer.CurveResourceId, layer.StillResourceId) = (-1, -1, -1, -1);
                (layer.CropRight, layer.CropBottom) = (1, 1);
                (layer.SolidR, layer.SolidG, layer.SolidB, layer.SolidA) = (0.9f, 0.4f, 0.1f, 1);
                layer.Flags = GpuLayerDescriptor.FlagVisible | (k == 0 ? GpuLayerDescriptor.FlagSelected : 0) | (kind == 3 ? GpuLayerDescriptor.FlagAdjustment : 0);
                if (k == 1)
                    layer.Effects[layer.EffectCount++] = new GpuEffectPacket(GpuShaderSlot.BoxBlur, 12, 0, 0, 0, 0, 0, 0, 0);
                if (kind == 2)
                    layer.Effects[layer.EffectCount++] = new GpuEffectPacket(GpuShaderSlot.BlendMode, 2, 0, 0, 0, 0, 0, 0, 0);
                client.Scene.ActiveLayerCount = k + 1;
            }
        }

        int Made() => Volatile.Read(ref client.Memory.Control.TexturesMade);
        bool Draw(string what, int viewMode, Pose pose)
        {
            Scene(viewMode, pose);
            if (client.WaitForFrame(client.Publish(), 10_000))
                return true;
            problems.Add($"{what}: no frame within 10 s ({client.State}: {client.Message})");
            return false;
        }

        // The flat picture first (as the player was), then the way in at 60 Hz, frame by frame, then the way out.
        if (!Draw("the flat picture", 0, Pose.Flat))
            return;
        var flat = Made();
        var wayIn = Run(Pose.Flat, _ => Into, _ => 1 / 60.0);
        if (!Draw("the way in's first frame", 1, wayIn[0]))
            return;
        var afterFirst = Made();
        foreach (var pose in wayIn.Skip(1))
        {
            if (!Draw("the way in", 1, pose))
                return;
        }

        var afterIn = Made();
        var wayOut = Run(Into, _ => Pose.Flat, _ => 1 / 60.0);
        foreach (var pose in wayOut.SkipLast(1))
        {
            if (!Draw("the way out", 1, pose))
                return;
        }

        var afterOut = Made();
        Console.WriteLine($"        textures made: {flat} by the flat picture, {afterFirst - flat} by the way in's first frame, {afterIn - afterFirst} by its {wayIn.Count - 1} others, {afterOut - afterIn} by the way out's {wayOut.Count - 1} 3D frames");
        Line(text, $"a compositor worker drawing four planes (a selected pattern, a blurred one, a blended one, an adjustment's glass) {cw} x {ch} on {ow} x {oh}:");
        Line(text, $"  the way in's {wayIn.Count - 1} frames after its first, and the way out's {wayOut.Count - 1}: no texture made (the first frame makes the 3D view's own)");
        if (afterIn != afterFirst || afterOut != afterIn)
            problems.Add($"textures were made during the camera's move: {afterIn - afterFirst} on the way in after its first frame, {afterOut - afterIn} on the way out");
        if (afterFirst == flat)
            problems.Add("the way in's first frame made no texture: the scene was not drawn in 3D (or the counter is not published)");
    }

    // ----- exploded-3d-gestures -----

    public static GraphResult Gestures() => Case("exploded-3d-gestures",
        "# The 3D view's wheel, touchpad and touch (5.3-Stage3): a pinch zooms, two fingers orbit, Alt + the wheel picks the layers.", (text, problems) =>
        {
            Line(text, $"the wheel (MainWindow.ExplodedWheelDoes), by its keys, its direction and its device:");
            (string What, bool Sideways, ModifierKeys Keys, bool Touchpad, Wheel Wanted)[] wheel =
            [
                ("a mouse's wheel", false, ModifierKeys.None, false, Wheel.Zoom),
                ("a touchpad's two-finger scroll up or down", false, ModifierKeys.None, true, Wheel.Tip),
                ("a touchpad's two-finger scroll sideways", true, ModifierKeys.None, true, Wheel.Turn),
                ("a mouse's tilted wheel", true, ModifierKeys.None, false, Wheel.Turn),
                ("a touchpad's pinch (Ctrl + the wheel)", false, ModifierKeys.Control, true, Wheel.Zoom),
                ("Ctrl + a mouse's wheel", false, ModifierKeys.Control, false, Wheel.Zoom),
                ("Alt + the wheel", false, ModifierKeys.Alt, false, Wheel.Layers),
                ("Alt + a touchpad's scroll", false, ModifierKeys.Alt, true, Wheel.Layers),
                ("Shift + the wheel", false, ModifierKeys.Shift, false, Wheel.Spacing),
                ("Ctrl + Alt + the wheel (Ctrl first)", false, ModifierKeys.Control | ModifierKeys.Alt, false, Wheel.Zoom),
                ("Ctrl + a sideways wheel", true, ModifierKeys.Control, false, Wheel.None),
            ];
            foreach (var (what, sideways, keys, touchpad, wanted) in wheel)
            {
                var does = MainWindow.ExplodedWheelDoes(sideways, keys, touchpad);
                Line(text, $"  {what}: {does}");
                if (does != wanted)
                    problems.Add($"{what}: {does}, not {wanted}");
            }

            Line(text, $"a touch on the empty space (MainWindow.ExplodedTouchDoes), a step of it:");
            (string What, int Fingers, Vector Moved, Vector Scale, (double Yaw, double Pitch, double Zoom, double Spacing) Wanted)[] touch =
            [
                ("one finger 10 right and 5 up", 1, new Vector(10, -5), new Vector(1, 1), (-3, -1.5, 1, 0)),
                ("two fingers spread to 1.25 times", 2, new Vector(0, 0), new Vector(1.25, 1.25), (0, 0, 0.8, 0)),
                ("two fingers pinched to 0.8 times", 2, new Vector(3, 2), new Vector(0.8, 0.8), (0, 0, 1.25, 0)),
                ("two fingers dragged 40 up, no pinch", 2, new Vector(0, -40), new Vector(1, 1), (0, 0, 1, 0.06)),
                ("two fingers dragged 40 up while spreading to 1.1 times", 2, new Vector(0, -40), new Vector(1.1, 1.1), (0, 0, 1 / 1.1, 0)),
            ];
            foreach (var (what, fingers, moved, scale, wanted) in touch)
            {
                var does = MainWindow.ExplodedTouchDoes(fingers, moved, scale);
                Line(text, $"  {what}: turn {does.Yaw:0.##}, tip {does.Pitch:0.##}, zoom factor {does.Zoom:0.###}, spacing {does.Spacing:+0.###;-0.###;0}");
                if (Math.Abs(does.Yaw - wanted.Yaw) > 1e-9 || Math.Abs(does.Pitch - wanted.Pitch) > 1e-9 || Math.Abs(does.Zoom - wanted.Zoom) > 1e-9 || Math.Abs(does.Spacing - wanted.Spacing) > 1e-9)
                    problems.Add($"{what}: {does}, not {wanted}");
            }

            // The hints the view shows say so.
            var main = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MainWindow.xaml"));
            var hints = main.Descendants().FirstOrDefault(e => e.Name.LocalName == "TextBlock"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name" && a.Value == "3D View Hints"));
            var said = string.Join(" / ", hints?.Elements().Where(e => e.Name.LocalName == "Run").Select(r => (string?)r.Attribute("Text")) ?? []);
            Line(text, $"the 3D view's hints: {said}");
            foreach (var needed in new[] { "pinch", "two-finger", "Alt + wheel", "[ ]", "Shift + wheel", "Right-drag" })
            {
                if (!said.Contains(needed, StringComparison.Ordinal))
                    problems.Add($"the 3D view's hints do not mention \"{needed}\"");
            }
        });

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HandPegApp.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("the repository (HandPegApp.csproj) above " + AppContext.BaseDirectory);
    }
}
