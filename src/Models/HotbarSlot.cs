using HandPegApp.Models.Effects;

namespace HandPegApp.Models;

/// <summary>What a slot of the Creator Hotbar puts on the selected clips (Pass 5.2.1).</summary>
public enum HotbarKind
{
    /// <summary>An effect card (<see cref="EffectRegistry"/>, by its type), with its starting values.</summary>
    Effect,

    /// <summary>A <see cref="TransitionStyle"/> (by name) for the clip's fade in, a fade of the default length where it has none.</summary>
    Transition,

    /// <summary>A Game Preset of the Styles folder (by name).</summary>
    GamePreset,

    /// <summary>A one-click tool of the Timeline's menu: <see cref="HotbarSlot.FlyIn"/> or <see cref="HotbarSlot.StudioVoice"/>.</summary>
    Tool,
}

/// <summary>
/// One slot of the Creator Hotbar on the Timeline's row of tools (5.2.1): an Effect, a Transition, a Game Preset or a one-click
/// tool, kept in the settings as "kind:id" (<see cref="ToString"/>, <see cref="Parse"/>). What it puts on a clip is ordinary: an
/// effect card, a fade, a Game Preset's place and Cutouts, keyframes, Studio Voice switched on; each one step of Undo.
/// </summary>
public readonly record struct HotbarSlot(HotbarKind Kind, string Id)
{
    /// <summary>The 3D Fly-In + Shockwave Impact (MainViewModel.FlyInWithShockwave).</summary>
    public const string FlyIn = "flyin";

    /// <summary>Studio Voice switched on for the clip's sound (its streams' clips for a video).</summary>
    public const string StudioVoice = "studiovoice";

    public const int Count = 5;

    /// <summary>The slots a new install starts with: Motion Blur, the 3D Fly-In, the Neon Rim, the Whip Pan and Studio Voice.</summary>
    public static IReadOnlyList<string> Defaults { get; } =
        [$"effect:{EffectRegistry.MotionBlur}", $"tool:{FlyIn}", $"effect:{EffectRegistry.NeonRim}", $"transition:{nameof(TransitionStyle.WhipPan)}", $"tool:{StudioVoice}"];

    /// <summary>The one-click tools a slot can hold, with their names.</summary>
    public static IReadOnlyList<(string Id, string Label)> Tools { get; } = [(FlyIn, "3D Fly-In"), (StudioVoice, "Studio Voice")];

    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Id}";

    /// <summary>A slot from its settings text; null for one HandPeg does not know (an effect, a style or a tool of another version).</summary>
    public static HotbarSlot? Parse(string? text)
    {
        var colon = text?.IndexOf(':') ?? -1;
        if (text is null || colon < 1 || colon == text.Length - 1)
            return null;

        var (kind, id) = (text[..colon], text[(colon + 1)..]);
        return kind switch
        {
            "effect" when EffectRegistry.Find(id) is { Pinned: false } => new HotbarSlot(HotbarKind.Effect, id),
            "transition" when Enum.TryParse<TransitionStyle>(id, out var style) && Enum.IsDefined(style) => new HotbarSlot(HotbarKind.Transition, style.ToString()),
            "gamepreset" => new HotbarSlot(HotbarKind.GamePreset, id),
            "tool" when Tools.Any(t => t.Id == id) => new HotbarSlot(HotbarKind.Tool, id),
            _ => null,
        };
    }

    /// <summary>The five slots as the settings keep them (null: <see cref="Defaults"/>), each the default where it cannot be read.</summary>
    public static IReadOnlyList<HotbarSlot> FromSettings(IReadOnlyList<string>? kept)
    {
        var slots = new HotbarSlot[Count];
        for (var i = 0; i < Count; i++)
            slots[i] = Parse(kept is not null && i < kept.Count ? kept[i] : null) ?? Parse(Defaults[i])!.Value;
        return slots;
    }

    /// <summary>Its name on its button.</summary>
    public string Label => Kind switch
    {
        HotbarKind.Effect when Id == EffectRegistry.NeonRim => "Neon Rim",
        HotbarKind.Effect => EffectRegistry.Find(Id)?.Name ?? Id,
        HotbarKind.Transition when Enum.TryParse<TransitionStyle>(Id, out var style) => TransitionStyles.Label(style),
        HotbarKind.Tool => ToolLabel(Id),
        _ => Id,
    };

    private static string ToolLabel(string id) => Tools.FirstOrDefault(t => t.Id == id).Label ?? id;

    /// <summary>What it does, for its tooltip.</summary>
    public string About => Kind switch
    {
        HotbarKind.Effect => $"Adds {Label} (an effect card with its starting values) to the selected clips, else the video clip at the playhead.",
        HotbarKind.Transition => $"Makes the selected clips' fade in a {Label} (a fade of the default transition length where a clip has none), else the video clip at the playhead's.",
        HotbarKind.GamePreset => $"Puts the Game Preset \"{Label}\" on the selected video clips, else the video clip at the playhead: its place, look, blur fill and Cutouts.",
        _ when Id == FlyIn => "From the playhead the selected clip (else the video clip at the playhead) flies out to the middle of the frame and lands with a shockwave: the 3D Fly-In + Shockwave Impact, as keyframes and an Impact clip.",
        _ => "Switches Studio Voice (the one-click vocal chain) on for the selected clips' sound, else the video clip at the playhead's: a video's is its streams' clips.",
    };
}
