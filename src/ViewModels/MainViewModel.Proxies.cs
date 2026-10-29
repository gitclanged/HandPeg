using System.IO;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Proxies (see ProxyCache): which files on the timeline want one, the ones ready, and the making of the rest in the
// background once HandPeg has been left alone for a while. Live Preview plays a file's proxy in its place as soon as
// it is ready; exports never do. And the composed timeline proxy: the whole sequence as one video, when Settings
// switches it on.
public partial class MainViewModel
{
    // Proxies made (this session or before) for files, by the file.
    private readonly Dictionary<string, string> _proxies = new(StringComparer.OrdinalIgnoreCase);

    // The proxies Live Preview plays now: of files on the timeline whose clips do not say Use Original.
    private Dictionary<string, string> _proxiesInUse = new(StringComparer.OrdinalIgnoreCase);

    // Files waiting for a proxy, first first; and files already looked at for whether they are heavy.
    private readonly List<string> _proxyQueue = [];
    private readonly HashSet<string> _proxyLooked = new(StringComparer.OrdinalIgnoreCase);
    private bool _baking;
    private long _lastActivity = Environment.TickCount64;

    /// <summary>
    /// Something heavy is going on (scrubbing, a drag of a clip, an edge or a box): proxies are made once that has stopped
    /// a while. Ordinary pointer moves, clicks and keys are not reported (see the main window).
    /// </summary>
    public void NoteActivity() => _lastActivity = Environment.TickCount64;

    /// <summary>
    /// How long HandPeg waits after heavy activity before making proxies: the short wait when a graphics card other
    /// than the player's does the work (it takes nothing from playback), the usual one otherwise.
    /// </summary>
    private static int ProxyIdleSeconds => GpuAdapters.HasSeparateBackgroundCard
        ? Math.Clamp(AppSettings.Current.ProxyIdleSecondsWithBackgroundCard, 0, 3600)
        : Math.Clamp(AppSettings.Current.ProxyIdleSeconds, 0, 3600);

    private bool LeftAlone => Environment.TickCount64 - _lastActivity >= ProxyIdleSeconds * 1000L && !IsPlaying && !IsBusy;

    /// <summary>The main video's proxy when the player plays it (Settings: proxies for the main video too); null for the original.</summary>
    private string? MainProxy => HasSource && _proxiesInUse.TryGetValue(LocalMediaPath.Trim().Trim('"'), out var proxy) ? proxy : null;

    /// <summary>How tall the main video's proxy is, when the player plays it; 0 when it plays the original.</summary>
    private int MainProxyHeight => MainProxy is null ? 0 : Math.Min(AppSettings.ProxyTiers.Contains(AppSettings.Current.ProxyTier) ? AppSettings.Current.ProxyTier : 540, SourceHeight > 0 ? SourceHeight : int.MaxValue);

    /// <summary>The files that may have a proxy, and what their clips say about it (Use Original on any clip wins, then Make Proxy).</summary>
    private Dictionary<string, ProxyChoice> ProxyFiles()
    {
        var files = new Dictionary<string, ProxyChoice>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, ProxyChoice choice)
        {
            path = path.Trim().Trim('"');
            if (path.Length == 0)
                return;

            files[path] = !files.TryGetValue(path, out var had) ? choice
                : had == ProxyChoice.Original || choice == ProxyChoice.Original ? ProxyChoice.Original
                : had == ProxyChoice.Make || choice == ProxyChoice.Make ? ProxyChoice.Make
                : ProxyChoice.Auto;
        }

        foreach (var layer in Layers.Where(l => l.IsVideoFile && !l.IsDeleted))
            Add(layer.ImagePath, layer.ProxyChoice);
        if (HasSource && _mediaInfo is { Video: not null } && AppSettings.Current.ProxyScope == AppSettings.ProxyAllVideos)
            Add(LocalMediaPath, MainLayer.ProxyChoice);
        return files;
    }

    /// <summary>
    /// Brings the proxies in line with the timeline: those ready are played, a file whose clips say Make Proxy is
    /// queued, and a file not looked at yet is looked at (in the background) for whether it is heavy. Cheap: asked
    /// after every change.
    /// </summary>
    private void RefreshProxies()
    {
        if (_isBackgroundWorker)
            return;

        var settings = AppSettings.Current;
        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, choice) in ProxyFiles())
        {
            if (choice == ProxyChoice.Original || (!settings.VideoProxies && choice != ProxyChoice.Make))
                continue;

            // Made before (an earlier session, or for another clip of the file).
            if ((!_proxies.TryGetValue(file, out var ready) || !FileState.Exists(ready)) && ProxyCache.Find(file, CurrentProjectPath) is { } found)
                _proxies[file] = ready = found;

            if (ready is not null && FileState.Exists(ready))
            {
                inUse[file] = ready;
                continue;
            }

            if (_proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase))
                continue;
            if (choice == ProxyChoice.Make)
                QueueProxy(file);
            else if (_proxyLooked.Add(file))
                _ = LookForHeavinessAsync(file);
        }

        if (inUse.Count != _proxiesInUse.Count || inUse.Any(p => !_proxiesInUse.TryGetValue(p.Key, out var had) || had != p.Value))
        {
            _proxiesInUse = inUse;
            InvalidateLiveFilter();
        }
    }

    /// <summary>The clip's right-click choice: a proxy when its file is heavy (Automatic), always (Make Proxy), or never (Use Original).</summary>
    public void SetProxyChoice(Layer clip, ProxyChoice choice)
    {
        if (clip.ProxyChoice == choice)
            return;

        clip.ProxyChoice = choice;
        RefreshProxies();
        Log(choice switch
        {
            ProxyChoice.Make => $"{clip.Name}: a proxy will be made, and played in its place",
            ProxyChoice.Original => $"{clip.Name}: the original file is played",
            _ => $"{clip.Name}: a proxy is made when its file is heavy",
        });
    }

    /// <summary>Where a clip's proxy stands, for its right-click menu.</summary>
    public string ProxyStatus(Layer clip)
    {
        var file = clip.ImagePath.Trim().Trim('"');
        if (clip.IsMainVideo)
            file = LocalMediaPath.Trim().Trim('"');
        return _proxiesInUse.ContainsKey(file) ? "Playing from its proxy"
            : _proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase) ? "Proxy waiting to be made (once HandPeg is left alone)"
            : clip.ProxyChoice == ProxyChoice.Original ? "Playing the original"
            : !AppSettings.Current.VideoProxies && clip.ProxyChoice != ProxyChoice.Make ? "Proxies are off (Settings)"
            : "No proxy: the file is not heavy";
    }

    /// <summary>Looks at a file (its size, codec and I-frame interval), in the background, and queues it for a proxy when it is heavy.</summary>
    private async Task LookForHeavinessAsync(string file)
    {
        try
        {
            var heavy = await WorkScheduler.Run(new WorkSpec($"Looking at {Path.GetFileName(file)}", WorkLane.Background) { Visible = false, Key = $"heavy|{file}" }, async work =>
                File.Exists(file) && await MediaProbe.ProbeAsync(file, work.Token) is { } info
                && ProxyCache.IsHeavy(info, await ProxyCache.IFrameIntervalAsync(file, work.Token)), _shutdown.Token);
            if (heavy)
                QueueProxy(file);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Write($"Proxy: {Path.GetFileName(file)} could not be looked at", ex);
        }
    }

    private void QueueProxy(string file)
    {
        if (_proxyQueue.Contains(file, StringComparer.OrdinalIgnoreCase))
            return;

        _proxyQueue.Add(file);
        _ = BakeProxiesAsync();
    }

    /// <summary>
    /// Makes the proxies waiting, one at a time, each once HandPeg has been left alone for the idle delay (no input,
    /// not playing, nothing busy): background work, decoded and encoded on the background card where there is one.
    /// Then the timeline proxy, when it is switched on and the timeline has none.
    /// </summary>
    private async Task BakeProxiesAsync()
    {
        if (_baking || _isBackgroundWorker)
            return;

        _baking = true;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                while (!LeftAlone)
                    await Task.Delay(1000, _shutdown.Token);

                if (_proxyQueue.Count > 0)
                {
                    var file = _proxyQueue[0];

                    // No longer on the timeline (another project was opened meanwhile): not made.
                    if (ProxyFiles().ContainsKey(file))
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
                    }

                    _proxyQueue.Remove(file);
                    RefreshProxies();
                    continue;
                }

                if (!await BakeTimelineProxyAsync())
                    break;
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

    // ----- The composed timeline proxy -----

    // The timeline proxy for the timeline as it is now, when there is one; dropped the moment anything changes.
    private string? _timelineProxy;

    // The fingerprint the timeline proxy was looked for (or made) under, so an unchanged timeline is not looked at again.
    private string _timelineProxyLooked = "";

    /// <summary>Whether the player plays the composed timeline proxy (the picture finished, only the sound mixed live).</summary>
    public bool PlaysTimelineProxy => _timelineProxy is not null;

    /// <summary>Something changed: the timeline proxy no longer shows the timeline, and the live graph takes over at once.</summary>
    private void DropTimelineProxy()
    {
        _timelineProxyLooked = "";
        if (_timelineProxy is null)
            return;

        _timelineProxy = null;
        InvalidateLiveFilter();
        _ = BakeProxiesAsync();
    }

    /// <summary>
    /// What the timeline proxy is named after: the whole sequence's preview command (every filter, layer and file of
    /// it) with each file's size and date, and the proxy settings. An unchanged timeline finds its proxy again.
    /// </summary>
    private string TimelineFingerprint()
    {
        var command = BuildPreviewCommand("timeline", [(0, SequenceSeconds)], TimelinePercent(), null);
        var files = new[] { LocalMediaPath }.Concat(Layers.Where(l => l.IsVideoFile || l.IsImage).Select(l => l.ImagePath))
            .Select(f => FileState.Get(f) is var state ? $"{f}|{state.Length}|{state.WrittenUtc.Ticks}" : f);
        var text = string.Join("\n", files.Prepend(command).Append(ProxyCache.Kind));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
    }

    /// <summary>The scale, in percent of the output, the timeline proxy is made at: the proxy height.</summary>
    private int TimelinePercent()
    {
        var (_, height) = (FrameWidth, FrameHeight);
        var tier = AppSettings.ProxyTiers.Contains(AppSettings.Current.ProxyTier) ? AppSettings.Current.ProxyTier : 540;
        return height > 0 ? Math.Clamp((int)Math.Round(tier * 100.0 / height), 5, 100) : 50;
    }

    /// <summary>
    /// Finds or makes the timeline proxy for the timeline as it is, when Settings switches it on. Returns whether there
    /// may be more to do (false: nothing is to be made now).
    /// </summary>
    private async Task<bool> BakeTimelineProxyAsync()
    {
        if (!AppSettings.Current.TimelineProxy || !HasSource || !FrameEngine || SequenceSeconds <= 0.5 || _timelineProxy is not null)
            return false;

        var fingerprint = TimelineFingerprint();
        if (fingerprint == _timelineProxyLooked)
            return false;

        _timelineProxyLooked = fingerprint;
        var working = VideoEncoders.Where(e => e.IsHardware).Select(e => e.Name).ToList();
        var seconds = SequenceSeconds;
        var made = await WorkScheduler.Run(
            new WorkSpec("Timeline proxy", WorkLane.Background) { Key = $"timeline|{fingerprint}", SuspendWhilePlaying = AppSettings.Current.PauseProxiesWhilePlaying },
            work => ProxyCache.MakeTimelineAsync(LocalMediaPath, fingerprint, TimelinePercent(),
                (output, percent, encode) => FilterGraphBuilder.BuildPreviewCommand(CompileExportState(), output, [(0, seconds)], percent, null, encode),
                working, seconds, CurrentProjectPath, work), _shutdown.Token);

        // Kept only if nothing changed while it was being made.
        if (made is not null && _timelineProxyLooked == fingerprint)
        {
            _timelineProxy = made;
            InvalidateLiveFilter();
        }

        return false;
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
