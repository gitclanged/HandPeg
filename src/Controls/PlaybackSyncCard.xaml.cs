using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HandPegApp.Services;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp;

/// <summary>
/// Playback &amp; A/V Sync Test card: pressing shows a small preview and runs the test in it (PlaybackSyncTest.RunAsync on its native
/// window) with progress; pressing again stops it. Completed results are raised via <see cref="Completed"/> for the host to persist (the
/// first-run window with its other choices, Settings immediately). The test is cancelled when the card is hidden or unloaded (screen
/// change, window closed).
/// </summary>
public partial class PlaybackSyncCard : UserControl
{
    private CancellationTokenSource? _run;
    private string? _blockedBy;

    public PlaybackSyncCard()
    {
        InitializeComponent();
        Unloaded += (_, _) => _run?.Cancel();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
                _run?.Cancel();
        };
    }

    /// <summary>Test runner; goldens substitute a stub (no engine, no worker).</summary>
    internal Func<nint, IProgress<PlaybackSyncProgress>, CancellationToken, Task<PlaybackSyncResult>> Runner { get; set; } = PlaybackSyncTest.RunAsync;

    /// <summary>Last displayed result (from this card's run or a persisted one); null when none.</summary>
    public PlaybackSyncResult? Result { get; private set; }

    /// <summary>Raised on the UI thread when a run finished with a result (not cancelled, not failed to start).</summary>
    public event Action<PlaybackSyncResult>? Completed;

    public bool IsRunning => _run is not null;

    /// <summary>Raised on the UI thread when a test starts or ends (Settings holds its hardware test meanwhile).</summary>
    public event Action? RunningChanged;

    /// <summary>Reason the test cannot start (e.g. a concurrent hardware check; used as tooltip); null when allowed.</summary>
    public string? BlockedBy
    {
        get => _blockedBy;
        set
        {
            _blockedBy = value;
            UpdateButton();
        }
    }

    /// <summary>Shows a persisted result (AppSettings.LastPlaybackSyncTest) as the last test.</summary>
    public void ShowKept(PlaybackSyncResult? kept)
    {
        if (_run is not null || kept is null)
            return;
        Result = kept;
        ShowResult(kept, "Last test: ");
    }

    /// <summary>Cancels a running test (no-op when idle).</summary>
    public void Cancel() => _run?.Cancel();

    private void Test_Click(object sender, RoutedEventArgs e) => Faults.Forget(Test_ClickAsync(sender, e), nameof(Test_Click));

    private async Task Test_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_run is { } running)
        {
            running.Cancel();
            ShowStatus("Stopping...", warning: false);
            return;
        }

        if (_blockedBy is not null)
            return;

        using var run = _run = new CancellationTokenSource();
        SetRunning(true);
        ShowStatus("Starting...", warning: false);
        try
        {
            var surface = await SurfaceReadyAsync(run.Token);
            var progress = new Progress<PlaybackSyncProgress>(step =>
            {
                if (!ReferenceEquals(_run, run))
                    return;
                RunProgress.Value = step.Fraction;
                ShowStatus($"{step.Step}...", warning: false);
            });

            var result = await Runner(surface, progress, run.Token);
            Result = result;
            ShowResult(result, "");
            if (result.Error.Length == 0)
                Completed?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Test cancelled", warning: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("The Playback & A/V Sync Test failed", ex);
            ShowStatus("The test failed (see the log)", warning: true);
        }
        finally
        {
            _run = null;
            SetRunning(false);
        }
    }

    // Waits until the preview is laid out and its native window exists at final size (the engine sizes its swap chain from it).
    private async Task<nint> SurfaceReadyAsync(CancellationToken token)
    {
        UpdateLayout();
        BringIntoView();
        for (var tries = 0; tries < 80; tries++)
        {
            token.ThrowIfCancellationRequested();
            if (Surface.SurfaceHandle != 0 && IsWindowVisible(Surface.SurfaceHandle) && GetClientRect(Surface.SurfaceHandle, out var rect) && rect.Right > 0 && rect.Bottom > 0)
                return Surface.SurfaceHandle;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(25, token);
        }

        throw new InvalidOperationException("The test's picture could not be shown.");
    }

    private void SetRunning(bool running)
    {
        SurfaceFrame.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.Value = 0;
        UpdateButton();
        RunningChanged?.Invoke();
    }

    private void UpdateButton()
    {
        var running = _run is not null;
        TestButtonText.Text = running ? "Cancel Test" : "Test Playback & A/V Sync";
        TestButtonIcon.Text = running ? Glyphs.Stop : Glyphs.Play;
        TestButton.IsEnabled = running || _blockedBy is null;
        TestButton.ToolTip = running ? "Stops the test" : _blockedBy ?? "Plays a short test on screen and measures A/V sync";
    }

    private void ShowResult(PlaybackSyncResult result, string prefix)
    {
        ShowStatus(prefix + PlaybackSyncTest.Summary(result), warning: !result.Passed);
        StatusText.ToolTip = result.Steps.Count > 0 ? PlaybackSyncTest.Detail(result) : null;
    }

    private void ShowStatus(string text, bool warning)
    {
        StatusText.Text = text;
        StatusText.ToolTip = null;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningTextBrush" : "MutedTextBrush");
    }

}
