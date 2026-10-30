using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using HandPegApp.Services;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// The player in real time (Pass 5.0-Verify): a real <see cref="GpuPreviewEngine"/> (its compositor worker, its frame pump,
/// its swap chain on a window of the harness's own, off the desktop, never shown on any screen nor activated) and its
/// <see cref="Services.Audio.WasapiAudioEngine"/> on the sound device Settings name (muted unless --audible: the clock runs
/// all the same), driven through the steps the app takes:
///   paused - a seek while paused, the paused settle (300 ms), then Play;
///   seek   - a seek while playing, 20 s on;
///   rate   - the speed changed while playing (J/K/L: twice as fast, then back);
///   loop   - played into the sequence's end; at the engine's Ended, back to the start and Play again (Loop: MainWindow's
///            OnPlaybackEnded).
/// A thread of its own follows both clocks the whole time, every few hundred microseconds: the sound being heard
/// (the engine's master clock, WasapiAudioEngine.Position: IAudioClock's position and its QPC carried forward by the
/// processor's counter, through the transport's lines, the Audio Sync Offset added) and every frame the pump presents
/// (the sequence moment its scene was evaluated at, from the shared memory's scene of the sequence the worker presented).
/// When that frame reaches the screen is the swap chain's own report where DXGI gives one (GetFrameStatistics: the vblank's
/// QPC of the present), else the first vblank after the present on DWM's grid (DwmGetCompositionTimingInfo), as said in
/// the report. A frame's A/V is its moment minus the sound's moment when it is shown: positive, the picture is ahead.
/// Each step is measured from 0.4 s after it for a few seconds; nominal is a median within 0.5 ms, and no frame may be
/// 15 ms or more off (--tolerance).
/// </summary>
internal static class Session
{
    public static int Run(Options options)
    {
        var tolerance = options.Number("tolerance", 15);
        var nominal = options.Number("nominal", 0.5);
        var measure = options.Number("measure", 3);
        var proxies = options.Has("proxies") && options.Get("proxies") == "on";

        using var project = ProjectSession.Open(Path.GetFullPath(options.Get("project")), Console.Out);
        project.WaitForFacts(TimeSpan.FromSeconds(90));
        project.UseProxies(proxies, proxies && options.Has("main-proxy") ? Path.GetFullPath(options.Get("main-proxy")) : null);
        var state = project.State();

        using var window = OffscreenWindow.Create((int)options.Number("width", 1280), (int)options.Number("height", 720));
        var starting = GpuPreviewEngine.StartAsync(window.Handle);
        if (!ProjectSession.WaitUntil(() => starting.IsCompleted, TimeSpan.FromSeconds(30)) || starting.Result is not { } engine)
            throw new InvalidOperationException($"The GPU engine did not start: {GpuPreviewEngine.LastStartFailure}");

        using (engine)
        {
            engine.Muted = !options.Has("audible");
            engine.SetState(state);
            var ended = 0;
            engine.Ended += () => Interlocked.Exchange(ref ended, 1);

            // The device opens on the engine's own thread: given a moment.
            ProjectSession.WaitUntil(() => engine.Audio.DeviceName != "Starting", TimeSpan.FromSeconds(5));
            var settings = AppSettings.Current;
            Console.WriteLine();
            Console.WriteLine($"proxies {(proxies ? "on" : "off")}; sound: {(engine.Audio.HasDevice ? engine.Audio.DeviceName : "no device could be opened: the engine's own clock (the processor's counter) is measured")}"
                              + $"{(engine.Muted ? ", muted" : "")}; buffer {settings.AudioBufferMs} ms, sync offset {settings.AudioSyncOffsetMs:+0.#;-0.#;0} ms"
                              + (Native.StreamLatency(engine.Audio) is { } latency ? $", stream latency {latency * 1000:0.##} ms (IAudioClock's position is at the speakers: not added again)" : ""));

            using var sampler = Sampler.Start(engine);
            var steps = new List<(string Step, double At, long From, long To)>();

            // Waits a while, as the UI thread does meanwhile: its queue run, the plan's textures and captions handed over.
            void Idle(double seconds)
            {
                var until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < until)
                {
                    engine.PrepareAround(engine.Position);
                    ProjectSession.Pump(TimeSpan.FromMilliseconds(30));
                }
            }

            void Measured(string step, double at)
            {
                var from = Stopwatch.GetTimestamp() + (long)(0.4 * Stopwatch.Frequency);
                Idle(measure + 0.4);
                steps.Add((step, at, from, Stopwatch.GetTimestamp()));
            }

            var length = engine.SequenceSeconds;
            var points = options.Points().Where(p => p < length - measure - 2).ToList();
            if (points.Count == 0)
                points.Add(Math.Max(0, Math.Min(length / 3, length - measure - 2)));
            foreach (var at in points)
            {
                engine.Seek(at);
                Idle(0.3);
                engine.Play();
                Measured("paused", at);

                if (at + 20 < length - measure - 1)
                {
                    engine.Seek(at + 20);
                    Measured("seek", at + 20);
                }

                engine.Rate = 2;
                Measured("rate 2x", engine.Position);
                engine.Rate = 1;

                // A short sequence (a quick-cut edit): 2x played into its end, and 1x is measured from where the step began.
                if (engine.Position > length - measure - 0.5 || !engine.IsPlaying)
                {
                    engine.Seek(at);
                    Idle(0.3);
                    engine.Play();
                }

                Measured("rate 1x", engine.Position);
                engine.Pause();
                Idle(0.2);
            }

            // Into the end with Loop on: at Ended, back to the start and Play (MainWindow.OnPlaybackEnded), the engine told where
            // Loop goes back to as the window tells it while playing (MainWindow.LoopWrap), so the start's cuts are ready.
            if (length > measure + 3)
            {
                engine.SetLoop(double.PositiveInfinity, 0);
                engine.Seek(Math.Max(length - 1.5, 0));
                Idle(0.3);
                Interlocked.Exchange(ref ended, 0);
                engine.Play();
                var waited = Stopwatch.StartNew();
                while (Volatile.Read(ref ended) == 0 && waited.Elapsed.TotalSeconds < 6)
                    Idle(0.01);
                if (Volatile.Read(ref ended) == 1)
                {
                    engine.Seek(0);
                    engine.Play();
                    Measured("loop", 0);
                }
                else
                {
                    Console.WriteLine("  the engine did not say Ended within 6 s of playing into the end: Loop's wrap not measured");
                }

                engine.Pause();
            }

            sampler.Stop();
            if (options.Has("dump"))
                sampler.Dump(Path.GetFullPath(options.Get("dump")), steps);
            return Report(sampler, steps, tolerance, nominal);
        }
    }

    private static int Report(Sampler sampler, List<(string Step, double At, long From, long To)> steps, double tolerance, double nominal)
    {
        Console.WriteLine($"{sampler.PresentCount} frames presented, {sampler.ClockCount} clock readings; the display's refresh {sampler.RefreshMs:0.###} ms (DWM).");
        Console.WriteLine($"Shown at: {sampler.DisplaySource}.");
        Console.WriteLine();
        Console.WriteLine("step      at (s)    frames   A/V (ms): median    mean    worst   within 0.5 ms   result");
        var failed = 0;
        var off = 0;
        foreach (var (step, at, from, to) in steps)
        {
            var values = sampler.Offsets(from, to);
            if (values.Count < 10)
            {
                failed++;
                Console.WriteLine($"{step,-8} {at,8:0.###}  {values.Count,7}   too few frames to say (the swap chain presented {values.Count})                 FAIL");
                continue;
            }

            var sorted = values.Order().ToList();
            var median = sorted[sorted.Count / 2];
            var worst = values.MaxBy(Math.Abs);
            var within = values.Count(v => Math.Abs(v) < 0.5) * 100.0 / values.Count;
            var passes = Math.Abs(median) < tolerance && Math.Abs(worst) < tolerance;
            failed += passes ? 0 : 1;
            off += Math.Abs(median) < nominal ? 0 : 1;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{step,-8} {at,8:0.###}  {values.Count,7}   {median,15:+0.00;-0.00;0.00}  {values.Average(),6:+0.00;-0.00;0.00}  {worst,7:+0.00;-0.00;0.00}  {within,13:0}%   {(passes ? "PASS" : "FAIL")}"));
        }

        Console.WriteLine(failed == 0 ? $"PASS: every step's frames within {tolerance:0.#} ms of the sound." : $"FAIL: {failed} of {steps.Count} steps have frames {tolerance:0.#} ms or more off.");
        Console.WriteLine(off == 0 ? $"Nominal: every step's median within {nominal:0.0#} ms." : $"Not nominal: {off} of {steps.Count} steps have a median {nominal:0.0#} ms or more off.");
        return failed == 0 && steps.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// The thread that follows the two clocks: the sound's (sampled with its QPC) and each frame the pump presents (its scene's
    /// moment, when Present returned, and when it was shown). Allocation-free while it runs: every reading goes into arrays
    /// made beforehand (a garbage collection on this thread would only cost readings, never the engine's timing).
    /// </summary>
    private sealed class Sampler : IDisposable
    {
        private const int Capacity = 400_000, Frames = 20_000;

        private readonly GpuPreviewEngine _engine;
        private readonly Func<long> _presents;
        private readonly Func<nint> _swapChain;
        private readonly Thread _thread;
        private volatile bool _stop;

        // The sound's clock: (QPC, the moment heard), while playing.
        private readonly long[] _clockTicks = new long[Capacity];
        private readonly double[] _clockSeconds = new double[Capacity];
        private int _clocks;

        // The frames presented: when Present returned, its present count, the scene's sequence and moment, when the swap
        // chain said it was shown (0 not said), and the first vblank after Present on DWM's grid.
        private readonly long[] _presentTicks = new long[Frames];
        private readonly uint[] _presentIds = new uint[Frames];
        private readonly long[] _sceneSequences = new long[Frames];
        private readonly double[] _sceneSeconds = new double[Frames];
        private readonly long[] _shownTicks = new long[Frames];
        private readonly long[] _gridTicks = new long[Frames];
        private int _frames;

        // DWM's vblank grid, last read.
        private long _vblank, _refreshTicks;

        private Sampler(GpuPreviewEngine engine)
        {
            _engine = engine;
            var viewport = typeof(GpuPreviewEngine).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)
                           ?? throw new InvalidOperationException("The engine has no viewport.");
            var self = Expression.Constant(viewport);
            _presents = Expression.Lambda<Func<long>>(Expression.Property(self, viewport.GetType().GetProperty("Presents")!)).Compile();
            _swapChain = Expression.Lambda<Func<nint>>(Expression.Field(self, viewport.GetType().GetField("_swapChain", BindingFlags.NonPublic | BindingFlags.Instance)!)).Compile();
            _thread = new Thread(Run) { IsBackground = true, Name = "A/V sampler", Priority = ThreadPriority.Highest };
        }

        public static Sampler Start(GpuPreviewEngine engine)
        {
            var sampler = new Sampler(engine);
            sampler._thread.Start();
            return sampler;
        }

        public int PresentCount => _frames;

        public int ClockCount => _clocks;

        public double RefreshMs => _refreshTicks * 1000.0 / Stopwatch.Frequency;

        /// <summary>How the frames' showing was known: by the swap chain's own report, or worked out (see <see cref="ShownAt"/>).</summary>
        public string DisplaySource
        {
            get
            {
                var reported = Enumerable.Range(0, _frames).Count(i => _shownTicks[i] != 0);
                return Latency() is { } latency
                    ? string.Create(CultureInfo.InvariantCulture, $"{reported} of {_frames} frames by the swap chain's frame statistics (the vblank that showed them); the rest {latency * 1000.0 / Stopwatch.Frequency:0.###} ms after their Present (those frames' median)")
                    : $"the first vblank after each Present on DWM's grid (the swap chain reported {reported} of {_frames} frames shown: too few to go by; assumed)";
            }
        }

        public void Stop()
        {
            _stop = true;
            _thread.Join(2000);
        }

        public void Dispose() => Stop();

        private void Run()
        {
            var lastPresents = _presents();
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
                var presents = _presents();
                if (presents != lastPresents && _frames < Frames)
                {
                    lastPresents = presents;
                    var sequence = Volatile.Read(ref client.Memory.Control.PresentedSequence);
                    _presentTicks[_frames] = now;
                    _sceneSequences[_frames] = sequence;
                    _sceneSeconds[_frames] = SceneSeconds(client, sequence);
                    _presentIds[_frames] = Native.LastPresentCount(_swapChain());
                    _shownTicks[_frames] = 0;
                    _gridTicks[_frames] = _refreshTicks > 0 ? _vblank + (long)(Math.Ceiling((now - _vblank) / (double)_refreshTicks) * _refreshTicks) : 0;
                    _frames++;
                }

                // When each present was shown: the swap chain's report of the vblank that showed it (asked every half millisecond).
                if (now >= nextStatistics && (nextStatistics = now + Stopwatch.Frequency / 2000) > 0
                    && Native.FrameStatistics(_swapChain(), out var shownId, out var shownAt) && shownId != lastSeen)
                {
                    // Only the present it names: one the report skipped over (a vblank not read in time) is not known exactly.
                    lastSeen = shownId;
                    for (var i = _frames - 1; i >= 0 && i >= _frames - 8; i--)
                    {
                        if (_shownTicks[i] == 0 && _presentIds[i] == shownId)
                            _shownTicks[i] = shownAt;
                    }
                }

                if (now >= nextDwm)
                {
                    if (Native.VBlank(out var vblank, out var period))
                        (_vblank, _refreshTicks) = (vblank, period);
                    nextDwm = now + Stopwatch.Frequency / 500;
                }

                Thread.SpinWait(20);
            }
        }

        // The moment the scene of a sequence was evaluated at: the slot of the shared memory that holds it (three slots: it is
        // not written again until two more scenes are published, a refresh or more away).
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

        /// <summary>Every frame presented and every step, as text (--dump): for looking into a result.</summary>
        public void Dump(string path, List<(string Step, double At, long From, long To)> steps)
        {
            static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var origin = _frames > 0 ? _presentTicks[0] : 0;
            using var writer = new StreamWriter(path);
            foreach (var (step, at, from, to) in steps)
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# step {step} at {at:0.###} s: {Ms(from - origin):0.000} to {Ms(to - origin):0.000} ms"));
            writer.WriteLine("present_ms,present_id,scene,scene_s,reported_shown_ms,heard_at_present_s,heard_when_shown_s");
            var latency = Latency();
            for (var i = 0; i < _frames; i++)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{Ms(_presentTicks[i] - origin):0.000},{_presentIds[i]},{_sceneSequences[i]},{_sceneSeconds[i]:0.000000},{(_shownTicks[i] == 0 ? double.NaN : Ms(_shownTicks[i] - origin)):0.000},{SoundAt(_presentTicks[i]) ?? double.NaN:0.000000},{(ShownAt(i, latency) is { } shown ? SoundAt(shown) : null) ?? double.NaN:0.000000}"));
            }
        }

        /// <summary>Each frame first shown within [from, to) (QPC): its moment minus the sound's moment then, in ms.</summary>
        public List<double> Offsets(long from, long to)
        {
            var values = new List<double>();
            var latency = Latency();

            // A frame is on screen from its first Present: the engine presents it again while the timeline frame stays the
            // same (5.0-Verify), and a frame first shown before the window is not shown late within it.
            var seen = new HashSet<long>();
            for (var i = 0; i < _frames; i++)
            {
                if (!seen.Add(_sceneSequences[i]) || _presentTicks[i] < from || _presentTicks[i] >= to || double.IsNaN(_sceneSeconds[i]))
                    continue;
                if (ShownAt(i, latency) is { } shown && SoundAt(shown) is { } heard)
                    values.Add((_sceneSeconds[i] - heard) * 1000);
            }

            return values;
        }

        /// <summary>
        /// The median of Present to the vblank that showed a frame, over the frames the swap chain reported (QPC ticks; null
        /// for fewer than 5: then DWM's grid is gone by). DXGI reports a windowed swap chain's frames only while DWM composes
        /// its window, which it may stop doing for one off every screen.
        /// </summary>
        private long? Latency()
        {
            var measured = Enumerable.Range(0, _frames).Where(i => _shownTicks[i] != 0 && _shownTicks[i] >= _presentTicks[i]).Select(i => _shownTicks[i] - _presentTicks[i]).Order().ToList();
            return measured.Count >= 5 ? measured[measured.Count / 2] : null;
        }

        /// <summary>When a frame was shown: as the swap chain reported it; else that long after its Present; else the first vblank after it.</summary>
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

            var limit = Stopwatch.Frequency / 500;
            var span = _clockTicks[hi] - _clockTicks[lo];
            if (span <= 0 || span > limit || _clockSeconds[hi] < _clockSeconds[lo])
                return null;
            return _clockSeconds[lo] + (_clockSeconds[hi] - _clockSeconds[lo]) * (ticks - _clockTicks[lo]) / span;
        }
    }
}
