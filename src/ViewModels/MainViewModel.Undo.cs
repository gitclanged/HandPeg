using System.ComponentModel;
using System.Windows.Interop;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// Sequence-level look not held by any clip: colour grade, sharpening and blur fill (radius, zoom, own effect stack). Stored in
/// every undo step (TimelineSnapshot.Look), in memory only.
/// </summary>
public sealed record SequenceLook(
    double Contrast, double Brightness, double Saturation, double Gamma, double Hue, double Red, double Green, double Blue,
    double Sharpen, double BlurRadius, int BlurPasses, double BackgroundDim, double BackgroundZoom = 1,
    List<Models.Effects.EffectState>? BackgroundEffects = null, bool BackgroundEffectsBypassed = false, string? LutPath = null);

// ----- Universal undo -----
// Document edits take a step on commit (OnDocumentCommitted); explicit Checkpoints cover gizmo drags, dialog visits and keyframe
// moves. All other user changes are looks (clip view placement/size/text/filters, caption box, output audio tracks, Sequence
// colour and blur fill), which take a step just before changing (PropertyChanging / OnXChanging) so it captures the prior look;
// only for user-originated changes, and once per burst (slider drag, typing).
public partial class MainViewModel
{
    // Max age of the last key/click/wheel for a change to count as user input (NoteUserInput).
    private const long UserActionWindow = 300;

    // Timestamp of the last input event and a monotonic gesture counter.
    private long _userInputAt;
    private int _inputGesture;

    // Suppresses per-look steps while a style applies under one step.
    private int _lookStepsHeld;

    // Dialog opened right after its visit step (Checkpoint, UndoMark, ShowDialog, DropStepIfUnchanged): its changes join that step
    // until another step follows.
    private byte[]? _visitStep;

    // Caption box, output tracks and dialogs are subscribed at the first user input (NoteUserInput); no user look change can precede it.
    private bool _undoSourcesHooked;

    /// <summary>Scope suppressing per-look steps during a bulk change covered by one step.</summary>
    private LookStepHold HoldLookSteps()
    {
        _lookStepsHeld++;
        return new LookStepHold(this);
    }

    private readonly struct LookStepHold(MainViewModel owner) : IDisposable
    {
        public void Dispose() => owner._lookStepsHeld--;
    }

    /// <summary>Captures the current Sequence look for an undo step.</summary>
    private SequenceLook CaptureLook() => new(
        ColorContrast, ColorBrightness, ColorSaturation, ColorGamma, ColorHue, ColorRed, ColorGreen, ColorBlue,
        SharpenStrength, BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom, CaptureBackgroundEffects(), _backgroundRow.EffectsBypassed, LutPath);

    /// <summary>Restores the Sequence look from an undo step.</summary>
    private void ApplyLook(SequenceLook look)
    {
        (ColorContrast, ColorBrightness, ColorSaturation, ColorGamma) = (look.Contrast, look.Brightness, look.Saturation, look.Gamma);
        (ColorHue, ColorRed, ColorGreen, ColorBlue, SharpenStrength) = (look.Hue, look.Red, look.Green, look.Blue, look.Sharpen);
        (BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom) = (look.BlurRadius, look.BlurPasses, look.BackgroundDim, look.BackgroundZoom);
        SetBackgroundEffects(look.BackgroundEffects, look.BackgroundEffectsBypassed);

        // Legacy LUT: undo/redo of a Convert moves it with the cards regardless of the Color tab; histories are per tab. A null
        // (older step) leaves it unchanged.
        if (look.LutPath is { } lut)
            LutPath = lut;
    }

    /// <summary>
    /// Whether looks may take steps now: false during view sync, restore, preset/project apply (command deferred), keyframe
    /// playback/recording, track propagation, reshape anchoring, crop display or write-back, style apply, open edits, Effects pane
    /// writes (own steps), and before any user input.
    /// </summary>
    private bool LookStepsShut =>
        _inputGesture == 0 || _isBackgroundWorker || _syncingViews || _applyingKeys || _recordingKey || _settingQuietly || _placingViews
        || _showingTarget || _takingCrop || _commandDeferrals > 0 || _lookStepsHeld > 0 || _openEdit is not null || _gesture.OwnedLooks > 0
        || IsRestoringTimeline;

    /// <summary>Text properties that change per keystroke.</summary>
    private static bool IsTyped(string name) => name is nameof(Layer.Name) or nameof(Layer.TextContent) or nameof(AudioTrack.Title);

    /// <summary>
    /// Whether an impending look change takes a step: only if user-originated (button held, recent key/click/wheel, typing) and not
    /// part of a burst (same property soon after with no intervening step, within 0.5 s of a manual step, same button press, or
    /// inside a dialog whose visit already took a step).
    /// </summary>
    private bool TakesLookStep(object target, string name)
    {
        // Restore or its binding settle: writes are echoes, not user changes.
        if (IsRestoringTimeline)
            return false;

        var now = Environment.TickCount64;
        var typed = IsTyped(name);
        var held = IsPointerHeld();
        if (now - _userInputAt > UserActionWindow && !held && !(typed && IsTypingInTextBox()))
            return false;

        var newest = NewestStep;
        var again = _gesture.Looks.Continues(target, name, now, typed ? TypingWindow : MergeWindow, newest);
        if (again || now - _checkpointedAt <= HandStepGrace || (_visitStep is { } visit && ReferenceEquals(newest, visit)))
            return false;

        // One held button press (slider or box drag) is one step.
        return !held || _gesture.StepPress != _inputGesture;
    }

    /// <summary>Takes a look step and records it as the current run so following changes merge.</summary>
    private void TakeLookStep(string what)
    {
        Checkpoint(what);
        _gesture.Looks.Restamp(NewestStep);
    }

    // ----- Views, the caption box -----

    /// <summary>PropertyChanging for views and the caption box: takes a step for user look changes.</summary>
    private void OnViewChanging(object? sender, PropertyChangingEventArgs e)
    {
        // Layer-raised effect value notifications (not a view property write) are not user changes.
        if (LookStepsShut || sender is not Layer { IsEmptyTrack: false, RaisingEffectChange: false } view || e.PropertyName is not { } name
            || !LayerFields.UndoableLooks.Contains(name) || !TakesLookStep(view, name))
            return;

        TakeLookStep(LookStepLabel(view, name));
    }

    /// <summary>Status bar label for a look step ("Undid: move Logo").</summary>
    private static string LookStepLabel(Layer view, string name) => name switch
    {
        nameof(Layer.Name) => $"rename {view.Name}",
        nameof(Layer.TextContent) => $"change the words of {view.Name}",
        nameof(Layer.PositionX) or nameof(Layer.PositionY) or nameof(Layer.AnchorX) or nameof(Layer.AnchorY) => $"move {view.Name}",
        nameof(Layer.SizeWidth) or nameof(Layer.SizeHeight) or nameof(Layer.LockAspectRatio) => $"resize {view.Name}",
        nameof(Layer.Rotation) => $"turn {view.Name}",
        nameof(Layer.TiltX) or nameof(Layer.TiltY) => $"tilt {view.Name} in 3D",
        nameof(Layer.CropLeft) or nameof(Layer.CropTop) or nameof(Layer.CropRight) or nameof(Layer.CropBottom) => $"crop {view.Name}",
        nameof(Layer.Opacity) => $"change the opacity of {view.Name}",
        nameof(Layer.FontFamily) or nameof(Layer.FontSize) or nameof(Layer.FontColor) or nameof(Layer.OutlineColor)
            or nameof(Layer.OutlineThickness) => $"change the lettering of {view.Name}",
        _ => $"change the look of {view.Name}",
    };

    // ----- The output audio tracks -----

    private void HookTrack(AudioTrack track)
    {
        track.PropertyChanging -= OnTrackChanging;
        track.PropertyChanging += OnTrackChanging;
    }

    /// <summary>PropertyChanging for output audio tracks (action, title): takes a step for user changes.</summary>
    private void OnTrackChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (LookStepsShut || sender is not AudioTrack track || e.PropertyName is not { } name)
            return;

        var what = name switch
        {
            nameof(AudioTrack.Action) => track.IsMuted ? $"take {track.Title} back into the output" : $"change the output of {track.Title}",
            nameof(AudioTrack.Title) => $"rename {track.Title}",
            _ => null,
        };
        if (what is not null && TakesLookStep(track, name))
            TakeLookStep(what);
    }

    // ----- Sequence look -----
    // Writes from Game Presets (held), presets/projects and undo (deferred) are not user changes.

    partial void OnColorContrastChanging(double value) => SequenceLookChanging(nameof(ColorContrast), "change the contrast");
    partial void OnColorBrightnessChanging(double value) => SequenceLookChanging(nameof(ColorBrightness), "change the brightness");
    partial void OnColorSaturationChanging(double value) => SequenceLookChanging(nameof(ColorSaturation), "change the saturation");
    partial void OnColorGammaChanging(double value) => SequenceLookChanging(nameof(ColorGamma), "change the gamma");
    partial void OnColorHueChanging(double value) => SequenceLookChanging(nameof(ColorHue), "change the hue");
    partial void OnColorRedChanging(double value) => SequenceLookChanging(nameof(ColorRed), "change the red balance");
    partial void OnColorGreenChanging(double value) => SequenceLookChanging(nameof(ColorGreen), "change the green balance");
    partial void OnColorBlueChanging(double value) => SequenceLookChanging(nameof(ColorBlue), "change the blue balance");
    partial void OnSharpenStrengthChanging(double value) => SequenceLookChanging(nameof(SharpenStrength), "change the sharpening");
    partial void OnBlurRadiusChanging(double value) => SequenceLookChanging(nameof(BlurRadius), "change the blur fill's blur");
    partial void OnBlurPassesChanging(int value) => SequenceLookChanging(nameof(BlurPasses), "change the blur fill's blur");
    partial void OnBackgroundDimChanging(double value) => SequenceLookChanging(nameof(BackgroundDim), "change the blur fill's darkening");
    partial void OnBackgroundZoomChanging(double value) => SequenceLookChanging(nameof(BackgroundZoom), "zoom the blur fill");


    private void SequenceLookChanging(string name, string what)
    {
        if (!LookStepsShut && TakesLookStep(this, name))
            TakeLookStep(what);
    }

    // ----- Subscriptions started at first user input -----

    /// <summary>
    /// Subscribes to the caption box, output audio tracks (current and future) and dialogs; called on the UI thread at the first
    /// user input, before any user change.
    /// </summary>
    private void HookUndoSources()
    {
        _undoSourcesHooked = true;
        CaptionLayer.PropertyChanging -= OnViewChanging;
        CaptionLayer.PropertyChanging += OnViewChanging;
        foreach (var track in AudioTracks)
            HookTrack(track);
        AudioTracks.CollectionChanged += (_, e) =>
        {
            foreach (var track in e.NewItems?.OfType<AudioTrack>() ?? [])
                HookTrack(track);
        };

        // Dialogs opened right after their visit step (Properties, blur settings, layer tools) merge all changes into it; dialogs
        // without one (frame preview colour controls) take steps like the main window.
        ComponentDispatcher.EnterThreadModal += (_, _) =>
            _visitStep = Environment.TickCount64 - _checkpointedAt < MergeWindow ? NewestStep : null;
        ComponentDispatcher.LeaveThreadModal += (_, _) => _visitStep = null;
    }

    // ----- Restore scope -----
    // During a document install (undo/redo, open, new: Step > RestoreTimeline > InstallDocument, nested), nothing may take a step,
    // open an edit, project captions or write Crop boxes into clips; such requests come from the restore itself. Captions project
    // once at the end from the restored anchors. Until bindings settle on the new views (dispatcher idle) or the next user input,
    // binding write-backs are echoes: no step, no edit.

    // Restore nesting depth; the outermost settle deadline (0 = none) and its generation (stale idle callbacks end nothing).
    private int _restoring;
    private long _settleEndsAt;
    private int _settleNumber;

    // Deferred end-of-restore work: caption re-projection and subtitle change notification.
    private bool _reprojectPending, _subtitlesChangedPending;

    // Maximum settle time if the dispatcher never idles (e.g. during playback).
    private const long SettleAtMost = 1000;

    /// <summary>
    /// True during a document install and until bindings settle (next dispatcher idle or <see cref="NoteUserInput"/>, whichever is
    /// first); blocks undo steps, edits, redo clearing and caption projection.
    /// </summary>
    public bool IsRestoringTimeline => _restoring > 0 || RestoreSettling;

    private bool RestoreSettling => _settleEndsAt != 0 && Environment.TickCount64 < _settleEndsAt;

    /// <summary>
    /// Raised once on the UI thread after an undo/redo or project install: views rebuilt and synced, <see cref="KeyLayer"/> remapped by
    /// clip id, captions projected. Timeline views rebuild rows and selection once. <see cref="IsRestoringTimeline"/> is still true
    /// (bindings not yet settled).
    /// </summary>
    public event Action? TimelineRestored;


    /// <summary>Begins a restore scope; disposing ends it (the outermost runs <see cref="EndRestore"/>).</summary>
    private RestoreScope BeginRestore()
    {
        _restoring++;
        return new RestoreScope(this);
    }

    private readonly struct RestoreScope(MainViewModel owner) : IDisposable
    {
        public void Dispose()
        {
            if (owner._restoring > 0 && --owner._restoring == 0)
                owner.EndRestore();
        }
    }

    /// <summary>
    /// Finishes the outermost restore: starts the settle window (until next idle), remaps the selection, projects captions once from
    /// restored anchors, raises the subtitle change once, then <see cref="TimelineRestored"/>. Faults are logged, never thrown.
    /// </summary>
    private void EndRestore()
    {
        // End any merge run: the next change takes its own step.
        _gesture.Looks.Reset();
        _gesture.Fields.Reset();

        // Start the settle first so window reactions (caption notify, row rebuild) count as echoes. Dispatcher-less VMs (background
        // workers, headless harnesses) have no bindings to settle.
        if (!_isBackgroundWorker && System.Windows.Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
        {
            var number = ++_settleNumber;
            _settleEndsAt = Math.Max(Environment.TickCount64 + SettleAtMost, 1);
            _ = dispatcher.InvokeAsync(() =>
            {
                if (number == _settleNumber)
                    _settleEndsAt = 0;
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        try
        {
            FollowSelection();
            if (_reprojectPending)
            {
                _reprojectPending = false;
                ReprojectCaptions();
            }

            if (_subtitlesChangedPending)
                NotifySubtitlesChanged();
        }
        catch (Exception ex)
        {
            NoteFault("undo", "Finishing putting the timeline back", "finishing a restore", ex);
        }
        finally
        {
            (_reprojectPending, _subtitlesChangedPending) = (false, false);
        }

        try
        {
            TimelineRestored?.Invoke();
        }
        catch (Exception ex)
        {
            NoteFault("undo", "Showing the timeline put back", "showing a restore", ex);
        }
    }

    /// <summary>
    /// Remaps <see cref="KeyLayer"/> to its clip's new view after an install (clip ids survive undo/redo); cleared if the clip is gone.
    /// </summary>
    private void FollowSelection()
    {
        if (KeyLayer is not { } selected)
            return;

        if (selected.ClipId != Guid.Empty)
        {
            var now = _views.GetValueOrDefault(selected.ClipId);
            if (!ReferenceEquals(now, selected))
                KeyLayer = now;
        }
        else if (selected.IsEmptyTrack && !_emptyTrackViews.ContainsValue(selected))
        {
            KeyLayer = null;
        }
    }

    // Words allowed after the first in a breadcrumb step label; none can identify user content.
    private static readonly HashSet<string> PlainWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "at", "in", "on", "into", "from", "out", "up", "back", "more", "again", "all", "every", "and", "s",
        "clip", "clips", "track", "tracks", "cut", "cuts", "speed", "volume", "easing", "keyframe", "keyframes", "silence", "folded",
        "stretch", "stretches", "dead", "air", "subtitles", "fade", "fades", "crossfade", "placement", "frame", "main", "video",
    };

    /// <summary>
    /// Privacy-safe breadcrumb label for a step or edit: the first word plus following allow-listed words/numbers, stopping before
    /// anything that could be a clip name, file name or title.
    /// </summary>
    private static string NoteOf(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(1).Concat(words.Skip(1).TakeWhile(w =>
            PlainWords.Contains(w) || double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))));
    }

    /// <summary>
    /// Records a contained fault (edit, undo step): full log entry with stack (<paramref name="what"/> may contain clip names; the
    /// log is local only) plus a name-free breadcrumb.
    /// </summary>
    private static void NoteFault(string category, string what, string note, Exception ex)
    {
        AppLog.Write($"{what} failed: {ex}");
        DevFlightRecorder.Record(category, $"{what} failed: {ex}");
        CrashReporter.Note(category, $"{note} failed: {ex.GetType().Name}");
    }
}
