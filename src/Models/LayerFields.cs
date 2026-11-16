using System.Collections.Frozen;

namespace HandPegApp.Models;

/// <summary>Who owns a value of a clip's view (<see cref="Layer"/>).</summary>
public enum FieldOwner
{
    /// <summary>Look value (LayerState): edited on the view, flushed to the clip (FlushLooks), undoable as a look step.</summary>
    Look,

    /// <summary>Clip-owned timing/switches (TimelineClip): view changes become document edits.</summary>
    Clip,

    /// <summary>The view's own state (what it shows, what is being done with it): never the clip's.</summary>
    View,

    /// <summary>What probing the file found (its kind, picture size, audio): never set by hand.</summary>
    Probe,

    /// <summary>Alias of a value owned elsewhere (box percent, Audio-tab row name); setting it sets the owner, whose change is authoritative.</summary>
    Alias,
}

/// <summary>
/// Single ownership table classifying each view property as clip-owned, look, view-only or probe-derived. Read by NoteViewChange and
/// look-undo step selection; golden layer-field-ownership verifies every Layer property is classified here.
/// </summary>
public static class LayerFields
{
    /// <summary>The clip's own values, by their names on the view.</summary>
    public static readonly FrozenSet<string> Clip = new[]
    {
        nameof(Layer.StartTime), nameof(Layer.Duration), nameof(Layer.MediaOffset), nameof(Layer.Speed), nameof(Layer.FadeInSeconds),
        nameof(Layer.FadeOutSeconds), nameof(Layer.FadeInStyle), nameof(Layer.FadeOutStyle), nameof(Layer.TransitionIn), nameof(Layer.IsHidden),
        nameof(Layer.IsSoundMuted), nameof(Layer.AudioGainDb), nameof(Layer.AudioOffset), nameof(Layer.ProxyChoice),
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The view's own values: what it shows and what is being done with it, and what ties it to its clip.</summary>
    public static readonly FrozenSet<string> View = new[]
    {
        nameof(Layer.IsLoading), nameof(Layer.TrackId), nameof(Layer.IsDeleted),
        nameof(Layer.MediaDuration), nameof(Layer.IsEmptyTrack), nameof(Layer.ClipId), nameof(Layer.SpeedEditor), nameof(Layer.AnchorCenter),
        nameof(Layer.PictureFadeInOff), nameof(Layer.PictureFadeOutOff),
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Alias properties (box percent editors, Audio tab row names; <see cref="FieldOwner.Alias"/>).</summary>
    public static readonly FrozenSet<string> Alias = new[]
    {
        nameof(Layer.AnchorXPercent), nameof(Layer.AnchorYPercent), nameof(Layer.PositionXPercent), nameof(Layer.PositionYPercent),
        nameof(Layer.SizePercent), nameof(Layer.SizeHeightPercent), nameof(Layer.CropLeftPercent), nameof(Layer.CropTopPercent),
        nameof(Layer.CropRightPercent), nameof(Layer.CropBottomPercent), nameof(Layer.ClipSpeed), nameof(Layer.Title), nameof(Layer.Action),
        nameof(Layer.IsMuted), nameof(Layer.GainDb), nameof(Layer.Codec), nameof(Layer.Bitrate),
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What a file is, found by probing it and never set by hand.</summary>
    public static readonly FrozenSet<string> Probe = new[]
    {
        nameof(LayerState.Kind), nameof(LayerState.ImagePath), nameof(LayerState.ImageWidth), nameof(LayerState.ImageHeight),
        nameof(LayerState.HasAudio), nameof(LayerState.AudioCodec), nameof(LayerState.AudioChannels),
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Who owns the value of this name (anything not listed is the clip's look).</summary>
    public static FieldOwner Owner(string name) =>
        Clip.Contains(name) ? FieldOwner.Clip : View.Contains(name) ? FieldOwner.View : Probe.Contains(name) ? FieldOwner.Probe
        : Alias.Contains(name) ? FieldOwner.Alias : FieldOwner.Look;

    /// <summary>
    /// Look properties tracked for undo (writable LayerState look properties). Same-named view properties record a step before changing,
    /// so each Layer look property needs a matching LayerState property (golden-enforced).
    /// </summary>
    public static FrozenSet<string> UndoableLooks => Looks.Value;

    private static readonly Lazy<FrozenSet<string>> Looks = new(() =>
        typeof(LayerState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && Owner(p.Name) == FieldOwner.Look).Select(p => p.Name).ToFrozenSet(StringComparer.Ordinal));
}
