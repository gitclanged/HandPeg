using System.Collections.Frozen;

namespace HandPegApp.Models;

/// <summary>Who owns a value of a clip's view (<see cref="Layer"/>).</summary>
public enum FieldOwner
{
    /// <summary>The clip's look (its LayerState): changed on the view, then written into the clip (FlushLooks); undoable as a look.</summary>
    Look,

    /// <summary>The clip's own timing and switches (the TimelineClip): a change on the view is a request, made an edit of the document.</summary>
    Clip,

    /// <summary>The view's own state (what it shows, what is being done with it): never the clip's.</summary>
    View,

    /// <summary>What probing the file found (its kind, picture size, audio): never set by hand.</summary>
    Probe,

    /// <summary>Another name for a value owned elsewhere (a box's percent, an Audio-tab row's name): setting it sets that value, whose own
    /// change is the one that counts.</summary>
    Alias,
}

/// <summary>
/// The single table of which values of a view are its clip's own, its look, its view-only state or what probing found. The view model's
/// sync (NoteViewChange) and its undo looks (the look names a view's PropertyChanging takes a step for) both read it, and the
/// layer-field-ownership golden checks every Layer property against it, so a new property is classified once, here.
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

    /// <summary>Other names for values owned elsewhere: the boxes' percent editors and the Audio tab's row names (see <see cref="FieldOwner.Alias"/>).</summary>
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
    /// The look values a view's undo steps follow: LayerState's writable properties that are the clip's look. A view's property of the
    /// same name takes a step before it changes; a Layer look property must therefore have a LayerState property of its name (the golden).
    /// </summary>
    public static FrozenSet<string> UndoableLooks => Looks.Value;

    private static readonly Lazy<FrozenSet<string>> Looks = new(() =>
        typeof(LayerState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && Owner(p.Name) == FieldOwner.Look).Select(p => p.Name).ToFrozenSet(StringComparer.Ordinal));
}
