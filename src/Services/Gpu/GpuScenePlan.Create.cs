using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services.Gpu;

// ----- Plan creation: the lowering compiled once into per-frame-evaluated entries -----
public sealed partial class GpuScenePlan
{
    private enum EntryKind : byte
    {
        /// <summary>A video decoded from its own file: every video clip, including Cutouts.</summary>
        File,

        /// <summary>A still image from a file.</summary>
        Still,

        /// <summary>Text rasterized into a still.</summary>
        Text,

        /// <summary>An adjustment clip: its Look effects applied to the picture under its box.</summary>
        Adjustment,

        /// <summary>A video's blurred full-frame copy under its track's pictures (its blur fill).</summary>
        Twin,

        /// <summary>Whole-frame processing: denoise, LUT, colour sliders, Sequence Output effects.</summary>
        Whole,

        /// <summary>Whole-frame fade from and to black.</summary>
        Fade,

        /// <summary>Bitmap subtitle track (PGS, VobSub or burned subtitles), each over the clip it appears in.</summary>
        PictureSubtitles,

        /// <summary>Captions, on top of everything.</summary>
        Caption,
    }

    /// <summary>A key-and-matte step: an effect (key, choke, smoothing), or the mask and edges (null) at the position of their first effect.</summary>
    private readonly record struct MatteStep(EffectInstance? Card);

    /// <summary>One drawable item, fully resolved at plan creation and immutable afterwards.</summary>
    private sealed class Entry
    {
        public EntryKind Kind;
        public Layer? Layer;

        // Identity: 3D view labels and picking, and the worker's per-clip history.
        public long ClipKey;
        public string ClipId = "";
        public int TrackIndex = -1;
        public string Name = "";
        public bool HasMask;

        // Active interval: From <= t <= To, inclusive (end exclusive where the file's frames run out).
        public double From = double.NegativeInfinity, To = double.PositiveInfinity;
        public bool ToExclusive;

        // Source binding (path hash as held by the source table at plan creation).
        public int SourceId = -1;
        public long PathHash;
        public double MediaStart, MediaOffset, Speed = 1, MediaDuration;
        public bool Loops;

        // Smooth Slow-Mo: enabled and slower than 1x, so drawn with interpolated in-betweens (GpuLayerDescriptor.FlagInterpolate).
        public bool Interpolate;
        public GpuResourceRef Still = GpuResourceRef.None;

        // Native video size in pixels (the source view's canvas; 1920 x 1080 until known).
        public int PictureWidth = 1920, PictureHeight = 1080;

        // Placement: canvas box, keyframes (relative to KeyStart), anchor, rotation and opacity.
        public float X, Y, W, H;
        public double Shape = 1, KeyStart;
        public float AnchorX = 0.5f, AnchorY = 0.5f, Rotation, Opacity = 1;
        public bool Turns;
        public Keyframe[]? KeysX, KeysY, KeysScale, KeysRotation, KeysOpacity;

        // True 3D: Pitch and Yaw in degrees with keyframes (rotation only).
        public float TiltX, TiltY;
        public Keyframe[]? KeysTiltX, KeysTiltY;

        // Visible source region as fractions: left, top, right, bottom edges.
        public float CropLeft, CropTop, CropRight = 1, CropBottom = 1;

        // Processing.
        public int BlendMode;
        public EffectInstance[] Look = [];
        public MatteStep[] Matte = [];
        public EffectInstance? Shadow;

        // Composite-time effects (Neon Rim Light, Frosted Glass).
        public EffectInstance[] LaidOn = [];
        public GpuEffectPacket Edges;
        public GpuEffectPacket[] Packets = [];
        public GpuResourceRef Lut = GpuResourceRef.None, Curve = GpuResourceRef.None, Mask = GpuResourceRef.None, MaskStandIn = GpuResourceRef.None;

        // Color Grade effect holding the LUT and curve table (null: a LUT or Curves effect holds them, or none).
        public EffectInstance? LutCard, CurveCard;
        public Fade[] Fades = [];

        // Twin blur parameters (BlurRadius, BlurPasses, BackgroundDim), and the video that fully hides it while active and not fading
        // (the twin is then skipped: GpuScenePlan.Hides).
        public float BlurRadius, BlurDim;
        public int BlurPasses;
        public Entry? CoveredBy;

        // Whole-frame fade; captions; bitmap subtitle track.
        public bool FadesIn, FadesOut;
        public TextRasterizer? Captions;
        public PictureLane? Pictures;
    }

    // ----- Create -----

    /// <summary>
    /// Builds the plan for a state (UI thread, once per edit: a few hundred microseconds for a typical project; the lowering is cached per
    /// document). Registers every file in the source table and requests every texture from <paramref name="resources"/>: text is rasterized
    /// immediately, masks, LUTs and stills on a background lane (layers appear as they arrive: GpuResourceBroker.ResourcesChanged). Never
    /// throws: failure returns <see cref="Empty"/> and logs the reason.
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

    /// <summary>Builds one plan: the state, its lowering, and the resulting entries.</summary>
    private sealed class Maker(SequenceExportState state, GpuResourceBroker resources)
    {
        private readonly GpuSceneLowering _low = TimelineLowering.LowerForGpu(state);
        private readonly int _width = Math.Max(state.Timeline.Width, 2);
        private readonly int _height = Math.Max(state.Timeline.Height, 2);

        // Blur fill effects (the sequence's BackgroundEffects), built once and shared by every twin entry.
        private EffectInstance[]? _blurFillCards;

        // Bitmask of unsupported-feature log messages already written: once per session, not per edit.
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

            // Captions placed under a track go at the lowering's CaptionsAt (same position as the export); otherwise on top.
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

            // Whole-frame processing: denoise, LUT and colour sliders, then the Sequence Output effects.
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

        // ----- Layer stack -----

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

        /// <summary>A layer's frame box in pixels at its native aspect (Layer.ComputeOutputRect).</summary>
        private (int X, int Y, int Width, int Height) BoxOf(Layer layer) => layer.ComputeOutputRect(_width, _height, layer.ImageWidth, layer.ImageHeight);

        /// <summary>
        /// A video decoded from its own file: the clip's stream at the clip's times, cropped, in its box. A legacy cut-out
        /// (<see cref="Legacy"/>) is cropped to its source rectangle minus its crop.
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
        /// A legacy clip (main-video cut-out, LayerKind.Video, or the main video) that reached the plan unconverted; projects are converted on
        /// open (LegacyMainVideo, MainViewModel.ConvertLegacy), so this should not happen. Drawn as a video from its file (<see cref="File"/>);
        /// skipped with a one-time log entry when it has none.
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
        /// File to decode for a video layer and its twin: in preview, its proxy when requested (Optimize Long-GOP Clip for Scrubbing) and ready
        /// (<see cref="SequenceExportState.PreviewPaths"/>; first video stream only, all a proxy holds); otherwise, and always in export, the
        /// original. Proxies keep the original's frame times (ProxyCache.FrameTiming), so times map 1:1; they are smaller, and all placement
        /// and crop values here are fractions of the picture.
        /// </summary>
        private string FileToRead(Layer layer, TimelineClip? clip) =>
            state.IsPreview && (clip?.ProxyChoice ?? layer.ProxyChoice) == ProxyChoice.Make && (clip?.VideoStreamIndex ?? 0) == 0
            && state.PreviewPaths.TryGetValue(layer.ImagePath.Trim().Trim('"'), out var proxy) ? proxy : layer.ImagePath;

        /// <summary>
        /// Blur fill twin: a blurred full-frame copy of a video under its track's pictures, for every clip with blur fill on. Uses the video's
        /// file and timing, uncropped, at the blur fill's zoom, blur and dim (<see cref="Blur"/>), the blur fill's Look effects (keys and ranges
        /// in sequence seconds), the video's opacity, and its fades as dissolves regardless of style. Skipped while the video hides it
        /// (<see cref="CoverTwins"/>).
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
        /// Twin blur settings: radius in canvas pixels (0 sharp to 64, continuous), passes and dim, and zoom as a centre crop of 1 / zoom of the
        /// picture in each direction.
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
        /// Links each twin to its video when the video hides every twin pixel while active and not fading (<see cref="CoversWhenOn"/>); the
        /// twin is then skipped per frame (GpuScenePlan.Hides), so a full-frame video never pays for an invisible blur pass.
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

        /// <summary>A still image loaded at native size, cropped and scaled to its box by the worker.</summary>
        private Entry Still(Layer layer)
        {
            var entry = new Entry { Kind = EntryKind.Still, Still = resources.StillFromFile(layer.ImagePath) };
            Picture(entry, layer, BoxOf(layer));
            Crop(entry, FileCrop(layer));
            Masks(entry, layer, BuiltSize(entry, layer), layer.CropFractions);
            return entry;
        }

        /// <summary>
        /// Text rasterized uncropped at the box size (the largest size when animated), then cropped to the pixel.
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

        /// <summary>An adjustment clip: Look effects on the picture under its box (clamped to the frame, even sides) for its duration, faded in and out.</summary>
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

        // ----- Common picture properties -----

        /// <summary>Fills identity, timing, placement, blending, effects and fades: everything except the source.</summary>
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

        /// <summary>Active interval and media mapping of a file video (speed, end exclusive where frames run out); zero length loops to the end.</summary>
        private static void Timing(Entry entry, Layer layer)
        {
            (entry.MediaStart, entry.MediaOffset, entry.MediaDuration) = (layer.StartTime, layer.MediaOffset, layer.MediaDuration);
            entry.Speed = layer.HasSpeed && double.IsFinite(layer.Speed) ? Math.Clamp(layer.Speed, Layer.SlowestSpeed, Layer.FastestSpeed) : 1;
            entry.Interpolate = entry.Speed < 1 && layer.SmoothSlowMo;
            entry.Loops = layer.Duration <= 0.001;
            if (entry.Loops)
                (entry.From, entry.To) = (layer.StartTime, double.PositiveInfinity);
            else
                (entry.From, entry.To, entry.ToExclusive) = (layer.StartTime, layer.StartTime + layer.Duration, true);
        }

        private static Keyframe[]? Keys(Layer layer, KeyProperty property) => layer.GetKeys(property) is { Count: > 0 } keys ? [.. keys] : null;

        /// <summary>Build size of a layer: its box, or the largest animated size when scale is keyframed.</summary>
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

        /// <summary>Crop applied to a file-based picture: the clip's own crop (including former main clips).</summary>
        private static (double Left, double Top, double Right, double Bottom) FileCrop(Layer layer) => layer.IsCropped ? layer.CropFractions : default;

        private static void Crop(Entry entry, (double Left, double Top, double Right, double Bottom) crop)
        {
            (entry.CropLeft, entry.CropTop) = ((float)Math.Clamp(crop.Left, 0, 1), (float)Math.Clamp(crop.Top, 0, 1));
            (entry.CropRight, entry.CropBottom) = ((float)Math.Clamp(1 - crop.Right, 0, 1), (float)Math.Clamp(1 - crop.Bottom, 0, 1));
            if (entry.CropRight <= entry.CropLeft || entry.CropBottom <= entry.CropTop)
                (entry.CropLeft, entry.CropTop, entry.CropRight, entry.CropBottom) = (0, 0, 1, 1);
        }

        /// <summary>Blend Mode effect choice as the GPU mode index (position in the effect's choices); 0 = normal.</summary>
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

        /// <summary>A layer's GPU-backed Look effects (zone 2) in order; the first LUT and first Curves effect get their tables.</summary>
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
                else if (definition.GpuSlot == GpuShaderSlot.ColorStudio)
                {
                    // Color Grade draws with or without tables, claiming them like LUT and Curves effects (first per layer wins);
                    // the plan's AddCard tells the shader which it holds.
                    var file = card.GetText(ColorGradeKeys.Lut).Trim().Trim('"');
                    if (file.Length > 0 && state.ExistingFiles.Contains(file) && TakeTable(ref entry.Lut, () => resources.Lut(file)))
                        entry.LutCard = card;
                    if (ColorGradeMath.CurvesActive(card) && TakeTable(ref entry.Curve, () => resources.ColorCurves(card.GetText(ColorGradeKeys.CurveMaster),
                            card.GetText(ColorGradeKeys.CurveRed), card.GetText(ColorGradeKeys.CurveGreen), card.GetText(ColorGradeKeys.CurveBlue))))
                        entry.CurveCard = card;
                }

                look.Add(card);
            }

            return [.. look];
        }

        // One LUT and one curve per layer (descriptor holds one of each): a second of either is skipped with a one-time log entry.
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
        /// Key-and-matte steps in effect order: the key (when enabled), mask and edges at their first effect's position, choke and smoothing.
        /// The mask is baked cropped like the picture, scaled to the layer, inverted, grown, offset and feathered; with a mask, Soft Edges
        /// feathers the mask and the rectangle only gets rounded corners (Corner Feather P1 = 0).
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
            var file = layer.CustomMask && layer.HasMask && layer.FxOn(EffectRegistry.Mask) ? MaskStore.SourceOf(layer) : "";
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

        /// <summary>Rounded and feathered edges as a Corner Feather packet; default for a plain rectangle.</summary>
        private static GpuEffectPacket EdgesOf(Layer layer, bool softEdge)
        {
            if (!layer.FxOn(EffectRegistry.Edges))
                return default;
            var corner = Math.Clamp(layer.CornerRadius, 0, 50);
            var feather = softEdge && layer.Feather;
            return corner > 0 || feather ? new GpuEffectPacket(GpuShaderSlot.CornerFeather, corner, feather ? 1 : 0, layer.FeatherRadius, 0, 0, 0, 0, 0) : default;
        }

        /// <summary>Box blur radius that, applied twice, approximates a Gaussian of the given sigma (clamped to the picture).</summary>
        private static int BoxRadius(double sigma, int width, int height)
        {
            var radius = (int)Math.Round((Math.Sqrt(6 * sigma * sigma + 1) - 1) / 2);
            return Math.Clamp(radius, 0, Math.Max(Math.Min(width, height) / 2 - 1, 0));
        }

        /// <summary>
        /// A layer's picture fades: fade in from its start and fade out to its end, each clamped to the remaining length, excluding fades
        /// handed to the overlapping clip by a crossfade. On adjustment layers, motion styles (slide, zoom, whip pan, zoom warp, spin, particle
        /// snap) become dissolves, since they would move the canvas underneath.
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
        /// True when a video hides every twin pixel while active and not fading: box covers the frame; fully opaque, square-cornered,
        /// unrotated, nothing keyed, masked or matted; normal blend with no composite-time effects; only opacity-preserving Look effects
        /// (<see cref="KeepsOpaque"/>); not animated.
        /// </summary>
        private bool CoversWhenOn(Entry video)
        {
            if (video.Layer is not { } layer)
                return false;

            var covers = video.X <= 0 && video.Y <= 0 && video.X + video.W >= _width && video.Y + video.H >= _height;
            var mask = MaskStore.SourceOf(layer);
            var opaque = layer.Opacity >= 100 && !layer.HasKeys(KeyProperty.Opacity) && !layer.ChromaKey && !(layer.CustomMask && state.ExistingFiles.Contains(mask))
                         && layer.CornerRadius == 0 && !layer.Feather && Math.Abs(layer.Rotation % 360) < 0.05 && !layer.IsTilted;
            var plain = video.Mask.IsNone && video.Edges.Slot == GpuShaderSlot.None && Array.TrueForAll(video.Matte, static step => step.Card is null)
                        && video.BlendMode == 0 && video.LaidOn.Length == 0 && Array.TrueForAll(video.Look, KeepsOpaque);
            return covers && opaque && plain && !layer.IsAnimated;
        }

        /// <summary>
        /// Look effects that keep an opaque picture opaque: colour-only effects, or a blur (PSBoxBlur clamps samples at the edges, so edges stay
        /// solid).
        /// </summary>
        private static bool KeepsOpaque(EffectInstance card) => card.Definition?.GpuSlot is GpuShaderSlot.BoxBlur or GpuShaderSlot.Flip or GpuShaderSlot.Lut3D or GpuShaderSlot.Denoise
            or GpuShaderSlot.ColorGrade or GpuShaderSlot.ColorBalance or GpuShaderSlot.ColorTemperature or GpuShaderSlot.Vignette or GpuShaderSlot.FilmGrain
            or GpuShaderSlot.HueSaturation or GpuShaderSlot.ToneCurve or GpuShaderSlot.Sharpen or GpuShaderSlot.HalationGlow or GpuShaderSlot.MotionBlur
            or GpuShaderSlot.ColorStudio;

        // ----- Whole frame -----

        /// <summary>
        /// Whole-frame processing before the Sequence Output effects: denoise (default strength), LUT, and the colour sliders' grade
        /// (<see cref="SequenceExportState.Grade"/>: balance, hue rotation, sharpening) as one adjustment. Null when nothing applies.
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

        /// <summary>Sequence Output effect stack on the finished frame (keys and ranges in sequence seconds).</summary>
        private Entry? SequenceLook()
        {
            var entry = new Entry { Kind = EntryKind.Whole, Name = "Sequence Output", ClipKey = WholeKey };
            entry.Look = LookCards(entry, _low.SequenceEffects);
            return entry.Look.Length > 0 ? entry : null;
        }

        /// <summary>
        /// Colour slider grade as GPU packets in eq, hue, unsharp order: balance as Color Grade (green gamma into luma gamma, red and blue into
        /// the chroma lanes, 0 when balanced), hue rotation as Hue/Saturation, and sharpening as a 5 x 5 kernel in export pixels
        /// (<paramref name="frameWidth"/> wide), expressed as the Sharpen radius in px of a 1080-wide frame.
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

        // ----- Captions -----

        /// <summary>
        /// Captions rendered on a canvas the size of the caption box (at least 16 x 16), placed at the box with its opacity, rounded or soft
        /// edges, mask and drop shadow.
        /// </summary>
        private Entry Captions()
        {
            var caption = state.CaptionLayer;
            var (x, y, w, h) = caption.ComputeOutputRect(_width, _height, 0, 0);
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
