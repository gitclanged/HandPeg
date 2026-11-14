using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// The Playback &amp; A/V Sync Test's card (Pass 5.5.2-Release): pressed, the small picture appears and the test plays in it
/// (PlaybackSyncTest.RunAsync on its native window) with its progress; pressed again, it stops. A result that finished is raised by
/// <see cref="Completed"/> for the window to keep (the first-run window with its other choices, Settings at once). The card stops its test
/// when it is hidden or unloaded (a screen changed, the window closed).
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

    /// <summary>The test itself; the goldens put a stand-in here (no engine, no worker).</summary>
    internal Func<nint, IProgress<PlaybackSyncProgress>, CancellationToken, Task<PlaybackSyncResult>> Runner { get; set; } = PlaybackSyncTest.RunAsync;

    /// <summary>The last result shown (this card's run, or the one kept), null none.</summary>
    public PlaybackSyncResult? Result { get; private set; }

    /// <summary>Raised on the UI thread when a run finished with a result (not cancelled, not failed to start).</summary>
    public event Action<PlaybackSyncResult>? Completed;

    public bool IsRunning => _run is not null;

    /// <summary>Raised on the UI thread when a test starts or ends (Settings holds its hardware test meanwhile).</summary>
    public event Action? RunningChanged;

    /// <summary>Why the test cannot start now (a hardware check running beside it: its tooltip), null when it can.</summary>
    public string? BlockedBy
    {
        get => _blockedBy;
        set
        {
            _blockedBy = value;
            UpdateButton();
        }
    }

    /// <summary>A result kept from before (AppSettings.LastPlaybackSyncTest), shown as the last test's.</summary>
    public void ShowKept(PlaybackSyncResult? kept)
    {
        if (_run is not null || kept is null)
            return;
        Result = kept;
        ShowResult(kept, "Last test: ");
    }

    /// <summary>Stops a test running (nothing when none is).</summary>
    public void Cancel() => _run?.Cancel();

    private async void Test_Click(object sender, RoutedEventArgs e)
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

    // The picture shown and laid out, its native window made and shown at its size: the engine's swap chain is made at the window's size.
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
        TestButtonIcon.Text = running ? "" : "";
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out NativeRect rect);
}
