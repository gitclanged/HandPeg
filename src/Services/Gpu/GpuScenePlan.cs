using System.Globalization;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Models.Timeline;

namespace HandPegApp.Services.Gpu;

/// <summary>A media frame the scene needs at a moment (Instant Seek's hints): the source and its moment in the worker's time.</summary>
public readonly record struct GpuMediaHint(int SourceId, double MediaSeconds);

/// <summary>
/// A place where the sequence starts reading a file at a moment of its own (a cut; Pass 5.0-Release's cut boundary index,
/// <see cref="GpuScenePlan.Cuts"/>): from <see cref="Start"/> to <see cref="End"/> of the sequence, source
/// <see cref="SourceId"/> is read from <see cref="Media"/> on (the worker's time of the file), at <see cref="Speed"/>, up to
/// <see cref="MediaEnd"/>. <see cref="PathHash"/> is the source's as the source table held it when the plan was made.
/// </summary>
public readonly record struct GpuCutEntry(double Start, double End, int SourceId, long PathHash, double Media, double MediaEnd, double Speed)
{
    /// <summary>The moment of the file read at a moment of the sequence within the entry.</summary>
    public double MediaAt(double sequenceSeconds) => Math.Min(Media + Math.Max(sequenceSeconds - Start, 0) * Speed, MediaEnd);
}

/// <summary>What a layer written by <see cref="GpuScenePlan.Evaluate"/> is, for the 3D exploded view's labels and picking (UI thread).</summary>
public readonly record struct GpuLayerInfo(long ClipKey, string ClipId, int TrackIndex, string Name, bool HasMask);

/// <summary>
/// What the GPU engine draws (Pass 5.0-AudioAnd3D): made once per edit from a <see cref="SequenceExportState"/>, on the UI
/// thread, then evaluated for every frame (at the display's rate while playing, up to some hundreds of times a second while
/// scrubbing) into the compositor's <see cref="FrameSceneHeader"/> and <see cref="GpuLayerDescriptor"/>s.
/// <para>It is built on the very lowering the FFmpeg path writes its graph from (<c>FilterGraphBuilder.LowerForGpu</c>:
/// the primary chain, its one piece and riders, the blurred background, the twins, the stack), and follows
/// FilterGraphBuilder.Video.cs lane for lane (BuildFrameEngine, AppendMainClips, AppendLayer, AppendVideoFile,
/// AppendKeyAndMatte, BuildLayerMask, PictureFades, WithTransitions, TwinBlur, StreamCropOf, PictureCropOf): the GPU and
/// the graph cannot disagree about which layers exist, where they are, or what is done to them.</para>
/// <para>Media time (A/V sync): <see cref="GpuLayerDescriptor.MediaSeconds"/> is the file's own time with 0 at its first
/// decoded video frame (the worker's MfVideoDecoder takes its first sample's time as 0), which is where FFmpeg's video
/// stream starts (MediaProbe's VideoStartSeconds, the stream's start_time). A clip read from its file (AppendVideoFile:
/// trim on the file's own time stamps from MediaStarts[file] = that start) is at MediaOffset + (t - start) x speed of it
/// directly. The primary chain is read from the export's first input, whose frames FFmpeg counts from the container's
/// start (the player's time-pos, which the timeline's main clips are in), so its moment m is m - (VideoStart -
/// ContainerStart) of the file's own time (<see cref="ChainShift"/>; 0 for nearly every file). The worker's I-frame list is
/// given in the same time (GpuResourceBroker.SourceFor). 5.0-Verify checks this frame for frame.</para>
/// <para>Threads: immutable once made. <see cref="Evaluate"/>, <see cref="MediaAt"/>, <see cref="NextCutAfter"/> and <see cref="Cuts"/> may run
/// on one other thread while the UI thread makes the next plan; they allocate nothing (no LINQ, no closures, no
/// enumerators: arrays walked by index) and read what the lowering made, which nothing changes after. <see cref="LayerInfoAt"/>
/// is the UI thread's.</para>
/// </summary>
public sealed partial class GpuScenePlan
{
    /// <summary>
    /// The flag a blurred background is written with (a proposed <see cref="GpuLayerDescriptor"/> flag, bit 2: the worker
    /// draws only the layer's blurred background, made of its whole source picture cut to the frame's shape, at its opacity
    /// and dissolve, not the picture itself): the chain's background under everything, and a twin under its video.
    /// </summary>
    public const int FlagBlurFillOnly = GpuLayerDescriptor.FlagBlurFillOnly;

    private readonly Entry[] _entries;
    private readonly ChainMap _chain;
    private readonly GpuResourceBroker? _resources;
    private readonly GpuCutEntry[] _cuts;
    private int _warned;

    private GpuScenePlan(int width, int height, double sequenceSeconds, Entry[] entries, ChainMap chain, GpuResourceBroker? resources)
    {
        (CanvasWidth, CanvasHeight, SequenceSeconds) = (width, height, sequenceSeconds);
        (_entries, _chain, _resources) = (entries, chain, resources);
        _cuts = CutsOf(entries, chain);
    }

    /// <summary>A plan that draws nothing (a black frame of 1920 x 1080).</summary>
    public static GpuScenePlan Empty { get; } = new(1920, 1080, 0, [], ChainMap.None, null);

    /// <summary>How long the sequence is, in seconds (the lowering's).</summary>
    public double SequenceSeconds { get; }

    /// <summary>The sequence's frame, in its own pixels: the layers' boxes are in these.</summary>
    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    /// <summary>Where the primary chain's moments (the player's time-pos) lie from its file's own time: VideoStart - ContainerStart.</summary>
    public double ChainShift => _chain.Shift;

    /// <summary>The primary file in the source table (-1 none), and its picture's size: the source view (<see cref="EvaluateSource"/>).</summary>
    public int PrimarySource { get; private init; } = -1;
    public long PrimaryPathHash { get; private init; }
    public int SourceWidth { get; private init; } = 1920;
    public int SourceHeight { get; private init; } = 1080;

    /// <summary>
    /// The primary file's own picture at a moment of the sequence, whole and unfinished (no crop, no effects, no other
    /// layer), on a canvas of the source's own size: what the player shows while a rectangle is drawn on the source (the
    /// crop, a layer's place) and with Live Preview off. The player's crop overlay maps onto exactly this picture. Writes
    /// nothing but the header between the chain's clips. Allocation-free.
    /// </summary>
    public int EvaluateSource(double sequenceSeconds, ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers)
    {
        (header.CanvasWidth, header.CanvasHeight, header.PlayheadSeconds) = (SourceWidth, SourceHeight, sequenceSeconds);
        (header.BackgroundR, header.BackgroundG, header.BackgroundB) = (0, 0, 0);
        header.ActiveLayerCount = 0;
        if (PrimarySource < 0 || layers.Length == 0 || !_chain.StreamAt(sequenceSeconds, out var moment))
            return 0;

        ref var d = ref layers[0];
        d = default;
        (d.SourceKind, d.SourceId, d.SourcePathHash, d.MediaSeconds) = ((int)GpuSourceKind.Media, PrimarySource, PrimaryPathHash, Math.Max(moment - _chain.Shift, 0));
        (d.CropLeft, d.CropTop, d.CropRight, d.CropBottom) = (0, 0, 1, 1);
        (d.X, d.Y, d.Width, d.Height) = (0, 0, SourceWidth, SourceHeight);
        (d.AnchorX, d.AnchorY, d.Opacity) = (0.5f, 0.5f, 1);
        (d.MaskResourceId, d.LutResourceId, d.CurveResourceId, d.StillResourceId) = (-1, -1, -1, -1);
        d.ClipKey = _chain.KeyAt(sequenceSeconds);
        d.Flags = GpuLayerDescriptor.FlagVisible;
        header.ActiveLayerCount = 1;
        return 1;
    }

    /// <summary>How many things the plan may draw (layers, before the moment decides which are on).</summary>
    public int EntryCount => _entries.Length;

    // ----- Evaluating -----

    /// <summary>
    /// Writes the scene at a moment of the sequence: the canvas, the playhead, a black background and the layers that are
    /// on there, bottom first (<see cref="GpuLayerDescriptor.FlagVisible"/> set, <see cref="GpuLayerDescriptor.ZDepth"/> their
    /// place, <see cref="GpuLayerDescriptor.ClipKey"/> their clip's), and their count in <see cref="FrameSceneHeader.ActiveLayerCount"/>.
    /// Never more than <see cref="CompositorProtocol.MaxLayers"/> (the topmost beyond are left out, said once in the log).
    /// A layer whose texture is still being made is left out of that frame; nothing waits.
    /// </summary>
    public int Evaluate(double sequenceSeconds, ref FrameSceneHeader header, Span<GpuLayerDescriptor> layers)
    {
        header.CanvasWidth = CanvasWidth;
        header.CanvasHeight = CanvasHeight;
        header.PlayheadSeconds = sequenceSeconds;
        (header.BackgroundR, header.BackgroundG, header.BackgroundB) = (0, 0, 0);
        var count = Walk(sequenceSeconds, layers, []);
        header.ActiveLayerCount = count;
        return count;
    }

    /// <summary>
    /// The media frames the scene needs at a moment (the layers that are on there and read a file): each source once for
    /// each moment of it, at most <paramref name="into"/>'s length. Allocation-free (Instant Seek's hints).
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
            if (entry.Kind == EntryKind.Center && _chain.HeadAt(t, out var head, out _))
                count = Hint(into, count, source, head - _chain.Shift);
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
    /// The next moment after <paramref name="sequenceSeconds"/> at which a layer that reads a file comes on, or the
    /// primary chain goes on to another clip (where to decode ahead: next-cut hints); infinity when none does.
    /// </summary>
    public double NextCutAfter(double sequenceSeconds)
    {
        var next = _chain.NextStartAfter(sequenceSeconds);
        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (entry.Kind is EntryKind.File or EntryKind.Twin or EntryKind.Piece && entry.From > sequenceSeconds && entry.From < next)
                next = entry.From;
        }

        return next;
    }

    // ----- The cut boundary index (Pass 5.0-Release) -----

    /// <summary>
    /// Every place the sequence starts reading a file at a moment of its own, by its start (then source and moment): each
    /// clip of the primary chain (the stream the main video, its pieces and its blurred background are cut from), each
    /// crossfade's head (the later clip, read from where it starts under the earlier one), and each video read from its own
    /// file (its blurred twin reads it at the same moments: one entry). Made once with the plan; what the worker decodes
    /// ahead of the playhead (GpuHintWriter: every cut within the lookahead horizon, not only the next).
    /// </summary>
    public ReadOnlySpan<GpuCutEntry> Cuts => _cuts;

    /// <summary>The first entry of <see cref="Cuts"/> that starts after a moment (their count when none does). Allocation-free.</summary>
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

    private static GpuCutEntry[] CutsOf(Entry[] entries, ChainMap chain)
    {
        var cuts = new List<GpuCutEntry>(chain.Clips.Length + chain.Crossfades.Length + 8);

        // The chain: read by whichever of its layers there are (the one piece, the riders and cut-outs, the background).
        var (source, hash) = (-1, 0L);
        foreach (var entry in entries)
        {
            if (entry.Kind is EntryKind.Center or EntryKind.Piece or EntryKind.Background && entry.SourceId >= 0)
            {
                (source, hash) = (entry.SourceId, entry.PathHash);
                break;
            }
        }

        if (source >= 0)
        {
            foreach (var clip in chain.Clips)
            {
                if (clip.End > clip.Start)
                    cuts.Add(new GpuCutEntry(clip.Start, clip.End, source, hash, Math.Max(clip.Offset - chain.Shift, 0), Math.Max(clip.Offset + (clip.End - clip.Start) - chain.Shift, 0), 1));
            }

            foreach (var fade in chain.Crossfades)
            {
                if (fade.To > fade.From)
                    cuts.Add(new GpuCutEntry(fade.From, fade.To, source, hash, Math.Max(fade.Source - chain.Shift, 0), Math.Max(fade.Source + (fade.To - fade.From) - chain.Shift, 0), 1));
            }
        }

        // A video read from its own file, from its first moment on the sequence (MediaOf's moment there).
        foreach (var entry in entries)
        {
            if (entry.Kind is not (EntryKind.File or EntryKind.Twin) || entry.SourceId < 0 || !double.IsFinite(entry.From) || entry.Speed <= 0)
                continue;
            var media = entry.MediaOffset + Math.Max((entry.From - entry.MediaStart) * entry.Speed, 0);
            var end = double.IsFinite(entry.To) ? entry.MediaOffset + Math.Max((entry.To - entry.MediaStart) * entry.Speed, 0) : double.PositiveInfinity;
            if (entry.MediaDuration > 0.05)
            {
                // A repeating video's next round starts from its first frame again (read on from there: no entry of its own).
                if (entry.Loops)
                    (media, end) = (Repeat(media, entry.MediaDuration), entry.MediaDuration);
                else
                    end = Math.Min(end, entry.MediaDuration);
            }
            else if (entry.Loops)
            {
                end = double.PositiveInfinity;
            }

            if (end > media)
                cuts.Add(new GpuCutEntry(entry.From, entry.To, entry.SourceId, entry.PathHash, media, end, entry.Speed));
        }

        cuts.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.SourceId != b.SourceId ? a.SourceId.CompareTo(b.SourceId) : a.Media.CompareTo(b.Media));
        var kept = new List<GpuCutEntry>(cuts.Count);
        foreach (var cut in cuts)
        {
            if (kept.Count > 0 && kept[^1] is var last && Math.Abs(last.Start - cut.Start) < 1e-6 && last.SourceId == cut.SourceId && Math.Abs(last.Media - cut.Media) < 1e-6)
            {
                // One file read at one moment from one moment on (a video and its twin): the farther reach of the two.
                if (cut.MediaEnd > last.MediaEnd)
                    kept[^1] = last with { End = Math.Max(last.End, cut.End), MediaEnd = cut.MediaEnd };
                continue;
            }

            kept.Add(cut);
        }

        return [.. kept];
    }

    /// <summary>What each layer <see cref="Evaluate"/> writes at a moment is, in the same order (the UI thread: the 3D view's labels and picking).</summary>
    public int LayerInfoAt(double sequenceSeconds, Span<GpuLayerInfo> into)
    {
        var scratch = _scratch ??= new GpuLayerDescriptor[CompositorProtocol.MaxLayers];
        var written = Walk(sequenceSeconds, scratch, into);
        return Math.Min(written, into.Length);
    }

    [ThreadStatic] private static GpuLayerDescriptor[]? _scratch;

    /// <summary>
    /// The UI thread's part as the playhead moves (a couple of milliseconds a call at most): hands over the textures made
    /// since on the background lane, draws the captions within ten seconds of the moment that are not drawn yet, and hands
    /// over the subtitle track's pictures within ten seconds of it that are not there yet.
    /// </summary>
    public void PrepareAround(double sequenceSeconds)
    {
        if (_resources is null)
            return;
        _resources.Pump();

        // (Both are at the top of the stack: the walk stops at the first entry that is neither.)
        for (var i = _entries.Length - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Kind == EntryKind.Caption)
            {
                if (entry.Captions?.PrepareAround(sequenceSeconds) == true)
                    _resources.Changed();
            }
            else if (entry.Kind == EntryKind.PictureSubtitles)
            {
                if (entry.Pictures?.Prepare(_resources, sequenceSeconds) == true)
                    _resources.Changed();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Every entry that is on at the moment, written bottom first; their descriptions too when asked.</summary>
    private int Walk(double t, Span<GpuLayerDescriptor> layers, Span<GpuLayerInfo> infos)
    {
        var room = Math.Min(layers.Length, CompositorProtocol.MaxLayers);
        var n = 0;
        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (!IsOn(entry, t))
                continue;

            var wrote = Write(entry, t, layers, n, room);
            if (wrote < 0)
            {
                Warn(1, "More than 64 layers are on at once (CompositorProtocol.MaxLayers): the topmost are not drawn by the GPU engine.");
                break;
            }

            for (var k = n; k < n + wrote; k++)
            {
                layers[k].ZDepth = k;
                if (k < infos.Length)
                    infos[k] = Describe(entry, t, k - n);
            }

            n += wrote;
        }

        return n;
    }

    /// <summary>Writes a line in the log the first time a plan meets something (one bit per kind of thing).</summary>
    private void Warn(int bit, string message)
    {
        var before = Interlocked.Or(ref _warned, bit);
        if ((before & bit) == 0)
            AppLog.Write(message);
    }

    // ----- What is on, where it reads from -----

    /// <summary>Whether an entry is on at a moment: its time (FFmpeg's between(), both ends in), and what it needs there.</summary>
    private bool IsOn(Entry e, double t)
    {
        if (t < e.From || t > e.To || (e.ToExclusive && t >= e.To))
            return false;

        switch (e.Kind)
        {
            case EntryKind.Center:
                return ChainMap.IndexAt(_chain.Center, t) >= 0;
            case EntryKind.Piece:
                return ChainMap.IndexAt(_chain.Clips, t) >= 0;
            case EntryKind.Background:
                return ChainMap.IndexAt(_chain.Blur, t) >= 0 && !HidesBackground(t);
            case EntryKind.File or EntryKind.Twin:
                if (e.Loops)
                    return true;
                var media = e.MediaOffset + (t - e.MediaStart) * e.Speed;
                return !(e.MediaDuration > 0.05 && media >= e.MediaDuration);
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
    /// The blurred background is not drawn where the main video hides every pixel of it (BuildFrameEngine's hidesBackground,
    /// moment by moment): the bottom layer, solid, square, unturned and still, covering the frame, and not fading.
    /// </summary>
    private bool HidesBackground(double t)
    {
        if (_chain.CoveringCenter is not { } center)
            return false;
        var at = ChainMap.IndexAt(_chain.Center, t);
        if (at < 0)
            return false;

        Span<Fade> fades = stackalloc Fade[2];
        var count = CenterFades(_chain.Center[at], fades, dissolve: false);
        for (var k = 0; k < count; k++)
        {
            if (fades[k].Holds(t))
                return false;
        }

        return t >= center.From && t <= center.To;
    }

    /// <summary>The source and moment an entry reads at a moment, for those that read a file.</summary>
    private bool MediaOf(Entry e, double t, out int source, out double media)
    {
        (source, media) = (e.SourceId, 0);
        switch (e.Kind)
        {
            case EntryKind.Center or EntryKind.Piece or EntryKind.Background:
                if (!_chain.StreamAt(t, out var moment))
                    return false;
                media = moment - _chain.Shift;
                return source >= 0;
            case EntryKind.File or EntryKind.Twin:
                var into = (t - e.MediaStart) * e.Speed;
                media = e.Loops && e.MediaDuration > 0.05 ? Repeat(e.MediaOffset + into, e.MediaDuration) : e.MediaOffset + Math.Max(into, 0);
                return source >= 0;
            default:
                return false;
        }
    }

    // A repeating video's moment: its file played again and again from its first frame (movie=...:loop=0 on an even clock).
    private static double Repeat(double moment, double length)
    {
        var within = moment % length;
        return within < 0 ? within + length : within;
    }

    // ----- Writing -----

    /// <summary>Writes an entry's layers (a crossfade's head is a second one) at <paramref name="n"/>; how many, or -1 for no room.</summary>
    private int Write(Entry e, double t, Span<GpuLayerDescriptor> layers, int n, int room)
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
            case EntryKind.Center:
                return WriteCenter(e, t, layers, n, room);

            case EntryKind.Piece or EntryKind.File:
                if (!MediaOf(e, t, out var source, out var media) || !UsableMask(e, ref d))
                    return 0;
                Media(ref d, e, source, media);
                WritePicture(e, t, ref d);
                return d.Opacity > 0.0005f ? 1 : 0;

            case EntryKind.Still or EntryKind.Text:
                if (_resources is null || _resources.StateOf(e.Still) != GpuResourceState.Ready || !UsableMask(e, ref d))
                    return 0;
                (d.SourceKind, d.StillResourceId) = ((int)GpuSourceKind.Still, e.Still.Id);
                WritePicture(e, t, ref d);
                return d.Opacity > 0.0005f ? 1 : 0;

            case EntryKind.Adjustment:
                (d.X, d.Y, d.Width, d.Height) = (e.X, e.Y, e.W, e.H);
                d.Flags |= GpuLayerDescriptor.FlagAdjustment;
                AddLook(e, t - e.KeyStart, ref d);
                AddFades(e.Fades, t, ref d);
                return d.EffectCount > 0 ? 1 : 0;

            case EntryKind.Background or EntryKind.Twin:
            {
                if (!MediaOf(e, t, out var from, out var moment))
                    return 0;
                Media(ref d, e, from, moment);
                (d.X, d.Y, d.Width, d.Height) = (0, 0, CanvasWidth, CanvasHeight);

                // The part of the picture it covers the frame with: its zoom's middle (5.0-Release, GpuScenePlan.Create's Blur).
                (d.CropLeft, d.CropTop, d.CropRight, d.CropBottom) = (e.CropLeft, e.CropTop, e.CropRight, e.CropBottom);
                d.Flags |= FlagBlurFillOnly;
                (d.BlurFillRadius, d.BlurFillPasses, d.BlurFillDim) = (e.BlurRadius, e.BlurPasses, e.BlurDim);

                // The background's own Look cards, after its blur (keys and ranges in seconds of the sequence).
                for (var k = 0; k < e.Look.Length; k++)
                    AddCard(e, e.Look[k], t, null, ref d);
                if (e.Kind == EntryKind.Twin)
                {
                    d.Opacity = OpacityAt(e, t);
                    AddFades(e.Fades, t, ref d);
                }
                else if (ChainMap.IndexAt(_chain.Center, t) is >= 0 and var at)
                {
                    // Made of the main video, it comes on and goes with it: from black, by a dissolve whatever its style.
                    Span<Fade> fades = stackalloc Fade[2];
                    AddFades(fades[..CenterFades(_chain.Center[at], fades, dissolve: true)], t, ref d);
                }

                return d.Opacity > 0.0005f ? 1 : 0;
            }

            case EntryKind.Whole:
                (d.X, d.Y, d.Width, d.Height) = (0, 0, CanvasWidth, CanvasHeight);
                d.Flags |= GpuLayerDescriptor.FlagAdjustment;
                foreach (var packet in e.Packets)
                {
                    if (packet.Slot == GpuShaderSlot.Lut3D)
                    {
                        if (_resources?.StateOf(e.Lut) != GpuResourceState.Ready)
                            continue;
                        d.LutResourceId = e.Lut.Id;
                    }

                    Add(ref d, packet);
                }

                // The Sequence Output's own effects: their keys and ranges are in seconds of the sequence.
                for (var k = 0; k < e.Look.Length; k++)
                    AddCard(e, e.Look[k], t, null, ref d);
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

    private static void Media(ref GpuLayerDescriptor d, Entry e, int source, double media)
    {
        d.SourceKind = (int)GpuSourceKind.Media;
        (d.SourceId, d.MediaSeconds, d.SourcePathHash) = (source, media, e.PathHash);
    }

    /// <summary>The main video in one piece, and over it the later clip's head while two of its clips crossfade (AppendMainCrossfades).</summary>
    private int WriteCenter(Entry e, double t, Span<GpuLayerDescriptor> layers, int n, int room)
    {
        ref var d = ref layers[n];
        var at = ChainMap.IndexAt(_chain.Center, t);
        if (at < 0 || !MediaOf(e, t, out var source, out var media) || !UsableMask(e, ref d))
            return 0;

        d.ClipKey = _chain.KeyAt(t);
        Media(ref d, e, source, media);
        Place(e, t, ref d);
        AddEffects(e, t - e.KeyStart, ref d);
        Span<Fade> fades = stackalloc Fade[2];
        AddFades(fades[..CenterFades(_chain.Center[at], fades, dissolve: false)], t, ref d);
        if (d.Opacity <= 0.0005f)
            return 0;

        // The later clip's head, read again from the file and coming up over the earlier one by an opacity ramp.
        if (!_chain.HeadAt(t, out var head, out var ramp))
            return 1;
        if (n + 1 >= room)
            return 1;

        ref var over = ref layers[n + 1];
        over = d;
        over.MediaSeconds = head - _chain.Shift;
        over.Opacity = d.Opacity * (float)ramp;
        over.ClipKey = _chain.HeadKeyAt(t);
        Without(ref over, GpuShaderSlot.DropShadow);
        return 2;
    }

    /// <summary>A layer's packets but those of a slot: a copy laid over its original (a crossfade's head, a sung caption) casts no second shadow.</summary>
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

    /// <summary>A caption: its state's rows of its still in the caption box; a karaoke line's sung state cut at its sweep over the rest.</summary>
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

        // Karaoke Sweep: the sung state from the line's left to the sweep, which crosses each word (and its space) while it is said.
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

        // (The narrower box would round, mask and shadow the sweep's edge: the base under it has them.)
        Without(ref sung, GpuShaderSlot.DropShadow);
        Without(ref sung, GpuShaderSlot.CornerFeather);
        Without(ref sung, GpuShaderSlot.LayerMask);
        sung.MaskResourceId = -1;
        return 2;
    }

    /// <summary>Everything of a picture but its source: its place at the moment, its blend, its effects in their zones, its fades.</summary>
    private void WritePicture(Entry e, double t, ref GpuLayerDescriptor d)
    {
        Place(e, t, ref d);
        d.BlendMode = e.BlendMode;
        AddEffects(e, t - e.KeyStart, ref d);
        AddFades(e.Fades, t, ref d);
    }

    /// <summary>
    /// A layer's place at a moment, as the motion compiler's expressions put it (GetMotion, BuildFrameEngine): its corner by
    /// its X and Y keys; its width by its Scale keys, its height keeping the box's shape, and, with its size animated and its
    /// place not, its anchor point held still; its turn by its Rotation keys (or as set) about the anchor; its opacity by its
    /// Opacity keys (or as set).
    /// </summary>
    private void Place(Entry e, double t, ref GpuLayerDescriptor d)
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
        (d.X, d.Y, d.Width, d.Height) = ((float)x, (float)y, (float)width, (float)height);
        (d.AnchorX, d.AnchorY) = (e.AnchorX, e.AnchorY);
        d.Rotation = !e.Turns ? 0 : e.KeysRotation is { } turn ? (float)EasingCurves.ValueAt(turn, at) : e.Rotation;
        d.Opacity = OpacityAt(e, t);
    }

    private static float OpacityAt(Entry e, double t) =>
        e.KeysOpacity is { } keys ? (float)Math.Clamp(EasingCurves.ValueAt(keys, t - e.KeyStart) / 100, 0, 1) : e.Opacity;

    /// <summary>
    /// Whether a masked entry can be drawn now, its mask set on the layer: the bake for its size, or one of an older size
    /// while that is made. Not while no bake of it is there yet (a HUD piece shown unmasked would flash its whole rectangle);
    /// a mask that could not be made is left off, as a missing mask file is.
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

    /// <summary>A layer's effects as the graph applies them: the Look cards (zone 2) in their order, then the key and matte (zone 3), then the shadow.</summary>
    private void AddEffects(Entry e, double clipTime, ref GpuLayerDescriptor d)
    {
        AddLook(e, clipTime, ref d);
        for (var k = 0; k < e.Matte.Length; k++)
        {
            var step = e.Matte[k];
            if (step.Card is { } card)
            {
                AddCard(e, card, clipTime, e.Layer, ref d);
                continue;
            }

            // The mask and the edges, where the first of their cards is (AppendKeyAndMatte's PlaceMasks).
            if (d.MaskResourceId >= 0)
                Add(ref d, new GpuEffectPacket(GpuShaderSlot.LayerMask, 0, 0, 0, 0, 0, 0, 0, 0));
            if (e.Edges.Slot != GpuShaderSlot.None)
                Add(ref d, e.Edges);
        }

        if (e.Shadow is { } shadow)
            AddCard(e, shadow, clipTime, e.Layer, ref d);
    }

    private void AddLook(Entry e, double clipTime, ref GpuLayerDescriptor d)
    {
        for (var k = 0; k < e.Look.Length; k++)
            AddCard(e, e.Look[k], clipTime, e.Layer, ref d);
    }

    /// <summary>A card's packets at a moment of its clip; one that needs a texture is left out until the texture is there.</summary>
    private void AddCard(Entry e, EffectInstance card, double clipTime, Layer? layer, ref GpuLayerDescriptor d)
    {
        if (card.Definition is not { } definition)
            return;

        var count = definition.GpuPacketCount;
        for (var k = 0; k < count; k++)
        {
            var packet = card.EvaluateGpuPacket(clipTime, layer, k);
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

    /// <summary>A fade of a picture on the sequence: from Start, Length seconds, coming on (In) or going, in a style.</summary>
    private readonly record struct Fade(double Start, double Length, bool In, TransitionStyle Style)
    {
        /// <summary>While it is at work: from its start (inclusive) to its end (not), as PictureFade.Gate.</summary>
        public bool Holds(double t) => t >= Start && t < Start + Length;
    }

    /// <summary>The packets of the fades at work at a moment (TransitionStyles.GpuPacket: how much is shown along the style's curve, which way, how far through).</summary>
    private void AddFades(ReadOnlySpan<Fade> fades, double t, ref GpuLayerDescriptor d)
    {
        for (var k = 0; k < fades.Length; k++)
        {
            var fade = fades[k];
            if (fade.Holds(t))
                Add(ref d, TransitionStyles.GpuPacket(fade.Style, (t - fade.Start) / fade.Length, fade.In));
        }
    }

    /// <summary>A clip of the main video's fades (PictureFades of the main layer: its fade in from its start, its fade out to its end); as dissolves for the blurred background.</summary>
    private static int CenterFades(in MainClip clip, Span<Fade> into, bool dissolve)
    {
        var count = 0;
        var length = clip.End - clip.Start;
        var fadeIn = Math.Min(clip.FadeIn, length);
        if (fadeIn > 0.01)
            into[count++] = new Fade(clip.Start, fadeIn, true, dissolve ? TransitionStyle.Dissolve : clip.FadeInStyle);
        var fadeOut = Math.Min(clip.FadeOut, Math.Max(length - clip.FadeIn, 0));
        if (fadeOut > 0.01)
            into[count++] = new Fade(clip.End - fadeOut, fadeOut, false, dissolve ? TransitionStyle.Dissolve : clip.FadeOutStyle);
        return count;
    }

    /// <summary>How black the whole picture's fade makes it at a moment (BuildWholeVideoFilters: a second in from black, a second out to it).</summary>
    private double FadeLevel(Entry e, double t)
    {
        var level = 0.0;
        if (e.FadesIn && t < WholeFadeSeconds)
            level = 1 - Math.Clamp(t / WholeFadeSeconds, 0, 1);
        if (e.FadesOut && SequenceSeconds > WholeFadeSeconds && t > SequenceSeconds - WholeFadeSeconds)
            level = Math.Max(level, Math.Clamp((t - (SequenceSeconds - WholeFadeSeconds)) / WholeFadeSeconds, 0, 1));
        return level;
    }

    /// <summary>The whole picture's fade in and out, in seconds (FilterGraphBuilder's FadeSeconds).</summary>
    private const double WholeFadeSeconds = 1;

    // ----- Descriptions -----

    private GpuLayerInfo Describe(Entry e, double t, int part)
    {
        if (e.Kind is EntryKind.Center or EntryKind.Background)
        {
            var clip = part == 1 ? _chain.HeadClipAt(t) : _chain.ClipAt(t);
            var key = clip is null ? e.ClipKey : GpuHash.Of(clip.Id);
            return new GpuLayerInfo(key, clip?.Id.ToString("D", CultureInfo.InvariantCulture) ?? e.ClipId, e.TrackIndex, e.Name, e.HasMask);
        }

        return new GpuLayerInfo(e.ClipKey, e.ClipId, e.TrackIndex, e.Name, e.HasMask);
    }
}
