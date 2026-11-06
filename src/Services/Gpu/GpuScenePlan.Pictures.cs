using System.Diagnostics;
using HandPegApp.Models;

namespace HandPegApp.Services.Gpu;

// ----- The subtitle track's pictures (5.0-Verify) -----
// A PGS or VobSub track (PictureSubtitles.cs), or an export's burned subtitles (BurnedSubtitles), as libmpv drew it: each
// cue's picture over the picture of the clip it is shown in, under the captions, from its cue's start (in) to its end (not).
// A cue's picture is handed to the worker as a still while the playhead is near it (PrepareAround, as captions are drawn),
// and let go by its last use alone.
public sealed partial class GpuScenePlan
{
    private static readonly long PictureSubtitlesKey = default(GpuHash).Add("picture-subtitles").Value;

    /// <summary>
    /// The entry of the subtitle track's pictures. Each cue is laid through the clip it is shown in (5.2-AuditB: any clip's
    /// video, where it was the main video's box): the clip its cue names (burned subtitles: the clip of the file whose stream
    /// they are, that its piece of the cue was placed by), else the bottom video of <paramref name="streamSource"/> (the file
    /// whose subtitle streams the settings name) on in the cue's middle, else at its start. The stream's canvas is laid on
    /// that clip's own picture at the scale that fills it, centred (a film cropped or scaled from the frame its subtitles
    /// were drawn for is the middle of that frame; a DVD's anamorphic canvas is the picture's own pixels), then placed as the
    /// clip is: the part its crop keeps filling its box. A picture is not cut by the crop or the box: a line under a
    /// letterbox that was cropped off is still there to read, below the picture, as a player shows it. A cue with no such
    /// clip is laid on the frame the same way.
    /// </summary>
    private static Entry PictureSubtitlesEntry(PictureSubtitleTrack track, IReadOnlyList<Entry> entries, string streamSource, int width, int height)
    {
        // The videos, by their clips' keys, and those of the stream's file, bottom first.
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

    /// <summary>The first (bottom) of some video entries that is on at a moment (its clip's stretch); null for none.</summary>
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
    /// A stream's canvas laid on a clip's picture (its own size, the frame's while that is not known; its crop's part filling
    /// its box), as a scale and a shift each way.
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

    /// <summary>A stream's canvas laid on the frame, filling it about its middle.</summary>
    private static CuePlace PlaceOn(PictureSubtitleTrack track, int width, int height) => CanvasOn(track, width, height, (0, 0, width, height), default);

    /// <summary>A stream's canvas on a picture of a size whose crop's part fills a box on the frame (the box, the crop's fractions).</summary>
    private static CuePlace CanvasOn(PictureSubtitleTrack track, int sourceWidth, int sourceHeight, (double X, double Y, double Width, double Height) box,
        (double Left, double Top, double Right, double Bottom) crop)
    {
        var (left, top, right, bottom) = crop;

        // The canvas on the source's picture, in its pixels; then the source's pixels on the frame.
        var fill = Math.Max((double)sourceWidth / track.CanvasWidth, (double)sourceHeight / track.CanvasHeight);
        var (canvasLeft, canvasTop) = ((sourceWidth - track.CanvasWidth * fill) / 2, (sourceHeight - track.CanvasHeight * fill) / 2);
        var (perX, perY) = (box.Width / (sourceWidth * (1 - left - right)), box.Height / (sourceHeight * (1 - top - bottom)));
        var (originX, originY) = (box.X - left * sourceWidth * perX, box.Y - top * sourceHeight * perY);
        return new CuePlace((float)(fill * perX), (float)(originX + canvasLeft * perX), (float)(fill * perY), (float)(originY + canvasTop * perY));
    }

    /// <summary>Where a cue's canvas's pixels fall on the frame: a scale and a shift each way.</summary>
    private readonly record struct CuePlace(float ScaleX, float ShiftX, float ScaleY, float ShiftY);

    /// <summary>The cue's picture up at a moment, as a still where its canvas's pixels fall on the frame.</summary>
    private int WritePictureSubtitle(Entry e, double t, ref GpuLayerDescriptor d)
    {
        if (_resources is null || e.Pictures is not { } lane || !lane.At(t, _resources, out var cue, out var still))
            return 0;

        (d.SourceKind, d.StillResourceId) = ((int)GpuSourceKind.Still, still.Id);
        (d.X, d.Y, d.Width, d.Height) = lane.Place(cue);
        return 1;
    }

    /// <summary>
    /// The subtitle track's pictures in a plan: its cues by their start, where each cue's canvas's pixels fall on the frame
    /// (a scale and a shift each way, through the clip it is shown in), and the still each cue's picture was handed over as.
    /// The stills are written on the UI thread (<see cref="Prepare"/>) and read lock-free by the thread that evaluates;
    /// nothing else changes after it is made.
    /// </summary>
    private sealed class PictureLane(PictureSubtitleCue[] cues, CuePlace[] places)
    {
        // Each cue's still: its id + 1 in the high half and its serial in the low; 0 while it has none.
        private readonly long[] _stills = new long[cues.Length];

        /// <summary>The cue up at a moment (its index) and its still, when that is there to be drawn with (allocation-free).</summary>
        public bool At(double t, GpuResourceBroker resources, out int cue, out GpuResourceRef still)
        {
            (cue, still) = (UpAt(t), GpuResourceRef.None);
            if (cue < 0)
                return false;

            still = StillOf(cue);
            return !still.IsNone && resources.StateOf(still) == GpuResourceState.Ready;
        }

        /// <summary>A cue's picture's box on the frame: its place on the canvas, scaled and moved as the cue's canvas is.</summary>
        public (float X, float Y, float Width, float Height) Place(int cue)
        {
            var (picture, place) = (cues[cue].Picture, places[cue]);
            return (place.ShiftX + place.ScaleX * picture.X, place.ShiftY + place.ScaleY * picture.Y, place.ScaleX * picture.Width, place.ScaleY * picture.Height);
        }

        /// <summary>
        /// Hands over the pictures of the cues within <see cref="TextRasterizer.PrepareWindowSeconds"/> of a moment that are
        /// not in the resource table (nearest first, for about <see cref="TextRasterizer.PrepareBudgetMs"/> a call; the rest
        /// on the next call), and keeps those that are from being let go. Returns whether a cue's still changed.
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
                // Outwards from the moment: at, at + 1, at - 1, at + 2...
                var i = at + (step % 2 == 0 ? step / 2 : -(step / 2 + 1));
                if (i < 0 || i >= cues.Length || cues[i].From > t + Window || cues[i].To < t - Window)
                    continue;

                // Found again by its content when it is in the table (and touched there); unpacked and handed over when not.
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

        /// <summary>The cue up at a moment: the latest begun that has not ended (a few earlier are looked at too, for cues retimed over one another); -1 for none.</summary>
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

        /// <summary>The last cue that begins at or before a moment; -1 for none.</summary>
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
