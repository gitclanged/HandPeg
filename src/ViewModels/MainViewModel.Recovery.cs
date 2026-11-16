using System.Text.Json;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Lagged recovery copy -----
// Recovery copies (autosave "Name.autosave.hproj" via AutosaveAsync, and the active tab's crash cache, MainWindow.TabCache.cs) would
// otherwise capture the very edit that went wrong (bad Ripple Delete, misplaced Clip). With >= 2 steps since the last save/open, they
// are written at N-2 with the last two actions as the first Redo steps (Ctrl+Y yields N-1, then N). No extra state is kept: N-2 and
// N-1 come from the two newest immutable Undo steps, N is the live project. All other writes (Save, Save As, portable save, bundle,
// open-time conversion, backgrounded or persisted tabs, duplicates, background-tab crash writer) use the current state.
public partial class MainViewModel
{
    /// <summary>Recovery lag in actions: writes the state before the last two, with them as Redo.</summary>
    internal const int RecoveryLagSteps = 2;

    /// <summary>
    /// Immutable inputs for the lagged copy, captured on the UI thread (<see cref="TakeRecoveryLag"/>) and assembled off it
    /// (<see cref="Lagged"/>): steps to N-2 and N-1 (each action and its prior state), current project N with frame, current frame, and
    /// the remaining history; all packed or copied so later edits cannot reach them.
    /// </summary>
    internal sealed record RecoveryLag(string WhatBack, byte[] Back, string WhatMiddle, byte[] Middle, byte[] Now, ProjectSettingsFrame Frame,
        List<UndoStepState>? Undo, List<UndoStepState>? Redo);

    /// <summary>Steps since last save/open (decreases when undoing back toward that point).</summary>
    internal int FreshSteps => _undo.Count - _savedDepth;

    /// <summary>
    /// Undo history fingerprint for the active tab's crash cache: newest step reference (each step is a fresh array) plus undo/redo
    /// counts. Changes on every do, undo or redo.
    /// </summary>
    internal (object? Newest, int Undo, int Redo) HistoryPosition => (_undo.Count > 0 ? _undo[^1].State : null, _undo.Count, _redo.Count);

    /// <summary>
    /// Whether to write the lagged copy: at least <see cref="RecoveryLagSteps"/> steps since save/open (never older than the saved state)
    /// and no change in flight (open or committing edit, undo/redo restore, master fader gesture). Otherwise writes the current state.
    /// </summary>
    internal bool CanLagRecovery =>
        !_isBackgroundWorker && _undo.Count >= RecoveryLagSteps && _undo.Count - _savedDepth >= RecoveryLagSteps
        && _restoring == 0 && _openEdit is null && _committing == 0 && Document.OpenTransactions.Count == 0 && _masterGainGesture is null;

    /// <summary>
    /// Captures lagged-copy inputs on the UI thread; null when not lagging (<see cref="CanLagRecovery"/>). Packs the current project with
    /// its frame (like a Project Settings step), costing about one undo step.
    /// </summary>
    internal RecoveryLag? TakeRecoveryLag()
    {
        if (!CanLagRecovery)
            return null;

        var frame = CaptureProjectFrame();
        var (back, middle) = (_undo[^2], _undo[^1]);
        var now = Pack(CaptureSnapshot() with { Frame = frame });
        return new RecoveryLag(back.What, back.State, middle.What, middle.State, now, frame,
            HistoryOf(_undo.GetRange(0, _undo.Count - RecoveryLagSteps), null), HistoryOf(_redo, null));
    }

    /// <summary>
    /// Builds the N-2 project (pure, any thread, fresh buffers): a copy of <paramref name="now"/> with timeline, bin, subtitles, caption
    /// box, sequence look, blur fill, output track actions/titles and frame from N-2; Undo = earlier steps; Redo = existing Redo plus
    /// N-1, N. Null if either step is unreadable (damaged or legacy format), falling back to current state. Non-undoable data (export
    /// settings, hand-typed command, thumbnail) stays current.
    /// </summary>
    internal static ProjectState? Lagged(ProjectState now, RecoveryLag lag)
    {
        TimelineSnapshot back, middle;
        try
        {
            if (UndoSteps.Unpack(lag.Back) is not { } read || !IsWhole(read) || ProjectStore.HoldsOldKinds(read.Document, read.Bin)
                || UndoSteps.Unpack(lag.Middle) is not { } next || !IsWhole(next) || ProjectStore.HoldsOldKinds(next.Document, next.Bin))
            {
                AppLog.Write("The recovery copy was written as the project is: one of the last two undo steps holds no timeline that can be put back");
                return null;
            }

            (back, middle) = (read, next);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("The recovery copy was written as the project is: one of the last two undo steps could not be read", ex);
            return null;
        }

        // The first Redo step carries the N-1 frame (like a Project Settings step) so Ctrl+Y restores it: the step's own if action N was a
        // Project Settings change, else the current frame (N did not change it).
        var redoMiddle = middle.Frame is null ? UndoSteps.Pack(middle with { Frame = lag.Frame }) : lag.Middle;

        // N-2 frame: from a Project Settings step among the two actions, else the current frame.
        var frame = back.Frame ?? middle.Frame ?? lag.Frame;

        // All fallible reads succeeded; build the copy. Settings are cloned (as a project file writes them) so the input is never mutated.
        var settings = JsonSerializer.Deserialize<EncodingPreset>(JsonSerializer.Serialize(now.Settings))!;
        settings.BackgroundHidden = back.BackgroundHidden;
        if (back.CaptionLayer is { } caption)
            settings.CaptionLayer = caption;
        if (back.CaptionStyle is { } style)
            settings.CaptionStyle = style;
        if (back.Look is { } look)
        {
            (settings.ColorContrast, settings.ColorBrightness, settings.ColorSaturation, settings.ColorGamma) = (look.Contrast, look.Brightness, look.Saturation, look.Gamma);
            (settings.ColorHue, settings.ColorRed, settings.ColorGreen, settings.ColorBlue) = (look.Hue, look.Red, look.Green, look.Blue);
            (settings.SharpenStrength, settings.BlurRadius, settings.BlurPasses, settings.BackgroundDim, settings.BackgroundZoom) =
                (look.Sharpen, look.BlurRadius, look.BlurPasses, look.BackgroundDim, look.BackgroundZoom);
            (settings.BackgroundEffects, settings.BackgroundEffectsBypassed) = (look.BackgroundEffects, look.BackgroundEffectsBypassed);
            // Legacy LUT is part of the look step (Convert moves it to Sequence Output cards); older steps lack it.
            if (look.LutPath is { } lutPath)
                settings.LutPath = lutPath;
        }

        (settings.UseVerticalResolution, settings.OutputWidth, settings.OutputHeight) = (frame.Vertical, frame.OutputWidth ?? "", frame.OutputHeight ?? "");
        (settings.SequenceEffects, settings.SequenceEffectsBypassed) = ([.. back.Document.SequenceEffects], back.Document.SequenceEffectsBypassed);
        settings.AudioTracks = WithStepAudio(settings.AudioTracks, back.Audio);

        var state = now.ShallowCopy();
        state.Timeline = back.Document;
        state.Deleted = back.Bin ?? [];
        state.Subtitles = back.Subtitles;
        state.Settings = settings;
        state.AudioTracks = WithStepAudio(now.AudioTracks, back.Audio);
        state.UndoHistory = lag.Undo;
        state.RedoHistory = [.. lag.Redo ?? [], new UndoStepState(lag.WhatMiddle, lag.Now), new UndoStepState(lag.WhatBack, redoMiddle)];
        return state;
    }

    /// <summary>Copy of the output tracks with a step's actions/titles applied where present (as <see cref="RestoreTimeline"/> does).</summary>
    private static List<AudioTrackState> WithStepAudio(List<AudioTrackState> tracks, List<AudioOutputEdit> step) =>
        tracks.ConvertAll(track => step.Find(s => s.Index == track.Index) is not { } edit
            ? track
            : track with
            {
                Action = edit.Action is { } action && AudioTrack.AllActions.Contains(action) ? action : track.Action,
                Title = edit.Title is { Length: > 0 } title ? title : track.Title,
            });

    /// <summary>
    /// Synchronous recovery state on the UI thread (golden entry point): <see cref="CaptureStateForSave"/> at N-2 when
    /// <see cref="CanLagRecovery"/>, else current.
    /// </summary>
    internal ProjectState CaptureStateForRecovery(string name = "")
    {
        var state = CaptureStateForSave(name);
        return TakeRecoveryLag() is { } lag && Lagged(state, lag) is { } lagged ? lagged : state;
    }
}
