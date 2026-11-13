using System.IO;
using System.Text.Json;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Project tabs (Pass 5.4A, MainWindow.ProjectTabs.cs) -----
// The title bar's tabs keep one view model and one Sequence: only the tab in front lives here. A tab put behind is written to the
// cache as a project file of its own (ProjectTabs, %Cache%\project-tabs\<session>\<id>.hptab) with its undo and redo history, and brought
// back the way a project is opened (RestoreStateAsync), so every per-project field is reset as an open resets it. The clipboard
// (_copied) is the view model's, not the project's: what was copied in one tab pastes in the next.
// Pass 5.4D-TabPerf (the user's U1): a tab brought to the front this session stays in memory as it goes behind (DetachForTab: its live
// Sequence, its undo and redo history by reference, its saved state's fingerprints as they are, what was opened of its file), and comes
// back by a swap of references (AttachTab), with no file read, no probe, no open and no fingerprint: the same steps an open takes to put
// a project's settings and timeline in place (ApplyProjectSettings, InstallProjectTimeline), on its own Sequence. Work left running in
// the background for the tab put behind (its I-frames, waveforms, hover sheet, its streams unpacked) is cancelled and never lands in
// the next tab (_projectGeneration); what it left unfinished starts again as the tab comes back (all of it cached on the disk).
public partial class MainViewModel
{
    /// <summary>
    /// Which project the view model holds (Pass 5.4D-TabPerf): moved on as a tab goes behind, so a continuation of work started for it
    /// (UnpackProjectStreamsAsync) that comes back later sees it is another tab's and does nothing.
    /// </summary>
    private int _projectGeneration;

    /// <summary>The project generation (<see cref="_projectGeneration"/>), for the window's adds that span awaits of their own (a drop of several files).</summary>
    internal int ProjectGeneration => _projectGeneration;

    // How many unpackings of a project's streams are under way for the project held now (UnpackProjectStreamsAsync).
    private int _unpacking;

    // ----- Adds under way (Pass 5.4D-TabPerf review, P1) -----
    // A clip added from a file is placed once its file has been looked at (AddAudioLayerAsync) or filled in then (AddVideoClipAsync):
    // an await the user can switch tabs during. The window refuses a switch while any is under way (MainWindow.MayChangeTabs reads
    // HasPendingAdds), and each add takes the project generation before its await and places or edits nothing once it has moved on.

    // How many adds from a file are under way (a drop's files, a voiceover being placed): the view model's, whichever tab is in front.
    private int _pendingAdds;

    /// <summary>Whether a clip is being added from a file (its probe still running, a drop's files still to come, a voiceover being placed): the tabs may not change meanwhile.</summary>
    public bool HasPendingAdds => _pendingAdds > 0;

    /// <summary>How many adds from a file are under way (<see cref="HasPendingAdds"/>).</summary>
    internal int PendingAdds => _pendingAdds;

    /// <summary>An add from a file under way until the result is disposed (the window's thread): counted in <see cref="HasPendingAdds"/>.</summary>
    internal IDisposable BeginAdd()
    {
        _pendingAdds++;
        return new AddUnderWay(this);
    }

    private sealed class AddUnderWay(MainViewModel owner) : IDisposable
    {
        private MainViewModel? _owner = owner;

        public void Dispose()
        {
            if (_owner is { } vm)
                vm._pendingAdds = Math.Max(vm._pendingAdds - 1, 0);
            _owner = null;
        }
    }

    /// <summary>
    /// How a video clip just dropped is finished once its file has been looked at (Pass 5.4D-TabPerf review, P2): played once or to the
    /// end, fitted inside (or filling) a box of the frame, cut to a sub-clip. Kept with its tab when the tab goes behind before then, so the
    /// add is finished the same way as the tab comes back.
    /// </summary>
    internal readonly record struct LoadingAdd(bool PlayOnce, (int X, int Y, int Width, int Height)? FitInto, bool Fill, (double In, double Out)? SourceRange);

    // The video clips of the project held now still being filled in after their add, by clip id (FinishVideoAddAsync).
    private readonly Dictionary<Guid, LoadingAdd> _loadingAdds = [];

    // The adds a tab coming back had still loading as it went behind, while its views are made (AttachTab -> ViewFor); null otherwise.
    private Dictionary<Guid, LoadingAdd>? _addsToResume;

    /// <summary>
    /// The project as a tab put behind keeps it: <see cref="CaptureState"/> with the undo and redo history (packed steps, shared:
    /// a packed step is never changed) and the last thumbnail taken. Never waits for a fresh thumbnail (<see cref="ThumbnailForSave"/>
    /// can hold this thread for a moment): a switch is never held up by the engine. The state is a copy of its own, so it may be
    /// written away from this thread.
    /// </summary>
    public ProjectState CaptureStateForTab(string name)
    {
        var state = CaptureState(name);
        (state.UndoHistory, state.RedoHistory) = (HistoryOf(_undo, null), HistoryOf(_redo, null));
        state.ThumbnailJpeg = Volatile.Read(ref _thumbnail);
        return state;
    }

    /// <summary>What the project looked like when it was last saved or opened (what tells whether it has changed since): kept with a tab put behind that has unsaved changes, so it comes back with them.</summary>
    public string SavedFingerprintForTab => _savedFingerprint;

    /// <summary>
    /// Whether the project may have unsaved changes, cheaply: something was opened, touched since the last save or open, and has a
    /// saved state to differ from. False means <see cref="HasUnsavedChanges"/> is false without making the fingerprint; true asks it.
    /// </summary>
    public bool MayHaveUnsavedChanges => HasSource && _touchedSinceSaved && _savedFingerprint.Length > 0;

    /// <summary>
    /// Whether the project holds nothing worth keeping as a tab goes behind (5.4A review): no file or clip, no undo or redo history (a
    /// project whose clips were all deleted can still have them back), no frame or rate chosen on purpose (the New Project studio,
    /// Project Settings), no name from the studio, no Game Preset waiting for a first file and nothing in Project Media (5.4C: the New
    /// Project studio's picks go there with no step of Undo). Only such a tab is let go as empty; every other one is written to the cache.
    /// </summary>
    public bool HoldsNothingForTab => !HasSource && _undo.Count == 0 && _redo.Count == 0 && !Document.ExplicitFrame && !Document.ExplicitFrameRate
        && NewProjectName.Length == 0 && _templateToCome is null && Document.MediaPool.Count == 0;

    /// <summary>The Game Preset waiting for the first file (null for none), opaque: a tab put behind keeps it with its header, in memory only.</summary>
    public object? TemplateWaitingForTab => _templateToCome;

    /// <summary>
    /// A tab brought back to the front: its state put in place as a project is opened (<see cref="OpenShowingCaptionsAsync"/>, never
    /// written back over a file: the state is the cache's copy), the file it was opened from or saved to as the file Save writes, and
    /// its unsaved changes still unsaved (<see cref="RestoreUnsavedState"/>). Its videos' streams are shown in the background, and its
    /// files kept out of crash reports, as an open does.
    /// </summary>
    /// <param name="projectPath">The tab's project file; null or empty for work never saved.</param>
    /// <param name="dirty">Whether the tab had unsaved changes as it was put behind.</param>
    /// <param name="savedFingerprint">The fingerprint of its saved state (<see cref="SavedFingerprintForTab"/>), for a dirty tab; null when unknown.</param>
    /// <param name="newProjectName">The name the New Project studio gave the tab ("" for none): the last tab's never stays.</param>
    /// <param name="newProjectFolder">The folder the studio chose for the tab's first save (null: the Projects folder).</param>
    /// <param name="waitingTemplate">The Game Preset the tab had waiting for its first file (<see cref="TemplateWaitingForTab"/>); null for none.</param>
    public async Task ResumeTabAsync(ProjectState state, string? projectPath, bool dirty, string? savedFingerprint,
        string newProjectName = "", string? newProjectFolder = null, object? waitingTemplate = null)
    {
        EditingJob = null;
        CancelProjectBackgroundWork();
        CrashReporter.KeepOut(CrashSummaries.FilesIn(state));
        var name = projectPath is { Length: > 0 } ? Path.GetFileNameWithoutExtension(projectPath) : state.Name;
        var before = Document;
        await OpenShowingCaptionsAsync(state, name.Length > 0 ? $"Switched to {name}" : "Switched project tabs");

        // Not put in place (its media not found, another operation running): nothing of the tab is taken on, so the Sequence still
        // here is never saved as the tab's (the window empties it, or goes back to the tab it came from).
        if (ReferenceEquals(before, Document))
            return;

        // The open keeps the last tab's studio name, folder and waiting Game Preset: always this tab's own instead.
        KeepNewProjectName(newProjectName, newProjectFolder);
        _templateToCome = waitingTemplate as ClipTemplate;
        CurrentProjectPath = projectPath ?? "";
        if (CurrentProjectPath.Length > 0)
            DevFlightRecorder.Record("project", "path " + CurrentProjectPath);
        RestoreUnsavedState(savedFingerprint, dirty);
        _ = UnpackProjectStreamsAsync();
    }

    /// <summary>
    /// The project's file renamed on the disk (Pass 5.4B, the tab's Rename Project...: MainWindow moved the file): Save writes to
    /// <paramref name="newPath"/> from now on. Nothing is saved and the unsaved changes stay as they were. A line for the status bar.
    /// </summary>
    public string RenameProjectFile(string newPath)
    {
        CurrentProjectPath = newPath;
        DevFlightRecorder.Record("project", "path " + newPath);
        var name = Path.GetFileNameWithoutExtension(newPath);
        RefreshRecentProjects(name);
        return StatusText = $"Project renamed: {name}";
    }

    /// <summary>
    /// After a tab's state was put in place (which takes it as the saved one: <see cref="MarkSaved"/>), the changes it had that were
    /// not saved are unsaved again: the saved state is the one it was compared with before it went behind, or (unknown) one nothing
    /// matches. A tab that had none stays as the open left it. A false "unsaved" only asks once too often; a false "saved" would lose work.
    /// </summary>
    internal void RestoreUnsavedState(string? savedFingerprint, bool dirty)
    {
        if (!dirty)
            return;

        _savedFingerprint = string.IsNullOrEmpty(savedFingerprint) ? "\0unsaved" : savedFingerprint;
        _touchedSinceSaved = true;
    }

    // ----- A tab kept in memory (Pass 5.4D-TabPerf) -----

    /// <summary>
    /// Everything of a project tab put behind that lives in the view model (Pass 5.4D-TabPerf), held in memory until it comes back to the
    /// front (<see cref="AttachTab"/>) or is let go to its cache file. <see cref="State"/> is <see cref="CaptureState(string)"/>'s notion of
    /// the project with its Timeline the live Sequence itself, detached (no Committed handler: <see cref="Document"/> is the only timeline);
    /// <see cref="Frozen"/> is a copy of its own (<see cref="CaptureStateForTab"/>: the history and thumbnail with it) for the cache file
    /// written on the thread pool, a save or a duplicate. Nothing of the project changes while the tab is behind.
    /// </summary>
    public sealed class TabProjectContext
    {
        internal TabProjectContext(ProjectState state, ProjectState frozen)
        {
            State = state;
            Frozen = frozen;
        }

        /// <summary>The project as the tab left it; its Timeline is the tab's own live Sequence.</summary>
        public ProjectState State { get; }

        /// <summary>A copy of the project with its undo history and thumbnail, never reached by the live Sequence: what the cache file is written from.</summary>
        public ProjectState Frozen { get; }

        /// <summary>The tab's own Sequence (the one <see cref="MainViewModel.Document"/> was, and is again as it comes back).</summary>
        public TimelineDocument Document => State.Timeline;

        /// <summary>The project's file ("" for work never saved): set by a save of the tab while it is behind (MainWindow.SaveTabInPlace).</summary>
        public string ProjectPath { get; set; } = "";

        /// <summary>The New Project studio's name and folder for its first save (cleared by a save while it is behind).</summary>
        public string NewProjectName { get; set; } = "";

        public string? NewProjectFolder { get; set; }

        /// <summary>Saved while it was behind (MainWindow.SaveTabInPlace): taken as saved as it comes back (one fingerprint then).</summary>
        public bool MarkSavedOnAttach { get; set; }

        /// <summary>Whether it may have unsaved changes (cheaply: <see cref="MayHaveUnsavedChanges"/> as it went behind).</summary>
        public bool MayBeDirty { get; init; }

        /// <summary>Its saved state's fingerprint (<see cref="SavedFingerprintForTab"/>) as it went behind; "" for none.</summary>
        public string SavedFingerprint { get; init; } = "";

        /// <summary>How many steps it has to undo and to redo.</summary>
        public int UndoDepthNow => Undo.Count;

        public int RedoDepthNow => Redo.Count;

        internal List<(string What, byte[] State)> Undo { get; init; } = [];
        internal List<(string What, byte[] State)> Redo { get; init; } = [];
        internal bool TouchedSinceSaved { get; init; }

        /// <summary>How many of its undo steps predate its last save or open (Pass 5.5A: what its recovery copy may lag through).</summary>
        internal int SavedDepth { get; init; }

        internal string AutosavedFingerprint { get; init; } = "";
        internal object? TemplateToCome { get; init; }
        internal byte[]? Thumbnail { get; set; }
        internal string? ConvertedFrom { get; init; }
        internal string LocalMediaPath { get; init; } = "";
        internal MediaInfo? MediaInfo { get; init; }
        internal List<double> IFrames { get; init; } = [];
        internal string IFrameStatus { get; init; } = "";
        internal bool IFramesPending { get; init; }
        internal string? SpriteSheetPath { get; init; }
        internal string? SpriteSource { get; init; }
        internal double SpriteIntervalSeconds { get; init; }
        internal bool SpritePending { get; init; }
        internal Dictionary<int, ImageSource> TrackWaveforms { get; init; } = [];
        internal ImageSource? TimelineWaveform { get; init; }
        internal bool WaveformsPending { get; init; }
        internal bool UnpackPending { get; init; }
        internal double PositionMs { get; init; }
        internal Layer? KeyLayer { get; init; }
        internal List<Layer> SelectedClips { get; init; } = [];

        /// <summary>Its video clips still being filled in after their add as it went behind, by clip id: finished as their add would have finished them as it comes back (P2).</summary>
        internal Dictionary<Guid, LoadingAdd> LoadingAdds { get; init; } = [];

        /// <summary>Its clips' views, taken up again as it comes back: none made anew for its unchanged Sequence (MainViewModel.KeptViews).</summary>
        internal KeptViews? Views { get; init; }
    }

    /// <summary>
    /// The tab in front goes behind and stays in memory (Pass 5.4D-TabPerf; the window's thread, at once: the window has stopped playback
    /// and holds no operation or recording, MainWindow.MayChangeTabs): everything of its project the view model holds is taken into a
    /// context (its Sequence by reference, detached; its history moved; its fingerprints as they are; its clips' views, let go of by the
    /// view model: <see cref="LetGoOfViews"/>), the background work started for it cancelled, and the view model left holding no project's
    /// Sequence: an empty one stands in (with no views) until the next tab is put in place, so nothing done meanwhile ever reaches the tab
    /// put behind.
    /// </summary>
    public TabProjectContext DetachForTab(string title)
    {
        var frozen = CaptureStateForTab(title);
        var state = CaptureState(title, liveTimeline: true);

        // Its views go with it (the looks are in its clips now: CaptureState), out of the view model's maps before anything else runs.
        var views = LetGoOfViews();
        var tab = new TabProjectContext(state, frozen)
        {
            ProjectPath = CurrentProjectPath,
            NewProjectName = NewProjectName,
            NewProjectFolder = NewProjectFolder,
            MayBeDirty = MayHaveUnsavedChanges,
            SavedFingerprint = _savedFingerprint,
            Undo = [.. _undo],
            Redo = [.. _redo],
            TouchedSinceSaved = _touchedSinceSaved,
            SavedDepth = _savedDepth,
            AutosavedFingerprint = _autosavedFingerprint,
            TemplateToCome = _templateToCome,
            Thumbnail = Volatile.Read(ref _thumbnail),
            ConvertedFrom = ConvertedFrom,
            LocalMediaPath = LocalMediaPath,
            MediaInfo = _mediaInfo,
            IFrames = _sourceIFrames,
            IFrameStatus = IFrameStatusText,
            IFramesPending = _iFrameScanCancellation is not null,
            SpriteSheetPath = SpriteSheetPath,
            SpriteSource = _spriteSource,
            SpriteIntervalSeconds = SpriteIntervalSeconds,
            SpritePending = _spriteCancellation is not null,
            TrackWaveforms = AudioTracks.Where(t => t.Waveform is not null).GroupBy(t => t.Index).ToDictionary(g => g.Key, g => g.First().Waveform!),
            TimelineWaveform = TimelineWaveform,
            WaveformsPending = _waveformCancellation is not null || _timelineWaveformCancellation is not null,
            UnpackPending = _unpacking > 0,
            PositionMs = PositionMs,
            KeyLayer = KeyLayer,
            SelectedClips = [.. _selectedClips],
            LoadingAdds = new(_loadingAdds),
            Views = views,
        };

        CancelProjectBackgroundWork();
        ParkDocument();

        // The views list as the stand-in Sequence has it (none), and the selection of clips (the context keeps it): the tab's views are
        // in no list of the view model while it is behind. The Inspector and Keyframes pane let go of its selected view too (P3): one
        // nudged while the next tab is read would change a view listened to by nothing, which its clip would take later.
        Layers.Clear();
        KeyLayer = null;
        _selectedClips.Clear();
        _selectedClipSet.Clear();
        ForgetHistory();
        (_savedFingerprint, _touchedSinceSaved, _autosavedFingerprint) = ("", false, "");
        return tab;
    }

    /// <summary>
    /// The work started in the background for the project held now, cancelled as its tab goes behind (Pass 5.4D-TabPerf): the I-frame scan,
    /// the waveforms, the hover sheet (each checks its own cancellation after its wait), and the generation moved on for what has none (the
    /// streams' unpacking, the adds from a file). What was cut short starts again as the tab comes back (<see cref="AttachTab"/>; a tab put
    /// behind has taken its adds still loading first).
    /// </summary>
    private void CancelProjectBackgroundWork()
    {
        _projectGeneration++;
        _unpacking = 0;
        _loadingAdds.Clear();
        _iFrameScanCancellation?.Cancel();
        _waveformCancellation?.Cancel();
        _timelineWaveformCancellation?.Cancel();
        _spriteCancellation?.Cancel();
    }

    /// <summary>
    /// An empty Sequence stands in for the one a tab put behind took with it (Pass 5.4D-TabPerf): put in place without its views made
    /// again (the next project's are, at once, as it is installed), so what the next project's settings change as they are put in place,
    /// and anything done while the next tab is read from its file, reaches this one and never the tab put behind.
    /// </summary>
    internal void ParkDocument()
    {
        Document.Committed -= OnDocumentCommitted;
        Document = NewDocument();
        Document.Committed += OnDocumentCommitted;
        _previewState = null;
    }

    /// <summary>
    /// A tab kept in memory brought back to the front (Pass 5.4D-TabPerf; the window's thread, at once): its project put in place as an
    /// open puts one (<see cref="ApplyProjectSettings"/>, <see cref="InstallProjectTimeline"/>) but on its own live Sequence, with the file it
    /// had open as it was probed, its history, fingerprints, file, New Project name and folder and waiting Game Preset as they were (no
    /// fingerprint is made: its unsaved changes are as unsaved as they were), its I-frames, waveforms and hover sheet as it left them, its
    /// own views taken up again (none made anew: <see cref="TakeUpViews"/>), and its selection with them. False when an operation or a
    /// recording holds the view model (nothing changed).
    /// </summary>
    public bool AttachTab(TabProjectContext tab)
    {
        if (IsBusy || IsRecording)
            return false;

        // Whatever was still under way for the project held now (one closed in front: AttachTab follows it) never lands in this one.
        CancelProjectBackgroundWork();
        var state = tab.State;
        EditingJob = null;
        CrashReporter.KeepOut(CrashSummaries.FilesIn(state));
        ConvertedFrom = tab.ConvertedFrom;
        TakeUpThumbnail(tab.Thumbnail);
        SourcePath = state.SourcePath;
        DownloadResolution = Pick(DownloadResolutions, state.DownloadResolution, "Best");
        DownloadSubtitles = state.DownloadSubtitles;

        // HasSource is told once, as the tab's own Sequence is in place (P12): until then it reads the stand-in Sequence, and a false
        // told on the way (its file set before its Sequence, or its settings' shape note) would have the window let go of the engine.
        _holdingSource = true;
        try
        {
            // The file it had open, as it was probed then (no probe, no write test of its folder: LoadMediaAsync's part of an open).
            ResetForNewSource();
            LocalMediaPath = tab.LocalMediaPath;
            SetMediaInfo(tab.MediaInfo);
            (_openedOver, _openedClip) = (null, null);
            if (LocalMediaPath.Length > 0)
            {
                CrashReporter.KeepOut(LocalMediaPath);
                CrashReporter.SetContext("media", CrashSummaries.Media(LocalMediaPath, _mediaInfo, state.Timeline.Tracks.Sum(t => t.Clips.Count)));
                if (string.IsNullOrWhiteSpace(state.DestinationPath))
                    DestinationPath = Path.Combine(GetDefaultOutputFolder(LocalMediaPath), $"{Path.GetFileNameWithoutExtension(LocalMediaPath)}_HandPeg.{Container}");
            }

            // Its selection, as its views of before: followed by their clips' ids into the views the install makes (FollowSelection, RemapClipSelection).
            KeyLayer = tab.KeyLayer;
            _selectedClips.Clear();
            _selectedClips.AddRange(tab.SelectedClips);
            _selectedClipSet.Clear();
            _selectedClipSet.UnionWith(tab.SelectedClips);

            // Its clips still loading after their add are finished as the add would have finished them as their views are made (P2, ViewFor).
            _addsToResume = tab.LoadingAdds.Count > 0 ? tab.LoadingAdds : null;
            ApplyProjectSettings(state, legacyMixing: false);
            InstallProjectTimeline(state, tab.Document, tab.Views);
            RemapClipSelection();
        }
        finally
        {
            _addsToResume = null;
            ReleaseSourceNote();
        }

        // Its history and saved state as they were (no MarkSaved: no fingerprint), unless it was saved while behind.
        ForgetHistory();
        _undo.AddRange(tab.Undo);
        _redo.AddRange(tab.Redo);
        _savedDepth = Math.Min(tab.SavedDepth, _undo.Count);
        (_savedFingerprint, _touchedSinceSaved, _autosavedFingerprint) = (tab.SavedFingerprint, tab.TouchedSinceSaved, tab.AutosavedFingerprint);
        if (tab.MarkSavedOnAttach)
            MarkSaved();

        (NewProjectName, NewProjectFolder) = (tab.NewProjectName, tab.NewProjectFolder);
        _templateToCome = tab.TemplateToCome as ClipTemplate;
        CurrentProjectPath = tab.ProjectPath;
        if (CurrentProjectPath.Length > 0)
            DevFlightRecorder.Record("project", "path " + CurrentProjectPath);

        // What was made of its file in the background, as it left it; what was cut short starts again (cached on the disk).
        SetSourceIFrames(tab.IFrames);
        IFrameStatusText = tab.IFrameStatus;
        (SpriteSheetPath, _spriteSource, SpriteIntervalSeconds) = (tab.SpriteSheetPath, tab.SpriteSource, tab.SpriteIntervalSeconds);
        foreach (var track in AudioTracks)
        {
            if (tab.TrackWaveforms.TryGetValue(track.Index, out var waveform))
                track.Waveform = waveform;
        }

        if (tab.TimelineWaveform is not null && !tab.WaveformsPending)
        {
            // Its tracks' actions put back asked for the mix's picture again (OnTrackChanged): it is the one kept.
            _timelineWaveformCancellation?.Cancel();
            TimelineWaveform = tab.TimelineWaveform;
        }

        if (!_isBackgroundWorker && LocalMediaPath.Length > 0)
        {
            if (tab.IFramesPending)
                _ = ScanIFramesAsync(LocalMediaPath);
            if (tab.WaveformsPending)
                _ = GenerateWaveformsAsync(LocalMediaPath);
            if (tab.SpritePending)
                _ = GenerateSpriteSheetAsync(LocalMediaPath);
        }

        if (tab.UnpackPending)
            _ = UnpackProjectStreamsAsync();
        PositionMs = Math.Max(0, Math.Min(tab.PositionMs, Math.Max(DurationMs, 0)));

        var name = CurrentProjectPath.Length > 0 ? Path.GetFileNameWithoutExtension(CurrentProjectPath) : state.Name;
        var done = name.Length > 0 ? $"Switched to {name}" : "Switched project tabs";
        StatusText = GetLayoutAspectWarning() is { } warning ? $"{done}. {warning}" : done;
        return true;
    }

    // HasSource held while a tab comes back (AttachTab, P12), and whether it was to be told meanwhile.
    private bool _holdingSource;
    private bool _sourceHeld;

    /// <summary>The hold on HasSource let go (AttachTab): told once, now that the tab's own Sequence is in place, if it was to be told meanwhile.</summary>
    private void ReleaseSourceNote()
    {
        _holdingSource = false;
        if (!_sourceHeld)
            return;

        _sourceHeld = false;
        OnPropertyChanged(nameof(HasSource));
    }

    /// <summary>
    /// Whether a tab put behind has unsaved changes (Pass 5.4D-TabPerf, any thread: the thread pool, as its cache file is written): the
    /// fingerprint (<see cref="Fingerprint"/>'s JSON) of its frozen state against its saved state's. The frozen state is only read (5.4D-TabPerf
    /// review, P4/P7/P17: a save, the crash writer or the hover card may read it meanwhile): its name, its Settings' name and the time it was
    /// saved are written as a fingerprint has them ("", "", none: <see cref="FingerprintOptions"/>); its history and thumbnail are never in
    /// the JSON.
    /// </summary>
    internal static bool FrozenStateDiffers(ProjectState frozen, string savedFingerprint) =>
        savedFingerprint.Length > 0 && JsonSerializer.Serialize(frozen, FingerprintOptions) != savedFingerprint;

    /// <summary>
    /// The JSON of a project state as a fingerprint is made (<see cref="Fingerprint"/>: no name, no Settings name, no time saved) without
    /// changing the state: the default serializer's, with those three read as blank. Built once; safe on any thread.
    /// </summary>
    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    foreach (var property in info.Properties)
                    {
                        if (info.Type == typeof(ProjectState) && property.AttributeProvider is System.Reflection.PropertyInfo { Name: nameof(ProjectState.Name) })
                            property.Get = _ => "";
                        else if (info.Type == typeof(ProjectState) && property.AttributeProvider is System.Reflection.PropertyInfo { Name: nameof(ProjectState.SavedAt) })
                            property.Get = _ => default(DateTime);
                        else if (info.Type == typeof(EncodingPreset) && property.AttributeProvider is System.Reflection.PropertyInfo { Name: nameof(EncodingPreset.Name) })
                            property.Get = _ => "";
                    }
                },
            },
        },
    };
}
