using HandPegApp.Compositor.Native;

namespace HandPegApp.Compositor;

/// <summary>
/// One decoded picture cached in VRAM (Instant Seek): an NV12/P010 texture (hardware decode) or luma and chroma planes (CPU decode), its
/// time span and colour metadata. Immutable once cached: decoders never write into a cached frame, so the renderer can read it while
/// others decode.
/// </summary>
internal sealed class CachedFrame
{
    public int Source;
    public double Time;
    public double Until;

    /// <summary>Seek-landing key frame: kept longer, since fast scrubs show these.</summary>
    public bool KeyFrame;

    /// <summary>Decoded speculatively (hint, pre-roll, play-ahead): counted as a predicted hit when shown.</summary>
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

    /// <summary>Source transfer function (MfVideoDecoder.Transfer): 15 PQ, 16 HLG; others SDR.</summary>
    public int Transfer;
    public long Bytes;
    public long LastUsed;

    /// <summary>Insertion time (Stopwatch ticks): evicting a frame soon after insertion indicates the cache is too small.</summary>
    public long Inserted;

    /// <summary>Active render pins (the renderer pins what it draws until the frame is submitted).</summary>
    public int Pins;

    /// <summary>
    /// Playhead frame of a deactivated tab, kept for reactivation (FrameCache.KeepOnly, lean parking): evicted last within
    /// FrameCache.KeptShare of the budget, first beyond it. Cleared when the tab's file re-registers (Unkeep) and on reuse.
    /// </summary>
    public bool Kept;

    /// <summary>
    /// True when <see cref="Until"/> is exact: the next frame in its run was read (FrameCache.CloseGap), FFmpeg supplied the duration to the
    /// next picture, or the stream ended after it. Otherwise the end is the container's sample duration.
    /// </summary>
    public bool Closed;

    /// <summary>
    /// End of the span this frame covers. An open frame yields its last millisecond to the next frame: millisecond container clocks can
    /// make the next frame start before this one's stated duration ends (Matroska at 420 fps: stamps 10.686 and 10.688 s, each 2.38 ms
    /// long). Exact lookups there wait for the next frame instead of taking this one (otherwise MF HEVC returned the previous frame for one
    /// reference frame); the playing player still takes it (<see cref="Covers"/>, non-strict).
    /// </summary>
    public double CoverEnd => Closed ? Until : Math.Max(Until - OpenSlack, Time + (Until - Time) / 2);

    /// <summary>Portion of an open frame's end yielded to the next frame (seconds): one millisecond container tick.</summary>
    public const double OpenSlack = 0.001;

    /// <summary>True when the frame covers <paramref name="seconds"/>: strictly up to <see cref="CoverEnd"/>, else up to its stated end.</summary>
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
/// VRAM cache of every decoded frame (Instant Seek): stepping back, re-scrubbing a range, or seeking into anything decoded once costs no
/// decode. Frames per source are time-ordered (binary search). Budget comes from the client (<see cref="Services.ControlBlock.CacheBudgetRequest"/>).
/// Auto mode is game-aware: with the GPU idle, half the DXGI budget beyond this process's other usage, max 12 GB, always leaving a margin
/// free (a third of VRAM, max 8 GB: ~11 GB cache and 8 GB untouched on 24 GB); when another app consumes VRAM (DXGI budget well below
/// VRAM, e.g. a game), a small cache of 1/8 VRAM, max 3 GB. Never more than DXGI allows beside this process's other allocations
/// (IDXGIAdapter3::QueryVideoMemoryInfo, polled twice a second), so starting a game shrinks the cache immediately. Unified-memory GPUs use a
/// share of system RAM instead, 1.5 to 2.5 GB from 12 GB up (<see cref="MemoryTier"/> holds the full Auto rule).
/// <para>Adaptive: the budget starts at MemoryTier.Baseline (~2 GB) and grows 1 GB at a time, at most every 2 s, when evicted frames are
/// still needed (recently decoded, or near a shown or sought time: <see cref="MemoryTier.IsThrash"/>), up to the hardware ceiling (the rule
/// above for GPUs with over 4 GB VRAM; at 4 GB or less the rule sets both baseline and ceiling, max 4 GB) or the Settings maximum, and only
/// while DXGI caps allow. <see cref="Clear"/> (tab deactivated, project closed) resets to baseline. The free list holds at most 1/8 of the
/// budget, max 256 MB; <see cref="DropFree"/> empties it on worker trim.</para>
/// <para>Large-VRAM GPUs (<see cref="BigCard"/>, MemoryTier.IsBigCard: discrete, over 4 GB) skip the baseline: the budget is the maximum
/// from the start, filled as frames decode; deactivated tabs leave their frames here (DecoderPool parks their serials so reactivation finds
/// them), evicted LRU as the next tab needs room.</para>
/// Over budget, frames farthest from every time of interest (shown, hinted, pre-rolled) go first, key frames last, and held ranges (first
/// frames of upcoming cuts) after everything else. Textures are reused by size and format. The budget is a ceiling, never a reservation:
/// only decoded frames occupy memory, so an idle editor holds none.
/// <para>Threads: decode lanes insert, the renderer looks up and pins; one lock held for microseconds.</para>
/// </summary>
internal sealed unsafe class FrameCache : IDisposable
{
    private const long MinBudget = 96L << 20;
    private const long WindowsHeadroom = 256L << 20;

    // DXGI budget share below which another app is assumed to be using the GPU (rest of the Auto rule in MemoryTier).
    private const double FreeCardShare = 0.7;

    // VRAM also counts against system commit (RAM + page file): a 12 GB cache plus a game and a 4 GB page file exhausted a 52 GB limit on a
    // 24 GB GPU, killing the worker and then the editor. Minimum commit left for everything else, as a share of the limit.
    private const long LeastCommitLeft = 3L << 30, MostCommitLeft = 8L << 30;
    private const int CommitShare = 8;

    private readonly GpuDevice _gpu;
    private readonly object _gate = new();
    private readonly Dictionary<int, List<CachedFrame>> _bySource = [];
    private readonly List<CachedFrame> _free = [];
    private readonly List<(int Source, double Seconds, double Weight)> _interests = [];
    private readonly List<(int Source, double From, double To)> _held = [];
    private readonly long _dedicated;

    // Unified-memory GPU (GpuDevice.UnifiedMemory) and total physical RAM: inputs to the Auto rule.
    private readonly bool _unified;
    private readonly long _physical;

    // Large-VRAM GPU (MemoryTier.IsBigCard) and the goldens' override (BigCardForTests): -1 none, 0 no, 1 yes.
    private readonly bool _bigCard;
    private int _bigCardForTests = -1;
    private nint _adapter3;
    private long _requested;
    private long _budget;
    private long _windowsRoom = long.MaxValue;
    private long _commitRoom = long.MaxValue;

    // Unified memory only: cache allowance from available system RAM (MemoryTier.UnifiedRoom).
    private long _physicalRoom = long.MaxValue;

    // Last-polled DXGI budget beyond this process's other usage; 0 until first polled.
    private long _freeOnCard;
    private bool _cardBusy;
    private long _bytes;
    private long _freeBytes;
    private long _use;
    private long _nextBudgetCheck;
    private bool _disabled;

    // Adaptive budget: growth steps above baseline, last growth time (Stopwatch ticks; 0 = never), and the last computed baseline,
    // maximum and ceiling (published for the Settings readout).
    private int _steps;
    private long _lastGrown;
    private long _baseline;
    private long _maximum;
    private long _ceiling;

    // Goldens' small-scale override (ScaleForTests): byte-sized baseline, ceiling and step instead of GB, with a test clock.
    private (long Baseline, long Ceiling, long Step)? _scale;
    private Func<long> _clock = System.Diagnostics.Stopwatch.GetTimestamp;

    // Forgotten source serials (never reused): a lane still decoding one may insert after Forget, leaving an orphan until eviction, so
    // such inserts are rejected. Tracks the newest ForgottenKept.
    private const int ForgottenKept = 1024;
    private readonly HashSet<int> _forgotten = [];
    private readonly Queue<int> _forgottenOrder = new();

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
        _bigCard = MemoryTier.IsBigCard(gpu.IsWarp, _unified, _dedicated);
        SetBudget(0);
    }

    /// <summary>Effective budget (bytes): the client's request clamped by DXGI, commit and RAM limits.</summary>
    public long BudgetBytes => Volatile.Read(ref _budget);

    public long Bytes => Volatile.Read(ref _bytes);

    /// <summary>Free list size (bytes): retired frames kept for reuse, excluded from <see cref="Bytes"/>.</summary>
    public long FreeBytes => Volatile.Read(ref _freeBytes);

    /// <summary>Current growth limit (bytes): hardware ceiling, or the Settings maximum when lower (MemoryTier.Maximum).</summary>
    public long MaximumBytes => Volatile.Read(ref _maximum);

    /// <summary>Current hardware ceiling (bytes, MemoryTier.Ceiling): Auto's upper bound.</summary>
    public long CeilingBytes => Volatile.Read(ref _ceiling);

    /// <summary>Growth steps above baseline (0 after <see cref="Clear"/>; always 0 on large-VRAM GPUs).</summary>
    public int GrowSteps => Volatile.Read(ref _steps);

    /// <summary>
    /// True for large-VRAM GPUs (MemoryTier.IsBigCard: discrete, over 4 GB): the budget is the maximum immediately and deactivated tabs
    /// keep their frames (CompositorWorker.TrimEmptiesCache, DecoderPool parked sources). Fixed for the cache lifetime except in goldens
    /// (<see cref="BigCardForTests"/>).
    /// </summary>
    public bool BigCard => Volatile.Read(ref _bigCardForTests) is var forced && forced >= 0 ? forced == 1 : _bigCard;

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

    /// <summary>False when the client disabled the cache: it then keeps the minimum (<see cref="MinBudget"/>).</summary>
    public bool Enabled => !_disabled;

    /// <summary>Applies the client's request: 0 = auto, -1 = off, else bytes.</summary>
    public void SetBudget(long requested)
    {
        lock (_gate)
        {
            _requested = requested;
            _disabled = requested < 0;
            SetBudgetLocked();
        }
    }

    /// <summary>Commit allowance for the cache: current holdings plus remaining commit, minus the margin reserved for everything else.</summary>
    /// <param name="held">Bytes the cache holds now (releasable).</param>
    /// <param name="commitLeft">Remaining system commit, bytes.</param>
    /// <param name="commitLimit">Commit limit (RAM + page file).</param>
    public static long CommitRoom(long held, long commitLeft, long commitLimit) =>
        Math.Max(held + commitLeft - Math.Clamp(commitLimit / CommitShare, LeastCommitLeft, MostCommitLeft), 0);

    /// <summary>
    /// Polls the DXGI VRAM budget and remaining commit at most twice a second, shrinking the cache when either drops. Called every worker
    /// turn, drawing or not, to react to a game starting while the editor is paused.
    /// </summary>
    public void CheckWindowsBudget(long nowTicks)
    {
        if (nowTicks < Volatile.Read(ref _nextBudgetCheck))
            return;
        Volatile.Write(ref _nextBudgetCheck, nowTicks + System.Diagnostics.Stopwatch.Frequency / 2);
        CheckCommit();
        if (_adapter3 == 0 || !Dxgi.QueryVideoMemory(_adapter3, out var info))
            return;

        // Cache room: DXGI budget minus this process's other usage, minus headroom.
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
        if (!SystemCommit.TryRead(out var left, out var limit, out var availablePhysical))
            return;

        lock (_gate)
        {
            var room = CommitRoom(_bytes + _freeBytes, left, limit);
            var physicalRoom = _unified && _scale is null ? MemoryTier.UnifiedRoom(_bytes + _freeBytes, availablePhysical, _physical) : long.MaxValue;
            if (Math.Abs(room - _commitRoom) < (64L << 20) && Math.Abs(physicalRoom - _physicalRoom) < (64L << 20))
                return;
            (_commitRoom, _physicalRoom) = (room, physicalRoom);
            SetBudgetLocked();
        }
    }

    private void SetBudgetLocked()
    {
        _budget = BudgetLocked(_steps);
        TrimLocked(mayGrow: false);
    }

    /// <summary>
    /// Budget after <paramref name="steps"/> growth steps: baseline plus growth, capped by the maximum (hardware ceiling or client request)
    /// and by DXGI, commit and RAM limits. Off means the minimum: shown frames and a few around them (any less and two layers of one file at
    /// two times would evict each other's frames forever).
    /// </summary>
    private long BudgetLocked(int steps)
    {
        var warp = _gpu.IsWarp;
        var baseline = _scale?.Baseline ?? MemoryTier.Baseline(warp, _unified, _dedicated, _physical, _cardBusy, _freeOnCard);
        var ceiling = _scale?.Ceiling ?? MemoryTier.Ceiling(warp, _unified, _dedicated, _physical, _cardBusy, _freeOnCard);
        var maximum = MemoryTier.Maximum(_requested, ceiling);
        (_baseline, _ceiling) = (baseline, ceiling);
        Volatile.Write(ref _maximum, _disabled ? MinBudget : maximum);
        var wanted = _disabled ? MinBudget : MemoryTier.Budget(BigCard, baseline, steps, maximum, _scale?.Step ?? MemoryTier.GrowStep);
        var least = _scale is { } scale ? Math.Min(scale.Baseline, MinBudget) : MinBudget;
        Volatile.Write(ref _capReason, CapOf(_disabled, wanted, _windowsRoom, _commitRoom, _physicalRoom));
        return Math.Max(Math.Min(Math.Min(Math.Min(wanted, _windowsRoom), _commitRoom), _physicalRoom), least);
    }

    /// <summary>Budget cap reasons (<see cref="CapReason"/>, ControlBlock.CacheBudgetCap): what limits the effective budget now.</summary>
    public const int CapAuto = 0, CapOff = 1, CapDxgi = 2, CapCommit = 3, CapRam = 4;

    // Cap reason of the last computed budget (CapAuto...CapRam).
    private int _capReason;

    /// <summary>What limits the effective budget now: <see cref="CapAuto"/> (the rule or Settings maximum), off, DXGI, commit or RAM.</summary>
    public int CapReason => Volatile.Read(ref _capReason);

    /// <summary>
    /// Cap reason of a budget: off when disabled, else the tightest of the DXGI, commit and RAM rooms below <paramref name="wanted"/>,
    /// else auto. Pure.
    /// </summary>
    internal static int CapOf(bool disabled, long wanted, long windowsRoom, long commitRoom, long physicalRoom)
    {
        if (disabled)
            return CapOff;
        var tightest = Math.Min(Math.Min(windowsRoom, commitRoom), physicalRoom);
        if (tightest >= wanted)
            return CapAuto;
        return tightest == windowsRoom ? CapDxgi : tightest == commitRoom ? CapCommit : CapRam;
    }

    /// <summary>Log name of a cap reason: off, dxgi, commit, ram or auto. Pure.</summary>
    public static string CapName(int cap) => cap switch
    {
        CapOff => "off",
        CapDxgi => "dxgi",
        CapCommit => "commit",
        CapRam => "ram",
        _ => "auto",
    };

    /// <summary>
    /// Grows one step when allowed (MemoryTier.MayGrow: below maximum, 2 s since the last) and the caps let the budget actually rise; false
    /// otherwise (unchanged).
    /// </summary>
    private bool TryGrowLocked(long now)
    {
        if (_disabled || BigCard)
            return false;
        var since = _lastGrown == 0 ? double.PositiveInfinity : (now - _lastGrown) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (!MemoryTier.MayGrow(_baseline, _steps, _maximum, since, _scale?.Step ?? MemoryTier.GrowStep))
            return false;
        var grown = BudgetLocked(_steps + 1);
        if (grown <= _budget)
            return false;
        (_budget, _lastGrown) = (grown, now);
        Volatile.Write(ref _steps, _steps + 1);
        return true;
    }

    /// <summary>
    /// Goldens: runs the adaptive rule with MB-scale baseline, ceiling and step and a test clock, so growth is observable without GB of
    /// frames or seconds of waiting. Never called by HandPeg.
    /// </summary>
    internal void ScaleForTests(long baseline, long ceiling, long step, Func<long> clock)
    {
        lock (_gate)
        {
            (_scale, _clock) = ((baseline, ceiling, step), clock);
            (_windowsRoom, _commitRoom) = (long.MaxValue, long.MaxValue);
            SetBudgetLocked();
        }
    }

    /// <summary>
    /// Goldens: forces the large-VRAM rule (true: maximum immediately, frames kept across tab deactivation), the baseline rule (false), or the
    /// actual GPU's (null). Never called by HandPeg.
    /// </summary>
    internal void BigCardForTests(bool? big)
    {
        lock (_gate)
        {
            Volatile.Write(ref _bigCardForTests, big is { } forced ? forced ? 1 : 0 : -1);
            SetBudgetLocked();
        }
    }

    // ----- Insertion and lookup -----

    /// <summary>Rents a frame of this size and format with textures (reused from the free list when possible), not yet cached.</summary>
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
                    free.KeyFrame = free.Ahead = free.Kept = false;
                    free.Pins = 0;
                    return free;
                }
            }
        }

        var frame = new CachedFrame { Width = width, Height = height, TenBit = tenBit, Software = software };
        try
        {
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
        }
        catch
        {
            if (!software)
            {
                Com.Release(ref frame.ChromaSrv);
                Com.Release(ref frame.LumaSrv);
                frame.Planar?.Dispose();
            }
            else
            {
                frame.Chroma?.Dispose();
                frame.Luma?.Dispose();
            }

            throw;
        }

        frame.Bytes = (long)width * height * 3 / 2 * (tenBit ? 2 : 1);
        return frame;
    }

    /// <summary>Returns an unused rented frame to the free list.</summary>
    public void Return(CachedFrame frame)
    {
        lock (_gate)
            FreeLocked(frame);
    }

    /// <summary>
    /// Caches a decoded frame (replacing one at the same time), then trims to budget, growing it first when evictees are still needed.
    /// Frames of a source forgotten meanwhile (a lane still decoding it) go straight to the free list.
    /// </summary>
    public void Insert(CachedFrame frame)
    {
        lock (_gate)
        {
            if (_forgotten.Contains(frame.Source))
            {
                FreeLocked(frame);
                return;
            }

            frame.LastUsed = ++_use;
            frame.Inserted = _clock();
            if (!_bySource.TryGetValue(frame.Source, out var list))
                _bySource[frame.Source] = list = [];

            var at = IndexAtOrAfter(list, frame.Time - 1e-6);
            if (at < list.Count && Math.Abs(list[at].Time - frame.Time) < 1e-6)
            {
                var old = list[at];
                frame.KeyFrame |= old.KeyFrame;

                // Same frame decoded again (seek back over it): keep the end learned from its run.
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
                TrimLocked(mayGrow: !BigCard);
        }
    }

    /// <summary>Makes the source's last frame open-ended (stream ended after it, so later times show it).</summary>
    public void HoldLastFrame(int source, double time)
    {
        lock (_gate)
        {
            if (_bySource.TryGetValue(source, out var list) && list.Count > 0 && Math.Abs(list[^1].Time - time) < 1e-6)
                (list[^1].Until, list[^1].Closed) = (double.PositiveInfinity, true);
        }
    }

    /// <summary>
    /// The frame covering <paramref name="seconds"/>, pinned (call <see cref="Unpin"/>); null when not yet decoded. Non-strict (the playing
    /// player) lets an open frame cover its full stated duration.
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
    /// True when a frame at exactly this time is cached (a decoder passing over it need not copy it again); marks it as a key frame if so.
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
    /// For an uncovered time just before the frame at <paramref name="frameTime"/> (VFR gap or decoder-dropped frame), moves that frame's
    /// start back to cover it, so it is never re-decoded. Only across gaps up to <paramref name="maxGap"/> seconds; the previous frame is
    /// never stretched (that let a key frame cover a whole undecoded range, freezing exports between key frames). False when unchanged.
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
    /// For two consecutively decoded frames (same run, no seek), the first lasts until the second starts regardless of its stated duration.
    /// Matroska's millisecond clock leaves gaps durations do not cover (420 fps: frames at 0, 2, 5, 7 ms, each 2.38 ms), which would be
    /// re-decoded repeatedly, and lets durations overrun the next frame (frames at 2 and 4 ms), which are trimmed. Extended only across gaps
    /// up to <paramref name="maxGap"/> seconds (a dropped frame or two), never over a hole; either way the end becomes exact
    /// (<see cref="CachedFrame.Closed"/>).
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
    /// The cached frame preceding <paramref name="frame"/>, pinned (motion reference); null when none within <paramref name="maxGap"/>
    /// seconds. Cache-only: nothing is decoded for it, so the shutter never makes a frame late.
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

    /// <summary>True when a frame covering <paramref name="seconds"/> is cached (no pin).</summary>
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
    /// Stand-in frame, pinned: the nearest cached frame (preferring the latest at or before the time unless the next is much closer), or
    /// <paramref name="fallback"/> (the layer's last shown frame) when nearer still. Null only when nothing is decoded and no fallback exists.
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

    /// <summary>
    /// Evicts a removed or changed source's frames (pinned ones are released when unpinned). Its serial is never reused, so later inserts
    /// for it (from a lane still decoding) are rejected.
    /// </summary>
    public void Forget(int source)
    {
        lock (_gate)
        {
            if (_forgotten.Add(source))
            {
                _forgottenOrder.Enqueue(source);
                if (_forgottenOrder.Count > ForgottenKept)
                    _forgotten.Remove(_forgottenOrder.Dequeue());
            }

            if (!_bySource.Remove(source, out var list))
                return;
            foreach (var frame in list)
                Retire(frame);
        }
    }

    // Bytes of frames kept at background tabs' playheads (KeepOnly), under the gate.
    private long _keptBytes;

    /// <summary>Bytes held by frames kept at background tabs' playheads (<see cref="KeepOnly"/>).</summary>
    public long KeptBytes => Volatile.Read(ref _keptBytes);

    /// <summary>
    /// Share of <paramref name="budget"/> background-tab playhead frames may hold before being evicted first (bytes): max(96 MB, budget / 8),
    /// never above budget / 4, so a shrinking budget leaves most of it to the active tab.
    /// </summary>
    internal static long KeptShare(long budget) => Math.Min(Math.Max(96L << 20, budget / 8), budget / 4);

    /// <summary>
    /// Lean parking (tab deactivated on unified memory or battery): keeps only a source's frames covering <paramref name="spans"/> (up to
    /// <paramref name="mostPerSpan"/> per span from its start), marked <see cref="CachedFrame.Kept"/> (evicted last); all other frames of the
    /// source are evicted now. Returns bytes kept (0 = none).
    /// </summary>
    public long KeepOnly(int source, ReadOnlySpan<(double From, double To)> spans, int mostPerSpan = 3)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list))
                return 0;

            var keep = new HashSet<CachedFrame>(ReferenceEqualityComparer.Instance);
            foreach (var (from, to) in spans)
            {
                var taken = 0;
                for (var i = Math.Max(IndexAtOrBefore(list, from + 1e-6), 0); i < list.Count && taken < mostPerSpan; i++)
                {
                    var frame = list[i];
                    if (frame.Time > to + 1e-6)
                        break;
                    if (frame.Until <= from + 1e-6)
                        continue;
                    keep.Add(frame);
                    taken++;
                }
            }

            var kept = 0L;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var frame = list[i];
                if (keep.Contains(frame))
                {
                    if (!frame.Kept)
                        (frame.Kept, _keptBytes) = (true, _keptBytes + frame.Bytes);
                    kept += frame.Bytes;
                    continue;
                }

                list.RemoveAt(i);
                Retire(frame);
            }

            if (list.Count == 0)
                _bySource.Remove(source);
            return kept;
        }
    }

    /// <summary>Un-parks a source (tab reactivated): its kept frames become ordinary cache entries.</summary>
    public void Unkeep(int source)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list))
                return;
            foreach (var frame in list)
                Unmark(frame);
        }
    }

    // Clears a frame's background-tab keep mark (under the gate).
    private void Unmark(CachedFrame frame)
    {
        if (!frame.Kept)
            return;
        frame.Kept = false;
        _keptBytes -= frame.Bytes;
    }

    /// <summary>
    /// Evicts every frame (Settings Clear, project closed, tab deactivated except on large-VRAM GPUs; pinned frames go when unpinned), empties
    /// the free list and resets the budget to baseline (large-VRAM GPUs stay at the maximum).
    /// </summary>
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
            Volatile.Write(ref _steps, 0);
            _lastGrown = 0;
            SetBudgetLocked();
        }
    }

    /// <summary>
    /// Worker trim: disposes the free list and unpinned orphans (frames retired while drawn). Cached frames stay. Returns bytes released.
    /// </summary>
    public long DropFree()
    {
        lock (_gate)
        {
            var dropped = _freeBytes;
            foreach (var free in _free)
                free.Dispose();
            _free.Clear();
            _freeBytes = 0;
            for (var i = _orphans.Count - 1; i >= 0; i--)
            {
                if (_orphans[i].Pins == 0)
                {
                    dropped += _orphans[i].Bytes;
                    _orphans[i].Dispose();
                    _orphans.RemoveAt(i);
                }
            }

            return dropped;
        }
    }

    /// <summary>Sets times of interest (shown, hinted, pre-rolled): nearby frames are kept, the farthest evicted first.</summary>
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
    /// Sets held ranges (first frames of upcoming cuts, until their hints are removed): evicted only after every other frame.
    /// </summary>
    public void SetHeld(List<(int Source, double From, double To)> held)
    {
        lock (_gate)
        {
            _held.Clear();
            _held.AddRange(held);
        }
    }

    /// <summary>True when a frame lies in a held range (under the lock).</summary>
    private bool IsHeldStretch(int source, CachedFrame frame)
    {
        foreach (var (heldSource, from, to) in _held)
        {
            if (heldSource == source && frame.Until > from + 1e-6 && frame.Time < to)
                return true;
        }

        return false;
    }

    /// <summary>True when frames cover <paramref name="from"/> to <paramref name="to"/> contiguously (an ambient window decoded).</summary>
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

    /// <summary>
    /// Enforces the budget and free-list cap. With <paramref name="mayGrow"/> (just inserted), if evictees are still needed
    /// (MemoryTier.IsThrash) the budget grows a step first when the rule and DXGI allow.
    /// </summary>
    private void TrimLocked(bool mayGrow)
    {
        var freeCap = MemoryTier.FreeListCap(_budget);
        if (_bytes <= _budget && _freeBytes <= freeCap)
            return;

        // Free list first (unneeded), then the least valuable frames until usage drops to 90 % of budget.
        while (_free.Count > 0 && (_freeBytes > freeCap || _bytes + _freeBytes > _budget))
        {
            var free = _free[^1];
            _free.RemoveAt(_free.Count - 1);
            _freeBytes -= free.Bytes;
            free.Dispose();
        }

        if (_bytes <= _budget)
            return;

        var scored = new List<(double Score, CachedFrame Frame, List<CachedFrame> List, double Near)>();
        List<(double Score, CachedFrame Frame, List<CachedFrame> List, double Near)>? kept = null;
        var unwanted = 0;
        foreach (var (source, list) in _bySource)
        {
            foreach (var frame in list)
            {
                if (frame.Pins > 0)
                    continue;

                // Background-tab playhead frames, LRU first: placed after the others below.
                if (frame.Kept)
                {
                    (kept ??= []).Add((-3 - 1.0 / (1 + (_use - frame.LastUsed)), frame, list, double.PositiveInfinity));
                    continue;
                }

                var (distance, near) = (double.PositiveInfinity, double.PositiveInfinity);
                foreach (var (interestSource, seconds, weight) in _interests)
                {
                    if (interestSource == source)
                    {
                        var away = Math.Abs(frame.Time - seconds);
                        (distance, near) = (Math.Min(distance, away / Math.Max(weight, 0.05)), Math.Min(near, away));
                    }
                }

                // Unwanted sources first (LRU); key frames survive 4x longer; held-range frames last.
                var score = double.IsPositiveInfinity(distance) ? 1e9 + (_use - frame.LastUsed) : distance;
                if (frame.KeyFrame)
                    score /= 4;
                if (_held.Count > 0 && IsHeldStretch(source, frame))
                    score = -1 - 1 / (1 + score);
                else if (double.IsPositiveInfinity(distance))
                    unwanted++;
                scored.Add((score, frame, list, near));
            }
        }

        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        // Kept frames are evicted last only within KeptShare of the current budget; the excess (LRU first) goes right after unwanted frames
        // (sorted first: score 1e9+, key frames a quarter of it) and before any wanted one. So a budget that shrank after tabs were
        // deactivated (other apps took memory) never starves the active tab, and no playhead frame goes while a dead frame stays. Within the
        // share, a reactivated tab still finds its frame.
        if (kept is not null)
        {
            kept.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            var (over, first) = (_keptBytes - KeptShare(_budget), 0);
            for (; first < kept.Count && over > 0; first++)
                over -= kept[first].Frame.Bytes;
            scored.InsertRange(unwanted, kept.Take(first));
            scored.AddRange(kept.Skip(first));
        }

        // Thrash: evictees were just decoded or sit near a shown or sought time, so the project needs more than the budget. Grow a step
        // when allowed; then evict only what exceeds the grown budget.
        if (mayGrow && WouldThrash(scored, _budget * 9 / 10) && TryGrowLocked(_clock()) && _bytes <= _budget)
            return;

        var target = _budget * 9 / 10;
        foreach (var (_, frame, list, _) in scored)
        {
            if (_bytes <= target)
                break;
            list.Remove(frame);
            _bytes -= frame.Bytes;
            Evictions++;
            Unmark(frame);
            FreeLocked(frame);
        }

        foreach (var source in _bySource.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
            _bySource.Remove(source);
    }

    /// <summary>True when any frame evicted to reach <paramref name="target"/> is still needed (MemoryTier.IsThrash).</summary>
    private bool WouldThrash(List<(double Score, CachedFrame Frame, List<CachedFrame> List, double Near)> scored, long target)
    {
        var now = _clock();
        var left = _bytes;
        foreach (var (_, frame, _, near) in scored)
        {
            if (left <= target)
                break;
            left -= frame.Bytes;
            if (MemoryTier.IsThrash((now - frame.Inserted) / (double)System.Diagnostics.Stopwatch.Frequency, near))
                return true;
        }

        return false;
    }

    /// <summary>Removes a frame from accounting; it is freed (or reused) once no render pins it.</summary>
    private void Retire(CachedFrame frame)
    {
        _bytes -= frame.Bytes;
        Unmark(frame);
        if (frame.Pins > 0)
        {
            // Still pinned: release later (renderer unpins; the next collection or trim disposes orphans).
            _orphans.Add(frame);
            return;
        }

        FreeLocked(frame);
    }

    private readonly List<CachedFrame> _orphans = [];

    /// <summary>Recycles frames retired while drawn once their render unpinned them (called after each frame).</summary>
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
        if (_freeBytes + frame.Bytes > MemoryTier.FreeListCap(_budget) || _free.Count >= 64)
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
            (_bytes, _freeBytes, _keptBytes) = (0, 0, 0);
        }

        Com.Release(ref _adapter3);
    }
}

/// <summary>System commit (RAM + page file) and physical memory queries.</summary>
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

    /// <summary>Remaining commit, commit limit and available physical RAM (AvailPhys), bytes; false when unavailable.</summary>
    public static bool TryRead(out long left, out long limit, out long availablePhysical)
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        var read = GlobalMemoryStatusEx(ref status) && status.TotalPageFile > 0;
        (left, limit, availablePhysical) = read ? ((long)status.AvailablePageFile, (long)status.TotalPageFile, (long)status.AvailablePhysical) : (0, 0, 0);
        return read;
    }

    /// <summary>Total physical RAM in bytes; 0 when unavailable.</summary>
    public static long PhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : 0;
    }
}
