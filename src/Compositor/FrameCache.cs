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
    public long Bytes;
    public long LastUsed;

    /// <summary>Renders using it right now (the renderer pins what it draws until its frame is submitted).</summary>
    public int Pins;

    public bool Covers(double seconds) => seconds >= Time - 1e-6 && seconds < Until - 1e-6;

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
/// are kept in time order (binary search). The budget is the client's (<see cref="Services.ControlBlock.CacheBudgetRequest"/>,
/// automatic: an eighth of the card's own memory, at most 3 GB) and never more than what Windows lets this process have
/// beside everything else it holds (IDXGIAdapter3::QueryVideoMemoryInfo, asked twice a second): when a game starts, the
/// cache shrinks at once. Beyond the budget, the frames farthest from every moment of interest (the frames shown, the
/// hints, the pre-roll) go first, key frames last, and the held stretches (the first frames of the cuts ahead of the
/// playhead, 5.0-Release) after everything else. Textures are reused by size and format.
/// <para>Threads: the decode lanes insert, the renderer looks up and pins; one lock, held for microseconds.</para>
/// </summary>
internal sealed unsafe class FrameCache : IDisposable
{
    private const long MaxAutoBudget = 3L << 30;
    private const long MinBudget = 96L << 20;
    private const long WindowsHeadroom = 256L << 20;

    private readonly GpuDevice _gpu;
    private readonly object _gate = new();
    private readonly Dictionary<int, List<CachedFrame>> _bySource = [];
    private readonly List<CachedFrame> _free = [];
    private readonly List<(int Source, double Seconds, double Weight)> _interests = [];
    private readonly List<(int Source, double From, double To)> _held = [];
    private readonly long _dedicated;
    private nint _adapter3;
    private long _requested;
    private long _budget;
    private long _windowsRoom = long.MaxValue;
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

    /// <summary>Asks Windows how much video memory it lets this process use, at most twice a second; shrinks the cache when that fell.</summary>
    public void CheckWindowsBudget(long nowTicks)
    {
        if (_adapter3 == 0 || nowTicks < Volatile.Read(ref _nextBudgetCheck))
            return;
        Volatile.Write(ref _nextBudgetCheck, nowTicks + System.Diagnostics.Stopwatch.Frequency / 2);
        if (!Dxgi.QueryVideoMemory(_adapter3, out var info))
            return;

        // What the cache may hold: Windows' budget less everything else this process uses, less some room to spare.
        var others = (long)info.CurrentUsage - Volatile.Read(ref _bytes) - Volatile.Read(ref _freeBytes);
        var room = Math.Max((long)info.Budget - Math.Max(others, 0) - WindowsHeadroom, 0);
        lock (_gate)
        {
            if (Math.Abs(room - _windowsRoom) < (16L << 20))
                return;
            _windowsRoom = room;
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
            : _gpu.IsWarp || _dedicated < (512L << 20) ? 512L << 20
            : Math.Min(_dedicated / 8, MaxAutoBudget);
        _budget = Math.Max(Math.Min(wanted, _windowsRoom), MinBudget);
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
                list[^1].Until = double.PositiveInfinity;
        }
    }

    /// <summary>The frame that covers <paramref name="seconds"/>, pinned (call <see cref="Unpin"/>); null when it was not decoded yet.</summary>
    public CachedFrame? Exact(int source, double seconds)
    {
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return null;
            var at = IndexAtOrBefore(list, seconds + 1e-6);
            if (at < 0 || !list[at].Covers(seconds))
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
    /// again. Only across at most <paramref name="maxGap"/> seconds (a dropped frame or two), never over a hole.
    /// </summary>
    public void CloseGap(int source, double earlier, double later, double maxGap)
    {
        if (!(later > earlier) || later - earlier > maxGap)
            return;
        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var list) || list.Count == 0)
                return;
            var at = IndexAtOrBefore(list, earlier + 1e-6);
            if (at < 0 || Math.Abs(list[at].Time - earlier) > 1e-6 || list[at].Until >= later - 1e-9)
                return;
            if (at + 1 < list.Count && list[at + 1].Time < later - 1e-6)
                return;
            list[at].Until = later;
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
