using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// The sequence's own look, which no clip holds (4.4): the colour grade, the sharpening and the blurred background (5.0-Release:
/// its continuous radius, its zoom, and its own effect stack). Kept with every undo step (TimelineSnapshot.Look), in memory only.
/// </summary>
public sealed record SequenceLook(
    double Contrast, double Brightness, double Saturation, double Gamma, double Hue, double Red, double Green, double Blue,
    double Sharpen, double BlurRadius, int BlurPasses, double BackgroundDim, double BackgroundZoom = 1,
    List<Models.Effects.EffectState>? BackgroundEffects = null, bool BackgroundEffectsBypassed = false);

// ----- Universal undo (4.4) -----
// An edit of the document takes its own step (OnDocumentCommitted), and so does whatever takes one by hand (Checkpoint):
// a gizmo dragged, a dialog's visit, a keyframe moved. Everything else the user changes is a look: of a clip's view (its
// place, size, text, filters), of the caption box, of one of the main file's audio tracks, or of the sequence (its colour,
// its blurred background, the Center controls). Each of those takes a step just before it changes (PropertyChanging, or
// the toolkit's OnXChanging), so the step holds the look as it was; but only when it is the user changing it, and once for
// a burst of changes: a slider dragged, a box typed into, a name typed letter by letter.
public partial class MainViewModel
{
    // How recent a key, click or turn of the wheel must be for a change to be the user's (NoteUserInput).
    private const long UserActionWindow = 300;

    // A change of the same value of the same thing within this long of the last one, with no other step taken between, is
    // part of the same step: a slider dragged, the arrow keys held on a box, the wheel turned.
    private const long LookBurst = 1500;

    // The same for a name or words typed letter by letter: one step for the whole of it, as a marker's rename has.
    private const long TypingBurst = 30_000;

    // When the user last pressed a key, a button or turned the wheel, and how many times they have (each one a gesture).
    private long _userInputAt;
    private int _inputGesture;

    // The gesture during which the last step was taken by hand: one press of the button held down is one step at most.
    private int _stepGesture = -1;

    // The last look the user changed: what, which value, when, and the newest step at that moment.
    private (object Target, string Name, long At, byte[]? Newest)? _lastLookEdit;

    // While set, no look takes a step of its own: a style is being applied, which took one for all it does.
    private int _lookStepsHeld;

    // A dialog opened just after a step was taken for its visit (Checkpoint, UndoMark, ShowDialog, DropStepIfUnchanged): what
    // is changed in it belongs to that step, for as long as no other step follows it.
    private byte[]? _visitStep;

    // The caption box, the main file's audio tracks and the dialogs are listened to from the user's first touch (NoteUserInput):
    // a look changes by the user's hand only after a touch of theirs.
    private bool _undoSourcesHooked;

    /// <summary>Holds back the looks' own steps while a whole set of them is put in place by something that took one for all.</summary>
    private LookStepHold HoldLookSteps()
    {
        _lookStepsHeld++;
        return new LookStepHold(this);
    }

    private readonly struct LookStepHold(MainViewModel owner) : IDisposable
    {
        public void Dispose() => owner._lookStepsHeld--;
    }

    /// <summary>The sequence's look as it is now, for an undo step.</summary>
    private SequenceLook CaptureLook() => new(
        ColorContrast, ColorBrightness, ColorSaturation, ColorGamma, ColorHue, ColorRed, ColorGreen, ColorBlue,
        SharpenStrength, BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom, CaptureBackgroundEffects(), _backgroundRow.EffectsBypassed);

    /// <summary>Puts the sequence's look back as an undo step had it.</summary>
    private void ApplyLook(SequenceLook look)
    {
        (ColorContrast, ColorBrightness, ColorSaturation, ColorGamma) = (look.Contrast, look.Brightness, look.Saturation, look.Gamma);
        (ColorHue, ColorRed, ColorGreen, ColorBlue, SharpenStrength) = (look.Hue, look.Red, look.Green, look.Blue, look.Sharpen);
        (BlurRadius, BlurPasses, BackgroundDim, BackgroundZoom) = (look.BlurRadius, look.BlurPasses, look.BackgroundDim, look.BackgroundZoom);
        SetBackgroundEffects(look.BackgroundEffects, look.BackgroundEffectsBypassed);
    }

    /// <summary>
    /// Whether a look may take a step at all now: not while the views are being written from the document, a step is being
    /// put back, a project or a preset is being put in place (the command deferred), keyframes are being played or recorded,
    /// a value is being copied along a track, the main video is being placed or shown in the Center controls, the crop is
    /// being written into clips, a style is being applied, an edit is open, or the Effects pane is writing (it keeps steps of
    /// its own). Nor before the user has touched anything at all.
    /// </summary>
    private bool LookStepsShut =>
        _inputGesture == 0 || _isBackgroundWorker || _syncingViews || _applyingKeys || _recordingKey || _copyingToTrack || _placingMain
        || _showingTarget || _takingCrop || _commandDeferrals > 0 || _lookStepsHeld > 0 || _openEdit is not null || Layer.IsEchoing
        || IsRestoringTimeline;

    /// <summary>A name or words: typed letter by letter, each letter a change.</summary>
    private static bool IsTyped(string name) => name is nameof(Layer.Name) or nameof(Layer.TextContent) or nameof(AudioTrack.Title);

    /// <summary>
    /// Whether the user's change of a look (about to be made) takes an undo step first. Only when it is the user's (the left
    /// button held, a key, click or turn of the wheel a moment ago, or a name typed into a box), and once for a burst: the same
    /// value of the same thing again soon after with no other step between, anything within half a second of a step taken by
    /// hand, anything more while one press of the button is held, anything in a dialog whose visit is a step already.
    /// </summary>
    private bool TakesLookStep(object target, string name)
    {
        // A step being put back, or its bindings settling: what they write is theirs, never the user's (4.4-BugHunt).
        if (IsRestoringTimeline)
            return false;

        var now = Environment.TickCount64;
        var typed = IsTyped(name);
        var onInterfaceThread = System.Windows.Application.Current?.Dispatcher.CheckAccess() == true;
        var held = onInterfaceThread && Mouse.LeftButton == MouseButtonState.Pressed;
        if (now - _userInputAt > UserActionWindow && !held && !(typed && onInterfaceThread && Keyboard.FocusedElement is TextBoxBase))
            return false;

        var newest = _undo.Count > 0 ? _undo[^1].State : null;
        var again = _lastLookEdit is (var lastTarget, var lastName, var at, var then) && ReferenceEquals(lastTarget, target)
            && lastName == name && ReferenceEquals(then, newest) && now - at < (typed ? TypingBurst : LookBurst);
        _lastLookEdit = (target, name, now, newest);
        if (again || now - _checkpointedAt <= 500 || (_visitStep is { } visit && ReferenceEquals(newest, visit)))
            return false;

        // One press of the button held down (a slider or a box dragged) is one step, however long it is held.
        return !held || _stepGesture != _inputGesture;
    }

    /// <summary>Takes the step for a look about to change, and remembers it as the newest, so the rest of its burst joins it.</summary>
    private void TakeLookStep(object target, string name, string what)
    {
        Checkpoint(what);
        _lastLookEdit = (target, name, Environment.TickCount64, _undo.Count > 0 ? _undo[^1].State : null);
    }

    // ----- Views, the caption box -----

    /// <summary>A view's (or the caption box's) value is about to change: a step first, when it is part of its look and the user's change.</summary>
    private void OnViewChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (LookStepsShut || sender is not Layer { IsEmptyTrack: false } view || e.PropertyName is not { } name || !UndoLooks.Names.Contains(name)
            || !TakesLookStep(view, name))
            return;

        TakeLookStep(view, name, LookStepLabel(view, name));
    }

    /// <summary>What a look's step is called in the status bar ("Undid: move Logo").</summary>
    private static string LookStepLabel(Layer view, string name) => name switch
    {
        nameof(Layer.Name) => $"rename {view.Name}",
        nameof(Layer.TextContent) => $"change the words of {view.Name}",
        nameof(Layer.PositionX) or nameof(Layer.PositionY) or nameof(Layer.AnchorX) or nameof(Layer.AnchorY) => $"move {view.Name}",
        nameof(Layer.SizeWidth) or nameof(Layer.SizeHeight) or nameof(Layer.LockAspectRatio) => $"resize {view.Name}",
        nameof(Layer.Rotation) => $"turn {view.Name}",
        nameof(Layer.CropLeft) or nameof(Layer.CropTop) or nameof(Layer.CropRight) or nameof(Layer.CropBottom) => $"crop {view.Name}",
        nameof(Layer.Opacity) => $"change the opacity of {view.Name}",
        nameof(Layer.FontFamily) or nameof(Layer.FontSize) or nameof(Layer.FontColor) or nameof(Layer.OutlineColor)
            or nameof(Layer.OutlineThickness) => $"change the lettering of {view.Name}",
        _ => $"change the look of {view.Name}",
    };

    /// <summary>
    /// The values of a view that are its look: what a LayerState holds, less a clip's own values (their changes are edits of
    /// the document, with steps of their own) and what is only the view's. Made at the first change the user makes, not before.
    /// </summary>
    private static class UndoLooks
    {
        public static readonly HashSet<string> Names = Make();

        private static HashSet<string> Make()
        {
            var names = typeof(LayerState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanWrite).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            names.ExceptWith(ClipFields);
            names.ExceptWith(ViewOnly);
            // What a file is (found by probing it, never set by hand).
            names.ExceptWith([nameof(LayerState.Kind), nameof(LayerState.ImagePath), nameof(LayerState.ImageWidth), nameof(LayerState.ImageHeight),
                nameof(LayerState.HasAudio), nameof(LayerState.AudioCodec), nameof(LayerState.AudioChannels)]);
            return names;
        }
    }

    // ----- The main file's audio tracks -----

    private void HookTrack(AudioTrack track)
    {
        track.PropertyChanging -= OnTrackChanging;
        track.PropertyChanging += OnTrackChanging;
    }

    /// <summary>One of the main file's audio tracks is about to change (its gain, muting, filters, ducking, voice or title): a step first, when it is the user's change.</summary>
    private void OnTrackChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (LookStepsShut || sender is not AudioTrack track || e.PropertyName is not { } name)
            return;

        var what = name switch
        {
            nameof(AudioTrack.GainDb) => $"change the volume of {track.Title}",
            nameof(AudioTrack.Action) => track.IsMuted ? $"unmute {track.Title}" : $"change the output of {track.Title}",
            nameof(AudioTrack.Filters) => $"change the audio filters of {track.Title}",
            nameof(AudioTrack.AutoDuck) or nameof(AudioTrack.IsVoice) => $"change the ducking of {track.Title}",
            nameof(AudioTrack.Title) => $"rename {track.Title}",
            _ => null,
        };
        if (what is not null && TakesLookStep(track, name))
            TakeLookStep(track, name, what);
    }

    // ----- The sequence's look and the Center controls -----
    // Set by a style (held), a preset or a project (deferred), an undo step (deferred), or by the Center controls showing
    // another clip (_showingTarget) or following one (_placingMain): none of those is the user's change.

    partial void OnColorContrastChanging(double value) => SequenceLookChanging(nameof(ColorContrast), "change the contrast");
    partial void OnColorBrightnessChanging(double value) => SequenceLookChanging(nameof(ColorBrightness), "change the brightness");
    partial void OnColorSaturationChanging(double value) => SequenceLookChanging(nameof(ColorSaturation), "change the saturation");
    partial void OnColorGammaChanging(double value) => SequenceLookChanging(nameof(ColorGamma), "change the gamma");
    partial void OnColorHueChanging(double value) => SequenceLookChanging(nameof(ColorHue), "change the hue");
    partial void OnColorRedChanging(double value) => SequenceLookChanging(nameof(ColorRed), "change the red balance");
    partial void OnColorGreenChanging(double value) => SequenceLookChanging(nameof(ColorGreen), "change the green balance");
    partial void OnColorBlueChanging(double value) => SequenceLookChanging(nameof(ColorBlue), "change the blue balance");
    partial void OnSharpenStrengthChanging(double value) => SequenceLookChanging(nameof(SharpenStrength), "change the sharpening");
    partial void OnBlurRadiusChanging(double value) => SequenceLookChanging(nameof(BlurRadius), "change the background blur");
    partial void OnBlurPassesChanging(int value) => SequenceLookChanging(nameof(BlurPasses), "change the background blur");
    partial void OnBackgroundDimChanging(double value) => SequenceLookChanging(nameof(BackgroundDim), "change the background's darkening");
    partial void OnBackgroundZoomChanging(double value) => SequenceLookChanging(nameof(BackgroundZoom), "zoom the background");

    // The Center controls are a view of one clip of the main video (4.4): typed or slid, that clip's place changes.
    partial void OnCenterZoomChanging(double value) => SequenceLookChanging(nameof(CenterZoom), "zoom the main video");
    partial void OnCenterOffsetXChanging(double value) => SequenceLookChanging(nameof(CenterOffsetX), "move the main video");
    partial void OnCenterOffsetYChanging(double value) => SequenceLookChanging(nameof(CenterOffsetY), "move the main video");

    private void SequenceLookChanging(string name, string what)
    {
        if (!LookStepsShut && TakesLookStep(this, name))
            TakeLookStep(this, name, what);
    }

    // ----- What is listened to, from the user's first touch -----

    /// <summary>
    /// Listens to the caption box, the main file's audio tracks (those there and those to come) and the dialogs: called on the
    /// interface's thread at the user's first touch, before any change of theirs can be made.
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

        // A dialog opened right after a step was taken for it (a Properties visit, the blur settings, a layer tool): what is
        // changed in it is that step's, however long it stays open. One opened without one (the frame preview's colour
        // controls) takes steps as the main window does.
        ComponentDispatcher.EnterThreadModal += (_, _) =>
            _visitStep = Environment.TickCount64 - _checkpointedAt < LookBurst && _undo.Count > 0 ? _undo[^1].State : null;
        ComponentDispatcher.LeaveThreadModal += (_, _) => _visitStep = null;
    }

    // ----- Putting a step back (4.4-BugHunt) -----
    // While a document is being put in place (an undo or redo step, a project opened, a new one: Step, RestoreTimeline and
    // InstallDocument, each nesting in the one before), nothing may take an undo step, open an edit, project the captions or
    // write the Center controls and the crop into clips: what asks then is the restore itself, or something it set going, and
    // it would land in the middle of it. The captions are projected once, as it ends, from the anchors put back. And from its
    // end until the window's bindings have taken the new views (the dispatcher idle again), or the user's next touch, what
    // they write back is their echo, not the user's change: it takes no step and makes no edit.

    // How many restores are under way (they nest); and the outermost's settling: until when at the latest (0: none), and
    // which restore's it is (an older one's idle moment ends nothing).
    private int _restoring;
    private long _settleEndsAt;
    private int _settleNumber;

    // What the outermost restore does once, as it ends: the captions projected again, the subtitles' change told.
    private bool _reprojectPending, _subtitlesChangedPending;

    // The longest the bindings are given to settle, should the dispatcher not go idle sooner (playback keeps it busy).
    private const long SettleAtMost = 1000;

    /// <summary>
    /// Whether a document is being put in place (an undo or redo step, a project opened, a new one) or has just been, the
    /// window's bindings not yet settled on its views: nothing that could take an undo step, start an edit, clear the redo
    /// history or project the captions may then. The settling ends at the dispatcher's next idle moment after the restore,
    /// or at the user's next touch (<see cref="NoteUserInput"/>), whichever comes first.
    /// </summary>
    public bool IsRestoringTimeline => _restoring > 0 || RestoreSettling;

    private bool RestoreSettling => _settleEndsAt != 0 && Environment.TickCount64 < _settleEndsAt;

    /// <summary>
    /// Raised once on the interface's thread when an undo or redo step, or a project, has been put in place: every view made
    /// afresh and synced, the selection (<see cref="KeyLayer"/>) moved to its clip's new view by id, the captions projected.
    /// What shows the timeline rebuilds its rows and its selection (by clip id) then, once. <see cref="IsRestoringTimeline"/>
    /// is still true while it is raised: the bindings have yet to settle.
    /// </summary>
    public event Action? TimelineRestored;

    /// <summary>
    /// Whether a preset, a style or a project's settings are being put in place (the command deferred): the Center controls
    /// and the Crop boxes then set every clip of the main video. Not an undo step, which defers the command as well.
    /// </summary>
    private bool PresetBeingApplied => _commandDeferrals > 0 && _restoring == 0;

    /// <summary>A document starts being put in place; disposed, it is done (the outermost then finishes, <see cref="EndRestore"/>).</summary>
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
    /// The outermost restore is over: the bindings are given until the dispatcher's next idle moment to settle on the new
    /// views, the selection follows its clip into its new view, the captions are projected once (through the timeline put
    /// back, from the anchors put back) and the subtitles' change is told once; then <see cref="TimelineRestored"/>. Nothing
    /// escapes it: a fault is written down.
    /// </summary>
    private void EndRestore()
    {
        // A change made right after this is a step of its own, never the end of a burst from before it.
        (_lastLookEdit, _lastFieldEdit) = (null, null);

        // First: what the window does on what follows (the captions told, its rows rebuilt) is not the user's change either. A
        // view model without a dispatcher (a background worker, a headless harness) has no bindings to settle.
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
    /// The selected clip (the Keyframes and Effects panes', <see cref="KeyLayer"/>) as the view its clip has now: a document
    /// put in place makes every view anew, and a clip keeps its id through undo and redo. Let go when its clip is gone.
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

    // The words a step's name may carry on past its first into a crash report's breadcrumb: none of them names anything of the user's.
    private static readonly HashSet<string> PlainWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "at", "in", "on", "into", "from", "out", "up", "back", "more", "again", "all", "every", "and", "s",
        "clip", "clips", "track", "tracks", "cut", "cuts", "speed", "volume", "easing", "keyframe", "keyframes", "silence", "folded",
        "stretch", "stretches", "dead", "air", "subtitles", "fade", "fades", "crossfade", "placement", "frame", "main", "video",
    };

    /// <summary>
    /// What an undo step or an edit is called, for a crash report's breadcrumbs: its first word (what was done) and the plain
    /// words and numbers after it, up to the first that may name a clip (a file's name, a title): no name of the user's.
    /// </summary>
    private static string NoteOf(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(1).Concat(words.Skip(1).TakeWhile(w =>
            PlainWords.Contains(w) || double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))));
    }

    /// <summary>
    /// A fault caught where it must not escape (an edit, an undo step): written to the log with its stack (<paramref name="what"/>
    /// may name the user's clips: the log stays on the machine), and a breadcrumb of what was being done without any name.
    /// </summary>
    private static void NoteFault(string category, string what, string note, Exception ex)
    {
        AppLog.Write($"{what} failed: {ex}");
        DevFlightRecorder.Record(category, $"{what} failed: {ex}");
        CrashReporter.Note(category, $"{note} failed: {ex.GetType().Name}");
    }
}
