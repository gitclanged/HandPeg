using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.ViewModels;

// ----- The Creator Hotbar (Pass 5.2.1) -----
// Five slots on the Timeline's row of tools, each an Effect, a Transition, a Game Preset or a one-click tool (Models.HotbarSlot), put
// on the clips the window hands over (the selected ones, else the video clip at the playhead). What each makes is ordinary (an
// effect card, a fade, a Game Preset's place and Cutouts, keyframes, Studio Voice switched on), and one press is one step of Undo.
public partial class MainViewModel
{
    /// <summary>Puts what a slot holds on these clips; whether it went on any. The status says what was done, or why nothing was.</summary>
    public bool ApplyHotbarSlot(HotbarSlot slot, IReadOnlyList<Layer> clips)
    {
        switch (slot.Kind)
        {
            case HotbarKind.Effect when EffectRegistry.Find(slot.Id) is { Pinned: false } definition:
                {
                    var targets = clips.Where(c => c.HasFilters && Layers.Contains(c)).ToList();
                    if (targets.Count == 0)
                        return Refused($"{definition.Name} goes on a clip with a picture: select one on the Timeline first.");

                    Checkpoint($"add {definition.Name} to {Named(targets)}");
                    using (HoldLookSteps())
                    {
                        foreach (var clip in targets)
                            PutEffectOn(clip, definition);
                    }

                    ShowEffectsPane = true;
                    StatusText = $"{definition.Name} added to {Named(targets)}. Ctrl+Z takes it off.";
                    Log($"Creator Hotbar: {definition.Name} on {Named(targets)}");
                    return true;
                }

            case HotbarKind.Transition when Enum.TryParse<TransitionStyle>(slot.Id, out var style):
                {
                    var targets = clips.Where(c => c.IsPicture && !c.IsCaptions).Select(c => (View: c, Model: ClipOf(c))).Where(t => t.Model is not null).ToList();
                    if (targets.Count == 0)
                        return Refused($"A {TransitionStyles.Label(style)} goes on a clip with a picture: select one on the Timeline first.");

                    var views = targets.Select(t => t.View).ToList();
                    if (!Edit($"{TransitionStyles.Label(style)} for the fade in of {Named(views)}", edit =>
                        {
                            foreach (var (_, model) in targets)
                            {
                                var seconds = model!.FadeInSeconds >= 0.01 ? model.FadeInSeconds : DefaultFade(model.Duration - model.FadeOutSeconds);
                                edit.SetClipFades(model, seconds, model.FadeOutSeconds, style, null);
                            }
                        }))
                        return false;

                    StatusText = $"{Named(views)}: fade in, {TransitionStyles.Label(style)}. Ctrl+Z takes it back.";
                    return true;
                }

            case HotbarKind.GamePreset:
                return ApplyGamePreset(clips, slot.Id);

            // The Fly-In is placed from the playhead: on the clip the window names first, never several flying at once.
            case HotbarKind.Tool when slot.Id == HotbarSlot.FlyIn:
                return clips.FirstOrDefault(c => c.HasFilters && Layers.Contains(c)) is { } flown
                    ? FlyInWithShockwave(flown)
                    : Refused("The Fly-In is for a clip with a picture: select one on the Timeline first.");

            case HotbarKind.Tool when slot.Id == HotbarSlot.StudioVoice:
                return StudioVoiceOn(clips);
        }

        return Refused($"\"{slot.Label}\" is not something this HandPeg can put on a clip: right-click the slot to choose another.");
    }

    /// <summary>
    /// Studio Voice switched on for the sound of these clips (a clip with sound of its own; a video's, its streams' clips on its
    /// Sub-Lanes), its mix as it was; one step of Undo. Whether any was switched on.
    /// </summary>
    private bool StudioVoiceOn(IReadOnlyList<Layer> clips)
    {
        var sounds = new List<Layer>();
        foreach (var clip in clips)
        {
            if (clip.CarriesSound && clip.IsAudio)
            {
                sounds.Add(clip);
                continue;
            }

            if (ClipOf(clip) is not { } model)
                continue;
            var streams = Document.Tracks.SelectMany(t => t.Clips).Where(c => c.ParentClipId == model.Id).Select(ViewOf).OfType<Layer>().Where(l => l.CarriesSound).ToList();
            sounds.AddRange(streams.Count > 0 ? streams : clip.CarriesSound ? [clip] : []);
        }

        sounds = [.. sounds.Distinct().Where(s => s.AudioFilters is not { StudioVoice: true })];
        if (sounds.Count == 0)
            return Refused(clips.Any(c => c.CarriesSound) ? "Studio Voice is already on for the selected clips' sound." : "Studio Voice goes on a clip with sound: select one on the Timeline first.");

        FlushLooks();
        Checkpoint($"switch Studio Voice on for {Named(sounds)}");
        using (HoldLookSteps())
        {
            foreach (var sound in sounds)
            {
                var filters = sound.AudioFilters?.Clone() ?? new TrackAudioFilters();
                filters.StudioVoice = true;
                sound.AudioFilters = filters;
            }
        }

        StatusText = $"Studio Voice is on for {Named(sounds)}. Ctrl+Z takes it off.";
        Log($"Creator Hotbar: Studio Voice on {Named(sounds)}");
        return true;
    }

    private bool Refused(string why)
    {
        StatusText = why;
        return false;
    }

    private static string Named(IReadOnlyList<Layer> clips) => clips.Count == 1 ? clips[0].Name : $"{clips.Count} clips";
}
