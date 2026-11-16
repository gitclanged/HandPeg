using System.IO;
using System.Text.Json;
using System.Windows.Media;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- Project tabs (see MainWindow.ProjectTabs.cs) -----
// One VM and one Sequence serve all title-bar tabs; only the active tab lives here. A backgrounded tab is cached as its own project
// file with history (ProjectTabs, %Cache%\project-tabs\<session>\<id>.hptab) and restored like an open (RestoreStateAsync), resetting
// per-project fields. The clipboard (_copied) is VM-wide, so copy/paste works across tabs.
// Tabs activated this session stay in memory when backgrounded (DetachForTab: live Sequence, history by reference, fingerprints,
// probed file) and return by reference swap (AttachTab) with no file read, probe or fingerprint, reusing ApplyProjectSettings and
// InstallProjectTimeline. Background work for the departing tab is cancelled via _projectGeneration and restarts (disk-cached) on return.
public partial class MainViewModel
{
    /// <summary>
    /// Project generation, bumped when a tab is backgrounded so late continuations (UnpackProjectStreamsAsync) detect a stale project and no-op.
    /// </summary>
    private int _projectGeneration;

    /// <summary>Current <see cref="_projectGeneration"/>, for window-side adds spanning their own awaits (multi-file drops).</summary>
    internal int ProjectGeneration => _projectGeneration;

    // In-flight stream unpack count for the current project (UnpackProjectStreamsAsync).
    private int _unpacking;

    // ----- Pending adds -----
    // File adds await a probe (AddAudioLayerAsync, AddVideoClipAsync) during which a tab switch could occur. The window blocks switches
    // while HasPendingAdds (MainWindow.MayChangeTabs), and each add captures the generation and no-ops if it changed.

    // VM-wide count of in-flight file adds (drop files, voiceover placement).
    private int _pendingAdds;

    /// <summary>Whether a file add is in flight (probe, remaining drop files, voiceover); tab switches are blocked meanwhile.</summary>
    public bool HasPendingAdds => _pendingAdds > 0;

    /// <summary>In-flight file add count (<see cref="HasPendingAdds"/>).</summary>
    internal int PendingAdds => _pendingAdds;

    /// <summary>Scope counting a file add in <see cref="HasPendingAdds"/> until disposed (UI thread).</summary>
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
    /// Completion parameters for a dropped video once probed: play-once vs loop, fit/fill box, sub-clip range. Travels with a
    /// backgrounded tab so the add completes identically on return.
    /// </summary>
    internal readonly record struct LoadingAdd(bool PlayOnce, (int X, int Y, int Width, int Height)? FitInto, bool Fill, (double In, double Out)? SourceRange);

    // Current project's video adds awaiting completion, by clip id (FinishVideoAddAsync).
    private readonly Dictionary<Guid, LoadingAdd> _loadingAdds = [];

    // A returning tab's pending adds, consumed while its views are built (AttachTab -> ViewFor); null otherwise.
    private Dictionary<Guid, LoadingAdd>? _addsToResume;

    /// <summary>
    /// Backgrounded tab snapshot: <see cref="CaptureState"/> plus shared immutable history and the last thumbnail. Never waits for a
    /// fresh thumbnail (<see cref="ThumbnailForSave"/> can block), so switches never stall on the engine. Safe to write off-thread.
    /// </summary>
    public ProjectState CaptureStateForTab(string name)
    {
        var state = CaptureState(name);
        (state.UndoHistory, state.RedoHistory) = (HistoryOf(_undo, null), HistoryOf(_redo, null));
        state.ThumbnailJpeg = Volatile.Read(ref _thumbnail);
        return state;
    }

    /// <summary>Saved-state fingerprint, kept with a dirty backgrounded tab so it returns still dirty.</summary>
    public string SavedFingerprintForTab => _savedFingerprint;

    /// <summary>
    /// Cheap dirty pre-check (content present, touched since save/open, baseline exists). False implies <see cref="HasUnsavedChanges"/>
    /// is false without fingerprinting; true requires the full check.
    /// </summary>
    public bool MayHaveUnsavedChanges => _touchedSinceSaved && _savedFingerprint.Length > 0;

    /// <summary>
    /// Whether a backgrounding tab is empty: no file or clips, no history (deleted clips may be undoable), no explicit frame/rate,
    /// no studio name, no pending Game Preset and empty Project Media (studio picks land there without undo). Only empty tabs are
    /// released; others are cached.
    /// </summary>
    public bool HoldsNothingForTab => !HasSource && _undo.Count == 0 && _redo.Count == 0 && !Document.ExplicitFrame && !Document.ExplicitFrameRate
        && NewProjectName.Length == 0 && _templateToCome is null && Document.MediaPool.Count == 0;

    /// <summary>Opaque pending Game Preset (null for none), kept in memory with a backgrounded tab's header.</summary>
    public object? TemplateWaitingForTab => _templateToCome;

    /// <summary>
    /// Restores a cached tab like an open (<see cref="OpenShowingCaptionsAsync"/>; never written back, the state is a cache copy), sets its
    /// Save path and re-applies its dirty state (<see cref="RestoreUnsavedState"/>). Streams unpack in the background and files are
    /// excluded from crash reports, as for an open.
    /// </summary>
    /// <param name="projectPath">Project file; null or empty when never saved.</param>
    /// <param name="dirty">Whether the tab was dirty when backgrounded.</param>
    /// <param name="savedFingerprint">Saved-state fingerprint (<see cref="SavedFingerprintForTab"/>) for a dirty tab; null when unknown.</param>
    /// <param name="newProjectName">New Project studio name ("" for none); never inherited from the previous tab.</param>
    /// <param name="newProjectFolder">Studio-chosen folder for the first save (null = Projects folder).</param>
    /// <param name="waitingTemplate">Pending Game Preset (<see cref="TemplateWaitingForTab"/>); null for none.</param>
    public async Task ResumeTabAsync(ProjectState state, string? projectPath, bool dirty, string? savedFingerprint,
        string newProjectName = "", string? newProjectFolder = null, object? waitingTemplate = null)
    {
        EditingJob = null;
        CancelProjectBackgroundWork();
        CrashReporter.KeepOut(CrashSummaries.FilesIn(state));
        var name = projectPath is { Length: > 0 } ? Path.GetFileNameWithoutExtension(projectPath) : state.Name;
        var before = Document;
        await OpenShowingCaptionsAsync(state, name.Length > 0 ? $"Switched to {name}" : "Switched project tabs");

        // Restore failed (media missing, busy): adopt nothing, so the current Sequence is never saved as this tab's (the window clears
        // it or returns to the previous tab).
        if (ReferenceEquals(before, Document))
            return;

        // Open would keep the previous tab's studio name, folder and pending preset: override with this tab's.
        KeepNewProjectName(newProjectName, newProjectFolder);
        _templateToCome = waitingTemplate as ClipTemplate;
        CurrentProjectPath = projectPath ?? "";
        if (CurrentProjectPath.Length > 0)
            DevFlightRecorder.Record("project", "path " + CurrentProjectPath);
        RestoreUnsavedState(savedFingerprint, dirty);
        _ = UnpackProjectStreamsAsync();
    }

    /// <summary>
    /// After Rename Project moved the file (MainWindow), retargets Save to <paramref name="newPath"/> without saving or changing dirty
    /// state. Returns a status line.
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
    /// After restore (which calls <see cref="MarkSaved"/>), re-applies the tab's dirty state: the baseline becomes its pre-background
    /// saved fingerprint, or an unmatchable one when unknown. Errs toward dirty: a false "unsaved" costs a prompt, a false "saved" loses work.
    /// </summary>
    internal void RestoreUnsavedState(string? savedFingerprint, bool dirty)
    {
        if (!dirty)
            return;

        _savedFingerprint = string.IsNullOrEmpty(savedFingerprint) ? "\0unsaved" : savedFingerprint;
        _touchedSinceSaved = true;
    }

    // ----- In-memory backgrounded tabs -----

    /// <summary>
    /// VM-held state of a backgrounded tab, kept until <see cref="AttachTab"/> or eviction to its cache file. <see cref="State"/> references
    /// the detached live Sequence (no Committed handler; <see cref="Document"/> stays the only timeline); <see cref="Frozen"/> is an isolated
    /// copy with history and thumbnail (<see cref="CaptureStateForTab"/>) for pool-thread cache writes, saves and duplicates. Immutable while backgrounded.
    /// </summary>
    public sealed class TabProjectContext
    {
        internal TabProjectContext(ProjectState state, ProjectState frozen)
        {
            State = state;
            Frozen = frozen;
        }

        /// <summary>The tab's state; its Timeline is the tab's live Sequence.</summary>
        public ProjectState State { get; }

        /// <summary>Isolated copy with history and thumbnail; the source for cache file writes.</summary>
        public ProjectState Frozen { get; }

        /// <summary>The tab's Sequence (reinstalled as <see cref="MainViewModel.Document"/> on return).</summary>
        public TimelineDocument Document => State.Timeline;

        /// <summary>Project file ("" if never saved); updated by background saves (MainWindow.SaveTabInPlace).</summary>
        public string ProjectPath { get; set; } = "";

        /// <summary>New Project studio name and first-save folder (cleared by a background save).</summary>
        public string NewProjectName { get; set; } = "";

        public string? NewProjectFolder { get; set; }

        /// <summary>Saved while backgrounded (MainWindow.SaveTabInPlace); marked saved on return (one fingerprint).</summary>
        public bool MarkSavedOnAttach { get; set; }

        /// <summary><see cref="MayHaveUnsavedChanges"/> at background time.</summary>
        public bool MayBeDirty { get; init; }

        /// <summary>Saved-state fingerprint (<see cref="SavedFingerprintForTab"/>) at background time; "" for none.</summary>
        public string SavedFingerprint { get; init; } = "";

        /// <summary>Undo and redo step counts.</summary>
        public int UndoDepthNow => Undo.Count;

        public int RedoDepthNow => Redo.Count;

        internal List<(string What, byte[] State)> Undo { get; init; } = [];
        internal List<(string What, byte[] State)> Redo { get; init; } = [];
        internal bool TouchedSinceSaved { get; init; }

        /// <summary>Undo steps predating the last save/open (bounds recovery-copy lag).</summary>
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
        internal IReadOnlyList<ImageSource> TrackWaveforms { get; init; } = [];
        internal ImageSource? TimelineWaveform { get; init; }
        internal bool WaveformsPending { get; init; }
        internal bool UnpackPending { get; init; }
        internal double PositionMs { get; init; }
        internal Layer? KeyLayer { get; init; }
        internal List<Layer> SelectedClips { get; init; } = [];

        /// <summary>Pending video adds by clip id, completed with their original parameters on return.</summary>
        internal Dictionary<Guid, LoadingAdd> LoadingAdds { get; init; } = [];

        /// <summary>Clip views reused on return for the unchanged Sequence (MainViewModel.KeptViews).</summary>
        internal KeptViews? Views { get; init; }
    }

    /// <summary>
    /// Backgrounds the active tab in memory (UI thread; caller ensures no playback, operation or recording via MainWindow.MayChangeTabs):
    /// moves its project state into a context (detached Sequence, history, fingerprints, views via <see cref="LetGoOfViews"/>), cancels its
    /// background work, and installs an empty stand-in Sequence so nothing before the next install can touch the backgrounded tab.
    /// </summary>
    public TabProjectContext DetachForTab(string title)
    {
        var frozen = CaptureStateForTab(title);
        var state = CaptureState(title, liveTimeline: true);

        // Detach views first (looks already flushed by CaptureState), before anything else runs.
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
            TrackWaveforms = [.. SourceWaveforms],
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

        // Empty the views list and selection (kept in the context). Inspector and Keyframes also drop the selected view: a nudge while
        // the next tab loads would edit an unobserved view whose clip would later absorb it.
        Layers.Clear();
        KeyLayer = null;
        _selectedClips.Clear();
        _selectedClipSet.Clear();
        ForgetHistory();
        (_savedFingerprint, _touchedSinceSaved, _autosavedFingerprint) = ("", false, "");
        return tab;
    }

    /// <summary>
    /// Cancels the current project's background work on backgrounding: I-frame scan, waveforms (their pictures dropped too), hover sheet (each
    /// checks its token after awaiting), and bumps the generation for untokened work (stream unpack, file adds). Interrupted work restarts in
    /// <see cref="AttachTab"/>.
    /// </summary>
    private void CancelProjectBackgroundWork()
    {
        _projectGeneration++;
        _unpacking = 0;
        _loadingAdds.Clear();
        _iFrameScanCancellation?.Cancel();
        ForgetWaveforms();
        _spriteCancellation?.Cancel();
    }

    /// <summary>
    /// Installs an empty stand-in Sequence (no views; the next project builds its own) so settings applied during the next install and
    /// anything done while its file loads hit the stand-in, never the backgrounded tab.
    /// </summary>
    internal void ParkDocument()
    {
        Document.Committed -= OnDocumentCommitted;
        Document = NewDocument();
        Document.Committed += OnDocumentCommitted;
        _previewState = null;
    }

    /// <summary>
    /// Re-activates an in-memory tab (UI thread) via <see cref="ApplyProjectSettings"/> and <see cref="InstallProjectTimeline"/> on its live
    /// Sequence, restoring probed file info, history, fingerprints (no new fingerprint), file, studio name/folder, pending preset, I-frames,
    /// waveforms, hover sheet, views (<see cref="TakeUpViews"/>) and selection. False (no change) while an operation or recording is active.
    /// </summary>
    public bool AttachTab(TabProjectContext tab)
    {
        if (IsBusy || HoldsRecording)
            return false;

        // Cancel work of the outgoing project (e.g. a just-closed tab) so it cannot land in this one.
        CancelProjectBackgroundWork();
        var state = tab.State;
        EditingJob = null;
        CrashReporter.KeepOut(CrashSummaries.FilesIn(state));
        ConvertedFrom = tab.ConvertedFrom;
        TakeUpThumbnail(tab.Thumbnail);
        SourcePath = state.SourcePath;
        DownloadResolution = Pick(DownloadResolutions, state.DownloadResolution, "Best");
        DownloadSubtitles = state.DownloadSubtitles;

        // Hold HasSource until the tab's Sequence is installed: a transient false (file set before Sequence, shape note) would make the
        // window release the engine.
        _holdingSource = true;
        try
        {
            // Restore the previously probed file info (skipping LoadMediaAsync's probe and folder write test).
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

            // Remap the saved selection by clip id onto installed views (FollowSelection, RemapClipSelection).
            KeyLayer = tab.KeyLayer;
            _selectedClips.Clear();
            _selectedClips.AddRange(tab.SelectedClips);
            _selectedClipSet.Clear();
            _selectedClipSet.UnionWith(tab.SelectedClips);

            // Complete pending adds with their original parameters as views are built (ViewFor).
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

        // Restore history and saved baseline without MarkSaved (no fingerprint), unless saved while backgrounded.
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

        // Restore completed background results; restart interrupted work (disk-cached).
        SetSourceIFrames(tab.IFrames);
        IFrameStatusText = tab.IFrameStatus;
        (SpriteSheetPath, _spriteSource, SpriteIntervalSeconds) = (tab.SpriteSheetPath, tab.SpriteSource, tab.SpriteIntervalSeconds);
        SourceWaveforms.Clear();
        foreach (var waveform in tab.TrackWaveforms)
            SourceWaveforms.Add(waveform);

        if (tab.TimelineWaveform is not null && !tab.WaveformsPending)
        {
            // Restoring track actions re-requested the mix picture (OnTrackChanged); keep the saved one.
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

    // HasSource notification hold during AttachTab, and whether a notification is pending.
    private bool _holdingSource;
    private bool _sourceHeld;

    /// <summary>Releases the HasSource hold after AttachTab, raising once if a notification was pending.</summary>
    private void ReleaseSourceNote()
    {
        _holdingSource = false;
        if (!_sourceHeld)
            return;

        _sourceHeld = false;
        OnPropertyChanged(nameof(HasSource));
    }

    /// <summary>
    /// Dirty check for a backgrounded tab (thread-safe; runs on the pool during cache writes): compares <see cref="Fingerprint"/> JSON of
    /// the frozen state with the saved baseline. Read-only (saves, crash writer and hover card may read concurrently); name, settings name
    /// and save time serialize blank via <see cref="FingerprintOptions"/>; history and thumbnail are excluded.
    /// </summary>
    internal static bool FrozenStateDiffers(ProjectState frozen, string savedFingerprint) =>
        savedFingerprint.Length > 0 && JsonSerializer.Serialize(frozen, FingerprintOptions) != savedFingerprint;

    /// <summary>
    /// Serializer options producing <see cref="Fingerprint"/> JSON without mutating the state: default options with name, settings
    /// name and save time read as blank. Built once; thread-safe.
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
