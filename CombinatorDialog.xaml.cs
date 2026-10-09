using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>One line of the Video Combinator's list: a video, its place in the order, and what is known about it.</summary>
public sealed partial class CombinatorItem : ObservableObject
{
    public required string Path { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>What ffprobe said; null until it has, or when the file is not a video that can be read.</summary>
    public MediaInfo? Info { get; set; }

    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _details = "Looking at the file...";

    public bool IsUsable => Info?.Video is not null;
}

/// <summary>
/// The Video Combinator: a list of up to five videos, a frame size and a transition, joined with FFmpeg either
/// into a file the user names or into a temporary one that is then loaded into the editor.
/// </summary>
public partial class CombinatorDialog : Window
{
    private const string VideoFilter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg|All files|*.*";

    private readonly ObservableCollection<CombinatorItem> _items = [];
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _runCancellation;

    public CombinatorDialog()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;
        ResolutionBox.ItemsSource = VideoCombinator.Resolutions;
        ResolutionBox.SelectedIndex = 0;
        TransitionBox.ItemsSource = VideoCombinator.Transitions;
        TransitionBox.SelectedIndex = 0;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Refresh();
    }

    /// <summary>Set when Send to Editor has finished: the joined video, for the main window to load.</summary>
    public string? SendToEditorPath { get; private set; }

    private bool IsRunning => _runCancellation is not null;

    private bool IsReady => _items.Count >= 2 && _items.All(i => i.IsUsable);

    // ----- The list -----

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select the videos to combine", Filter = VideoFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            AddFiles(dialog.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !IsRunning ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!IsRunning && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddFiles(files.Where(File.Exists).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Adds files to the end of the list, as far as there is room, and has each one looked at.</summary>
    private void AddFiles(IEnumerable<string> files)
    {
        var left = 0;
        foreach (var file in files)
        {
            if (_items.Count >= VideoCombinator.MaxInputs)
            {
                left++;
                continue;
            }

            var item = new CombinatorItem { Path = file };
            _items.Add(item);
            _ = InspectAsync(item);
        }

        Refresh();
        if (left > 0)
            ShowStatus($"The list holds {VideoCombinator.MaxInputs} videos: {left} more {(left == 1 ? "was" : "were")} left out.", isProblem: true);
        else if (!IsRunning)
            StatusText.Text = "";
    }

    private async Task InspectAsync(CombinatorItem item)
    {
        try
        {
            item.Info = File.Exists(item.Path) ? await MediaProbe.ProbeAsync(item.Path, _closing.Token) : null;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        item.Details = !File.Exists(item.Path) ? "File not found."
            : item.Info?.Video is not { } video
                ? (File.Exists(DependencyUpdater.FfprobePath) ? "Not a video that can be read." : "FFmpeg is not installed: install it from Settings first.")
            : $"{video.Width} x {video.Height}, {video.FrameRate:0.##} fps, {Models.TimeDisplay.Format(item.Info.DurationSeconds)}, "
              + item.Info.Audio.Count switch { 0 => "no sound", 1 => "1 audio track", var count => $"{count} audio tracks" };
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not CombinatorItem item)
            return;

        var index = _items.IndexOf(item);
        _items.Remove(item);
        FileList.SelectedIndex = Math.Min(index, _items.Count - 1);
        Refresh();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int by)
    {
        var index = FileList.SelectedIndex;
        if (index < 0 || index + by < 0 || index + by >= _items.Count)
            return;

        _items.Move(index, index + by);
        FileList.SelectedIndex = index + by;
        Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        Refresh();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Choice_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private List<CombinatorInput> Inputs => _items.Where(i => i.IsUsable).Select(i => new CombinatorInput(i.Path, i.Info!)).ToList();

    /// <summary>Numbers the list, says what will come out, and sets which buttons can be pressed.</summary>
    private void Refresh()
    {
        // Raised while the window is still being built.
        if (SummaryText is null || ResolutionBox.SelectedItem is not string resolution || TransitionBox.SelectedItem is not string transition)
            return;

        for (var i = 0; i < _items.Count; i++)
            _items[i].Number = $"{i + 1}.";

        var selected = FileList.SelectedIndex;
        AddButton.IsEnabled = _items.Count < VideoCombinator.MaxInputs;
        RemoveButton.IsEnabled = selected >= 0;
        UpButton.IsEnabled = selected > 0;
        DownButton.IsEnabled = selected >= 0 && selected < _items.Count - 1;
        ClearButton.IsEnabled = _items.Count > 0;
        if (!IsRunning)
            ExportButton.IsEnabled = SendButton.IsEnabled = IsReady;

        if (_items.Count < 2)
        {
            SummaryText.Text = $"Add at least two videos (up to {VideoCombinator.MaxInputs}): with Add Videos, or by dropping files on the list.";
            return;
        }

        if (!IsReady)
        {
            SummaryText.Text = _items.Any(i => i.Info is null && i.Details.StartsWith("Looking", StringComparison.Ordinal))
                ? "Looking at the files..."
                : "One of the files cannot be used: remove it to carry on.";
            return;
        }

        var inputs = Inputs;
        var (width, height) = VideoCombinator.GetFrameSize(resolution, inputs);
        var tracks = VideoCombinator.GetAudioTrackCount(inputs);
        var note = transition != VideoCombinator.HardSplice && !VideoCombinator.UsesTransition(inputs, transition)
            ? " One of the videos is too short for one-second transitions, so they will be spliced."
            : "";
        SummaryText.Text = $"Result: {inputs.Count} videos, {width} x {height}, {Models.TimeDisplay.Format(VideoCombinator.GetOutputSeconds(inputs, transition))} long, "
                           + tracks switch { 0 => "no sound", 1 => "1 audio track", _ => $"{tracks} audio tracks" } + $".{note}";
    }

    // ----- Joining -----

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!IsReady)
            return;

        var first = _items[0].Path;
        var dialog = new SaveFileDialog
        {
            Title = "Save the combined video as",
            Filter = "MP4|*.mp4",
            DefaultExt = "mp4",
            FileName = $"{Path.GetFileNameWithoutExtension(first)}_combined.mp4",
            InitialDirectory = Path.GetDirectoryName(first) ?? "",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (await RunAsync(dialog.FileName))
            StatusText.Text = $"Saved {dialog.FileName}";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        // In the session's folder: a working file, which goes when HandPeg closes.
        var output = Path.Combine(SessionPaths.Root, "combined", $"combined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        if (!await RunAsync(output))
            return;

        SendToEditorPath = output;
        DialogResult = true;
    }

    /// <summary>Runs the join. Returns whether the file was made; what went wrong otherwise is shown in the dialog.</summary>
    private async Task<bool> RunAsync(string outputPath)
    {
        if (IsRunning || !IsReady || ResolutionBox.SelectedItem is not string resolution || TransitionBox.SelectedItem is not string transition)
            return false;

        var inputs = Inputs;
        if (inputs.Any(i => string.Equals(Path.GetFullPath(i.Path), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus("The result cannot be saved over one of the videos it is made from.", isProblem: true);
            return false;
        }

        var cancellation = _runCancellation = new CancellationTokenSource();
        SetRunning(true);
        ShowStatus("Starting FFmpeg...", isProblem: false);
        Progress.IsIndeterminate = true;

        var expected = TimeSpan.FromSeconds(Math.Max(VideoCombinator.GetOutputSeconds(inputs, transition), 0.1));
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
            await FfmpegRunner.RunAsync(VideoCombinator.BuildCommand(inputs, resolution, transition, outputPath), progress, cancellation.Token);
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
        CloseButton.Content = running ? "Cancel" : "Close";
        if (!running)
            Progress.Value = 0;
        ExportButton.IsEnabled = SendButton.IsEnabled = !running && IsReady;
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
        _closing.Cancel();
        _runCancellation?.Cancel();
        base.OnClosing(e);
    }
}
