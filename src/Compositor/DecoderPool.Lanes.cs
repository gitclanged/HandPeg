using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

// The decode lanes of the DecoderPool: what each lane picks next (MostUrgent), which decoder serves it (DecoderFor, OpenFor),
// the decoding itself (WorkOn), and the idle decoders buried and disposed.
internal sealed partial class DecoderPool
{
    // ----- The lanes -----

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

                    // Idle work opens no decoder once the player has moved meanwhile (5.4.4): the open it counted on is undone.
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

                    // Nor for a far cut a cold play's window opened on while this lane waited for the draw (5.5A review): no decoder opened
                    // and none sought for it. The open it counted on is undone and the demand stays (not done), picked again once the window
                    // closes or the cut comes within the cold play's lead.
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
                // A file that fails part way (a broken stream): its decoder goes; the source is tried again from scratch,
                // that stretch of it only after three seconds.
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

                    // A pre-roll or a hint met stays (done): its stretch stays one of the cache's interests, so what was
                    // decoded ahead is still there when it is asked for (the cuts' frames, until their hints go).
                    if (demand.Cancelled || demand.Done && demand.Kind is not (DemandKind.PreRoll or DemandKind.Hinted or DemandKind.Hover or DemandKind.Idle))
                        _demands.Remove(demand);
                    if (demand.Source.Retired)
                        BuryIdle(demand.Source);
                }

                // A source retired while this lane read it (a tab put behind): its decoder is disposed now, on this thread, outside the lock
                // (5.4.4: never left open for Tidy's next look).
                if (demand.Source.Retired)
                    DisposeBuried();
            }
        }
    }

    // Picks made (under the lock): a demand no decoder can come for in a pick is passed over for the rest of it (Demand.PassedIn).
    private long _picks;

    /// <summary>
    /// The most urgent demand no lane works on and whose frame is not there yet, with the decoder to work it with (the one
    /// of its source that reaches it in the fewest frames, not one another lane holds; null: open one). One no decoder can
    /// come for now (its source has as many as it may, each held by a lane or kept where it is wanted) is passed over for
    /// the next most urgent (5.0-Release review: a seventh cut of one file within the horizon left the lanes idle, and the
    /// pre-roll, the pointer's work and other files' cuts were never decoded while paused). Under the lock.
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
    /// How many lanes work that can wait may take at once: two, or one for each video on screen; never more than two on unified memory
    /// (5.4.1-M4), where every speculative frame is the computer's memory and its bandwidth.
    /// </summary>
    private int BackgroundLanes => _gpu.UnifiedMemory ? UnifiedBackgroundLanes : Math.Clamp(_videosShown, 2, Math.Max(_lanes.Count - 1, 2));

    private const int UnifiedBackgroundLanes = 2;

    private int _videosShown = 1;

    // How many lanes are on work that can wait (under the lock).
    private int BackgroundBusy()
    {
        var busy = 0;
        foreach (var demand in _demands)
            busy += demand.Lane >= 0 && !demand.Done && demand.Kind is DemandKind.PreRoll or DemandKind.Hover or DemandKind.Hinted or DemandKind.Idle ? 1 : 0;
        return busy;
    }

    /// <summary>The most urgent demand to work now, not one passed over in this pick (under the lock).</summary>
    private Demand? MostUrgent(long pick)
    {
        Demand? best = null;
        bool? idleMayRun = null;
        foreach (var demand in _demands)
        {
            if (demand.Lane >= 0 || demand.Cancelled || demand.Done || demand.Source.Failed || demand.Source.Retired || demand.PassedIn == pick)
                continue;

            // Idle work (5.4.4: a tab's pre-roll, a drop's file) only while the player is quiet: never while playing or scrubbing, while a
            // frame shown or played ahead is still to decode, or within IdleQuietMs of the playhead's last move.
            if (demand.Kind == DemandKind.Idle && !(idleMayRun ??= IdleMayRunNow(countLanes: true)))
                continue;

            // Cold play (5.5A, the user's Q4 b): for ColdPlaySeconds after a cold start, a cut more than CutSoon ahead waits; it stays
            // a demand (no memory taken) and is read after, earliest first.
            if (HeldForColdPlay(demand))
                continue;

            // The most urgent kind first; among hints of one kind, the one wanted soonest (the earliest cut); then the oldest.
            if (best is not null && !(demand.Rank < best.Rank || demand.Rank == best.Rank && (demand.At < best.At || demand.At == best.At && demand.Asked < best.Asked)))
                continue;

            // Its frame is there already (another lane passed through it), or a stretch's whole stretch is: nothing to
            // decode, and no decoder opened for it (a pre-roll is never given the decoder parked at the playhead).
            if (!demand.Stretch ? _cache.Has(demand.Source.Serial, demand.Target) : _cache.Covers(demand.Source.Serial, demand.KeepFrom, demand.Target))
            {
                demand.Done = true;
                continue;
            }

            // Another lane reads this file forward through it (or up to just short of it): one reader per stretch of a
            // file, never a second decoder sought to the same key frame to decode the same frames (5.0-Verify).
            if (ReadByAnotherLane(demand) is not null)
                continue;

            // Playing, a file the playback reads forward is not opened or sought a second time for work that can wait until its
            // moment is within CutSoon (5.4.3, the XPS 13's Overwatch freeze: five layers of one file, a lookahead decoder sought
            // at every one-second key frame, and the Adreno's decoder lock held up the playing one for 40-100 ms each time).
            if (_lastWasPlaying && demand.Kind is DemandKind.PreRoll or DemandKind.Hover or DemandKind.Hinted &&
                !(demand.At - _lastPlayhead <= CutSoon) && PlaybackReads(demand.Source))
                continue;

            // The pre-roll and the pointer's work wait while a frame on screen is being chased (and, paused, the cuts'): the
            // card's decoder is one, and a second stream on it halves the chase (SeekBench: cold seeks a third slower with
            // the pre-roll beside them). Playing, a cut ahead is not held up by one late frame.
            if ((demand.Kind is DemandKind.PreRoll or DemandKind.Hover || demand.HintKind == GpuSeekHintKind.CutEntry && !_lastWasPlaying) && VisibleBeingDecoded())
                continue;

            // The lanes beyond two are for frames due on screen (5.1-TrackEqualityAndVram): a card's decoder is one, and work that
            // can wait must not take them from a frame that cannot (SeekBench: 6 lanes and a wider pre-roll made 1 frame in 1000 late).
            if (demand.Kind is DemandKind.PreRoll or DemandKind.Hover or DemandKind.Hinted or DemandKind.Idle && BackgroundBusy() >= BackgroundLanes)
                continue;
            best = demand;
        }

        return best;
    }

    /// <summary>
    /// The decoder a demand would be worked with now (under the lock): the free one of its source that reaches it in the
    /// fewest frames and may be taken for it (<paramref name="choice"/>), or one more to be opened (<paramref name="open"/>).
    /// False when none can come: its source has as many as it may, each held by a lane or kept where it is wanted.
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

        // No decoder free, or only those parked at a visible layer's moment or a cut: one more, while the source has room
        // (opening costs a file open, on this lane's thread; the cuts ahead may have a few more). With the pool full an open
        // closes another (TrimToCapacity): the pointer's work never opens one then, and the other work ahead only when one
        // parked nowhere it is wanted can go (never the playhead's, or a sooner cut's: those wait for it).
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
    /// Whether a free decoder may be taken for a demand, and the cost added for taking it (1e6: only when no other can
    /// come). Under the lock.
    /// </summary>
    private bool MayTake(Demand best, MfVideoDecoder decoder, out double penalty)
    {
        // For a hint or the pre-roll, a decoder that goes on forward from exactly there (its stretch decoded up to the
        // decoder already) is the one: another would seek back to the same key frame and decode the same frames again.
        penalty = 0;
        if (!(IsHint(best.Kind) || best.Kind == DemandKind.PreRoll) || best.Stretch && ContinuesStretch(best, decoder))
            return true;

        // Idle work (5.4.4) never takes a decoder parked at a visible layer's moment (playing or paused); one at a cut is refused below.
        if (best.Kind == DemandKind.Idle && IsParkedAtVisible(decoder))
            return false;

        // One parked at a visible layer's moment is never taken from it while playing (the frames shown next come from it:
        // taking it would make playback chase), nor for a cut ahead (it opens one of its own, or waits: the playhead's own
        // stretch is what Space plays first); paused, for the others only when no other can come (5.0-Release: with every
        // decoder open, a cut's hint took the playing one, and playback stuttered at every new cut).
        if (IsParkedAtVisible(decoder))
        {
            if (_lastWasPlaying || best.HintKind == GpuSeekHintKind.CutEntry)
                return false;
            penalty = 1e6;
        }

        // One parked at a cut's first frames waits there for the playhead (it reads on from there at real time): never
        // taken for the pointer, the pre-roll or a later cut; for an earlier cut, only when no other can come.
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

    /// <summary>The decoders open and being opened, every source's (under the lock).</summary>
    private int OpenDecoders()
    {
        var open = 0;
        foreach (var source in _sources.Values)
            open += source.Decoders.Count + source.Opening;
        return open;
    }

    /// <summary>Whether a free decoder of any source is parked nowhere it is wanted (TrimToCapacity closes such first; under the lock).</summary>
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

    /// <summary>Whether a decoder stands inside a stretch with all of it up to there decoded: the stretch goes on from it (StartOf).</summary>
    private bool ContinuesStretch(Demand stretch, MfVideoDecoder decoder)
    {
        var read = decoder.ReadTime;
        return !double.IsNaN(read) && !decoder.AtEnd && read > stretch.KeepFrom && read < stretch.Target && _cache.Covers(stretch.Source.Serial, stretch.KeepFrom, read);
    }

    /// <summary>
    /// Whether a decoder waits at the end of a cut's first frames for the playhead (under the lock): 0 not; 1 taken for
    /// <paramref name="best"/> only when no other decoder can come (a scrub's heading, or a cut wanted sooner than the one it
    /// waits at); 2 never taken for it (the pointer's work, the pre-roll, a cut wanted later or as soon).
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

    // The share of the cache's frames a stretch of one source may take: what is kept behind a frame asked for, the paused
    // pre-roll round it, what is read ahead while playing, a cut's first frames, the pointer's play-ahead.
    private const double BehindShare = 0.2, PreRollShare = 0.4, AheadShare = 0.25, CutShare = 0.05, HoverShare = 0.1;

    /// <summary>
    /// A stretch of a source, in seconds, held to the share of the cache's budget its frames may take (5.0-Verify: a 420 fps
    /// 1440p recording's 3 s pre-roll is 1260 frames, more than a 3 GB cache holds: decoded, let go and decoded again).
    /// </summary>
    private double Fit(SourceState source, double seconds, double share)
    {
        if (source.FrameBytes <= 0 || source.FrameDuration <= 0)
            return seconds;
        var frames = _cache.BudgetBytes * share / source.FrameBytes;
        return Math.Min(seconds, Math.Max(frames, 8) * source.FrameDuration);
    }

    /// <summary>Whether a lane is decoding toward a frame on screen now (under the lock).</summary>
    private bool VisibleBeingDecoded()
    {
        foreach (var demand in _demands)
        {
            if (demand.Lane >= 0 && demand.Kind == DemandKind.Visible && !demand.Cancelled)
                return true;
        }

        return false;
    }

    /// <summary>How soon (sequence seconds) a cut into a file the playback reads may have a decoder opened or sought for it.</summary>
    internal const double CutSoon = 1.5;

    /// <summary>Whether the playback reads a file now: a lane on a frame shown or played ahead, or a decoder parked at one (under the lock).</summary>
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

    /// <summary>How far past a reader's own target a frame of its file is left to it (it is taken next, from where it stopped).</summary>
    private const double ReaderReach = 1.0;

    /// <summary>
    /// The demand of another lane whose decoder reads <paramref name="demand"/>'s file forward to, through, or up to a
    /// second short of its frame (under the lock); null when none does. A moment behind every reader, or farther ahead, gets
    /// a decoder of its own (two moments of one file: a crossfade's head, a cut far into it).
    /// </summary>
    private Demand? ReadByAnotherLane(Demand demand)
    {
        // A hint's stretch (a cut's first frames, the pointer's play-ahead) waits for a reader that passes its first frame.
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
    /// Whether a lane's read (its demand <paramref name="read"/>, its decoder <paramref name="reader"/>) will hand over the
    /// frame at <paramref name="target"/> sooner than a chase of its own would (under the lock): the frame lies ahead of the
    /// reader (or of the key frame it is landing on), no farther than the read goes, among the frames it keeps, and the
    /// frames still to read before it are no more than a seek to the key frame before it and the chase from there (5.0-Verify
    /// review: a click past a pre-roll's end must not wait for it).
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

    /// <summary>Opens one more decoder for a source (on the lane's thread: a file open never holds up a frame).</summary>
    private MfVideoDecoder? OpenFor(SourceState source, int lane)
    {
        TrimToCapacity();
        try
        {
            // Opening a file on a sleeping disk or a share may take long: not the stuck-lane watchdog's business.
            Volatile.Write(ref _laneBusySince[lane], 0);
            // On the card unless there is none (WARP) or the client asks for the processor (a driver that shows broken frames).
            var hardware = !_gpu.IsWarp && Volatile.Read(ref _shared.Control.SoftwareDecodeRequest) == 0;
            // An export on an ARM64 PC decodes HEVC with FFmpeg, the faster there (5.2C, the user's Q1: MfVideoDecoder.PreferFfmpeg).
            var decoder = new MfVideoDecoder(_gpu, source.Path, source.StreamIndex, hardware, _cache, source.Serial,
                preferFfmpeg: _exactOnly && MfVideoDecoder.PrefersFfmpegForHevcExports);
            Volatile.Write(ref _laneBusySince[lane], Stopwatch.GetTimestamp());
            Interlocked.Add(ref _decoded, decoder.FramesDecoded);
            decoder.SetIFrames(source.IFrames);
            var retired = false;
            lock (_gate)
            {
                source.Opening = Math.Max(source.Opening - 1, 0);

                // Its source retired while it opened: disposed after the lock, never under it (5.4.4).
                if (source.Retired)
                {
                    _graveyard.Add(decoder);
                    retired = true;
                }
                else
                {
                    if (source.Duration <= 0)
                        source.Duration = Math.Max(decoder.DurationSeconds - decoder.FrameDuration * 0.5, 0);

                    // The source's frame length: a known one is handed to the new decoder; the decoder's is taken only when known.
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
            _arrived.Set(); // its first frame is in the cache now
            return decoder;
        }
        catch (Exception ex) when (ex is GpuException { IsDeviceLost: false } or DllNotFoundException or EntryPointNotFoundException)
        {
            // A file Media Foundation cannot read (or no Media Foundation at all: a Windows N edition without its Media
            // Feature Pack): not tried again until the client changes the entry. Solid, still and pattern layers still draw.
            // A source that already decodes with another decoder is not failed for one more that would not open (a file
            // busy for a moment): no more opens of it for a few seconds, its open decoders go on (5.0-Verify review: an
            // export would otherwise leave the main video out from there on, its frames still counted exact).
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

    /// <summary>Decodes toward a demand, frame by frame, until its frame is there, it is taken back, or something more urgent waits.</summary>
    private void Work(int lane, Demand demand, MfVideoDecoder decoder)
    {
        var source = demand.Source;
        var target = demand.Target;

        // A stretch that is all there already (frames stepped over, a chase that passed through it): nothing to do.
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

        // A far cut a cold play's window opened on while this lane waited (5.5A review): no seek for it; it stays a demand, taken up
        // again by a later pick (the loop below gives way the same way once a read is under way).
        lock (_gate)
        {
            if (HeldForColdPlay(demand))
                return;
        }

        decoder.Prepare(StartOf(demand, decoder, target));
        var frames = 0;

        // Seeks made again for this target because the last one landed after it, because the reader had passed its frame, or (an
        // export) because the decoder gave the frame after it and not it (reset when the demand moves).
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

                // A scrub moved the demand: forward within reach goes on, anything else starts again from its key frame
                // (the seek itself made outside the lock: the renderer never waits on a file).
                if (Math.Abs(demand.Target - target) > 1e-6)
                {
                    target = demand.Target;
                    decoder.KeepingAhead = demand.Kind != DemandKind.Visible;
                    retarget = StartOf(demand, decoder, target);
                    (lateTries, refetches, dropTries) = (0, 0, 0);
                }

                keepFrom = demand.KeepFrom;

                // Idle work (5.4.4) gives way before every frame once the player moved or plays, and after each frame it decoded to anything
                // else waiting (its first frame always: a pick that decoded nothing would only be picked again).
                if (demand.Kind == DemandKind.Idle && (!IdleMayRunNow(countLanes: false) || frames > 0 && MoreUrgentWaiting(demand, decoder)))
                    return;

                // A far cut's read gives way at once to a cold play's own frames (5.5A): it stays a demand, not done, and is taken up
                // again where it stopped once the window closes or the cut comes within CutSoon.
                if (HeldForColdPlay(demand))
                    return;

                // Something more urgent that no lane serves (and that this read does not pass on its way): give way, after
                // at least a few frames, so nothing starves.
                if (frames >= 4 && demand.Rank > 0 && MoreUrgentWaiting(demand, decoder))
                    return;
            }

            if (!double.IsNaN(retarget))
                decoder.Prepare(retarget);

            if (!demand.Stretch && _cache.Has(source.Serial, target))
                break;

            // The frame wanted is the one the reader stands on (read before, then let go of by the cache): reading on can
            // never give it back, only a seek can (never the frame after it stretched over it).
            if (!demand.Stretch && PassedOver(decoder, target, source) && refetches++ < 2)
            {
                decoder.SeekTo(target);
                continue;
            }

            var before = decoder.ReadTime;
            if (demand.Stretch && !double.IsNaN(decoder.ReadTime) && decoder.ReadTime >= target - decoder.FrameDuration * 0.5)
                break;

            // A stretch keeps its frames from a hair before its first moment: the frame that shows that moment may end, as
            // first read, a hair short of it (a container's millisecond clock: the frame of 86.2667 s is stamped 86.233 s
            // and lasts 33.3 ms until the next one read says it reaches 86.267 s, FrameCache.CloseGap).
            if (!decoder.Step(demand.Stretch ? keepFrom - StampHair : keepFrom, out var time, out var until))
            {
                // The end of the stream: the last frame stands for every moment past it, and the source ends there (a
                // container longer than its video: a moment past the last frame is asked for as the last frame).
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
            if (Paced(demand, decoder))
                WaitForNextDraw();

            // Past the target without a frame that covers it (FrameCache.CloseGap already joined frames read in a row):
            // - a seek that landed after it (a container's coarse index, an open GOP whose first pictures the decoder
            //   drops): sought again from further back, 1, 2 then 4 s before its key frame;
            // - else a frame or two the decoder dropped: the frame after stands for the moment;
            // - else a hole in the stream: the moment is missing (shown with the nearest frame; an export fails on it and
            //   is made by FFmpeg) and is not decoded for again for a while. Never a key frame stretched over frames
            //   nothing decoded (5.0-Verify: the user's export held each key frame).
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

                // The frame before this one was the one wanted and the cache no longer has it (it was read but not kept, or
                // let go of since): sought again. One still there would have been joined to this one (CloseGap): a hole.
                var passedIt = !double.IsNaN(before) && before <= target + 1e-6 && !_cache.Has(source.Serial, before);
                if (passedIt && refetches++ < (_exactOnly ? 5 : 2))
                {
                    decoder.SeekTo(target);
                    continue;
                }

                // An export never takes the next frame for one it passed: that frame exists, and the picture would be a frame
                // out. It stays missing, the export fails on it, and FFmpeg makes it (the once-in-many-runs "held over" of
                // gpu-export-every-frame, 5.1-TrackEqualityAndVram).
                // Nor, at first, for one the decoder did not give (5.2C, the user's Q1: Media Foundation's HEVC on the Snapdragon gave
                // the frame after a moment for it, twice in one game-style export, and that frame stood for it: two held frames, counted
                // as stretches, no late landing): decoded again from a key frame 1, then 2 s further back (counted with the late landings: seeks
                // made again from further back), so the moment is read in a run of frames rather than just after a seek; only then does
                // the frame after stand for it (a frame the file lacks).
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
            // The frame length as the frames space it (MfVideoDecoder.LearnSpacing), once known: what "the same frame" means from now on.
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
    /// Whether the reader stands on (has just read) the frame that shows <paramref name="target"/>, and that frame is no
    /// longer in the cache: the next frame read comes after it. A frame still there says itself where it ends (and the next
    /// frame read joins it, FrameCache.CloseGap).
    /// </summary>
    private bool PassedOver(MfVideoDecoder decoder, double target, SourceState source) =>
        !double.IsNaN(decoder.ReadTime) && !decoder.AtEnd && decoder.ReadTime <= target + 1e-6 && target < decoder.ReadTime + decoder.FrameDuration * 0.98
        && !_cache.Has(source.Serial, decoder.ReadTime);

    /// <summary>
    /// Whether a frame just decoded is one a visible demand waits for that this lane does not serve (one it reads through
    /// on its way to its own target, ReadByAnotherLane): the renderer is woken for it as for its own.
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
    /// Whether a more urgent demand waits that no lane serves (under the lock): not one of this file that this lane's
    /// read reaches on its way (the frame shown while it reads ahead is one it passes), nor one no decoder can come for
    /// (its source at its most, each decoder kept where it is wanted: Pick passes it over) that could not take this
    /// lane's either: giving way would only start this read again.
    /// </summary>
    private bool MoreUrgentWaiting(Demand demand, MfVideoDecoder decoder)
    {
        foreach (var d in _demands)
        {
            if (d.Lane >= 0 || d.Done || d.Cancelled || d.Rank >= demand.Rank || d.Source.Failed)
                continue;

            // A cut a cold play holds back (5.5A) is not waiting: no pick would take it, so nothing gives way to it.
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
    /// Where a lane starts decoding for a demand: the pre-roll from the first moment of its stretch not decoded yet
    /// (forward from the decoder when the cache already holds the stretch up to it), the others from their target.
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
    /// Makes room for one more decoder in the pool: the free ones used longest ago are closed, those parked where they are
    /// wanted (at a visible layer's moment, at a cut's first frames waiting for the playhead) last of all (5.0-Release
    /// review: the decoders of the soonest cuts, assigned first, were the first to go, and Space stuttered at them).
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

    /// <summary>Whether a decoder is parked where it is wanted: at a visible layer's moment, or at a cut's first frames waiting for the playhead (under the lock).</summary>
    private bool ParkedWhereWanted(SourceState source, MfVideoDecoder decoder) => IsParkedAtVisible(decoder) || ParkedAtAnyCut(source, decoder);

    /// <summary>Whether a decoder waits at the end of the first frames of any cut ahead of its source (under the lock; see ParkedAtCut).</summary>
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

    // Decoders let go of under the lock and disposed after it (a reader's teardown is the media pipeline's: never under _gate).
    private readonly List<MfVideoDecoder> _graveyard = [];

    /// <summary>A source's idle decoders out of its list, to be disposed after the lock (under the lock).</summary>
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

    /// <summary>Disposes the decoders let go of (outside the lock).</summary>
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
