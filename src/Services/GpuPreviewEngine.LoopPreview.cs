using System.Diagnostics;
using HandPegApp.Services.Gpu;

namespace HandPegApp.Services;

// ----- Live looping hover previews (Pass 5.7.1 lane L2) -----
// A menu item resting over a fade, a cut's crossfade or a clip's gain plays the real GPU composite (and its sound) round a short window
// of the Sequence: the window is pre-decoded at idle priority while the menu is open (WarmLoopPreview: AddLoopPreviewHints), played on
// the engine alone (the view model stays paused and its playhead parked) and wrapped by the pump each turn (LoopPreviewTurn posts a seek
// to its start); StopLoopPreview pauses and puts the engine back at the parked moment. The state installed before the preview is kept
// (StateInstalled) so leaving shows the clip as it was at once, ahead of the view model's own push.
public sealed partial class GpuPreviewEngine
{
    /// <summary>Longest window a hover preview loops (s).</summary>
    public const double LoopPreviewMostSeconds = 2.0;

    /// <summary>Shortest window a hover preview loops (s): shorter targets are padded round their middle.</summary>
    public const double LoopPreviewLeastSeconds = 1.0;

    /// <summary>How long a menu's pre-warm request stays (s) after it opened.</summary>
    public const double LoopWarmSeconds = 8.0;

    // The window being looped (null: none), written by the UI thread, read by the pump.
    private sealed record LoopWindow(double From, double To);

    private LoopWindow? _hoverLoop;

    // Serializes the pump's wrap publication with StopLoopPreview's cancel (review F29): once a stop took the window, no wrap is posted.
    private readonly Lock _loopGate = new();

    // A menu's pre-warm request: its window and expiry (Stopwatch ticks).
    private sealed record LoopWarm(double From, double To, long Until);

    private LoopWarm? _loopWarm;

    // The state installed while no preview loops (UI thread): what leaving puts back at once.
    private SequenceExportState? _stateBeforeLoop;

    /// <summary>True while a hover preview loops on the engine.</summary>
    public bool IsLoopPreviewing => Volatile.Read(ref _hoverLoop) is not null;

    /// <summary>The window being looped (NaN, NaN: none).</summary>
    public (double From, double To) LoopPreviewWindow => Volatile.Read(ref _hoverLoop) is { } loop ? (loop.From, loop.To) : (double.NaN, double.NaN);

    /// <summary>
    /// The window a hover preview loops round <paramref name="from"/>-<paramref name="to"/> (a fade, a crossfade, a clip's stretch), held to
    /// <see cref="LoopPreviewLeastSeconds"/>-<see cref="LoopPreviewMostSeconds"/> about its middle and inside the Sequence (two frames clear of
    /// its end, so play never reaches it and pauses there). Null when the Sequence is too short to loop. Pure.
    /// </summary>
    public static (double From, double To)? LoopWindowOf(double from, double to, double sequenceSeconds, double fps = 30)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to) || !double.IsFinite(sequenceSeconds))
            return null;

        var frame = 1 / (fps > 1 ? fps : 30);
        var end = sequenceSeconds - 2 * frame;
        if (end < 4 * frame)
            return null;

        (from, to) = (Math.Min(from, to), Math.Max(from, to));
        var middle = (from + to) / 2;
        var span = Math.Clamp(to - from + 0.5, LoopPreviewLeastSeconds, LoopPreviewMostSeconds);
        var start = middle - span / 2;
        var stop = start + span;
        if (start < 0)
            (start, stop) = (0, Math.Min(span, end));
        if (stop > end)
            (start, stop) = (Math.Max(end - span, 0), end);
        return stop - start >= 2 * frame ? (start, stop) : null;
    }

    /// <summary>
    /// Where the pump wraps a looping preview: <paramref name="from"/> when the clock at <paramref name="position"/> has reached
    /// <paramref name="to"/> (less <paramref name="lead"/>, a refresh, so the window's last frame is not repeated) or run before the
    /// window; NaN to leave it playing. Pure.
    /// </summary>
    public static double LoopWrapTarget(double position, double from, double to, double lead = 0)
    {
        if (!double.IsFinite(position) || !double.IsFinite(from) || !double.IsFinite(to) || to <= from)
            return double.NaN;
        return position >= to - Math.Max(lead, 0) || position < from - 0.25 ? from : double.NaN;
    }

    /// <summary>
    /// Menu opened over a target: its window is pre-decoded at idle priority (and its text rasterized) for <see cref="LoopWarmSeconds"/>,
    /// so a hover preview starts on frames already in VRAM. UI thread.
    /// </summary>
    public void WarmLoopPreview(double from, double to)
    {
        _uiOwner.Check();
        if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from)
        {
            Volatile.Write(ref _loopWarm, null);
            return;
        }

        Volatile.Write(ref _loopWarm, new LoopWarm(from, to, Stopwatch.GetTimestamp() + (long)(LoopWarmSeconds * Stopwatch.Frequency)));
        Volatile.Read(ref _plan).PrepareAround(from);
        if (!_playing)
            _dirty.Set();
    }

    /// <summary>
    /// Loops <paramref name="from"/>-<paramref name="to"/> on the engine (picture and sound) from its start; the view model's transport is
    /// not touched. False when the engine already plays for the user. UI thread.
    /// </summary>
    public bool StartLoopPreview(double from, double to)
    {
        _uiOwner.Check();
        if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from || (_playing && !IsLoopPreviewing))
            return false;

        _stateBeforeLoop ??= _state;
        Volatile.Write(ref _hoverLoop, new LoopWindow(from, to));
        Seek(from);
        Play();
        return true;
    }

    /// <summary>
    /// Ends a looping preview: pauses (unless <paramref name="keepPlaying"/>: the user pressed play meanwhile, so play goes on from the
    /// parked moment), shows the state installed before it at once (the caller's own push follows) and parks the engine at
    /// <paramref name="parkAt"/> (s). Nothing when none loops. UI thread.
    /// </summary>
    public void StopLoopPreview(double parkAt, bool keepPlaying = false)
    {
        _uiOwner.Check();
        LoopWindow? taken;
        lock (_loopGate)
            taken = Interlocked.Exchange(ref _hoverLoop, null);
        if (taken is null)
            return;

        if (!keepPlaying)
            Pause();
        if (Interlocked.Exchange(ref _stateBeforeLoop, null) is { } before && !ReferenceEquals(before, _state))
            SetState(before);
        if (double.IsFinite(parkAt))
            Seek(parkAt);
    }

    /// <summary>Drops a menu's pre-warm request (the menu closed). Any thread.</summary>
    public void EndLoopWarm() => Volatile.Write(ref _loopWarm, null);

    partial void LoopPreviewTurn()
    {
        if (Volatile.Read(ref _hoverLoop) is null)
            return;

        // Under the gate a stop cannot take the window between the look and the post, and the post lands only in an empty slot, so a seek
        // the UI posted after the look (its own, or a stop's park) is never replaced by a stale wrap (review F29).
        lock (_loopGate)
        {
            if (Volatile.Read(ref _hoverLoop) is not { } loop)
                return;

            // A seek already posted (the UI's, or a wrap not drained yet) lands first: one wrap per pass through the window, as DrainSeek
            // moves the audio clock back to the window's start within this same turn.
            if (SeekMailSlot.IsPending(ref _pendingSeekBits))
                return;

            var target = LoopWrapTarget(PlayingPosition(), loop.From, loop.To, _refresh > 0 ? _refresh : 1.0 / 60);
            if (double.IsNaN(target) || !SeekMailSlot.TryPostIfEmpty(ref _pendingSeekBits, target))
                return;

            // Posted as Seek posts it, without Seek's scrub bookkeeping (that is the UI thread's own).
            Volatile.Write(ref _pausedAt, target);
            Interlocked.Increment(ref _transportSerial);
        }
    }

    partial void AddLoopPreviewHints(Span<GpuSeekHint> extras, ref int count)
    {
        if (Volatile.Read(ref _loopWarm) is not { } warm || Stopwatch.GetTimestamp() >= warm.Until)
            return;

        // The clips the window crosses (at most two: a fade's clip, or both sides of a cut), from the window's start.
        var added = 0;
        foreach (var cut in Volatile.Read(ref _plan).Cuts)
        {
            if (count >= extras.Length || added >= 2)
                break;
            if (cut.End <= warm.From || cut.Start >= warm.To || cut.SourceId < 0)
                continue;

            var start = Math.Max(warm.From, cut.Start);
            var speed = cut.Speed > 0 ? cut.Speed : 1;
            extras[count++] = new GpuSeekHint
            {
                SourceId = cut.SourceId, Kind = (int)GpuSeekHintKind.TabRoll, PathHash = cut.PathHash,
                MediaSeconds = Math.Max(cut.Media + (start - cut.Start) * speed, 0),
                Span = (float)Math.Max((Math.Min(warm.To, cut.End) - start) * speed, 0.04),
                At = (float)start,
            };
            added++;
        }
    }

    partial void StateInstalled(SequenceExportState state)
    {
        // The newest state while nothing loops is what a preview, once begun, puts back.
        if (!IsLoopPreviewing)
            _stateBeforeLoop = null;
    }
}
