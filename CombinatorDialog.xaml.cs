using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>
/// The Video Combinator: picks two videos, a frame size and a transition, and joins them with FFmpeg, either
/// into a file the user names or into a temporary one that is then loaded into the editor.
/// </summary>
public partial class CombinatorDialog : Window
{
    private const string VideoFilter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg|All files|*.*";

    private MediaInfo? _infoA;
    private MediaInfo? _infoB;
    private CancellationTokenSource? _probeCancellation;
    private CancellationTokenSource? _runCancellation;

    public CombinatorDialog()
    {
        InitializeComponent();
        ResolutionBox.ItemsSource = VideoCombinator.Resolutions;
        ResolutionBox.SelectedIndex = 0;
        TransitionBox.ItemsSource = VideoCombinator.Transitions;
        TransitionBox.SelectedIndex = 0;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        UpdateSummary();
    }

    /// <summary>Set when Send to Editor has finished: the joined video, for the main window to load.</summary>
    public string? SendToEditorPath { get; private set; }

    private bool IsRunning => _runCancellation is not null;

    // ----- Choosing the videos -----

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox target })
            return;

        var dialog = new OpenFileDialog { Title = "Select a video", Filter = VideoFilter };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FileName;
    }

    // A text box takes text drops by itself and refuses files, so files are let through here.
    private void PathBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void PathBox_Drop(object sender, DragEventArgs e)
    {
        if (sender is TextBox box && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            box.Text = files[0];
            e.Handled = true;
        }
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e) => _ = InspectAsync();

    private void Choice_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private static string Clean(string path) => path.Trim().Trim('"');

    /// <summary>Looks at both files, so the summary can say what will come out and the buttons know whether to work.</summary>
    private async Task InspectAsync()
    {
        _probeCancellation?.Cancel();
        var cancellation = _probeCancellation = new CancellationTokenSource();
        try
        {
            var (a, b) = (await ProbeAsync(Clean(PathABox.Text), cancellation.Token), await ProbeAsync(Clean(PathBBox.Text), cancellation.Token));
            if (cancellation.IsCancellationRequested)
                return;

            (_infoA, _infoB) = (a, b);
            InfoAText.Text = Describe(Clean(PathABox.Text), a);
            InfoBText.Text = Describe(Clean(PathBBox.Text), b);
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<MediaInfo?> ProbeAsync(string path, CancellationToken cancellationToken) =>
        path.Length > 0 && File.Exists(path) ? await MediaProbe.ProbeAsync(path, cancellationToken) : null;

    private static string Describe(string path, MediaInfo? info)
    {
        if (path.Length == 0)
            return "";
        if (!File.Exists(path))
            return "File not found.";
        if (info?.Video is not { } video)
            return File.Exists(DependencyUpdater.FfprobePath) ? "Not a video that can be read." : "FFmpeg is not installed: install it from Settings first.";

        return $"{video.Width} x {video.Height}, {video.FrameRate:0.##} fps, {Models.TimeDisplay.Format(info.DurationSeconds)}"
               + (info.Audio.Count == 0 ? ", no sound" : "");
    }

    private bool IsReady => _infoA?.Video is not null && _infoB?.Video is not null;

    private void UpdateSummary()
    {
        // Raised while the window is still being built.
        if (SummaryText is null || ResolutionBox.SelectedItem is not string resolution || TransitionBox.SelectedItem is not string transition)
            return;

        if (!IsRunning)
            ExportButton.IsEnabled = SendButton.IsEnabled = IsReady;

        if (_infoA is not { Video: not null } a || _infoB is not { Video: not null } b)
        {
            SummaryText.Text = "Choose the two videos to join.";
            return;
        }

        var (width, height) = VideoCombinator.GetFrameSize(resolution, a, b);
        var note = transition != VideoCombinator.HardSplice && !VideoCombinator.UsesTransition(a, b, transition)
            ? " One of the videos is too short for a one-second transition, so they will be spliced."
            : "";
        SummaryText.Text = $"Result: {width} x {height}, {Models.TimeDisplay.Format(VideoCombinator.GetOutputSeconds(a, b, transition))} long.{note}";
    }

    // ----- Joining -----

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var pathA = Clean(PathABox.Text);
        var dialog = new SaveFileDialog
        {
            Title = "Save the combined video as",
            Filter = "MP4|*.mp4",
            DefaultExt = "mp4",
            FileName = $"{Path.GetFileNameWithoutExtension(pathA)}_combined.mp4",
            InitialDirectory = Path.GetDirectoryName(pathA) ?? "",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (await RunAsync(dialog.FileName))
            StatusText.Text = $"Saved {dialog.FileName}";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        // In the session's folder: a working file, which goes when HandPeg closes.
        var folder = Path.Combine(SessionPaths.Root, "combined");
        var output = Path.Combine(folder, $"combined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        if (!await RunAsync(output))
            return;

        SendToEditorPath = output;
        DialogResult = true;
    }

    /// <summary>Runs the join. Returns whether the file was made; what went wrong otherwise is shown in the dialog.</summary>
    private async Task<bool> RunAsync(string outputPath)
    {
        var (pathA, pathB) = (Clean(PathABox.Text), Clean(PathBBox.Text));
        if (IsRunning || _infoA is not { Video: not null } a || _infoB is not { Video: not null } b
            || ResolutionBox.SelectedItem is not string resolution || TransitionBox.SelectedItem is not string transition)
        {
            return false;
        }

        if (new[] { pathA, pathB }.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus("The result cannot be saved over one of the two videos it is made from.", isProblem: true);
            return false;
        }

        var cancellation = _runCancellation = new CancellationTokenSource();
        SetRunning(true);
        ShowStatus("Starting FFmpeg...", isProblem: false);
        Progress.IsIndeterminate = true;

        var expected = TimeSpan.FromSeconds(Math.Max(VideoCombinator.GetOutputSeconds(a, b, transition), 0.1));
        var progress = new Progress<FfmpegProgress>(report =>
        {
            if (!ReferenceEquals(_runCancellation, cancellation) || report.Position is not { } position)
                return;

            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(position / expected, 0, 1);
            StatusText.Text = $"Combining... {Progress.Value:P0}";
        });

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await FfmpegRunner.RunAsync(VideoCombinator.BuildCommand(pathA, pathB, a, b, resolution, transition, outputPath), progress, cancellation.Token);
            Progress.Value = 1;
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Cancelled.", isProblem: false);
            DeleteUnfinished(outputPath);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowStatus($"The videos could not be combined: {ex.Message}", isProblem: true);
            DeleteUnfinished(outputPath);
            return false;
        }
        finally
        {
            _runCancellation = null;
            cancellation.Dispose();
            Progress.IsIndeterminate = false;
            SetRunning(false);
        }
    }

    private static void DeleteUnfinished(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SetRunning(bool running)
    {
        InputPanel.IsEnabled = !running;
        ExportButton.IsEnabled = SendButton.IsEnabled = !running && IsReady;
        CloseButton.Content = running ? "Cancel" : "Close";
        if (!running)
            Progress.Value = 0;
    }

    private void ShowStatus(string text, bool isProblem)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(ForegroundProperty, isProblem ? "WarningTextBrush" : "TextBrush");
    }

    // While a join is running the button is Cancel; otherwise it closes the window.
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
            _runCancellation?.Cancel();
        else
            Close();
    }

    /// <summary>Closing the window in the middle of a join stops it: nothing is left running behind the main window.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _probeCancellation?.Cancel();
        _runCancellation?.Cancel();
        base.OnClosing(e);
    }
}
