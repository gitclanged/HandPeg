using System.Text.Json;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- The lagged recovery copy (Pass 5.5A) -----
// A recovery copy is written while the user works: the autosave ("Name.autosave.hproj", AutosaveAsync) and the tab in front's crash-recovery
// cache file (MainWindow.TabCache.cs). Written as the project is this moment, it would keep the very edit that went wrong (a bad Ripple
// Delete, a Clip dropped on the wrong Track) as the state it opens at. So once two steps or more have been taken since the project was last
// saved or opened, the copy is written two steps back: the state before the last two actions (N-2) is the one it opens at, and those two
// actions are the first two steps of its Redo history (Ctrl+Y gives N-1, then N, exactly as Undo had left them). Nothing new is kept for
// it: N-2 and N-1 are the newest two steps Undo already holds (packed, never changed), and N is the live project. Every other file (Save,
// Save As, a portable save, a bundle, a project converted as it opened, a tab put behind or kept for the next start, a duplicate, the crash
// writer of the tabs behind) is written exactly as the project is.
public partial class MainViewModel
{
    /// <summary>How many actions back the recovery copy is written (Pass 5.5A): the state before the last two, with them on its Redo.</summary>
    internal const int RecoveryLagSteps = 2;

    /// <summary>
    /// What the lagged recovery copy is made from (Pass 5.5A), taken on the window's thread (<see cref="TakeRecoveryLag"/>) and turned into the
    /// copy away from it (<see cref="Lagged"/>): the step back to N-2 (the action N-1 and the state before it), the step back to N-1 (the
    /// action N and the state before it), the project as it is now (N, with its frame), the frame now, and the rest of the history, all
    /// packed or copied already: nothing here is reached by later edits.
    /// </summary>
    internal sealed record RecoveryLag(string WhatBack, byte[] Back, string WhatMiddle, byte[] Middle, byte[] Now, ProjectSettingsFrame Frame,
        List<UndoStepState>? Undo, List<UndoStepState>? Redo);

    /// <summary>How many steps have been taken since the project was last saved or opened (Pass 5.5A: fewer after undoing past that point).</summary>
    internal int FreshSteps => _undo.Count - _savedDepth;

    /// <summary>
    /// Where Undo's history stands (Pass 5.5A, for the tab in front's crash-recovery file): its newest step (a new step's bytes are always a
    /// new array) and the counts of both histories. Moves with every step taken, undone or redone.
    /// </summary>
    internal (object? Newest, int Undo, int Redo) HistoryPosition => (_undo.Count > 0 ? _undo[^1].State : null, _undo.Count, _redo.Count);

    /// <summary>
    /// Whether the recovery copy is written two steps back now (Pass 5.5A): at least <see cref="RecoveryLagSteps"/> steps taken since the last
    /// save or open (so it never lands before what was saved or opened), and no change under way (an edit open or committing, a step being
    /// put back, a gesture of the master fader). Otherwise it is written as the project is.
    /// </summary>
    internal bool CanLagRecovery =>
        !_isBackgroundWorker && _undo.Count >= RecoveryLagSteps && _undo.Count - _savedDepth >= RecoveryLagSteps
        && _restoring == 0 && _openEdit is null && _committing == 0 && Document.OpenTransactions.Count == 0 && _masterGainGesture is null;

    /// <summary>
    /// What the lagged recovery copy is made from, on the window's thread (Pass 5.5A); null when it is written as the project is
    /// (<see cref="CanLagRecovery"/>). The project as it is now is packed here with its frame (as a Project Settings step keeps it), which
    /// costs what one step of Undo costs.
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
    /// The project two actions back (Pass 5.5A, pure: any thread, with fresh buffers only): <paramref name="now"/> (a copy of the caller's
    /// own, left as it is) with the timeline, bin, subtitles, caption box, sequence look, blur fill, output tracks' actions and titles and frame
    /// of the state before the last two actions, its Undo history the steps before those, and its Redo history the two actions (N-1, then N)
    /// after whatever Redo held. Null when either step cannot be read (damaged, or of the shape before 5.2): the copy is then written as
    /// the project is. What no step of Undo holds (the export settings, the command typed by hand, the thumbnail) stays as it is now.
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
            AppLog.Write($"The recovery copy was written as the project is: one of the last two undo steps could not be read ({ex.GetType().Name}: {ex.Message})");
            return null;
        }

        // The step Redo gives first carries its frame (as a Project Settings step does), so Ctrl+Y puts the frame of N-1 back with it: its
        // own when the action N was a Project Settings step (taken then), else the frame now (the action N did not change it).
        var redoMiddle = middle.Frame is null ? UndoSteps.Pack(middle with { Frame = lag.Frame }) : lag.Middle;

        // The frame at N-2: the Project Settings step's own where one of the two actions was one, else the frame now.
        var frame = back.Frame ?? middle.Frame ?? lag.Frame;

        // Everything that can fail has: the copy is made now. The settings are a copy of their own (as a project file writes them), so
        // the state given is never changed.
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

    /// <summary>The output tracks with the actions and titles a step put back, each only where it gives one (as <see cref="RestoreTimeline"/> puts them back).</summary>
    private static List<AudioTrackState> WithStepAudio(List<AudioTrackState> tracks, List<AudioOutputEdit> step) =>
        tracks.ConvertAll(track => step.Find(s => s.Index == track.Index) is not { } edit
            ? track
            : track with
            {
                Action = edit.Action is { } action && AudioTrack.AllActions.Contains(action) ? action : track.Action,
                Title = edit.Title is { Length: > 0 } title ? title : track.Title,
            });

    /// <summary>
    /// The project as its recovery copy is written (Pass 5.5A; the window's thread, at once: the goldens' own way in): what a save writes
    /// (<see cref="CaptureStateForSave"/>), two actions back when <see cref="CanLagRecovery"/>, else as it is.
    /// </summary>
    internal ProjectState CaptureStateForRecovery(string name = "")
    {
        var state = CaptureStateForSave(name);
        return TakeRecoveryLag() is { } lag && Lagged(state, lag) is { } lagged ? lagged : state;
    }
}
