using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Pass 5.1-GpuFx: one click, and nothing locked: what these make is ordinary clips, cards and keyframes -----
public partial class MainViewModel
{
    /// <summary>
    /// Drops a procedural effect at the playhead: an adjustment clip of its own on a new track, with the card on it, as long as the effect runs (the Explosion's 1.6 s, 5 s
    /// for what stays on). An adjustment clip works on everything under it, so its Light Spill falls on the video. Moved, trimmed and tuned as any clip is. One step of Undo.
    /// </summary>
    public Layer? AddVfxClip(string type)
    {
        if (EffectRegistry.Find(type) is not { } definition)
            return null;
        if (!HasSource)
        {
            StatusText = $"Load a video first: {definition.Name} goes on at the playhead.";
            return null;
        }

        var card = definition.NewCard();
        var runs = definition.Params.FirstOrDefault(p => p.GpuProgress);
        var seconds = runs is { Key: "duration" } ? card.Get(runs.Key) : 5;
        var clip = VfxClip(definition.Name, card);
        var start = Math.Clamp(PositionMs / 1000, 0, Math.Max(SequenceSeconds - 0.1, 0));
        if (!PlaceLayer(clip, start, start + seconds, $"add {definition.Name}"))
            return null;

        KeyLayer = clip;
        ShowEffectsPane = true;
        StatusText = $"{definition.Name} added at the playhead: its card is in the Effects pane, and the clip moves and trims as any other.";
        return clip;
    }

    private Layer VfxClip(string name, EffectInstance card)
    {
        var clip = new Layer { Kind = LayerKind.Adjustment, Name = name, PositionX = 0, PositionY = 0, SizeWidth = 1, SizeHeight = 1, LockAspectRatio = false };
        clip.AddEffect(card);
        return clip;
    }

    /// <summary>The Fly-In's beats, in seconds: out to the middle, held on the impact, and home.</summary>
    public const double FlyOutSeconds = 0.42, ImpactHoldSeconds = 0.1, FlyBackSeconds = 0.58;

    /// <summary>How long the impact's clip lasts: the Shockwave's own run.</summary>
    public const double ImpactSeconds = 0.9;

    /// <summary>
    /// The 3D Fly-In + Shockwave Impact: from the playhead the clip flies out to the middle of the frame, growing and tilting toward the viewer, lands with a Shockwave and a camera
    /// shake centred on it, and glides back to where it was. All of it ordinary: keyframes on the clip's X, Y, Scale and Rotation (what it had elsewhere is kept), and an
    /// adjustment clip with an Impact Shockwave card at the moment it lands. One step of Undo.
    /// </summary>
    public bool FlyInWithShockwave(Layer layer)
    {
        if (!layer.HasFilters || layer.IsAdjustment || layer.IsBackground || layer.IsCaptions)
        {
            StatusText = "The Fly-In is for a clip with a picture: an overlay, a cut-out, a video.";
            return false;
        }

        if (EffectRegistry.Find(EffectRegistry.Shockwave) is not { } shockwave)
            return false;

        // From the playhead when it is on the clip, else from the clip's start; a clip too short for all of it has the beats shortened to fit.
        var clipEnd = layer.StartTime + Math.Max(layer.Duration, 0);
        var begin = Math.Clamp(PositionMs / 1000, layer.StartTime, Math.Max(clipEnd, layer.StartTime));
        var whole = FlyOutSeconds + ImpactHoldSeconds + FlyBackSeconds;
        var left = clipEnd - begin;
        if (left < 0.3 && layer.Duration >= 0.3)
        {
            begin = Math.Max(layer.StartTime, clipEnd - whole);
            left = clipEnd - begin;
        }

        if (left < 0.3)
        {
            StatusText = "The Fly-In needs at least 0.3 s of the clip after the playhead.";
            return false;
        }

        var fit = Math.Min(left / whole, 1);
        var from = begin - layer.StartTime;
        var (impact, leaves, home) = (from + FlyOutSeconds * fit, from + (FlyOutSeconds + ImpactHoldSeconds) * fit, from + whole * fit);

        var width = Math.Max(layer.Evaluate(KeyProperty.Scale, from), 0.01);
        var height = width * (layer.SizeWidth > 1e-6 ? layer.SizeHeight / layer.SizeWidth : 1);
        var peak = width >= 0.85 ? width * 1.08 : Math.Min(width * 2, 0.85);
        var peakHeight = height * peak / width;
        var (x, y) = (0.5 - peak / 2, 0.5 - peakHeight / 2);

        Checkpoint($"fly {layer.Name} in with a shockwave");
        using (DeferCommand())
        {
            using (Layer.Echo())
            {
                Beat(KeyProperty.X, x);
                Beat(KeyProperty.Y, y);
                Beat(KeyProperty.Scale, peak);
                Beat(KeyProperty.Rotation, layer.Evaluate(KeyProperty.Rotation, from) + 7);
            }

            var card = shockwave.NewCard();
            var at = begin + FlyOutSeconds * fit;
            var clip = VfxClip($"Impact ({layer.Name})", card);
            Edit($"fly {layer.Name} in with a shockwave", edit => PlaceNew(edit, clip, at, at + ImpactSeconds, null, joins: false), undoable: false);
        }

        ApplyKeyframes();
        StatusText = $"{layer.Name} flies in and lands with a shockwave: its keyframes and the Impact clip are yours to change. Ctrl+Z takes it all back.";
        return true;

        // One property's four beats, in place of any keyframes it had in that stretch; each leaves as the beat calls for.
        void Beat(KeyProperty property, double there)
        {
            var (rest, restAfter) = (layer.Evaluate(property, from), layer.Evaluate(property, home));
            var keys = layer.GetKeys(property).Where(k => k.Time < from - Layer.KeyTolerance || k.Time > home + Layer.KeyTolerance).ToList();
            keys.Add(new Keyframe(from, rest, EasingType.Back));
            keys.Add(new Keyframe(impact, there, EasingType.Hold));
            keys.Add(new Keyframe(leaves, there, EasingType.EaseInOut));
            keys.Add(new Keyframe(home, restAfter));
            layer.SetKeys(property, keys.OrderBy(k => k.Time));
        }
    }
}
