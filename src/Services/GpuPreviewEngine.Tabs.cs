using System.Diagnostics;
using System.Globalization;
using HandPegApp.Compositor;

namespace HandPegApp.Services;

// ----- Background tab isolation -----
// Background tabs must not cost the active tab: the worker parks them lean on unified memory or battery (TabParkingRequest, DecoderPool
// lean parking); tab pre-roll and drop files are idle hints (GpuSeekHintKind.TabRoll) sent only while quiet; sources pre-rolled for a
// switch survive it; scrubbing defers background work; each play session logs one line.
public sealed partial class GpuPreviewEngine
{
    /// <summary>Background-work hold after each scrub move (strip workers pause between frames meanwhile).</summary>
    public static readonly TimeSpan ScrubHold = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// True when tabs run lean (UI thread): unified-memory worker adapter or on battery. Valid after Ready (one OS call for power state).
    /// </summary>
    public bool LeanTabs => UnifiedMemory || PowerStatus.OnBattery;

    /// <summary>
    /// True when idle hints may be sent: not playing or scrubbing, and at least DecoderPool.IdleQuietMs since the last seek
    /// (<paramref name="msSinceSeek"/>).
    /// </summary>
    internal static bool ExtrasMayBeWritten(bool playing, bool scrubbing, double msSinceSeek) =>
        !playing && !scrubbing && msSinceSeek >= DecoderPool.IdleQuietMs;

    /// <summary>
    /// Suspend: releases all sources except pre-rolled ones (so the next active tab finds its frames and decoders warm); the next SetState
    /// releases any it does not use.
    /// </summary>
    private void ReleaseSourcesForSwitch(PrewarmedTabs? prewarmed)
    {
        var keep = new HashSet<int>();
        if (prewarmed is { } tabs)
        {
            foreach (var hint in tabs.Hints)
                keep.Add(hint.SourceId);
        }

        _broker.ReleaseAllBut(keep);
    }

    /// <summary>
    /// On SetState: releases sources retained at the last switch that remain unused (once); files hinted after the switch stay open.
    /// </summary>
    private void ReleaseUnusedKeptSources() => _broker.ReleaseKeptUnasked();

    // ----- Per-play-session log line -----

    // Worker counters at Play (consumed once by the ending Pause, any thread) and pump-thread waited/late frame counts.
    private sealed record PlaySession(int Presents, long Approximate, int Opens, int Seeks, int Decoded, long Evictions, long Started);

    private PlaySession? _session;
    private int _sessionFrames, _sessionLate;

    /// <summary>Play pressed: the worker's counters as they stand.</summary>
    private void BeginPlaySession()
    {
        ref var control = ref _client.Memory.Control;
        Volatile.Write(ref _sessionFrames, 0);
        Volatile.Write(ref _sessionLate, 0);
        Volatile.Write(ref _session, new PlaySession(Volatile.Read(ref control.FramesRendered), Volatile.Read(ref control.ApproximatePresents),
            Volatile.Read(ref control.DecoderOpens), Volatile.Read(ref control.DecoderSeeks), Volatile.Read(ref control.DecodedFrames),
            Volatile.Read(ref control.Evictions), Stopwatch.GetTimestamp()));
    }

    /// <summary>Counts a waited play frame (pump thread), tallying lateness; returns <paramref name="late"/>.</summary>
    private bool NoteFrame(bool late)
    {
        Interlocked.Increment(ref _sessionFrames);
        if (late)
            Interlocked.Increment(ref _sessionLate);
        return late;
    }

    /// <summary>
    /// On Pause: returns the session's single summary line (null if none). Returned rather than logged so no file I/O occurs under the
    /// transport lock.
    /// </summary>
    private string? EndPlaySession()
    {
        if (Interlocked.Exchange(ref _session, null) is not { } start)
            return null;
        ref var control = ref _client.Memory.Control;
        var seconds = Stopwatch.GetElapsedTime(start.Started).TotalSeconds;
        return string.Create(CultureInfo.InvariantCulture,
            $"Play session: {seconds:0.0} s, {Volatile.Read(ref control.FramesRendered) - start.Presents} frames presented by the worker ({Volatile.Read(ref _sessionLate)} of {Volatile.Read(ref _sessionFrames)} played late, {Volatile.Read(ref control.ApproximatePresents) - start.Approximate} with a stand-in); decoders: {Volatile.Read(ref control.DecoderOpens) - start.Opens} opened, {Volatile.Read(ref control.DecoderSeeks) - start.Seeks} seeks, {Volatile.Read(ref control.DecodedFrames) - start.Decoded} frames decoded, {Volatile.Read(ref control.Evictions) - start.Evictions} evicted; frame cache budget {Volatile.Read(ref control.CacheBudgetBytes) >> 20} MB (cap {FrameCache.CapName(Volatile.Read(ref control.CacheBudgetCap))}).");
    }
}
