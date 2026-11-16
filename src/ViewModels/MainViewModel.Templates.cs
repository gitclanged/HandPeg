using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Templates on clips -----
// A template (Game Preset placement, look, blur fill, Cutouts) belongs to one base clip, never a track or media file. Its Cutouts are
// children (ParentClipId) spanning the clip, so they split, trim, move and delete with it; any clip may have its own. A clip
// dropped onto a template's track inherits the neighbouring clip's template with its own Cutouts.
//
// A picture Cutout (a Game Preset LayerKind.Video entry) becomes a VideoFile clip of the base clip's file on its clock, cropped to the
// same region and shaped by the same mask. Presets apply to TemplateTargets (selected videos, else all top-level videos); with no
// clip, to the next opened file (PlaceOpenedFile).
public partial class MainViewModel
{
    /// <summary>Template payload: look (null keeps the clip's), frame placement, blur fill and children (Cutouts etc.), lowest first.</summary>
    /// <param name="Width">Clip width as a fraction of frame width.</param>
    /// <param name="MiddleX">Centre X as a fraction of frame width.</param>
    /// <param name="MiddleY">Centre Y as a fraction of frame height.</param>
    private sealed record ClipTemplate(LayerState? Look, double Width, double MiddleX, double MiddleY, bool BlurFilled, IReadOnlyList<TemplatePiece> Pieces);

    /// <param name="Under">Placed below the base clip's track.</param>
    /// <param name="Offset">Start offset from the base clip start (s).</param>
    /// <param name="Length">Duration (s); 0 = the base clip's full length.</param>
    /// <param name="FollowsBase">Plays the base clip's file on its clock (a file Cutout).</param>
    private sealed record TemplatePiece(LayerState State, bool Under, double Offset, double Length, bool FollowsBase);

    /// <summary>Window-provided Timeline selection for preset targeting (empty when nothing is selected).</summary>
    public Func<IReadOnlyList<Layer>>? TimelineSelection { get; set; }

    /// <summary>A clip's visual template pieces (<see cref="IsPiece"/>): drawn children, excluding stream-lane audio and tethered clips.</summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip)> TemplateChildrenOf(TimelineClip clip) =>
        Document.ChildrenOf(clip).Where(c => c.Track.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle) && IsPiece(c.Clip)).ToList();

    /// <summary>
    /// Whether a child is a template piece: every child except Timeline-tethered ones (<see cref="TimelineClip.Tethered"/>). Tethered
    /// children follow the parent in time but are never deleted, copied or moved by presets/Cutouts on it, and a preset chosen with
    /// one selected targets the tethered clip itself. Lane position never changes the classification.
    /// </summary>
    private static bool IsPiece(TimelineClip child) => !child.Tethered;

    /// <summary>Whether a clip can be a template base (a file video).</summary>
    private static bool CanBaseTemplate(TimelineClip clip) => clip.ContentKind == LayerKind.VideoFile;

    /// <summary>
    /// Preset targets for a selection: selected videos, and for selected Cutouts (template pieces) their base clip; excludes audio,
    /// captions and tethered pictures; deduplicated.
    /// </summary>
    public IReadOnlyList<Layer> GamePresetTargets(IEnumerable<Layer> selected)
    {
        var targets = new List<Layer>();
        foreach (var layer in selected)
        {
            var clip = ClipOf(layer);
            for (var depth = 0; depth < 64 && clip is { ParentClipId: { } parent } && (!CanBaseTemplate(clip) || IsCutout(clip)) && IsPiece(clip)
                 && Document.FindClip(parent) is { } over; depth++)
                clip = over;

            if (clip is not null && CanBaseTemplate(clip) && !IsCutout(clip) && ViewOf(clip) is { } view && !targets.Contains(view))
                targets.Add(view);
        }

        return targets;
    }

    private static ClipTemplate? TemplateFrom(StylePreset style) => style.Layout is { } layout
        ? TemplateOf(layout.Layers, layout.MainVideoIndex, layout.MainLayer, layout.CenterZoom, layout.CenterOffsetX, layout.CenterOffsetY, style.Blur is not null && !layout.BackgroundHidden)
        : null;

    /// <summary>
    /// Converts a style/preset layout to a template: layers become pieces (below-video ones under the clip's track), MainLayer becomes
    /// the clip look, and legacy Center values (centred width plus offsets) become the placement.
    /// </summary>
    private static ClipTemplate TemplateOf(IReadOnlyList<LayerState>? saved, int? videoIndex, LayerState? look, double zoom, double offsetX, double offsetY, bool blurFilled)
    {
        var layers = saved ?? [];
        var under = Math.Max(videoIndex ?? layers.Count, 0);
        var pieces = new List<TemplatePiece>(layers.Count);
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i] is { Kind: not LayerKind.Audio, IsEmptyTrack: false } state)
                pieces.Add(new TemplatePiece(state, i < under, Math.Max(state.StartTime, 0), state.Duration > 0.001 ? state.Duration : 0, FollowsBase: false));
        }

        var width = zoom > 0 ? Math.Clamp(zoom, SmallestTemplateWidth, LargestTemplateWidth) : 1;
        return new ClipTemplate(look, width, 0.5 + Math.Clamp(offsetX, -100, 100) / 100, 0.5 + Math.Clamp(offsetY, -100, 100) / 100, blurFilled, pieces);
    }

    // Template clip width bounds as frame-width fractions (the legacy Center zoom range).
    private const double SmallestTemplateWidth = 0.1, LargestTemplateWidth = 4;

    /// <summary>A clip's template (inherited by clips landing beside it); null without pieces.</summary>
    private ClipTemplate? TemplateFrom(TimelineClip source)
    {
        if (ViewOf(source) is not { } view || Document.TrackOf(source) is not { } track || TemplateChildrenOf(source) is not { Count: > 0 } children)
            return null;

        var place = Document.Tracks.IndexOf(track);
        var pieces = children.OrderBy(c => Document.Tracks.IndexOf(c.Track)).Select(c =>
        {
            var whole = c.Clip.Start <= source.Start + 0.001 && c.Clip.End >= source.End - 0.001;
            var ownFile = c.Clip.ContentKind == LayerKind.VideoFile && string.Equals(c.Clip.SourcePath, source.SourcePath, StringComparison.OrdinalIgnoreCase);
            return new TemplatePiece(c.Clip.State, Document.Tracks.IndexOf(c.Track) < place, whole ? 0 : c.Clip.Start - source.Start, whole ? 0 : c.Clip.Duration, ownFile);
        }).ToList();

        var (x, y, width, height) = view.GetOutputRect(Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2), SourceWidth, SourceHeight);
        var (frameWidth, frameHeight) = ((double)Math.Max(FrameWidth, 2), (double)Math.Max(FrameHeight, 2));
        return new ClipTemplate(view.ToState(), view.SizeWidth, (x + width / 2.0) / frameWidth, (y + height / 2.0) / frameHeight, IsBlurFilled(view), pieces);
    }

    /// <summary>
    /// Converts a generic picture Cutout (Game Preset "video" cut-out) into a silent file Cutout of the base clip's file on its clock,
    /// cropped to the same region and box. Crops below <see cref="Layer.LargestCropTotal"/>'s minimum remainder are widened about the
    /// centre with the box enlarged to match (the mask still shapes it). Null when the crop leaves nothing; legacy kinds are never created.
    /// </summary>
    private LayerState? FileCutoutOf(LayerState piece, Layer target, TimelineClip clip, double start)
    {
        var (frameWidth, frameHeight) = (Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2));
        var (pictureWidth, pictureHeight) = target.ImageWidth > 0 && target.ImageHeight > 0 ? (target.ImageWidth, target.ImageHeight) : (SourceWidth, SourceHeight);
        var cut = Layer.FromState(piece, pictureWidth, pictureHeight, frameWidth, frameHeight);
        var source = cut.CroppedSource;
        if (source.Width <= 0 || source.Height <= 0)
            return null;

        var box = cut.GetOutputRect(frameWidth, frameHeight, pictureWidth, pictureHeight);
        var (perX, perY) = ((double)box.Width / frameWidth / source.Width, (double)box.Height / frameHeight / source.Height);
        var least = 1 - Layer.LargestCropTotal + 1e-6;
        static (double From, double Length) Held(double from, double length, double least)
        {
            var held = Math.Max(length, least);
            return (Math.Clamp(from - (held - length) / 2, 0, 1 - held), held);
        }

        var (left, width) = Held(source.X, source.Width, least);
        var (top, height) = Held(source.Y, source.Height, least);
        var state = TimelineTransaction.CopyOf(piece);
        state.Kind = LayerKind.VideoFile;
        (state.SourceX, state.SourceY, state.SourceWidth, state.SourceHeight) = (0, 0, 1, 1);
        (state.CropLeft, state.CropRight, state.CropTop, state.CropBottom) = (left, 1 - left - width, top, 1 - top - height);
        state.PositionX = box.X / (double)frameWidth - (source.X - left) * perX;
        state.PositionY = box.Y / (double)frameHeight - (source.Y - top) * perY;
        (state.SizeWidth, state.SizeHeight, state.LockAspectRatio) = (width * perX, height * perY, false);
        MaskStore.SpreadOverPicture(state, (cut.SourceX, cut.SourceY, cut.SourceWidth, cut.SourceHeight));
        state.MaskFrame = false;
        return OnClockOf(state, target, clip, start);
    }

    // A Cutout reads its clip's file from its own start at the clip's speed, and is always muted (the clip carries the audio).
    private static LayerState OnClockOf(LayerState state, Layer target, TimelineClip clip, double start)
    {
        (state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaDuration) = (clip.SourcePath ?? target.ImagePath, target.ImageWidth, target.ImageHeight, target.MediaDuration);
        (state.MediaOffset, state.Speed) = (clip.MediaOffset + (start - clip.Start) * clip.Speed, clip.Speed);
        (state.HasAudio, state.AudioCodec, state.AudioChannels) = (false, "", "");
        return state;
    }

    // Instantiates a template child on this clip at a time. Picture cut-outs require a file video clip; on other kinds they are omitted.
    private LayerState? StateFor(TemplatePiece piece, Layer target, TimelineClip clip, double start)
    {
        if (piece.State.Kind == LayerKind.Video)
            return clip.ContentKind == LayerKind.VideoFile ? FileCutoutOf(piece.State, target, clip, start) : null;

        var state = TimelineTransaction.CopyOf(piece.State);
        return piece.FollowsBase ? OnClockOf(state, target, clip, start) : state;
    }

    /// <summary>A Cutout Sub-Lane with room (preferring the lane of the same piece on earlier clips to keep one stack), or a new lane.</summary>
    private TimelineTrackModel LaneFor(ITimelineTransaction edit, TimelineTrackModel track, Layer view, double start, double end, bool under, ref int above)
    {
        var kind = TimelineTrackModel.KindFor(view.Kind);
        foreach (var lane in Document.ChildTracksOf(track))
        {
            if (lane.Kind == kind && lane.Clips.Any(c => c.State.Name == view.Name) && lane.ClipsIntersecting(start + 1e-6, end - 1e-6).IsEmpty)
                return lane;
        }

        var index = under ? Document.Tracks.IndexOf(track) : above;
        var made = edit.InsertTrack(index, kind);
        edit.SetTrackParent(made, track.Id);
        above = under ? above + 1 : index + 1;
        return made;
    }

    // Recreates the clip's Cutouts (replacing existing: one template per clip), or appends (added region, mask Cutouts).
    private void SpawnCutouts(ITimelineTransaction edit, TimelineClip clip, ClipTemplate template, bool replace = true)
    {
        if (Document.TrackOf(clip) is not { } track || ViewOf(clip) is not { } target)
            return;

        if (replace)
        {
            foreach (var (_, old) in TemplateChildrenOf(clip))
                edit.DeleteClip(old);
        }

        var pictures = Document.ChildTracksOf(track).Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).Select(t => Document.Tracks.IndexOf(t)).ToList();
        var above = Math.Max(Document.Tracks.IndexOf(track), pictures.Count > 0 ? pictures.Max() : 0) + 1;
        foreach (var piece in template.Pieces)
        {
            var start = clip.Start + piece.Offset;
            var end = piece.Length > 0 ? Math.Min(start + piece.Length, clip.End) : clip.End;
            if (end - start < TimelineDocument.MinimumClipSeconds)
                continue;

            if (StateFor(piece, target, clip, start) is not { } state)
                continue;
            (state.TrackId, state.IsHidden, state.StartTime, state.Duration) = (0, false, start, end - start);
            var view = Layer.FromState(state, SourceWidth, SourceHeight, FrameWidth, FrameHeight);
            var made = NewClipFor(view, start, end);
            Adopt(view, made);
            try
            {
                var lane = LaneFor(edit, track, view, start, end, piece.Under, ref above);
                edit.SetClipParent(edit.PlaceClip(lane, made, allowCrossfadeOverlap: false), clip.Id, tethered: false);
            }
            catch (TimelineEditException)
            {
                Unadopt(view);
            }
        }
    }

    /// <summary>Applies a template to one clip (look, placement, blur fill, then Cutouts over its span). No undo step (caller's).</summary>
    /// <param name="swap">Replaces the clip's preset (Track Preset Node), also removing blur fill the template lacks.</param>
    private void PutTemplateOn(Guid clipId, ClipTemplate template, string label, bool swap = false)
    {
        if (Document.FindClip(clipId) is not { } clip || Document.TrackOf(clip) is not { } track || ViewOf(clip) is not { } view)
            return;

        if (template.Look is { } look)
            TakeLook(view, clip, track, look);

        // Keep the clip's own aspect, centred on the template's centre, so other-aspect files are not distorted.
        var placing = _placingViews;
        _placingViews = true;
        try
        {
            PlaceAbout(view, template.Width, template.MiddleX, template.MiddleY);
        }
        finally
        {
            _placingViews = placing;
        }

        _placedFor.Remove(view);
        _staleLooks.Add(view);
        if (template.BlurFilled)
            view.BlurFill = true;
        else if (swap && view.BlurFill == true)
            view.BlurFill = null;

        Edit(label, edit =>
        {
            if (Document.FindClip(clipId) is { } now)
                SpawnCutouts(edit, now, template);
        }, undoable: false);
    }

    /// <summary>
    /// Applies a layout to <see cref="TemplateTargets"/>, each getting placement, look, blur fill and own Cutouts; with no targets it is
    /// pended for the next opened file (PlaceOpenedFile). No undo step (caller's).
    /// </summary>
    /// <returns>Number of clips updated.</returns>
    private int PutTemplate(ClipTemplate template, string label)
    {
        var targets = TemplateTargets().Select(v => v.ClipId).Where(id => id != Guid.Empty).ToList();
        _templateToCome = targets.Count == 0 ? template : null;
        FlushLooks();
        using var held = HoldLookSteps();
        foreach (var id in targets)
            PutTemplateOn(id, template, label);
        return targets.Count;
    }

    /// <summary>
    /// Applies a saved Game Preset to the given clips only (Timeline list with selection, clip menu). Sequence colour filters and
    /// captions are untouched; the preset's blur settings become the Sequence blur. One undo step.
    /// </summary>
    /// <returns>Whether any clip was updated.</returns>
    public bool ApplyGamePreset(IEnumerable<Layer> clips, string name)
    {
        var targets = GamePresetTargets(clips).Select(c => c.ClipId).ToList();
        if (targets.Count == 0)
        {
            StatusText = "A Game Preset goes on a video clip: select one on the Timeline first.";
            return false;
        }

        if (ReadStyle(Path.Combine(AppPaths.Styles, name + StyleFile.Extension)) is not { } style || TemplateFrom(style) is not { } template)
        {
            StatusText = $"\"{name}\" has no layout to put on a clip.";
            return false;
        }

        FlushLooks();
        var before = CaptureSnapshot();
        using var held = HoldLookSteps();
        using var whole = DeferCommand();
        _activeStyle = style;
        ExtractMasks(style);
        if (style.Layout?.Vertical is { } vertical && vertical != UseVerticalResolution)
            UseVerticalResolution = vertical;

        foreach (var id in targets)
            PutTemplateOn(id, template, $"apply {name}");
        if (style.Blur is { } blur)
            TakeBlur(blur);

        ApplyEncodingDefaultsOverStyle();
        Checkpoint($"apply {name}", before);
        StatusText = $"{name} is on {(targets.Count == 1 ? "the clip" : $"{targets.Count} clips")}, its Cutouts over {(targets.Count == 1 ? "its" : "each one's")} span. Ctrl+Z takes it away.";
        return true;
    }

    /// <summary>Nearest other clip on a track that has a template to give; null when none.</summary>
    private TimelineClip? TemplateClipOn(TimelineTrackModel? track, double at, TimelineClip? except = null)
    {
        if (track is null)
            return null;

        var (best, nearest) = ((TimelineClip?)null, double.PositiveInfinity);
        foreach (var clip in track.Clips)
        {
            if (ReferenceEquals(clip, except) || !CanBaseTemplate(clip) || TemplateChildrenOf(clip).Count == 0)
                continue;

            var distance = at < clip.Start ? clip.Start - at : at > clip.End ? at - clip.End : 0;
            if (distance < nearest)
                (best, nearest) = (clip, distance);
        }

        return best;
    }

    /// <summary>
    /// Gives a clip landing on a template track the neighbour's placement, look and its own copy of the pieces. No-op if it has pieces
    /// or the track has none to give. Part of the placing step.
    /// </summary>
    /// <param name="droppedOn">Drop row (the clip may have landed on a new track above it).</param>
    /// <param name="at">Drop time.</param>
    /// <param name="hadPreset">Source track's Game Preset on a track move; the same preset node is not re-applied.</param>
    public void InheritTemplate(Layer clip, Layer? droppedOn, double at, string? hadPreset = null)
    {
        if (ClipOf(clip) is not { } model || !CanBaseTemplate(model) || model.ParentClipId is not null || IsCutout(model))
            return;

        // Track Preset Node: replace the clip's preset and Cutouts with the track's.
        if ((NodeOf(Document.TrackOf(model)) ?? NodeOf(TrackOfRow(droppedOn))) is { } node)
        {
            if (string.Equals(node, hadPreset, StringComparison.OrdinalIgnoreCase) || PresetTemplate(node, out var style) is not { } preset)
                return;

            FlushLooks();
            using var held = HoldLookSteps();
            ExtractMasks(style!);
            PutTemplateOn(model.Id, preset, $"give {clip.Name} its track's preset {node}", swap: true);
            StatusText = $"{clip.Name} took its track's Game Preset, {node}, with Cutouts of its own. Ctrl+Z takes it back.";
            return;
        }

        if (TemplateChildrenOf(model).Count > 0)
            return;

        var from = TemplateClipOn(Document.TrackOf(model), at, model) ?? TemplateClipOn(TrackOfRow(droppedOn), at, model);
        if (from is null)
            return;

        FlushLooks();
        if (TemplateFrom(from) is { } template)
        {
            using var held = HoldLookSteps();
            PutTemplateOn(model.Id, template, $"give {clip.Name} its track's template");
        }
    }

    // ----- Track Preset Nodes -----
    // A picture track may carry a Game Preset (TimelineTrackModel.PresetNodeName): top-level videos arriving there take its placement,
    // look, blur fill and Cutouts in the arriving step (InheritTemplate), so moving between node tracks swaps presets in one undo step.
    // Setting a node applies it to current clips; clearing leaves them as-is. Templates remain per base clip; the node only selects one.

    /// <summary>A track's node Game Preset (top-level picture tracks only); null for none.</summary>
    private static string? NodeOf(TimelineTrackModel? track) =>
        track is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, ParentTrackId: null, PresetNodeName: { Length: > 0 } name } ? name : null;

    /// <summary>The row track's node Game Preset, or null.</summary>
    public string? TrackPresetOf(Layer? row) => NodeOf(TrackOfRow(row));

    /// <summary>Whether a row's track can carry a node: a top-level picture track, not a Sub-Lane.</summary>
    public bool CanHaveTrackPreset(Layer? row) => row is { IsCaptions: false, IsBackground: false } && TrackOfRow(row) is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, ParentTrackId: null };

    /// <summary>Loads a Game Preset from the Styles folder as a template; null (reason in the status bar) if it has no layout.</summary>
    private ClipTemplate? PresetTemplate(string name, out StylePreset? style)
    {
        style = ReadStyle(Path.Combine(AppPaths.Styles, name + StyleFile.Extension));
        if (style is not null && TemplateFrom(style) is { } template)
            return template;

        if (style is not null)
            StatusText = $"\"{name}\" has no layout to put on a clip.";
        return null;
    }

    /// <summary>
    /// Sets a track's Track Preset Node, applying it (with per-clip Cutouts) to its current videos; null clears the node and leaves
    /// clips unchanged. One undo step.
    /// </summary>
    /// <returns>Whether the node changed.</returns>
    public bool SetTrackPreset(Layer row, string? name)
    {
        if (!CanHaveTrackPreset(row) || TrackOfRow(row) is not { } track)
        {
            StatusText = "A Track Preset goes on a video track of its own (not a Cutout's or a sound's lane).";
            return false;
        }

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (string.Equals(track.PresetNodeName, name, StringComparison.Ordinal))
            return false;

        StylePreset? style = null;
        ClipTemplate? template = null;
        if (name is not null && (template = PresetTemplate(name, out style)) is null)
            return false;

        FlushLooks();
        var label = name is null ? $"clear the Track Preset of {track.DisplayName}" : $"set {name} as the Track Preset of {track.DisplayName}";
        var before = CaptureSnapshot();
        using var held = HoldLookSteps();
        using var whole = DeferCommand();
        if (!Edit(label, edit => edit.SetTrackPreset(track, name), undoable: false))
            return false;

        var targets = template is null ? [] : track.Clips.Where(c => CanBaseTemplate(c) && c.ParentClipId is null && !IsCutout(c)).Select(c => c.Id).ToList();
        if (style is not null && template is not null)
        {
            // As ApplyGamePreset: unpack masks, set frame shape (only when PresetMayShapeFrame allows) and the Sequence blur.
            _activeStyle = style;
            ExtractMasks(style);
            if (style.Layout?.Vertical is { } vertical && vertical != UseVerticalResolution && PresetMayShapeFrame(track, name!))
                UseVerticalResolution = vertical;
            foreach (var id in targets)
                PutTemplateOn(id, template, label, swap: true);
            if (style.Blur is { } blur && targets.Count > 0)
                TakeBlur(blur);
            ApplyEncodingDefaultsOverStyle();
        }

        Checkpoint(label, before);
        StatusText = name is null
            ? $"{track.DisplayName} gives its clips no Game Preset now; they keep the ones they have. Ctrl+Z puts the preset back."
            : targets.Count == 0
                ? $"{track.DisplayName}'s Track Preset is {name}: every video dropped or moved onto it takes {name}, with Cutouts of its own."
                : $"{track.DisplayName}'s Track Preset is {name}: on its {(targets.Count == 1 ? "clip" : $"{targets.Count} clips")} now, and on every video dropped or moved onto it. Ctrl+Z takes it back.";
        return true;
    }

    /// <summary>
    /// Whether a Track Preset may reshape the Sequence frame: never if another track has a different preset; otherwise if no other
    /// track holds clips (audio counts; this track's Sub-Lanes do not), or the frame is still default 16:9 with no other presets.
    /// The rest of the preset applies regardless.
    /// </summary>
    private bool PresetMayShapeFrame(TimelineTrackModel track, string name)
    {
        var others = Document.Tracks.Where(t => !ReferenceEquals(t, track) && t.ParentTrackId is null).ToList();
        if (others.Exists(t => NodeOf(t) is { } other && !string.Equals(other, name, StringComparison.Ordinal)))
            return false;
        if (!others.Exists(t => t.Clips.Count > 0))
            return true;

        var atDefaultShape = !UseVerticalResolution && FrameHeight > 0 && Math.Abs((double)FrameWidth / FrameHeight - 16.0 / 9) < 0.01;
        return atDefaultShape && !others.Exists(t => NodeOf(t) is not null);
    }
}
