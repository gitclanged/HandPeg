using System.Diagnostics;
using HandPegApp.Models;

namespace HandPegApp.Services.Gpu;

// ----- Picture subtitle track -----
// Draws a PGS/VobSub track (PictureSubtitles.cs) or an export's burned subtitles (BurnedSubtitles): each cue's bitmap over the clip
// it belongs to, beneath captions, over [start, end). Cue bitmaps are uploaded as stills while the playhead is near (PrepareAround,
// like captions) and evicted on last use.
public sealed partial class GpuScenePlan
{
    private static readonly long PictureSubtitlesKey = default(GpuHash).Add("picture-subtitles").Value;

    /// <summary>
    /// Builds the picture-subtitle entry. Each cue is placed through its clip: the clip it names (burned subtitles), else the bottom
    /// <paramref name="streamSource"/> video visible at the cue's middle, else at its start. The stream canvas is fill-fitted and centred
    /// on that clip's source picture (anamorphic canvases map to source pixels), then follows the clip's crop and box. Cues are not
    /// clipped by the crop or box, so a line in a cropped-off letterbox stays readable. Cues without a clip are fitted to the frame.
    /// </summary>
    private static Entry PictureSubtitlesEntry(PictureSubtitleTrack track, IReadOnlyList<Entry> entries, string streamSource, int width, int height)
    {
        // File entries by clip key, and the stream source's entries bottom first.
        var source = streamSource.Trim().Trim('"');
        var (byKey, ofSource) = (new Dictionary<long, Entry>(), new List<Entry>());
        foreach (var entry in entries)
        {
            if (entry.Kind != EntryKind.File || entry.Layer is not { } layer)
                continue;
            if (entry.ClipKey != 0)
                byKey.TryAdd(entry.ClipKey, entry);
            if (source.Length > 0 && string.Equals(layer.ImagePath.Trim().Trim('"'), source, StringComparison.OrdinalIgnoreCase))
                ofSource.Add(entry);
        }

        var cues = track.CueArray;
        var places = new CuePlace[cues.Length];
        for (var i = 0; i < cues.Length; i++)
        {
            var cue = cues[i];
            var video = (cue.ClipId != Guid.Empty ? byKey.GetValueOrDefault(GpuHash.Of(cue.ClipId)) : null)
                        ?? OnAt(ofSource, (cue.From + cue.To) / 2) ?? OnAt(ofSource, cue.From);
            places[i] = video is { Layer: { } layer } ? PlaceOn(track, (video.X, video.Y, video.W, video.H), layer, width, height) : PlaceOn(track, width, height);
        }

        var lane = new PictureLane(cues, places);
        return new Entry { Kind = EntryKind.PictureSubtitles, Pictures = lane, Name = "Picture Subtitles", ClipKey = PictureSubtitlesKey };
    }

    /// <summary>The bottom-most video entry visible at <paramref name="t"/>; null when none.</summary>
    private static Entry? OnAt(List<Entry> videos, double t)
    {
        foreach (var video in videos)
        {
            if (t >= video.From && (video.ToExclusive ? t < video.To : t <= video.To))
                return video;
        }

        return null;
    }

    /// <summary>
    /// Stream canvas placement on a clip's picture (source size, else frame size; crop region filling the box), as scale and shift
    /// per axis.
    /// </summary>
    private static CuePlace PlaceOn(PictureSubtitleTrack track, (float X, float Y, float Width, float Height) box, Layer layer, int width, int height)
    {
        var (sourceWidth, sourceHeight) = layer.ImageWidth > 0 && layer.ImageHeight > 0 ? (layer.ImageWidth, layer.ImageHeight) : (width, height);
        var (left, top, right, bottom) = layer.IsCropped ? layer.CropFractions : default;
        (left, top, right, bottom) = (Math.Clamp(left, 0, 1), Math.Clamp(top, 0, 1), Math.Clamp(right, 0, 1), Math.Clamp(bottom, 0, 1));
        if (left + right >= 0.999 || top + bottom >= 0.999)
            (left, top, right, bottom) = (0, 0, 0, 0);
        return CanvasOn(track, sourceWidth, sourceHeight, (box.X, box.Y, box.Width, box.Height), (left, top, right, bottom));
    }

    /// <summary>Stream canvas fill-fitted to the whole frame, centred.</summary>
    private static CuePlace PlaceOn(PictureSubtitleTrack track, int width, int height) => CanvasOn(track, width, height, (0, 0, width, height), default);

    /// <summary>Stream canvas on a source picture whose crop region (fractions) fills <paramref name="box"/> on the frame.</summary>
    private static CuePlace CanvasOn(PictureSubtitleTrack track, int sourceWidth, int sourceHeight, (double X, double Y, double Width, double Height) box,
        (double Left, double Top, double Right, double Bottom) crop)
    {
        var (left, top, right, bottom) = crop;

        // Canvas in source pixels (fill-fit, centred), then source pixels onto the frame.
        var fill = Math.Max((double)sourceWidth / track.CanvasWidth, (double)sourceHeight / track.CanvasHeight);
        var (canvasLeft, canvasTop) = ((sourceWidth - track.CanvasWidth * fill) / 2, (sourceHeight - track.CanvasHeight * fill) / 2);
        var (perX, perY) = (box.Width / (sourceWidth * (1 - left - right)), box.Height / (sourceHeight * (1 - top - bottom)));
        var (originX, originY) = (box.X - left * sourceWidth * perX, box.Y - top * sourceHeight * perY);
        return new CuePlace((float)(fill * perX), (float)(originX + canvasLeft * perX), (float)(fill * perY), (float)(originY + canvasTop * perY));
    }

    /// <summary>Maps cue canvas pixels to frame pixels: scale and shift per axis.</summary>
    private readonly record struct CuePlace(float ScaleX, float ShiftX, float ScaleY, float ShiftY);

    /// <summary>Writes the cue visible at <paramref name="t"/> as a still layer at its frame rect; 0 when none is ready.</summary>
    private int WritePictureSubtitle(Entry e, double t, ref GpuLayerDescriptor d)
    {
        if (_resources is null || e.Pictures is not { } lane || !lane.At(t, _resources, out var cue, out var still))
            return 0;

        (d.SourceKind, d.StillResourceId) = ((int)GpuSourceKind.Still, still.Id);
        (d.X, d.Y, d.Width, d.Height) = lane.Place(cue);
        return 1;
    }

    /// <summary>
    /// A plan's picture-subtitle lane: cues sorted by start, their canvas placements, and each cue's uploaded still. Stills are written
    /// on the UI thread (<see cref="Prepare"/>) and read lock-free by the evaluating thread; everything else is immutable.
    /// </summary>
    private sealed class PictureLane(PictureSubtitleCue[] cues, CuePlace[] places)
    {
        // Packed still per cue: (id + 1) in the high 32 bits, serial in the low; 0 when none.
        private readonly long[] _stills = new long[cues.Length];

        /// <summary>Index of the cue visible at <paramref name="t"/> and its still; true only when the still is ready (allocation-free).</summary>
        public bool At(double t, GpuResourceBroker resources, out int cue, out GpuResourceRef still)
        {
            (cue, still) = (UpAt(t), GpuResourceRef.None);
            if (cue < 0)
                return false;

            still = StillOf(cue);
            return !still.IsNone && resources.StateOf(still) == GpuResourceState.Ready;
        }

        /// <summary>Cue bitmap rect on the frame: its canvas position mapped through the cue's placement.</summary>
        public (float X, float Y, float Width, float Height) Place(int cue)
        {
            var (picture, place) = (cues[cue].Picture, places[cue]);
            return (place.ShiftX + place.ScaleX * picture.X, place.ShiftY + place.ScaleY * picture.Y, place.ScaleX * picture.Width, place.ScaleY * picture.Height);
        }

        /// <summary>
        /// Uploads cue bitmaps within <see cref="TextRasterizer.PrepareWindowSeconds"/> of <paramref name="t"/> that are not yet in the
        /// resource table (nearest first, ~<see cref="TextRasterizer.PrepareBudgetMs"/> per call, the rest next call) and touches resident
        /// ones so they are not evicted. Returns true when any cue's still changed.
        /// </summary>
        public bool Prepare(GpuResourceBroker resources, double t)
        {
            const double Window = TextRasterizer.PrepareWindowSeconds;
            if (cues.Length == 0)
                return false;

            var changed = false;
            var clock = Stopwatch.StartNew();
            var at = Math.Max(LastBegunBy(t), 0);
            for (var step = 0; step < 2 * cues.Length; step++)
            {
                // Spiral outward from the current cue: at, at + 1, at - 1, at + 2, ...
                var i = at + (step % 2 == 0 ? step / 2 : -(step / 2 + 1));
                if (i < 0 || i >= cues.Length || cues[i].From > t + Window || cues[i].To < t - Window)
                    continue;

                // Content-hash lookup touches a resident still; otherwise decode and upload it.
                var picture = cues[i].Picture;
                var made = !resources.Has(picture.Hash);
                var still = resources.Still(picture.Hash, () => (picture.Pixels(), picture.Width, picture.Height), pinned: false);
                var packed = still.IsNone ? 0 : ((long)(still.Id + 1) << 32) | (uint)still.Serial;
                if (packed != Volatile.Read(ref _stills[i]))
                {
                    Volatile.Write(ref _stills[i], packed);
                    changed = true;
                }

                if (made && clock.Elapsed.TotalMilliseconds > TextRasterizer.PrepareBudgetMs)
                    break;
            }

            return changed;
        }

        private GpuResourceRef StillOf(int cue)
        {
            var packed = Volatile.Read(ref _stills[cue]);
            return packed == 0 ? GpuResourceRef.None : new GpuResourceRef((int)(packed >> 32) - 1, (int)packed);
        }

        /// <summary>Cue visible at <paramref name="t"/>: the latest started and not yet ended (checks up to 8 back for overlapping retimed cues); -1 when none.</summary>
        private int UpAt(double t)
        {
            var last = LastBegunBy(t);
            for (var i = last; i >= 0 && i > last - 8; i--)
            {
                if (t < cues[i].To)
                    return i;
            }

            return -1;
        }

        /// <summary>Last cue starting at or before <paramref name="t"/> (binary search); -1 when none.</summary>
        private int LastBegunBy(double t)
        {
            int low = 0, high = cues.Length - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                if (cues[middle].From <= t)
                    (found, low) = (middle, middle + 1);
                else
                    high = middle - 1;
            }

            return found;
        }
    }
}
