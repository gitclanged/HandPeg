using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Templates on clips (5.1-DogfoodPolish, the user's rule) -----
// A template (a Game Preset's place, look, blur fill and Cutouts) belongs to one base clip: never to a track or a media file. Its
// Cutouts are children of that clip (ParentClipId) over that clip's span, so they split, trim, move and go with it, and any clip can have
// one of its own: the 1st, 2nd or Nth video on the timeline. A clip dropped or dragged onto a template's track takes the template of the
// clip it landed by, with a set of Cutouts of its own.
//
// A Cutout cut from the picture (a Game Preset file's LayerKind.Video entry, saved as a part of "the video") is a VideoFile clip of its
// clip's own file, on its clock (5.2-AuditB: every clip reads its own file), cut to the same part of the picture by its crop and shaped
// by the same mask. A Game Preset or a preset goes on the selected video clips, or with none selected on every top-level video clip
// (TemplateTargets); chosen before any clip, on the next file opened (PlaceOpenedFile).
public partial class MainViewModel
{
    /// <summary>What a template puts on a clip: its look (null leaves the clip's), its place on the frame, its blur fill, and its Cutouts (and other children), lowest first.</summary>
    /// <param name="Width">The clip's width as a fraction of the frame's.</param>
    /// <param name="MiddleX">Where its middle is, as a fraction of the frame's width.</param>
    /// <param name="MiddleY">Where its middle is, as a fraction of the frame's height.</param>
    private sealed record ClipTemplate(LayerState? Look, double Width, double MiddleX, double MiddleY, bool BlurFilled, IReadOnlyList<TemplatePiece> Pieces);

    /// <param name="Under">It lies under the base clip's track.</param>
    /// <param name="Offset">Where it starts after the base clip's start, seconds.</param>
    /// <param name="Length">How long it is, seconds; 0 for the base clip's whole length.</param>
    /// <param name="FollowsBase">It plays the base clip's file on the base clip's clock (a Cutout cut from a clip of a file).</param>
    private sealed record TemplatePiece(LayerState State, bool Under, double Offset, double Length, bool FollowsBase);

    /// <summary>What was selected on the Timeline, for a preset chosen from its list (the window's; nothing while nothing is selected).</summary>
    public Func<IReadOnlyList<Layer>>? TimelineSelection { get; set; }

    /// <summary>The clips of a template's pictures: what lies over a clip as its child and is drawn, not the sound of its stream lanes.</summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip)> TemplateChildrenOf(TimelineClip clip) =>
        Document.ChildrenOf(clip).Where(c => c.Track.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).ToList();

    /// <summary>Whether a clip can be a template's base: a video from a file.</summary>
    private static bool CanBaseTemplate(TimelineClip clip) => clip.ContentKind == LayerKind.VideoFile;

    /// <summary>
    /// The clips a preset goes on for a selection: each selected video, or for a selected Cutout the clip it is cut from. Nothing else
    /// (a sound, a caption) and no clip twice.
    /// </summary>
    public IReadOnlyList<Layer> GamePresetTargets(IEnumerable<Layer> selected)
    {
        var targets = new List<Layer>();
        foreach (var layer in selected)
        {
            var clip = ClipOf(layer);
            for (var depth = 0; depth < 64 && clip is { ParentClipId: { } parent } && (!CanBaseTemplate(clip) || IsCutout(clip)) && Document.FindClip(parent) is { } over; depth++)
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
    /// A layout as a template (5.2-AuditB: a style's or a preset's): its layers the pieces (those it lays under its video under the clip's
    /// track), its saved video look (MainLayer) the clip's look, and its legacy Center values (a width about the frame's middle, moved by
    /// the offsets) the clip's place.
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

    // How narrow and how wide a template makes its clip, as fractions of the frame's width (what the old Center zoom reached).
    private const double SmallestTemplateWidth = 0.1, LargestTemplateWidth = 4;

    /// <summary>The template a clip has: what a clip that lands by it takes; null for a clip with no pieces.</summary>
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
    /// A Cutout of the picture (a cut-out of "the video", as a Game Preset file saves it) as a Cutout of a clip of a file: that file, on
    /// that clip's clock from where the Cutout starts, silent, cut by its crop to the same part of the picture and to the box the Cutout
    /// has now on that picture. A crop leaves at least <see cref="Layer.LargestCropTotal"/>'s remainder of a picture: a Cutout narrower
    /// than that is cut wider about its middle and its box made as much larger, and its mask, cut the same, still shapes it. Null when
    /// its crop leaves nothing of the picture: no clip of the old kind is ever made (a project holding one is converted as it is opened, LegacyMainVideo).
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

    // A Cutout reads its clip's file at its clip's moment where the Cutout starts (5.2-AuditB: from its own start, at its clip's speed),
    // and is never heard: the clip's own sound is that file's.
    private static LayerState OnClockOf(LayerState state, Layer target, TimelineClip clip, double start)
    {
        (state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaDuration) = (clip.SourcePath ?? target.ImagePath, target.ImageWidth, target.ImageHeight, target.MediaDuration);
        (state.MediaOffset, state.Speed) = (clip.MediaOffset + (start - clip.Start) * clip.Speed, clip.Speed);
        (state.HasAudio, state.AudioCodec, state.AudioChannels) = (false, "", "");
        return state;
    }

    // What a template's child is on this clip, starting at a moment of it. A cut-out of "the video" is a Cutout of the clip's file, so
    // only a clip of a file's picture takes one (5.2-Arm64AndNpu): on any other (a sound, a picture) it is left out, never kept as the old kind.
    private LayerState? StateFor(TemplatePiece piece, Layer target, TimelineClip clip, double start)
    {
        if (piece.State.Kind == LayerKind.Video)
            return clip.ContentKind == LayerKind.VideoFile ? FileCutoutOf(piece.State, target, clip, start) : null;

        var state = TimelineTransaction.CopyOf(piece.State);
        return piece.FollowsBase ? OnClockOf(state, target, clip, start) : state;
    }

    /// <summary>A Sub-Lane of the clip's Cutouts with room for this one (the same Cutout of an earlier clip's, so a template's lanes are kept in one stack), or a new one.</summary>
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

    // The clip's Cutouts, made anew: those it had go first (one template to a clip); or, not replacing them, more Cutouts beside them
    // (a region added, a mask's Cutouts).
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
                edit.SetClipParent(edit.PlaceClip(lane, made, allowCrossfadeOverlap: false), clip.Id);
            }
            catch (TimelineEditException)
            {
                Unadopt(view);
            }
        }
    }

    /// <summary>Puts a template on one clip: its look, place and blur fill, then its Cutouts over the clip's span. No undo step of its own: the caller's.</summary>
    /// <param name="swap">It takes the place of the clip's preset (a Track Preset Node, 5.2A): a blur fill the template does not have goes too.</param>
    private void PutTemplateOn(Guid clipId, ClipTemplate template, string label, bool swap = false)
    {
        if (Document.FindClip(clipId) is not { } clip || Document.TrackOf(clip) is not { } track || ViewOf(clip) is not { } view)
            return;

        if (template.Look is { } look)
            TakeLook(view, clip, track, look);

        // Its own shape, in the template's middle: a file of another shape keeps its picture whole.
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
    /// Puts a Game Preset's or a preset's layout on the clips it goes on (<see cref="TemplateTargets"/>: the selected video clips, or every
    /// top-level one), each taking its place, look and blur fill and Cutouts of its own over its own span; with no clip to go on, keeps
    /// it for the next file opened (PlaceOpenedFile). No undo step of its own: the caller's.
    /// </summary>
    /// <returns>How many clips it went on.</returns>
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
    /// A saved Game Preset on these clips alone (the Timeline's list with clips selected, a clip's menu): each takes its place, look and blur
    /// fill, and Cutouts of its own over its own span; nothing else on the timeline changes. The sequence's colour filters and captions stay
    /// as they are (they are not a clip's); a preset's blur settings are the one blur the sequence has. One step of Undo.
    /// </summary>
    /// <returns>Whether it was put on any clip.</returns>
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

        if (!_isBackgroundWorker && HasSource)
            LivePreview = true;
        ApplyEncodingDefaultsOverStyle();
        Checkpoint($"apply {name}", before);
        StatusText = $"{name} is on {(targets.Count == 1 ? "the clip" : $"{targets.Count} clips")}, its Cutouts over {(targets.Count == 1 ? "its" : "each one's")} span. Ctrl+Z takes it away.";
        return true;
    }

    /// <summary>The clip nearest a moment on a track with a template of its own to give, itself left out; null when no clip there has one.</summary>
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
    /// A clip that landed on a track with a template takes it: the place and look of the clip it landed by, and its own set of that clip's
    /// pieces over its own span. Nothing for a clip with pieces of its own, or a track with none to give. Part of the step that put it there.
    /// </summary>
    /// <param name="droppedOn">The row it was dropped on, whose track it may not have ended up on (no room: a track of its own above it).</param>
    /// <param name="at">The moment it was dropped at.</param>
    /// <param name="hadPreset">The Game Preset of the track it came from (a move from one track to another): a node of the same preset is not put on it again.</param>
    public void InheritTemplate(Layer clip, Layer? droppedOn, double at, string? hadPreset = null)
    {
        if (ClipOf(clip) is not { } model || !CanBaseTemplate(model) || model.ParentClipId is not null || IsCutout(model))
            return;

        // A Track Preset Node (5.2A): the track's Game Preset in place of whatever the clip had, its Cutouts swapped for the preset's.
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

    // ----- Track Preset Nodes (5.2A, the user's directive) -----
    // A picture track can carry a Game Preset of its own (TimelineTrackModel.PresetNodeName): every top-level video clip that comes onto it,
    // dropped or dragged from another track, takes that preset's place, look, blur fill and Cutouts in place of what it had, in the step that
    // brought it there (InheritTemplate), so a clip moved from one node's track to another's swaps its preset and Cutouts in one step of Undo.
    // Set on a track, the preset goes on the clips the track holds then too; cleared, they keep what they have. The rule above (a template is
    // one base clip's) still holds: the node only says which template a clip arriving there is given.

    /// <summary>The Game Preset a track gives its clips: its node's, on a picture track of its own; null for none.</summary>
    private static string? NodeOf(TimelineTrackModel? track) =>
        track is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, ParentTrackId: null, PresetNodeName: { Length: > 0 } name } ? name : null;

    /// <summary>The Game Preset a row's track gives its clips (5.2A), or null for none.</summary>
    public string? TrackPresetOf(Layer? row) => NodeOf(TrackOfRow(row));

    /// <summary>Whether a row's track can carry a Game Preset: a picture track of its own, not a Sub-Lane of another's.</summary>
    public bool CanHaveTrackPreset(Layer? row) => row is { IsCaptions: false, IsBackground: false } && TrackOfRow(row) is { Kind: TimelineTrackKind.Video or TimelineTrackKind.Switch, ParentTrackId: null };

    /// <summary>A saved Game Preset as a template, read from the Styles folder; null (the reason in the status bar) when it has no layout.</summary>
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
    /// Gives a track a Game Preset (a Track Preset Node, 5.2A), which goes on every video clip on it now, each with Cutouts of its own, in
    /// place of what each had; null takes the node away and leaves the clips as they are. One step of Undo.
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
            // As a Game Preset chosen for clips does (ApplyGamePreset): its masks unpacked, the frame's shape (only where the Sequence is the
            // preset's to shape: PresetMayShapeFrame, 5.2B) and the sequence's one blur.
            _activeStyle = style;
            ExtractMasks(style);
            if (style.Layout?.Vertical is { } vertical && vertical != UseVerticalResolution && PresetMayShapeFrame(track, name!))
                UseVerticalResolution = vertical;
            foreach (var id in targets)
                PutTemplateOn(id, template, label, swap: true);
            if (style.Blur is { } blur && targets.Count > 0)
                TakeBlur(blur);
            if (targets.Count > 0 && !_isBackgroundWorker && HasSource)
                LivePreview = true;
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
    /// Whether a Track Preset set on a track may turn the Sequence's frame to the preset's shape (5.2B, the user's Q2): never while another
    /// track already gives its clips a different preset (the frame is that one's, or the user's, then); otherwise when no other track holds a
    /// clip (the Sequence is this track's, or empty: any other track counts, a music track's sound as much as another's pictures; this track's
    /// own Sub-Lanes do not), or the frame is still the default 16:9 and no other track has a preset at all. The rest of the preset goes on as ever.
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
