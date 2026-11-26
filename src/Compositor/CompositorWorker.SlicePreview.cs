using System.Diagnostics;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// ----- Slice-Preview burst (Pass 5.7.1) -----
// The client posts one file's neighbour times around the playhead (SliceBurstSlot, ControlBlock 324..415). Each worker turn takes the
// newest whole burst (seqlock) and asks the decoder pool for every slice not yet in the frame cache, never while playing and only while no
// decode lane is busy (so a visible frame being chased, cut pre-roll or play-ahead always goes first: ahead of background work, never ahead
// of the visible frame). The burst is taken after the turn's scene is drawn, and asks are admitted only on a turn that drew no new or
// redrawn scene and whose picture is exact (a seek or play start that just arrived registers its demands first). Asks repeat at most
// every SliceAskEveryMs. Once every slice is cached it answers SliceBurstDone with that exact version; a burst superseded before then is
// dropped unanswered. While one is outstanding the paused loop polls every SliceBurstPollMs instead of 100 ms, so the answer is prompt.
public static unsafe partial class CompositorWorker
{
    /// <summary>The paused loop's wait, ms, while a slice burst is outstanding.</summary>
    internal const int SliceBurstPollMs = 8;

    /// <summary>Least time, ms, between two asks for the same burst's missing slices.</summary>
    internal const int SliceAskEveryMs = 120;

    /// <summary>How long, ms, a burst is worked on before the worker stops asking and polling for it (left unanswered; the next one starts over).</summary>
    internal const int SliceGiveUpMs = 2000;

    // Decoder affinity key for slice asks (GpuPreviewEngine.SliceClipKey: the strip's layers resolve the same decoder).
    private const long SliceClipKey = 0x5_11CE_5_7_1;

    // Worker loop's own: the burst being decoded (version 0: none), whether it was answered, and when its slices were last asked for.
    private static SliceBurst _slice;
    private static bool _sliceAnswered = true;
    private static long _sliceAskedAt, _sliceTakenAt;

    /// <summary>True while a posted burst is not answered yet (the paused loop polls faster).</summary>
    private static bool SliceBurstOutstanding => !_sliceAnswered && _slice.Count > 0;

    /// <summary>
    /// Whether a frame is a small BGRA readback (a quarter of the ring's area at most: a project thumbnail, the Slice-Preview strip) drawn
    /// in place in the current output ring: no NV12 or P010 conversion (which reads the whole texture), and the ring holds it. Its region
    /// is copied back and the player's ring is kept; larger readbacks (Export Frame) still get a ring of exactly their size. Pure; goldens.
    /// </summary>
    internal static bool ReadsBackInPlace(int flags, int ringWidth, int ringHeight, int width, int height) =>
        (flags & FrameSceneHeader.FlagReadback) != 0 && (flags & (FrameSceneHeader.FlagReadbackNv12 | FrameSceneHeader.FlagReadbackP010)) == 0
        && ringWidth > 0 && ringHeight > 0 && width > 0 && height > 0 && width <= ringWidth && height <= ringHeight
        && (long)width * height * 4 <= (long)ringWidth * ringHeight;

    /// <summary>
    /// Whether the worker asks for a burst's missing slices this turn: a burst not yet answered, nothing playing, every decode lane idle,
    /// and <see cref="SliceAskEveryMs"/> since the last ask. Pure; goldens.
    /// </summary>
    internal static bool SliceAskDue(bool outstanding, bool playing, int busyLanes, long now, long askedAt, long frequency) =>
        outstanding && !playing && busyLanes == 0 && (askedAt == 0 || (now - askedAt) * 1000 >= SliceAskEveryMs * frequency);

    /// <summary>
    /// Each worker turn, after the turn's scene: takes the newest burst and answers it once every slice is cached; asks for missing slices
    /// only when <paramref name="admit"/> (a quiet turn: no new or redrawn scene, the picture exact).
    /// </summary>
    private static void SliceBurstTurn(ref ControlBlock control, DecoderPool decoders, bool admit)
    {
        // A newer whole burst replaces the one being decoded (its answer is never written); a torn read is retried next turn.
        if (SliceBurstSlot.TryRead(ref control, out var burst) && burst.Version != _slice.Version)
        {
            (_slice, _sliceAnswered, _sliceAskedAt, _sliceTakenAt) = (burst, burst.Count == 0, 0, Stopwatch.GetTimestamp());

            // Its file may have just been added to the source table: sync first, or an unknown source would read as cached.
            if (!_sliceAnswered)
                decoders.SyncSources();
        }
        if (!SliceBurstOutstanding)
            return;

        var missing = 0;
        for (var i = 0; i < _slice.Count; i++)
        {
            if (!decoders.HasExact(_slice.SourceId, _slice.Media[i]))
                missing++;
        }

        if (missing == 0)
        {
            Volatile.Write(ref control.SliceBurstDone, _slice.Version);
            _sliceAnswered = true;
            return;
        }

        // A burst that cannot be finished (lanes never idle, a file that will not decode) stops costing a fast poll.
        var now = Stopwatch.GetTimestamp();
        if ((now - _sliceTakenAt) * 1000 >= SliceGiveUpMs * Stopwatch.Frequency)
        {
            _sliceAnswered = true;
            return;
        }

        if (!admit || !SliceAskDue(true, decoders.Playing, decoders.BusyLanes, now, _sliceAskedAt, Stopwatch.Frequency))
            return;

        _sliceAskedAt = now;
        for (var i = 0; i < _slice.Count; i++)
        {
            var seconds = _slice.Media[i];
            if (!decoders.HasExact(_slice.SourceId, seconds))
                decoders.Request(_slice.SourceId, seconds, _slice.PathHash, SliceClipKey);
        }
    }
}
