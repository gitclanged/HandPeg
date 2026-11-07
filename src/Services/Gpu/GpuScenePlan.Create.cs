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
        /// <summary>A video read from its own file: every video clip, a Cutout too (5.2-AuditB: no clip is read from a primary chain's stream).</summary>
        File,

        /// <summary>A picture from a file.</summary>
        Still,

        /// <summary>Words, drawn into a still.</summary>
        Text,

        /// <summary>An adjustment clip: its Look effects on the picture under its box.</summary>
        Adjustment,

        /// <summary>A video's blurred copy covering the frame, under its track's pictures (its blur fill, drawn as the sequence's blurred background was).</summary>
        Twin,

        /// <summary>What is done to the whole finished picture: the denoise, the LUT, the colour sliders, the Sequence Output's effects.</summary>
        Whole,

        /// <summary>The whole picture's fade from and to black.</summary>
        Fade,

        /// <summary>The subtitle track's pictures (a PGS or VobSub track, or burned subtitles), each over the picture of the clip it is shown in.</summary>
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

        // A video's own picture size, in its pixels (the source view's canvas; 1920 x 1080 while it is not known).
        public int PictureWidth = 1920, PictureHeight = 1080;

        // Where: its box on the canvas as set, its keys (counted from KeyStart), its anchor, turn and opacity.
        public float X, Y, W, H;
        public double Shape = 1, KeyStart;
        public float AnchorX = 0.5f, AnchorY = 0.5f, Rotation, Opacity = 1;
        public bool Turns;
        public Keyframe[]? KeysX, KeysY, KeysScale, KeysRotation, KeysOpacity;

        // True 3D (5.2A): its Pitch and Yaw in degrees, and their keys (a picture that turns only).
        public float TiltX, TiltY;
        public Keyframe[]? KeysTiltX, KeysTiltY;

        // The part of its source shown, as fractions: left, top, right and bottom edges.
        public float CropLeft, CropTop, CropRight = 1, CropBottom = 1;

        // What is done to it.
        public int BlendMode;
        public EffectInstance[] Look = [];
        public MatteStep[] Matte = [];
        public EffectInstance? Shadow;

        // What is drawn as it is laid on, with no FFmpeg behind it (5.1-GpuFx: the Neon Rim Light, Frosted Glass).
        public EffectInstance[] LaidOn = [];
        public GpuEffectPacket Edges;
        public GpuEffectPacket[] Packets = [];
        public GpuResourceRef Lut = GpuResourceRef.None, Curve = GpuResourceRef.None, Mask = GpuResourceRef.None, MaskStandIn = GpuResourceRef.None;
        public Fade[] Fades = [];

        // A twin's blur (BlurRadius, BlurPasses, BackgroundDim), and its video when that hides every pixel of it whenever it is
        // on and not fading (the twin is then not drawn: GpuScenePlan.Hides).
        public float BlurRadius, BlurDim;
        public int BlurPasses;
        public Entry? CoveredBy;

        // The whole picture's fade; the captions; the subtitle track's pictures.
        public bool FadesIn, FadesOut;
        public TextRasterizer? Captions;
        public PictureLane? Pictures;
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
        private readonly GpuSceneLowering _low = TimelineLowering.LowerForGpu(state);
        private readonly int _width = Math.Max(state.Timeline.Width, 2);
        private readonly int _height = Math.Max(state.Timeline.Height, 2);

        // The blur fill's own effects (the sequence's BackgroundEffects), made once and shared by every twin's entry.
        private EffectInstance[]? _blurFillCards;

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
            var entries = new List<Entry>(_low.Stack.Count + 6);

            // The captions among the layers when they are put under a track (5.1-Polish, the user's Q2: the lowering's
            // CaptionsAt, the same place the export lays them), else over everything as they always were.
            var captionsAt = state.AutoCaptions ? _low.CaptionsAt : -1;
            for (var n = 0; n < _low.Stack.Count; n++)
            {
                if (n == captionsAt)
                    entries.Add(Captions());
                if (Picture(_low.Stack[n]) is { } entry)
                    entries.Add(entry);
            }

            if (captionsAt >= _low.Stack.Count)
                entries.Add(Captions());
            CoverTwins(entries);

            // The whole finished picture: the denoise, the LUT and the colour sliders, then the Sequence Output's own effects.
            if (Whole() is { } whole)
                entries.Add(whole);
            if (SequenceLook() is { } look)
                entries.Add(look);
            if (state.FadeIn || state.FadeOut)
                entries.Add(new Entry { Kind = EntryKind.Fade, FadesIn = state.FadeIn, FadesOut = state.FadeOut, Name = "Fade", ClipKey = FadeKey });
            if (state.PictureSubtitles is { Cues.Count: > 0 } pictureTrack)
                entries.Add(PictureSubtitlesEntry(pictureTrack, entries, state.StreamSourcePath, _width, _height));
            if (state.AutoCaptions && captionsAt < 0)
                entries.Add(Captions());

            foreach (var entry in entries)
            {
                if (entry.SourceId >= 0)
                    entry.PathHash = resources.PathHashOf(entry.SourceId);
            }

            return new GpuScenePlan(_width, _height, _low.SequenceSeconds, [.. entries], resources)
            {
                FrameSeconds = _low.FrameSeconds,
                MediaFlags = (state.Deinterlace ? GpuLayerDescriptor.FlagDeinterlace : 0) | (state.TonemapToSdr ? GpuLayerDescriptor.FlagToneMapSdr : 0),
            };
        }

        private static readonly long FadeKey = default(GpuHash).Add("fade").Value, CaptionKey = CaptionClipKey,
            WholeKey = default(GpuHash).Add("whole").Value;

        // ----- The stack -----

        private Entry? Picture(Layer layer)
        {
            if (_low.BlurTwins.Contains(layer))
                return Twin(layer);
            if (layer.Kind is LayerKind.Video or LayerKind.MainVideo)
                return Legacy(layer);
            if (!layer.IsUsable)
                return null;

            return layer.Kind switch
            {
                LayerKind.Adjustment => Adjustment(layer),
                LayerKind.VideoFile => File(layer),
                LayerKind.Image => Still(layer),
                LayerKind.Text => Text(layer),
                _ => null,
            };
        }

        /// <summary>A layer's box on the frame, in its pixels, at its own picture's shape (Layer.GetOutputRect).</summary>
        private (int X, int Y, int Width, int Height) BoxOf(Layer layer) => layer.GetOutputRect(_width, _height, layer.ImageWidth, layer.ImageHeight);

        /// <summary>
        /// A video from its own file (AppendVideoFile): its clip's stream at its clip's moments, cut by its crop, in its box. A
        /// legacy cut-out (<see cref="Legacy"/>) is cut by its piece of the picture (its source rectangle less its crop).
        /// </summary>
        private Entry? File(Layer layer)
        {
            var clip = _low.ClipOf.GetValueOrDefault(layer);
            var source = resources.SourceFor(FileToRead(layer, clip), clip?.VideoStreamIndex ?? 0);
            if (source < 0)
                return null;

            var entry = new Entry { Kind = EntryKind.File, SourceId = source };
            Picture(entry, layer, BoxOf(layer));
            Timing(entry, layer);
            if (layer.ImageWidth > 0 && layer.ImageHeight > 0)
                (entry.PictureWidth, entry.PictureHeight) = (layer.ImageWidth, layer.ImageHeight);
            if (layer.IsVideo)
            {
                var (sx, sy, sw, sh) = layer.CroppedSource;
                (entry.CropLeft, entry.CropTop) = ((float)Math.Clamp(sx, 0, 1), (float)Math.Clamp(sy, 0, 1));
                (entry.CropRight, entry.CropBottom) = ((float)Math.Clamp(sx + sw, 0, 1), (float)Math.Clamp(sy + sh, 0, 1));
            }
            else
            {
                Crop(entry, FileCrop(layer));
            }

            Masks(entry, layer, BuiltSize(entry, layer), layer.CropFractions);
            return entry;
        }

        /// <summary>
        /// A clip of a legacy kind (a cut-out of the main video, LayerKind.Video, or the main video itself) that reached the plan
        /// unconverted: a project that holds one is converted as it is opened (LegacyMainVideo, MainViewModel.ConvertLegacy), so none
        /// should. Read as a video from its clip's file (<see cref="File"/>); left out, said once, when it has none.
        /// </summary>
        private Entry? Legacy(Layer layer)
        {
            if (layer.IsHidden || layer.IsDeleted || layer.IsLoading || layer.SizeWidth <= 0)
                return null;
            if (layer.ImagePath.Trim().Trim('"').Length == 0)
            {
                TellOnce(4, $"A clip of a legacy kind ({layer.Kind}) with no file of its own reached the GPU engine unconverted: it is not drawn.");
                return null;
            }

            TellOnce(8, $"A clip of a legacy kind ({layer.Kind}) reached the GPU engine unconverted: it is drawn as a video from its file.");
            return File(layer);
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

        /// <summary>
        /// The blurred copy a video from a file fills the frame with, under its track's pictures (TwinBlur; 5.2-AuditB: drawn as
        /// the sequence's blurred background was, for every clip whose blur fill is on): its file and timing, its whole picture,
        /// uncropped, at the blur fill's zoom, blur and darkening (<see cref="Blur"/>), the blur fill's own Look cards (keys and
        /// ranges in seconds of the sequence, as BackgroundRecipe makes them), its video's opacity, and its video's fades as
        /// dissolves, whatever their style. Left out while its video hides it (<see cref="CoverTwins"/>).
        /// </summary>
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
            for (var k = 0; k < entry.Fades.Length; k++)
                entry.Fades[k] = entry.Fades[k] with { Style = TransitionStyle.Dissolve };

            Blur(entry);
            if (state.BackgroundEffects.Count > 0)
                entry.Look = LookCards(entry, _blurFillCards ??= state.BackgroundEffects.Select(EffectInstance.FromState).ToArray());
            entry.Name = layer.Name;
            return entry;
        }

        /// <summary>
        /// A twin's blur: its radius in canvas pixels (0, sharp, to 64, continuous), passes and darkening, and its zoom as the
        /// part of the picture it covers the frame with: the middle 1 / zoom, each way, of the whole picture.
        /// </summary>
        private void Blur(Entry entry)
        {
            var radius = double.IsFinite(state.BlurRadius) ? Math.Clamp(state.BlurRadius, 0, EffectRegistry.BackgroundMostRadius) : 20;
            (entry.BlurRadius, entry.BlurPasses, entry.BlurDim) = ((float)radius, Math.Clamp(state.BlurPasses, 1, 4), (float)Math.Clamp(state.BackgroundDim, -0.5, 0));
            var zoom = Math.Clamp(double.IsFinite(state.BackgroundZoom) ? state.BackgroundZoom : 1, 1, 4);
            Crop(entry, default);
            var (middleX, middleY) = ((entry.CropLeft + entry.CropRight) / 2, (entry.CropTop + entry.CropBottom) / 2);
            var (halfX, halfY) = ((float)((entry.CropRight - entry.CropLeft) / 2.0 / zoom), (float)((entry.CropBottom - entry.CropTop) / 2.0 / zoom));
            (entry.CropLeft, entry.CropTop, entry.CropRight, entry.CropBottom) = (middleX - halfX, middleY - halfY, middleX + halfX, middleY + halfY);
        }

        /// <summary>
        /// Each twin's video, when that hides every pixel of the twin whenever it is on and not fading (<see cref="CoversWhenOn"/>):
        /// the twin is then left out moment by moment (GpuScenePlan.Hides), as the blurred background was under a main video that
        /// covered the frame (Law 3: a full-frame video never pays for a blur pass no one sees).
        /// </summary>
        private void CoverTwins(List<Entry> entries)
        {
            Dictionary<long, Entry>? videos = null;
            foreach (var twin in entries)
            {
                if (twin.Kind != EntryKind.Twin || twin.ClipKey == 0)
                    continue;

                if (videos is null)
                {
                    videos = [];
                    foreach (var entry in entries)
                    {
                        if (entry.Kind == EntryKind.File && entry.ClipKey != 0)
                            videos.TryAdd(entry.ClipKey, entry);
                    }
                }

                if (videos.TryGetValue(twin.ClipKey, out var video) && CoversWhenOn(video))
                    twin.CoveredBy = video;
            }
        }

        /// <summary>A picture from a file, read at its own size, cut by its crop and scaled to its box by the worker.</summary>
        private Entry Still(Layer layer)
        {
            var entry = new Entry { Kind = EntryKind.Still, Still = resources.StillFromFile(layer.ImagePath) };
            Picture(entry, layer, BoxOf(layer));
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
            Picture(entry, layer, BoxOf(layer));
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
            var (x, y, w, h) = BoxOf(layer);
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
            if (layer.HasTiming && (layer.StartTime > 0.001 || layer.Duration > 0.001))
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
            (entry.TiltX, entry.TiltY) = layer.HasFilters ? ((float)Layer.Tilt(layer.TiltX), (float)Layer.Tilt(layer.TiltY)) : (0f, 0f);
            (entry.KeysTiltX, entry.KeysTiltY) = layer.HasFilters ? (Keys(layer, KeyProperty.TiltX), Keys(layer, KeyProperty.TiltY)) : (null, null);

            entry.BlendMode = BlendIndex(layer.BlendModeName);
            entry.Look = Look(entry, layer);
            if (layer.Shadow && layer.FxOn(EffectRegistry.Shadow))
                entry.Shadow = layer.Find(EffectRegistry.Shadow) is { Enabled: true } card ? card : new EffectInstance { Type = EffectRegistry.Shadow };
            if (!layer.EffectsBypassed)
                entry.LaidOn = [.. layer.Effects.Where(c => c.Enabled && c.Definition is { Zone: EffectZone.Composite, GpuOnly: true })];
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

        /// <summary>The crop a picture read from a file is cut by (FileCropOf): its clip's own (4.4-BugHunt; 5.2-AuditB: a former main clip's too).</summary>
        private static (double Left, double Top, double Right, double Bottom) FileCrop(Layer layer) => layer.IsCropped ? layer.CropFractions : default;

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

        // One LUT and one curve a layer (the descriptor has room for one of each): a second effect of either is left out, said once.
        private bool TakeTable(ref GpuResourceRef slot, Func<GpuResourceRef> make)
        {
            if (!slot.IsNone)
            {
                TellOnce(1, "A clip with two LUT or two Curves effects: the GPU engine draws the first of each.");
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
            var file = layer.CustomMask && layer.HasMask && layer.FxOn(EffectRegistry.Mask) ? layer.MaskSource : "";
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

        /// <summary>The radius of a box blur that, run twice, spreads as a Gaussian of the given sigma (the box radius the export's graph used before 5.2-Audit).</summary>
        private static int BoxRadius(double sigma, int width, int height)
        {
            var radius = (int)Math.Round((Math.Sqrt(6 * sigma * sigma + 1) - 1) / 2);
            return Math.Clamp(radius, 0, Math.Max(Math.Min(width, height) / 2 - 1, 0));
        }

        /// <summary>
        /// A layer's fades (PictureFades): its fade in from its start and its fade out to its end, each held to what its
        /// length leaves, none a crossfade gave to the clip over it. An adjustment layer's slide, zoom, whip pan, zoom warp or
        /// spin (what moves its picture: the canvas under it) is a dissolve.
        /// </summary>
        private Fade[] Fades(Layer layer, bool adjustment)
        {
            if (!layer.HasTiming || layer.IsAudio)
                return [];

            var (start, end) = layer.GetSpan(_low.SequenceSeconds);
            var length = end - start;
            var fades = new List<Fade>(2);
            TransitionStyle Style(TransitionStyle style) => adjustment && (TransitionStyles.IsSlide(style)
                || style is TransitionStyle.Zoom or TransitionStyle.ParticleSnap or TransitionStyle.WhipPan or TransitionStyle.ZoomWarp or TransitionStyle.Spin)
                ? TransitionStyle.Dissolve : style;

            var fadeIn = Math.Min(layer.FadeInSeconds, length);
            if (!layer.PictureFadeInOff && fadeIn > 0.01)
                fades.Add(new Fade(start, fadeIn, true, Style(layer.FadeInStyle)));
            var fadeOut = Math.Min(layer.FadeOutSeconds, Math.Max(length - (layer.PictureFadeInOff ? 0 : layer.FadeInSeconds), 0));
            if (!layer.PictureFadeOutOff && fadeOut > 0.01)
                fades.Add(new Fade(end - fadeOut, fadeOut, false, Style(layer.FadeOutStyle)));
            return [.. fades];
        }

        /// <summary>
        /// Whether a video hides every pixel of its twin whenever it is on and not fading (the blurred background's test,
        /// BuildFrameEngine's hidesBackground, made for each twin in 5.2-AuditB): its box covering the frame, solid, square
        /// cornered, unturned, nothing keyed, masked or matted out (IsOpaque), laid on as it is with nothing laid on it, only
        /// colour on its Look (<see cref="ColourOnly"/>), and still.
        /// </summary>
        private bool CoversWhenOn(Entry video)
        {
            if (video.Layer is not { } layer)
                return false;

            var covers = video.X <= 0 && video.Y <= 0 && video.X + video.W >= _width && video.Y + video.H >= _height;
            var mask = layer.MaskSource;
            var opaque = layer.Opacity >= 100 && !layer.HasKeys(KeyProperty.Opacity) && !layer.ChromaKey && !(layer.CustomMask && state.ExistingFiles.Contains(mask))
                         && layer.CornerRadius == 0 && !layer.Feather && Math.Abs(layer.Rotation % 360) < 0.05 && !layer.IsTilted;
            var plain = video.Mask.IsNone && video.Edges.Slot == GpuShaderSlot.None && Array.TrueForAll(video.Matte, static step => step.Card is null)
                        && video.BlendMode == 0 && video.LaidOn.Length == 0 && Array.TrueForAll(video.Look, KeepsOpaque);
            return covers && opaque && plain && !layer.IsAnimated;
        }

        /// <summary>
        /// A Look card that never lets anything through an opaque picture: one that changes only its pixels' colour, or a blur
        /// (PSBoxBlur samples clamped at the picture's edges, so the edges stay as solid as the rest).
        /// </summary>
        private static bool KeepsOpaque(EffectInstance card) => card.Definition?.GpuSlot is GpuShaderSlot.BoxBlur or GpuShaderSlot.Flip or GpuShaderSlot.Lut3D or GpuShaderSlot.Denoise
            or GpuShaderSlot.ColorGrade or GpuShaderSlot.ColorBalance or GpuShaderSlot.ColorTemperature or GpuShaderSlot.Vignette or GpuShaderSlot.FilmGrain
            or GpuShaderSlot.HueSaturation or GpuShaderSlot.ToneCurve or GpuShaderSlot.Sharpen or GpuShaderSlot.HalationGlow or GpuShaderSlot.MotionBlur;

        // ----- The whole picture -----

        /// <summary>
        /// What BuildVideoFilters did to the finished picture before the Sequence Output's effects: the denoise (hqdn3d at its
        /// default strength), the LUT, and the colour sliders' grade (<see cref="SequenceExportState.Grade"/>: its balance, hue
        /// turn and sharpening), as one adjustment of the whole frame. None when there is nothing.
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

            GradePackets(state.Grade, packets, _width);
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

        /// <summary>
        /// The colour sliders' grade as its GPU packets, in the order eq, hue and unsharp were applied: the balance as Color
        /// Grade (green's gamma goes to luma's, red and blue to the chroma planes' as eq has it, their lanes 0 while the channels
        /// are even), the hue turn as Hue/Saturation, and the sharpening at unsharp's 5 x 5 square in pixels of the export's
        /// frame (<paramref name="frameWidth"/> wide), given as the Sharpen card's radius, in pixels of a 1080-wide frame.
        /// </summary>
        private static void GradePackets(in SequenceGrade grade, List<GpuEffectPacket> packets, int frameWidth)
        {
            if (grade.Balance is { } balance)
            {
                var (red, green, blue) = (balance.GammaRed, balance.GammaGreen, balance.GammaBlue);
                var balanced = Math.Abs(red - 1) > 0.005 || Math.Abs(green - 1) > 0.005 || Math.Abs(blue - 1) > 0.005;
                packets.Add(new GpuEffectPacket(GpuShaderSlot.ColorGrade, (float)balance.Contrast, (float)balance.Brightness, (float)balance.Saturation, (float)(balance.Gamma * green),
                    balanced ? (float)Math.Sqrt(blue / green) : 0, balanced ? (float)Math.Sqrt(red / green) : 0, 0, 0));
            }

            if (grade.Hue is { } hue)
                packets.Add(new GpuEffectPacket(GpuShaderSlot.HueSaturation, (float)hue, 1, 0, 0, 0, 0, 0, 0));
            if (grade.Sharpen is { } strength)
                packets.Add(new GpuEffectPacket(GpuShaderSlot.Sharpen, (float)strength, (float)(2 * 1080.0 / Math.Max(frameWidth, 1)), 0, 0, 0, 0, 0, 0));
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
