using HandPegApp.Compositor.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// One decoded picture kept on the card (Instant Seek, Pass 5.0-AudioAnd3D): an NV12 / P010 texture (hardware) or a
/// luma and a chroma plane (the processor's decode), the moment it covers, and how it is read. Immutable once in the
/// cache: a decoder never writes into a frame the cache holds, so the renderer can read one while another is decoded.
/// </summary>
internal sealed class CachedFrame
{
    public int Source;
    public double Time;
    public double Until;

    /// <summary>The frame a seek landed on (a key frame): kept longer, it is what a fast scrub shows.</summary>
    public bool KeyFrame;

    /// <summary>Decoded ahead of being asked for (a hint, the pre-roll, play-ahead): counted when it is then shown.</summary>
    public bool Ahead;

    public GpuTexture? Planar;
    public GpuTexture? Luma;
    public GpuTexture? Chroma;
    public nint LumaSrv;
    public nint ChromaSrv;
    public int Width;
    public int Height;
    public bool TenBit;
    public bool Software;
    public int Matrix = 1;
    public bool FullRange;

    /// <summary>The source's transfer function (MfVideoDecoder.Transfer): 15 PQ, 16 HLG; anything else SDR.</summary>
    public int Transfer;
    public long Bytes;
    public long LastUsed;

    /// <summary>Renders using it right now (the renderer pins what it draws until its frame is submitted).</summary>
    public int Pins;

    /// <summary>
    /// Whether <see cref="Until"/> is known for sure: the next frame of its run was read (FrameCache.CloseGap), FFmpeg gave its
    /// length to the next picture, or the stream ended after it. Open, the end is the container's word for it (a sample's duration).
    /// </summary>
    public bool Closed;

    /// <summary>
    /// Where the frame stops standing for moments. An open frame leaves its last millisecond to the frame after it (Pass
    /// 5.2D): a container's clock rounds stamps to a millisecond, so the next frame may start before this
    /// one's duration ends (Matroska at 420 fps: frames stamped 10.686 and 10.688 s, each said to last 2.38 ms), and a moment
    /// there is the next frame's. An exact moment there waits for that frame to be read instead of taking this one (the
    /// reference's frame 316 of the game style on Media Foundation's HEVC was the frame before); the player, which shows what
    /// there is as it plays, still takes it (<see cref="Covers"/>, not strictly).
    /// </summary>
    public double CoverEnd => Closed ? Until : Math.Max(Until - OpenSlack, Time + (Until - Time) / 2);

    /// <summary>How much of an open frame's end is left to the frame after it (seconds): a container's millisecond clock.</summary>
    public const double OpenSlack = 0.001;

    /// <summary>Whether the frame stands for <paramref name="seconds"/>: strictly, to <see cref="CoverEnd"/>; else to its stated end.</summary>
    public bool Covers(double seconds, bool strictly = true) => seconds >= Time - 1e-6 && seconds < (strictly ? CoverEnd : Until) - 1e-6;

    public void Dispose()
    {
        if (Planar is not null)
        {
            Com.Release(ref LumaSrv);
            Com.Release(ref ChromaSrv);
        }
        else
        {
            (LumaSrv, ChromaSrv) = (0, 0);
        }

        Planar?.Dispose();
        Luma?.Dispose();
        Chroma?.Dispose();
        (Planar, Luma, Chroma) = (null, null, null);
    }
}

/// <summary>
/// Every frame the decoders produce, kept in video memory (Instant Seek, Pass 5.0-AudioAnd3D): a frame stepped back to, a
/// scrub that passes over a stretch again, a seek into anything decoded once costs no decode at all. Frames of one source
/// are kept in time order (binary search). The budget is the client's (<see cref="Services.ControlBlock.CacheBudgetRequest"/>).
/// Automatic, it is game-aware (5.1-TrackEqualityAndVram, the user's directive): with the card free, half of the memory Windows offers
/// beyond what this process holds otherwise, at most 12 GB, always leaving a margin free (a third of the card, at most 8 GB: on a 24 GB
/// card about 11 GB of cache and 8 GB untouched); with something else eating the card (Windows' budget for this process well under
/// the card's size: a game), the small cache of old, an eighth of the card, at most 3 GB. Whichever it is, never more than what
/// Windows lets this process have beside everything else it holds (IDXGIAdapter3::QueryVideoMemoryInfo, asked twice a second): when a
/// game starts, the cache shrinks at once. A card that shares the computer's memory (a unified-memory SoC: 5.2-Arm64AndNpu) takes a
/// share of the computer's memory instead, 1.5 to 2.5 GB from 12 GB up (<see cref="MemoryTier"/>, which holds the whole Auto rule).
/// Beyond the budget, the frames farthest from every moment of interest (the frames shown, the hints, the pre-roll) go first, key
/// frames last, and the held stretches (the first frames of the cuts ahead of the playhead, 5.0-Release) after everything else.
/// Textures are reused by size and format. The budget is a ceiling, never a reservation: the cache holds only frames decoded, so an
/// idle editor holds none of it.
/// <para>Threads: the decode lanes insert, the renderer looks up and pins; one lock, held for microseconds.</para>
/// </summary>
internal sealed unsafe class FrameCache : IDisposable
{
    private const long MinBudget = 96L << 20;
    private const long WindowsHeadroom = 256L << 20;

    // The share of Windows' budget for this process below which something else is taken to be eating the card (the rest of the
    // Auto rule is MemoryTier's).
    private const double FreeCardShare = 0.7;

    // Video memory is charged to Windows' commit (RAM + page file) as well as to the card (5.1-DogfoodPolish: a 12 GB cache, a game and
    // a 4 GB page file ran a 52 GB limit out on a 24 GB card; the worker, then the editor's own windows, died of it). The least commit
    // the cache leaves everything else, as a share of the limit.
    private const long LeastCommitLeft = 3L << 30, MostCommitLeft = 8L << 30;
    private const int CommitShare = 8;

    private readonly GpuDevice _gpu;
    private readonly object _gate = new();
    private readonly Dictionary<int, List<CachedFrame>> _bySource = [];
    private readonly List<CachedFrame> _free = [];
    private readonly List<(int Source, double Seconds, double Weight)> _interests = [];
    private readonly List<(int Source, double From, double To)> _held = [];
    private readonly long _dedicated;

    // The card shares the computer's memory (GpuDevice.UnifiedMemory), and how much the computer has: the Auto rule's own.
    private readonly bool _unified;
    private readonly long _physical;
    private nint _adapter3;
    private long _requested;
    private long _budget;
    private long _windowsRoom = long.MaxValue;
    private long _commitRoom = long.MaxValue;

    // What Windows offers beyond what this process holds otherwise, as last asked; 0 until it has been asked.
    private long _freeOnCard;
    private bool _cardBusy;
    private long _bytes;
    private long _freeBytes;
    private long _use;
    private long _nextBudgetCheck;
    private bool _disabled;

    public FrameCache(GpuDevice gpu)
    {
        _gpu = gpu;
        var adapter = Dxgi.AdapterOf(gpu.DevicePointer);
        if (adapter != 0)
        {
            try
            {
                _dedicated = (long)Dxgi.Describe(adapter).DedicatedVideoMemory;
                _adapter3 = Com.Query(adapter, Iid.DxgiAdapter3);
            }
            finally
            {
                Com.Release(ref adapter);
            }
        }

        (_unified, _physical) = (gpu.UnifiedMemory, SystemCommit.PhysicalMemory());
        SetBudget(0);
    }

    /// <summary>The budget in force (bytes): the client's, held under what Windows gives this process.</summary>
    public long BudgetBytes => Volatile.Read(ref _budget);

    public long Bytes => Volatile.Read(ref _bytes);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var list in _bySource.Values)
                    count += list.Count;
                return count;
            }
        }
    }

    public long Evictions { get; private set; }

    /// <summary>False when the client turned the cache off: it then keeps the least it can (<see cref="MinBudget"/>).</summary>
    public bool Enabled => !_disabled;

    /// <summary>The client's request: 0 automatic, -1 off, else bytes.</summary>
    public void SetBudget(long requested)
    {
        lock (_gate)
        {
            _requested = requested;
            _disabled = requested < 0;
            SetBudgetLocked();
        }
    }

    /// <summary>What the cache may hold of Windows' commit: what it holds now plus what is left, less the margin it leaves everything else.</summary>
    /// <param name="held">What the cache holds now, which it could give back.</param>
    /// <param name="commitLeft">The commit Windows has left, bytes.</param>
    /// <param name="commitLimit">Its limit: RAM and page file.</param>
    public static long CommitRoom(long held, long commitLeft, long commitLimit) =>
        Math.Max(held + commitLeft - Math.Clamp(commitLimit / CommitShare, LeastCommitLeft, MostCommitLeft), 0);

    /// <summary>
    /// Asks Windows how much video memory it lets this process use, and how much commit is left, at most twice a second; shrinks the cache
    /// when either fell. The worker's every turn asks, drawing or not: a game started while the editor sits paused is what it is there for.
    /// </summary>
    public void CheckWindowsBudget(long nowTicks)
    {
        if (nowTicks < Volatile.Read(ref _nextBudgetCheck))
            return;
        Volatile.Write(ref _nextBudgetCheck, nowTicks + System.Diagnostics.Stopwatch.Frequency / 2);
        CheckCommit();
        if (_adapter3 == 0 || !Dxgi.QueryVideoMemory(_adapter3, out var info))
            return;

        // What the cache may hold: Windows' budget less everything else this process uses, less some room to spare.
        var others = (long)info.CurrentUsage - Volatile.Read(ref _bytes) - Volatile.Read(ref _freeBytes);
        var room = Math.Max((long)info.Budget - Math.Max(others, 0) - WindowsHeadroom, 0);
        var busy = _dedicated > 0 && (long)info.Budget < _dedicated * FreeCardShare;
        var free = Math.Max((long)info.Budget - Math.Max(others, 0), 0);
        lock (_gate)
        {
            if (Math.Abs(room - _windowsRoom) < (16L << 20) && busy == _cardBusy && Math.Abs(free - _freeOnCard) < (64L << 20))
                return;
            (_windowsRoom, _cardBusy, _freeOnCard) = (room, busy, free);
            SetBudgetLocked();
        }
    }

    private void CheckCommit()
    {
        if (!SystemCommit.TryRead(out var left, out var limit))
            return;

        lock (_gate)
        {
            var room = CommitRoom(_bytes + _freeBytes, left, limit);
            if (Math.Abs(room - _commitRoom) < (64L << 20))
                return;
            _commitRoom = room;
            SetBudgetLocked();
        }
    }

    private void SetBudgetLocked()
    {
        // Off is the least there can be: the frames shown and a few round them (with less, two layers of one file at two
        // moments would take each other's frames away for ever).
        var requested = _requested;
        var wanted = _disabled ? MinBudget
            : requested > 0 ? requested
            : MemoryTier.AutoBudget(_gpu.IsWarp, _unified, _dedicated, _physical, _cardBusy, _freeOnCard);
        _budget = Math.Max(Math.Min(Math.Min(wanted, _windowsRoom), _commitRoom), MinBudget);
        TrimLocked();
    }

    // ----- Frames in and out -----

    /// <summary>A frame of this size and kind with its textures (reused when one is free), not in the cache yet.</summary>
    public CachedFrame Rent(int width, int height, bool tenBit, bool software)
    {
        lock (_gate)
        {
            for (var i = _free.Count - 1; i >= 0; i--)
            {
                var free = _free[i];
                if (free.Width == width && free.Height == height && free.TenBit == tenBit && free.Software == software)
                {
                    _free.RemoveAt(i);
                    _freeBytes -= free.Bytes;
                    free.KeyFrame = free.Ahead = false;
                    free.Pins = 0;
                    return free;
                }
            }
        }

        var frame = new CachedFrame { Width = width, Height = height, TenBit = tenBit, Software = software };
        if (!software)
        {
            frame.Planar = _gpu.CreateTexture(width, height, tenBit ? DxgiFormat.P010 : DxgiFormat.Nv12, renderTarget: false);
            var luma = new ShaderResourceViewDesc { Format = tenBit ? DxgiFormat.R16Unorm : DxgiFormat.R8Unorm, ViewDimension = D3D11.SrvTexture2D, U0 = 0, U1 = 1 };
            var chroma = new ShaderResourceViewDesc { Format = tenBit ? DxgiFormat.R16G16Unorm : DxgiFormat.R8G8Unorm, ViewDimension = D3D11.SrvTexture2D, U0 = 0, U1 = 1 };
            frame.LumaSrv = _gpu.Device.CreateShaderResourceView(frame.Planar.Texture, &luma);
            frame.ChromaSrv = _gpu.Device.CreateShaderResourceView(frame.Planar.Texture, &chroma);
        }
        else
        {
            frame.Luma = _gpu.CreateTexture(width, height, tenBit ? DxgiFormat.R16Unorm : DxgiFormat.R8Unorm, renderTarget: false);
            frame.Chroma = _gpu.CreateTexture(width / 2, height / 2, tenBit ? DxgiFormat.R16G16Unorm : DxgiFormat.R8G8Unorm, renderTarget: false);
            (frame.LumaSrv, frame.ChromaSrv) = (frame.Luma.Srv, frame.Chroma.Srv);
        }

        frame.Bytes = (long)width * height * 3 / 2 * (tenBit ? 2 : 1);
        return frame;
    }

    /// <summary>A rented frame that was not kept goes back to the free list.</summary>
    public void Return(CachedFrame frame)
    {
        lock (_gate)
            FreeLocked(frame);
    }

    /// <summary>Keeps a decoded frame (a frame of the same moment already there is replaced); trims to the budget.</summary>
    public void Insert(CachedFrame frame)
    {
        lock (_gate)
        {
            frame.LastUsed = ++_use;
            if (!_bySource.TryGetValue(frame.Source, out var list))
                _bySource[frame.Source] = list = [];

            var at = IndexAtOrAfter(list, frame.Time - 1e-6);
            if (at < list.Count && Math.Abs(list[at].Time - frame.Time) < 1e-6)
            {
                var old = list[at];
                frame.KeyFrame |= old.KeyFrame;

                // The same frame decoded again (a seek back over it): the end its run learnt stands.
                if (old.Closed && !frame.Closed)
                    (frame.Until, frame.Closed) = (old.Until, true);
                list[at] = frame;
                Retire(old);
            }
            else
            {
                list.Insert(at, frame);
            }

            _bytes += frame.Bytes;
            if (_bytes > _budget)
                TrimLocked();
        }
    }

    /// <summary>Gives the source's last frame no end (the stream ended after it: a moment past the end shows it).</summary>
    public void HoldLastFrame(int source, double time)
    {
        lock (_gate)
        {
            if (_bySource.TryGetValue(source, out var list) && list.Count > 0 && Math.Abs(list[^1].Time - time) < 1e-6)
                (list[^1].Until, list[^1].Closed) = (double.PositiveInfinity, true);
        }
    }

    /// <summary>
    /// The frame that covers <paramref name="seconds"/>, pinned (call <see cref="Unpin"/>); null when it was not decoded yet. Not
    /// <paramref name="strictly"/> (the player while it plays), an open frame stands for its whole stated length.
    /// </summary>
    public CachedFrame? Exact(int source, double seconds, bool strictly = true)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return null;
            var at = IndexAtOrBefore(list, seconds + 1e-6);
            if (at < 0 || !list[at].Covers(seconds, strictly))
                return null;
            var frame = list[at];
            frame.Pins++;
            frame.LastUsed = ++_use;
            return frame;
        }
    }

    /// <summary>
    /// Whether a frame of exactly this time is there already (a decoder passing over it again need not copy it); a key
    /// frame found that way is marked as one.
    /// </summary>
    public bool HasFrameAt(int source, double time, bool keyFrame)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return false;
            var at = IndexAtOrBefore(list, time + 1e-6);
            if (at < 0 || Math.Abs(list[at].Time - time) > 1e-6)
                return false;
            list[at].KeyFrame |= keyFrame;
            return true;
        }
    }

    /// <summary>
    /// A moment no frame covers, just before the frame at <paramref name="frameTime"/> (a gap in a variable-rate stream,
    /// a frame the decoder dropped): that frame is made to start there, so the moment shows it and is never decoded for
    /// again. Only across a gap of at most <paramref name="maxGap"/> seconds; the frame before is never touched (5.0-Verify:
    /// stretching it let a key frame stand for a whole stretch nothing had decoded, the user's export frozen between key
    /// frames). False when nothing was stretched.
    /// </summary>
    public bool StretchBack(int source, double frameTime, double seconds, double maxGap)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list))
                return false;
            var at = IndexAtOrBefore(list, frameTime + 1e-6);
            if (at < 0 || Math.Abs(list[at].Time - frameTime) > 1e-6 || seconds >= frameTime || frameTime - seconds > maxGap)
                return false;
            if (at > 0 && list[at - 1].Until > seconds + 1e-6)
                return false;
            list[at].Time = seconds;
            return true;
        }
    }

    /// <summary>
    /// Two frames decoded one after the other (one run of the decoder, no seek between): the first lasts until the second
    /// starts, whatever its own duration said. Matroska's millisecond clock leaves gaps a frame's duration does not cover
    /// (420 fps: frames at 0, 2, 5, 7 ms, each 2.38 ms long), and a moment in such a gap would be decoded for again and
    /// again; it also makes a duration run past the next frame's start (frames at 2 and 4 ms), which is cut back to it
    /// (Pass 5.2D). Lengthened only across at most <paramref name="maxGap"/> seconds (a dropped frame or
    /// two), never over a hole; either way the frame's end is then known (<see cref="CachedFrame.Closed"/>).
    /// </summary>
    public void CloseGap(int source, double earlier, double later, double maxGap)
    {
        if (!(later > earlier))
            return;
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return;
            var at = IndexAtOrBefore(list, earlier + 1e-6);
            if (at < 0 || Math.Abs(list[at].Time - earlier) > 1e-6)
                return;
            if (at + 1 < list.Count && list[at + 1].Time < later - 1e-6)
                return;
            var frame = list[at];
            if (frame.Until > later + 1e-9 || frame.Until < later - 1e-9 && later - earlier <= maxGap)
                frame.Until = later;
            frame.Closed = true;
        }
    }

    /// <summary>
    /// The frame decoded just before <paramref name="frame"/>, pinned (what motion is measured against, 5.1-GpuFx); null when none is held within
    /// <paramref name="maxGap"/> seconds. Only what is there: nothing is decoded for it, so a shutter never makes a frame late.
    /// </summary>
    public CachedFrame? Before(CachedFrame frame, double maxGap)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(frame.Source, out var list) || list.Count == 0)
                return null;
            var at = IndexAtOrBefore(list, frame.Time + 1e-6);
            if (at < 1 || !ReferenceEquals(list[at], frame) || frame.Time - list[at - 1].Time > maxGap)
                return null;
            var before = list[at - 1];
            before.Pins++;
            before.LastUsed = ++_use;
            return before;
        }
    }

    /// <summary>Whether the frame that covers <paramref name="seconds"/> is there (no pin).</summary>
    public bool Has(int source, double seconds)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return false;
            var at = IndexAtOrBefore(list, seconds + 1e-6);
            return at >= 0 && list[at].Covers(seconds);
        }
    }

    /// <summary>
    /// The frame to show in the meantime, pinned: the nearest one there is (the latest at or before the moment when it is
    /// no farther than the next one after it), or <paramref name="fallback"/> (the frame the layer showed last) when that
    /// is nearer still. Null only when the source has nothing decoded yet and there is no fallback.
    /// </summary>
    public CachedFrame? Nearest(int source, double seconds, CachedFrame? fallback)
    {
        lock (_gate)
        {
            CachedFrame? best = null;
            var bestDistance = double.PositiveInfinity;
            if (_bySource.TryGetValue(source, out var list) && list.Count > 0)
            {
                var at = IndexAtOrBefore(list, seconds);
                if (at >= 0)
                    (best, bestDistance) = (list[at], seconds - list[at].Time);
                if (at + 1 < list.Count && list[at + 1].Time - seconds < bestDistance * 0.5)
                    (best, bestDistance) = (list[at + 1], list[at + 1].Time - seconds);
            }

            if (fallback is { } held && held.Source == source && IsHeld(held) && Math.Abs(held.Time - seconds) < bestDistance)
                best = held;
            if (best is null)
                return null;
            best.Pins++;
            best.LastUsed = ++_use;
            return best;
        }
    }

    private bool IsHeld(CachedFrame frame) =>
        _bySource.TryGetValue(frame.Source, out var list) && list.Count > 0 && IndexAtOrBefore(list, frame.Time + 1e-6) is var at && at >= 0 && ReferenceEquals(list[at], frame);

    public void Unpin(CachedFrame frame)
    {
        lock (_gate)
        {
            if (frame.Pins > 0)
                frame.Pins--;
        }
    }

    /// <summary>A source left the table (or changed): its frames go, but for those being drawn, which go when let go of.</summary>
    public void Forget(int source)
    {
        lock (_gate)
        {
            if (!_bySource.Remove(source, out var list))
                return;
            foreach (var frame in list)
                Retire(frame);
        }
    }

    /// <summary>Every frame goes (Settings' Clear), but for those being drawn, which go when let go of; the free list is emptied.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var list in _bySource.Values)
            {
                foreach (var frame in list)
                    Retire(frame);
            }

            _bySource.Clear();
            foreach (var free in _free)
                free.Dispose();
            _free.Clear();
            _freeBytes = 0;
        }
    }

    /// <summary>The moments that matter now (shown, hinted, pre-rolled): frames near them are kept, the farthest go first.</summary>
    public void SetInterests(ReadOnlySpan<(int Source, double Seconds, double Weight)> interests)
    {
        lock (_gate)
        {
            _interests.Clear();
            foreach (var interest in interests)
                _interests.Add(interest);
        }
    }

    /// <summary>
    /// The stretches held on to (the cuts ahead of the playhead: their first frames, until their hints go): their frames
    /// are let go only after every other frame, should the budget ever come to them.
    /// </summary>
    public void SetHeld(List<(int Source, double From, double To)> held)
    {
        lock (_gate)
        {
            _held.Clear();
            _held.AddRange(held);
        }
    }

    /// <summary>Whether a frame lies in a held stretch (under the lock).</summary>
    private bool IsHeldStretch(int source, CachedFrame frame)
    {
        foreach (var (heldSource, from, to) in _held)
        {
            if (heldSource == source && frame.Until > from + 1e-6 && frame.Time < to)
                return true;
        }

        return false;
    }

    /// <summary>The time of the last frame of a source at or before <paramref name="seconds"/> that is a key frame, NaN if none is known.</summary>
    public double KeyFrameAtOrBefore(int source, double seconds)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list))
                return double.NaN;
            for (var i = IndexAtOrBefore(list, seconds + 1e-6); i >= 0; i--)
            {
                if (list[i].KeyFrame)
                    return list[i].Time;
            }

            return double.NaN;
        }
    }

    /// <summary>Whether every frame from <paramref name="from"/> to <paramref name="to"/> is there (an ambient window decoded).</summary>
    public bool Covers(int source, double from, double to)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return false;
            var at = IndexAtOrBefore(list, from + 1e-6);
            if (at < 0 || !list[at].Covers(from))
                return false;
            for (; at + 1 < list.Count; at++)
            {
                if (list[at].Until >= to - 1e-6)
                    return true;
                if (list[at + 1].Time > list[at].Until + 1e-3)
                    return false;
            }

            return list[at].Until >= to - 1e-6;
        }
    }

    // ----- Eviction -----

    private void TrimLocked()
    {
        if (_bytes <= _budget && _freeBytes <= _budget / 8)
            return;

        // The free list first (nothing needs it), then the frames least worth keeping until 90 % of the budget is used.
        while (_free.Count > 0 && (_freeBytes > _budget / 8 || _bytes + _freeBytes > _budget))
        {
            var free = _free[^1];
            _free.RemoveAt(_free.Count - 1);
            _freeBytes -= free.Bytes;
            free.Dispose();
        }

        if (_bytes <= _budget)
            return;

        var target = _budget * 9 / 10;
        var scored = new List<(double Score, CachedFrame Frame, List<CachedFrame> List)>();
        foreach (var (source, list) in _bySource)
        {
            foreach (var frame in list)
            {
                if (frame.Pins > 0)
                    continue;
                var distance = double.PositiveInfinity;
                foreach (var (interestSource, seconds, weight) in _interests)
                {
                    if (interestSource == source)
                        distance = Math.Min(distance, Math.Abs(frame.Time - seconds) / Math.Max(weight, 0.05));
                }

                // Unwanted sources go first, the least recently used of them; key frames stay four times longer; a held
                // stretch's frames last of all.
                var score = double.IsPositiveInfinity(distance) ? 1e9 + (_use - frame.LastUsed) : distance;
                if (frame.KeyFrame)
                    score /= 4;
                if (_held.Count > 0 && IsHeldStretch(source, frame))
                    score = -1 - 1 / (1 + score);
                scored.Add((score, frame, list));
            }
        }

        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        foreach (var (_, frame, list) in scored)
        {
            if (_bytes <= target)
                break;
            list.Remove(frame);
            _bytes -= frame.Bytes;
            Evictions++;
            FreeLocked(frame);
        }

        foreach (var source in _bySource.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
            _bySource.Remove(source);
    }

    /// <summary>A frame leaves the cache: its bytes are no longer counted, and it is freed (or reused) once nothing draws it.</summary>
    private void Retire(CachedFrame frame)
    {
        _bytes -= frame.Bytes;
        if (frame.Pins > 0)
        {
            // Still pinned by a render: let go of it later (the renderer unpins, the next trim disposes the orphans).
            _orphans.Add(frame);
            return;
        }

        FreeLocked(frame);
    }

    private readonly List<CachedFrame> _orphans = [];

    /// <summary>Frames retired while drawn are reused once the render that drew them let go (called after each frame).</summary>
    public void CollectOrphans()
    {
        lock (_gate)
        {
            for (var i = _orphans.Count - 1; i >= 0; i--)
            {
                if (_orphans[i].Pins == 0)
                {
                    FreeLocked(_orphans[i]);
                    _orphans.RemoveAt(i);
                }
            }
        }
    }

    private void FreeLocked(CachedFrame frame)
    {
        if (_freeBytes + frame.Bytes > _budget / 8 || _free.Count >= 64)
        {
            frame.Dispose();
            return;
        }

        _free.Add(frame);
        _freeBytes += frame.Bytes;
    }

    private static int IndexAtOrBefore(List<CachedFrame> list, double seconds)
    {
        int low = 0, high = list.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            if (list[mid].Time <= seconds)
                (found, low) = (mid, mid + 1);
            else
                high = mid - 1;
        }

        return found;
    }

    private static int IndexAtOrAfter(List<CachedFrame> list, double seconds)
    {
        int low = 0, high = list.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (list[mid].Time < seconds)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var list in _bySource.Values)
            {
                foreach (var frame in list)
                    frame.Dispose();
            }

            _bySource.Clear();
            foreach (var frame in _free)
                frame.Dispose();
            _free.Clear();
            foreach (var frame in _orphans)
                frame.Dispose();
            _orphans.Clear();
            (_bytes, _freeBytes) = (0, 0);
        }

        Com.Release(ref _adapter3);
    }
}

/// <summary>What Windows has left of its commit limit (RAM + page file).</summary>
internal static partial class SystemCommit
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary>The commit left and its limit, bytes; false when Windows does not say.</summary>
    public static bool TryRead(out long left, out long limit)
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        var read = GlobalMemoryStatusEx(ref status) && status.TotalPageFile > 0;
        (left, limit) = read ? ((long)status.AvailablePageFile, (long)status.TotalPageFile) : (0, 0);
        return read;
    }

    /// <summary>The computer's memory (the physical total), bytes; 0 when Windows does not say.</summary>
    public static long PhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : 0;
    }
}
