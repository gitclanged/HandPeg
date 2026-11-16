using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// DecoderPool decode lanes: demand selection (MostUrgent), decoder assignment (DecoderFor, OpenFor), decoding (WorkOn), and
// burial/disposal of idle decoders.
internal sealed partial class DecoderPool
{
    // ----- Lanes -----

    private void RunLane(int lane)
    {
        _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
        var waits = new WaitHandle[] { _work, _stopping };
        while (!_stopping.WaitOne(0))
        {
            Demand? demand;
            MfVideoDecoder? decoder;
            lock (_gate)
                (demand, decoder) = Pick(lane);

            if (demand is null)
            {
                if (WaitHandle.WaitAny(waits, 250) == 1)
                    return;
                continue;
            }

            try
            {
                if (decoder is null)
                {
                    WaitWhileDrawing();

                    // Idle work opens no decoder if the player moved meanwhile: undo the reserved open.
                    if (demand.Kind == DemandKind.Idle)
                    {
                        bool mayOpen;
                        lock (_gate)
                        {
                            mayOpen = IdleMayRunNow(countLanes: false);
                            if (!mayOpen)
                                demand.Source.Opening = Math.Max(demand.Source.Opening - 1, 0);
                        }

                        if (!mayOpen)
                            continue;
                    }

                    // Nor for a far cut when a cold-play window opened while this lane waited for the draw: no open or seek. Undo the
                    // reserved open; the demand stays pending and is re-picked once the window closes or the cut comes within the lead.
                    bool held;
                    lock (_gate)
                    {
                        held = HeldForColdPlay(demand);
                        if (held)
                            demand.Source.Opening = Math.Max(demand.Source.Opening - 1, 0);
                    }

                    if (held)
                        continue;

                    decoder = OpenFor(demand.Source, lane);
                    lock (_gate)
                        demand.Decoder = decoder;
                }

                if (decoder is not null)
                    Work(lane, demand, decoder);
            }
            catch (GpuException ex) when (ex.IsDeviceLost)
            {
                _laneFault = ex;
                _arrived.Set();
                return;
            }
            catch (Exception ex) when (ex is GpuException or InvalidOperationException or IOException or ArgumentException)
            {
                // Mid-stream failure (broken stream): dispose the decoder; the source retries from scratch, that range only after 3 s.
                AppLog.WriteOnce($"decode|{demand.Source.Path}|{ex.HResult}", $"Decode: {Path.GetFileName(demand.Source.Path)} failed", ex);
                LastError = $"{Path.GetFileName(demand.Source.Path)}: {ex.Message}";
                (demand.Source.FailedAt, demand.Source.FailedUntil) = (demand.Target, Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency);
                lock (_gate)
                {
                    if (decoder is not null)
                    {
                        demand.Source.Decoders.Remove(decoder);
                        decoder.Owner = -1;
                    }

                    demand.Done = true;
                }

                decoder?.Dispose();
                decoder = null;
            }
            finally
            {
                Volatile.Write(ref _laneBusySince[lane], 0);
                lock (_gate)
                {
                    (demand.Lane, demand.Decoder) = (-1, null);
                    if (decoder is not null)
                        decoder.Owner = -1;

                    // A satisfied pre-roll or hint stays (done) so its range remains a cache interest and the frames decoded ahead
                    // survive until requested (cut frames, until their hints are removed).
                    if (demand.Cancelled || demand.Done && demand.Kind is not (DemandKind.PreRoll or DemandKind.Hinted or DemandKind.Hover or DemandKind.Idle))
                        _demands.Remove(demand);
                    if (demand.Source.Retired)
                        BuryIdle(demand.Source);
                }

                // Source retired while this lane read it (tab deactivated): dispose the decoder now, on this thread, outside the lock
                // (not left open until Tidy's next pass).
                if (demand.Source.Retired)
                    DisposeBuried();
            }
        }
    }

    // Pick counter (under the lock): a demand no decoder can serve is skipped for the rest of that pick (Demand.PassedIn).
    private long _picks;

    /// <summary>
    /// The most urgent unserved demand whose frame is missing, with its decoder (the source's free decoder reaching it in the fewest frames;
    /// null = open one). A demand no decoder can serve now (source at its limit, each decoder held or parked where wanted) is skipped for the
    /// next (otherwise a seventh cut of one file could idle all lanes and starve pre-roll, pointer work and other files). Under the lock.
    /// </summary>
    private (Demand?, MfVideoDecoder?) Pick(int lane)
    {
        var pick = ++_picks;
        while (true)
        {
            var best = MostUrgent(pick);
            if (best is null)
                return (null, null);
            if (!DecoderFor(best, out var choice, out var open))
            {
                best.PassedIn = pick;
                continue;
            }

            if (open)
                best.Source.Opening++;
            best.Lane = lane;
            best.Decoder = choice;
            if (choice is not null)
            {
                choice.Owner = lane;
                choice.LastUsed = Stopwatch.GetTimestamp();
            }

            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            return (best, choice);
        }
    }

    /// <summary>
    /// Maximum lanes for deferrable work: two, or one per visible video; at most two on unified memory, where every speculative frame costs
    /// system RAM and bandwidth.
    /// </summary>
    private int BackgroundLanes => _gpu.UnifiedMemory ? UnifiedBackgroundLanes : Math.Clamp(_videosShown, 2, Math.Max(_lanes.Count - 1, 2));

    private const int UnifiedBackgroundLanes = 2;

    private int _videosShown = 1;

    // Lanes currently on deferrable work (under the lock).
    private int BackgroundBusy()
    {
        var busy = 0;
        foreach (var demand in _demands)
            busy += demand.Lane >= 0 && !demand.Done && demand.Kind is DemandKind.PreRoll or DemandKind.Hover or DemandKind.Hinted or DemandKind.Idle ? 1 : 0;
        return busy;
    }

    /// <summary>Most urgent workable demand not skipped in this pick (under the lock).</summary>
    private Demand? MostUrgent(long pick)
    {
        Demand? best = null;
        bool? idleMayRun = null;
        foreach (var demand in _demands)
        {
            if (demand.Lane >= 0 || demand.Cancelled || demand.Done || demand.Source.Failed || demand.Source.Retired || demand.PassedIn == pick)
                continue;

            // Idle work (a tab's pre-roll, a dropped file) only while the player is quiet: not while playing or scrubbing, while a visible
            // or play-ahead frame is pending, or within IdleQuietMs of the last playhead move.
            if (demand.Kind == DemandKind.Idle && !(idleMayRun ??= IdleMayRunNow(countLanes: true)))
                continue;

            // Cold play: for ColdPlaySeconds after a cold start, cuts more than CutSoon ahead wait as demands (no memory taken) and
            // are read afterwards, earliest first.
            if (HeldForColdPlay(demand))
                continue;

            // Order: most urgent kind; within a hint kind, soonest needed (earliest cut); then oldest.
            if (best is not null && !(demand.Rank < best.Rank || demand.Rank == best.Rank && (demand.At < best.At || demand.At == best.At && demand.Asked < best.Asked)))
                continue;

            // Frame (or whole range) already cached, e.g. another lane passed through it: nothing to decode and no decoder opened
            // (a pre-roll never gets the decoder parked at the playhead).
            if (!demand.Stretch ? _cache.Has(demand.Source.Serial, demand.Target) : _cache.Covers(demand.Source.Serial, demand.KeepFrom, demand.Target))
            {
                demand.Done = true;
                continue;
            }

            // Another lane reads this file forward through (or up to just short of) it: one reader per file range, never a second
            // decoder seeking the same key frame to decode the same frames.
            if (ReadByAnotherLane(demand) is not null)
                continue;

            // While playing, a file being read by playback is not opened or sought again for deferrable work until within CutSoon (five
            // layers of one file once made a look-ahead decoder seek every 1 s key frame, and a shared hardware decoder lock stalled
            // playback 40-100 ms each time).
            if (_lastWasPlaying && demand.Kind is DemandKind.PreRoll or DemandKind.Hover or DemandKind.Hinted &&
                !(demand.At - _lastPlayhead <= CutReadAhead(_gpu.UnifiedMemory, Volatile.Read(ref _playRate))) && PlaybackReads(demand.Source))
                continue;

            // Pre-roll and pointer work (and, paused, cut hints) wait while a visible frame is being chased: the GPU has one decoder
            // engine and a second stream halves the chase (SeekBench: cold seeks a third slower alongside pre-roll). While playing, a
            // cut ahead is not held up by one late frame.
            if ((demand.Kind is DemandKind.PreRoll or DemandKind.Hover || demand.HintKind == GpuSeekHintKind.CutEntry && !_lastWasPlaying) && VisibleBeingDecoded())
                continue;

            // Lanes beyond two are reserved for frames due on screen: the GPU has one decoder engine, and deferrable work must not
            // take them from urgent frames (SeekBench: 6 lanes with a wider pre-roll made 1 frame in 1000 late).
            if (demand.Kind is DemandKind.PreRoll or DemandKind.Hover or DemandKind.Hinted or DemandKind.Idle && BackgroundBusy() >= BackgroundLanes)
                continue;
            best = demand;
        }

        return best;
    }

    /// <summary>
    /// Decoder for a demand now (under the lock): the source's free, takeable decoder reaching it in the fewest frames
    /// (<paramref name="choice"/>), or a new one to open (<paramref name="open"/>). False when none is possible: the source is at its
    /// limit with every decoder held by a lane or parked where wanted.
    /// </summary>
    private bool DecoderFor(Demand best, out MfVideoDecoder? choice, out bool open)
    {
        var source = best.Source;
        (choice, open) = (null, false);
        var cost = double.PositiveInfinity;
        foreach (var decoder in source.Decoders)
        {
            if (decoder.Owner >= 0 || !MayTake(best, decoder, out var penalty))
                continue;
            var c = decoder.CostTo(best.Stretch && best.Kind != DemandKind.PreRoll ? StartOf(best, decoder, best.Target) : best.Target) + penalty;
            if (c < cost)
                (choice, cost) = (decoder, c);
        }

        // No free decoder, or only ones parked at a visible layer or cut: open another while the source has room (a file open on this
        // lane's thread; cuts ahead may get a few extra). With the pool full an open closes another (TrimToCapacity): pointer work never
        // opens then, and other look-ahead work only when an unwanted parked decoder can go (never the playhead's or a sooner cut's).
        if (choice is not null && cost < 1e6)
            return true;
        var most = best.HintKind == GpuSeekHintKind.CutEntry ? MostPerSourceForCuts : MostPerSource;
        if (source.Decoders.Count + source.Opening < most && Stopwatch.GetTimestamp() >= source.NoOpensUntil &&
            (!(IsHint(best.Kind) || best.Kind == DemandKind.PreRoll) || OpenDecoders() < Capacity || best.Kind is not (DemandKind.Hover or DemandKind.Idle) && AnyIdleUnwanted()))
        {
            (choice, open) = (null, true);
            return true;
        }

        return choice is not null;
    }

    /// <summary>
    /// Whether a free decoder may be taken for a demand, and the added cost (1e6 = only as a last resort). Under the lock.
    /// </summary>
    private bool MayTake(Demand best, MfVideoDecoder decoder, out double penalty)
    {
        // For a hint or pre-roll, prefer the decoder continuing forward from exactly there (range decoded up to it): another would
        // seek back to the same key frame and decode the same frames again.
        penalty = 0;
        if (!(IsHint(best.Kind) || best.Kind == DemandKind.PreRoll) || best.Stretch && ContinuesStretch(best, decoder))
            return true;

        // Idle work never takes a decoder parked at a visible layer (playing or paused); one parked at a cut is refused below.
        if (best.Kind == DemandKind.Idle && IsParkedAtVisible(decoder))
            return false;

        // A decoder parked at a visible layer is never taken while playing (the next shown frames come from it; taking it forces a
        // chase), nor for a cut ahead (that opens its own or waits: the playhead's range plays first); paused, only as a last resort
        // (a cut hint taking the playing decoder caused stutter at every new cut).
        if (IsParkedAtVisible(decoder))
        {
            if (_lastWasPlaying || best.HintKind == GpuSeekHintKind.CutEntry)
                return false;
            penalty = 1e6;
        }

        // A decoder parked at a cut's first frames waits for the playhead (then reads on in real time): never taken for the pointer,
        // pre-roll or a later cut; for an earlier cut only as a last resort.
        switch (ParkedAtCut(decoder, best))
        {
            case 2:
                return false;
            case 1:
                penalty = 1e6;
                break;
        }

        return true;
    }

    /// <summary>Open and opening decoders across all sources (under the lock).</summary>
    private int OpenDecoders()
    {
        var open = 0;
        foreach (var source in _sources.Values)
            open += source.Decoders.Count + source.Opening;
        return open;
    }

    /// <summary>True when any source has a free decoder not parked where wanted (TrimToCapacity closes those first; under the lock).</summary>
    private bool AnyIdleUnwanted()
    {
        foreach (var source in _sources.Values)
        {
            foreach (var decoder in source.Decoders)
            {
                if (decoder.Owner < 0 && !ParkedWhereWanted(source, decoder))
                    return true;
            }
        }

        return false;
    }

    /// <summary>True when a decoder sits inside a range decoded up to its position, so the range continues from it (StartOf).</summary>
    private bool ContinuesStretch(Demand stretch, MfVideoDecoder decoder)
    {
        var read = decoder.ReadTime;
        return !double.IsNaN(read) && !decoder.AtEnd && read > stretch.KeepFrom && read < stretch.Target && _cache.Covers(stretch.Source.Serial, stretch.KeepFrom, read);
    }

    /// <summary>
    /// Whether a decoder waits at the end of a cut's first frames for the playhead (under the lock): 0 no; 1 takeable for
    /// <paramref name="best"/> only as a last resort (scrub direction, or a cut needed sooner than its own); 2 never (pointer work,
    /// pre-roll, a cut needed later or at the same time).
    /// </summary>
    private int ParkedAtCut(MfVideoDecoder decoder, Demand best)
    {
        var read = decoder.ReadTime;
        if (double.IsNaN(read) || decoder.AtEnd)
            return 0;
        var parked = 0;
        foreach (var demand in _demands)
        {
            if (ReferenceEquals(demand, best) || demand.HintKind != GpuSeekHintKind.CutEntry || demand.Cancelled || demand.Source != best.Source ||
                read < demand.KeepFrom - 1e-6 || read > demand.Target + decoder.FrameDuration * 1.5)
                continue;
            if (best.Kind is DemandKind.Hover or DemandKind.PreRoll or DemandKind.Idle)
                return 2;
            parked = Math.Max(parked, best.HintKind == GpuSeekHintKind.CutEntry && demand.At <= best.At ? 2 : 1);
        }

        return parked;
    }

    // Cache budget share one source range may take: frames kept behind a request, the paused pre-roll around it, play-ahead, a cut's
    // first frames, pointer play-ahead.
    private const double BehindShare = 0.2, PreRollShare = 0.4, AheadShare = 0.25, CutShare = 0.05, HoverShare = 0.1;

    /// <summary>
    /// Clamps a source range (seconds) to its cache budget share (a 3 s pre-roll of a 420 fps 1440p recording is 1260 frames, more than a
    /// 3 GB cache holds, which would decode, evict and decode again).
    /// </summary>
    private double Fit(SourceState source, double seconds, double share)
    {
        if (source.FrameBytes <= 0 || source.FrameDuration <= 0)
            return seconds;
        var frames = _cache.BudgetBytes * share / source.FrameBytes;
        return Math.Min(seconds, Math.Max(frames, 8) * source.FrameDuration);
    }

    /// <summary>True when a lane is decoding toward a visible frame (under the lock).</summary>
    private bool VisibleBeingDecoded()
    {
        foreach (var demand in _demands)
        {
            if (demand.Lane >= 0 && demand.Kind == DemandKind.Visible && !demand.Cancelled)
                return true;
        }

        return false;
    }

    /// <summary>Lead (sequence seconds) within which a cut into a file being played may get a decoder opened or sought.</summary>
    internal const double CutSoon = 1.5;

    /// <summary>True when playback is reading a file: a lane on a visible or play-ahead frame, or a decoder parked at one (under the lock).</summary>
    private bool PlaybackReads(SourceState source)
    {
        foreach (var other in _demands)
        {
            if (other.Lane >= 0 && other.Source == source && !other.Cancelled && other.Kind is DemandKind.Visible or DemandKind.PlayAhead)
                return true;
        }

        foreach (var decoder in source.Decoders)
        {
            if (decoder.Owner < 0 && IsParkedAtVisible(decoder))
                return true;
        }

        return false;
    }

    /// <summary>Distance past a reader's target within which frames of its file are left to it (it continues from where it stopped).</summary>
    private const double ReaderReach = 1.0;

    /// <summary>
    /// Another lane's demand whose decoder reads <paramref name="demand"/>'s file forward to, through, or within a second of its frame
    /// (under the lock); null when none. Times behind every reader or farther ahead get their own decoder (e.g. a crossfade head and a far
    /// cut in one file).
    /// </summary>
    private Demand? ReadByAnotherLane(Demand demand)
    {
        // A hint range (cut first frames, pointer play-ahead) defers to a reader that passes its first frame.
        var at = demand.Stretch && demand.Kind != DemandKind.PreRoll ? demand.KeepFrom : demand.Target;
        foreach (var other in _demands)
        {
            if (other.Lane < 0 || ReferenceEquals(other, demand) || other.Source != demand.Source || other.Cancelled || other.Decoder is not { } reader)
                continue;
            if (Delivers(other, reader, at))
                return other;
        }

        return null;
    }

    /// <summary>
    /// True when a lane's read (demand <paramref name="read"/>, decoder <paramref name="reader"/>) will deliver the frame at
    /// <paramref name="target"/> sooner than its own chase (under the lock): the frame is ahead of the reader (or its landing key frame),
    /// within the read's range and kept frames, and the frames before it are no more than a seek to its key frame plus the chase (a click
    /// past a pre-roll's end must not wait for it).
    /// </summary>
    private static bool Delivers(Demand read, MfVideoDecoder reader, double target)
    {
        var from = reader.ReadTime;
        if (double.IsNaN(from))
        {
            var landing = reader.ListedKeyAtOrBefore(read.Target);
            from = double.IsNaN(landing) ? read.Target - ReaderReach : landing;
        }

        if (target < from - 1e-6 || target > read.Target + 1e-6 || target < read.KeepFrom - 1e-6)
            return false;

        var frame = Math.Max(reader.FrameDuration, 1e-4);
        var key = reader.ListedKeyAtOrBefore(target);
        var chase = 4 + (double.IsNaN(key) ? ReaderReach : Math.Max(target - key, 0)) / frame;
        return (target - from) / frame <= chase;
    }

    private bool IsParkedAtVisible(MfVideoDecoder decoder)
    {
        if (double.IsNaN(decoder.ReadTime))
            return false;
        foreach (var demand in _demands)
        {
            if (demand.Kind is DemandKind.Visible or DemandKind.PlayAhead && demand.Source.Decoders.Contains(decoder) &&
                Math.Abs(demand.Target - decoder.ReadTime) < 2)
                return true;
        }

        foreach (var memory in _layers.Values)
        {
            if (_frame - memory.LastFrame < 4 && Math.Abs(memory.Media - decoder.ReadTime) < 2)
                return true;
        }

        return false;
    }

    /// <summary>Opens another decoder for a source on the lane thread (a file open never blocks a frame).</summary>
    private MfVideoDecoder? OpenFor(SourceState source, int lane)
    {
        TrimToCapacity();
        try
        {
            // Opening on a sleeping disk or share can be slow: exempt from the stuck-lane watchdog.
            Volatile.Write(ref _laneBusySince[lane], 0);
            // Hardware decode unless on WARP or the client requests CPU decode (drivers producing broken frames).
            var hardware = !_gpu.IsWarp && Volatile.Read(ref _shared.Control.SoftwareDecodeRequest) == 0;
            // Exports on ARM64 decode HEVC with FFmpeg, which is faster there (MfVideoDecoder.PrefersFfmpegForHevcExports).
            var decoder = new MfVideoDecoder(_gpu, source.Path, source.StreamIndex, hardware, _cache, source.Serial,
                preferFfmpeg: _exactOnly && MfVideoDecoder.PrefersFfmpegForHevcExports);
            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            Interlocked.Add(ref _decoded, decoder.FramesDecoded);
            decoder.SetIFrames(source.IFrames);
            var retired = false;
            lock (_gate)
            {
                source.Opening = Math.Max(source.Opening - 1, 0);

                // Source retired during the open: dispose after releasing the lock, never under it.
                if (source.Retired)
                {
                    _graveyard.Add(decoder);
                    retired = true;
                }
                else
                {
                    if (source.Duration <= 0)
                        source.Duration = Math.Max(decoder.DurationSeconds - decoder.FrameDuration * 0.5, 0);

                    // Frame duration: pass a known source value to the new decoder; adopt the decoder's only when it knows it.
                    if (source.FrameDurationKnown)
                        decoder.SeedFrameDuration(source.FrameDuration);
                    else if (decoder.FrameDurationKnown)
                        (source.FrameDuration, source.FrameDurationKnown) = (decoder.FrameDuration, true);
                    else if (source.FrameDuration <= 0)
                        source.FrameDuration = decoder.FrameDuration;
                    source.FrameBytes = (long)decoder.Width * decoder.Height * 3 / 2 * (decoder.TenBit ? 2 : 1);
                    (decoder.Owner, decoder.LastUsed) = (lane, Stopwatch.GetTimestamp());
                    source.Decoders.Add(decoder);
                }
            }

            if (retired)
            {
                DisposeBuried();
                return null;
            }

            Interlocked.Increment(ref _opens);
            _arrived.Set(); // first frame is now cached
            return decoder;
        }
        catch (Exception ex) when (ex is GpuException { IsDeviceLost: false } or DllNotFoundException or EntryPointNotFoundException)
        {
            // Unreadable by MF (or no MF at all, e.g. Windows N without the Media Feature Pack): not retried until the client changes
            // the entry; solid, still and pattern layers still draw. A source already decoding elsewhere is not failed for one extra
            // open that failed (file briefly busy): opens pause a few seconds while existing decoders continue (otherwise an export
            // could silently drop the video while still counting its frames exact).
            lock (_gate)
            {
                source.Opening = Math.Max(source.Opening - 1, 0);
                if (source.Decoders.Count == 0)
                    source.Failed = true;
                else
                    source.NoOpensUntil = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
            }

            LastError = $"{Path.GetFileName(source.Path)}: {ex.Message}";
            _arrived.Set();
            return null;
        }
        catch
        {
            lock (_gate)
                source.Opening = Math.Max(source.Opening - 1, 0);
            throw;
        }
    }

    private int _opens;

    /// <summary>Decodes toward a demand frame by frame until its frame is cached, the demand is withdrawn, or something more urgent waits.</summary>
    private void Work(int lane, Demand demand, MfVideoDecoder decoder)
    {
        var source = demand.Source;
        var target = demand.Target;

        // Range already fully cached (stepped over, or a chase passed through it): nothing to do.
        if (demand.Stretch && _cache.Covers(source.Serial, demand.KeepFrom, target))
        {
            lock (_gate)
                demand.Done = true;
            return;
        }

        var seeksBefore = decoder.Seeks;
        try
        {
            WorkOn(lane, demand, decoder, source, target);
        }
        finally
        {
            Interlocked.Add(ref _seeks, decoder.Seeks - seeksBefore);
        }
    }

    private void WorkOn(int lane, Demand demand, MfVideoDecoder decoder, SourceState source, double target)
    {
        decoder.KeepingAhead = demand.Kind != DemandKind.Visible;
        WaitWhileDrawing();

        // Far cut caught by a cold-play window that opened while this lane waited: no seek; it stays a demand for a later pick (the
        // loop below yields the same way once a read is under way).
        lock (_gate)
        {
            if (HeldForColdPlay(demand))
                return;
        }

        decoder.Prepare(StartOf(demand, decoder, target));
        var frames = 0;

        // Re-seeks for this target: last seek landed after it, the reader passed its frame, or (export) the decoder returned the next
        // frame instead (reset when the demand moves).
        var (lateTries, refetches, dropTries) = (0, 0, 0);
        while (true)
        {
            WaitWhileDrawing();
            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            double keepFrom;
            var retarget = double.NaN;
            lock (_gate)
            {
                if (demand.Cancelled || source.Retired || _stopping.WaitOne(0))
                    return;

                // Scrub moved the demand: continue if forward within reach, else restart from its key frame (seek outside the
                // lock: the renderer never waits on file I/O).
                if (Math.Abs(demand.Target - target) > 1e-6)
                {
                    target = demand.Target;
                    decoder.KeepingAhead = demand.Kind != DemandKind.Visible;
                    retarget = StartOf(demand, decoder, target);
                    (lateTries, refetches, dropTries) = (0, 0, 0);
                }

                keepFrom = demand.KeepFrom;

                // Idle work yields before every frame once the player moves or plays, and after each decoded frame to anything else waiting
                // (always after its first frame: a pick that decoded nothing would just be re-picked).
                if (demand.Kind == DemandKind.Idle && (!IdleMayRunNow(countLanes: false) || frames > 0 && MoreUrgentWaiting(demand, decoder)))
                    return;

                // A far cut's read yields immediately to cold-play frames: it stays pending and resumes where it stopped once the window
                // closes or the cut comes within CutSoon.
                if (HeldForColdPlay(demand))
                    return;

                // Something more urgent that no lane serves (and this read will not pass): yield after at least a few frames so
                // nothing starves.
                if (frames >= 4 && demand.Rank > 0 && MoreUrgentWaiting(demand, decoder))
                    return;
            }

            if (!double.IsNaN(retarget))
                decoder.Prepare(retarget);

            if (!demand.Stretch && _cache.Has(source.Serial, target))
                break;

            // Wanted frame is the one the reader is on (read earlier, since evicted): reading on cannot return it, only a seek can
            // (never stretch the next frame over it).
            if (!demand.Stretch && PassedOver(decoder, target, source) && refetches++ < 2)
            {
                decoder.SeekTo(target);
                continue;
            }

            var before = decoder.ReadTime;
            if (demand.Stretch && !double.IsNaN(decoder.ReadTime) && decoder.ReadTime >= target - decoder.FrameDuration * 0.5)
                break;

            // Ranges keep frames from slightly before their start: the frame covering the start may initially end just short of it
            // (millisecond container clock: the frame for 86.2667 s is stamped 86.233 s, lasting 33.3 ms until the next read extends
            // it to 86.267 s, FrameCache.CloseGap).
            if (!decoder.Step(demand.Stretch ? keepFrom - StampHair : keepFrom, out var time, out var until))
            {
                // End of stream: the last frame covers every later time and the source ends there (container longer than its video:
                // times past the last frame resolve to it).
                if (decoder.AtEnd && !double.IsNaN(decoder.ReadTime))
                {
                    lock (_gate)
                    {
                        if (source.Duration <= 0 || decoder.ReadTime < source.Duration)
                            source.Duration = Math.Max(decoder.ReadTime, 0);
                    }
                }

                break;
            }

            frames++;
            Interlocked.Increment(ref _decoded);
            if (demand.Kind == DemandKind.Visible && until > target - 1e-6 && time <= target + 1e-6 || _waitingExact > 0 || ShownSoon(source, time, until))
                _arrived.Set();
            Pace(demand, decoder);

            // Past the target with no covering frame (FrameCache.CloseGap already joined consecutive frames):
            // - seek landed after it (coarse container index, open GOP leading pictures dropped): re-seek 1, 2, then 4 s before its
            //   key frame;
            // - else the decoder dropped a frame or two: the next frame covers the time;
            // - else a stream hole: the time is marked missing (nearest frame shown; export fails over to FFmpeg) and not retried
            //   for a while. Never stretch a key frame over undecoded frames (an export would hold each key frame).
            if (!double.IsNaN(time) && time > target + 1e-6 && !demand.Stretch && !_cache.Has(source.Serial, target))
            {
                if (decoder.LastWasLanding && lateTries < 3)
                {
                    var key = decoder.KeyAtOrBefore(target);
                    var back = 1 << lateTries++;
                    decoder.SeekTo(Math.Max((double.IsNaN(key) ? target : Math.Min(key, target)) - back, 0));
                    Interlocked.Increment(ref _lateLandings);
                    continue;
                }

                // The previous frame was the wanted one and is no longer cached (read but not kept, or evicted): re-seek. Had it
                // still been cached it would have been joined to this one (CloseGap), so this is a hole.
                var passedIt = !double.IsNaN(before) && before <= target + 1e-6 && !_cache.Has(source.Serial, before);
                if (passedIt && refetches++ < (_exactOnly ? 5 : 2))
                {
                    decoder.SeekTo(target);
                    continue;
                }

                // Export never substitutes the next frame for one it passed: that frame exists and the picture would be one frame
                // off. It stays missing and the export falls back to FFmpeg (rare "held over" in gpu-export-every-frame).
                // Nor, initially, for a frame the decoder skipped (MF HEVC on ARM64 returned the following frame twice in one export,
                // producing held frames): re-decode from a key frame 1, then 2 s further back (counted as re-seeks) so the time is read
                // within a run of frames rather than right after a seek; only then does the next frame stand in (the file lacks it).
                if (_exactOnly && !passedIt && dropTries < 2)
                {
                    var key = decoder.KeyAtOrBefore(target);
                    var back = 1 << dropTries++;
                    decoder.SeekTo(Math.Max((double.IsNaN(key) ? target : Math.Min(key, target)) - back, 0));
                    Interlocked.Increment(ref _lateLandings);
                    continue;
                }

                if (!(_exactOnly && passedIt) && _cache.StretchBack(source.Serial, time, target, decoder.FrameDuration * 2.5))
                {
                    Interlocked.Increment(ref _stretches);
                }
                else
                {
                    lock (_gate)
                        _missing.Add((source.Serial, target, Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency));
                    Interlocked.Increment(ref _missingFrames);
                }

                break;
            }

            if (frames > 20_000)
                break;
        }

        lock (_gate)
        {
            // Frame duration from observed spacing (MfVideoDecoder.LearnSpacing), once known: defines "the same frame" from now on.
            if (decoder.FrameDurationKnown)
                (source.FrameDuration, source.FrameDurationKnown) = (decoder.FrameDuration, true);
            demand.Done = true;
            if (demand.Kind == DemandKind.Visible)
            {
                var ms = (float)Stopwatch.GetElapsedTime(demand.Asked).TotalMilliseconds;
                LastExactLatencyMs = ms;
                WorstExactLatencyMs = Math.Max(WorstExactLatencyMs, ms);
            }
        }

        _arrived.Set();
    }

    /// <summary>
    /// True when the reader is on (just read) the frame covering <paramref name="target"/> and it is no longer cached, so further reads
    /// come after it. A still-cached frame knows its own end (the next read joins it, FrameCache.CloseGap).
    /// </summary>
    private bool PassedOver(MfVideoDecoder decoder, double target, SourceState source) =>
        !double.IsNaN(decoder.ReadTime) && !decoder.AtEnd && decoder.ReadTime <= target + 1e-6 && target < decoder.ReadTime + decoder.FrameDuration * 0.98
        && !_cache.Has(source.Serial, decoder.ReadTime);

    /// <summary>
    /// True when a just-decoded frame satisfies a visible demand served by another lane (read through on the way to this lane's target,
    /// ReadByAnotherLane): the renderer is woken for it as for its own.
    /// </summary>
    private bool ShownSoon(SourceState source, double time, double until)
    {
        if (double.IsNaN(time))
            return false;
        lock (_gate)
        {
            foreach (var d in _demands)
            {
                if (d.Kind == DemandKind.Visible && !d.Done && !d.Cancelled && d.Source == source && d.Target >= time - 1e-6 && d.Target < until + 1e-6)
                    return true;
            }
        }

        return false;
    }

    private bool HasPreRoll(long clipKey, SourceState source, double to)
    {
        foreach (var d in _demands)
        {
            if (d.Kind == DemandKind.PreRoll && d.ClipKey == clipKey && d.Source == source && Math.Abs(d.Target - to) < 0.25)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a more urgent unserved demand waits (under the lock), excluding ones this read will reach in this file (e.g. the visible
    /// frame it passes while reading ahead) and ones no decoder can serve (source at its limit, decoders parked where wanted: Pick skips
    /// them) that could not take this lane's decoder either: yielding would only restart this read.
    /// </summary>
    private bool MoreUrgentWaiting(Demand demand, MfVideoDecoder decoder)
    {
        foreach (var d in _demands)
        {
            if (d.Lane >= 0 || d.Done || d.Cancelled || d.Rank >= demand.Rank || d.Source.Failed)
                continue;

            // A cut held back by cold play is not waiting: no pick would take it, so nothing yields to it.
            if (HeldForColdPlay(d))
                continue;
            if (d.Source == demand.Source && Delivers(demand, decoder, d.Target))
                continue;
            if (!DecoderFor(d, out _, out _) && (d.Source != demand.Source || !MayTake(d, decoder, out _)))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Decode start for a demand: pre-roll from the first undecoded time in its range (forward from the decoder when the range is cached up
    /// to it); others from their target.
    /// </summary>
    private double StartOf(Demand demand, MfVideoDecoder decoder, double target)
    {
        if (!demand.Stretch)
            return target;
        var read = decoder.ReadTime;
        if (!double.IsNaN(read) && read > demand.KeepFrom && read < target && _cache.Covers(demand.Source.Serial, demand.KeepFrom, read))
            return Math.Min(read + decoder.FrameDuration * 0.5, target);
        return demand.KeepFrom;
    }

    /// <summary>
    /// Makes room for one more decoder: closes least recently used free decoders, those parked where wanted (visible layer, cut first frames
    /// awaiting the playhead) last (otherwise the soonest cuts' decoders, assigned first, were closed first and playback stuttered there).
    /// </summary>
    private void TrimToCapacity()
    {
        lock (_gate)
        {
            var total = _sources.Values.Sum(s => s.Decoders.Count);
            if (total >= Capacity)
            {
                var all = _sources.Values.SelectMany(s => s.Decoders.Where(d => d.Owner < 0).Select(d => (Source: s, Decoder: d))).ToList();
                foreach (var (source, decoder) in all.OrderBy(x => ParkedWhereWanted(x.Source, x.Decoder)).ThenBy(x => x.Decoder.LastUsed).Take(total - Capacity + 1))
                {
                    source.Decoders.Remove(decoder);
                    _graveyard.Add(decoder);
                }
            }
        }

        DisposeBuried();
    }

    /// <summary>True when a decoder is parked where wanted: at a visible layer's time, or at a cut's first frames awaiting the playhead (under the lock).</summary>
    private bool ParkedWhereWanted(SourceState source, MfVideoDecoder decoder) => IsParkedAtVisible(decoder) || ParkedAtAnyCut(source, decoder);

    /// <summary>True when a decoder waits at the end of the first frames of any upcoming cut of its source (under the lock; see ParkedAtCut).</summary>
    private bool ParkedAtAnyCut(SourceState source, MfVideoDecoder decoder)
    {
        var read = decoder.ReadTime;
        if (double.IsNaN(read) || decoder.AtEnd)
            return false;
        foreach (var demand in _demands)
        {
            if (demand.HintKind == GpuSeekHintKind.CutEntry && !demand.Cancelled && demand.Source == source &&
                read >= demand.KeepFrom - 1e-6 && read <= demand.Target + decoder.FrameDuration * 1.5)
                return true;
        }

        return false;
    }

    // Decoders released under the lock and disposed after it (reader teardown enters the media pipeline: never under _gate).
    private readonly List<MfVideoDecoder> _graveyard = [];

    /// <summary>Moves a source's idle decoders to the burial list for disposal after the lock (under the lock).</summary>
    private void BuryIdle(SourceState source)
    {
        for (var i = source.Decoders.Count - 1; i >= 0; i--)
        {
            if (source.Decoders[i].Owner < 0)
            {
                _graveyard.Add(source.Decoders[i]);
                source.Decoders.RemoveAt(i);
            }
        }
    }

    /// <summary>Disposes buried decoders (outside the lock).</summary>
    private void DisposeBuried()
    {
        MfVideoDecoder[] buried;
        lock (_gate)
        {
            if (_graveyard.Count == 0)
                return;
            buried = [.. _graveyard];
            _graveyard.Clear();
        }

        foreach (var decoder in buried)
            decoder.Dispose();
    }
}
