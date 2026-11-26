using HandPegApp.Models;
using HandPegApp.Models.Effects;

namespace HandPegApp.Services.Gpu;

/// <summary>A media frame the scene needs at a time (Instant Seek hints): source id and media time in the worker's clock.</summary>
public readonly record struct GpuMediaHint(int SourceId, double MediaSeconds);

/// <summary>
/// A cut: where the sequence starts reading a file at its own time (<see cref="GpuScenePlan.Cuts"/>). From <see cref="Start"/> to
/// <see cref="End"/> of the sequence, source <see cref="SourceId"/> is read from <see cref="Media"/> (worker file time) at
/// <see cref="Speed"/>, up to <see cref="MediaEnd"/>. <see cref="PathHash"/> is the source table's value at plan creation.
/// </summary>
public readonly record struct GpuCutEntry(double Start, double End, int SourceId, long PathHash, double Media, double MediaEnd, double Speed)
{
    /// <summary>
    /// A speed-ramped clip's keys (Layer.SpeedKeys, snapshotted at plan creation; Pass 5.6.5), null for a constant speed. Then
    /// <see cref="Speed"/> is the ramp's fastest speed (read-ahead spans cover its fastest stretch) and file time follows the ramp from
    /// <see cref="RampMedia"/> at the clip's start <see cref="RampStart"/>.
    /// </summary>
    public Keyframe[]? Ramp { get; init; }

    /// <summary>Sequence time of the ramped clip's start (its keys' zero).</summary>
    public double RampStart { get; init; }

    /// <summary>File time at the ramped clip's start (its MediaOffset).</summary>
    public double RampMedia { get; init; }

    /// <summary>File time read at a sequence time within the entry.</summary>
    public double MediaAt(double sequenceSeconds) => Math.Min(Ramp is { } ramp
        ? RampMedia + Math.Max(SpeedRamp.MediaSeconds(ramp, Speed, Math.Max(sequenceSeconds, Start) - RampStart), 0)
        : Media + Math.Max(sequenceSeconds - Start, 0) * Speed, MediaEnd);
}

/// <summary>Identity of a layer written by <see cref="GpuScenePlan.Evaluate"/>, for 3D exploded view labels and picking (UI thread).</summary>
public readonly record struct GpuLayerInfo(long ClipKey, string ClipId, int TrackIndex, string Name, bool HasMask);

/// <summary>
/// GPU scene plan: built once per edit from a <see cref="SequenceExportState"/> on the UI thread, then evaluated every frame (display rate
/// while playing, hundreds of times a second while scrubbing) into the compositor's <see cref="FrameSceneHeader"/> and
/// <see cref="GpuLayerDescriptor"/>s.
/// <para>Built on the timeline lowering (<c>TimelineLowering.LowerForGpu</c>: one layer per clip, blur twins, stack order), shared with the
/// audio program, and used for export too, so preview and export cannot disagree about which layers exist, where they are, or how they
/// are processed.</para>
/// <para>Media time (A/V sync): <see cref="GpuLayerDescriptor.MediaSeconds"/> is file time with 0 at the first decoded video frame (the
/// worker's MfVideoDecoder treats its first sample time as 0), matching FFmpeg's video stream start (MediaProbe VideoStartSeconds,
/// start_time). Each clip is read from its own file at MediaOffset + (t - start) x speed; no clip uses a container clock. The worker's
/// I-frame list uses the same time base (GpuResourceBroker.SourceFor).</para>
/// <para>Threads: immutable once built. <see cref="Evaluate"/>, <see cref="MediaAt"/>, <see cref="NextCutAfter"/> and <see cref="Cuts"/> may
/// run on one other thread while the UI thread builds the next plan; they allocate nothing (no LINQ, closures or enumerators: arrays
/// indexed directly) and read only immutable lowering data. <see cref="LayerInfoAt"/> is UI-thread only.</para>
/// </summary>
public sealed partial class GpuScenePlan
{
    /// <summary>
    /// Blur fill flag (<see cref="GpuLayerDescriptor"/> bit 2): the worker draws only the layer's blurred background, from its whole source
    /// picture cropped to the frame aspect, at its opacity and dissolve, not the picture itself (a video's twin under its track's pictures).
    /// </summary>
    public const int FlagBlurFillOnly = GpuLayerDescriptor.FlagBlurFillOnly;

    private readonly Entry[] _entries;
    private readonly GpuResourceBroker? _resources;
    private readonly GpuCutEntry[] _cuts;
    private int _warned;

    // Keyframed speed ramps (Pass 5.6.5) of file-reading entries, keys snapshotted from their clips here on the UI thread so evaluation
    // (any thread) never reads a live Layer; null when no clip is ramped (the constant-speed path costs nothing).
    private readonly Dictionary<Entry, Keyframe[]>? _ramps;

    private GpuScenePlan(int width, int height, double sequenceSeconds, Entry[] entries, GpuResourceBroker? resources)
    {
        (CanvasWidth, CanvasHeight, SequenceSeconds) = (width, height, sequenceSeconds);
        (_entries, _resources) = (entries, resources);
        _ramps = RampsOf(entries);
        _cuts = CutsOf(entries, _ramps);
    }

    private static Dictionary<Entry, Keyframe[]>? RampsOf(Entry[] entries)
    {
        Dictionary<Entry, Keyframe[]>? ramps = null;
        foreach (var entry in entries)
        {
            if (entry.Kind is EntryKind.File or EntryKind.Twin && entry.Layer is { HasSpeedRamp: true } layer)
                (ramps ??= [])[entry] = [.. layer.SpeedKeys];
        }

        // A blur fill twin (TimelineLowering.BlurTwinOf) copies only its video's constant speed: it takes the ramp of its own video piece
        // (same clip, start, file offset and speed), so the fill and the picture over it read the same source frame throughout the ramp.
        if (ramps is not null)
        {
            foreach (var twin in entries)
            {
                if (twin.Kind != EntryKind.Twin || twin.ClipKey == 0 || ramps.ContainsKey(twin))
                    continue;

                foreach (var video in entries)
                {
                    if (video.Kind == EntryKind.File && video.ClipKey == twin.ClipKey && video.MediaStart == twin.MediaStart
                        && video.MediaOffset == twin.MediaOffset && video.Speed == twin.Speed && ramps.TryGetValue(video, out var keys))
                    {
                        ramps[twin] = keys;
                        break;
                    }
                }
            }
        }

        return ramps;
    }

    /// <summary>
    /// File seconds an entry reads from its clip's start to sequence time <paramref name="t"/> (negative before it): its speed ramp's
    /// integral (<see cref="SpeedRamp.MediaSeconds"/>, the one mapping picture, sound and captions share), else the constant speed.
    /// Allocation-free.
    /// </summary>
    private static double Into(Entry e, Dictionary<Entry, Keyframe[]>? ramps, double t) =>
        ramps is not null && ramps.TryGetValue(e, out var ramp) ? SpeedRamp.MediaSeconds(ramp, e.Speed, t - e.MediaStart) : (t - e.MediaStart) * e.Speed;

    /// <summary>A plan that draws nothing (black 1920 x 1080 frame).</summary>
    public static GpuScenePlan Empty { get; } = new(1920, 1080, 0, [], null);

    /// <summary>Sequence length in seconds (from the lowering).</summary>
    public double SequenceSeconds { get; }

    /// <summary>Sequence frame duration in seconds (used for shutter timing).</summary>
    public double FrameSeconds { get; private init; } = 1 / 30.0;

    /// <summary>Sequence frame size in pixels: layer boxes use these coordinates.</summary>
    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    /// <summary>
    /// Flags applied to every video picture (sequence Deinterlace and Tone Map to SDR, done in the compositor):
    /// <see cref="GpuLayerDescriptor.FlagDeinterlace"/>, <see cref="GpuLayerDescriptor.FlagToneMapSdr"/>.
    /// </summary>
    public int MediaFlags { get; private init; }

    /// <summary>
    /// A clip's raw picture at a sequence time (no crop, effects or other layers) on a canvas of its native size, shown while drawing a
    /// rectangle on a clip's source (Interactive Crop, Cutout Draw Target); the crop overlay maps exactly onto it. <paramref name="clipKey"/>
    /// is the clip's (<see cref="GpuHash.Of(Guid)"/>); 0, or a clip without video, selects the bottom active video. Writes only the header
    /// while that video is inactive (frame size when none). Allocation-free.
    /// </summary>
    public int EvaluateSource(double sequenceSeconds, long clipKey, ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers)
    {
        var t = sequenceSeconds;
        var entry = SourceEntry(t, clipKey);
        (header.CanvasWidth, header.CanvasHeight) = entry is null ? (CanvasWidth, CanvasHeight) : (entry.PictureWidth, entry.PictureHeight);
        header.PlayheadSeconds = t;
        (header.BackgroundR, header.BackgroundG, header.BackgroundB) = (0, 0, 0);
        header.ActiveLayerCount = 0;
        if (entry is null || layers.Length == 0 || !IsOn(entry, t) || !MediaOf(entry, t, out var source, out var media))
            return 0;

        ref var d = ref layers[0];
        d = default;
        (d.SourceKind, d.SourceId, d.SourcePathHash, d.MediaSeconds) = ((int)GpuSourceKind.Media, source, entry.PathHash, media);
        (d.CropLeft, d.CropTop, d.CropRight, d.CropBottom) = (0, 0, 1, 1);
        (d.X, d.Y, d.Width, d.Height) = (0, 0, entry.PictureWidth, entry.PictureHeight);
        (d.AnchorX, d.AnchorY, d.Opacity) = (0.5f, 0.5f, 1);
        (d.MaskResourceId, d.LutResourceId, d.CurveResourceId, d.StillResourceId) = (-1, -1, -1, -1);
        d.ClipKey = entry.ClipKey;
        d.Flags = GpuLayerDescriptor.FlagVisible;
        header.ActiveLayerCount = 1;
        return 1;
    }

    /// <summary>Video for the source view: the keyed clip's, else the bottom active one; null when none.</summary>
    private Entry? SourceEntry(double t, long clipKey)
    {
        Entry? bottom = null;
        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (entry.Kind != EntryKind.File)
                continue;
            if (clipKey != 0 && entry.ClipKey == clipKey)
                return entry;
            if (bottom is null && IsOn(entry, t))
            {
                bottom = entry;
                if (clipKey == 0)
                    break;
            }
        }

        return bottom;
    }

    /// <summary>Number of drawable entries (before time-based activity filtering).</summary>
    public int EntryCount => _entries.Length;

    // ----- Evaluating -----

    /// <summary>
    /// Writes the scene at a sequence time: canvas, playhead, black background and active layers bottom first
    /// (<see cref="GpuLayerDescriptor.FlagVisible"/> set, <see cref="GpuLayerDescriptor.ZDepth"/> = position,
    /// <see cref="GpuLayerDescriptor.ClipKey"/> = clip), with the count in <see cref="FrameSceneHeader.ActiveLayerCount"/>. At most
    /// <see cref="CompositorProtocol.MaxLayers"/> (topmost extras dropped, logged once). Layers whose texture is pending are skipped for
    /// that frame; nothing waits.
    /// <para><paramref name="compare"/> is the player's grade Before/After (GpuPreviewEngine.SetGradeCompare), passed only by player frames
    /// and the Color Studio input still; export, strips and other stills leave it off.</para>
    /// </summary>
    public int Evaluate(double sequenceSeconds, ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers, GradeCompare compare = default)
    {
        header.CanvasWidth = CanvasWidth;
        header.CanvasHeight = CanvasHeight;
        header.PlayheadSeconds = sequenceSeconds;
        (header.BackgroundR, header.BackgroundG, header.BackgroundB) = (0, 0, 0);
        var count = Walk(sequenceSeconds, layers, [], compare);
        header.ActiveLayerCount = count;
        return count;
    }

    /// <summary>
    /// Media frames needed at a sequence time (active file-reading layers), deduplicated by source and time, up to
    /// <paramref name="into"/>'s length. Allocation-free (Instant Seek hints).
    /// </summary>
    public int MediaAt(double sequenceSeconds, Span<GpuMediaHint> into)
    {
        var count = 0;
        var t = sequenceSeconds;
        for (var i = 0; i < _entries.Length && count < into.Length; i++)
        {
            var entry = _entries[i];
            if (!IsOn(entry, t) || !MediaOf(entry, t, out var source, out var media))
                continue;
            count = Hint(into, count, source, media);
        }

        return count;
    }

    private static int Hint(Span<GpuMediaHint> into, int count, int source, double media)
    {
        for (var k = 0; k < count; k++)
        {
            if (into[k].SourceId == source && Math.Abs(into[k].MediaSeconds - media) < 1e-6)
                return count;
        }

        if (count < into.Length)
            into[count++] = new GpuMediaHint(source, media);
        return count;
    }

    /// <summary>
    /// Next time after <paramref name="sequenceSeconds"/> at which a file-reading layer starts (decode-ahead target for next-cut hints);
    /// infinity when none.
    /// </summary>
    public double NextCutAfter(double sequenceSeconds)
    {
        var next = double.PositiveInfinity;
        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (entry.Kind is EntryKind.File or EntryKind.Twin && entry.From > sequenceSeconds && entry.From < next)
                next = entry.From;
        }

        return next;
    }

    // ----- Cut boundary index -----

    /// <summary>
    /// Every point where the sequence starts reading a file at its own time, sorted by start (then source and media time): each file video,
    /// including a crossfade's later clip from where it starts under the earlier one (its twin reads the same times: one entry). Built once
    /// with the plan; the worker decodes ahead from it (GpuHintWriter: every cut within the look-ahead horizon, not just the next).
    /// </summary>
    public ReadOnlySpan<GpuCutEntry> Cuts => _cuts;

    /// <summary>Index of the first <see cref="Cuts"/> entry starting after a time (count when none). Allocation-free.</summary>
    public int FirstCutAfter(double sequenceSeconds)
    {
        int low = 0, high = _cuts.Length - 1, found = _cuts.Length;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (_cuts[middle].Start > sequenceSeconds)
                (found, high) = (middle, middle - 1);
            else
                low = middle + 1;
        }

        return found;
    }

    private static GpuCutEntry[] CutsOf(Entry[] entries, Dictionary<Entry, Keyframe[]>? ramps)
    {
        var cuts = new List<GpuCutEntry>(entries.Length);

        // Each file video from its first sequence time (MediaOf's media time there).
        foreach (var entry in entries)
        {
            if (entry.Kind is not (EntryKind.File or EntryKind.Twin) || entry.SourceId < 0 || !double.IsFinite(entry.From) || entry.Speed <= 0)
                continue;
            var media = entry.MediaOffset + Math.Max(Into(entry, ramps, entry.From), 0);
            var end = double.IsFinite(entry.To) ? entry.MediaOffset + Math.Max(Into(entry, ramps, entry.To), 0) : double.PositiveInfinity;
            if (entry.MediaDuration > 0.05)
            {
                // A looping video's next round restarts at frame 0 (read on from there: no separate entry).
                if (entry.Loops)
                    (media, end) = (Repeat(media, entry.MediaDuration), entry.MediaDuration);
                else
                    end = Math.Min(end, entry.MediaDuration);
            }
            else if (entry.Loops)
            {
                end = double.PositiveInfinity;
            }

            if (end <= media)
                continue;
            if (ramps is not null && ramps.TryGetValue(entry, out var ramp) && !entry.Loops)
            {
                var fastest = entry.Speed;
                foreach (var key in ramp)
                    fastest = Math.Max(fastest, Math.Clamp(key.Value, Layer.SlowestSpeed, Layer.FastestSpeed));
                cuts.Add(new GpuCutEntry(entry.From, entry.To, entry.SourceId, entry.PathHash, media, end, fastest)
                {
                    Ramp = ramp, RampStart = entry.MediaStart, RampMedia = entry.MediaOffset,
                });
            }
            else
            {
                cuts.Add(new GpuCutEntry(entry.From, entry.To, entry.SourceId, entry.PathHash, media, end, entry.Speed));
            }
        }

        cuts.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.SourceId != b.SourceId ? a.SourceId.CompareTo(b.SourceId) : a.Media.CompareTo(b.Media));
        var kept = new List<GpuCutEntry>(cuts.Count);
        foreach (var cut in cuts)
        {
            if (kept.Count > 0 && kept[^1] is var last && Math.Abs(last.Start - cut.Start) < 1e-6 && last.SourceId == cut.SourceId && Math.Abs(last.Media - cut.Media) < 1e-6)
            {
                // Same file, start and media time (a video and its twin): merge, keeping the farther reach.
                if (cut.MediaEnd > last.MediaEnd)
                    kept[^1] = last with { End = Math.Max(last.End, cut.End), MediaEnd = cut.MediaEnd };
                continue;
            }

            kept.Add(cut);
        }

        return [.. kept];
    }

    /// <summary>Identity of each layer <see cref="Evaluate"/> writes at a time, in the same order (UI thread: 3D view labels and picking).</summary>
    public int LayerInfoAt(double sequenceSeconds, Span<GpuLayerInfo> into)
    {
        var scratch = _scratch ??= new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var written = Walk(sequenceSeconds, scratch, into);
        return Math.Min(written, into.Length);
    }

    [ThreadStatic] private static GpuLayerDescriptor[]? _scratch;

    /// <summary>
    /// UI-thread work as the playhead moves (at most a couple of milliseconds per call): publishes textures finished on the background
    /// lane, rasterizes captions within 10 s not yet drawn, and uploads bitmap subtitles within 10 s not yet present.
    /// </summary>
    public void PrepareAround(double sequenceSeconds)
    {
        if (_resources is null)
            return;
        _resources.Pump();

        // Captions may sit anywhere in the stack: located once and cached.
        if (CaptionEntry is { Captions: { } captions } && captions.PrepareAround(sequenceSeconds))
            _resources.Changed();
        for (var i = _entries.Length - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Kind == EntryKind.PictureSubtitles)
            {
                if (entry.Pictures?.Prepare(_resources, sequenceSeconds) == true)
                    _resources.Changed();
            }
            else if (entry.Kind != EntryKind.Caption)
            {
                break;
            }
        }
    }

    /// <summary>Caption layer key (<see cref="GpuLayerDescriptor.ClipKey"/>): identifies the caption plane in the 3D view.</summary>
    public static readonly long CaptionClipKey = default(GpuHash).Add("captions").Value;

    /// <summary>
    /// 3D view selection key for Background Blur (the Timeline row for the blur fill look, MainViewModel.BackgroundLayer): when selected,
    /// every clip's blur fill plane is outlined. No layer uses it as its ClipKey (a twin carries its video's).
    /// </summary>
    public static readonly long BlurFillKey = default(GpuHash).Add("blur-fill").Value;

    /// <summary>The caption entry wherever it is in the stack (among layers or on top); null when none.</summary>
    private Entry? CaptionEntry => _captionEntry ??= Array.Find(_entries, static e => e.Kind == EntryKind.Caption) ?? NoCaptions;

    private Entry? _captionEntry;
    private static readonly Entry NoCaptions = new() { Kind = EntryKind.Caption };

    /// <summary>True when the plan draws captions.</summary>
    public bool HasCaptions => CaptionEntry is { Captions: not null };

    /// <summary>
    /// Export only (on its render thread, which then owns the plan's tables: GpuExport): rasterizes every undrawn caption between
    /// <paramref name="fromSeconds"/> and <paramref name="toSeconds"/> with no time budget. True when the caption active at
    /// <paramref name="fromSeconds"/> (if any) is drawn and published, so that frame shows it.
    /// </summary>
    public bool PrepareCaptions(double fromSeconds, double toSeconds) =>
        CaptionEntry is not { Captions: { } captions } || captions.PrepareFor(fromSeconds, toSeconds);

    /// <summary>Writes every active entry bottom first, plus their identities when requested.</summary>
    private int Walk(double t, Span<GpuLayerDescriptor> layers, Span<GpuLayerInfo> infos, GradeCompare compare = default)
    {
        var room = Math.Min(layers.Length, CompositorProtocol.MaxLayers);
        var n = 0;
        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (!IsOn(entry, t))
                continue;

            var wrote = compare.Mode == GradeCompareMode.Split && Graded(entry) ? WriteSplit(entry, t, layers, n, room, compare.Wipe)
                : Write(entry, t, layers, n, room, compare.Mode == GradeCompareMode.Bypass ? compare.Card : null);
            if (wrote < 0)
            {
                Warn(1, "More than 64 layers are on at once (CompositorProtocol.MaxLayers): the topmost are not drawn by the GPU engine.");
                break;
            }

            for (var k = n; k < n + wrote; k++)
            {
                layers[k].ZDepth = k;
                if (k < infos.Length)
                    infos[k] = Describe(entry);
            }

            n += wrote;
        }

        return n;
    }

    /// <summary>Logs a message the first time this plan hits a condition (one bit per condition).</summary>
    private void Warn(int bit, string message)
    {
        var before = Interlocked.Or(ref _warned, bit);
        if ((before & bit) == 0)
            AppLog.Write(message);
    }

    // ----- Activity and media mapping -----

    /// <summary>True when an entry is active at a time: within its interval (inclusive) and with its required resources ready.</summary>
    private bool IsOn(Entry e, double t)
    {
        if (t < e.From || t > e.To || (e.ToExclusive && t >= e.To))
            return false;

        switch (e.Kind)
        {
            case EntryKind.File or EntryKind.Twin:
                if (e.Kind == EntryKind.Twin && e.CoveredBy is { } cover && Hides(cover, t))
                    return false;
                if (e.Loops)
                    return true;
                var media = e.MediaOffset + Into(e, _ramps, t);
                return !(e.MediaDuration > 0.05 && media >= e.MediaDuration) || HoldsLastFrame(e, t);
            case EntryKind.Fade:
                return FadeLevel(e, t) > 0.0005;
            case EntryKind.Caption:
                return e.Captions?.CueAt(t) is { Raster: not null };
            case EntryKind.PictureSubtitles:
                return _resources is not null && e.Pictures?.At(t, _resources, out _, out _) == true;
            default:
                return true;
        }
    }

    /// <summary>
    /// True when a twin's covering video hides it completely at <paramref name="t"/> (Law 3: a full-frame video never pays for an invisible
    /// blur): the video (<see cref="Entry.CoveredBy"/>, set only when opaque, square, unrotated, unmasked, static and frame-covering) is
    /// active and none of its fades is running.
    /// </summary>
    private bool Hides(Entry cover, double t)
    {
        if (!IsOn(cover, t))
            return false;
        for (var k = 0; k < cover.Fades.Length; k++)
        {
            if (cover.Fades[k].Holds(t))
                return false;
        }

        return true;
    }

    /// <summary>Source and media time a file-reading entry reads at a sequence time.</summary>
    private bool MediaOf(Entry e, double t, out int source, out double media)
    {
        (source, media) = (e.SourceId, 0);
        switch (e.Kind)
        {
            case EntryKind.File or EntryKind.Twin:
                var into = Into(e, _ramps, t);
                media = e.Loops && e.MediaDuration > 0.05 ? Repeat(e.MediaOffset + into, e.MediaDuration) : e.MediaOffset + Math.Max(into, 0);

                // A ramped clip's tail frame within the EOF grace (HoldsLastFrame) reads the file's last frame, never past it.
                if (!e.Loops && e.MediaDuration > 0.05 && media >= e.MediaDuration && _ramps is not null && _ramps.ContainsKey(e))
                    media = Math.Max(e.MediaDuration - RampEofHold, 0);
                return source >= 0;
            default:
                return false;
        }
    }

    /// <summary>File seconds before its end that a ramped clip's held tail frame reads (the last decoded frame starts before it).</summary>
    public const double RampEofHold = 1e-3;

    /// <summary>
    /// Pass 5.7.1 EOF guard: a speed-ramped clip whose ramp reaches the file's end within its last sequence frame (the integral's rounding,
    /// a frame-aligned end) holds the file's last frame there instead of dropping out to black. Only that one frame: a ramp that genuinely
    /// runs past the file (a project from before auto-duration) still ends where the file does. Allocation-free.
    /// </summary>
    private bool HoldsLastFrame(Entry e, double t) =>
        _ramps is not null && _ramps.ContainsKey(e) && e.MediaOffset + Into(e, _ramps, t - FrameSeconds - 1e-6) < e.MediaDuration;

    // Looping video media time: the file replayed from frame 0, wrapped modulo its length.
    private static double Repeat(double moment, double length)
    {
        var within = moment % length;
        return within < 0 ? within + length : within;
    }

    // ----- Writing -----

    /// <summary>
    /// Writes an entry's layers at <paramref name="n"/> (a karaoke line's sung part is a second layer); returns the count, or -1 when out of
    /// room. <paramref name="bypass"/> omits a grade: null none, <see cref="Guid.Empty"/> every Color Grade effect plus the colour slider
    /// grade, otherwise only the Color Grade effect with that id.
    /// </summary>
    private int Write(Entry e, double t, Span<GpuLayerDescriptor> layers, int n, int room, Guid? bypass = null)
    {
        if (n >= room)
            return -1;

        ref var d = ref layers[n];
        d = default;
        d.ClipKey = e.ClipKey;
        (d.MaskResourceId, d.LutResourceId, d.CurveResourceId, d.StillResourceId) = (-1, -1, -1, -1);
        d.Flags = GpuLayerDescriptor.FlagVisible;
        (d.AnchorX, d.AnchorY, d.Opacity) = (0.5f, 0.5f, 1);
        (d.CropLeft, d.CropTop, d.CropRight, d.CropBottom) = (e.CropLeft, e.CropTop, e.CropRight, e.CropBottom);

        switch (e.Kind)
        {
            case EntryKind.File:
                if (!MediaOf(e, t, out var source, out var media) || !UsableMask(e, ref d))
                    return 0;
                Media(ref d, e, source, media);
                WritePicture(e, t, ref d, bypass);
                return d.Opacity > 0.0005f ? 1 : 0;

            case EntryKind.Still or EntryKind.Text:
                if (_resources is null || _resources.StateOf(e.Still) != GpuResourceState.Ready || !UsableMask(e, ref d))
                    return 0;
                (d.SourceKind, d.StillResourceId) = ((int)GpuSourceKind.Still, e.Still.Id);
                WritePicture(e, t, ref d, bypass);
                return d.Opacity > 0.0005f ? 1 : 0;

            case EntryKind.Adjustment:
                (d.X, d.Y, d.Width, d.Height) = (e.X, e.Y, e.W, e.H);
                d.Flags |= GpuLayerDescriptor.FlagAdjustment;
                AddLook(e, t - e.KeyStart, ref d, bypass);
                AddFades(e.Fades, t, ref d);
                SequenceShutter(e, t, ref d);
                return d.EffectCount > 0 ? 1 : 0;

            case EntryKind.Twin:
            {
                if (!MediaOf(e, t, out var from, out var moment))
                    return 0;
                Media(ref d, e, from, moment);
                (d.X, d.Y, d.Width, d.Height) = (0, 0, CanvasWidth, CanvasHeight);

                // Centre crop for the zoom (GpuScenePlan.Create's Blur).
                (d.CropLeft, d.CropTop, d.CropRight, d.CropBottom) = (e.CropLeft, e.CropTop, e.CropRight, e.CropBottom);
                d.Flags |= FlagBlurFillOnly;
                (d.BlurFillRadius, d.BlurFillPasses, d.BlurFillDim) = (e.BlurRadius, e.BlurPasses, e.BlurDim);

                // Blur fill Look effects after the blur (keys and ranges in sequence seconds); the video's opacity and its fades as
                // dissolves (Create's Twin).
                for (var k = 0; k < e.Look.Length; k++)
                    AddCard(e, e.Look[k], t, null, ref d, bypass);
                d.Opacity = OpacityAt(e, t);
                AddFades(e.Fades, t, ref d);
                return d.Opacity > 0.0005f ? 1 : 0;
            }

            case EntryKind.Whole:
                (d.X, d.Y, d.Width, d.Height) = (0, 0, CanvasWidth, CanvasHeight);
                d.Flags |= GpuLayerDescriptor.FlagAdjustment;
                foreach (var packet in e.Packets)
                {
                    // Before/After bypass: the colour sliders' LUT, balance and hue rotation make up their grade.
                    if (bypass == Guid.Empty && LegacyGrade(packet.Slot))
                        continue;
                    if (packet.Slot == GpuShaderSlot.Lut3D)
                    {
                        if (_resources?.StateOf(e.Lut) != GpuResourceState.Ready)
                            continue;
                        d.LutResourceId = e.Lut.Id;
                    }

                    Add(ref d, packet);
                }

                // Sequence Output effects: keys and ranges in sequence seconds.
                for (var k = 0; k < e.Look.Length; k++)
                    AddCard(e, e.Look[k], t, null, ref d, bypass);
                SequenceShutter(e, t, ref d);
                return d.EffectCount > 0 ? 1 : 0;

            case EntryKind.Fade:
                (d.X, d.Y, d.Width, d.Height) = (0, 0, CanvasWidth, CanvasHeight);
                d.SourceKind = (int)GpuSourceKind.SolidColor;
                (d.SolidR, d.SolidG, d.SolidB, d.SolidA) = (0, 0, 0, 1);
                d.Opacity = (float)FadeLevel(e, t);
                return 1;

            case EntryKind.Caption:
                return WriteCaption(e, t, layers, n, room);

            case EntryKind.PictureSubtitles:
                return WritePictureSubtitle(e, t, ref d);

            default:
                return 0;
        }
    }

    // ----- Grade Before/After -----

    /// <summary>True for whole-frame packets belonging to the colour slider grade (omitted by Before/After): LUT, balance, hue rotation.</summary>
    private static bool LegacyGrade(GpuShaderSlot slot) => slot is GpuShaderSlot.Lut3D or GpuShaderSlot.ColorGrade or GpuShaderSlot.HueSaturation;

    /// <summary>True when an entry is graded: an enabled Color Grade Look effect, or the colour slider grade. Index-walked.</summary>
    private static bool Graded(Entry e)
    {
        for (var k = 0; k < e.Look.Length; k++)
        {
            if (e.Look[k] is { Enabled: true, Definition.GpuSlot: GpuShaderSlot.ColorStudio })
                return true;
        }

        for (var k = 0; e.Kind == EntryKind.Whole && k < e.Packets.Length; k++)
        {
            if (LegacyGrade(e.Packets[k].Slot))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Splits a graded entry at the divider (<paramref name="wipe"/>, 0..1 of canvas width): ungraded on the left, graded on the right. Each
    /// half is the entry written again with a hard-edged wipe (SceneRenderer.SetWipe compare edge): at most one extra layer, none for a
    /// layer wholly on one side. Layers the wipe cannot follow (rotated, tilted, in a transition, box grown or moved at composite, full-frame
    /// blur fill, no packet room) are shown ungraded, like plain Before.
    /// </summary>
    private int WriteSplit(Entry e, double t, Span<GpuLayerDescriptor> layers, int n, int room, double wipe)
    {
        var wrote = Write(e, t, layers, n, room);
        if (wrote != 1)
            return wrote;

        ref var graded = ref layers[n];
        if (!Splittable(graded))
            return Write(e, t, layers, n, room, Guid.Empty);

        var share = (Math.Max(0, Math.Min(double.IsFinite(wipe) ? wipe : 0.5, 1)) * CanvasWidth - graded.X) / graded.Width;
        if (share <= 0)
            return 1;
        if (share >= 1 || n + 1 >= room)
            return Write(e, t, layers, n, room, Guid.Empty);

        layers[n + 1] = graded;
        var before = Write(e, t, layers, n, room, Guid.Empty);
        if (before != 1)
        {
            // Nothing to draw ungraded (an adjustment whose only effect is its grade): draw only the graded half.
            layers[n] = layers[n + 1];
            Add(ref layers[n], CompareWipe((float)share, keepLeft: false));
            return 1;
        }

        if (layers[n].EffectCount >= CompositorProtocol.MaxEffectsPerLayer)
        {
            layers[n] = layers[n + 1];
            return Write(e, t, layers, n, room, Guid.Empty);
        }

        Add(ref layers[n], CompareWipe((float)share, keepLeft: true));
        Add(ref layers[n + 1], CompareWipe((float)share, keepLeft: false));
        return 2;
    }

    /// <summary>Compare wipe packet keeping the left (or right) side of a divider at <paramref name="share"/> of the box width.</summary>
    internal static GpuEffectPacket CompareWipe(float share, bool keepLeft) =>
        new(GpuShaderSlot.TransitionWipe, share, 1, 0, 0, keepLeft ? 1 : 0, CompareEdgePixels, 0, 0);

    /// <summary>Compare divider edge width in output pixels (SceneRenderer.SetWipe: lane 5 above 0 marks a hard compare edge).</summary>
    internal const float CompareEdgePixels = 1;

    /// <summary>True when a written layer can be wipe-split: axis-aligned, placed at its box, and room for one more packet.</summary>
    private static bool Splittable(in GpuLayerDescriptor d)
    {
        if (d.Rotation != 0 || d.TiltX != 0 || d.TiltY != 0 || !(d.Width > 0) || !(d.Height > 0) || d.EffectCount >= CompositorProtocol.MaxEffectsPerLayer
            || (d.Flags & (FlagBlurFillOnly | GpuLayerDescriptor.FlagShutterPlace)) != 0)
            return false;

        for (var k = 0; k < d.EffectCount; k++)
        {
            var slot = d.Effects[k].Slot;
            if (slot >= GpuShaderSlot.TransitionDissolve && slot != GpuShaderSlot.TransitionDissolve || slot == GpuShaderSlot.RimGlass)
                return false;
        }

        return true;
    }

    private void Media(ref GpuLayerDescriptor d, Entry e, int source, double media)
    {
        d.SourceKind = (int)GpuSourceKind.Media;
        (d.SourceId, d.MediaSeconds, d.SourcePathHash) = (source, media, e.PathHash);
        d.Flags |= MediaFlags | (e.Interpolate ? GpuLayerDescriptor.FlagInterpolate : 0);
    }

    /// <summary>Removes a slot's packets from a layer, so an overlaid copy (sung caption) casts no second shadow.</summary>
    private static void Without(ref GpuLayerDescriptor d, GpuShaderSlot slot)
    {
        var kept = 0;
        for (var k = 0; k < d.EffectCount; k++)
        {
            if (d.Effects[k].Slot != slot)
                d.Effects[kept++] = d.Effects[k];
        }

        d.EffectCount = kept;
    }

    /// <summary>Writes a caption: its state's rows of the still in the caption box; for karaoke, the sung state cropped at the sweep on top.</summary>
    private int WriteCaption(Entry e, double t, Span<GpuLayerDescriptor> layers, int n, int room)
    {
        if (e.Captions?.CueAt(t) is not { Raster: { } raster } cue || _resources?.StateOf(raster.Still) != GpuResourceState.Ready || !UsableMask(e, ref layers[n]))
            return 0;

        ref var d = ref layers[n];
        (d.SourceKind, d.StillResourceId) = ((int)GpuSourceKind.Still, raster.Still.Id);
        (d.X, d.Y, d.Width, d.Height) = (e.X, e.Y, e.W, e.H);
        d.Opacity = e.Opacity;
        for (var k = 0; k < e.Packets.Length; k++)
            Add(ref d, e.Packets[k]);
        if (d.MaskResourceId >= 0)
            Add(ref d, new GpuEffectPacket(GpuShaderSlot.LayerMask, 0, 0, 0, 0, 0, 0, 0, 0));
        if (e.Shadow is { } shadow)
            AddCard(e, shadow, t, e.Layer, ref d);

        var word = cue.WordAt(t);
        var state = cue.Pop ? Math.Min(word, raster.States - 1) : 0;
        var (top, bottom) = raster.Rows(state);
        (d.CropLeft, d.CropTop, d.CropRight, d.CropBottom) = (0, top, 1, bottom);
        if (cue.Pop || n + 1 >= room)
            return 1;

        // Karaoke Sweep: sung state from the line's left edge to the sweep, which crosses each word (and trailing space) while spoken.
        var span = cue.WordEnd(word) - cue.WordStarts[word];
        var through = span > 1e-6 ? Math.Clamp((t - cue.WordStarts[word]) / span, 0, 1) : 1;
        var sweep = raster.Lefts[word] + (float)through * (raster.Rights[word] - raster.Lefts[word]);
        var fraction = Math.Clamp(sweep / Math.Max(raster.Width, 1), 0, 1);
        if (fraction <= 0.0005f)
            return 1;

        ref var sung = ref layers[n + 1];
        sung = d;
        (top, bottom) = raster.Rows(1);
        (sung.CropLeft, sung.CropTop, sung.CropRight, sung.CropBottom) = (0, top, fraction, bottom);
        sung.Width = e.W * fraction;

        // Strip shadow, corners and mask from the narrower overlay (they would apply at the sweep edge; the base layer has them).
        Without(ref sung, GpuShaderSlot.DropShadow);
        Without(ref sung, GpuShaderSlot.CornerFeather);
        Without(ref sung, GpuShaderSlot.LayerMask);
        sung.MaskResourceId = -1;
        return 2;
    }

    /// <summary>Writes everything except the source: placement at the time, blend, effects by zone, fades and shutter.</summary>
    private void WritePicture(Entry e, double t, ref GpuLayerDescriptor d, Guid? bypass = null)
    {
        Place(e, t, ref d);
        d.BlendMode = e.BlendMode;
        AddEffects(e, t - e.KeyStart, ref d, bypass);
        AddFades(e.Fades, t, ref d);
        Shutter(e, t, ref d);
    }

    /// <summary>Max file frames per sequence frame: beyond the fastest clip speed, a jump between frames is a cut or loop wrap.</summary>
    private const double MostShutterSpeed = Layer.FastestSpeed + 1;

    /// <summary>The entry's Motion Blur Look effect and definition; null when none. Index-walked (called every frame).</summary>
    private static EffectInstance? MotionCard(Entry e, out EffectDefinition? definition)
    {
        for (var k = 0; k < e.Look.Length; k++)
        {
            if (e.Look[k].Definition is { GpuSlot: GpuShaderSlot.MotionBlur } motion)
            {
                definition = motion;
                return e.Look[k];
            }
        }

        definition = null;
        return null;
    }

    /// <summary>
    /// Motion Blur inputs at a time: the layer's placement at shutter open and close (when it moves), and the file time one sequence frame
    /// ago so speed ramps blur by their own speed. Only for layers with the effect; other descriptors are untouched.
    /// </summary>
    private void Shutter(Entry e, double t, ref GpuLayerDescriptor d)
    {
        if (MotionCard(e, out var definition) is not { Enabled: true } card || card.EvaluateGpuPacket(definition!, t - e.KeyStart, e.Layer) is not { Slot: GpuShaderSlot.MotionBlur } packet)
            return;

        // Across a cut or loop wrap the previous frame is a different shot: no motion (speed ramps stay well within this bound).
        if (MomentAgo(e, t - FrameSeconds, out var source, out var before) && source == d.SourceId && Math.Abs(d.MediaSeconds - before) <= FrameSeconds * MostShutterSpeed)
        {
            d.PreviousMoment = before;
            d.Flags |= GpuLayerDescriptor.FlagPreviousMoment;
        }

        if (packet.P3 < 0.5f || e.KeysX is null && e.KeysY is null && e.KeysScale is null && e.KeysRotation is null)
            return;

        // Amounts above 50 % extend the shutter (MotionBoost.Span; 1 up to 50 %).
        var half = Math.Clamp(packet.P0, 0, 720) / 360 * FrameSeconds / 2 * MotionBoost.Span(packet.P4);
        var (from, to) = (BoxAt(e, t - half), BoxAt(e, t + half));
        if (from == to)
            return;

        (d.ShutterFromX, d.ShutterFromY, d.ShutterFromWidth, d.ShutterFromHeight, d.ShutterFromRotation) = from;
        (d.ShutterToX, d.ShutterToY, d.ShutterToWidth, d.ShutterToHeight, d.ShutterToRotation) = to;
        d.Flags |= GpuLayerDescriptor.FlagShutterPlace;
    }

    /// <summary>
    /// File time shown on the picture's track at <paramref name="t"/>, for the shutter: the entry's own while active, else the active clip
    /// of the same track (a split clip that plays on is still one shot); false when none. Allocation-free.
    /// </summary>
    private bool MomentAgo(Entry e, double t, out int source, out double media)
    {
        if (IsOn(e, t))
            return MediaOf(e, t, out source, out media);

        for (var i = 0; e.TrackIndex >= 0 && i < _entries.Length; i++)
        {
            var other = _entries[i];
            if (other != e && other.Kind == e.Kind && other.TrackIndex == e.TrackIndex && IsOn(other, t))
                return MediaOf(other, t, out source, out media);
        }

        (source, media) = (-1, 0);
        return false;
    }

    /// <summary>Shutter for adjustment layers and the final picture: the sequence time one frame ago, to tell sequential frames from jumps (SceneRenderer.CanvasFlow).</summary>
    private void SequenceShutter(Entry e, double t, ref GpuLayerDescriptor d)
    {
        if (MotionCard(e, out _) is { Enabled: true })
        {
            d.PreviousMoment = t - FrameSeconds;
            d.Flags |= GpuLayerDescriptor.FlagPreviousMoment;
        }
    }

    /// <summary>
    /// Layer placement at a time: corner from X/Y keys; width from Scale keys with height keeping the aspect (anchor fixed when only size
    /// is animated); rotation from Rotation keys (or static) about the anchor; opacity from Opacity keys (or static); tilt from its keys.
    /// </summary>
    private void Place(Entry e, double t, ref GpuLayerDescriptor d)
    {
        (d.X, d.Y, d.Width, d.Height, d.Rotation) = BoxAt(e, t);
        (d.AnchorX, d.AnchorY) = (e.AnchorX, e.AnchorY);
        d.Opacity = OpacityAt(e, t);
        (d.TiltX, d.TiltY) = (TiltAt(e.KeysTiltX, e.TiltX, t - e.KeyStart), TiltAt(e.KeysTiltY, e.TiltY, t - e.KeyStart));
    }

    // Tilt at a clip time: keyed value clamped to the limit, or the static value.
    private static float TiltAt(Keyframe[]? keys, float set, double at) =>
        keys is { Length: > 0 } ? (float)Layer.Tilt(EasingCurves.ValueAt(keys, at)) : set;

    /// <summary>Layer box and rotation at a time (<see cref="Place"/>).</summary>
    private (float X, float Y, float Width, float Height, float Rotation) BoxAt(Entry e, double t)
    {
        var at = t - e.KeyStart;
        double width = e.W, height = e.H;
        if (e.KeysScale is { } scale)
        {
            width = EasingCurves.ValueAt(scale, at) * CanvasWidth;
            height = width * e.Shape;
        }

        var x = e.KeysX is { } keysX ? EasingCurves.ValueAt(keysX, at) * CanvasWidth : e.KeysScale is null ? e.X : e.X + e.AnchorX * (e.W - width);
        var y = e.KeysY is { } keysY ? EasingCurves.ValueAt(keysY, at) * CanvasHeight : e.KeysScale is null ? e.Y : e.Y + e.AnchorY * (e.H - height);
        var turn = !e.Turns ? 0 : e.KeysRotation is { } keys ? (float)EasingCurves.ValueAt(keys, at) : e.Rotation;
        return ((float)x, (float)y, (float)width, (float)height, turn);
    }

    private static float OpacityAt(Entry e, double t) =>
        e.KeysOpacity is { } keys ? (float)Math.Clamp(EasingCurves.ValueAt(keys, t - e.KeyStart) / 100, 0, 1) : e.Opacity;

    /// <summary>
    /// True when a masked entry can draw now, setting its mask: the bake for its size, or an older-size stand-in while baking. Not before
    /// any bake exists (an unmasked HUD element would flash its whole rectangle); a failed bake is omitted like a missing mask file.
    /// </summary>
    private bool UsableMask(Entry e, ref GpuLayerDescriptor d)
    {
        if (e.Mask.IsNone && e.MaskStandIn.IsNone)
            return true;
        if (_resources is null)
            return false;

        var state = _resources.StateOf(e.Mask);
        if (state == GpuResourceState.Ready)
        {
            d.MaskResourceId = e.Mask.Id;
            return true;
        }

        if (!e.MaskStandIn.IsNone && _resources.StateOf(e.MaskStandIn) == GpuResourceState.Ready)
        {
            d.MaskResourceId = e.MaskStandIn.Id;
            return true;
        }

        return state is GpuResourceState.Failed or GpuResourceState.None;
    }

    // ----- Effects -----

    /// <summary>Adds a layer's effects in pipeline order: Look effects (zone 2), key and matte (zone 3), shadow, then composite-time effects.</summary>
    private void AddEffects(Entry e, double clipTime, ref GpuLayerDescriptor d, Guid? bypass = null)
    {
        AddLook(e, clipTime, ref d, bypass);
        for (var k = 0; k < e.Matte.Length; k++)
        {
            var step = e.Matte[k];
            if (step.Card is { } card)
            {
                AddCard(e, card, clipTime, e.Layer, ref d);
                continue;
            }

            // Mask and edges at the position of their first effect (see Create's PlaceMasks).
            if (d.MaskResourceId >= 0)
                Add(ref d, new GpuEffectPacket(GpuShaderSlot.LayerMask, 0, 0, 0, 0, 0, 0, 0, 0));
            if (e.Edges.Slot != GpuShaderSlot.None)
                Add(ref d, e.Edges);
        }

        if (e.Shadow is { } shadow)
            AddCard(e, shadow, clipTime, e.Layer, ref d);
        for (var k = 0; k < e.LaidOn.Length; k++)
            AddCard(e, e.LaidOn[k], clipTime, e.Layer, ref d);
    }

    private void AddLook(Entry e, double clipTime, ref GpuLayerDescriptor d, Guid? bypass = null)
    {
        for (var k = 0; k < e.Look.Length; k++)
            AddCard(e, e.Look[k], clipTime, e.Layer, ref d, bypass);
    }

    /// <summary>
    /// Adds an effect's packets at a clip time; effects needing a texture are skipped until it is ready. Color Grade draws with or without
    /// its tables (last lane flags which: GpuColorTables.StudioTablesLane) and is omitted by a Before/After <paramref name="bypass"/>
    /// (see <see cref="Write"/>).
    /// </summary>
    private void AddCard(Entry e, EffectInstance card, double clipTime, Layer? layer, ref GpuLayerDescriptor d, Guid? bypass = null)
    {
        if (card.Definition is not { } definition)
            return;
        if (definition.GpuSlot == GpuShaderSlot.ColorStudio && bypass is { } leftOut && (leftOut == Guid.Empty || leftOut == card.Id))
            return;

        var count = definition.GpuPacketCount;
        for (var k = 0; k < count; k++)
        {
            var packet = card.EvaluateGpuPacket(definition, clipTime, layer, k);
            if (packet.Slot == GpuShaderSlot.None)
                return;

            if (packet.Slot == GpuShaderSlot.Lut3D)
            {
                if (_resources?.StateOf(e.Lut) != GpuResourceState.Ready)
                    return;
                d.LutResourceId = e.Lut.Id;
            }
            else if (packet.Slot == GpuShaderSlot.ToneCurve)
            {
                if (_resources?.StateOf(e.Curve) != GpuResourceState.Ready)
                    return;
                d.CurveResourceId = e.Curve.Id;
            }
            else if (packet.Slot == GpuShaderSlot.ColorStudio && k == 1)
            {
                // Color Grade LUT and curves when owned by this effect (one of each per layer: Create's TakeTable) and ready.
                var lut = e.LutCard == card && _resources?.StateOf(e.Lut) == GpuResourceState.Ready;
                var curves = e.CurveCard == card && _resources?.StateOf(e.Curve) == GpuResourceState.Ready;
                if (lut)
                    d.LutResourceId = e.Lut.Id;
                if (curves)
                    d.CurveResourceId = e.Curve.Id;
                packet = packet with { P7 = GpuColorTables.StudioTablesLane(packet.P7, curves, lut) };
            }

            Add(ref d, packet);
        }
    }

    private void Add(ref GpuLayerDescriptor d, GpuEffectPacket packet)
    {
        if (d.EffectCount >= CompositorProtocol.MaxEffectsPerLayer)
        {
            Warn(2, "A layer has more than 16 effect passes (CompositorProtocol.MaxEffectsPerLayer): the GPU engine leaves out the last.");
            return;
        }

        d.Effects[d.EffectCount++] = packet;
    }

    // ----- Fades and transitions -----

    /// <summary>A picture fade on the sequence: from Start for Length seconds, in (In) or out, in a style.</summary>
    private readonly record struct Fade(double Start, double Length, bool In, TransitionStyle Style)
    {
        /// <summary>True while active: start inclusive, end exclusive.</summary>
        public bool Holds(double t) => t >= Start && t < Start + Length;
    }

    /// <summary>Adds packets for fades active at a time (TransitionStyles.GpuPacket: progress along the style curve and direction).</summary>
    private void AddFades(ReadOnlySpan<Fade> fades, double t, ref GpuLayerDescriptor d)
    {
        for (var k = 0; k < fades.Length; k++)
        {
            var fade = fades[k];
            if (fade.Holds(t))
                Add(ref d, TransitionStyles.GpuPacket(fade.Style, (t - fade.Start) / fade.Length, fade.In));
        }
    }

    /// <summary>Black level of the whole-frame fade at a time (one second in from black, one second out to black).</summary>
    private double FadeLevel(Entry e, double t)
    {
        var level = 0.0;
        if (e.FadesIn && t < WholeFadeSeconds)
            level = 1 - Math.Clamp(t / WholeFadeSeconds, 0, 1);
        if (e.FadesOut && SequenceSeconds > WholeFadeSeconds && t > SequenceSeconds - WholeFadeSeconds)
            level = Math.Max(level, Math.Clamp((t - (SequenceSeconds - WholeFadeSeconds)) / WholeFadeSeconds, 0, 1));
        return level;
    }

    /// <summary>Whole-frame fade duration in seconds (matches AudioProgram.FadeSeconds).</summary>
    private const double WholeFadeSeconds = 1;

    // ----- Descriptions -----

    private static GpuLayerInfo Describe(Entry e) => new(e.ClipKey, e.ClipId, e.TrackIndex, e.Name, e.HasMask);
}
