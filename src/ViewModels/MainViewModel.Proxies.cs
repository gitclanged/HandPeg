using System.IO;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Proxies (ProxyCache): files with clips requesting one (right-click Optimize Long-GOP Clip for Scrubbing, ProxyChoice.Make), ready
// proxies, and background generation whenever nothing plays or exports. The item is offered only where useful (OptimizeIsUseful:
// long-GOP file the preview cannot decode fast, or Shift held); nothing is generated automatically and there is no cache toggle. The
// GPU preview substitutes ready proxies (GpuScenePlan via PreviewPaths, read only when SequenceExportState.IsPreview); exports never
// do, and audio always comes from the originals.
public partial class MainViewModel
{
    // Ready proxies by source file (any session).
    private readonly Dictionary<string, string> _proxies = new(StringComparer.OrdinalIgnoreCase);

    // Proxies the preview currently uses: requested and ready.
    private Dictionary<string, string> _proxiesInUse = new(StringComparer.OrdinalIgnoreCase);

    // FIFO of files awaiting a proxy.
    private readonly List<string> _proxyQueue = [];
    private bool _baking;

    /// <summary>
    /// Proxy-eligible files (all video clip files) with their request state: Make if any clip of the file chose Optimize, else Auto
    /// (legacy Use Original maps to Auto).
    /// </summary>
    private Dictionary<string, ProxyChoice> ProxyFiles()
    {
        var files = new Dictionary<string, ProxyChoice>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, ProxyChoice choice)
        {
            path = path.Trim().Trim('"');
            if (path.Length > 0 && (choice == ProxyChoice.Make || !files.ContainsKey(path)))
                files[path] = choice == ProxyChoice.Make ? ProxyChoice.Make : ProxyChoice.Auto;
        }

        foreach (var layer in Layers.Where(l => l.IsVideoFile && !l.IsDeleted))
            Add(layer.ImagePath, layer.ProxyChoice);

        return files;
    }

    /// <summary>Whether any clip of a file explicitly requested a proxy (not inferred from GOP length).</summary>
    private bool ProxyAskedFor(string file) => ProxyFiles().TryGetValue(file, out var choice) && choice == ProxyChoice.Make;

    /// <summary>Syncs proxies with the timeline: uses ready ones and queues missing requested ones. Cheap; runs after every change.</summary>
    private void RefreshProxies()
    {
        if (_isBackgroundWorker)
            return;

        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, choice) in ProxyFiles())
        {
            if (choice != ProxyChoice.Make)
                continue;

            // Already exists (earlier session or another clip of the file).
            if ((!_proxies.TryGetValue(file, out var ready) || !FileState.Exists(ready)) && ProxyCache.Find(file, CurrentProjectPath) is { } found)
                _proxies[file] = ready = found;

            if (ready is not null && FileState.Exists(ready))
            {
                inUse[file] = ready;
                continue;
            }

            // Failed this session (undecodable file, full drive): wait for an explicit re-request.
            if (!_proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase) && !_proxyFailed.Contains(file))
                QueueProxy(file);
        }

        if (inUse.Count != _proxiesInUse.Count || inUse.Any(p => !_proxiesInUse.TryGetValue(p.Key, out var had) || had != p.Value))
        {
            _proxiesInUse = inUse;
            InvalidatePreviewState();
        }
    }

    /// <summary>
    /// Sets a clip's proxy choice: <see cref="ProxyChoice.Make"/> (generate now; preview substitutes it when ready) or Auto (original).
    /// </summary>
    public void SetProxyChoice(Layer clip, ProxyChoice choice)
    {
        if (clip.ProxyChoice == choice)
            return;

        clip.ProxyChoice = choice;
        _proxyFailed.Clear();
        RefreshProxies();
        Log(choice switch
        {
            ProxyChoice.Make => $"{clip.Name}: optimized for scrubbing: a proxy is made, which the preview plays in its place (exports read the original)",
            _ => $"{clip.Name}: no longer optimized for scrubbing: the original file is played",
        });
    }

    /// <summary>Clip proxy status for its context menu.</summary>
    public string ProxyStatus(Layer clip)
    {
        var file = clip.ImagePath.Trim().Trim('"');
        return clip.ProxyChoice != ProxyChoice.Make ? "Not optimized: the preview plays the original"
            : _proxiesInUse.ContainsKey(file) ? "Optimized: the preview plays its proxy"
            : _proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase) ? "Its proxy is being made"
            : _proxyFailed.Contains(file) ? "Its proxy could not be made (HandPeg.log says why)"
            : "Its proxy is to be made";
    }

    // ----- Menu item visibility (hidden unless useful) -----

    /// <summary>Fallback without a benchmark for the preview GPU: a worst-case exact-frame time above this (ms, this session) means slow.</summary>
    private const double SlowExactFrameMs = 250;

    /// <summary>
    /// Whether to offer Optimize Long-GOP Clip for Scrubbing: already on (to allow turning off), Shift held at menu open
    /// (<paramref name="asked"/>), or a long-GOP video (I-frame spacing above Settings' heavy interval) that the preview cannot decode
    /// fast (<see cref="PreviewDecodesFast"/>).
    /// </summary>
    public bool OptimizeIsUseful(Layer clip, bool asked)
    {
        if (clip.ProxyChoice == ProxyChoice.Make)
            return true;
        if (!clip.IsVideoFile)
            return false;
        if (asked)
            return true;

        var file = clip.ImagePath.Trim().Trim('"');
        return IsLongGop(file) && !PreviewDecodesFast(file);
    }

    /// <summary>Whether the median I-frame spacing (indexed this session) exceeds Settings' heavy interval; false while unknown.</summary>
    private static bool IsLongGop(string file)
    {
        if (MediaProbe.KnownIFrames(file) is not { Length: >= 2 } times)
            return false;

        var gaps = new double[times.Length - 1];
        for (var i = 1; i < times.Length; i++)
            gaps[i - 1] = times[i] - times[i - 1];
        Array.Sort(gaps);
        return gaps[gaps.Length / 2] > Math.Max(AppSettings.Current.ProxyHeavyIFrameSeconds, 0.5);
    }

    /// <summary>
    /// Whether the GPU preview decodes a file fast: hardware decode on, not WARP, and the GPU has a hardware decoder for the codec (per
    /// a benchmark matching current GPUs/drivers). Without one, uses worst exact-frame time (<see cref="SlowExactFrameMs"/>); defaults
    /// to fast.
    /// </summary>
    private static bool PreviewDecodesFast(string file)
    {
        if (!AppSettings.Current.PlayerHardwareAcceleration)
            return false;

        var engine = GpuPreviewEngine.Current;
        if (engine is not null && engine.Client.Memory.Control.IsSoftwareDevice != 0)
            return false;

        var card = engine?.Client.Memory.Control.AdapterIndex ?? AppSettings.Current.CompositorGpuAdapter;
        if (HardwareBenchmarkService.LoadSaved() is { } benchmark && HardwareBenchmarkService.IsCurrent(benchmark)
            && benchmark.Adapters.FirstOrDefault(a => a.Index == card) is { } measured)
        {
            return HardwareCodec(MediaProbe.KnownInfo(file)?.Video) is { } codec && measured.DecodeCodecs.Contains(codec);
        }

        return engine is null || engine.CacheStats.WorstExactLatencyMs <= SlowExactFrameMs;
    }

    /// <summary>Benchmark codec key for a video (<see cref="HardwareBenchmarkAdapter.DecodeCodecs"/>); null when no GPU decodes it for preview.</summary>
    private static string? HardwareCodec(VideoStreamInfo? video) => video?.Codec switch
    {
        "h264" => "H.264",
        "hevc" => video.PixelFormat.Contains("10", StringComparison.Ordinal) ? "HEVC 10-bit" : "HEVC",
        "vp9" => "VP9",
        "av1" => "AV1",
        _ => null,
    };

    // Files whose proxy failed this session; not retried until a clip's choice changes.
    private readonly HashSet<string> _proxyFailed = new(StringComparer.OrdinalIgnoreCase);

    private void QueueProxy(string file)
    {
        if (_proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase))
            return;

        _proxyQueue.Add(file);
        _ = BakeProxiesAsync();
    }

    /// <summary>Generates queued proxies one at a time while idle (no playback/export), on the background GPU when available.</summary>
    private async Task BakeProxiesAsync()
    {
        if (_baking || _isBackgroundWorker)
            return;

        _baking = true;
        try
        {
            while (_proxyQueue.Count > 0 && !_shutdown.IsCancellationRequested)
            {
                var file = _proxyQueue[0];
                while (IsPlaying || IsBusy)
                    await Task.Delay(1000, _shutdown.Token);

                // Skip if no longer on the timeline (another project opened) or no longer requested.
                if (ProxyAskedFor(file))
                {
                    var working = VideoEncoders.Where(e => e.IsHardware).Select(e => e.Name).ToList();
                    var made = await WorkScheduler.Run(
                        new WorkSpec($"Proxy of {Path.GetFileName(file)}", WorkLane.Background)
                        {
                            Key = $"proxy|{file}",
                            SuspendWhilePlaying = AppSettings.Current.PauseProxiesWhilePlaying,
                        },
                        work => ProxyCache.MakeAsync(file, CurrentProjectPath, working, work), _shutdown.Token);
                    if (made is not null)
                        _proxies[file] = made;
                    else
                        _proxyFailed.Add(file);
                }

                _proxyQueue.Remove(file);
                RefreshProxies();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Write("Proxies could not be made", ex);
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>On project/source close, deletes its proxies when Settings enables it.</summary>
    /// <param name="now">Delete synchronously (app shutdown) instead of in the background.</param>
    private void ForgetProjectProxies(bool now = false)
    {
        if (_isBackgroundWorker || !AppSettings.Current.ClearProjectProxiesOnExit)
            return;

        var files = ProxyFiles().Keys.ToList();
        if (files.Count == 0)
            return;

        var project = CurrentProjectPath;
        foreach (var file in files)
            _proxies.Remove(file);
        if (now)
            ProxyCache.Forget(files, project);
        else
            _ = Task.Run(() => ProxyCache.Forget(files, project));
    }
}
