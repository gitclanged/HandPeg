using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Templates on clips (5.1-DogfoodPolish, the user's rule) -----
// A template (a Game Preset's place, look, blur fill and cut-out pieces) belongs to one base clip: never to a track or a media file. Its
// pieces are children of that clip (ParentClipId) over that clip's span, so they split, trim, move and go with it, and any clip can have
// one of its own: the main video's, or the 2nd or Nth video on the timeline. A clip dropped or dragged onto a template's track takes the
// template of the clip it landed by, with a set of pieces of its own.
//
// A piece of the main video is a LayerKind.Video cut from the primary chain. A clip of another file is not in that chain, so its pieces are
// VideoFile clips of its own file, on its clock, cut to the same part of the picture by their crop and shaped by the same mask.
public partial class MainViewModel
{
    /// <summary>What a template puts on a clip: its look (null leaves the clip's), its place on the frame, its blur fill, and its pieces, lowest first.</summary>
    /// <param name="Width">The clip's width as a fraction of the frame's.</param>
    /// <param name="MiddleX">Where its middle is, as a fraction of the frame's width.</param>
    /// <param name="MiddleY">Where its middle is, as a fraction of the frame's height.</param>
    private sealed record ClipTemplate(LayerState? Look, double Width, double MiddleX, double MiddleY, bool BlurFilled, IReadOnlyList<TemplatePiece> Pieces);

    /// <param name="Under">It lies under the base clip's track.</param>
    /// <param name="Offset">Where it starts after the base clip's start, seconds.</param>
    /// <param name="Length">How long it is, seconds; 0 for the base clip's whole length.</param>
    /// <param name="FollowsBase">It plays the base clip's file on the base clip's clock (a piece cut from a clip of a file).</param>
    private sealed record TemplatePiece(LayerState State, bool Under, double Offset, double Length, bool FollowsBase);

    /// <summary>What was selected on the Timeline, for a preset chosen from its list (the window's; nothing while nothing is selected).</summary>
    public Func<IReadOnlyList<Layer>>? TimelineSelection { get; set; }

    /// <summary>The clips of a template's pictures: what lies over a clip as its child and is drawn, not the sound of its stream lanes.</summary>
    private List<(TimelineTrackModel Track, TimelineClip Clip)> TemplateChildrenOf(TimelineClip clip) =>
        Document.ChildrenOf(clip).Where(c => c.Track.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).ToList();

    /// <summary>Whether a clip can be a template's base: a video, the main file's or another's, that is no piece of another clip.</summary>
    private static bool CanBaseTemplate(TimelineClip clip) => clip.ContentKind is LayerKind.MainVideo or LayerKind.VideoFile;

    /// <summary>
    /// The clips a preset goes on for a selection: each selected video, or for a selected piece the clip it is a piece of. Nothing else
    /// (a sound, a caption) and no clip twice.
    /// </summary>
    public IReadOnlyList<Layer> GamePresetTargets(IEnumerable<Layer> selected)
    {
        var targets = new List<Layer>();
        foreach (var layer in selected)
        {
            var clip = ClipOf(layer);
            for (var depth = 0; depth < 64 && clip is { ParentClipId: { } parent } && !CanBaseTemplate(clip) && Document.FindClip(parent) is { } over; depth++)
                clip = over;

            if (clip is not null && CanBaseTemplate(clip) && ViewOf(clip) is { } view && !targets.Contains(view))
                targets.Add(view);
        }

        return targets;
    }

    private static ClipTemplate? TemplateFrom(StylePreset style)
    {
        if (style.Layout is not { } layout)
            return null;

        var layers = layout.Layers ?? [];
        var under = Math.Max(layout.MainVideoIndex ?? layers.Count, 0);
        var pieces = new List<TemplatePiece>(layers.Count);
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i] is { Kind: not LayerKind.Audio, IsEmptyTrack: false } state)
                pieces.Add(new TemplatePiece(state, i < under, Math.Max(state.StartTime, 0), state.Duration > 0.001 ? state.Duration : 0, FollowsBase: false));
        }

        var zoom = layout.CenterZoom is > 0 and var wanted ? Math.Clamp(wanted, SmallestCenterZoom, LargestCenterZoom) : 1;
        return new ClipTemplate(layout.MainLayer, zoom, 0.5 + Math.Clamp(layout.CenterOffsetX, -100, 100) / 100, 0.5 + Math.Clamp(layout.CenterOffsetY, -100, 100) / 100,
            style.Blur is not null && !layout.BackgroundHidden, pieces);
    }

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
    /// A piece of the main video as a piece of a clip of a file: that file, on that clip's clock, silent, cut by its crop to the same part
    /// of the picture and to the box the piece has now. A crop leaves at least <see cref="Layer.LargestCropTotal"/>'s remainder of a picture:
    /// a piece narrower than that is cut wider about its middle and its box made as much larger, and its mask, cut the same, still shapes it.
    /// </summary>
    private LayerState FilePieceOf(LayerState piece, Layer target, TimelineClip clip)
    {
        var (frameWidth, frameHeight) = (Math.Max(FrameWidth, 2), Math.Max(FrameHeight, 2));
        var cut = Layer.FromState(piece, SourceWidth, SourceHeight, frameWidth, frameHeight);
        var source = cut.CroppedSource;
        if (source.Width <= 0 || source.Height <= 0)
            return TimelineTransaction.CopyOf(piece);

        var box = cut.GetOutputRect(frameWidth, frameHeight, SourceWidth, SourceHeight);
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
        state.MaskFrame = false;
        return OnClockOf(state, target, clip);
    }

    // A piece reads its clip's file at its clip's moment, and is never heard: the clip's own sound is that file's.
    private static LayerState OnClockOf(LayerState state, Layer target, TimelineClip clip)
    {
        (state.ImagePath, state.ImageWidth, state.ImageHeight, state.MediaDuration) = (clip.SourcePath ?? target.ImagePath, target.ImageWidth, target.ImageHeight, target.MediaDuration);
        (state.MediaOffset, state.Speed) = (clip.MediaOffset, clip.Speed);
        (state.HasAudio, state.AudioCodec, state.AudioChannels) = (false, "", "");
        return state;
    }

    // What a template's piece is on this clip.
    private LayerState StateFor(TemplatePiece piece, Layer target, TimelineClip clip)
    {
        var onFile = clip.ContentKind == LayerKind.VideoFile;
        if (piece.State.Kind == LayerKind.Video && onFile)
            return FilePieceOf(piece.State, target, clip);

        var state = TimelineTransaction.CopyOf(piece.State);
        return piece.FollowsBase ? OnClockOf(state, target, clip) : state;
    }

    /// <summary>A track of the clip's pieces with room for this one (the same piece of an earlier clip's, so a template's lanes are kept in one stack), or a new one.</summary>
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

    // The clip's pieces, made anew: those it had go first (one template to a clip).
    private void SpawnPieces(ITimelineTransaction edit, TimelineClip clip, ClipTemplate template)
    {
        if (Document.TrackOf(clip) is not { } track || ViewOf(clip) is not { } target)
            return;

        foreach (var (_, old) in TemplateChildrenOf(clip))
            edit.DeleteClip(old);

        var pictures = Document.ChildTracksOf(track).Where(t => t.Kind is not (TimelineTrackKind.Audio or TimelineTrackKind.Subtitle)).Select(t => Document.Tracks.IndexOf(t)).ToList();
        var above = Math.Max(Document.Tracks.IndexOf(track), pictures.Count > 0 ? pictures.Max() : 0) + 1;
        foreach (var piece in template.Pieces)
        {
            var start = clip.Start + piece.Offset;
            var end = piece.Length > 0 ? Math.Min(start + piece.Length, clip.End) : clip.End;
            if (end - start < TimelineDocument.MinimumClipSeconds)
                continue;

            var state = StateFor(piece, target, clip);
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

    /// <summary>Puts a template on one clip: its look, place and blur fill, then its pieces over the clip's span. No undo step of its own: the caller's.</summary>
    private void PutTemplateOn(Guid clipId, ClipTemplate template, string label)
    {
        if (Document.FindClip(clipId) is not { } clip || Document.TrackOf(clip) is not { } track || ViewOf(clip) is not { } view)
            return;

        if (template.Look is { } look)
            TakeLook(view, clip, track, look);

        if (view.IsMainVideo)
        {
            var placing = _placingMain;
            _placingMain = true;
            try
            {
                WriteMainPlace(view, template.Width, (template.MiddleX - 0.5) * 100, (template.MiddleY - 0.5) * 100);
            }
            finally
            {
                _placingMain = placing;
            }
        }
        else if (ShapeOf(view) is > 0 and var tall && FrameHeight > 0)
        {
            // Its own shape, in the template's middle: a file of another shape keeps its picture whole.
            var (left, top, right, bottom) = view.CropFractions;
            var height = template.Width * FrameWidth * tall * (1 - top - bottom) / (1 - left - right) / FrameHeight;
            view.LockAspectRatio = true;
            view.FromCorner(() =>
            {
                view.SizeWidth = template.Width;
                view.PositionX = template.MiddleX - template.Width / 2;
                view.PositionY = template.MiddleY - height / 2;
            });
        }

        if (template.BlurFilled)
            view.BlurFill = true;

        Edit(label, edit =>
        {
            if (Document.FindClip(clipId) is { } now)
                SpawnPieces(edit, now, template);
        }, undoable: false);
    }

    /// <summary>
    /// A saved Game Preset on these clips alone (the Timeline's list with clips selected, a clip's menu): each takes its place, look and blur
    /// fill, and pieces of its own over its own span; nothing else on the timeline changes. The sequence's colour filters and captions stay
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

        FrameEngine = true;
        if (!_isBackgroundWorker && HasSource)
            LivePreview = true;
        ApplyEncodingDefaultsOverStyle();
        Checkpoint($"apply {name}", before);
        StatusText = $"{name} is on {(targets.Count == 1 ? "the clip" : $"{targets.Count} clips")}, its pieces over {(targets.Count == 1 ? "its" : "each one's")} span. Ctrl+Z takes it away.";
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
    public void InheritTemplate(Layer clip, Layer? droppedOn, double at)
    {
        if (ClipOf(clip) is not { } model || !CanBaseTemplate(model) || model.ParentClipId is not null || TemplateChildrenOf(model).Count > 0)
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
}
