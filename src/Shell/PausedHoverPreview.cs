using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp.Shell;

/// <summary>
/// Live paused hover previews (Pass 5.6.5) for the Effects pane's Add Effect list, the Quick FX slots and the Fade In / Fade Out menus:
/// resting on an item for <see cref="DwellMs"/> while paused shows it on its clips (an effect card added, or the fade's style set, with
/// a default-length fade where there was none), seeking into the clip (or the fade) when the playhead is not on it; leaving the item,
/// closing the menu, choosing the item, or playback starting puts everything back as it was, playhead included unless it was moved
/// meanwhile. Nothing of a preview is an undo step: effect cards are look changes inside an owned look scope (no look auto-step), fade
/// styles show on the clips' views only (leaving reloads them from the document), so a chosen item's own step starts from the restored
/// state. Effects that switch a clip feature on (key, shadow, edges, blend mode, mask) are not previewed. UI thread only.
/// </summary>
internal sealed class PausedHoverPreview : IDisposable
{
    /// <summary>Rest (ms) on an item before its preview shows: sweeping across a menu previews nothing on the way.</summary>
    public const int DwellMs = 120;

    private readonly MainViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _dwell;

    // Undo actions of the preview showing, newest last; the pending preview waiting for its dwell.
    private readonly List<Action> _restore = [];
    private Action? _pending;

    // Playhead moved by the preview: where it was, and where the preview put it (ms).
    private (double From, double To)? _seek;

    public PausedHoverPreview(MainViewModel vm)
    {
        _vm = vm;
        _dwell = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(DwellMs) };
        _dwell.Tick += Dwell_Tick;
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    /// <summary>True while a preview is shown on the picture.</summary>
    public bool IsShowing => _restore.Count > 0;

    /// <summary>Pointer rests on an effect item: after the dwell, while paused, the effect is shown on <paramref name="layers"/>.</summary>
    public void HoverEffect(IReadOnlyList<Layer> layers, Models.Effects.EffectDefinition definition) =>
        Hover(Previewable(definition) ? () => ShowEffect(layers, definition) : null);

    /// <summary>Pointer rests on a transition style: after the dwell, while paused, the fade in (or out) of <paramref name="layers"/> takes it.</summary>
    public void HoverFade(IReadOnlyList<Layer> layers, bool fadeIn, TransitionStyle style) => Hover(() => ShowFade(layers, fadeIn, style));

    /// <summary>Pointer left the item (or the menu closed, or the item was chosen): the preview, if any, is undone.</summary>
    public void Leave()
    {
        _dwell.Stop();
        _pending = null;
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

    private void ShowFade(IReadOnlyList<Layer> layers, bool fadeIn, TransitionStyle style)
    {
        var shown = new List<(Layer View, Models.Timeline.TimelineClip Clip, double Seconds)>();
        var standard = AppSettings.Current.DefaultTransitionSeconds;
        foreach (var layer in layers)
        {
            if (layer is not { IsPicture: true, IsCaptions: false, IsLoading: false } || !_vm.Layers.Contains(layer) || _vm.ClipOf(layer) is not { } clip)
                continue;
            var (own, other) = fadeIn ? (clip.FadeInSeconds, clip.FadeOutSeconds) : (clip.FadeOutSeconds, clip.FadeInSeconds);
            shown.Add((layer, clip, own >= 0.01 ? own : Math.Round(Math.Clamp(standard, 0, Math.Max(clip.Duration - other, 0)), 2)));
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
                    (v.FadeInSeconds, v.FadeInStyle) = (seconds, style);
                else
                    (v.FadeOutSeconds, v.FadeOutStyle) = (seconds, style);
            });
        }

        _restore.Add(() =>
        {
            foreach (var (view, _, _) in shown)
                _vm.ShowViewAgain(view);
        });

        // Into the middle of the first clip's fade.
        var (_, first, fade) = shown[0];
        SeekInto(first.Start, first.End, fadeIn ? first.Start + fade / 2 : first.End - fade / 2, always: fade > 0.01);
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
        if (_seek is { } seek && Math.Abs(_vm.PositionMs - seek.To) < 1)
            _vm.PositionMs = seek.From;
        _seek = null;
    }
}
