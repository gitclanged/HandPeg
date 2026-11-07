using System.Text;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// creator-hotbar (Pass 5.2.1): the Creator Hotbar's five slots as the settings keep them, and what a press puts on the clips on the
/// real view model: ordinary effect cards, fades, Studio Voice and keyframes, each press one step of Undo. Every line says what it proved.
/// </summary>
public static class HotbarTests
{
    private const string Overlay = "D:/media/overlay.mkv", Voice = "D:/media/voice.wav";

    public static GraphResult Run()
    {
        var text = new StringBuilder("# The Creator Hotbar (5.2.1): five slots, each an Effect, a Transition, a Game Preset or a one-click tool; a press one step of Undo.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        var settings = AppSettings.Current;
        var (kept, shown) = (settings.HotbarSlots, settings.ShowCreatorHotbar);
        try
        {
            Slots(Expect);
            Presses(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            (settings.HotbarSlots, settings.ShowCreatorHotbar) = (kept, shown);
        }

        return new GraphResult("creator-hotbar", text.ToString(), problems, 0);
    }

    private static void Slots(Action<bool, string> expect)
    {
        var fresh = new AppSettings();
        expect(fresh.ShowCreatorHotbar && fresh.HotbarSlots is null, "a new install shows the hotbar, with the default slots");
        var slots = HotbarSlot.FromSettings(null);
        expect(string.Join(" | ", slots.Select(s => s.Label)) == "Motion Blur | 3D Fly-In | Neon Rim | Whip Pan | Studio Voice",
            "they are Motion Blur, 3D Fly-In, Neon Rim, Whip Pan and Studio Voice");
        expect(slots.Select(s => s.Kind).SequenceEqual([HotbarKind.Effect, HotbarKind.Tool, HotbarKind.Effect, HotbarKind.Transition, HotbarKind.Tool]),
            "two effects, two one-click tools and a transition");
        expect(slots.All(s => HotbarSlot.Parse(s.ToString()) == s), "each is written as kind:id and read back the same");
        expect(HotbarSlot.Parse($"gamepreset:Overwatch 2") is { Kind: HotbarKind.GamePreset, Id: "Overwatch 2" }, "a Game Preset is kept by its name");
        expect(HotbarSlot.Parse("effect:builtin.nothing") is null && HotbarSlot.Parse("transition:Teleport") is null && HotbarSlot.Parse("tool:") is null
               && HotbarSlot.Parse("Whip Pan") is null, "what this HandPeg does not know is not read");
        var odd = HotbarSlot.FromSettings(["transition:Spin", "effect:builtin.nothing"]);
        expect(odd[0] == new HotbarSlot(HotbarKind.Transition, "Spin") && odd[1] == slots[1] && odd[4] == slots[4],
            "a slot that cannot be read, or is missing, holds its default; the others what was chosen");
        expect(EffectRegistry.All.Where(d => !d.Pinned).All(d => HotbarSlot.Parse(new HotbarSlot(HotbarKind.Effect, d.Type).ToString()) is not null)
               && TransitionStyles.All.All(t => HotbarSlot.Parse(new HotbarSlot(HotbarKind.Transition, t.ToString()).ToString()) is not null),
            "every effect card and every transition can go in a slot");
    }

    private static void Presses(Action<bool, string> expect)
    {
        var voiceTrack = new TimelineTrackModel { Kind = TimelineTrackKind.Audio };
        voiceTrack.Clips.Add(new TimelineClip { ContentKind = LayerKind.Audio, SourcePath = Voice, AudioStreamIndex = 0, Start = 0, End = 6, SourceDuration = 10 });
        var vm = DogfoodTests.Open(DogfoodTests.Lane(DogfoodTests.FileClip(Overlay, 1, 7)), voiceTrack);
        var main = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        var overlay = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        var voice = vm.ViewOf(vm.Document.Tracks[2].Clips[0])!;
        voice.HasAudio = true;
        var slots = HotbarSlot.FromSettings(null);

        // Motion Blur on two clips: a card on each, one step.
        expect(vm.ApplyHotbarSlot(slots[0], [main, overlay]), "Motion Blur goes on the two selected clips");
        expect(main.Find(EffectRegistry.MotionBlur) is { Enabled: true } && overlay.Find(EffectRegistry.MotionBlur) is { Enabled: true }, "an ordinary card on each, to tune");
        vm.Undo();
        main = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        overlay = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        expect(main.Find(EffectRegistry.MotionBlur) is null && overlay.Find(EffectRegistry.MotionBlur) is null, "one Undo takes both away");
        expect(!vm.ApplyHotbarSlot(slots[2], [vm.ViewOf(vm.Document.Tracks[2].Clips[0])!]), "the Neon Rim refuses a clip with no picture");

        // The Whip Pan as a fade in: one of the default length where there was none.
        var model = vm.Document.Tracks[1].Clips[0];
        expect(model.FadeInSeconds < 0.01, "the overlay starts with no fade in");
        expect(vm.ApplyHotbarSlot(slots[3], [overlay]), "the Whip Pan goes on it");
        model = vm.Document.Tracks[1].Clips[0];
        expect(model.FadeInStyle == TransitionStyle.WhipPan && Math.Abs(model.FadeInSeconds - AppSettings.Current.DefaultTransitionSeconds) < 1e-6,
            "as its fade in, of the default transition length");
        expect(vm.ApplyHotbarSlot(new HotbarSlot(HotbarKind.Transition, nameof(TransitionStyle.Spin)), [overlay]), "a Spin chosen in the slot instead");
        model = vm.Document.Tracks[1].Clips[0];
        expect(model.FadeInStyle == TransitionStyle.Spin && Math.Abs(model.FadeInSeconds - AppSettings.Current.DefaultTransitionSeconds) < 1e-6, "the fade keeps its length, its style changed");
        vm.Undo();
        vm.Undo();
        model = vm.Document.Tracks[1].Clips[0];
        expect(model.FadeInSeconds < 0.01 && model.FadeInStyle != TransitionStyle.WhipPan, "two Undos, no fade again");

        // Studio Voice on the voice's sound: switched on, its mix as it was.
        voice = vm.ViewOf(vm.Document.Tracks[2].Clips[0])!;
        voice.HasAudio = true;
        expect(vm.ApplyHotbarSlot(slots[4], [voice]), "Studio Voice goes on a clip with sound");
        expect(voice.AudioFilters is { StudioVoice: true, StudioVoiceMix: 85 }, "switched on in its filters, at its usual mix");
        expect(!vm.ApplyHotbarSlot(slots[4], [voice]), "pressed again it says it is on already");
        vm.Undo();
        voice = vm.ViewOf(vm.Document.Tracks[2].Clips[0])!;
        expect(voice.AudioFilters is not { StudioVoice: true }, "one Undo switches it off");

        // The Fly-In: keyframes on the one clip, from the playhead.
        overlay = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        (overlay.SizeWidth, overlay.SizeHeight) = (0.2, 0.2);
        vm.PositionMs = 2000;
        expect(vm.ApplyHotbarSlot(slots[1], [overlay, main]), "the 3D Fly-In goes on the first clip");
        overlay = vm.ViewOf(vm.Document.Tracks[1].Clips[0])!;
        main = vm.ViewOf(vm.Document.Tracks[0].Clips[0])!;
        expect(overlay.GetKeys(KeyProperty.Scale).Count == 4 && main.GetKeys(KeyProperty.Scale).Count == 0, "as keyframes on it alone: never two clips flying at once");
        expect(!vm.ApplyHotbarSlot(new HotbarSlot(HotbarKind.GamePreset, "No Such Preset"), [main]), "a Game Preset the Styles folder lacks puts nothing on");
    }
}
