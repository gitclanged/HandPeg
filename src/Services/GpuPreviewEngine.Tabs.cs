using System.Diagnostics;
using System.Globalization;
using HandPegApp.Compositor;

namespace HandPegApp.Services;

// ----- Project tabs behind cost the tab in front nothing (Pass 5.4.4) -----
// The player's worker parks a tab put behind lean on unified memory or on battery (the client's TabParkingRequest, set as the engine is
// made: DecoderPool's lean parking); the tabs' pre-roll and a drop's file are idle work (GpuSeekHintKind.TabRoll), written to the worker
// only while the player is quiet; the sources a tab's pre-roll opened are kept across the switch it was made for; a scrub holds background
// work back; and each play session leaves one line in the log.
public sealed partial class GpuPreviewEngine
{
    /// <summary>How long background work is held back after each move of a scrub (5.4.4: the strip workers stop between pictures meanwhile).</summary>
    public static readonly TimeSpan ScrubHold = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Whether project tabs are kept lean here (5.4.4, UI thread): the worker's card shares the PC's memory, or the PC runs on its battery.
    /// Read only after the engine is Ready (one call into Windows for the battery).
    /// </summary>
    public bool LeanTabs => UnifiedMemory || PowerStatus.OnBattery;

    /// <summary>
    /// Whether the tabs' pre-roll and a drop's file may be named to the worker now (5.4.4): never while playing or scrubbing, and not until
    /// the playhead has rested DecoderPool.IdleQuietMs after its last seek (<paramref name="msSinceSeek"/>).
    /// </summary>
    internal static bool ExtrasMayBeWritten(bool playing, bool scrubbing, double msSinceSeek) =>
        !playing && !scrubbing && msSinceSeek >= DecoderPool.IdleQuietMs;

    /// <summary>
    /// A tab put behind (Suspend): every source let go of but those the tabs' pre-roll named (5.4.4: the tab brought to the front next finds
    /// its frames and its open decoders where its pre-roll left them); the first SetState after it lets go of those it does not use.
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
    /// A plan made (SetState): the sources kept at the last switch that nothing asked for since are let go of, once (5.4.4). Only those:
    /// the files a pre-roll or a drop named after the switch, before this plan, stay for what they were opened for.
    /// </summary>
    private void ReleaseUnusedKeptSources() => _broker.ReleaseKeptUnasked();

    // ----- One line in the log per play session (5.4.4) -----

    // The worker's counters when play was pressed (taken once by the Pause that ends it, whichever thread), and the frames the pump waited
    // for since and those that came late (the pump's thread).
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

    /// <summary>A played frame waited for (the pump's thread): counted, late or not. Returns <paramref name="late"/>.</summary>
    private bool NoteFrame(bool late)
    {
        Interlocked.Increment(ref _sessionFrames);
        if (late)
            Interlocked.Increment(ref _sessionLate);
        return late;
    }

    /// <summary>Paused: what the play session cost the worker, in one line (no per-frame logging).</summary>
    private void EndPlaySession()
    {
        if (Interlocked.Exchange(ref _session, null) is not { } start)
            return;
        ref var control = ref _client.Memory.Control;
        var seconds = Stopwatch.GetElapsedTime(start.Started).TotalSeconds;
        AppLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Play session: {seconds:0.0} s, {Volatile.Read(ref control.FramesRendered) - start.Presents} frames presented by the worker ({Volatile.Read(ref _sessionLate)} of {Volatile.Read(ref _sessionFrames)} played late, {Volatile.Read(ref control.ApproximatePresents) - start.Approximate} with a stand-in); decoders: {Volatile.Read(ref control.DecoderOpens) - start.Opens} opened, {Volatile.Read(ref control.DecoderSeeks) - start.Seeks} seeks, {Volatile.Read(ref control.DecodedFrames) - start.Decoded} frames decoded, {Volatile.Read(ref control.Evictions) - start.Evictions} evicted; frame cache budget {Volatile.Read(ref control.CacheBudgetBytes) >> 20} MB."));
    }
}
