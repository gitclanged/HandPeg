using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services.Gpu;

// ----- Making a plan: the lowering read once into what every frame is evaluated from -----
public sealed partial class GpuScenePlan
{
    private enum EntryKind : byte
    {
        /// <summary>The primary chain's one piece (the main layer), in the main video's box.</summary>
        Center,

        /// <summary>A layer cut whole from the chain's stream (a rider) or a piece cut out of it (a HUD cut-out, LayerKind.Video).</summary>
        Piece,

        /// <summary>A video read from its own file.</summary>
        File,

        /// <summary>A picture from a file.</summary>
        Still,

        /// <summary>Words, drawn into a still.</summary>
        Text,

        /// <summary>An adjustment clip: its Look effects on the picture under its box.</summary>
        Adjustment,

        /// <summary>The blurred background, made of the chain's frames, under everything.</summary>
        Background,

        /// <summary>A video's blurred copy covering the frame, just under it (its blur fill).</summary>
        Twin,

        /// <summary>What is done to the whole finished picture: the denoise, the LUT, the colour sliders, the Sequence Output's effects.</summary>
        Whole,

        /// <summary>The whole picture's fade from and to black.</summary>
        Fade,

        /// <summary>The subtitle track's pictures (a PGS or VobSub track: the preview's alone), over the main video's picture.</summary>
        PictureSubtitles,

        /// <summary>The captions, on top of everything.</summary>
        Caption,
    }

    /// <summary>A step of the key and matte zone: a card (a key, a choke, a smoothing), or the mask and edges (null), where the first of their cards is.</summary>
    private readonly record struct MatteStep(EffectInstance? Card);

    /// <summary>One thing the plan may draw, everything about it worked out when the plan was made. Never changed after.</summary>
    private sealed class Entry
    {
        public EntryKind Kind;
        public Layer? Layer;

        // Who it is: for the 3D view's labels and picking, and the worker's per-clip history.
        public long ClipKey;
        public string ClipId = "";
        public int TrackIndex = -1;
        public string Name = "";
        public bool HasMask;

        // When: FFmpeg's between(t, From, To), both ends in (the end left out where the file's frames run out).
        public double From = double.NegativeInfinity, To = double.PositiveInfinity;
        public bool ToExclusive;

        // What it reads (its source's path hash as the source table held it when the plan was made).
        public int SourceId = -1;
        public long PathHash;
        public double MediaStart, MediaOffset, Speed = 1, MediaDuration;
        public bool Loops;
        public GpuResourceRef Still = GpuResourceRef.None;

        // Where: its box on the canvas as set, its keys (counted from KeyStart), its anchor, turn and opacity.
        public float X, Y, W, H;
        public double Shape = 1, KeyStart;
        public float AnchorX = 0.5f, AnchorY = 0.5f, Rotation, Opacity = 1;
        public bool Turns;
        public Keyframe[]? KeysX, KeysY, KeysScale, KeysRotation, KeysOpacity;

        // The part of its source shown, as fractions: left, top, right and bottom edges.
        public float CropLeft, CropTop, CropRight = 1, CropBottom = 1;

        // What is done to it.
        public int BlendMode;
        public EffectInstance[] Look = [];
        public MatteStep[] Matte = [];
        public EffectInstance? Shadow;
        public GpuEffectPacket Edges;
        public GpuEffectPacket[] Packets = [];
        public GpuResourceRef Lut = GpuResourceRef.None, Curve = GpuResourceRef.None, Mask = GpuResourceRef.None, MaskStandIn = GpuResourceRef.None;
        public Fade[] Fades = [];

        // A blurred background's or a twin's blur (BlurRadius, BlurPasses, BackgroundDim).
        public float BlurRadius, BlurDim;
        public int BlurPasses;

        // The whole picture's fade; the captions; the subtitle track's pictures.
        public bool FadesIn, FadesOut;
        public TextRasterizer? Captions;
        public PictureLane? Pictures;
    }

    /// <summary>
    /// The primary chain on the sequence: its clips (the stream every piece of the main video is cut from), those of its one
    /// piece and of the blurred background, its crossfades, the document's clips it was made from, and where its moments
    /// lie from its file's own time. Binary searches over arrays: nothing allocated.
    /// </summary>
    private sealed class ChainMap(MainClip[] clips, MainClip[] center, MainClip[] blur, MainCrossfade[] crossfades, TimelineClip[] document, double shift)
    {
        public static ChainMap None { get; } = new([], [], [], [], [], 0);

        public MainClip[] Clips { get; } = clips;
        public MainClip[] Center { get; } = center;
        public MainClip[] Blur { get; } = blur;
        public MainCrossfade[] Crossfades { get; } = crossfades;
        public TimelineClip[] Document { get; } = document;
        public double Shift { get; } = shift;
        private readonly long[] _keys = Keys(document);

        /// <summary>The one piece, when it hides every pixel of the blurred background whenever it is on (BuildFrameEngine's hidesBackground).</summary>
        public Entry? CoveringCenter { get; set; }

        private static long[] Keys(TimelineClip[] document)
        {
            var keys = new long[document.Length];
            for (var i = 0; i < keys.Length; i++)
                keys[i] = GpuHash.Of(document[i].Id);
            return keys;
        }

        /// <summary>The clip of a list (in order, apart) that is on at a moment, from its start (in) to its end (not); -1 for none.</summary>
        public static int IndexAt(MainClip[] clips, double t)
        {
            int low = 0, high = clips.Length - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                if (clips[middle].Start <= t)
                    (found, low) = (middle, middle + 1);
                else
                    high = middle - 1;
            }

            return found >= 0 && t < clips[found].End ? found : -1;
        }

        /// <summary>The stream's moment at a moment of the sequence (the player's time-pos of the primary file); false between its clips.</summary>
        public bool StreamAt(double t, out double moment)
        {
            var at = IndexAt(Clips, t);
            moment = at >= 0 ? Clips[at].Offset + (t - Clips[at].Start) : 0;
            return at >= 0;
        }

        /// <summary>A crossfade's head at a moment: the later clip's moment of the stream, and how far it has come up (0 to 1).</summary>
        public bool HeadAt(double t, out double head, out double ramp)
        {
            for (var k = 0; k < Crossfades.Length; k++)
            {
                var crossfade = Crossfades[k];
                if (t >= crossfade.From && t < crossfade.To)
                {
                    head = crossfade.Source + (t - crossfade.From);
                    ramp = Math.Clamp((t - crossfade.From) / Math.Max(crossfade.To - crossfade.From, 0.001), 0, 1);
                    return true;
                }
            }

            (head, ramp) = (0, 0);
            return false;
        }

        /// <summary>The first start of a clip of the stream after a moment; infinity for none.</summary>
        public double NextStartAfter(double t)
        {
            int low = 0, high = Clips.Length - 1, found = Clips.Length;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                if (Clips[middle].Start > t)
                    (found, high) = (middle, middle - 1);
                else
                    low = middle + 1;
            }

            return found < Clips.Length ? Clips[found].Start : double.PositiveInfinity;
        }

        /// <summary>The document's clip of the chain under a moment (the first when none is).</summary>
        public int DocumentAt(double t)
        {
            int low = 0, high = Document.Length - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                if (Document[middle].Start <= t)
                    (found, low) = (middle, middle + 1);
                else
                    high = middle - 1;
            }

            return found >= 0 && t < Document[found].End ? found : Document.Length > 0 ? 0 : -1;
        }

        public long KeyAt(double t) => DocumentAt(t) is >= 0 and var at ? _keys[at] : 0;

        public long HeadKeyAt(double t) => HeadAt(t, out _, out _) && HeadIndex(t) is >= 0 and var at ? _keys[at] : KeyAt(t);

        public TimelineClip? ClipAt(double t) => DocumentAt(t) is >= 0 and var at ? Document[at] : null;

        public TimelineClip? HeadClipAt(double t) => HeadIndex(t) is >= 0 and var at ? Document[at] : ClipAt(t);

        // The later clip of the crossfade at a moment: the one on at its end.
        private int HeadIndex(double t)
        {
            for (var k = 0; k < Crossfades.Length; k++)
            {
                if (t >= Crossfades[k].From && t < Crossfades[k].To)
                    return DocumentAt(Crossfades[k].To);
            }

            return -1;
        }
    }

    // ----- Create -----

    /// <summary>
    /// Makes the plan of a state (the UI thread, once per edit: a few hundred microseconds for a typical project, the
    /// lowering itself cached by its document as the graph's is). Every file is put in the source table and every texture
    /// asked of <paramref name="resources"/> here: text clips drawn at once, masks, LUTs and pictures made on a background
    /// lane (their layers come on as they are handed over: GpuResourceBroker.ResourcesChanged). Never throws: a plan that
    /// cannot be made is <see cref="Empty"/>, and why is in the log.
    /// </summary>
    public static GpuScenePlan Create(SequenceExportState state, GpuResourceBroker resources)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resources);
        try
        {
            var plan = new Maker(state, resources).Make();
            resources.Pump();
            return plan;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("The GPU engine's scene could not be planned", ex);
            return Empty;
        }
    }

    /// <summary>The making of one plan: the state, its lowering, and the entries it is read into.</summary>
    private sealed class Maker(SequenceExportState state, GpuResourceBroker resources)
    {
        private readonly GpuSceneLowering _low = FilterGraphBuilder.LowerForGpu(state);
        private readonly int _width = Math.Max(state.Timeline.Width, 2);
        private readonly int _height = Math.Max(state.Timeline.Height, 2);
        private int _primary = -1;

        // What has been said in the log about what the GPU engine leaves to the export: once a session, not once an edit.
        private static int _told;

        private static void TellOnce(int bit, string message)
        {
            if ((Interlocked.Or(ref _told, bit) & bit) == 0)
                AppLog.Write(message);
        }

        public GpuScenePlan Make()
        {
            resources.BeginPlan();
            var primary = state.HasSource ? state.LocalMediaPath.Trim().Trim('"') : "";
            if (primary.Length > 0 && _low.MainClips.Count > 0 && !state.SourceHasNoVideo)
                _primary = resources.SourceFor(primary, 0);

            var blurClips = state.BackgroundHidden ? _low.ForcedBlurClips : _low.BlurClips;
            var chain = new ChainMap(Sorted(_low.MainClips), Sorted(_low.CenterClips), Sorted(blurClips), [.. _low.MainCrossfades], [.. _low.ChainClips],
                ChainShiftOf(primary));

            var entries = new List<Entry>(_low.Stack.Count + 6);

            // The blurred background, under everything: made of the chain's frames, while its clips that want it are on.
            if (_primary >= 0 && blurClips.Count > 0)
                entries.Add(Background());

            for (var n = 0; n < _low.Stack.Count; n++)
            {
                if (Picture(_low.Stack[n]) is not { } entry)
                    continue;
                if (entry.Kind == EntryKind.Center && n == 0 && CoversWhenOn(_low.Stack[n]))
                    chain.CoveringCenter = entry;
                entries.Add(entry);
            }

            // The whole finished picture: the denoise, the LUT and the colour sliders, then the Sequence Output's own effects.
            if (Whole() is { } whole)
                entries.Add(whole);
            if (SequenceLook() is { } look)
                entries.Add(look);
            if (state.FadeIn || state.FadeOut)
                entries.Add(new Entry { Kind = EntryKind.Fade, FadesIn = state.FadeIn, FadesOut = state.FadeOut, Name = "Fade", ClipKey = FadeKey });
            if (state.PictureSubtitles is { Cues.Count: > 0 } pictureTrack)
                entries.Add(PictureSubtitlesEntry(pictureTrack, state, _width, _height, _primary >= 0));
            if (state.AutoCaptions && Captions() is { } captions)
                entries.Add(captions);

            foreach (var entry in entries)
            {
                if (entry.SourceId >= 0)
                    entry.PathHash = resources.PathHashOf(entry.SourceId);
            }

            return new GpuScenePlan(_width, _height, _low.SequenceSeconds, [.. entries], chain, resources)
            {
                PrimarySource = _primary,
                PrimaryPathHash = _primary >= 0 ? resources.PathHashOf(_primary) : 0,
                SourceWidth = Math.Max(state.SourceWidth, 2),
                SourceHeight = Math.Max(state.SourceHeight, 2),
            };
        }

        private static readonly long FadeKey = default(GpuHash).Add("fade").Value, CaptionKey = default(GpuHash).Add("captions").Value,
            WholeKey = default(GpuHash).Add("whole").Value, BackgroundKey = default(GpuHash).Add("background").Value;

        private static MainClip[] Sorted(IReadOnlyList<MainClip> clips)
        {
            var sorted = clips.ToArray();
            Array.Sort(sorted, static (a, b) => a.Start.CompareTo(b.Start));
            return sorted;
        }

        /// <summary>
        /// Where the primary chain's moments lie from its file's own time: its picture's start less its container's (the
        /// export's first input counts from the container's start, the worker from the first frame). 0 while either is not known.
        /// </summary>
        private double ChainShiftOf(string primary)
        {
            if (primary.Length == 0)
                return 0;
            double? video = state.MediaStarts.TryGetValue(primary, out var known) ? known : MediaProbe.KnownVideoStart(primary);
            double? container = state.SeekIndexes.TryGetValue(primary, out var index) ? index.ContainerStart : MediaProbe.KnownContainerStart(primary);
            return video is { } start && container is { } clock && double.IsFinite(start - clock) ? start - clock : 0;
        }

        // ----- The stack -----

        private Entry? Picture(Layer layer)
        {
            if (layer.IsMainVideo)
                return Center(layer);
            if (_low.BlurTwins.Contains(layer))
                return Twin(layer);
            if (!layer.IsUsable)
                return null;

            return layer.Kind switch
            {
                LayerKind.Adjustment => Adjustment(layer),
                LayerKind.Video when _primary >= 0 => Piece(layer),
                LayerKind.VideoFile => File(layer),
                LayerKind.Image => Still(layer),
                LayerKind.Text => Text(layer),
                _ => null,
            };
        }

        /// <summary>The main video in one piece: in its box (the Center controls', CenterRect), cut by the sequence's crop.</summary>
        private Entry? Center(Layer layer)
        {
            var (x, y, w, h) = state.CenterRect;
            if (layer.IsHidden || _primary < 0 || _low.CenterClips.Count == 0 || w <= 0 || h <= 0)
                return null;

            var entry = new Entry { Kind = EntryKind.Center, SourceId = _primary };
            Picture(entry, layer, (x, y, w, h));
            Crop(entry, state.HasCrop ? state.CropFractions : default);
            entry.BlendMode = 0;
            entry.Fades = [];
            Masks(entry, layer, BuiltSize(entry, layer), state.CropFractions);
            return entry;
        }

        /// <summary>A clip of the chain riding alone (cut by its own crop or the sequence's), or a piece cut out of the frame (its source rectangle less its crop).</summary>
        private Entry Piece(Layer layer)
        {
            var entry = new Entry { Kind = EntryKind.Piece, SourceId = _primary };
            var rect = layer.GetOutputRect(_width, _height, state.SourceWidth, state.SourceHeight);
            Picture(entry, layer, rect);
            (double Left, double Top, double Right, double Bottom) pictureCrop;
            if (_low.Riders.Contains(layer))
            {
                pictureCrop = _low.StreamCrops.TryGetValue(layer, out var own) ? own : state.CropFractions;
                Crop(entry, _low.StreamCrops.ContainsKey(layer) ? own : state.HasCrop ? state.CropFractions : default);
            }
            else
            {
                pictureCrop = layer.CropFractions;
                var (sx, sy, sw, sh) = layer.CroppedSource;
                (entry.CropLeft, entry.CropTop) = ((float)Math.Clamp(sx, 0, 1), (float)Math.Clamp(sy, 0, 1));
                (entry.CropRight, entry.CropBottom) = ((float)Math.Clamp(sx + sw, 0, 1), (float)Math.Clamp(sy + sh, 0, 1));
            }

            Masks(entry, layer, BuiltSize(entry, layer), pictureCrop);
            return entry;
        }

        /// <summary>A video from its file (AppendVideoFile), cut by its crop (a clip of the main video laid as a file: its stream's own).</summary>
        private Entry? File(Layer layer)
        {
            var clip = _low.ClipOf.GetValueOrDefault(layer);
            var source = resources.SourceFor(FileToRead(layer, clip), clip?.VideoStreamIndex ?? 0);
            if (source < 0)
                return null;

            var entry = new Entry { Kind = EntryKind.File, SourceId = source };
            Picture(entry, layer, layer.GetOutputRect(_width, _height, state.SourceWidth, state.SourceHeight));
            Timing(entry, layer);
            var crop = FileCrop(layer);
            Crop(entry, crop);
            Masks(entry, layer, BuiltSize(entry, layer), _low.StreamCrops.TryGetValue(layer, out var own) ? own : layer.CropFractions);
            return entry;
        }

        /// <summary>
        /// The file a video layer (and its blurred twin) is read from: in the preview's state, its proxy when its clip asked for
        /// one (Optimize Long-GOP Clip for Scrubbing, 5.0-Verify) and it is made (<see cref="SequenceExportState.PreviewPaths"/>,
        /// of the first video stream, which is all a proxy holds); its own file otherwise, and always in an export. A proxy keeps
        /// the original's frames at their own times from its first one (ProxyCache.FrameTiming), so a moment of it is the same
        /// moment of the original; its picture is smaller, and every place and crop here is a fraction of the picture.
        /// </summary>
        private string FileToRead(Layer layer, TimelineClip? clip) =>
            state.IsPreview && (clip?.ProxyChoice ?? layer.ProxyChoice) == ProxyChoice.Make && (clip?.VideoStreamIndex ?? 0) == 0
            && state.PreviewPaths.TryGetValue(layer.ImagePath.Trim().Trim('"'), out var proxy) ? proxy : layer.ImagePath;

        /// <summary>The blurred copy a video from a file fills the frame with, just under it (TwinBlur): its file, timing, opacity and fades.</summary>
        private Entry? Twin(Layer layer)
        {
            if (layer.IsHidden || layer.ImagePath.Length == 0)
                return null;
            var clip = _low.ClipOf.GetValueOrDefault(layer);
            var source = resources.SourceFor(FileToRead(layer, clip), clip?.VideoStreamIndex ?? 0);
            if (source < 0)
                return null;

            var entry = new Entry { Kind = EntryKind.Twin, SourceId = source };
            Identify(entry, layer);
            Timing(entry, layer);
            entry.Layer = layer;
            (entry.KeyStart, entry.Opacity) = (layer.StartTime, Math.Clamp(layer.Opacity, 0, 100) / 100f);
            entry.KeysOpacity = Keys(layer, KeyProperty.Opacity);
            entry.Fades = Fades(layer, adjustment: false);

            // Its file cut by the clip's crop first, as AppendLayer cuts it before TwinBlur.
            Blur(entry, FileCrop(layer));
            entry.Name = layer.Name;
            return entry;
        }

        /// <summary>
        /// The blurred background (5.0-Release: the main video's video-only child): its blur and zoom (<see cref="Blur"/>) and its
        /// own Look cards (keys and ranges in seconds of the sequence), as BackgroundRecipe makes it.
        /// </summary>
        private Entry Background()
        {
            var entry = new Entry { Kind = EntryKind.Background, SourceId = _primary, Name = "Blurred Background", ClipKey = BackgroundKey, TrackIndex = _low.PrimaryTrackIndex };
            Blur(entry);
            if (state.BackgroundEffects.Count > 0)
                entry.Look = LookCards(entry, state.BackgroundEffects.Select(EffectInstance.FromState));
            return entry;
        }

        /// <summary>
        /// A blurred background's or a twin's blur: its radius in canvas pixels (0, sharp, to 64, continuous), passes and
        /// darkening, and its zoom as the part of the picture it covers the frame with: the middle 1 / zoom, each way, of the
        /// picture, or of the part a twin's crop (<paramref name="cut"/>, its file's) keeps of it.
        /// </summary>
        private void Blur(Entry entry, (double Left, double Top, double Right, double Bottom) cut = default)
        {
            var radius = double.IsFinite(state.BlurRadius) ? Math.Clamp(state.BlurRadius, 0, EffectRegistry.BackgroundMostRadius) : 20;
            (entry.BlurRadius, entry.BlurPasses, entry.BlurDim) = ((float)radius, Math.Clamp(state.BlurPasses, 1, 4), (float)Math.Clamp(state.BackgroundDim, -0.5, 0));
            var zoom = Math.Clamp(double.IsFinite(state.BackgroundZoom) ? state.BackgroundZoom : 1, 1, 4);
            Crop(entry, cut);
            var (middleX, middleY) = ((entry.CropLeft + entry.CropRight) / 2, (entry.CropTop + entry.CropBottom) / 2);
            var (halfX, halfY) = ((float)((entry.CropRight - entry.CropLeft) / 2.0 / zoom), (float)((entry.CropBottom - entry.CropTop) / 2.0 / zoom));
            (entry.CropLeft, entry.CropTop, entry.CropRight, entry.CropBottom) = (middleX - halfX, middleY - halfY, middleX + halfX, middleY + halfY);
        }

        /// <summary>A picture from a file, read at its own size, cut by its crop and scaled to its box by the worker.</summary>
        private Entry Still(Layer layer)
        {
            var entry = new Entry { Kind = EntryKind.Still, Still = resources.StillFromFile(layer.ImagePath) };
            Picture(entry, layer, layer.GetOutputRect(_width, _height, state.SourceWidth, state.SourceHeight));
            Crop(entry, FileCrop(layer));
            Masks(entry, layer, BuiltSize(entry, layer), layer.CropFractions);
            return entry;
        }

        /// <summary>
        /// Words, drawn whole at the size the box has at their scale (the largest it gets, when its size is animated) and cut
        /// by the crop to the pixel, as AppendLayer draws a cropped text layer.
        /// </summary>
        private Entry Text(Layer layer)
        {
            var entry = new Entry { Kind = EntryKind.Text };
            Picture(entry, layer, layer.GetOutputRect(_width, _height, state.SourceWidth, state.SourceHeight));
            var (built, builtHeight) = BuiltSize(entry, layer);
            var (left, top, right, bottom) = layer.CropFractions;
            var (wholeWidth, wholeHeight) = (Math.Max((int)Math.Round(built / (1 - left - right)), built), Math.Max((int)Math.Round(builtHeight / (1 - top - bottom)), builtHeight));
            var (cutX, cutY) = (Math.Min((int)Math.Round(wholeWidth * left), wholeWidth - built), Math.Min((int)Math.Round(wholeHeight * top), wholeHeight - builtHeight));
            entry.Still = resources.Text.TextStill(layer, wholeWidth, wholeHeight);
            (entry.CropLeft, entry.CropTop) = ((float)cutX / wholeWidth, (float)cutY / wholeHeight);
            (entry.CropRight, entry.CropBottom) = ((float)(cutX + built) / wholeWidth, (float)(cutY + builtHeight) / wholeHeight);
            Masks(entry, layer, (built, builtHeight), layer.CropFractions);
            return entry;
        }

        /// <summary>An adjustment clip: its Look effects on the picture under its box (held to the frame, even sides), while it lasts, coming and going by its fades.</summary>
        private Entry? Adjustment(Layer layer)
        {
            var (x, y, w, h) = layer.GetOutputRect(_width, _height, state.SourceWidth, state.SourceHeight);
            var (left, top) = (Math.Clamp(x, 0, _width), Math.Clamp(y, 0, _height));
            var (right, bottom) = (Math.Clamp(x + w, 0, _width), Math.Clamp(y + h, 0, _height));
            var (cutWidth, cutHeight) = ((right - left) / 2 * 2, (bottom - top) / 2 * 2);
            if (left <= 0 && top <= 0 && right >= _width && bottom >= _height)
                (left, top, cutWidth, cutHeight) = (0, 0, _width, _height);
            if (cutWidth < 2 || cutHeight < 2)
                return null;

            var entry = new Entry { Kind = EntryKind.Adjustment, Layer = layer, KeyStart = layer.StartTime };
            Identify(entry, layer);
            (entry.X, entry.Y, entry.W, entry.H) = (left, top, cutWidth, cutHeight);
            (entry.From, entry.To) = (layer.StartTime, layer.Duration > 0.001 ? layer.StartTime + layer.Duration : double.PositiveInfinity);
            entry.Look = Look(entry, layer);
            entry.Fades = Fades(layer, adjustment: true);
            return entry.Look.Length > 0 ? entry : null;
        }

        // ----- What every picture has -----

        /// <summary>Who it is, when it is on, where it is, how it is laid on, and its effects and fades: everything but its source.</summary>
        private void Picture(Entry entry, Layer layer, (int X, int Y, int Width, int Height) rect)
        {
            Identify(entry, layer);
            entry.Layer = layer;
            if (!layer.IsMainVideo && layer.HasTiming && (layer.StartTime > 0.001 || layer.Duration > 0.001))
                (entry.From, entry.To) = (layer.StartTime, layer.Duration > 0.001 ? layer.StartTime + layer.Duration : double.PositiveInfinity);

            (entry.X, entry.Y, entry.W, entry.H) = (rect.X, rect.Y, Math.Max(rect.Width, 1), Math.Max(rect.Height, 1));
            entry.Shape = (double)entry.H / entry.W;
            entry.KeyStart = layer.StartTime;
            (entry.AnchorX, entry.AnchorY) = ((float)Layer.Anchor(layer.AnchorX), (float)Layer.Anchor(layer.AnchorY));
            entry.Turns = layer.HasFilters;
            entry.Rotation = layer.HasFilters && Math.Abs(layer.Rotation % 360) >= 0.05 ? (float)layer.Rotation : 0;
            entry.Opacity = Math.Clamp(layer.Opacity, 0, 100) / 100f;
            entry.KeysX = Keys(layer, KeyProperty.X);
            entry.KeysY = Keys(layer, KeyProperty.Y);
            entry.KeysScale = Keys(layer, KeyProperty.Scale);
            entry.KeysRotation = layer.HasFilters ? Keys(layer, KeyProperty.Rotation) : null;
            entry.KeysOpacity = layer.IsPicture ? Keys(layer, KeyProperty.Opacity) : null;

            entry.BlendMode = layer.IsMainVideo ? 0 : BlendIndex(layer.BlendModeName);
            entry.Look = Look(entry, layer);
            if (layer.Shadow && layer.FxOn(EffectRegistry.Shadow))
                entry.Shadow = layer.Find(EffectRegistry.Shadow) is { Enabled: true } card ? card : new EffectInstance { Type = EffectRegistry.Shadow };
            entry.Fades = Fades(layer, adjustment: false);
        }

        private void Identify(Entry entry, Layer layer)
        {
            if (_low.ClipOf.TryGetValue(layer, out var clip))
                (entry.ClipKey, entry.ClipId) = (GpuHash.Of(clip.Id), clip.Id.ToString("D", CultureInfo.InvariantCulture));
            entry.TrackIndex = _low.TrackOf.GetValueOrDefault(layer, -1);
            entry.Name = layer.Name;
        }

        /// <summary>When a video from a file is on: its clip's stretch, its file's frames running out at its end; one with no length repeats to the end.</summary>
        private static void Timing(Entry entry, Layer layer)
        {
            (entry.MediaStart, entry.MediaOffset, entry.MediaDuration) = (layer.StartTime, layer.MediaOffset, layer.MediaDuration);
            entry.Speed = layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
            entry.Loops = layer.Duration <= 0.001;
            if (entry.Loops)
                (entry.From, entry.To) = (layer.StartTime, double.PositiveInfinity);
            else
                (entry.From, entry.To, entry.ToExclusive) = (layer.StartTime, layer.StartTime + layer.Duration, true);
        }

        private static Keyframe[]? Keys(Layer layer, KeyProperty property) => layer.GetKeys(property) is { Count: > 0 } keys ? [.. keys] : null;

        /// <summary>The size a layer is built at: its box, or with its size animated the largest it gets (AppendLayer's builtWidth).</summary>
        private (int Width, int Height) BuiltSize(Entry entry, Layer layer)
        {
            var (width, height) = ((int)entry.W, (int)entry.H);
            if (entry.KeysScale is { Length: > 0 } scale)
            {
                var largest = 0.0;
                foreach (var key in scale)
                    largest = Math.Max(largest, key.Value);
                width = Even(largest * _width);
                height = Even((double)entry.H * width / Math.Max(entry.W, 1));
            }

            return (Math.Max(width, 2), Math.Max(height, 2));
        }

        private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);

        /// <summary>The crop a picture read from a file is cut by (FileCropOf): a clip of the main video laid as a file, its own; any other clip, its own crop.</summary>
        private (double Left, double Top, double Right, double Bottom) FileCrop(Layer layer) =>
            _low.StreamCrops.TryGetValue(layer, out var own) ? own : layer.IsCropped ? layer.CropFractions : default;

        private static void Crop(Entry entry, (double Left, double Top, double Right, double Bottom) crop)
        {
            (entry.CropLeft, entry.CropTop) = ((float)Math.Clamp(crop.Left, 0, 1), (float)Math.Clamp(crop.Top, 0, 1));
            (entry.CropRight, entry.CropBottom) = ((float)Math.Clamp(1 - crop.Right, 0, 1), (float)Math.Clamp(1 - crop.Bottom, 0, 1));
            if (entry.CropRight <= entry.CropLeft || entry.CropBottom <= entry.CropTop)
                (entry.CropLeft, entry.CropTop, entry.CropRight, entry.CropBottom) = (0, 0, 1, 1);
        }

        /// <summary>The Blend Mode card's choice as the GPU's mode (its index among the card's choices); 0 for normal.</summary>
        private static int BlendIndex(string mode)
        {
            if (EffectRegistry.Find(EffectRegistry.BlendMode)?.Param("mode") is not { } param)
                return 0;
            for (var i = 0; i < param.Choices.Count; i++)
            {
                if (param.Choices[i].Value == mode)
                    return i;
            }

            return 0;
        }

        /// <summary>A layer's Look cards (zone 2) that do something on the GPU, in their order; the first LUT and the first Curves card get their tables.</summary>
        private EffectInstance[] Look(Entry entry, Layer layer)
        {
            if (!layer.HasFilters || layer.EffectsBypassed)
                return [];
            return LookCards(entry, layer.Effects);
        }

        private EffectInstance[] LookCards(Entry entry, IEnumerable<EffectInstance> cards)
        {
            var look = new List<EffectInstance>();
            foreach (var card in cards)
            {
                if (!card.Enabled || card.Definition is not { Zone: EffectZone.Look } definition || definition.GpuSlot == GpuShaderSlot.None)
                    continue;

                if (definition.GpuSlot == GpuShaderSlot.Lut3D)
                {
                    var file = card.GetText("file").Trim().Trim('"');
                    if (file.Length == 0 || !state.ExistingFiles.Contains(file) || !TakeTable(ref entry.Lut, () => resources.Lut(file)))
                        continue;
                }
                else if (definition.GpuSlot == GpuShaderSlot.ToneCurve && !TakeTable(ref entry.Curve, () => resources.Curve(card.GetText("preset"), card.GetText("master"))))
                {
                    continue;
                }

                look.Add(card);
            }

            return [.. look];
        }

        // One LUT and one curve a layer (the descriptor has room for one of each): a second card of either is left out, said once.
        private bool TakeTable(ref GpuResourceRef slot, Func<GpuResourceRef> make)
        {
            if (!slot.IsNone)
            {
                TellOnce(1, "A layer with two LUT or two Curves cards: the GPU engine draws the first of each.");
                return false;
            }

            slot = make();
            return !slot.IsNone;
        }

        /// <summary>
        /// The key and matte zone (AppendKeyAndMatte), in the cards' order: the key (while it is switched on), the mask and
        /// the edges where the first of their cards is, the choke and smoothing. The mask is baked as BuildMaskSource makes it
        /// (cut as the picture is, scaled to the layer, inverted, grown, nudged, its outline softened); with a mask, Soft Edges
        /// softens the mask and the rectangle only has its corners rounded (Corner Feather's P1 = 0).
        /// </summary>
        private void Masks(Entry entry, Layer layer, (int Width, int Height) size, (double Left, double Top, double Right, double Bottom) pictureCrop)
        {
            if (layer.EffectsBypassed)
                return;

            var steps = new List<MatteStep>();
            var placed = false;
            foreach (var card in layer.Effects)
            {
                if (!card.Enabled || card.Definition is not { Zone: EffectZone.KeyAndMatte } definition)
                    continue;

                if (definition.Type == EffectRegistry.ChromaKey)
                {
                    if (layer.ChromaKey && layer.HasKeying)
                        steps.Add(new MatteStep(card));
                }
                else if (definition.Type is EffectRegistry.Mask or EffectRegistry.Edges && !placed)
                {
                    placed = true;
                    PlaceMasks(entry, layer, size, pictureCrop);
                    steps.Add(new MatteStep(null));
                }
                else if (definition.AlphaOnly && definition.GpuSlot != GpuShaderSlot.None)
                {
                    steps.Add(new MatteStep(card));
                }
            }

            entry.Matte = [.. steps];
        }

        private void PlaceMasks(Entry entry, Layer layer, (int Width, int Height) size, (double Left, double Top, double Right, double Bottom) pictureCrop)
        {
            var file = layer.CustomMask && layer.HasMask && layer.FxOn(EffectRegistry.Mask) ? layer.MaskPath.Trim().Trim('"') : "";
            var hasMask = file.Length > 0 && state.ExistingFiles.Contains(file);
            if (hasMask)
            {
                (double X, double Y, double Width, double Height) cut;
                if (layer.UsesFrameMask)
                {
                    cut = layer.CroppedSource;
                }
                else
                {
                    var (left, top, right, bottom) = pictureCrop;
                    cut = left + top + right + bottom > 0 ? (left, top, 1 - left - right, 1 - top - bottom) : (0, 0, 1, 1);
                }

                var grow = layer.MaskGrow == 0 ? 0 : Math.Sign(layer.MaskGrow) * Math.Max((int)Math.Round((double)Math.Abs(Math.Clamp(layer.MaskGrow, -Layer.MaskGrowLimit, Layer.MaskGrowLimit))), 1);
                var (dx, dy) = ((int)Math.Round(Layer.MaskOffset(layer.MaskOffsetX) / 100.0 * size.Width), (int)Math.Round(Layer.MaskOffset(layer.MaskOffsetY) / 100.0 * size.Height));
                var blur = layer.Feather && layer.FxOn(EffectRegistry.Edges) ? BoxRadius(Math.Clamp(layer.FeatherRadius, 1, 100) / 2.0, size.Width, size.Height) : 0;
                var recipe = new MaskRecipe(file, state.AlphaMasks.Contains(file), size.Width, size.Height, cut, layer.MaskInvert, grow, dx, dy, blur);
                entry.Mask = resources.Mask(recipe);
                if (resources.StateOf(entry.Mask) == GpuResourceState.Pending)
                    entry.MaskStandIn = resources.MaskStandIn(recipe);
                entry.HasMask = !entry.Mask.IsNone;
            }

            entry.Edges = EdgesOf(layer, softEdge: !hasMask);
        }

        /// <summary>The rounded and feathered edges (BuildLayerMask) as Corner Feather's packet; none for a plain rectangle.</summary>
        private static GpuEffectPacket EdgesOf(Layer layer, bool softEdge)
        {
            if (!layer.FxOn(EffectRegistry.Edges))
                return default;
            var corner = Math.Clamp(layer.CornerRadius, 0, 50);
            var feather = softEdge && layer.Feather;
            return corner > 0 || feather ? new GpuEffectPacket(GpuShaderSlot.CornerFeather, corner, feather ? 1 : 0, layer.FeatherRadius, 0, 0, 0, 0, 0) : default;
        }

        /// <summary>The radius of a box blur that, run twice, spreads as a Gaussian of the given sigma (FilterGraphBuilder's BoxRadius).</summary>
        private static int BoxRadius(double sigma, int width, int height)
        {
            var radius = (int)Math.Round((Math.Sqrt(6 * sigma * sigma + 1) - 1) / 2);
            return Math.Clamp(radius, 0, Math.Max(Math.Min(width, height) / 2 - 1, 0));
        }

        /// <summary>
        /// A layer's fades (PictureFades): its fade in from its start and its fade out to its end, each held to what its
        /// length leaves, none a crossfade gave to the clip over it. An adjustment layer's slide or zoom is a dissolve.
        /// </summary>
        private Fade[] Fades(Layer layer, bool adjustment)
        {
            if (!layer.HasTiming || layer.IsAudio)
                return [];

            var (start, end) = layer.GetSpan(_low.SequenceSeconds);
            var length = end - start;
            var fades = new List<Fade>(2);
            TransitionStyle Style(TransitionStyle style) => adjustment && (TransitionStyles.IsSlide(style) || style == TransitionStyle.Zoom) ? TransitionStyle.Dissolve : style;

            var fadeIn = Math.Min(layer.FadeInSeconds, length);
            if (!layer.PictureFadeInOff && fadeIn > 0.01)
                fades.Add(new Fade(start, fadeIn, true, Style(layer.FadeInStyle)));
            var fadeOut = Math.Min(layer.FadeOutSeconds, Math.Max(length - (layer.PictureFadeInOff ? 0 : layer.FadeInSeconds), 0));
            if (!layer.PictureFadeOutOff && fadeOut > 0.01)
                fades.Add(new Fade(end - fadeOut, fadeOut, false, Style(layer.FadeOutStyle)));
            return [.. fades];
        }

        /// <summary>
        /// Whether the main layer, the bottom of the stack, hides every pixel of the blurred background whenever it is on:
        /// covering the frame, solid, square-cornered, unturned, nothing keyed or masked out (IsOpaque), and still.
        /// </summary>
        private bool CoversWhenOn(Layer main)
        {
            var (x, y, w, h) = state.CenterRect;
            var covers = x <= 0 && y <= 0 && x + w >= _width && y + h >= _height;
            var mask = main.MaskPath.Trim().Trim('"');
            var opaque = main.Opacity >= 100 && !main.HasKeys(KeyProperty.Opacity) && !main.ChromaKey && !(main.CustomMask && state.ExistingFiles.Contains(mask))
                         && main.CornerRadius == 0 && !main.Feather && Math.Abs(main.Rotation % 360) < 0.05;
            return covers && opaque && !main.IsAnimated;
        }

        // ----- The whole picture -----

        /// <summary>
        /// What BuildVideoFilters does to the finished picture before the Sequence Output's effects: the denoise (hqdn3d at
        /// its default strength), the LUT, and the colour sliders (their eq, hue and cas, read back from the filters the view
        /// model wrote), as one adjustment of the whole frame. None when there is nothing.
        /// </summary>
        private Entry? Whole()
        {
            var entry = new Entry { Kind = EntryKind.Whole, Name = "Picture", ClipKey = WholeKey };
            var packets = new List<GpuEffectPacket>();
            if (state.Denoise)
                packets.Add(new GpuEffectPacket(GpuShaderSlot.Denoise, 4, 0, 0, 0, 0, 0, 0, 0));
            if (state.TonemapToSdr)
                TellOnce(2, "Tone mapping to SDR is not drawn by the GPU engine yet: the export does it.");
            if (state.LutPath.Trim().Trim('"') is { Length: > 0 } lut && resources.Lut(lut) is { IsNone: false } table)
            {
                entry.Lut = table;
                packets.Add(new GpuEffectPacket(GpuShaderSlot.Lut3D, 0, 0, 0, 0, 0, 0, 0, 0));
            }

            foreach (var filter in state.ColorFilters)
                ColorFilter(filter, packets);

            entry.Packets = [.. packets];
            return entry.Packets.Length > 0 ? entry : null;
        }

        /// <summary>The Sequence Output's effect stack on the finished frame (its keys and ranges in seconds of the sequence).</summary>
        private Entry? SequenceLook()
        {
            var entry = new Entry { Kind = EntryKind.Whole, Name = "Sequence Output", ClipKey = WholeKey };
            entry.Look = LookCards(entry, _low.SequenceEffects);
            return entry.Look.Length > 0 ? entry : null;
        }

        /// <summary>One of the colour sliders' filters (MainViewModel.BuildColorFilters writes eq, hue and cas) as its GPU packet.</summary>
        private static void ColorFilter(string filter, List<GpuEffectPacket> packets)
        {
            var equals = filter.IndexOf('=');
            if (equals <= 0)
                return;

            var options = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var option in filter[(equals + 1)..].Split(':'))
            {
                var at = option.IndexOf('=');
                if (at > 0 && double.TryParse(option[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    options[option[..at]] = value;
            }

            double Get(string key, double fallback) => options.TryGetValue(key, out var value) ? value : fallback;
            switch (filter[..equals])
            {
                case "eq":
                    packets.Add(new GpuEffectPacket(GpuShaderSlot.ColorGrade, (float)Get("contrast", 1), (float)Get("brightness", 0), (float)Get("saturation", 1), (float)Get("gamma", 1), 0, 0, 0, 0));
                    if (Math.Abs(Get("gamma_r", 1) - 1) > 0.005 || Math.Abs(Get("gamma_g", 1) - 1) > 0.005 || Math.Abs(Get("gamma_b", 1) - 1) > 0.005)
                        TellOnce(4, "The colour sliders' red, green and blue balance is not drawn by the GPU engine yet: the export does it.");
                    break;
                case "hue":
                    packets.Add(new GpuEffectPacket(GpuShaderSlot.HueSaturation, (float)Get("h", 0), (float)Get("s", 1), 0, 0, 0, 0, 0, 0));
                    break;
                case "cas":
                    packets.Add(new GpuEffectPacket(GpuShaderSlot.Sharpen, (float)Get("strength", 0), 0, 0, 0, 0, 0, 0, 0));
                    break;
            }
        }

        // ----- The captions -----

        /// <summary>
        /// The captions as BuildCaptionOverlay lays them: drawn on a canvas the size of the caption box (at least 16 x 16),
        /// laid where the box is, at its opacity, with its rounded or soft edges, its mask and its drop shadow; on top of all.
        /// </summary>
        private Entry Captions()
        {
            var caption = state.CaptionLayer;
            var (x, y, w, h) = caption.GetOutputRect(_width, _height, 0, 0);
            (w, h) = (Math.Max(w, 16), Math.Max(h, 16));
            resources.Text.UseCaptionBox(w, h);

            var entry = new Entry { Kind = EntryKind.Caption, Captions = resources.Text, Layer = caption, Name = "Captions", ClipKey = CaptionKey };
            (entry.X, entry.Y, entry.W, entry.H) = (x, y, w, h);
            entry.Opacity = Math.Clamp(caption.Opacity, 0, 100) / 100f;
            var edges = EdgesOf(caption, softEdge: true);
            entry.Packets = edges.Slot == GpuShaderSlot.None ? [] : [edges];

            var mask = caption.MaskPath.Trim().Trim('"');
            if (caption.CustomMask && mask.Length > 0 && state.ExistingFiles.Contains(mask))
            {
                entry.Mask = resources.Mask(new MaskRecipe(mask, state.AlphaMasks.Contains(mask), w, h, (0, 0, 1, 1), false, 0, 0, 0, 0));
                entry.HasMask = !entry.Mask.IsNone;
            }

            if (caption.Shadow)
                entry.Shadow = new EffectInstance { Type = EffectRegistry.Shadow };
            return entry;
        }
    }
}
