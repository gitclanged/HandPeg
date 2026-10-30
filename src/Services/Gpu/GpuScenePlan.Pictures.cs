using System.Diagnostics;

namespace HandPegApp.Services.Gpu;

// ----- The subtitle track's pictures (5.0-Verify) -----
// A PGS or VobSub track (PictureSubtitles.cs) in the preview, as libmpv drew it: each cue's picture over the main video's
// picture, under the captions, from its cue's start (in) to its end (not). A cue's picture is handed to the worker as a
// still while the playhead is near it (PrepareAround, as captions are drawn), and let go by its last use alone.
public sealed partial class GpuScenePlan
{
    private static readonly long PictureSubtitlesKey = default(GpuHash).Add("picture-subtitles").Value;

    /// <summary>
    /// The entry of the subtitle track's pictures. The stream's canvas is laid on the main video's own picture at the scale
    /// that fills it, centred (a film cropped or scaled from the frame its subtitles were drawn for is the middle of that
    /// frame; a DVD's anamorphic canvas is the picture's own pixels), then placed as the main video is: the crop's part of
    /// the picture filling its box (CenterRect). A picture is not cut by the crop or the box: a line under a letterbox that
    /// was cropped off is still there to read, below the picture, as a player shows it. With no main video, the canvas is
    /// laid on the frame the same way.
    /// </summary>
    private static Entry PictureSubtitlesEntry(PictureSubtitleTrack track, SequenceExportState state, int width, int height, bool hasPrimary)
    {
        var (sourceWidth, sourceHeight) = hasPrimary && state.SourceWidth > 0 && state.SourceHeight > 0 ? (state.SourceWidth, state.SourceHeight) : (width, height);
        var (boxX, boxY, boxWidth, boxHeight) = hasPrimary && state.CenterRect is { Width: > 0, Height: > 0 } box ? box : (0, 0, width, height);
        var (left, top, right, bottom) = hasPrimary && state.HasCrop ? state.CropFractions : default;
        (left, top, right, bottom) = (Math.Clamp(left, 0, 1), Math.Clamp(top, 0, 1), Math.Clamp(right, 0, 1), Math.Clamp(bottom, 0, 1));
        if (left + right >= 0.999 || top + bottom >= 0.999)
            (left, top, right, bottom) = (0, 0, 0, 0);

        // The canvas on the source's picture, in its pixels; then the source's pixels on the frame.
        var fill = Math.Max((double)sourceWidth / track.CanvasWidth, (double)sourceHeight / track.CanvasHeight);
        var (canvasLeft, canvasTop) = ((sourceWidth - track.CanvasWidth * fill) / 2, (sourceHeight - track.CanvasHeight * fill) / 2);
        var (perX, perY) = (boxWidth / (sourceWidth * (1 - left - right)), boxHeight / (sourceHeight * (1 - top - bottom)));
        var (originX, originY) = (boxX - left * sourceWidth * perX, boxY - top * sourceHeight * perY);

        var lane = new PictureLane(track.CueArray, (float)(fill * perX), (float)(originX + canvasLeft * perX), (float)(fill * perY), (float)(originY + canvasTop * perY));
        return new Entry { Kind = EntryKind.PictureSubtitles, Pictures = lane, Name = "Picture Subtitles", ClipKey = PictureSubtitlesKey };
    }

    /// <summary>The cue's picture up at a moment, as a still where its canvas's pixels fall on the frame.</summary>
    private int WritePictureSubtitle(Entry e, double t, ref GpuLayerDescriptor d)
    {
        if (_resources is null || e.Pictures is not { } lane || !lane.At(t, _resources, out var cue, out var still))
            return 0;

        (d.SourceKind, d.StillResourceId) = ((int)GpuSourceKind.Still, still.Id);
        (d.X, d.Y, d.Width, d.Height) = lane.Place(cue.Picture);
        return 1;
    }

    /// <summary>
    /// The subtitle track's pictures in a plan: its cues by their start, where the canvas's pixels fall on the frame (a scale
    /// and a shift each way), and the still each cue's picture was handed over as. The stills are written on the UI thread
    /// (<see cref="Prepare"/>) and read lock-free by the thread that evaluates; nothing else changes after it is made.
    /// </summary>
    private sealed class PictureLane(PictureSubtitleCue[] cues, float scaleX, float shiftX, float scaleY, float shiftY)
    {
        // Each cue's still: its id + 1 in the high half and its serial in the low; 0 while it has none.
        private readonly long[] _stills = new long[cues.Length];

        /// <summary>The cue up at a moment and its still, when that is there to be drawn with (allocation-free).</summary>
        public bool At(double t, GpuResourceBroker resources, out PictureSubtitleCue cue, out GpuResourceRef still)
        {
            (cue, still) = (default, GpuResourceRef.None);
            var at = UpAt(t);
            if (at < 0)
                return false;

            cue = cues[at];
            still = StillOf(at);
            return !still.IsNone && resources.StateOf(still) == GpuResourceState.Ready;
        }

        /// <summary>A picture's box on the frame: its place on the canvas, scaled and moved as the canvas is.</summary>
        public (float X, float Y, float Width, float Height) Place(PictureSubtitlePicture picture) =>
            (shiftX + scaleX * picture.X, shiftY + scaleY * picture.Y, scaleX * picture.Width, scaleY * picture.Height);

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
