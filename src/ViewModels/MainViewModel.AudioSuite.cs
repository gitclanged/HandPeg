using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegApp.ViewModels;

// ----- Studio Audio Suite: master fader, beat detection, Master Meter pane -----
public partial class MainViewModel
{
    // ----- Sequence master gain -----
    // The master fader (Audio tab and Master Meter pane) writes MasterGainDb via the SetMasterGain transaction step: one undo step per
    // discrete set (double-click to 0 dB) or per gesture (drag or wheel burst, BeginMasterGainChange..EndMasterGainChange), applied
    // live meanwhile. The audio program rebuilds on commit like any edit (OnDocumentCommitted resets live state and reloads the engine).

    private const double MasterGainLeast = -40, MasterGainMost = 12;

    // Last value pushed to the fader (NoteMasterGain) and gesture state: start snapshot, start gain, and undo history fingerprint (count
    // plus newest step bytes). If Undo/Redo/another edit changes history mid-gesture, no gesture step is committed (it would restore an
    // older timeline and clear Redo).
    private double _masterGainShown;
    private (TimelineSnapshot? Before, double From, int Steps, byte[]? Newest)? _masterGainGesture;

    /// <summary>
    /// Sequence master gain in dB (-40..+12), applied post-tracks and pre-Sequence-fades in playback and export. One undo step per
    /// discrete set, or per fader gesture.
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

        // History changed mid-gesture (e.g. Ctrl+Z between wheel notches): keep prior values and restart the gesture here so later
        // changes still form one undo step.
        if (_masterGainGesture is { } gesture && HistoryMovedSince(gesture.Steps, gesture.Newest))
        {
            _masterGainGesture = null;
            BeginMasterGainChange();
        }

        if (Edit("change the master gain", edit => edit.SetMasterGain(decibels), undoable: _masterGainGesture is null))
            StatusText = string.Create(CultureInfo.InvariantCulture, $"Master gain: {Document.MasterGainDb:+0.0;-0.0;0} dB (the whole mix, as the export makes it).");
        NoteMasterGain();
    }

    /// <summary>Starts a master fader gesture (drag or wheel burst); changes until End form one undo step.</summary>
    public void BeginMasterGainChange()
    {
        if (_masterGainGesture is not null || _isBackgroundWorker)
            return;

        FlushLooks();
        _masterGainGesture = (_restoring == 0 ? CaptureSnapshot() : null, Document.MasterGainDb, _undo.Count, _undo.Count > 0 ? _undo[^1].State : null);
    }

    /// <summary>
    /// Ends a fader gesture: commits one undo step if the gain changed and history is unchanged since Begin; otherwise discards it
    /// (Undo/Redo/another edit took ownership).
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

    // History changed check: count or newest step reference (each step's bytes are a fresh array).
    private bool HistoryMovedSince(int steps, byte[]? newest) =>
        _undo.Count != steps || !ReferenceEquals(_undo.Count > 0 ? _undo[^1].State : null, newest);

    /// <summary>Syncs the fader to the document gain (edit, undo, open); notifies only on change.</summary>
    private void NoteMasterGain()
    {
        if (Document.MasterGainDb.Equals(_masterGainShown))
            return;

        _masterGainShown = Document.MasterGainDb;
        OnPropertyChanged(nameof(MasterGainDb));
    }

    // ----- The master meter's pane -----

    /// <summary>Master Meter pane visibility (vertical meter + master fader). Default off; persisted.</summary>
    [ObservableProperty] private bool _showMasterMeter = AppSettings.Current.ShowMasterMeter;

    partial void OnShowMasterMeterChanged(bool value) => SaveView(s => s.ShowMasterMeter = value);

    // ----- Beats (Detect & Snap to Audio Beats) -----
    // Background onset detection (BeatDetector, spectral flux, file seconds) stores beats on the clip (TimelineClip.BeatTimes, one undo
    // step) so trims, moves, splits and speed keep them aligned. BeatsOnSequence maps them for edge snapping and the beat grid.

    /// <summary>Snap dragged clip edges to detected audio beats. Persisted.</summary>
    [ObservableProperty] private bool _snapToBeats = AppSettings.Current.SnapToBeats;

    partial void OnSnapToBeatsChanged(bool value) => SaveView(s => s.SnapToBeats = value);

    /// <summary>Draw beat ticks along clip blocks (beat grid). Persisted.</summary>
    [ObservableProperty] private bool _showBeatGrid = AppSettings.Current.ShowBeatGrid;

    partial void OnShowBeatGridChanged(bool value) => SaveView(s => s.ShowBeatGrid = value);

    // Clip ids with detection in flight; a repeat request awaits the running one.
    private readonly HashSet<Guid> _findingBeats = [];

    /// <summary>Whether beat detection applies (file clip with audio).</summary>
    public bool CanDetectBeats(Layer? clip) => ClipOf(clip) is { HasSound: true, SourcePath.Length: > 0 } && !_findingBeats.Contains(ClipOf(clip)!.Id);

    /// <summary>Whether a clip has detected beats.</summary>
    public bool HasBeats(Layer? clip) => ClipOf(clip) is { BeatTimes.Length: > 0 };

    /// <summary>
    /// Detects a clip's audio beats off the UI thread (status shown) and stores them in one undo step. False when nothing was stored
    /// (no audio, clip removed meanwhile, unreadable file; reason in status bar).
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
        // Mark a pending add (HasPendingAdds blocks tab switches until the beats land).
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

        // Project tab was backgrounded during detection: discard, never apply to the active tab (duplicates share clip ids).
        if (generation != _projectGeneration)
        {
            StatusText = $"The beats of {name} were not kept: its project tab went behind first. Detect them again there.";
            return false;
        }

        // The clip may have been removed or replaced meanwhile: resolve the current clip by id.
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

    /// <summary>Clear Audio Beats: removes a clip's beats in one undo step.</summary>
    public void ClearClipBeats(Layer clip)
    {
        if (ClipOf(clip) is not { BeatTimes: not null } model)
            return;

        if (Edit($"clear the beats of {clip.Name}", edit => edit.SetClipBeats(model, null)))
            StatusText = $"{clip.Name} has no beats now.";
    }

    /// <summary>
    /// Appends a clip's beats (<see cref="TimelineClip.BeatTimes"/>, file seconds) to <paramref name="into"/> in Sequence time:
    /// Start + slip + (m - MediaOffset) / Speed, with slip from unlinked audio, only where audio plays (J lead-in to L tail; unlinked clips
    /// keep J/L inaudible, so only Start + slip over their own length). Allocation-free.
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
