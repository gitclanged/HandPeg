using System.IO;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services;

// ----- The main video, converted once as a project of 5.1 is opened (5.2-AuditB; back in the app since 5.2E) -----
// 5.2-AuditB wrote this for the app; 5.2-Arm64AndNpu made it the golden tests' alone and refused a 5.1 project instead, which left
// every 5.1.0 user's projects unopenable. Since 5.2E (the user's answer) the app converts such a project again as
// it is opened (MainViewModel.ConvertLegacy), and the window writes the converted project over its own file, under the same name
// (MainViewModel.WriteBackConverted); the golden tests' 4.2.0-shaped fixtures (LegacyFixtures) still reach the current model through
// it, so every export golden stays what it was, to the byte. What follows is 5.2-AuditB's account of it, unchanged.
//
// Until 5.2-AuditB one file was "the main video": its clips (kind MainVideo) were read as one chain on the lowest track that
// held one (TimelineLowering's CanRide: visible, video stream 0, its own sound or none, speed 1, the opened file), placed by the
// Center controls' box (CenterRect), cut by the first clip's crop, sized the Sequence by that crop, drew the blurred background
// under everything, and were heard through the opened file's streams as the Audio tab mixed them (AudioTracks: a gain, filters,
// ducking, a voice, a slip and silenced pieces each, on the file's container clock). Cut-outs of it (kind Video) showed that
// chain's picture at each moment. None of that is privileged now: every video clip reads its own file at its own offset, with
// its own look, blur fill, Cutouts and Sub-Lanes. This turns a document of the old kinds into one of the new, once, in place
// (no undo step), so that nothing after it ever meets the old kinds, and so that what it plays is what it played: the same
// boxes, crops, moments of the file and Cutout pictures (the lowering's arithmetic, done once here), the same sound on Sub-Lanes.

/// <summary>
/// What <see cref="LegacyMainVideo.Migrate"/> needs to know besides the document: the file the project opened (the old main
/// video) as it was probed, the switches and the frame the old main video was laid out with, and its streams as they were mixed.
/// </summary>
public sealed record LegacyMainVideoContext
{
    /// <summary>The project's opened file as it is read now (ProjectState.LocalMediaPath, the download behind a web address, after any move); "" for none.</summary>
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

    /// <summary>The Sequence frame the old Center box was laid in (the view model's FrameWidth and FrameHeight); 0 while unknown.</summary>
    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    /// <summary>
    /// The old Center box in frame pixels (the view model's GetCenterRect: where the chain's first clip was drawn); null
    /// while unknown, and then worked out from that clip's place as the view model worked it out (MainPlaceOf, CenterRectOf).
    /// </summary>
    public (int X, int Y, int Width, int Height)? CenterRect { get; init; }

    /// <summary>
    /// The opened file's audio streams with their old mixing (gain, filters, ducking, voice, slip, silenced pieces), in stream
    /// order: the place of each in this list is its ordinal. Their mixing is set back to neutral by a migration that converts
    /// the document (pass copies for an undo step's document; the live ones for the project being opened).
    /// </summary>
    public IReadOnlyList<AudioTrack>? Streams { get; init; }

    /// <summary>
    /// The recycle bin that goes with the document (its clips are put back onto it): converted with it, its clips in place.
    /// Null for none. (An addition to the contract's shape: a clip of the old kinds put back from the bin would bring them back.)
    /// </summary>
    public IReadOnlyList<DeletedClip>? Bin { get; init; }
}

/// <summary>The one-time conversion of a project's main video, and of the cut-outs of it, into clips of files (5.2-AuditB).</summary>
public static class LegacyMainVideo
{
    // Two edges this close are one (LinkToMainVideo's slack, as a piece's parent was found).
    private const double Edge = 0.0005;

    // A time this close to another is the same one (TimelineTransaction's slack).
    private const double Slack = 1e-6;

    // A part of a sound shorter than this is not made: it goes to its neighbour.
    private const double Sliver = 0.001;

    /// <summary>Whether a document still holds clips of the legacy kinds (MainVideo, or a Video cut-out of it), on its tracks or in its folds.</summary>
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
    /// Converts them in place (no undo step). Idempotent: a document that does not Need it is left alone (its bin, given in the
    /// context, is still looked at). See the file's header and the steps below; the document's tracks stay in order and apart.
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

            // 3. The main video's clips: each a video-file clip of its file, in the box it was drawn in, on its picture's clock.
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

            // (What was folded out of it: the same, kept for the fold to put back.)
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

    /// <summary>A caption source of the legacy kind (the main video's file) as the kind every file's is now: the same file and stream, a clip's.</summary>
    /// <param name="openedPath">The file the project opened now (the download may lie elsewhere than when it was saved); null keeps the source's own path.</param>
    public static CaptionSource? MigrateCaption(CaptionSource? source, string? openedPath = null) =>
        source is { Kind: CaptionSourceKind.MainFile }
            ? source with { Kind = CaptionSourceKind.Clip, Path = string.IsNullOrWhiteSpace(openedPath) ? source.Path : openedPath }
            : source;

    /// <summary>A subtitle track's caption source converted in place (<see cref="MigrateCaption"/>); whether there was one to convert.</summary>
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

    /// <summary>A track whose clips are pictures laid on the canvas (a switch track holds what a video track holds).</summary>
    private static bool ShowsPictures(TimelineTrackModel track) => track.Kind is TimelineTrackKind.Video or TimelineTrackKind.Switch;

    // The view model's evening (MainViewModel.Even, TimelineLowering.Even) and the layer's (Layer.Even, under its own Math.Max).
    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

    private static int LayerEven(double value) => (int)Math.Round(value / 2) * 2;

    /// <summary>What a crop leaves of a picture, in its pixels, even, as the crop filter cuts it (MainViewModel.CroppedSize).</summary>
    private static (int Width, int Height) CroppedSize((double Left, double Top, double Right, double Bottom) crop, int width, int height) =>
        (Math.Max((int)(width * (1 - crop.Left - crop.Right) / 2) * 2, 2), Math.Max((int)(height * (1 - crop.Top - crop.Bottom) / 2) * 2, 2));

    /// <summary>
    /// The old engine's reading of a document: the primary chain (TimelineLowering.CanRide), the clip the Sequence was placed,
    /// cropped and sized by, the Center box, the clips heard through the opened file's streams and their clock, read once.
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

            // What the main video's offsets were counted from (the container's clock) against what a file's are (its first
            // picture's): GpuScenePlan.ChainShiftOf. Nothing when either is not known.
            if (Primary.Length > 0 && MediaProbe.KnownVideoStart(Primary) is { } video && MediaProbe.KnownContainerStart(Primary) is { } container
                && double.IsFinite(video - container))
            {
                Shift = video - container;
            }

            // The chain (TimelineLowering.LowerAfresh): the lowest picture track with a visible clip that can ride it, and every
            // visible clip of that track that can.
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

            // The track the opened file's streams were heard on: the chain's, or with none (each of its clips switched off) the
            // lowest that holds one that would ride it. Its clips that could ride it, switched off or not, were on the chain's
            // clock; for a file with no picture, every clip of it was its sound.
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

            // The clip the Sequence was placed, cropped and sized by (MainViewModel.AnchorMainView): the chain's first, else
            // the main video's first.
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

        /// <summary>The clips on the chain's clock (the chain's, and those of its track switched off): heard through the streams.</summary>
        public HashSet<TimelineClip> Clocked { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Each main video clip's offset on its picture's clock, as it is converted.</summary>
        public Dictionary<TimelineClip, double> Offsets { get; } = new(ReferenceEqualityComparer.Instance);

        public TimelineClip? AnchorClip { get; }

        public LayerState? AnchorLook { get; }

        public (int X, int Y, int Width, int Height)? Center { get; }

        // The lanes made, by the video track they lie under and the stream place they hold.
        public Dictionary<(Guid Track, int Ordinal), List<TimelineTrackModel>> Lanes { get; } = new();

        /// <summary>The opened file's picture as the anchor clip's crop leaves it, even; null while its size is not known.</summary>
        public (int Width, int Height)? NaturalSize =>
            Source is { } source ? CroppedSize(AnchorLook is { } look ? Layer.HeldCrop(look) : default, source.Width, source.Height) : null;

        /// <summary>Whether a clip is of the opened file.</summary>
        public bool IsOf(TimelineClip clip) => Primary.Length > 0 && string.Equals(Clean(clip.SourcePath), Primary, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a clip of the main video could ride the chain, its switch aside (TimelineLowering.CanRide).</summary>
        public bool Rides(TimelineClip clip) =>
            clip.ContentKind == LayerKind.MainVideo && clip.VideoStreamIndex == 0 && clip.AudioStreamIndex <= 0 && Math.Abs(clip.Speed - 1) < 0.0005 && IsOf(clip);

        public bool IsClocked(TimelineClip clip) => Clocked.Contains(clip);

        /// <summary>
        /// A main video clip's offset on its picture's clock: the chain's offsets were on the container's (shifted to the
        /// picture's by GpuScenePlan and the sound engine); any other clip of it was read as a file, at its offset as it is.
        /// </summary>
        public double OffsetOf(TimelineClip clip, bool clocked) => clocked ? Math.Max(clip.MediaOffset - Shift, 0) : clip.MediaOffset;

        /// <summary>The offset a clip of the document has once converted (a clip not converted: its own).</summary>
        public double ClockOf(TimelineClip clip) => Offsets.TryGetValue(clip, out var offset) ? offset : clip.MediaOffset;

        /// <summary>A clip's file's length once converted: its own, else the opened file's for a clip of it.</summary>
        public double LengthOf(TimelineClip clip) => clip.SourceDuration > 0 ? clip.SourceDuration : IsOf(clip) ? Math.Max(Context.MediaSeconds, 0) : 0;

        /// <summary>
        /// The Center box as the view model worked it out from the clip it stood for (MainPlaceOf, then CenterRectOf): its zoom
        /// and offsets read back from its corner and width at its cropped shape, laid about the frame's middle. Null without a
        /// frame or a clip.
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
        /// The box a clip of the main video was drawn in, in frame pixels, as the old lowering laid it: a clip of the chain in the
        /// Center box (the one piece, MakeRider), any other in the Center box at its source's own shape (MakeLayer); one placed
        /// or cropped otherwise than the anchor in a box of its own (MainRectOf). Then as its layer came out of it
        /// (Layer.GetOutputRect: corner rounded, sides evened). Null without a frame or a Center box: its look stays as it is.
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

        /// <summary>Whether a main video clip was placed (its corner, its width) and cropped otherwise than the anchor (TimelineLowering.OwnOf).</summary>
        private (bool Place, bool Crop) OwnOf(TimelineClip clip, LayerState look)
        {
            if (AnchorLook is not { } anchor || ReferenceEquals(clip, AnchorClip) || ReferenceEquals(look, anchor))
                return (false, false);

            static bool Differ(double a, double b) => Math.Abs(a - b) > 1e-9;
            var place = Differ(look.PositionX, anchor.PositionX) || Differ(look.PositionY, anchor.PositionY) || Differ(look.SizeWidth, anchor.SizeWidth);
            var (crop, anchorCrop) = (Layer.HeldCrop(look), Layer.HeldCrop(anchor));
            return (place, Differ(crop.Left, anchorCrop.Left) || Differ(crop.Top, anchorCrop.Top) || Differ(crop.Right, anchorCrop.Right) || Differ(crop.Bottom, anchorCrop.Bottom));
        }

        /// <summary>The box of a main video clip placed by its own look: its corner and width, its height at its cropped shape (TimelineLowering.MainRectOf).</summary>
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
    /// The natural size a document of the legacy kinds had (MainViewModel.GetCroppedSourceSize): the opened file's picture as
    /// the clip the Sequence was sized by (the chain's first, else the main video's first) leaves it with its crop, even,
    /// before Use Vertical Resolution turns it. Null while the file's size is not known (the frame was then 1920 x 1080). For
    /// the view model to work out the frame the old Center box was laid in (<see cref="LegacyMainVideoContext.FrameWidth"/>)
    /// before it converts; read from the document as it is (only <see cref="LegacyMainVideoContext.PrimaryPath"/> and the
    /// source's size of the context are read).
    /// </summary>
    public static (int Width, int Height)? NaturalSizeOf(TimelineDocument document, LegacyMainVideoContext context) =>
        new Plan(document, context).NaturalSize;

    /// <summary>The size the Sequence had (<see cref="NaturalSizeOf"/>), never over one already set; left unset while it is not known.</summary>
    private static void TakeNaturalSize(TimelineDocument document, Plan plan)
    {
        if (document.NaturalWidth <= 0 && plan.NaturalSize is { } natural)
            (document.NaturalWidth, document.NaturalHeight) = natural;
    }

    // ----- 2. The cut-outs -----

    /// <summary>
    /// A cut-out of the main video (it showed the chain's picture at each moment, nothing where the chain had no clip) as video
    /// file clips: one for each clip of the chain it lies over (split at their edges), each that clip's child on its clock, cut
    /// from its picture as the cut-out was (FileCutoutOf); a part over no clip of the chain is switched off (it showed nothing).
    /// The first part keeps the cut-out's id, fade in and crossfade, the last its fade out; every part's keys are where they were.
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
    /// The stretches of a cut-out between the edges of the chain's clips, each with the clip whose picture it showed there (a
    /// clip of the chain that covers it, its parent first), shown; or, over none, the clip of the main video it lies over (its
    /// parent first) to keep its clock, switched off; or none.
    /// </summary>
    private static List<(double From, double To, TimelineClip? Over, bool Shown)> PartsOf(TimelineClip piece, Plan plan)
    {
        var (start, end) = (piece.Start, piece.End);
        var parent = piece.ParentClipId is { } id ? plan.Document.FindClip(id) : null;
        static bool Covers(TimelineClip clip, double from, double to) => clip.Start <= from + Edge && clip.End >= to - Edge;

        // Where the chain's clips start or end inside it (none when its parent of the chain covers it whole), two edges
        // closer than a slack one: each stretch between two of them is longer than that.
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
    /// A cut-out's look as a piece of a clip of a file (MainViewModel.FileCutoutOf): that file's picture, cut by its crop to the
    /// same part of it (the cut-out's source rectangle less its crop) and laid in the box the cut-out had, its mask, cut the
    /// same, still shaping it. A crop leaves at least <see cref="Layer.LargestCropTotal"/>'s remainder of a picture: a piece
    /// narrower than that is cut wider about its middle and its box made as much larger. Its blur fill off.
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

        var box = cut.GetOutputRect(frameWidth, frameHeight, sourceWidth, sourceHeight);
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
    /// A part of a cut-out on the clock of the clip whose picture it shows (<paramref name="over"/>; none: the opened file from
    /// its start, switched off): that clip's file, stream and speed, at the moment of it under the part's start, silent, its
    /// look <paramref name="look"/> with its keys moved back by <paramref name="shift"/> (the part's start after the cut-out's).
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
    /// A clip of the main video as a clip of its file: kind VideoFile (the look's too), its file named in its look with the
    /// opened file's size and length where it has none, its blur fill what it was (a clip of the chain's clock had the blurred
    /// background where it left it to the Background switch; any other had none), "Main Video" named after its file; the box
    /// it was drawn in written into its look (<see cref="Plan.BoxOf"/>); its offset on its picture's clock. A look is never
    /// changed in place: it gets a copy.
    /// </summary>
    /// <param name="clocked">On the chain's clock: its offset was the container's, its blur fill followed the switch.</param>
    /// <param name="chained">Drawn as the chain's (the Center box), not as a layer of its own.</param>
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
    /// Whether a look's box, held to its picture's shape (LockAspectRatio), is <paramref name="height"/> tall at
    /// <paramref name="width"/>, whichever way the layer rounds a cropped picture (its shape's, Layer.GetProportionalHeight, or
    /// the crop filter's evened sides): then it keeps that shape as it is resized or cropped; otherwise its height is its own
    /// (SizeHeight), the box exactly as it was drawn.
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
    /// The opened file's streams on Sub-Lanes, as MainViewModel.UnpackStreams lays a video file's (one lane under the clip's
    /// track for each stream place, a sound clip of each stream the clip's child, the clip itself silent), for every clip that
    /// was heard through them, even with one stream: each lane takes its stream's old mixing, each sound clip its clip's place
    /// in time as the old engine heard it (SoundOfChain, AddMainVoices). Then the crossfades a lane cannot carry dip, as they
    /// did; then a file with no picture keeps only its sound.
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

        // A crossfade the lanes do not carry dips, sound in and out, through what is under it (as LowerAfresh dipped a crossfade
        // between a clip of the chain and another, and as LayOutCrossfades dipped one with nothing beyond the cut).
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

        // A file with no picture: its clips are their sound alone. Each goes, its first stream's first sound clip taking its id
        // (and so its children, the other streams' sound clips among them); one that has none is made a sound clip of its own.
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
    /// The sound clips of one stream of a clip, as the old engine heard that stream there (SoundOfChain, AddMainVoices): from
    /// the clip's start less its J-cut to its end and L-cut (<paramref name="widen"/>), moved by its unlinked slip, the stream's
    /// own slip taking it that much earlier in the file; cut to what the file and the Sequence have; then split where the
    /// stream was silenced (its muted pieces, in its own time), those parts muted. Each a child of the clip, at its gain and
    /// its track's, switched off and muted with it, a voice when the stream was. Not placed. Empty when nothing is left.
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
    /// The lane under a video track for a stream place: one this conversion made for it, with room; else one already there
    /// for that place that mixes as the stream did (no gain, filters, ducking or mute of its own to change another file's
    /// sound) and has room; else a new one at the top, as UnpackStreams inserts it, with the stream's old mixing.
    /// </summary>
    /// <param name="stream">The stream whose mixing a new lane takes; null for a lane of no stream's (neutral).</param>
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
    /// The clip on a track whose end meets the start of the one at <paramref name="index"/> (as 4.2.0 counted meeting: within 11
    /// ms), or null (TimelineLowering.Before).
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
    /// A fold's clips, converted as the document's are (its times stay counted from the fold's start): the main video's clips
    /// as clips of their file, with their streams' sound clips kept in the fold too, on the lanes of their track (made when the
    /// document has none), to come back with them; its cut-outs on the clock of the clip of the fold they were cut from (any
    /// other switched off). J and L cuts and slips are not widened here: what comes back fits where it was folded from.
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

                // It showed the chain's picture: shown over a clip of the chain (on its clock, switched on), switched off otherwise.
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
    /// A clip in the recycle bin, and what went with it, converted as the document's are: a clip of the main video as a clip
    /// of its file (its own sound still on it: the bin has no lanes; a project opened again unpacks it), on the chain's clock
    /// when it could ride it; a cut-out on the clock of the clip it was cut from (the one it went to the bin with, else the
    /// document's), switched off over none.
    /// </summary>
    /// <param name="binnedWith">The clip of the main video it went to the bin with (converted), and whether its picture was the chain's.</param>
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

    /// <summary>A look with every keyframe, effect keyframe and effect range moved by <paramref name="seconds"/> (TimelineTransaction.Shifted): a copy, or the look itself when nothing moves.</summary>
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

    /// <summary>A clip's fades held to its length, the fade in first (as TimelineTransaction.Respeed holds them), and to the longest fade.</summary>
    private static void HoldFades(TimelineClip clip)
    {
        var length = Math.Max(clip.Duration, 0);
        clip.FadeInSeconds = Math.Clamp(clip.FadeInSeconds, 0, Math.Min(length, TimelineDocument.LongestFade));
        clip.FadeOutSeconds = Math.Clamp(clip.FadeOutSeconds, 0, Math.Min(length - clip.FadeInSeconds, TimelineDocument.LongestFade));
    }

    /// <summary>Puts a clip on a track among its clips by its start (TimelineTransaction.Insert): a clip that starts with another goes after it.</summary>
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

    /// <summary>A crossfade stays only where its clip meets the one before it, never longer than either (TimelineTransaction.Reconcile).</summary>
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
    /// An id made from another (a cut-out's part from the cut-out's, a sound clip from its clip's, a lane from its track's) and
    /// what it is of it: the same every time the same document is converted, so that an undo step converted when it is stepped
    /// to names the same parts as the document did (the selection and anything that names a clip by its id follow it).
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
