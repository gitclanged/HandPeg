using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// Batch encoding queue.
public partial class MainViewModel
{
    public ObservableCollection<QueueJob> Queue { get; } = [];

    public string QueueButtonText => $"Queue ({Queue.Count(j => j.Status == QueueJob.Pending)})";

    /// <summary>Pause Queue flag: the running job completes, then the queue stops.</summary>
    [ObservableProperty] private bool _isQueuePauseRequested;

    /// <summary>Pending job currently loaded into the main window for editing, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingQueuedJob))]
    [NotifyCanExecuteChangedFor(nameof(UpdateQueuedJobCommand))]
    private QueueJob? _editingJob;

    public bool IsEditingQueuedJob => EditingJob is not null;

    // Note marking a job already retried on a software encoder.
    private const string FallbackNote = "Hardware fallback:";

    /// <summary>Directory for per-job copies of the cuts list.</summary>
    private static string QueueFolder => SessionPaths.Queue;

    private void InitializeQueue() => Queue.CollectionChanged += (_, _) => OnQueueChanged();

    /// <summary>Deletes queue working files; the queue is session-scoped.</summary>
    private static void DeleteQueueFiles()
    {
        try
        {
            if (Directory.Exists(QueueFolder))
                Directory.Delete(QueueFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by an encode still shutting down; the files are tiny, so leave them.
        }
    }

    private void OnQueueChanged()
    {
        // Removed or started jobs are no longer editable.
        if (EditingJob is { } editing && (!Queue.Contains(editing) || editing.Status != QueueJob.Pending))
            EditingJob = null;

        OnPropertyChanged(nameof(QueueButtonText));
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
        EditQueueJobCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Builds a job from the Command Preview text and current state; null (reason in status bar) when nothing to queue.</summary>
    private QueueJob? CreateQueueJob()
    {
        var command = CommandPreview;
        if (!HasSource || string.IsNullOrWhiteSpace(command))
        {
            StatusText = "Load a source before adding to the queue.";
            return null;
        }

        // Refuse an uncuttable Copy export now (with reason) instead of queuing a job that will fail.
        if (CopyRefusal() is { } refused)
        {
            StatusText = refused;
            return null;
        }

        // Cuts and chapters files are rewritten on every settings change, so snapshot per-job copies.
        CaptionJob? captions = null;
        string? ownCaptions = null;
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Per-job caption file (named in its export state; drawn by the compositor). No transcription here: subtitle-track cues are
            // written now, and transcribed captions are deferred to job run time.
            if (CommandUsesCaptions(command))
            {
                Directory.CreateDirectory(QueueFolder);
                var own = ownCaptions = Path.Combine(QueueFolder, $"captions_{Guid.NewGuid():N}.ass");

                if (HasTextCues)
                {
                    var lines = GetCueCaptions();
                    WriteCaptionFile(lines.SelectMany(l => l).ToList(), lines, own, 0);
                }
                else
                {
                    // Transcribed at run time in source time, mapped through the clip layout snapshotted now.
                    var source = ResolveCaptionSource();
                    (captions, var problem) = source is null ? (null, "There is nothing to make captions from.") : CreateCaptionJob(source, own, 0);
                    if (captions is null)
                    {
                        StatusText = problem ?? "The captions cannot be made.";
                        return null;
                    }
                }
            }

            // Snapshot the voiceover too, or the next recording would overwrite it under the job.
            foreach (var shared in new[] { CutsFilePath, ChaptersFilePath, VoiceoverPath })
            {
                var heard = captions is not null && string.Equals(captions.AudioPath, shared, StringComparison.OrdinalIgnoreCase);
                var used = command.Contains(shared, StringComparison.OrdinalIgnoreCase) || (IncludeVoiceover && string.Equals(shared, VoiceoverPath, StringComparison.OrdinalIgnoreCase));
                if (shared.Length == 0 || !File.Exists(shared) || !(heard || used))
                    continue;

                Directory.CreateDirectory(QueueFolder);
                var copy = Path.Combine(QueueFolder, $"{Path.GetFileNameWithoutExtension(shared)}_{Guid.NewGuid():N}{Path.GetExtension(shared)}");
                File.Copy(shared, copy);
                command = command.Replace(shared, copy, StringComparison.OrdinalIgnoreCase);
                copies[shared] = copy;

                // Job captions transcribe from that copy.
                if (heard)
                    captions = captions! with { AudioPath = copy };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"Could not add to the queue: {ex.Message}", "Add to the queue", ex);
            return null;
        }

        return new QueueJob(
            SourcePath.Trim().Trim('"'), DestinationPath.Trim().Trim('"'), command,
            TimeSpan.FromSeconds(GetOutputDuration()), CaptureState()) { Captions = captions, ExportState = JobExportState(command, ownCaptions, copies) };
    }

    /// <summary>
    /// Snapshot export state for a job (sequence plus per-job captions, cuts, chapters, voiceover from CreateQueueJob) for the GPU
    /// compositor in RunExportAsync, the sole renderer (hand-edited commands still encode its frames with their own arguments). Null
    /// only when it cannot be gathered.
    /// </summary>
    private SequenceExportState? JobExportState(string command, string? captions, IReadOnlyDictionary<string, string> copies)
    {
        try
        {
            string Own(string path) => copies.TryGetValue(path, out var copy) ? copy : path;
            var state = CompileExportState();
            return state with
            {
                CaptionsFilePath = captions ?? state.CaptionsFilePath,
                CutsFilePath = Own(state.CutsFilePath),
                ChaptersFilePath = Own(state.ChaptersFilePath),
                VoiceoverPath = Own(state.VoiceoverPath),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write($"A queued job keeps no sequence of its own, so it cannot be drawn ({command.Length} characters of command)", ex);
            return null;
        }
    }

    /// <summary>Subscribes to a job's status so the queue count and buttons stay current.</summary>
    private QueueJob Follow(QueueJob job)
    {
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueJob.Status))
                OnQueueChanged();
        };
        return job;
    }

    /// <summary>
    /// Queues current settings as a new job (a second one while editing a job). Immediate regardless of other work; transcription is
    /// deferred to run time.
    /// </summary>
    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        // Prompt for run-time dependencies and project-supplied command approval now, while the user is present.
        if (!await RequireForCommandAsync("Add to Queue"))
            return;
        if (!ConfirmFileCommand(CommandPreview))
        {
            StatusText = "Not queued: the project's own command was not confirmed.";
            return;
        }

        if (CreateQueueJob() is not { } job)
            return;

        Queue.Add(Follow(job));
        StatusText = job.GenerateCaptions
            ? $"Added to the queue: {Path.GetFileName(job.Target)}. Its captions are transcribed when the job runs."
            : $"Added to the queue: {Path.GetFileName(job.Target)}";
    }

    /// <summary>
    /// Queues a saved project without touching the window: a background VM loads it, builds its command and returns the job. Used
    /// while the window is encoding.
    /// </summary>
    private async Task QueueProjectInBackgroundAsync(ProjectState state, string projectName)
    {
        var worker = new MainViewModel(this);
        try
        {
            await worker.RestoreStateAsync(state, "");

            // A file-supplied command cannot be confirmed here: drop it and use the command generated from the project.
            var dropped = worker.DropFileCommand();
            if (worker.CreateQueueJob() is { } job)
            {
                Queue.Add(Follow(job));

                // The running operation owns the status bar; only write when it is idle.
                if (!IsBusy)
                    StatusText = dropped
                        ? $"Project \"{projectName}\" was added to the queue without its own FFmpeg command (open it to review that command)."
                        : $"Project \"{projectName}\" was added to the queue.";
            }
            else if (!IsBusy)
            {
                StatusText = $"Project \"{projectName}\" could not be queued: {worker.StatusText}";
            }
        }
        finally
        {
            worker.Shutdown();
        }
    }

    private bool CanEditQueueJob(QueueJob? job) => !IsBusy && job is { Status: QueueJob.Pending };

    /// <summary>Loads a pending job's source, cuts and settings into the main window.</summary>
    [RelayCommand(CanExecute = nameof(CanEditQueueJob))]
    private async Task EditQueueJobAsync(QueueJob? job)
    {
        if (job is null)
            return;

        await RestoreStateAsync(job.State, $"Editing queued job: {Path.GetFileName(job.Target)}. Update Queued Job saves the changes.", fromFile: false);

        // Only after the job's source is confirmed loaded.
        if (Queue.Contains(job) && job.Status == QueueJob.Pending
            && string.Equals(LocalMediaPath, job.State.LocalMediaPath, StringComparison.OrdinalIgnoreCase))
        {
            EditingJob = job;
        }
    }

    /// <summary>Replaces the edited job with current settings, preserving queue position.</summary>
    [RelayCommand(CanExecute = nameof(IsEditingQueuedJob))]
    private void UpdateQueuedJob()
    {
        if (EditingJob is not { } editing)
            return;

        var index = Queue.IndexOf(editing);
        if (index < 0 || editing.Status != QueueJob.Pending)
        {
            EditingJob = null;
            StatusText = "That job is no longer waiting in the queue.";
            return;
        }

        // Existing target: overwrite, rename or cancel.
        if (!ResolveOverwrite())
        {
            StatusText = "The queued job was left as it was.";
            return;
        }

        if (CreateQueueJob() is not { } job || !Queue.Contains(editing))
            return;

        // Clear first so the replacement is not treated as a removal.
        EditingJob = null;
        Queue[Queue.IndexOf(editing)] = Follow(job);
        StatusText = $"Queued job updated: {Path.GetFileName(job.Target)}";
    }

    private bool CanStartQueue() => !IsBusy && Queue.Any(j => j.Status == QueueJob.Pending);

    /// <summary>Runs pending jobs sequentially; a failure does not stop later jobs.</summary>
    [RelayCommand(CanExecute = nameof(CanStartQueue))]
    private Task StartQueueAsync() => RunOperationAsync("Encoding the queue", WorkLane.Foreground, async cancellationToken =>
    {
        IsQueuePauseRequested = false;
        var completed = 0;
        var failed = 0;

        while (!IsQueuePauseRequested && Queue.FirstOrDefault(j => j.Status == QueueJob.Pending) is { } job)
        {
            var name = Path.GetFileName(job.Target);
            job.Progress = 0;
            if (!job.Detail.StartsWith(FallbackNote, StringComparison.Ordinal))
                job.Detail = "";
            IsProgressIndeterminate = true;
            SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.Normal);

            // Transcribe pending captions first (queue-time settings) into the job's caption file. Caption failures are kept separate
            // from encode failures, which may be retried in software.
            if (job is { Captions: { } captions, CaptionsReady: false })
            {
                job.Status = QueueJob.Transcribing;
                StatusText = $"[{name}] Transcribing...";
                try
                {
                    var words = await HearCaptionsAsync(captions, cancellationToken, $"[{name}] Transcribing", percent =>
                    {
                        if (job.Status == QueueJob.Transcribing)
                            job.Progress = percent;
                    });
                    // Source-time words mapped through the queue-time clip layout, grouped per visible window so no caption spans a cut.
                    var lines = CaptionProjection.ProjectLines(words, captions.Spans, captions.Style);
                    CaptionGenerator.WriteAss([.. lines.SelectMany(l => l)], captions.Style, captions.Width, captions.Height,
                        captions.TextScale, captions.AssPath, captions.TimeOffset, lines);
                    job.CaptionsReady = true;
                }
                catch (OperationCanceledException)
                {
                    // Cancelled jobs are requeued (not failures).
                    job.Status = QueueJob.Pending;
                    job.Progress = 0;
                    SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.None);
                    throw;
                }
                catch (Exception ex)
                {
                    job.Status = QueueJob.Failed;
                    job.Detail = $"The captions could not be made: {ex.Message}";
                    AppLog.Write($"Captions for the queued job {name} could not be made", ex);
                    failed++;
                    ProgressValue = 0;
                    SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.None);
                    continue;
                }
            }

            job.Status = QueueJob.Encoding;
            job.Progress = 0;
            IsProgressIndeterminate = true;
            StatusText = $"[{name}] Encoding...";

            var expected = job.ExpectedDuration;
            var progress = new Progress<FfmpegProgress>(report =>
            {
                // Progress is posted asynchronously; ignore reports arriving after the job ends.
                if (!_acceptProgressReports || job.Status != QueueJob.Encoding)
                    return;

                if (expected <= TimeSpan.Zero && report.InputDuration is { } inputDuration)
                    expected = inputDuration;

                if (report.Position is { } position && expected > TimeSpan.Zero)
                {
                    IsProgressIndeterminate = false;
                    job.Progress = ProgressValue = Math.Clamp(position / expected * 100, 0, 100);
                    SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.Normal, ProgressValue / 100);
                }

                if (report.Line.Length > 0)
                    StatusText = $"[{name}] {report.Line}";
            });

            try
            {
                // Crash-report context: encoders, output kind and duration; never file paths or names.
                CrashReporter.KeepOut(job.Source, job.Target);
                var jobSummary = CrashSummaries.Command(job.Command, expected);
                CrashReporter.SetContext("export", "queue job: " + jobSummary);
                CrashReporter.Note("queue", "job started: " + jobSummary);

                // A job is its final (possibly hand-edited) command plus, when reproducible, its sequence (QueueJob.ExportState): the GPU
                // compositor renders it then, FFmpeg's graph otherwise.
                await RunExportAsync(job.Command, job.ExportState, progress, cancellationToken);
                CrashReporter.Note("queue", "job finished");
                job.Status = QueueJob.Complete;
                job.Progress = 100;
                completed++;
            }
            catch (OperationCanceledException)
            {
                // Cancelled jobs are requeued (not failures).
                job.Status = QueueJob.Pending;
                job.Progress = 0;
                SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.None);
                throw;
            }
            catch (InvalidOperationException ex) when (
                ex is not FfmpegCommandException
                && AppSettings.Current.AutoFallbackToSoftware
                && !job.Detail.StartsWith(FallbackNote, StringComparison.Ordinal)
                && HardwareFallback.TryRewrite(job.Command, out _, out _, out _))
            {
                // Hardware encoder failed: requeue with the same codec's software encoder; the job note prevents a second rewrite.
                HardwareFallback.TryRewrite(job.Command, out var rewritten, out var hardware, out var software);
                AppLog.Write($"{hardware} failed for {name} and {software} is tried instead", ex);
                job.Command = rewritten;
                job.Detail = $"{FallbackNote} {hardware} failed, retried with {software}.";
                job.Status = QueueJob.Pending;
                job.Progress = 0;
                StatusText = $"Warning: {hardware} failed for {name}. Retrying with {software}...";
            }
            catch (Exception ex)
            {
                AppLog.Write($"Queue: {name} failed", ex);
                job.Status = QueueJob.Failed;
                job.Detail = ex.Message;
                failed++;
                CrashReporter.Note("queue", "job failed: " + ex.GetType().Name);
            }

            ProgressValue = 0;
            SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.None);
        }

        CrashReporter.SetContext("export", null);
        var summary = $"{completed} complete, {failed} failed";
        if (completed + failed > 0 && !IsQueuePauseRequested)
            NotifyFinished("Queue finished", summary);
        return IsQueuePauseRequested ? $"Queue paused: {summary}." : $"Queue finished: {summary}.";
    });

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void PauseQueue()
    {
        IsQueuePauseRequested = true;
        StatusText = "The queue will pause when the current job finishes.";
    }

    private bool CanClearCompleted() => Queue.Any(j => j.Status == QueueJob.Complete);

    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    private void ClearCompleted()
    {
        foreach (var job in Queue.Where(j => j.Status == QueueJob.Complete).ToList())
            Queue.Remove(job);
    }

    [RelayCommand]
    private void RemoveQueueJob(QueueJob? job)
    {
        if (job is { CanRemove: true })
            Queue.Remove(job);
    }
}
