using System.IO;
using System.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>
/// The engine side of a looping hover preview (Pass 5.7.1): MainWindow's GPU preview engine, or a recorder in the goldens. UI thread.
/// </summary>
internal interface ILoopPreviewHost
{
    /// <summary>Pre-decodes a window at idle priority while a menu is open (the menu opened over its target).</summary>
    void Warm(double from, double to);

    /// <summary>Drops the pre-warm request (the menu closed).</summary>
    void EndWarm();

    /// <summary>Loops a window of the Sequence on the engine, picture and sound; the view model stays paused. False when it cannot.</summary>
    bool Start(double from, double to);

    /// <summary>Ends the loop and parks the engine at <paramref name="parkAt"/> (s); <paramref name="keepPlaying"/> when the user pressed play meanwhile.</summary>
    void Stop(double parkAt, bool keepPlaying);
}

/// <summary>
/// Live paused hover previews (Pass 5.6.5) for the Effects pane's Add Effect list, the Quick FX slots and the timeline's menus: resting on
/// an item for <see cref="DwellMs"/> while paused shows it on its clips (an effect card added; a fade's style or length; a cut's
/// crossfade and its gain law; a clip's gain, Auto-Level's gain, or ducking switched), seeking into the clip (or the fade) when the
/// playhead is not on it. With a <see cref="Loop"/> host (Pass 5.7.1) the fade, cut or stretch instead loops on the GPU engine round
/// its target (<see cref="GpuPreviewEngine.LoopWindowOf"/>) while the playhead stays parked. Leaving the item, closing the menu, choosing
/// the item, or playback starting puts everything back as it was, playhead included unless it was moved meanwhile. Nothing of a preview
/// is an undo step or makes the project dirty: effect cards are look changes inside an owned look scope (no look auto-step), clip values
/// show on the clips' views only (leaving reloads them from the document), so a chosen item's own step starts from the restored state.
/// Effects that switch a clip feature on (key, shadow, edges, blend mode, mask) are not previewed. UI thread only.
/// </summary>
internal sealed class PausedHoverPreview : IDisposable
{
    /// <summary>Rest (ms) on an item before its preview shows: sweeping across a menu previews nothing on the way.</summary>
    public const int DwellMs = 120;

    /// <summary>A stretch of a clip a gain or ducking preview loops (s), from the playhead when it is on the clip.</summary>
    public const double SoundLoopSeconds = 2.0;

    private readonly MainViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _dwell;

    // Undo actions of the preview showing, newest last; the pending preview waiting for its dwell.
    private readonly List<Action> _restore = [];
    private Action? _pending;

    // Playhead moved by the preview: where it was, and where the preview put it (ms).
    private (double From, double To)? _seek;

    // Auto-Level gains measured for hover previews, by what the measure hears (file, audio stream, media offset and span: a stream, trim or
    // speed change measures again), the newest LevelCacheSize kept, no clip view held (review F33).
    private readonly Dictionary<LevelKey, double?> _levels = [];

    // Auto-Level measurements running, one per source and span (review F32): a hover meanwhile waits for it; MenuClosed cancels them.
    private readonly Dictionary<LevelKey, (Task<double?> Task, CancellationTokenSource Cancel)> _scans = [];
    private readonly Queue<LevelKey> _levelOrder = new();
    private const int LevelCacheSize = 16;

    private readonly record struct LevelKey(string Path, int? Stream, double From, double Span);

    // Bumped by every hover and leave: an Auto-Level measurement that finishes after its item was left shows nothing.
    private int _hoverSerial;

    public PausedHoverPreview(MainViewModel vm)
    {
        _vm = vm;
        _dwell = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(DwellMs) };
        _dwell.Tick += Dwell_Tick;
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    /// <summary>True while a preview is shown on the picture.</summary>
    public bool IsShowing => _restore.Count > 0;

    /// <summary>The engine a fade, cut or stretch loops on while previewed (Pass 5.7.1); null: the playhead is moved into it instead.</summary>
    public ILoopPreviewHost? Loop { get; set; }

    /// <summary>The window looping now, null when none (goldens and the status line).</summary>
    public (double From, double To)? Looping { get; private set; }

    /// <summary>Pointer rests on an effect item: after the dwell, while paused, the effect is shown on <paramref name="layers"/>.</summary>
    public void HoverEffect(IReadOnlyList<Layer> layers, Models.Effects.EffectDefinition definition) =>
        Hover(Previewable(definition) ? () => ShowEffect(layers, definition) : null);

    /// <summary>Pointer rests on a transition style: after the dwell, while paused, the fade in (or out) of <paramref name="layers"/> takes it.</summary>
    public void HoverFade(IReadOnlyList<Layer> layers, bool fadeIn, TransitionStyle style) => Hover(() => ShowFade(layers, fadeIn, style, null));

    /// <summary>Pointer rests on a fade length: after the dwell, while paused, the fade in (or out) of <paramref name="layers"/> takes it (0: none).</summary>
    public void HoverFadeLength(IReadOnlyList<Layer> layers, bool fadeIn, double seconds) => Hover(() => ShowFade(layers, fadeIn, null, Math.Max(seconds, 0)));

    /// <summary>
    /// Pointer rests on a Seam menu item: after the dwell, while paused, the cut where <paramref name="left"/> meets <paramref name="right"/>
    /// crossfades over <paramref name="seconds"/> (null keeps its length; 0 a plain cut) by <paramref name="curve"/> (null keeps its curve).
    /// </summary>
    public void HoverSeam(Layer left, Layer right, double? seconds, CrossfadeCurve? curve) => Hover(() => ShowSeam(left, right, seconds, curve));

    /// <summary>Pointer rests on a gain: after the dwell, while paused, <paramref name="clip"/> plays at <paramref name="gainDb"/>.</summary>
    public void HoverGain(Layer clip, double gainDb) => Hover(() => ShowGain(clip, gainDb));

    /// <summary>
    /// Pointer rests on Auto-Level: after the dwell, while paused, <paramref name="measure"/> (once per clip and span, in the background)
    /// gives the gain reaching -14 LUFS, and <paramref name="clip"/> plays at it once measured, unless the item was left meanwhile.
    /// </summary>
    public void HoverAutoLevel(Layer clip, Func<Task<double?>> measure) => HoverAutoLevel(clip, _ => measure());

    /// <summary>As above, the measurement told to stop (its token) once no preview needs it: the menu closed (review F32).</summary>
    public void HoverAutoLevel(Layer clip, Func<CancellationToken, Task<double?>> measure) => Hover(() => ShowAutoLevel(clip, measure));

    /// <summary>
    /// Pointer rests on Auto-Duck: after the dwell, while paused, <paramref name="clip"/> plays with Auto-Duck <paramref name="ducks"/> set on
    /// <paramref name="owner"/> (its row's layer, whose setting it is).
    /// </summary>
    public void HoverDucking(Layer owner, Layer clip, bool ducks) => Hover(() => ShowDucking(owner, clip, ducks));

    /// <summary>A menu opened over a target: its window is pre-warmed on the engine (nothing without a <see cref="Loop"/> host).</summary>
    public void Warm(double from, double to)
    {
        if (Loop is { } host && GpuPreviewEngine.LoopWindowOf(from, to, _vm.SequenceSeconds, _vm.SequenceFps) is var (start, stop))
            host.Warm(start, stop);
    }

    /// <summary>The menu closed: the preview, if any, is undone and the pre-warm dropped.</summary>
    public void MenuClosed()
    {
        Leave();
        Loop?.EndWarm();

        // No preview needs a running Auto-Level measurement now (review F32).
        foreach (var scan in _scans.Values)
            scan.Cancel.Cancel();
    }

    /// <summary>Pointer left the item (or the menu closed, or the item was chosen): the preview, if any, is undone.</summary>
    public void Leave()
    {
        _dwell.Stop();
        _pending = null;
        _hoverSerial++;
        Restore();
    }

    public void Dispose()
    {
        Leave();
        _dwell.Tick -= Dwell_Tick;
        _vm.PropertyChanged -= Vm_PropertyChanged;
    }

    /// <summary>
    /// Effects a preview can add and take away cleanly: a card of its own, not a clip feature switched on (key, shadow, edges), a blend
    /// mode or a mask (which opens the mask editor).
    /// </summary>
    public static bool Previewable(Models.Effects.EffectDefinition definition) =>
        definition is { Pinned: false, Feature: null } && definition.Type != Models.Effects.EffectRegistry.BlendMode
        && definition.Type != Models.Effects.EffectRegistry.Mask;

    /// <summary>Test seam: shows the pending preview now, as its dwell would (goldens drive no dispatcher timer).</summary>
    internal void ShowPendingNow()
    {
        if (_pending is not null)
            Dwell_Tick(null, EventArgs.Empty);
    }

    private void Hover(Action? show)
    {
        Leave();
        if (show is null || _vm.IsPlaying)
            return;

        _pending = show;
        _dwell.Start();
    }

    private void Dwell_Tick(object? sender, EventArgs e)
    {
        _dwell.Stop();
        var show = _pending;
        _pending = null;
        if (show is null || _vm.IsPlaying)
            return;

        try
        {
            show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AppLog.Write("Hover preview", ex);
            Restore();
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying) && _vm.IsPlaying)
            Leave();
    }

    private void ShowEffect(IReadOnlyList<Layer> layers, Models.Effects.EffectDefinition definition)
    {
        var targets = layers.Where(l => l is { HasFilters: true, IsLoading: false, IsDeleted: false } && _vm.Layers.Contains(l) && l.CanTake(definition)).ToList();
        if (targets.Count == 0)
            return;

        foreach (var layer in targets)
        {
            Models.Effects.EffectInstance card;
            using (_vm.OwnLookScope())
                card = layer.AddEffect(definition.NewEffect());
            _restore.Add(() =>
            {
                using (_vm.OwnLookScope())
                    layer.Effects.Remove(card);
            });
        }

        if (_vm.ClipOf(targets[0]) is { } clip)
            SeekInto(clip.Start, clip.End, clip.Start + clip.Duration * 0.25);
    }

    // A fade's style (null keeps it) or length (null keeps it, or the default length where there is none) on the views of `layers`.
    private void ShowFade(IReadOnlyList<Layer> layers, bool fadeIn, TransitionStyle? style, double? length)
    {
        var shown = new List<(Layer View, Models.Timeline.TimelineClip Clip, double Seconds)>();
        var standard = AppSettings.Current.DefaultTransitionSeconds;
        foreach (var layer in layers)
        {
            if (layer is not { IsLoading: false } || !_vm.Layers.Contains(layer) || _vm.ClipOf(layer) is not { } clip)
                continue;
            if (style is not null && layer is not { IsPicture: true, IsCaptions: false })
                continue;
            var (own, other) = fadeIn ? (clip.FadeInSeconds, clip.FadeOutSeconds) : (clip.FadeOutSeconds, clip.FadeInSeconds);
            var room = Math.Max(clip.Duration - other, 0);
            var seconds = length is { } asked ? Math.Round(Math.Clamp(asked, 0, room), 2) : own >= 0.01 ? own : Math.Round(Math.Clamp(standard, 0, room), 2);
            shown.Add((layer, clip, seconds));
        }

        if (shown.Count == 0)
            return;

        // On the views only, as a sync shows them (no edit: a save or an Undo during the hover never sees the preview); leaving reloads
        // each view from its clip as the document then holds it. Choosing the fade commits it through its own Edit.
        foreach (var (view, _, seconds) in shown)
        {
            _vm.PreviewOnView(view, v =>
            {
                if (fadeIn)
                    (v.FadeInSeconds, v.FadeInStyle) = (seconds, style ?? v.FadeInStyle);
                else
                    (v.FadeOutSeconds, v.FadeOutStyle) = (seconds, style ?? v.FadeOutStyle);
            });
        }

        _restore.Add(() =>
        {
            foreach (var (view, _, _) in shown)
                _vm.ShowViewAgain(view);
        });

        // Loops round the first clip's fade (or moves the playhead into its middle).
        var (_, first, fade) = shown[0];
        var (from, to) = fadeIn ? (first.Start, first.Start + fade) : (first.End - fade, first.End);
        PlayRound(from, to, first.Start, first.End, fadeIn ? first.Start + fade / 2 : first.End - fade / 2, always: fade > 0.01);
    }

    private void ShowSeam(Layer left, Layer right, double? seconds, CrossfadeCurve? curve)
    {
        if (!_vm.Layers.Contains(left) || !_vm.Layers.Contains(right) || _vm.ClipOf(left) is not { } outgoing || _vm.ClipOf(right) is not { } incoming)
            return;

        var length = Math.Round(Math.Clamp(seconds ?? incoming.TransitionIn, 0, Math.Min(outgoing.Duration, incoming.Duration)), 3);

        // The curves are look values a reload from the clip does not touch: put back as they were, then the clip's values reloaded.
        var (wasIn, wasCurveIn, wasCurveOut) = (right.TransitionIn, right.CrossfadeInCurve, left.CrossfadeOutCurve);
        _vm.PreviewOnView(right, v =>
        {
            v.TransitionIn = length;
            if (curve is { } law)
                v.CrossfadeInCurve = law;
        });
        if (curve is { } outLaw)
            _vm.PreviewOnView(left, v => v.CrossfadeOutCurve = outLaw);
        _restore.Add(() =>
        {
            _vm.PreviewOnView(right, v => (v.TransitionIn, v.CrossfadeInCurve) = (wasIn, wasCurveIn));
            _vm.PreviewOnView(left, v => v.CrossfadeOutCurve = wasCurveOut);
            _vm.ShowViewAgain(right);
            _vm.ShowViewAgain(left);
        });

        // Round the cut: the crossfade's middle is the cut.
        var cut = incoming.Start;
        PlayRound(cut - length / 2, cut + length / 2, outgoing.Start, incoming.End, cut, always: true);
    }

    private void ShowGain(Layer clip, double gainDb)
    {
        if (!_vm.Layers.Contains(clip) || _vm.ClipOf(clip) is not { } model)
            return;

        var was = clip.GainDb;
        _vm.PreviewOnView(clip, v => v.GainDb = gainDb);
        _restore.Add(() =>
        {
            _vm.PreviewOnView(clip, v => v.GainDb = was);
            _vm.ShowViewAgain(clip);
        });
        PlayStretch(model.Start, model.End);
    }

    private void ShowAutoLevel(Layer clip, Func<CancellationToken, Task<double?>> measure)
    {
        if (!_vm.Layers.Contains(clip) || _vm.ClipOf(clip) is not { } model)
            return;

        var key = new LevelKey(model.SourcePath ?? "", model.AudioStreamIndex, clip.MediaOffset, MeasuredMediaSpan(clip));
        if (_levels.TryGetValue(key, out var known))
        {
            if (known is { } gain)
                ShowGain(clip, gain);
            return;
        }

        var serial = _hoverSerial;

        // One measurement per source and span at a time (review F32): a hover while it runs waits for it rather than scanning again.
        if (!_scans.TryGetValue(key, out var scan))
        {
            var cancel = new CancellationTokenSource();
            scan = (Started(measure, cancel.Token), cancel);
            _scans[key] = scan;
        }

        _ = MeasureThenShow(scan);

        static Task<double?> Started(Func<CancellationToken, Task<double?>> measure, CancellationToken token)
        {
            try
            {
                return measure(token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Task.FromException<double?>(ex);
            }
        }

        async Task MeasureThenShow((Task<double?> Task, CancellationTokenSource Cancel) running)
        {
            double? gain;
            try
            {
                gain = await running.Task;
            }
            catch (OperationCanceledException) when (running.Cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException
                                           or NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                AppLog.Write("Hover preview: Auto-Level", ex);
                return;
            }
            finally
            {
                // Over: the next hover finds its level remembered, or measures again.
                if (_scans.TryGetValue(key, out var now) && ReferenceEquals(now.Task, running.Task))
                {
                    _scans.Remove(key);
                    now.Cancel.Dispose();
                }
            }

            RememberLevel(key, gain);
            if (serial == _hoverSerial && gain is { } level && !_vm.IsPlaying && !IsShowing)
                ShowGain(clip, level);
        }
    }

    // Keeps a measured gain, dropping the oldest beyond LevelCacheSize.
    private void RememberLevel(LevelKey key, double? gain)
    {
        if (!_levels.ContainsKey(key))
        {
            _levelOrder.Enqueue(key);
            while (_levelOrder.Count > LevelCacheSize)
                _levels.Remove(_levelOrder.Dequeue());
        }

        _levels[key] = gain;
    }

    /// <summary>The media seconds an Auto-Level measure hears for <paramref name="clip"/>: its span through its speed or Speed Ramp.</summary>
    internal static double MeasuredMediaSpan(Layer clip) =>
        clip.HasSpeedRamp ? clip.MediaSecondsAt(clip.Duration) : clip.Duration * Math.Max(clip.Speed, Layer.SlowestSpeed);

    // Auto-Duck is the row's setting (owner), heard on the clicked clip: over a voice within that clip's interval, else its stretch (F30).
    private void ShowDucking(Layer owner, Layer clip, bool ducks)
    {
        if (!_vm.Layers.Contains(owner) || !_vm.Layers.Contains(clip) || _vm.ClipOf(clip) is not { } model)
            return;

        var was = owner.AutoDuck;
        _vm.PreviewOnView(owner, v => v.AutoDuck = ducks);
        _restore.Add(() =>
        {
            _vm.PreviewOnView(owner, v => v.AutoDuck = was);
            _vm.ShowViewAgain(owner);
        });

        // Where a voice speaks over it, if one does: that is where ducking is heard.
        var voice = _vm.Layers.Where(l => l is { IsVoice: true, CarriesSound: true, IsHidden: false } && !ReferenceEquals(l, owner) && !ReferenceEquals(l, clip))
            .Select(_vm.ClipOf).OfType<Models.Timeline.TimelineClip>()
            .Where(c => c.Start < model.End && c.End > model.Start).OrderBy(c => c.Start).FirstOrDefault();
        if (voice is not null)
        {
            var from = Math.Max(voice.Start, model.Start);
            PlayRound(from, Math.Min(from + SoundLoopSeconds, Math.Min(voice.End, model.End)), model.Start, model.End, from, always: false);
        }
        else
        {
            PlayStretch(model.Start, model.End);
        }
    }

    // A clip's stretch to hear: SoundLoopSeconds from the playhead when it is on the clip, else from the clip's start.
    private void PlayStretch(double start, double end)
    {
        var at = _vm.PositionMs / 1000;
        var from = at >= start && at < end ? at : start;
        PlayRound(from, Math.Min(from + SoundLoopSeconds, end), start, end, from, always: false);
    }

    // Loops [from, to] on the engine when a host can (the playhead stays parked), else moves the playhead into [start, end) at `seconds`.
    private void PlayRound(double from, double to, double start, double end, double seconds, bool always)
    {
        if (Loop is { } host && GpuPreviewEngine.LoopWindowOf(from, to, _vm.SequenceSeconds, _vm.SequenceFps) is var (loopFrom, loopTo)
            && host.Start(loopFrom, loopTo))
        {
            Looping = (loopFrom, loopTo);
            _restore.Add(() =>
            {
                Looping = null;
                // Parked where the playhead is now: a seek made during the audition stands (review F35).
                host.Stop(_vm.PositionMs / 1000, keepPlaying: _vm.IsPlaying);
            });
            return;
        }

        SeekInto(start, end, seconds, always);
    }

    // Moves the playhead to `seconds` when it is outside [start, end) (or always), remembering where it was.
    private void SeekInto(double start, double end, double seconds, bool always = false)
    {
        var at = _vm.PositionMs / 1000;
        if (!always && at >= start && at < end)
            return;

        var from = _vm.PositionMs;
        _vm.PositionMs = Math.Clamp(seconds, start, Math.Max(start, end - 0.001)) * 1000;
        _seek = (from, _vm.PositionMs);
    }

    private void Restore()
    {
        for (var i = _restore.Count - 1; i >= 0; i--)
        {
            try
            {
                _restore[i]();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                AppLog.Write("Hover preview restore", ex);
            }
        }

        _restore.Clear();
        Looping = null;
        if (_seek is { } seek && Math.Abs(_vm.PositionMs - seek.To) < 1)
            _vm.PositionMs = seek.From;
        _seek = null;
    }
}
