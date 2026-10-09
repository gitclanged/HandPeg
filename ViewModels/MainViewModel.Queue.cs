using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// The batch encoding queue.
public partial class MainViewModel
{
    public ObservableCollection<QueueJob> Queue { get; } = [];

    public string QueueButtonText => $"Queue ({Queue.Count(j => j.Status == QueueJob.Pending)})";

    /// <summary>Set by Pause Queue: the running job finishes, then the queue stops.</summary>
    [ObservableProperty] private bool _isQueuePauseRequested;

    /// <summary>The pending job whose settings are loaded in the main window for editing, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingQueuedJob))]
    [NotifyCanExecuteChangedFor(nameof(UpdateQueuedJobCommand))]
    private QueueJob? _editingJob;

    public bool IsEditingQueuedJob => EditingJob is not null;

    // Marks a job that has already been moved to a software encoder once.
    private const string FallbackNote = "Hardware fallback:";

    /// <summary>Where queued jobs keep their own copy of the cuts list.</summary>
    private static string QueueFolder => SessionPaths.Queue;

    private void InitializeQueue() => Queue.CollectionChanged += (_, _) => OnQueueChanged();

    /// <summary>The queue does not outlive the session, so neither do its working files.</summary>
    private static void DeleteQueueFiles()
    {
        try
        {
            if (Directory.Exists(QueueFolder))
                Directory.Delete(QueueFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use by an encode that is shutting down; the files are tiny and can stay.
        }
    }

    private void OnQueueChanged()
    {
        // A job that was removed, or has started encoding, can no longer be edited.
        if (EditingJob is { } editing && (!Queue.Contains(editing) || editing.Status != QueueJob.Pending))
            EditingJob = null;

        OnPropertyChanged(nameof(QueueButtonText));
        StartQueueCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
        EditQueueJobCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Builds a job from the command in the Command Preview box and the current state of the window.
    /// Returns null, with the reason in the status bar, when there is nothing to queue.
    /// </summary>
    private QueueJob? CreateQueueJob()
    {
        var command = CommandPreview;
        if (!HasSource || string.IsNullOrWhiteSpace(command))
        {
            StatusText = "Load a source before adding to the queue.";
            return null;
        }

        // A caption file that was never made (a project queued in the background, say) cannot be copied.
        if (CommandUsesCaptions(command) && !File.Exists(CaptionsFilePath))
        {
            StatusText = "Auto-captions have to be transcribed before this can be queued: load it and use Add to Queue.";
            return null;
        }

        // The cuts and chapters lists are rewritten whenever the settings change, so the job gets copies of its own.
        try
        {
            // The caption file likewise. The command names it the way a filter needs it written.
            if (CommandUsesCaptions(command))
            {
                Directory.CreateDirectory(QueueFolder);
                var copy = Path.Combine(QueueFolder, $"captions_{Guid.NewGuid():N}.ass");
                File.Copy(CaptionsFilePath, copy);
                command = command.Replace(FilterGraphBuilder.CaptionFilter(CaptionsFilePath), FilterGraphBuilder.CaptionFilter(copy), StringComparison.OrdinalIgnoreCase);
            }

            // The voiceover too: the next recording would otherwise replace it under the job.
            foreach (var shared in new[] { CutsFilePath, ChaptersFilePath, VoiceoverPath })
            {
                if (shared.Length == 0 || !command.Contains(shared, StringComparison.OrdinalIgnoreCase) || !File.Exists(shared))
                    continue;

                Directory.CreateDirectory(QueueFolder);
                var copy = Path.Combine(QueueFolder, $"{Path.GetFileNameWithoutExtension(shared)}_{Guid.NewGuid():N}{Path.GetExtension(shared)}");
                File.Copy(shared, copy);
                command = command.Replace(shared, copy, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not add to the queue: {ex.Message}";
            return null;
        }

        return new QueueJob(
            SourcePath.Trim().Trim('"'), DestinationPath.Trim().Trim('"'), Segments.Count, command,
            TimeSpan.FromSeconds(GetOutputDuration()), CaptureState());
    }

    /// <summary>Starts following a job's status, so the queue count and buttons stay right as it runs.</summary>
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
    /// Builds a job as <see cref="CreateQueueJob"/> does, first making the caption file when the command
    /// draws one: a job carries its own copy of it, so it has to exist when the job is made.
    /// </summary>
    private async Task<QueueJob?> CreateQueueJobAsync()
    {
        if (!CommandUsesCaptions(CommandPreview))
            return CreateQueueJob();

        if (IsBusy)
        {
            StatusText = "This job needs its captions transcribed first: wait for the running operation to finish, then add it.";
            return null;
        }

        QueueJob? job = null;
        await RunOperationAsync(async cancellationToken =>
        {
            if (await PrepareCaptionsForEncodeAsync(CommandPreview, cancellationToken) is { } problem)
                return problem;

            job = CreateQueueJob();
            return null;
        });
        return job;
    }

    /// <summary>Queues the current settings as a new job. While editing a job, this makes a second one.</summary>
    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (await CreateQueueJobAsync() is not { } job)
            return;

        Queue.Add(Follow(job));
        StatusText = $"Added to the queue: {Path.GetFileName(job.Target)}";
    }

    /// <summary>
    /// Turns a saved project into a pending job without touching the window: a background instance loads
    /// the project, builds its command, and hands the job over. Used when the window is busy encoding.
    /// </summary>
    private async Task QueueProjectInBackgroundAsync(ProjectState state, string projectName)
    {
        var worker = new MainViewModel(this);
        try
        {
            await worker.RestoreStateAsync(state, "");
            if (worker.CreateQueueJob() is { } job)
            {
                Queue.Add(Follow(job));

                // The running operation owns the status bar; this only shows when nothing else is writing to it.
                if (!IsBusy)
                    StatusText = $"Project \"{projectName}\" was added to the queue.";
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

    /// <summary>Loads a pending job's source, cuts and settings back into the main window.</summary>
    [RelayCommand(CanExecute = nameof(CanEditQueueJob))]
    private async Task EditQueueJobAsync(QueueJob? job)
    {
        if (job is null)
            return;

        await RestoreStateAsync(job.State, $"Editing queued job: {Path.GetFileName(job.Target)}. Update Queued Job saves the changes.");

        // Only once the job's source really is the one on screen.
        if (Queue.Contains(job) && job.Status == QueueJob.Pending
            && string.Equals(LocalMediaPath, job.State.LocalMediaPath, StringComparison.OrdinalIgnoreCase))
        {
            EditingJob = job;
        }
    }

    /// <summary>Replaces the job being edited with the current settings, keeping its place in the queue.</summary>
    [RelayCommand(CanExecute = nameof(IsEditingQueuedJob))]
    private async Task UpdateQueuedJobAsync()
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

        // The job's target may already exist: overwrite it, pick another name, or back out.
        if (!ResolveOverwrite())
        {
            StatusText = "The queued job was left as it was.";
            return;
        }

        if (await CreateQueueJobAsync() is not { } job || !Queue.Contains(editing))
            return;

        // Cleared first: replacing the job would otherwise look like its removal.
        EditingJob = null;
        Queue[Queue.IndexOf(editing)] = Follow(job);
        StatusText = $"Queued job updated: {Path.GetFileName(job.Target)}";
    }

    private bool CanStartQueue() => !IsBusy && Queue.Any(j => j.Status == QueueJob.Pending);

    /// <summary>Runs the pending jobs one after another. A failed job does not stop the ones behind it.</summary>
    [RelayCommand(CanExecute = nameof(CanStartQueue))]
    private Task StartQueueAsync() => RunOperationAsync(async cancellationToken =>
    {
        IsQueuePauseRequested = false;
        var completed = 0;
        var failed = 0;

        while (!IsQueuePauseRequested && Queue.FirstOrDefault(j => j.Status == QueueJob.Pending) is { } job)
        {
            job.Status = QueueJob.Encoding;
            job.Progress = 0;
            if (!job.Detail.StartsWith(FallbackNote, StringComparison.Ordinal))
                job.Detail = "";
            IsProgressIndeterminate = true;
            SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.Normal);

            var expected = job.ExpectedDuration;
            var name = Path.GetFileName(job.Target);
            var progress = new Progress<FfmpegProgress>(report =>
            {
                // Reports are posted, so a late one can arrive after its job has ended.
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
                await FfmpegRunner.RunAsync(job.Command, progress, cancellationToken);
                job.Status = QueueJob.Complete;
                job.Progress = 100;
                completed++;
            }
            catch (OperationCanceledException)
            {
                // A cancelled job was not at fault: it goes back in line.
                job.Status = QueueJob.Pending;
                job.Progress = 0;
                SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.None);
                throw;
            }
            catch (InvalidOperationException) when (
                AppSettings.Current.AutoFallbackToSoftware
                && !job.Detail.StartsWith(FallbackNote, StringComparison.Ordinal)
                && HardwareFallback.TryRewrite(job.Command, out _, out _, out _))
            {
                // The hardware encoder failed: put the job back in line with the software encoder for the same codec.
                // The note on the job stops it from being rewritten a second time.
                HardwareFallback.TryRewrite(job.Command, out var rewritten, out var hardware, out var software);
                job.Command = rewritten;
                job.Detail = $"{FallbackNote} {hardware} failed, retried with {software}.";
                job.Status = QueueJob.Pending;
                job.Progress = 0;
                StatusText = $"Warning: {hardware} failed for {name}. Retrying with {software}...";
            }
            catch (Exception ex)
            {
                job.Status = QueueJob.Failed;
                job.Detail = ex.Message;
                failed++;
            }

            ProgressValue = 0;
            SetTaskbarProgress(System.Windows.Shell.TaskbarItemProgressState.None);
        }

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
