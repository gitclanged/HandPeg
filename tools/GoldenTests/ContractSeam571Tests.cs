using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.7.1 wave-0 contracts the lanes build on: the Cursor-Locked Scrub Pointer setting and the Top &amp; Tail keys; the crossfade
/// curve on the Layer model; the region-aware menu request and the cut seam; the Slice-Preview request; and the engine and
/// shared-memory seams (the slice burst, the loop-preview and scrub hooks). Every line says what it proved.
/// </summary>
public static class ContractSeam571Tests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("contract-571-settings-keys", "# Cursor-Locked Scrub Pointer: off by default, saved only when on, in Settings; Top & Tail on Shift+Q/E and [ ] (WASD kept), Q/W in the Razor and Color presets.", SettingsAndKeys);
        yield return Case("contract-571-crossfade-curve", "# Crossfade curves: equal power by default, one gain formula, saved on the Layer only when linear, read back held to the known curves.", CrossfadeCurveFields);
        yield return Case("contract-571-menu-slice", "# One menu request for timeline and menus (region, seam, pointer time); the Slice-Preview request, spacing readout and gating rules.", MenuAndSlice);
        yield return Case("contract-571-engine-seams", "# Shared-memory slice burst in free control space (posted, capped, withdrawn, answered); engine hooks called where the lanes need them.", EngineSeams);
    }

    // ----- contract-571-settings-keys -----

    private static void SettingsAndKeys(Action<bool, string> expect)
    {
        var fresh = new AppSettings();
        var json = JsonSerializer.Serialize(fresh, AppSettingsJsonContext.Default.AppSettings);
        var on = new AppSettings { CursorLockedScrubPointer = true };
        var onJson = JsonSerializer.Serialize(on, AppSettingsJsonContext.Default.AppSettings);
        var read = JsonSerializer.Deserialize(onJson, AppSettingsJsonContext.Default.AppSettings)!;
        expect(!fresh.CursorLockedScrubPointer && !json.Contains("\"CursorLockedScrubPointer\"", StringComparison.Ordinal)
               && onJson.Contains("\"CursorLockedScrubPointer\":true", StringComparison.Ordinal) && read.CursorLockedScrubPointer,
            "CursorLockedScrubPointer is off in a new install (user decision Q1), written only when on, and read back on");
        expect(on.Clone().CursorLockedScrubPointer && !fresh.Clone().CursorLockedScrubPointer, "Clone copies it");

        var edited = fresh.Clone();
        edited.CursorLockedScrubPointer = true;
        var target = new AppSettings();
        var copied = AppSettings.CopyEdited(fresh, edited, target);
        var back = on.Clone();
        back.CursorLockedScrubPointer = false;
        var targetOn = new AppSettings { CursorLockedScrubPointer = true };
        AppSettings.CopyEdited(on, back, targetOn);
        expect(target.CursorLockedScrubPointer && copied.Contains(nameof(AppSettings.CursorLockedScrubPointer)) && !targetOn.CursorLockedScrubPointer,
            "the Settings window's Save writes it back either way (CopyEdited)");

        var settings = XDocument.Load(Path.Combine(SourceProbe.Root, "src", "Dialogs", "SettingsWindow.xaml"));
        var boxes = settings.Descendants(Wpf + "CheckBox").Where(c => (string?)c.Attribute("IsChecked") == "{Binding CursorLockedScrubPointer}").ToList();
        var label = boxes.Count == 1 ? (string?)boxes[0].Attribute("Content") ?? "" : "";
        var tip = boxes.Count == 1 ? (string?)boxes[0].Attribute("ToolTip") ?? "" : "";
        expect(boxes.Count == 1 && boxes[0].Ancestors(Wpf + "TabItem").FirstOrDefault() is { } tab && (string?)tab.Attribute("Header") == "Timeline & Player"
               && Words(label) <= 4 && Words(tip) <= 12,
            $"Settings has one CheckBox for it in the Timeline & Player tab: \"{label}\" ({Words(label)} words), its tooltip {Words(tip)} words");

        var top = KeybindManager.Action(KeybindManager.TrimStartToPlayhead);
        var tail = KeybindManager.Action(KeybindManager.TrimEndToPlayhead);
        expect(top is { Id: "clip.trimStartToPlayhead", Category: KeybindManager.Editing, Defaults: ["Shift+Q", "["] } && top.Has(KeyActionFlags.ChangesProject)
               && tail is { Id: "clip.trimEndToPlayhead", Category: KeybindManager.Editing, Defaults: ["Shift+E", "]"] } && tail.Has(KeyActionFlags.ChangesProject)
               && !top.Has(KeyActionFlags.Repeat | KeyActionFlags.Continuous | KeyActionFlags.Hold | KeyActionFlags.WorksInText),
            "Top & Tail are clip.trimStartToPlayhead (Shift+Q, [) and clip.trimEndToPlayhead (Shift+E, ]): project edits, once per press");
        expect(new[] { "Shift+Q", "[", "Shift+E", "]", "Q", "W" }.All(text => KeybindManager.Parse(text) is { } chord
               && KeybindManager.Validate(chord, KeybindManager.TrimStartToPlayhead) is null && KeybindManager.Format(chord) == text),
            "Shift+Q, [, Shift+E, ], Q and W are legal chords for them and read back as written");

        var defaults = KeybindManager.PresetChords(AppSettings.DefaultKeybindPreset);
        expect(Keys(defaults, "transport.stepBack").Contains("Q") && Keys(defaults, "transport.stepForward").Contains("E")
               && Keys(defaults, "nav.forward") == "W" && Keys(defaults, "nav.back") == "S" && Keys(defaults, "nav.left") == "A" && Keys(defaults, "nav.right") == "D"
               && Keys(defaults, KeybindManager.TrimStartToPlayhead) == "Shift+Q [" && Keys(defaults, KeybindManager.TrimEndToPlayhead) == "Shift+E ]",
            "WASD (Default): Q/E still step frames and W/S/A/D still zoom and pan while Top & Tail take Shift+Q / Shift+E and [ / ]");
        foreach (var preset in new[] { "Studio Razor & Ripple", "Color & Node Studio" })
        {
            var chords = KeybindManager.PresetChords(preset);
            expect(KeybindManager.PresetNames.Contains(preset) && Keys(chords, KeybindManager.TrimStartToPlayhead) == "Q" && Keys(chords, KeybindManager.TrimEndToPlayhead) == "W"
                   && Keys(chords, "transport.stepBack") == "Left" && Keys(chords, "nav.forward") == "" && Keys(chords, "edit.trimStart") == "" && Keys(chords, "edit.trimEnd") == "",
                $"{preset}: Top & Tail on Q / W; frame back on Left, the held zoom keys and the plain trims' keys given up for them");
        }

        var problems = KeybindManager.CheckPresets();
        expect(problems.Count == 0, "KeybindManager.CheckPresets(): no preset gives a chord to two actions" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
    }

    private static string Keys(Dictionary<string, List<InputChord>> chords, string id) => string.Join(" ", chords[id].Select(KeybindManager.Format));

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // ----- contract-571-crossfade-curve -----

    private static void CrossfadeCurveFields(Action<bool, string> expect)
    {
        expect(string.Join(",", Enum.GetNames<CrossfadeCurve>()) == "EqualPower,Linear" && default(CrossfadeCurve) == CrossfadeCurve.EqualPower
               && CrossfadeCurves.All.Count == 2 && CrossfadeCurves.Label(CrossfadeCurve.EqualPower) == "Equal Power" && CrossfadeCurves.Label(CrossfadeCurve.Linear) == "Linear",
            "CrossfadeCurve is EqualPower (the default, 0) then Linear, labelled Equal Power and Linear");

        var steps = Enumerable.Range(0, 101).Select(i => i / 100.0).ToList();
        expect(steps.All(t => CrossfadeCurves.Gains(CrossfadeCurve.EqualPower, t) is var (o, i) && Math.Abs(o * o + i * i - 1) < 1e-12)
               && Math.Abs(CrossfadeCurves.Gains(CrossfadeCurve.EqualPower, 0.5).Out - Math.Sqrt(0.5)) < 1e-12,
            "equal power: out^2 + in^2 = 1 all through, both at -3 dB in the middle");
        expect(steps.All(t => CrossfadeCurves.Gains(CrossfadeCurve.Linear, t) is var (o, i) && Math.Abs(o + i - 1) < 1e-12 && Math.Abs(i - t) < 1e-12),
            "linear: out + in = 1 all through, in = progress");
        expect(CrossfadeCurves.All.All(c => CrossfadeCurves.Gains(c, 0) == (1, 0) && CrossfadeCurves.Gains(c, 1) == (0, 1)
               && CrossfadeCurves.Gains(c, -2) == (1, 0) && CrossfadeCurves.Gains(c, 7) == (0, 1) && CrossfadeCurves.Gains(c, double.NaN) == (1, 0)),
            "both curves are exact at the ends, clamp outside them and read NaN as the start");

        var plain = new Layer().ToState();
        var plainJson = JsonSerializer.Serialize(plain);
        expect(new Layer() is { CrossfadeInCurve: CrossfadeCurve.EqualPower, CrossfadeOutCurve: CrossfadeCurve.EqualPower }
               && plain is { CrossfadeInCurve: null, CrossfadeOutCurve: null } && !plainJson.Contains("Crossfade", StringComparison.Ordinal),
            "a new clip crossfades with equal power and saves neither curve (older projects read and write unchanged)");

        var linear = new Layer { CrossfadeInCurve = CrossfadeCurve.Linear };
        var state = JsonSerializer.Deserialize<LayerState>(JsonSerializer.Serialize(linear.ToState()))!;
        var rebuilt = Layer.FromState(state, 1920, 1080, 1920, 1080);
        var timed = new Layer();
        timed.ApplyTiming(state);
        expect(state is { CrossfadeInCurve: CrossfadeCurve.Linear, CrossfadeOutCurve: null }
               && rebuilt is { CrossfadeInCurve: CrossfadeCurve.Linear, CrossfadeOutCurve: CrossfadeCurve.EqualPower }
               && timed is { CrossfadeInCurve: CrossfadeCurve.Linear, CrossfadeOutCurve: CrossfadeCurve.EqualPower },
            "a linear curve is saved, and read back by FromState and ApplyTiming (the timing transforms' path)");

        var odd = Layer.FromState(new LayerState { CrossfadeInCurve = (CrossfadeCurve)7, CrossfadeOutCurve = CrossfadeCurve.Linear }, 1920, 1080, 1920, 1080);
        expect(odd is { CrossfadeInCurve: CrossfadeCurve.EqualPower, CrossfadeOutCurve: CrossfadeCurve.Linear } && CrossfadeCurves.Known(null) == CrossfadeCurve.EqualPower,
            "an unknown saved curve reads as equal power; each side is read on its own");
    }

    // ----- contract-571-menu-slice -----

    private static void MenuAndSlice(Action<bool, string> expect)
    {
        expect(string.Join(",", Enum.GetNames<ClipMenuRegion>()) == "Body,FadeIn,FadeOut,Seam,Gain" && default(ClipMenuRegion) == ClipMenuRegion.Body,
            "ClipMenuRegion: Body (the default), FadeIn, FadeOut, Seam, Gain");

        var menu = new ContextMenu();
        var fadeIn = new TimelineMenuRequest(null, null, false, true, menu);
        var fadeOut = new TimelineMenuRequest(null, null, false, false, menu);
        var body = new TimelineMenuRequest(null, null, false, null, menu);
        expect(fadeIn.Region == ClipMenuRegion.FadeIn && fadeOut.Region == ClipMenuRegion.FadeOut && body.Region == ClipMenuRegion.Body
               && body.Seam is null && double.IsNaN(body.Seconds),
            "a request made the old way routes as before: a fade handle names its fade, else the body; no seam, no pointer time");

        var (a, b) = (new object(), new object());
        var row = new TimelineRow { Key = "v1", Data = new object() };
        var seam = new TimelineSeamTarget(row, new TimelineBlock { Clip = a }, new TimelineBlock { Clip = b }, 12.5) { CrossfadeSeconds = 0.5, IsThroughEdit = true };
        var asked = body with { Region = ClipMenuRegion.Seam, Seam = seam, Seconds = 12.5 };
        var rebuilt = new TimelineSeamTarget(row, new TimelineBlock { Clip = a }, new TimelineBlock { Clip = b }, 12.5);
        var swapped = new TimelineSeamTarget(row, new TimelineBlock { Clip = b }, new TimelineBlock { Clip = a }, 12.5);
        expect(asked is { Region: ClipMenuRegion.Seam, Seconds: 12.5, Seam: { CrossfadeSeconds: 0.5, IsThroughEdit: true } } && ReferenceEquals(asked.Menu, menu)
               && seam.IsSame(rebuilt) && !seam.IsSame(swapped) && !seam.IsSame(null)
               && !new TimelineSeamTarget(row, new TimelineBlock(), new TimelineBlock(), 0).IsSame(new TimelineSeamTarget(row, new TimelineBlock(), new TimelineBlock(), 0)),
            "the timeline refines a request to a seam with its time; a seam is the same seam by its two clips, whatever the blocks were rebuilt as");

        var pm = "\u00B1";
        expect(SlicePreviewFrameSpacing.OfFrames(1, 30) is { Frames: 1, InFrames: true } one && Math.Abs(one.Seconds - 1 / 30.0) < 1e-12 && one.Readout == pm + "1f"
               && SlicePreviewFrameSpacing.OfFrames(3, 60).Readout == pm + "3f" && SlicePreviewFrameSpacing.OfFrames(0, 30).Frames == 1,
            "frame spacing: whole frames at the frame rate (at least one), read out as +-1f, +-3f");
        expect(SlicePreviewFrameSpacing.OfSeconds(0.5).Readout == pm + "0.5s" && SlicePreviewFrameSpacing.OfSeconds(1).Readout == pm + "1s"
               && SlicePreviewFrameSpacing.OfSeconds(1 / 3.0).Readout == pm + "0.33s" && !SlicePreviewFrameSpacing.OfSeconds(0.5).InFrames,
            "seconds spacing: read out as +-0.5s, +-1s (at most two decimals)");
        expect(!SlicePreviewFrameSpacing.OfFrames(2, 0).IsUsable && !SlicePreviewFrameSpacing.OfSeconds(double.NaN).IsUsable && !default(SlicePreviewFrameSpacing).IsUsable
               && default(SlicePreviewFrameSpacing).Readout == "",
            "no frame rate, NaN or nothing makes an unusable spacing with no readout");

        var spacing = SlicePreviewFrameSpacing.OfFrames(1, 30);
        List<SlicePreviewSlot> Slots(int count) => [.. Enumerable.Range(0, count).Select(i => new SlicePreviewSlot(i - count / 2, 10 + (i - count / 2) / 30.0, 4 + (i - count / 2) / 30.0))];
        var five = new SlicePreviewRequest(@"C:\clip.mp4", 0, Slots(5), spacing);
        expect(five is { IsValid: true, ShortSide: SlicePreviewRequest.DefaultShortSide } && five.Slots[2].Offset == 0 && new SlicePreviewRequest("v.mp4", 0, Slots(7), spacing).IsValid,
            "a request of five or seven slots of one video file is valid, the middle slot the playhead's (offset 0)");
        expect(!new SlicePreviewRequest("v.mp4", 0, [], spacing).IsValid && !new SlicePreviewRequest("v.mp4", 0, Slots(8), spacing).IsValid
               && !new SlicePreviewRequest("", 0, Slots(5), spacing).IsValid && !new SlicePreviewRequest("v.mp4", -1, Slots(5), spacing).IsValid
               && !new SlicePreviewRequest("v.mp4", 0, [new SlicePreviewSlot(0, 1, double.NaN)], spacing).IsValid
               && !new SlicePreviewRequest("v.mp4", 0, Slots(5), spacing, 0).IsValid,
            "no slots, too many, no path, a negative stream, a NaN file time or no thumbnail size is not");
        expect(SlicePreviewRequest.MinSlots == 5 && SlicePreviewRequest.MaxSlots == 7 && SlicePreviewRequest.WakeAfterMs == 120
               && SlicePreviewRequest.DormantAbove == 300 && SlicePreviewRequest.WakeBelow == 250 && SlicePreviewRequest.MaxSlots <= CompositorProtocol.MaxSliceSlots,
            "the ribbon shows 5-7 slots (all fit one burst), dormant above 300 DIP/s, fading in below 250 DIP/s held 120 ms");
    }

    // ----- contract-571-engine-seams -----

    private static void EngineSeams(Action<bool, string> expect)
    {
        var block = new ControlBlock();
        ref var start = ref Unsafe.As<ControlBlock, byte>(ref block);
        var offsets = new (string Name, long At, int Size)[]
        {
            ("SliceBurstVersion", Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref block.SliceBurstVersion)), 4),
            ("SliceBurstCount", Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref block.SliceBurstCount)), 4),
            ("SliceBurstSourceId", Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref block.SliceBurstSourceId)), 4),
            ("SliceBurstPathHash", Unsafe.ByteOffset(ref start, ref Unsafe.As<long, byte>(ref block.SliceBurstPathHash)), 8),
            ("SliceBurstDone", Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref block.SliceBurstDone)), 4),
            ("SliceBurstShortSide", Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref block.SliceBurstShortSide)), 4),
            ("SliceBurstMedia", Unsafe.ByteOffset(ref start, ref Unsafe.As<SliceBurstTimes, byte>(ref block.SliceBurstMedia)), Unsafe.SizeOf<SliceBurstTimes>()),
        };
        var cacheCap = Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref block.CacheBudgetCap));
        var timings = Unsafe.ByteOffset(ref start, ref Unsafe.As<SlotTimings, byte>(ref block.SlotGpuMs));
        var ordered = offsets.OrderBy(o => o.At).ToList();
        expect(Unsafe.SizeOf<ControlBlock>() == CompositorProtocol.ControlBytes && CompositorProtocol.Version == 9 && Unsafe.SizeOf<SliceBurstTimes>() == 8 * CompositorProtocol.MaxSliceSlots
               && ordered[0].At >= cacheCap + 4 && ordered[^1].At + ordered[^1].Size <= timings
               && ordered.Zip(ordered.Skip(1)).All(p => p.First.At + p.First.Size <= p.Second.At) && offsets.All(o => o.At % Math.Min(o.Size, 8) == 0),
            $"the burst sits in the free control space ({ordered[0].At}..{ordered[^1].At + ordered[^1].Size - 1}, between CacheBudgetCap and the slot timings), aligned, overlapping nothing; the block is still {CompositorProtocol.ControlBytes} bytes and protocol {CompositorProtocol.Version}");

        var first = SliceBurstSlot.Post(ref block, 3, 0x1234, [1.0, double.NaN, 2.0, -1, 3.0], 72);
        expect(first == 1 && block is { SliceBurstVersion: 1, SliceBurstCount: 3, SliceBurstSourceId: 3, SliceBurstPathHash: 0x1234, SliceBurstShortSide: 72 }
               && block.SliceBurstMedia[0] == 1 && block.SliceBurstMedia[1] == 2 && block.SliceBurstMedia[2] == 3,
            "a post writes the source, hash, size and the finite times in order (NaN and negative ones dropped), then bumps the version");
        var capped = SliceBurstSlot.Post(ref block, 3, 0x1234, [.. Enumerable.Range(0, 12).Select(i => i * 0.1)], 0);
        expect(capped == 2 && block.SliceBurstCount == CompositorProtocol.MaxSliceSlots && block.SliceBurstMedia[7] == 7 * 0.1, "a post of more times keeps the first eight");
        var withdrawn = SliceBurstSlot.Post(ref block, 3, 0x1234, [], 0);
        var noSource = SliceBurstSlot.Post(ref block, -1, 5, [1.0], 0);
        expect(withdrawn == 0 && noSource == 0 && block is { SliceBurstCount: 0, SliceBurstSourceId: -1, SliceBurstPathHash: 0, SliceBurstVersion: 4 },
            "no times, or no source, withdraws the burst (count 0) and still bumps the version so the worker lets it go");
        block.SliceBurstDone = 2;
        expect(SliceBurstSlot.IsDone(ref block, 2) && SliceBurstSlot.IsDone(ref block, 1) && !SliceBurstSlot.IsDone(ref block, 3) && !SliceBurstSlot.IsDone(ref block, 0),
            "a burst is done once the worker answers its version (or a newer one); version 0 never is");

        var client = SourceProbe.Src("Services", "CompositorClient.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(client, "public int PostSliceBurst("), "SliceBurstSlot.Post(ref _shared.Control", "_wake.Set();")
               && SourceProbe.Member(client, "public bool SliceBurstDone(").Contains("SliceBurstSlot.IsDone(", StringComparison.Ordinal),
            "CompositorClient posts through SliceBurstSlot and wakes the worker without rendering; SliceBurstDone reads the answer");

        var engine = SourceProbe.Src("Services", "GpuPreviewEngine.cs");
        var hooks = new[] { "partial void LoopPreviewTurn();", "partial void AddLoopPreviewHints(Span<GpuSeekHint> extras, ref int count);",
            "partial void StateInstalled(SequenceExportState state);", "partial void SliceBurstTurn(bool playing);" };
        expect(hooks.All(h => engine.Contains(h, StringComparison.Ordinal)), "GpuPreviewEngine declares the four partial hooks the lanes' partial files implement");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "private void Pump()"), "TakeMirrorRequests();", "SliceBurstTurn(_playing);", "if (_playing)",
                   "ApplyGhostPause();", "LoopPreviewTurn();", "DrainSeek();", "var at = _audio.Position"),
            "the pump posts the slice burst every turn, and wraps a hover preview's loop each playing turn before the posted seek is applied");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "public void SetState("), "_state = state;", "StateInstalled(state);")
               && SourceProbe.InOrder(SourceProbe.Member(engine, "private void LeaveHints("), "tabs.Hints", "AddLoopPreviewHints(extras, ref extraCount);", "_hintWriter.Write("),
            "SetState tells the hover preview of each installed Sequence; the quiet prewarm extras take its window's hints");
        expect(SourceProbe.InOrder(SourceProbe.Member(engine, "public bool ScrubSeek("), "_ghostPaused", "_lastSeekAt", "_pausedAt", "return false;", "Seek(seconds);"),
            "ScrubSeek (one coalesced step a frame) posts nothing when a paused or ghost-paused target is the moment already posted");
    }
}
