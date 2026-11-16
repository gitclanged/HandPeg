using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>
/// Playback &amp; A/V Sync Test sampler (in-app copy of tools\AvSync's): polls a <see cref="GpuPreviewEngine"/> every few hundred
/// microseconds, recording the audio master clock (WasapiAudioEngine.Position with QPC) and each presented frame's scene time (from
/// shared memory). Display time comes from GetFrameStatistics (the showing vblank), else the median reported latency after Present,
/// else the next DWM vblank. A/V = frame time minus audio time at display (positive = video ahead). Allocation-free while running;
/// stopped before the engine is disposed.
/// </summary>
internal sealed unsafe partial class PlaybackSyncSampler : IDisposable
{
    private const int Capacity = 200_000, FrameCapacity = 12_000;

    private readonly GpuPreviewEngine _engine;
    private readonly GpuViewport _viewport;
    private readonly Thread _thread;
    private volatile bool _stop;

    // The sound's clock: (QPC, the moment heard), while playing.
    private readonly long[] _clockTicks = new long[Capacity];
    private readonly double[] _clockSeconds = new double[Capacity];
    private int _clocks;

    // Per present: Present return time, present count, scene sequence and time, reported display time (0 unknown), next DWM vblank.
    private readonly long[] _presentTicks = new long[FrameCapacity];
    private readonly uint[] _presentIds = new uint[FrameCapacity];
    private readonly long[] _sceneSequences = new long[FrameCapacity];
    private readonly double[] _sceneSeconds = new double[FrameCapacity];
    private readonly long[] _shownTicks = new long[FrameCapacity];
    private readonly long[] _gridTicks = new long[FrameCapacity];
    private int _frames;

    // DWM's vblank grid, last read (QPC ticks).
    private long _vblank, _refreshTicks;

    private PlaybackSyncSampler(GpuPreviewEngine engine)
    {
        _engine = engine;
        _viewport = engine.Viewport;
        _thread = new Thread(Run) { IsBackground = true, Name = "A/V sync test sampler", Priority = ThreadPriority.Highest };
    }

    public static PlaybackSyncSampler Start(GpuPreviewEngine engine)
    {
        var sampler = new PlaybackSyncSampler(engine);
        sampler._thread.Start();
        return sampler;
    }

    /// <summary>The display's refresh period as DWM gives it, ms (0 not known).</summary>
    public double RefreshMs => _refreshTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The frames whose showing the swap chain reported itself (GetFrameStatistics).</summary>
    public int ReportedFrames => Enumerable.Range(0, _frames).Count(i => _shownTicks[i] != 0);

    /// <summary>Stops the sampler thread.</summary>
    /// <returns>True if stopped within 2 s (DXGI/DWM calls can hang in the driver); logged otherwise.</returns>
    public bool Stop()
    {
        _stop = true;
        return ThreadStop.Join(_thread, 2000, "Playback sync sampler");
    }

    public void Dispose() => Stop();

    private void Run()
    {
        var lastPresents = _viewport.Presents;
        var lastSeen = 0u;
        var (nextClock, nextStatistics, nextDwm) = (0L, 0L, 0L);
        var client = _engine.Client;
        while (!_stop)
        {
            // The sound's clock every quarter of a millisecond (its reading's own QPC: halfway through it).
            var now = Stopwatch.GetTimestamp();
            if (now >= nextClock && _engine.IsPlaying && _clocks < Capacity)
            {
                _clockSeconds[_clocks] = _engine.Audio.Position;
                _clockTicks[_clocks] = (now + Stopwatch.GetTimestamp()) / 2;
                _clocks++;
                nextClock = now + Stopwatch.Frequency / 4000;
            }

            // A frame presented: its scene is the one the worker presented last (the pump waited for it before Present).
            var presents = _viewport.Presents;
            if (presents != lastPresents && _frames < FrameCapacity)
            {
                lastPresents = presents;
                var sequence = Volatile.Read(ref client.Memory.Control.PresentedSequence);
                _presentTicks[_frames] = now;
                _sceneSequences[_frames] = sequence;
                _sceneSeconds[_frames] = SceneSeconds(client, sequence);
                _presentIds[_frames] = _viewport.SampleLastPresentCount();
                _shownTicks[_frames] = 0;
                _gridTicks[_frames] = _refreshTicks > 0 ? _vblank + (long)(Math.Ceiling((now - _vblank) / (double)_refreshTicks) * _refreshTicks) : 0;
                _frames++;
            }

            // Match swap-chain display reports (polled every 0.5 ms) over the last 128 presents (at 500 Hz display lags 3-4 refreshes).
            if (now >= nextStatistics)
            {
                nextStatistics = now + Stopwatch.Frequency / 2000;
                if (_viewport.SampleFrameStatistics(out var shownId, out var shownAt) && shownId != lastSeen)
                {
                    lastSeen = shownId;
                    for (var i = _frames - 1; i >= 0 && i >= _frames - 128; i--)
                    {
                        if (_shownTicks[i] == 0 && _presentIds[i] == shownId)
                            _shownTicks[i] = shownAt;
                    }
                }
            }

            if (now >= nextDwm)
            {
                if (VBlank(out var vblank, out var period))
                    (_vblank, _refreshTicks) = (vblank, period);
                nextDwm = now + Stopwatch.Frequency / 500;
            }

            Thread.SpinWait(20);
        }
    }

    // Scene time for a sequence from its shared-memory slot (triple-buffered, safe for two publishes); NaN if overwritten or not playing.
    private static double SceneSeconds(CompositorClient client, long sequence)
    {
        for (var slot = 0; slot < CompositorProtocol.SceneSlots; slot++)
        {
            ref var header = ref client.Memory.SceneHeader(slot);
            if (Volatile.Read(ref header.FrameSequenceNumber) == sequence)
            {
                var seconds = header.PlayheadSeconds;
                if (Volatile.Read(ref header.FrameSequenceNumber) == sequence && header.IsPlaying == 1)
                    return seconds;
            }
        }

        return double.NaN;
    }

    /// <summary>
    /// Frames first displayed in [from, to) (QPC): A/V in ms (unknown display/audio omitted) and sequence times in display order (gaps =
    /// skips). A frame counts from its first Present (repeats of the same timeline frame are ignored).
    /// </summary>
    public (List<double> OffsetsMs, List<double> Moments) FramesWithin(long from, long to)
    {
        var (offsets, moments) = (new List<double>(), new List<double>());
        var latency = Latency();
        var seen = new HashSet<long>();
        for (var i = 0; i < _frames; i++)
        {
            if (!seen.Add(_sceneSequences[i]) || _presentTicks[i] < from || _presentTicks[i] >= to || double.IsNaN(_sceneSeconds[i]))
                continue;
            moments.Add(_sceneSeconds[i]);
            if (ShownAt(i, latency) is { } at && SoundAt(at) is { } heard)
                offsets.Add((_sceneSeconds[i] - heard) * 1000);
        }

        return (offsets, moments);
    }

    // Median Present-to-display latency over reported frames (QPC ticks; null with fewer than 5).
    private long? Latency()
    {
        var measured = Enumerable.Range(0, _frames).Where(i => _shownTicks[i] != 0 && _shownTicks[i] >= _presentTicks[i])
            .Select(i => _shownTicks[i] - _presentTicks[i]).Order().ToList();
        return measured.Count >= 5 ? measured[measured.Count / 2] : null;
    }

    // Display time: reported, else Present + median latency, else next vblank.
    private long? ShownAt(int i, long? latency) =>
        _shownTicks[i] != 0 ? _shownTicks[i] : latency is { } l ? _presentTicks[i] + l : _gridTicks[i] != 0 ? _gridTicks[i] : null;

    // The sound's moment at a QPC: between the two readings around it (each at most 2 ms away, on one stretch of playing).
    private double? SoundAt(long ticks)
    {
        var (lo, hi) = (0, _clocks - 1);
        if (hi < 1 || ticks < _clockTicks[0] || ticks > _clockTicks[hi])
            return null;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_clockTicks[mid] <= ticks)
                lo = mid;
            else
                hi = mid;
        }

        var span = _clockTicks[hi] - _clockTicks[lo];
        if (span <= 0 || span > Stopwatch.Frequency / 500 || _clockSeconds[hi] < _clockSeconds[lo])
            return null;
        return _clockSeconds[lo] + (_clockSeconds[hi] - _clockSeconds[lo]) * (ticks - _clockTicks[lo]) / span;
    }

    // DWM last vblank and refresh period in QPC ticks (DwmGetCompositionTimingInfo). DWM_TIMING_INFO is byte-packed (dwmapi.h):
    // cbSize 0, rateRefresh 4, qpcRefreshPeriod 12, rateCompose 20, qpcVBlank 28, ..., 292 bytes.
    private static bool VBlank(out long vblank, out long period)
    {
        const int size = 292;
        var info = stackalloc byte[512];
        new Span<byte>(info, 512).Clear();
        *(uint*)info = size;
        (vblank, period) = (0, 0);
        if (DwmGetCompositionTimingInfo(0, info) < 0)
            return false;
        (period, vblank) = (*(long*)(info + 12), *(long*)(info + 28));
        return period > 0 && vblank > 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(nint window, byte* info);
}
