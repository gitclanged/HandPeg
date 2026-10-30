using System.IO;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Proxies (see ProxyCache): the files whose clips asked for one (a clip's right-click Optimize Long-GOP Clip for
// Scrubbing, ProxyChoice.Make), the ones ready, and the making of the rest in the background, as soon as nothing plays or
// exports. Since 5.0-Verify (the user's Q6) the item is offered only where it helps (OptimizeIsUseful: a long-GOP file the
// preview cannot decode fast, or Shift held), nothing is made by itself, and the proxy cache has no on/off switch. The GPU
// preview reads a video layer's proxy in place of its file once it is made (GpuScenePlan, through PreviewPaths, which only
// the preview's state reads: SequenceExportState.IsPreview); exports never do, and the sound always comes from the originals.
public partial class MainViewModel
{
    // Proxies made (this session or before) for files, by the file.
    private readonly Dictionary<string, string> _proxies = new(StringComparer.OrdinalIgnoreCase);

    // The proxies the preview plays now: of the files whose clips asked for one, made.
    private Dictionary<string, string> _proxiesInUse = new(StringComparer.OrdinalIgnoreCase);

    // Files waiting for a proxy, first first.
    private readonly List<string> _proxyQueue = [];
    private bool _baking;

    /// <summary>
    /// The main video's proxy when one of its clips asked for one and it is made (a project of before 5.0-Verify: the item is
    /// not offered on the main video, whose proxy the GPU preview does not read yet); null for the original. Read by the
    /// live graphs (the golden tests' and the A/V harness's) only.
    /// </summary>
    private string? MainProxy => HasSource && _proxiesInUse.TryGetValue(LocalMediaPath.Trim().Trim('"'), out var proxy) ? proxy : null;

    /// <summary>How tall the main video's proxy is, when there is one in use (see <see cref="MainProxy"/>); 0 otherwise.</summary>
    private int MainProxyHeight => MainProxy is null ? 0 : Math.Min(AppSettings.ProxyTiers.Contains(AppSettings.Current.ProxyTier) ? AppSettings.Current.ProxyTier : 540, SourceHeight > 0 ? SourceHeight : int.MaxValue);

    /// <summary>
    /// The files that may have a proxy, and whether one was asked for (Optimize on any clip of the file: Make; Auto
    /// otherwise; an older project's Use Original counts as Auto): the video layers' files, and the main video's when one of
    /// its clips asked (see <see cref="MainProxy"/>).
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
        if (HasSource && _mediaInfo is { Video: not null })
        {
            foreach (var view in Layers.Where(l => l.IsMainVideo && !l.IsDeleted && l.ProxyChoice == ProxyChoice.Make))
                Add(LocalMediaPath, view.ProxyChoice);
        }

        return files;
    }

    /// <summary>Whether a file's proxy was asked for on one of its clips (Optimize Long-GOP Clip for Scrubbing), not found heavy.</summary>
    private bool ProxyAskedFor(string file) => ProxyFiles().TryGetValue(file, out var choice) && choice == ProxyChoice.Make;

    /// <summary>
    /// Brings the proxies in line with the timeline: those ready are played, and a file whose clips asked for one and has
    /// none is queued. Cheap: asked after every change.
    /// </summary>
    private void RefreshProxies()
    {
        if (_isBackgroundWorker)
            return;

        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, choice) in ProxyFiles())
        {
            if (choice != ProxyChoice.Make)
                continue;

            // Made before (an earlier session, or for another clip of the file).
            if ((!_proxies.TryGetValue(file, out var ready) || !FileState.Exists(ready)) && ProxyCache.Find(file, CurrentProjectPath) is { } found)
                _proxies[file] = ready = found;

            if (ready is not null && FileState.Exists(ready))
            {
                inUse[file] = ready;
                continue;
            }

            // One that failed this session (a file FFmpeg cannot decode, a full drive) waits for the user to ask again.
            if (!_proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase) && !_proxyFailed.Contains(file))
                QueueProxy(file);
        }

        if (inUse.Count != _proxiesInUse.Count || inUse.Any(p => !_proxiesInUse.TryGetValue(p.Key, out var had) || had != p.Value))
        {
            _proxiesInUse = inUse;
            InvalidateLiveFilter();
        }
    }

    /// <summary>
    /// The clip's right-click choice: Optimize Long-GOP Clip for Scrubbing (<see cref="ProxyChoice.Make"/>: a proxy made
    /// now, which the preview plays in the file's place once it is ready), or not (Auto: the original).
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

    /// <summary>Where a clip's proxy stands, for its right-click menu.</summary>
    public string ProxyStatus(Layer clip)
    {
        var file = clip.ImagePath.Trim().Trim('"');
        if (clip.IsMainVideo)
            file = LocalMediaPath.Trim().Trim('"');
        return clip.ProxyChoice != ProxyChoice.Make ? "Not optimized: the preview plays the original"
            : _proxiesInUse.ContainsKey(file) ? clip.IsMainVideo ? "Its proxy is made, but the preview plays the main video's original" : "Optimized: the preview plays its proxy"
            : _proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase) ? "Its proxy is being made"
            : _proxyFailed.Contains(file) ? "Its proxy could not be made (HandPeg.log says why)"
            : "Its proxy is to be made";
    }

    // ----- When the item is offered (5.0-Verify, the user's Q6: hidden unless it is needed) -----

    /// <summary>With no hardware benchmark of the preview's card: an exact frame that took the preview longer than this (its worst this session) counts as slow.</summary>
    private const double SlowExactFrameMs = 250;

    /// <summary>
    /// Whether a clip's right-click menu offers Optimize Long-GOP Clip for Scrubbing: when the clip has it on (so that it can
    /// be turned off); when Shift is held as the menu opens (<paramref name="asked"/>); or when a video layer's file is
    /// long-GOP (its I-frames further apart than Settings' heavy interval) and the preview cannot decode it fast (see
    /// <see cref="PreviewDecodesFast"/>). Never otherwise for the main video, whose proxy the GPU preview does not read yet.
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

    /// <summary>Whether a file's I-frames (listed this session) lie further apart than Settings' heavy interval, at the median; false while they are not known.</summary>
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
    /// Whether the GPU preview decodes a file fast: hardware decoding is on, the preview does not run on the processor
    /// (WARP), and its card has a hardware decoder for the file's codec (the hardware benchmark's list for that card, when it
    /// was measured on these cards and drivers). Without such a benchmark, by how long an exact frame has taken the running
    /// preview at worst (<see cref="SlowExactFrameMs"/>); fast while nothing says otherwise.
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

    /// <summary>The hardware benchmark's name for a video's codec (<see cref="HardwareBenchmarkAdapter.DecodeCodecs"/>); null for one no card decodes for the preview.</summary>
    private static string? HardwareCodec(VideoStreamInfo? video) => video?.Codec switch
    {
        "h264" => "H.264",
        "hevc" => video.PixelFormat.Contains("10", StringComparison.Ordinal) ? "HEVC 10-bit" : "HEVC",
        "vp9" => "VP9",
        "av1" => "AV1",
        _ => null,
    };

    // Files whose proxy could not be made this session: not tried again until a clip's choice changes.
    private readonly HashSet<string> _proxyFailed = new(StringComparer.OrdinalIgnoreCase);

    private void QueueProxy(string file)
    {
        if (_proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase))
            return;

        _proxyQueue.Add(file);
        _ = BakeProxiesAsync();
    }

    /// <summary>
    /// Makes the proxies waiting, one at a time, as soon as nothing plays or exports. Background work, decoded and encoded
    /// on the background card where there is one.
    /// </summary>
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

                // No longer on the timeline (another project was opened meanwhile), or no longer wanted: not made.
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

    /// <summary>Called when a project or a source is closed: its proxies go too, when Settings says so.</summary>
    /// <param name="now">Deleted before returning (HandPeg is closing), not in the background.</param>
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
