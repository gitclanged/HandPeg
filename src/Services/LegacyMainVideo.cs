using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- One-time conversion of legacy main-video projects -----
// Run on open (MainViewModel.ConvertLegacy); the window then overwrites the file (MainViewModel.WriteBackConverted). Golden
// fixtures (LegacyFixtures) also pass through it, keeping export goldens byte-identical.
//
// Legacy model: MainVideo clips formed one chain on the lowest eligible track (TimelineLowering.CanRide: visible, video stream 0,
// own or no audio, speed 1, the opened file), placed by the Center box (CenterRect), cropped by the first clip's crop (which also
// sized the Sequence), drew the full-frame blur background, and played audio through the opened file's mixed streams (AudioTracks:
// gain, filters, ducking, voice, slip, muted ranges, on the container clock). Video-kind cut-outs showed the chain's picture.
// Conversion rewrites the document in place (no undo step) into VideoFile clips with their own looks, blur fill, Cutouts and
// Sub-Lanes, reproducing the same boxes, crops, source times, Cutout pixels and audio (the lowering arithmetic, evaluated once).

/// <summary>
/// Inputs for <see cref="LegacyMainVideo.Migrate"/> beyond the document: probed main file, legacy layout switches and frame, and
/// its audio streams with their legacy mixing.
/// </summary>
public sealed record LegacyMainVideoContext
{
    /// <summary>Current local path of the project's opened file (ProjectState.LocalMediaPath, including downloads); "" for none.</summary>
    public required string PrimaryPath { get; init; }

    /// <summary>Its probed picture size (0 while unknown).</summary>
    public int SourceWidth { get; init; }

    public int SourceHeight { get; init; }

    /// <summary>Its probed length, in seconds (0 while unknown).</summary>
    public double MediaSeconds { get; init; }

    /// <summary>It has no picture (a sound file opened as the main video): its clips become sound clips.</summary>
    public bool SourceHasNoVideo { get; init; }

    /// <summary>The project's old Background switch: off, the clips that left their blur fill to it had none.</summary>
    public bool BackgroundHidden { get; init; }

    /// <summary>Frame size the legacy Center box was laid out in (view model FrameWidth/FrameHeight); 0 while unknown.</summary>
    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    /// <summary>
    /// Legacy Center box in frame pixels (GetCenterRect); null derives it from the first chain clip (MainPlaceOf, CenterRectOf).
    /// </summary>
    public (int X, int Y, int Width, int Height)? CenterRect { get; init; }

    /// <summary>
    /// Main file audio streams with legacy mixing, in ordinal order. Migration resets their mixing to neutral (pass copies for undo
    /// documents, live instances for the opening project).
    /// </summary>
    public IReadOnlyList<AudioTrack>? Streams { get; init; }

    /// <summary>
    /// Recycle bin converted alongside the document so restoring a clip never reintroduces legacy kinds; null for none.
    /// </summary>
    public IReadOnlyList<DeletedClip>? Bin { get; init; }
}

/// <summary>One-time conversion of legacy main-video and cut-out clips into VideoFile clips.</summary>
public static class LegacyMainVideo
{
    // Two edges this close are one (LinkToMainVideo's slack, as a piece's parent was found).
    private const double Edge = 0.0005;

    // A time this close to another is the same one (TimelineTransaction's slack).
    private const double Slack = 1e-6;

    // A part of a sound shorter than this is not made: it goes to its neighbour.
    private const double Sliver = 0.001;

    /// <summary>True when tracks or folds still hold legacy MainVideo or Video clips.</summary>
    public static bool Needs(TimelineDocument document)
    {
        foreach (var track in document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (IsLegacy(clip))
                    return true;
            }
        }

        return document.Folds?.Exists(f => f.Clips.Any(c => IsLegacy(c.Clip))) == true;
    }

    /// <summary>
    /// Converts in place (no undo step). Idempotent: documents that do not need it are untouched (the context's bin is still checked).
    /// Track ordering and non-overlap invariants are preserved.
    /// </summary>
    public static void Migrate(TimelineDocument document, LegacyMainVideoContext context)
    {
        var converts = Needs(document);
        var bin = context.Bin is { Count: > 0 } binned && binned.Any(HoldsLegacy) ? binned : null;
        if (!converts && bin is null)
            return;

        // Everything the old engine read of the document, read before anything of it changes.
        var plan = new Plan(document, context);
        if (converts)
        {
            // 1. The Sequence's size: what the main video's size and its first clip's crop made it.
            TakeNaturalSize(document, plan);

            // 2. The cut-outs: each a video-file child of the clip whose picture it showed, on that clip's clock.
            foreach (var track in document.Tracks)
            {
                foreach (var piece in track.Clips.Where(c => c.ContentKind == LayerKind.Video).ToList())
                    CutOut(track, piece, plan);
            }

            // 3. MainVideo clips become VideoFile clips in their drawn box, rebased to the video stream clock.
            foreach (var track in document.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip.ContentKind == LayerKind.MainVideo)
                        ToFileClip(clip, plan.IsClocked(clip), plan.Chain.Contains(clip), plan);
                }
            }

            // 4. Their sound: the opened file's streams on Sub-Lanes under each clip that was heard through them.
            Sound(document, plan);

            // Folded clips are converted the same way.
            if (document.Folds is { Count: > 0 } folds)
                document.Folds = folds.ConvertAll(f => f.Clips.Any(c => IsLegacy(c.Clip)) ? f with { Clips = Folded(f, document, plan) } : f);

            Renumber(document);

            // From now on an audio track is only an output choice: its old mixing lives on the Sub-Lanes.
            foreach (var stream in context.Streams ?? [])
            {
                (stream.GainDb, stream.Filters, stream.AutoDuck, stream.IsVoice) = (0, new TrackAudioFilters(), false, false);
                stream.SetEdits(0, null);
            }
        }

        if (bin is not null)
        {
            foreach (var deleted in bin)
                Deleted(deleted, null, document, plan);
        }
    }

    /// <summary>Converts a legacy main-file caption source to a per-clip source with the same file and stream.</summary>
    /// <param name="openedPath">Current opened file path (downloads may have moved); null keeps the stored path.</param>
    public static CaptionSource? MigrateCaption(CaptionSource? source, string? openedPath = null) =>
        source is { Kind: CaptionSourceKind.MainFile }
            ? source with { Kind = CaptionSourceKind.Clip, Path = string.IsNullOrWhiteSpace(openedPath) ? source.Path : openedPath }
            : source;

    /// <summary>Converts a subtitle track's caption source in place (<see cref="MigrateCaption"/>); true when one was converted.</summary>
    public static bool MigrateCaptions(SubtitleTrackData? subtitles, string? openedPath = null)
    {
        if (subtitles?.Source is not { Kind: CaptionSourceKind.MainFile } source)
            return false;

        subtitles.Source = MigrateCaption(source, openedPath);
        return true;
    }

    // ----- What the old engine read of the document -----

    private static bool IsLegacy(TimelineClip clip) => clip.ContentKind is LayerKind.MainVideo or LayerKind.Video;

    private static bool HoldsLegacy(DeletedClip deleted) =>
        deleted.Clip is { } clip && IsLegacy(clip) || deleted.Children?.Any(HoldsLegacy) == true;

    private static string Clean(string? path) => (path ?? "").Trim().Trim('"');

    /// <summary>True for picture tracks (video or switch).</summary>
    private static bool ShowsPictures(TimelineTrackModel track) => track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch;

    // Even-rounding as in MainViewModel.Even/TimelineLowering.Even and Layer.Even (with its Math.Max floor).
    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

    private static int LayerEven(double value) => (int)Math.Round(value / 2) * 2;

    /// <summary>Cropped size in pixels, even-rounded like the crop filter (MainViewModel.CroppedSize).</summary>
    private static (int Width, int Height) CroppedSize((double Left, double Top, double Right, double Bottom) crop, int width, int height) =>
        (Math.Max((int)(width * (1 - crop.Left - crop.Right) / 2) * 2, 2), Math.Max((int)(height * (1 - crop.Top - crop.Bottom) / 2) * 2, 2));

    /// <summary>
    /// Legacy interpretation of a document, computed once: primary chain (TimelineLowering.CanRide), anchor clip, Center box, and
    /// clips played through the main file's streams with their clock.
    /// </summary>
    private sealed class Plan
    {
        public Plan(TimelineDocument document, LegacyMainVideoContext context)
        {
            Document = document;
            Context = context;
            Primary = Clean(context.PrimaryPath);
            Streams = context.Streams ?? [];
            Frame = context.FrameWidth > 0 && context.FrameHeight > 0 ? (context.FrameWidth, context.FrameHeight) : null;
            Source = context.SourceWidth > 0 && context.SourceHeight > 0 ? (context.SourceWidth, context.SourceHeight) : null;

            // Offset between container clock (legacy main video) and first-picture clock (files): GpuScenePlan.ChainShiftOf; 0 if unknown.
            if (Primary.Length > 0 && MediaProbe.KnownVideoStart(Primary) is { } video && MediaProbe.KnownContainerStart(Primary) is { } container
                && double.IsFinite(video - container))
            {
                Shift = video - container;
            }

            // Chain (TimelineLowering.LowerAfresh): lowest picture track with a visible eligible clip, and all its visible eligible clips.
            foreach (var track in document.Tracks)
            {
                if (ShowsPictures(track) && track.Clips.FirstOrDefault(c => !c.IsHidden && Rides(c)) is { } rider)
                {
                    (ChainTrack, Anchor) = (track, rider);
                    break;
                }
            }

            foreach (var clip in ChainTrack?.Clips ?? [])
            {
                if (!clip.IsHidden && Rides(clip))
                    Chain.Add(clip);
            }

            // Audio track: the chain's, else the lowest with an eligible (possibly disabled) clip. Its eligible clips were on the chain
            // clock; for audio-only files every clip was audio.
            SoundTrack = ChainTrack ?? document.Tracks.FirstOrDefault(t => ShowsPictures(t) && t.Clips.Exists(Rides));
            TimelineClip? first = null;
            foreach (var track in document.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip.ContentKind != LayerKind.MainVideo)
                        continue;

                    first ??= clip;
                    if (context.SourceHasNoVideo ? IsOf(clip) : ReferenceEquals(track, SoundTrack) && Rides(clip))
                        Clocked.Add(clip);
                    Offsets[clip] = OffsetOf(clip, Clocked.Contains(clip));
                }
            }

            // Anchor clip (MainViewModel.AnchorMainView): first chain clip, else first MainVideo clip.
            AnchorClip = Anchor ?? first;
            AnchorLook = AnchorClip?.State;
            Center = context.CenterRect is { Width: > 0, Height: > 0 } given ? given : CenterOf(AnchorLook);
        }

        public TimelineDocument Document { get; }

        public LegacyMainVideoContext Context { get; }

        public string Primary { get; }

        public IReadOnlyList<AudioTrack> Streams { get; }

        public (int Width, int Height)? Frame { get; }

        public (int Width, int Height)? Source { get; }

        public double Shift { get; }

        public TimelineTrackModel? ChainTrack { get; }

        public TimelineClip? Anchor { get; }

        public TimelineTrackModel? SoundTrack { get; }

        /// <summary>The chain's clips: drawn in the Center box (or a box of their own), heard through the streams.</summary>
        public HashSet<TimelineClip> Chain { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Clips on the chain clock (chain plus disabled clips on its track), played through the streams.</summary>
        public HashSet<TimelineClip> Clocked { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Each main video clip's offset on its picture's clock, as it is converted.</summary>
        public Dictionary<TimelineClip, double> Offsets { get; } = new(ReferenceEqualityComparer.Instance);

        public TimelineClip? AnchorClip { get; }

        public LayerState? AnchorLook { get; }

        public (int X, int Y, int Width, int Height)? Center { get; }

        // The lanes made, by the video track they lie under and the stream place they hold.
        public Dictionary<(Guid Track, int Ordinal), List<TimelineTrackModel>> Lanes { get; } = new();

        /// <summary>Main file size after the anchor crop, even-rounded; null while unknown.</summary>
        public (int Width, int Height)? NaturalSize =>
            Source is { } source ? CroppedSize(AnchorLook is { } look ? Layer.HeldCrop(look) : default, source.Width, source.Height) : null;

        /// <summary>Whether a clip is of the opened file.</summary>
        public bool IsOf(TimelineClip clip) => Primary.Length > 0 && string.Equals(Clean(clip.SourcePath), Primary, StringComparison.OrdinalIgnoreCase);

        /// <summary>Chain eligibility ignoring the enabled flag (TimelineLowering.CanRide).</summary>
        public bool Rides(TimelineClip clip) =>
            clip.ContentKind == LayerKind.MainVideo && clip.VideoStreamIndex == 0 && clip.AudioStreamIndex <= 0 && Math.Abs(clip.Speed - 1) < 0.0005 && IsOf(clip);

        public bool IsClocked(TimelineClip clip) => Clocked.Contains(clip);

        /// <summary>
        /// MainVideo clip offset on the video clock: chain offsets are rebased from the container clock; others are already file offsets.
        /// </summary>
        public double OffsetOf(TimelineClip clip, bool clocked) => clocked ? Math.Max(clip.MediaOffset - Shift, 0) : clip.MediaOffset;

        /// <summary>The offset a clip of the document has once converted (a clip not converted: its own).</summary>
        public double ClockOf(TimelineClip clip) => Offsets.TryGetValue(clip, out var offset) ? offset : clip.MediaOffset;

        /// <summary>A clip's file's length once converted: its own, else the opened file's for a clip of it.</summary>
        public double LengthOf(TimelineClip clip) => clip.SourceDuration > 0 ? clip.SourceDuration : IsOf(clip) ? Math.Max(Context.MediaSeconds, 0) : 0;

        /// <summary>
        /// Center box derived from a clip (MainPlaceOf, CenterRectOf): zoom and offsets from its corner/width at cropped aspect, centred
        /// in the frame. Null without frame or clip.
        /// </summary>
        private (int X, int Y, int Width, int Height)? CenterOf(LayerState? look)
        {
            if (look is null || Frame is not { } frame)
                return null;

            var (frameWidth, frameHeight) = frame;
            var (croppedWidth, croppedHeight) = Source is { } source ? CroppedSize(Layer.HeldCrop(look), source.Width, source.Height) : (1920, 1080);
            var zoom = Math.Clamp(look.SizeWidth, 0.1, 4);
            var offsetX = Math.Clamp((look.PositionX - (1 - zoom) / 2) * 100, -100, 100);
            var offsetY = Math.Clamp((look.PositionY - (1 - zoom * Math.Max(frameWidth, 1) * croppedHeight / croppedWidth / Math.Max(frameHeight, 1)) / 2) * 100, -100, 100);
            if (!double.IsFinite(zoom + offsetX + offsetY))
                return null;

            var width = Even(frameWidth * zoom);
            var height = Even((double)width * croppedHeight / croppedWidth);
            return ((frameWidth - width) / 2 + (int)Math.Round(offsetX / 100 * frameWidth), (frameHeight - height) / 2 + (int)Math.Round(offsetY / 100 * frameHeight), width, height);
        }

        /// <summary>
        /// Legacy drawn box in frame pixels: chain clips in the Center box (MakeRider), others in the Center box at source aspect
        /// (MakeLayer), independently placed/cropped clips in their own box (MainRectOf); then rounded like Layer.GetOutputRect.
        /// Null without frame or Center box (look unchanged).
        /// </summary>
        public (int X, int Y, int Width, int Height)? BoxOf(TimelineClip clip, LayerState look, bool chained)
        {
            if (Frame is not { } frame || Center is not { } center)
                return null;

            var ((frameWidth, frameHeight), (x, y, width, height)) = (frame, center);
            var (ownPlace, ownCrop) = OwnOf(clip, look);
            (double X, double Y, double Width, double Height) made;
            if (!chained && !ownPlace && !ownCrop && !IsCropped(Layer.HeldCrop(look)))
            {
                // MakeLayer: the box at the source's own shape, about the Center box's middle.
                var tall = Source is { } source ? width * (double)source.Height / source.Width : height;
                made = (x / (double)frameWidth, (y + (height - tall) / 2) / frameHeight, width / (double)frameWidth, tall / frameHeight);
            }
            else
            {
                var (bx, by, bw, bh) = ownPlace || ownCrop ? RectOf(look, frameWidth, frameHeight) : (x, y, width, height);
                made = (bx / (double)frameWidth, by / (double)frameHeight, bw / (double)frameWidth, bh / (double)frameHeight);
            }

            return ((int)Math.Round(made.X * frameWidth), (int)Math.Round(made.Y * frameHeight),
                Math.Max(LayerEven(made.Width * frameWidth), 2), Math.Max(LayerEven(made.Height * frameHeight), 2));
        }

        private static bool IsCropped((double Left, double Top, double Right, double Bottom) crop) => crop.Left + crop.Top + crop.Right + crop.Bottom > 1e-9;

        /// <summary>True when placement or crop differs from the anchor (TimelineLowering.OwnOf).</summary>
        private (bool Place, bool Crop) OwnOf(TimelineClip clip, LayerState look)
        {
            if (AnchorLook is not { } anchor || ReferenceEquals(clip, AnchorClip) || ReferenceEquals(look, anchor))
                return (false, false);

            static bool Differ(double a, double b) => Math.Abs(a - b) > 1e-9;
            var place = Differ(look.PositionX, anchor.PositionX) || Differ(look.PositionY, anchor.PositionY) || Differ(look.SizeWidth, anchor.SizeWidth);
            var (crop, anchorCrop) = (Layer.HeldCrop(look), Layer.HeldCrop(anchor));
            return (place, Differ(crop.Left, anchorCrop.Left) || Differ(crop.Top, anchorCrop.Top) || Differ(crop.Right, anchorCrop.Right) || Differ(crop.Bottom, anchorCrop.Bottom));
        }

        /// <summary>Own-look box: corner and width, height at cropped aspect (TimelineLowering.MainRectOf).</summary>
        private (int X, int Y, int Width, int Height) RectOf(LayerState look, int frameWidth, int frameHeight)
        {
            var (sourceWidth, sourceHeight) = Source ?? (1920, 1080);
            var (croppedWidth, croppedHeight) = CroppedSize(Layer.HeldCrop(look), sourceWidth, sourceHeight);
            var width = Even(frameWidth * (double.IsFinite(look.SizeWidth) ? look.SizeWidth : 1));
            var height = Even((double)width * croppedHeight / croppedWidth);
            return ((int)Math.Round(look.PositionX * frameWidth), (int)Math.Round(look.PositionY * frameHeight), width, height);
        }
    }

    // ----- 1. The Sequence's size -----

    /// <summary>
    /// Legacy natural size (MainViewModel.GetCroppedSourceSize): main file size after the anchor crop, even, before vertical rotation.
    /// Null when unknown (legacy default 1920 x 1080). Used to derive <see cref="LegacyMainVideoContext.FrameWidth"/> before conversion;
    /// reads only <see cref="LegacyMainVideoContext.PrimaryPath"/> and source size from the context.
    /// </summary>
    public static (int Width, int Height)? NaturalSizeOf(TimelineDocument document, LegacyMainVideoContext context) =>
        new Plan(document, context).NaturalSize;

    /// <summary>Applies <see cref="NaturalSizeOf"/> unless a size is already set or it is unknown.</summary>
    private static void TakeNaturalSize(TimelineDocument document, Plan plan)
    {
        if (document.NaturalWidth <= 0 && plan.NaturalSize is { } natural)
            (document.NaturalWidth, document.NaturalHeight) = natural;
    }

    // ----- 2. The cut-outs -----

    /// <summary>
    /// Converts a legacy cut-out into VideoFile parts split at chain clip edges, each a child of the underlying chain clip on its clock
    /// and cropped like the cut-out (FileCutoutOf); parts over no chain clip are disabled. First part keeps id, fade-in and crossfade,
    /// last keeps fade-out; keyframes stay in place.
    /// </summary>
    private static void CutOut(TimelineTrackModel track, TimelineClip piece, Plan plan)
    {
        var index = track.IndexOf(piece);
        if (index < 0)
            return;

        // What the parts share, read before the first part (the cut-out itself) changes.
        var parts = PartsOf(piece, plan);
        var look = PieceLook(piece.State, plan);
        var whole = piece.Snapshot();
        track.Clips.RemoveAt(index);
        for (var k = 0; k < parts.Count; k++)
        {
            var (from, to, over, shown) = parts[k];
            var part = k == 0 ? piece : whole.Snapshot();
            if (k > 0)
                part.Id = Derived(whole.Id, 1, k, 0);

            (part.Start, part.End) = (from, to);
            OnClock(part, over, plan, look, from - whole.Start);
            part.IsHidden = whole.IsHidden || !shown;
            (part.TransitionIn, part.FadeInSeconds) = k == 0 ? (whole.TransitionIn, whole.FadeInSeconds) : (0.0, 0.0);
            part.FadeOutSeconds = k == parts.Count - 1 ? whole.FadeOutSeconds : 0;
            HoldFades(part);
            track.Clips.Insert(index + k, part);
        }
    }

    /// <summary>
    /// Cut-out segments between chain clip edges, each with its source clip: a covering chain clip (parent preferred, enabled), else
    /// an underlying MainVideo clip for its clock (disabled), else none.
    /// </summary>
    private static List<(double From, double To, TimelineClip? Over, bool Shown)> PartsOf(TimelineClip piece, Plan plan)
    {
        var (start, end) = (piece.Start, piece.End);
        var parent = piece.ParentClipId is { } id ? plan.Document.FindClip(id) : null;
        static bool Covers(TimelineClip clip, double from, double to) => clip.Start <= from + Edge && clip.End >= to - Edge;

        // Chain clip edges inside the cut-out (none when its parent covers it), deduplicated within the slack so every segment exceeds it.
        var edges = new List<double>();
        if (!(parent is not null && plan.Chain.Contains(parent) && Covers(parent, start, end)))
        {
            foreach (var clip in plan.Chain)
            {
                if (clip.Start > start + Edge && clip.Start < end - Edge)
                    edges.Add(clip.Start);
                if (clip.End > start + Edge && clip.End < end - Edge)
                    edges.Add(clip.End);
            }
        }

        edges.Sort();
        var cuts = new List<double>(edges.Count + 2) { start };
        foreach (var edge in edges)
        {
            if (edge - cuts[^1] > Edge)
                cuts.Add(edge);
        }

        cuts.Add(end);
        var parts = new List<(double From, double To, TimelineClip? Over, bool Shown)>(cuts.Count - 1);
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var (from, to) = (cuts[i], cuts[i + 1]);
            var over = parent is not null && plan.Chain.Contains(parent) && Covers(parent, from, to) ? parent : plan.Chain.FirstOrDefault(c => Covers(c, from, to));
            var shown = over is not null;
            over ??= parent is { ContentKind: LayerKind.MainVideo } own && Covers(own, from, to) ? own
                : plan.Document.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.ContentKind == LayerKind.MainVideo && plan.IsOf(c) && Covers(c, from, to));

            // Two stretches over the same clip are one.
            if (parts.Count > 0 && ReferenceEquals(parts[^1].Over, over) && parts[^1].Shown == shown)
                parts[^1] = parts[^1] with { To = to };
            else
                parts.Add((from, to, over, shown));
        }

        return parts;
    }

    /// <summary>
    /// Cut-out look as a file Cutout (MainViewModel.FileCutoutOf): crop to the same source region (source rect minus crop) in the
    /// same box, mask cropped identically. Regions narrower than the <see cref="Layer.LargestCropTotal"/> limit are widened about
    /// their centre with a proportionally larger box. Blur fill off.
    /// </summary>
    private static LayerState PieceLook(LayerState piece, Plan plan)
    {
        var (frameWidth, frameHeight) = plan.Frame ?? (1920, 1080);
        var (sourceWidth, sourceHeight) = plan.Source ?? (0, 0);
        var measured = piece.ShallowCopy();
        measured.Kind = LayerKind.Video;
        var cut = Layer.FromState(measured, sourceWidth, sourceHeight, frameWidth, frameHeight);
        var source = cut.CroppedSource;

        var state = TimelineTransaction.CopyOf(piece);
        (state.Kind, state.BlurFill) = (LayerKind.VideoFile, false);
        if (source.Width <= 0 || source.Height <= 0)
            return state;

        var box = cut.ComputeOutputRect(frameWidth, frameHeight, sourceWidth, sourceHeight);
        var (perX, perY) = ((double)box.Width / frameWidth / source.Width, (double)box.Height / frameHeight / source.Height);
        var least = 1 - Layer.LargestCropTotal + 1e-6;
        static (double From, double Length) Held(double from, double length, double least)
        {
            var held = Math.Max(length, least);
            return (Math.Clamp(from - (held - length) / 2, 0, 1 - held), held);
        }

        var (left, width) = Held(source.X, source.Width, least);
        var (top, height) = Held(source.Y, source.Height, least);
        (state.SourceX, state.SourceY, state.SourceWidth, state.SourceHeight) = (0, 0, 1, 1);
        (state.CropLeft, state.CropRight, state.CropTop, state.CropBottom) = (left, 1 - left - width, top, 1 - top - height);
        state.PositionX = box.X / (double)frameWidth - (source.X - left) * perX;
        state.PositionY = box.Y / (double)frameHeight - (source.Y - top) * perY;
        (state.SizeWidth, state.SizeHeight, state.LockAspectRatio) = (width * perX, height * perY, false);
        MaskStore.SpreadOverPicture(state, (cut.SourceX, cut.SourceY, cut.SourceWidth, cut.SourceHeight));
        state.MaskFrame = false;
        return state;
    }

    /// <summary>
    /// Cut-out part on the clock of <paramref name="over"/> (null: main file from 0, disabled): its file, stream and speed at the
    /// part's start, muted, with <paramref name="look"/> keys shifted back by <paramref name="shift"/>.
    /// </summary>
    private static void OnClock(TimelineClip part, TimelineClip? over, Plan plan, LayerState look, double shift)
    {
        part.ContentKind = LayerKind.VideoFile;
        part.SourcePath = over?.SourcePath ?? (plan.Primary.Length > 0 ? plan.Primary : part.SourcePath);
        (part.VideoStreamIndex, part.AudioStreamIndex) = (over?.VideoStreamIndex ?? 0, -1);
        part.Speed = over?.Speed ?? 1;
        part.MediaOffset = over is null ? 0 : Math.Max(plan.ClockOf(over) + (part.Start - over.Start) * over.Speed, 0);
        part.SourceDuration = over is null ? Math.Max(plan.Context.MediaSeconds, 0) : plan.LengthOf(over);
        (part.AudioSlipSeconds, part.AudioLeadInSeconds, part.AudioTailOutSeconds) = (0, 0, 0);
        (part.ParentClipId, part.Tethered) = (over?.Id, false);
        part.ProxyChoice = over?.ProxyChoice ?? part.ProxyChoice;

        var state = Shifted(look, -shift);
        if (ReferenceEquals(state, look))
            state = look.ShallowCopy();
        (state.ImagePath, state.MediaDuration) = (part.SourcePath ?? "", part.SourceDuration);
        if (plan.Source is { } source)
            (state.ImageWidth, state.ImageHeight) = source;
        (state.MediaOffset, state.Speed, state.StartTime, state.Duration) = (part.MediaOffset, part.Speed, part.Start, part.Duration);
        (state.HasAudio, state.AudioCodec, state.AudioChannels) = (false, "", "");
        part.State = state;
    }

    // ----- 3. The main video's clips -----

    /// <summary>
    /// Converts a MainVideo clip to VideoFile (clip and look): sets the file with main-file size/duration fallbacks, blur fill from the
    /// legacy Background switch for chain-clock clips, renames "Main Video" after the file, writes its drawn box (<see cref="Plan.BoxOf"/>)
    /// and rebases its offset to the video clock. Copy-on-write look.
    /// </summary>
    /// <param name="clocked">On the chain clock: offset was container-based and blur followed the switch.</param>
    /// <param name="chained">Drawn as the chain (Center box) rather than as its own layer.</param>
    private static void ToFileClip(TimelineClip clip, bool clocked, bool chained, Plan plan)
    {
        var look = clip.State.ShallowCopy();
        var box = plan.BoxOf(clip, clip.State, chained);
        clip.ContentKind = look.Kind = LayerKind.VideoFile;
        if (plan.IsOf(clip))
        {
            if (clip.SourceDuration <= 0 && plan.Context.MediaSeconds > 0)
                clip.SourceDuration = plan.Context.MediaSeconds;
            if ((look.ImageWidth <= 0 || look.ImageHeight <= 0) && plan.Source is { } source)
                (look.ImageWidth, look.ImageHeight) = source;
        }

        if (Clean(clip.SourcePath) is { Length: > 0 } path)
        {
            look.ImagePath = clip.SourcePath!;
            if (look.Name == "Main Video")
                look.Name = Path.GetFileName(path);
        }

        look.BlurFill ??= clocked && !plan.Context.BackgroundHidden;
        (look.SourceX, look.SourceY, look.SourceWidth, look.SourceHeight) = (0, 0, 1, 1);
        if (box is { } drawn && plan.Frame is { } frame)
        {
            (look.PositionX, look.PositionY) = (drawn.X / (double)frame.Width, drawn.Y / (double)frame.Height);
            (look.SizeWidth, look.SizeHeight) = (drawn.Width / (double)frame.Width, drawn.Height / (double)frame.Height);
            look.LockAspectRatio = KeepsShape(look, drawn.Width, drawn.Height);
        }

        clip.MediaOffset = plan.OffsetOf(clip, clocked);
        (look.MediaOffset, look.Speed, look.StartTime, look.Duration) = (clip.MediaOffset, clip.Speed, clip.Start, clip.Duration);
        if (clip.SourceDuration > 0)
            look.MediaDuration = clip.SourceDuration;
        clip.State = look;
    }

    /// <summary>
    /// True when an aspect-locked box at <paramref name="width"/> yields <paramref name="height"/> under either rounding
    /// (Layer.GetProportionalHeight or even-rounded crop), so it can stay locked; otherwise an explicit SizeHeight preserves the drawn box.
    /// </summary>
    private static bool KeepsShape(LayerState look, int width, int height)
    {
        if (look.ImageWidth <= 0 || look.ImageHeight <= 0)
            return false;

        var crop = look.CropLeft + look.CropRight + look.CropTop + look.CropBottom > 0.0005 ? Layer.HeldCrop(look) : default;
        var shape = (1 - crop.Top - crop.Bottom) / (1 - crop.Left - crop.Right);
        var (cutWidth, cutHeight) = CroppedSize(crop, look.ImageWidth, look.ImageHeight);
        return Math.Max(LayerEven((double)width * look.ImageHeight / look.ImageWidth * shape), 2) == height
               && Math.Max(LayerEven((double)width * cutHeight / cutWidth), 2) == height;
    }

    // ----- 4. The sound -----

    /// <summary>
    /// Unpacks main-file streams onto Sub-Lanes like MainViewModel.UnpackStreams (one lane per stream ordinal, child audio clips, the
    /// video clip muted) for every clip that played through them. Lanes inherit legacy stream mixing; audio clips get legacy timing
    /// (SoundOfChain, AddMainVoices). Crossfades lanes cannot carry become dips; audio-only files keep only audio.
    /// </summary>
    private static void Sound(TimelineDocument document, Plan plan)
    {
        var made = new Dictionary<TimelineClip, (TimelineClip First, TimelineClip Last, bool Carried)?[]>(ReferenceEqualityComparer.Instance);
        var touched = new HashSet<TimelineTrackModel>(ReferenceEqualityComparer.Instance);
        if (plan.Streams.Count > 0)
        {
            foreach (var track in document.Tracks.ToList())
            {
                foreach (var clip in track.Clips.Where(plan.IsClocked).ToList())
                {
                    if (clip.AudioStreamIndex < 0)
                        continue;

                    var lanes = new (TimelineClip First, TimelineClip Last, bool Carried)?[plan.Streams.Count];
                    for (var ordinal = 0; ordinal < plan.Streams.Count; ordinal++)
                    {
                        var stream = plan.Streams[ordinal];
                        var parts = LaneClips(clip, track, ordinal, stream, plan, widen: true);
                        if (parts.Count == 0)
                            continue;

                        var lane = LaneFor(document, track, ordinal, stream, parts[0].Start, parts[^1].End, plan, needsRoom: true);
                        foreach (var part in parts)
                            Insert(lane, part);
                        touched.Add(lane);

                        // Its crossfade, where the lane's clip before it meets it (Reconcile's rule); else it dips (below).
                        var at = lane.IndexOf(parts[0]);
                        var before = at > 0 ? lane.Clips[at - 1] : null;
                        var meets = before is not null && Math.Abs(before.End - parts[0].Start) <= Slack;
                        parts[0].TransitionIn = meets && clip.TransitionIn > 0 ? Math.Min(clip.TransitionIn, Math.Min(before!.Duration, parts[0].Duration)) : 0;
                        lanes[ordinal] = (parts[0], parts[^1], clip.TransitionIn <= 0.01 || meets);
                    }

                    made[clip] = lanes;
                    (clip.AudioStreamIndex, clip.AudioSlipSeconds, clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) = (-1, 0, 0, 0);
                    clip.State.HasAudio = false;
                }
            }
        }

        // Crossfades lanes cannot carry become audio dips (matching legacy LowerAfresh/LayOutCrossfades behaviour).
        foreach (var track in document.Tracks)
        {
            for (var i = 0; i < track.Clips.Count; i++)
            {
                var after = track.Clips[i];
                if (after.TransitionIn <= 0.01 || after.Duration <= 0.001 || Before(track, i) is not { } before)
                    continue;

                var (afterLanes, beforeLanes) = (made.GetValueOrDefault(after), made.GetValueOrDefault(before));
                if (afterLanes is null && beforeLanes is null)
                    continue;

                var half = Math.Min(after.TransitionIn, Math.Min(before.Duration, after.Duration)) / 2;
                for (var ordinal = 0; ordinal < plan.Streams.Count; ordinal++)
                {
                    var (into, outOf) = (afterLanes?[ordinal], beforeLanes?[ordinal]);
                    if (into is { Carried: true })
                        continue;
                    if (into is { } first)
                        first.First.FadeInSeconds = Math.Max(first.First.FadeInSeconds, half);
                    if (outOf is { } last)
                        last.Last.FadeOutSeconds = Math.Max(last.Last.FadeOutSeconds, half);
                }
            }
        }

        foreach (var lane in touched)
        {
            foreach (var clip in lane.Clips)
                HoldFades(clip);
            Reconcile(lane);
        }

        // Audio-only file: replace each clip with its first stream's first audio clip (inheriting id and children); clips without
        // one become standalone audio clips.
        if (plan.Context.SourceHasNoVideo)
        {
            foreach (var track in document.Tracks.ToList())
            {
                foreach (var clip in track.Clips.Where(plan.IsClocked).ToList())
                {
                    if (made.GetValueOrDefault(clip)?.FirstOrDefault(l => l is not null) is not { } heir)
                    {
                        clip.ContentKind = LayerKind.Audio;
                        var look = clip.State.ShallowCopy();
                        look.Kind = LayerKind.Audio;
                        clip.State = look;
                        clip.AudioStreamIndex = plan.Streams.Count > 0 ? plan.Streams[0].Stream.Index : 0;
                        track.Clips.Remove(clip);
                        Insert(LaneFor(document, track, 0, null, clip.Start, clip.End, plan, needsRoom: true), clip);
                        continue;
                    }

                    track.Clips.Remove(clip);
                    (heir.First.Id, heir.First.ParentClipId, heir.First.Tethered) = (clip.Id, null, false);
                }
            }
        }
    }

    /// <summary>
    /// Audio clips for one stream of a clip with legacy timing (SoundOfChain, AddMainVoices): span widened by J/L cuts
    /// (<paramref name="widen"/>), shifted by clip and stream slip, clamped to file and Sequence, split at muted ranges (muted parts).
    /// Children of the clip with its and its track's gain, disabled/muted state and voice flag. Unplaced; empty if nothing remains.
    /// </summary>
    private static List<TimelineClip> LaneClips(TimelineClip clip, TimelineTrackModel? track, int ordinal, AudioTrack stream, Plan plan, bool widen)
    {
        var speed = clip.Speed > 0 && double.IsFinite(clip.Speed) ? clip.Speed : 1;
        var (lead, tail, slip) = !widen ? (0.0, 0.0, 0.0) : clip.AudioLinked ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds, 0.0) : (0.0, 0.0, clip.AudioSlipSeconds);
        var (start, end) = (clip.Start - lead + slip, clip.End + tail + slip);
        var offset = clip.MediaOffset - lead * speed - stream.OffsetSeconds;

        // Never before the file's start, nor the Sequence's: what would play there is left out. Nor past the file's end.
        if (offset < 0)
            (start, offset) = (start - offset / speed, 0);
        if (start < 0)
            (offset, start) = (offset - start * speed, 0);
        var length = plan.LengthOf(clip);
        if (length > 0 && offset + (end - start) * speed > length)
            end = start + (length - offset) / speed;
        if (end - start <= Sliver)
            return [];

        // Where the stream was silenced, on the Sequence (its pieces are in the file's own time, on the container's clock).
        var silenced = new List<(double From, double To)>();
        foreach (var piece in stream.Pieces)
        {
            if (!piece.Muted)
                continue;
            var (from, to) = (start + (piece.Start - plan.Shift - offset) / speed, start + (piece.End - plan.Shift - offset) / speed);
            (from, to) = (Math.Max(from, start), Math.Min(to, end));
            if (to - from > Sliver)
                silenced.Add((from, to));
        }

        silenced.Sort();
        var spans = new List<(double From, double To, bool Muted)>();
        void Add(double from, double to, bool muted)
        {
            if (to - from <= 0)
                return;
            if (spans.Count > 0 && (spans[^1].Muted == muted || to - from < Sliver))
                spans[^1] = spans[^1] with { To = to };
            else if (spans.Count > 0 && spans[^1].To - spans[^1].From < Sliver)
                spans[^1] = (spans[^1].From, to, muted);
            else
                spans.Add((from, to, muted));
        }

        var cursor = start;
        foreach (var (from, to) in silenced)
        {
            Add(cursor, Math.Max(from, cursor), false);
            Add(Math.Max(from, cursor), Math.Max(to, cursor), true);
            cursor = Math.Max(to, cursor);
        }

        Add(cursor, end, false);
        var gain = clip.GainDb + (track?.GainDb ?? 0);
        var parts = new List<TimelineClip>(spans.Count);
        for (var k = 0; k < spans.Count; k++)
        {
            var (from, to, muted) = spans[k];
            var info = stream.Stream;
            var part = new TimelineClip
            {
                Id = Derived(clip.Id, 2, ordinal, k),
                ContentKind = LayerKind.Audio,
                SourcePath = clip.SourcePath,
                AudioStreamIndex = stream.Index,
                Start = from,
                End = to,
                MediaOffset = Math.Max(offset + (from - start) * speed, 0),
                Speed = clip.Speed,
                SourceDuration = length,
                FadeInSeconds = k == 0 ? clip.FadeInSeconds : 0,
                FadeOutSeconds = k == spans.Count - 1 ? clip.FadeOutSeconds : 0,
                FadeInStyle = clip.FadeInStyle,
                FadeOutStyle = clip.FadeOutStyle,
                GainDb = gain,
                IsHidden = clip.IsHidden,
                IsMuted = clip.IsMuted || track?.IsMuted == true || muted,
                ParentClipId = clip.Id,
                State = new LayerState
                {
                    Name = stream.Title, Kind = LayerKind.Audio, ImagePath = clip.SourcePath ?? "", HasAudio = true, MediaDuration = length, IsVoice = stream.IsVoice,
                    AudioCodec = info.Codec, AudioChannels = info.ChannelLayout.Length > 0 ? info.ChannelLayout : $"{info.Channels} ch",
                    StartTime = from, Duration = to - from, Speed = clip.Speed,
                },
            };
            part.State.MediaOffset = part.MediaOffset;
            HoldFades(part);
            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// Sub-Lane for a stream ordinal: one created by this conversion with room; else an existing neutral lane (no own gain, filters,
    /// ducking or mute) with room; else a new top lane (as UnpackStreams) with the stream's legacy mixing.
    /// </summary>
    /// <param name="stream">Stream whose mixing a new lane inherits; null for neutral.</param>
    private static TimelineTrackModel LaneFor(TimelineDocument document, TimelineTrackModel track, int ordinal, AudioTrack? stream, double from, double to,
        Plan plan, bool needsRoom)
    {
        bool Room(TimelineTrackModel lane) => !needsRoom || lane.ClipsIntersecting(from + Slack, to - Slack).IsEmpty;
        var key = (track.Id, ordinal);
        if (plan.Lanes.TryGetValue(key, out var ours))
        {
            foreach (var lane in ours)
            {
                if (Room(lane))
                    return lane;
            }
        }

        var (gain, duck, muted) = (stream?.GainDb ?? 0, stream?.AutoDuck ?? false, stream?.IsMuted ?? false);
        var filtered = stream?.Filters.IsActive == true;
        foreach (var lane in document.Tracks)
        {
            if (lane is { Kind: TimelineTrackKind.Audio, IsLocked: false, IsSoloed: false } && lane.ParentTrackId == track.Id && lane.StreamOrdinal == ordinal
                && ours?.Contains(lane) != true && Math.Abs(lane.GainDb - gain) < 1e-9 && lane.DuckAgainstVoice == duck && lane.IsMuted == muted
                && !filtered && lane.AudioFilters?.IsActive != true && Room(lane))
            {
                (ours ??= plan.Lanes[key] = []).Add(lane);
                return lane;
            }
        }

        var made = new TimelineTrackModel
        {
            Id = Derived(track.Id, 3, ordinal, ours?.Count ?? 0),
            Kind = TimelineTrackKind.Audio,
            Name = stream?.Title ?? $"Stream {ordinal + 1}",
            StreamOrdinal = ordinal,
            ParentTrackId = track.Id,
            GainDb = gain,
            AudioFilters = filtered ? stream!.Filters.Clone() : null,
            DuckAgainstVoice = duck,
            IsMuted = muted,
        };
        document.Tracks.Add(made);
        (ours ??= plan.Lanes[key] = []).Add(made);
        return made;
    }

    /// <summary>
    /// Clip whose end abuts the start of the clip at <paramref name="index"/> within the legacy 11 ms tolerance, or null
    /// (TimelineLowering.Before).
    /// </summary>
    private static TimelineClip? Before(TimelineTrackModel track, int index)
    {
        var after = track.Clips[index];
        static bool Meets(TimelineClip clip, TimelineClip after) => clip.Duration > 0.001 && Math.Abs(clip.End - after.Start) < 0.011;
        if (index > 0 && Meets(track.Clips[index - 1], after))
            return track.Clips[index - 1];

        for (var j = track.Clips.Count - 1; j >= 0; j--)
        {
            if (j != index && Meets(track.Clips[j], after))
                return track.Clips[j];
        }

        return null;
    }

    // ----- What was folded out, and the recycle bin -----

    /// <summary>
    /// Converts a fold's clips like the document's (times stay seam-relative): MainVideo clips to VideoFile with their stream audio
    /// clips stored in the fold on their track's lanes (created if needed); cut-outs on the clock of their fold source (others
    /// disabled). J/L cuts and slips are not widened, so unfolded content fits its original range.
    /// </summary>
    private static List<FoldedClip> Folded(FoldedSegment fold, TimelineDocument document, Plan plan)
    {
        var kept = new List<FoldedClip>(fold.Clips.Count);
        var originals = new Dictionary<Guid, (TimelineClip Clip, bool Clocked)>();
        foreach (var folded in fold.Clips)
        {
            if (folded.Clip is { ContentKind: LayerKind.MainVideo } clip)
                originals.TryAdd(clip.Id, (clip, folded.TrackId == plan.SoundTrack?.Id && plan.Rides(clip) || (plan.Context.SourceHasNoVideo && plan.IsOf(clip))));
        }

        foreach (var folded in fold.Clips)
        {
            var copy = folded.Clip.Snapshot();
            if (copy.ContentKind == LayerKind.MainVideo)
            {
                var clocked = originals[copy.Id].Clocked;
                ToFileClip(copy, clocked, clocked && !copy.IsHidden, plan);
                kept.Add(folded with { Clip = copy });
                var track = document.Tracks.Find(t => t.Id == folded.TrackId);
                if (!clocked || copy.AudioStreamIndex < 0 || track is null)
                    continue;

                for (var ordinal = 0; ordinal < plan.Streams.Count; ordinal++)
                {
                    var lane = LaneFor(document, track, ordinal, plan.Streams[ordinal], 0, 0, plan, needsRoom: false);
                    foreach (var part in LaneClips(copy, track, ordinal, plan.Streams[ordinal], plan, widen: false))
                        kept.Add(new FoldedClip(lane.Id, TimelineTrackKind.Audio, part));
                }

                (copy.AudioStreamIndex, copy.AudioSlipSeconds, copy.AudioLeadInSeconds, copy.AudioTailOutSeconds) = (-1, 0, 0, 0);
                copy.State.HasAudio = false;
            }
            else if (copy.ContentKind == LayerKind.Video)
            {
                // Its parent folded with it, whose times are counted from the same start: before it was converted.
                TimelineClip? over = null;
                if (copy.ParentClipId is { } id && originals.TryGetValue(id, out var parent) && parent.Clip.Start <= copy.Start + Edge && parent.Clip.End >= copy.End - Edge)
                {
                    over = parent.Clip.Snapshot();
                    over.MediaOffset = plan.OffsetOf(parent.Clip, parent.Clocked);
                    over.SourceDuration = plan.LengthOf(parent.Clip);
                }

                // Enabled over a chain clip (on its clock), disabled elsewhere.
                var shown = over is not null && originals[over.Id] is { Clocked: true, Clip.IsHidden: false };
                OnClock(copy, over, plan, PieceLook(copy.State, plan), 0);
                copy.IsHidden |= !shown;
                HoldFades(copy);
                kept.Add(folded with { Clip = copy });
            }
            else
            {
                kept.Add(folded);
            }
        }

        return kept;
    }

    /// <summary>
    /// Converts a recycle-bin clip and its family: MainVideo to VideoFile keeping embedded audio (the bin has no lanes; reopening
    /// unpacks it), on the chain clock when eligible; cut-outs on their source clip's clock (binned partner, else document), disabled over none.
    /// </summary>
    /// <param name="binnedWith">Converted MainVideo clip binned with it, and whether it was a chain clip.</param>
    private static void Deleted(DeletedClip deleted, (TimelineClip Clip, bool Shown)? binnedWith, TimelineDocument document, Plan plan)
    {
        static bool Covers(TimelineClip clip, TimelineClip piece) => clip.Start <= piece.Start + Edge && clip.End >= piece.End - Edge;
        var clip = deleted.Clip;
        var parent = binnedWith;
        if (clip is { ContentKind: LayerKind.MainVideo })
        {
            var clocked = plan.Rides(clip) || (plan.Context.SourceHasNoVideo && plan.IsOf(clip));
            ToFileClip(clip, clocked, clocked && !clip.IsHidden, plan);
            parent = (clip, clocked && !clip.IsHidden);
        }
        else if (clip is { ContentKind: LayerKind.Video })
        {
            (TimelineClip Clip, bool Shown)? over = null;
            if (binnedWith is { } given && Covers(given.Clip, clip))
                over = given;
            else if (clip.ParentClipId is { } id && document.FindClip(id) is { ContentKind: LayerKind.VideoFile } found && Covers(found, clip))
                over = (found, !found.IsHidden);

            OnClock(clip, over?.Clip, plan, PieceLook(clip.State, plan), 0);
            clip.IsHidden |= over is not { Shown: true };
            HoldFades(clip);
        }

        foreach (var child in deleted.Children ?? [])
            Deleted(child, parent, document, plan);
    }

    // ----- Keeping the document whole -----

    /// <summary>Look with all keyframes and effect ranges shifted by <paramref name="seconds"/> (TimelineTransaction.Shifted); same instance when zero.</summary>
    private static LayerState Shifted(LayerState look, double seconds)
    {
        static bool Timed(LayerState state) =>
            state.KeysX is { Count: > 0 } || state.KeysY is { Count: > 0 } || state.KeysScale is { Count: > 0 } || state.KeysRotation is { Count: > 0 }
            || state.KeysOpacity is { Count: > 0 } || state.Effects?.Any(e => e.Keys is { Count: > 0 } || e.Range is not null) == true;
        if (Math.Abs(seconds) < 1e-9 || !Timed(look))
            return look;

        List<Keyframe>? Shift(List<Keyframe>? keys) => keys?.Select(k => k with { Time = Math.Round(k.Time + seconds, 6) }).ToList();
        var shifted = TimelineTransaction.CopyOf(look);
        (shifted.KeysX, shifted.KeysY, shifted.KeysScale) = (Shift(shifted.KeysX), Shift(shifted.KeysY), Shift(shifted.KeysScale));
        (shifted.KeysRotation, shifted.KeysOpacity) = (Shift(shifted.KeysRotation), Shift(shifted.KeysOpacity));
        foreach (var effect in shifted.Effects ?? [])
        {
            if (effect.Keys is { } keys)
            {
                foreach (var name in keys.Keys.ToList())
                    keys[name] = Shift(keys[name]) ?? [];
            }

            if (effect.Range is [var from, var to] && double.IsFinite(from + seconds) && double.IsFinite(to + seconds))
                effect.Range = [Math.Round(from + seconds, 6), Math.Round(to + seconds, 6)];
        }

        return shifted;
    }

    /// <summary>Clamps fades to clip length (fade-in priority) and the maximum fade, as TimelineTransaction.Respeed.</summary>
    private static void HoldFades(TimelineClip clip)
    {
        var length = Math.Max(clip.Duration, 0);
        clip.FadeInSeconds = Math.Clamp(clip.FadeInSeconds, 0, Math.Min(length, TimelineDocument.LongestFade));
        clip.FadeOutSeconds = Math.Clamp(clip.FadeOutSeconds, 0, Math.Min(length - clip.FadeInSeconds, TimelineDocument.LongestFade));
    }

    /// <summary>Sorted insert by start (TimelineTransaction.Insert); ties go after existing clips.</summary>
    private static void Insert(TimelineTrackModel track, TimelineClip clip)
    {
        int low = 0, high = track.Clips.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (track.Clips[middle].Start <= clip.Start)
                low = middle + 1;
            else
                high = middle;
        }

        track.Clips.Insert(low, clip);
    }

    /// <summary>Drops crossfades without abutment and caps the rest (TimelineTransaction.Reconcile).</summary>
    private static void Reconcile(TimelineTrackModel track)
    {
        for (var i = 0; i < track.Clips.Count; i++)
        {
            var clip = track.Clips[i];
            if (clip.TransitionIn == 0)
                continue;

            var before = i > 0 ? track.Clips[i - 1] : null;
            var longest = before is null || Math.Abs(before.End - clip.Start) > Slack ? 0 : Math.Min(before.Duration, clip.Duration);
            clip.TransitionIn = Math.Min(clip.TransitionIn, Math.Min(longest, TimelineDocument.LongestCrossfade));
        }
    }

    /// <summary>Each track numbered among those of its kind, from 1 at the bottom (TimelineTransaction.Renumber).</summary>
    private static void Renumber(TimelineDocument document)
    {
        foreach (var kind in Enum.GetValues<TimelineTrackKind>())
        {
            var number = 0;
            foreach (var track in document.Tracks)
            {
                if (track.Kind == kind)
                    track.TrackNumber = ++number;
            }
        }
    }

    /// <summary>
    /// Deterministic derived id (cut-out part, audio clip, lane) from a source id and role, so converting an undo step later yields
    /// the same ids as the document (selection and id references stay valid).
    /// </summary>
    private static Guid Derived(Guid from, int what, int a, int b)
    {
        Span<byte> name = stackalloc byte[28];
        from.TryWriteBytes(name);
        BitConverter.TryWriteBytes(name[16..], what);
        BitConverter.TryWriteBytes(name[20..], a);
        BitConverter.TryWriteBytes(name[24..], b);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(name, hash);

        // A name-based id (RFC 4122: version 5's bits, the variant's).
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }
}
