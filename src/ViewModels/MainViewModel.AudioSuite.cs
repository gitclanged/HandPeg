using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegApp.ViewModels;

// ----- The Studio Audio Suite (Pass 5.2B): the master fader, the beats, the meter's pane -----
public partial class MainViewModel
{
    // ----- The Sequence's master gain -----
    // The master fader (the meter's, in the Audio tab and its own pane) writes the document's MasterGainDb through the transaction's
    // SetMasterGain step: one step of Undo for a value set by itself (a double-click back to 0 dB), and one for a whole gesture (a drag, a
    // burst of wheel notches: BeginMasterGainChange .. EndMasterGainChange), which follows live meanwhile. The sound program is made again
    // as every edit of the document makes it (OnDocumentCommitted: the live state dropped, the engine given the Sequence anew).

    private const double MasterGainLeast = -40, MasterGainMost = 12;

    // The value the fader was last told (NoteMasterGain), and a gesture under way: the timeline as it was when it began, its gain then, and
    // the undo history then (how many steps, and the newest step's own bytes): Undo, Redo or another edit's step in the middle of a gesture
    // moves it, and the gesture's step is not taken then (it would bring back a timeline older than theirs, and clear Redo).
    private double _masterGainShown;
    private (TimelineSnapshot? Before, double From, int Steps, byte[]? Newest)? _masterGainGesture;

    /// <summary>
    /// The Sequence's master output fader, dB from -40 to +12 (5.2B): a gain on the whole mix after every track, before the Sequence's fades,
    /// in the player and the export alike. Set by itself it is one step of Undo; inside a gesture of the fader, the gesture is.
    /// </summary>
    public double MasterGainDb
    {
        get => Document.MasterGainDb;
        set => SetMasterGain(value);
    }

    private void SetMasterGain(double decibels)
    {
        decibels = double.IsFinite(decibels) ? Math.Round(Math.Clamp(decibels, MasterGainLeast, MasterGainMost), 1) : 0;
        if (Math.Abs(decibels - Document.MasterGainDb) < 0.05)
        {
            NoteMasterGain();
            return;
        }

        // The history moved in the middle of a gesture (Ctrl+Z between two wheel notches): what the gesture set before it is let be, and the
        // gesture goes on as a new one from here, so what it sets from now on is still one step of Undo.
        if (_masterGainGesture is { } gesture && HistoryMovedSince(gesture.Steps, gesture.Newest))
        {
            _masterGainGesture = null;
            BeginMasterGainChange();
        }

        if (Edit("change the master gain", edit => edit.SetMasterGain(decibels), undoable: _masterGainGesture is null))
            StatusText = string.Create(CultureInfo.InvariantCulture, $"Master gain: {Document.MasterGainDb:+0.0;-0.0;0} dB (the whole mix, as the export makes it).");
        NoteMasterGain();
    }

    /// <summary>A gesture of the master fader begins (a drag, a burst of wheel notches): what it sets until it ends is one step of Undo.</summary>
    public void BeginMasterGainChange()
    {
        if (_masterGainGesture is not null || _isBackgroundWorker)
            return;

        FlushLooks();
        _masterGainGesture = (_restoring == 0 ? CaptureSnapshot() : null, Document.MasterGainDb, _undo.Count, _undo.Count > 0 ? _undo[^1].State : null);
    }

    /// <summary>
    /// The gesture ended: the step it makes, taken when it changed the gain, and only while the undo history is as it was when the gesture
    /// began. Undo, Redo or another edit meanwhile own the history from there: the gesture is let go with no step.
    /// </summary>
    public void EndMasterGainChange()
    {
        if (_masterGainGesture is not { } gesture)
            return;

        _masterGainGesture = null;
        if (HistoryMovedSince(gesture.Steps, gesture.Newest))
        {
            AppLog.Write("The master gain's gesture took no undo step: the undo history moved while it was under way");
            return;
        }

        if (gesture.Before is { } before && Math.Abs(Document.MasterGainDb - gesture.From) >= 0.05)
            Checkpoint("change the master gain", before);
    }

    // Whether the undo history is other than it was (its count, or its newest step: a new step's bytes are always a new array).
    private bool HistoryMovedSince(int steps, byte[]? newest) =>
        _undo.Count != steps || !ReferenceEquals(_undo.Count > 0 ? _undo[^1].State : null, newest);

    /// <summary>The fader follows the document's gain (an edit, an undo, a project opened): told only when it is another value.</summary>
    private void NoteMasterGain()
    {
        if (Document.MasterGainDb.Equals(_masterGainShown))
            return;

        _masterGainShown = Document.MasterGainDb;
        OnPropertyChanged(nameof(MasterGainDb));
    }

    // ----- The master meter's pane -----

    /// <summary>The Master Meter pane (5.2B: the meter and the master fader upright, a pane of its own). Off to start with. Remembered.</summary>
    [ObservableProperty] private bool _showMasterMeter = AppSettings.Current.ShowMasterMeter;

    partial void OnShowMasterMeterChanged(bool value) => SaveView(s => s.ShowMasterMeter = value);

    // ----- Beats (Detect & Snap to Audio Beats) -----
    // A clip's sound is read in the background (BeatDetector: onsets found by spectral flux, in the file's own seconds) and the beats are kept
    // with the clip (TimelineClip.BeatTimes, one step of Undo), so trims, moves, splits and speed keep each on its moment of the sound. Where
    // they fall on the Sequence (BeatsOnSequence) is what the clips' edges snap to (Snap to Audio Beats) and what the beat grid draws.

    /// <summary>Whether a dragged clip's edges also catch on the beats found in clips' sound (5.2B). Remembered.</summary>
    [ObservableProperty] private bool _snapToBeats = AppSettings.Current.SnapToBeats;

    partial void OnSnapToBeatsChanged(bool value) => SaveView(s => s.SnapToBeats = value);

    /// <summary>Whether a clip with beats shows them as small ticks along its block (the beat grid, 5.2B). Remembered.</summary>
    [ObservableProperty] private bool _showBeatGrid = AppSettings.Current.ShowBeatGrid;

    partial void OnShowBeatGridChanged(bool value) => SaveView(s => s.ShowBeatGrid = value);

    // The clips whose beats are being found (by id): a second ask while one is under way waits for it.
    private readonly HashSet<Guid> _findingBeats = [];

    /// <summary>Whether a clip's sound can be searched for beats: a clip of a file with sound.</summary>
    public bool CanDetectBeats(Layer? clip) => ClipOf(clip) is { HasSound: true, SourcePath.Length: > 0 } && !_findingBeats.Contains(ClipOf(clip)!.Id);

    /// <summary>Whether a clip has beats found in its sound.</summary>
    public bool HasBeats(Layer? clip) => ClipOf(clip) is { BeatTimes.Length: > 0 };

    /// <summary>
    /// Finds the beats of a clip's sound (Detect &amp; Snap to Audio Beats): its file's sound read and searched off the interface's thread (the
    /// status bar says so meanwhile), then kept with the clip in one step of Undo. False when nothing was kept (no sound, the clip gone
    /// meanwhile, the file unreadable: the status bar says why).
    /// </summary>
    public async Task<bool> DetectClipBeatsAsync(Layer clip)
    {
        if (ClipOf(clip) is not { HasSound: true, SourcePath: { Length: > 0 } path } model)
        {
            StatusText = "Detect & Snap to Audio Beats needs a clip with sound.";
            return false;
        }

        if (!_findingBeats.Add(model.Id))
            return false;

        var (id, name, stream, generation) = (model.Id, clip.Name, model.AudioStreamIndex, _projectGeneration);
        StatusText = $"Finding the beats of {name}...";
        // An add to the clip under way meanwhile (HasPendingAdds: the tabs do not change until its beats are in).
        double[] beats;
        using var adding = BeginAdd();
        try
        {
            beats = await Task.Run(() => BeatDetector.DetectFileAsync(path, stream, _shutdown.Token), _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException
                                       or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write($"Beats of {path}", ex);
            StatusText = $"The beats of {name} could not be found: {ex.Message}";
            return false;
        }
        finally
        {
            _findingBeats.Remove(id);
        }

        // Its project tab gone behind while its sound was read (5.4D-TabPerf review, P1): the beats are kept on no clip, never on the
        // tab in front (a duplicate shares its clip ids).
        if (generation != _projectGeneration)
        {
            StatusText = $"The beats of {name} were not kept: its project tab went behind first. Detect them again there.";
            return false;
        }

        // The clip may have gone, or been made another, while its sound was read: the beats go on the clip that is there now, by its id.
        if (Document.FindClip(id) is not { } now)
            return false;
        if (beats.Length == 0)
        {
            StatusText = $"No beats were found in {name}'s sound.";
            return false;
        }

        if (!Edit($"detect the beats of {name}", edit => edit.SetClipBeats(now, beats)))
            return false;

        StatusText = $"{beats.Length} beats found in {name}'s sound: {(SnapToBeats ? "dragged clips snap to them" : "turn on Snap to Audio Beats for clips to snap to them")}. Ctrl+Z takes them away.";
        return true;
    }

    /// <summary>Takes a clip's beats away (Clear Audio Beats): one step of Undo.</summary>
    public void ClearClipBeats(Layer clip)
    {
        if (ClipOf(clip) is not { BeatTimes: not null } model)
            return;

        if (Edit($"clear the beats of {clip.Name}", edit => edit.SetClipBeats(model, null)))
            StatusText = $"{clip.Name} has no beats now.";
    }

    /// <summary>
    /// Where a clip's beats (its file's own seconds, <see cref="TimelineClip.BeatTimes"/>) fall on the Sequence, added to
    /// <paramref name="into"/> in order: a beat at file second m is at Start + slip + (m - MediaOffset) / Speed, the slip its unlinked sound's,
    /// and only while its sound plays there (from its J cut's lead-in before the clip's start to its L cut's tail-out after its end; an
    /// unlinked clip's J/L cut is kept but not heard, as the model has it, so its sound plays just its own length from Start + slip).
    /// Nothing for a clip with none. Allocates nothing.
    /// </summary>
    public static void BeatsOnSequence(TimelineClip clip, List<double> into)
    {
        if (clip.BeatTimes is not { Length: > 0 } beats || !(clip.Speed > 0))
            return;

        var slip = clip.AudioLinked ? 0 : clip.AudioSlipSeconds;
        var (lead, tail) = clip.AudioLinked ? (clip.AudioLeadInSeconds, clip.AudioTailOutSeconds) : (0.0, 0.0);
        var (from, to) = (clip.Start + slip - lead, clip.End + slip + tail);
        foreach (var beat in beats)
        {
            var at = clip.Start + slip + (beat - clip.MediaOffset) / clip.Speed;
            if (at >= from - 1e-9 && at <= to + 1e-9)
                into.Add(at);
        }
    }
}
