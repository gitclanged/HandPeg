using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// A saved project as the Project Manager lists it (4.1.1-B): its entry, and the picture of its main video, which the
/// pointer scrubs through while it is across it.
/// </summary>
public sealed partial class ProjectRow(ProjectEntry entry) : ObservableObject
{
    public ProjectEntry Entry { get; } = entry;

    /// <summary>The project's name: what the row is called to screen readers and UI Automation (4.2B-3), not the type's name.</summary>
    public string Name => Entry.Name;

    public override string ToString() => Name;

    /// <summary>What the picture shows: the poster, or while the pointer is across it, the frame there.</summary>
    [ObservableProperty] private ImageSource? _picture;

    [ObservableProperty] private Visibility _pictureVisibility = Visibility.Collapsed;

    public BitmapSource? Poster { get; set; }

    /// <summary>The media browser's strip of the main video's frames (its cache's file), and the video's shape.</summary>
    public string? Strip { get; set; }

    public double Shape { get; set; } = 16.0 / 9;

    /// <summary>The strip's frames, decoded while the pointer is across the picture and let go when it leaves.</summary>
    public IReadOnlyList<BitmapSource>? Frames { get; set; }

    public bool DecodingFrames { get; set; }
}

/// <summary>
/// Saves the current session as a project, starts a new one, and lists the saved ones: since 4.1.1-B with a 16:9
/// picture of each project's main video (the media browser's poster and strip, from its cache, made in the background
/// when not kept), scrubbed by the pointer, while Settings' Show video &amp; hover previews in Project Manager is on.
/// </summary>
public partial class ProjectManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();

    // How tall the pictures are decoded: the list's picture is 72 pixels tall, at up to twice that on a high-DPI screen.
    private const int PictureHeight = 144;

    public ProjectManagerWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        if (viewModel.HasSource)
            NameBox.Text = Path.GetFileNameWithoutExtension(viewModel.LocalMediaPath);

        PreviewsBox.IsChecked = AppSettings.Current.ShowProjectPreviews;
        RefreshList();

        // Projects imported while the list is showing appear in it at once.
        ProjectStore.Changed += RefreshList;
        Closed += (_, _) =>
        {
            ProjectStore.Changed -= RefreshList;
            _closing.Cancel();
        };
    }

    /// <summary>The project the user chose to open, for the main window to load once this one has closed.</summary>
    public string? ProjectToLoad { get; private set; }

    /// <summary>+ New Project was chosen: the main window empties the session (asking about unsaved changes first).</summary>
    public bool NewProjectRequested { get; private set; }

    // The listing asked for last: one that finishes after a newer one was asked for is dropped.
    private int _listing;

    private void RefreshList()
    {
        // Asked from elsewhere (projects imported: ProjectStore.Changed): listed on the window's own thread.
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshList);
            return;
        }

        _ = RefreshListAsync();
    }

    /// <summary>
    /// Lists the saved projects. Each project file is read whole to say what it holds, which for a folder of them took
    /// the interface's thread far past a frame as the window opened (4.3E): they are read on the thread pool, and the list
    /// is filled once they are.
    /// </summary>
    private async Task RefreshListAsync()
    {
        var listing = ++_listing;
        List<ProjectEntry> entries;
        try
        {
            entries = await Task.Run(() => ProjectStore.ListRecent());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries = [];
        }

        if (listing != _listing || _closing.IsCancellationRequested)
            return;

        // With previews on, every row has its picture's place from the start (dark until its picture comes, and dark for a
        // source that has none, such as a link): the rows are one height and do not grow as their pictures arrive.
        var previews = AppSettings.Current.ShowProjectPreviews ? Visibility.Visible : Visibility.Collapsed;
        var rows = entries.Select(e => new ProjectRow(e) { PictureVisibility = previews }).ToList();
        ProjectList.ItemsSource = rows;
        if (rows.Count == 0)
            MessageText.Text = "No projects saved yet.";
        if (AppSettings.Current.ShowProjectPreviews)
            _ = LoadPicturesAsync(rows);
    }

    // ----- The pictures -----

    /// <summary>
    /// Each project's picture: from the media browser's cache when it is kept there (read off the interface), otherwise
    /// made in the background (one piece of work for them all), at the browser's own strip settings so that the browser
    /// and this list share one strip per video.
    /// </summary>
    private async Task LoadPicturesAsync(IReadOnlyList<ProjectRow> rows)
    {
        var token = _closing.Token;
        var settings = AppSettings.Current;
        var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
        var missing = new List<ProjectRow>();
        try
        {
            foreach (var row in rows)
            {
                var path = row.Entry.SourcePath;
                var kept = await Task.Run(() => FileState.Exists(path) ? MediaBrowserFrames.Kept(path, frames, frameHeight, PictureHeight, 0.3) : null, token);
                if (kept is { } found)
                    Show(row, found.Strip, (double)found.Width / Math.Max(found.Height, 1), found.Poster);
                else if (await Task.Run(() => FileState.Exists(path), token))
                    missing.Add(row);
            }

            if (missing.Count == 0)
                return;

            await WorkScheduler.Run(new WorkSpec("Project previews", WorkLane.Background) { Key = "project-previews" }, async work =>
            {
                foreach (var row in missing)
                {
                    work.Token.ThrowIfCancellationRequested();
                    var path = row.Entry.SourcePath;
                    if (await MediaBrowserFrames.DescribeAsync(path, work.Token) is not { Width: > 0, Height: > 0 } video)
                        continue;

                    var strip = await MediaBrowserFrames.StripAsync(path, video.Seconds, video.Width, video.Height, frames, frameHeight, work.Token);
                    if (strip is null)
                        continue;

                    var shape = (double)video.Width / video.Height;
                    var poster = await Task.Run(() => MediaBrowserFrames.Poster(strip, shape, PictureHeight, 0.3), work.Token);
                    await Dispatcher.InvokeAsync(() => Show(row, strip, shape, poster));
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // The window was closed: the rest are made another time.
        }
    }

    private void Show(ProjectRow row, string strip, double shape, BitmapSource? poster)
    {
        (row.Strip, row.Shape, row.Poster) = (strip, shape, poster);
        if (poster is null || !AppSettings.Current.ShowProjectPreviews)
            return;

        (row.Picture, row.PictureVisibility) = (poster, Visibility.Visible);
    }

    // The pointer across a picture: the frame at that point of the video (the strip decoded once, on the way in).
    private async void Picture_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectRow row } picture || row.Strip is not { } strip)
            return;

        if (row.Frames is null)
        {
            if (row.DecodingFrames)
                return;

            row.DecodingFrames = true;
            var shape = row.Shape;
            row.Frames = await Task.Run(() => MediaBrowserFrames.Frames(strip, shape, PictureHeight));
            row.DecodingFrames = false;
            if (!picture.IsMouseOver)
            {
                row.Frames = null;
                return;
            }
        }

        if (row.Frames is { Count: > 0 } frames)
        {
            var at = Math.Clamp(e.GetPosition(picture).X / Math.Max(picture.ActualWidth, 1), 0, 0.999);
            row.Picture = frames[(int)(at * frames.Count)];
        }
    }

    private void Picture_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectRow row })
            (row.Picture, row.Frames) = (row.Poster, null);
    }

    private void PreviewsBox_Changed(object sender, RoutedEventArgs e)
    {
        var on = PreviewsBox.IsChecked == true;
        if (on == AppSettings.Current.ShowProjectPreviews)
            return;

        AppSettings.Current.ShowProjectPreviews = on;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not written: the choice holds for this session.
        }

        RefreshList();
    }

    // ----- Saving, starting anew, loading, deleting -----

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            MessageText.Text = "Load a video first: a project is a video with its cuts and settings.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageText.Text = "Type a name for the project.";
            return;
        }

        MessageText.Text = _viewModel.SaveProject(NameBox.Text);
        RefreshList();
    }

    /// <summary>Asked for by Save as Bundle: the window closes and the main window asks where (Save As with the bundle type).</summary>
    public bool BundleRequested { get; private set; }

    private void SaveBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            MessageText.Text = "Load a video first: a bundle is a project with the files it uses.";
            return;
        }

        BundleRequested = true;
        DialogResult = true;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a Project or Bundle",
            Filter = $"HandPeg projects and bundles|*{ProjectStore.Extension};*{ProjectBundle.Extension}|All files|*.*",
            InitialDirectory = Directory.Exists(ProjectStore.Folder) ? ProjectStore.Folder : "",
        };
        if (dialog.ShowDialog(this) != true || !ProjectStore.IsProjectFile(dialog.FileName))
            return;

        ProjectToLoad = dialog.FileName;
        DialogResult = true;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        NewProjectRequested = true;
        DialogResult = true;
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadButton.IsEnabled = DeleteButton.IsEnabled = ProjectList.SelectedItem is ProjectRow;

    // On the thread pool (4.3E): making the folder and starting Explorer took the interface's thread for tens of milliseconds.
    private async void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        var folder = ProjectStore.Folder;
        var problem = await Task.Run<string?>(() =>
        {
            try
            {
                // The folder only comes into being with the first saved project.
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"")?.Dispose();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return ex.Message;
            }
        });

        if (problem is not null)
            MessageText.Text = $"Could not open the folder: {problem}";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectRow { Entry: var entry })
            return;

        var answer = MessageBox.Show(this,
            $"Delete the project \"{entry.Name}\"?\n\nThe project file is removed from disk. The video it refers to is not touched.",
            "HandPeg", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Could not delete the project: {ex.Message}";
            return;
        }

        // Gone from the list at once (the list read afresh comes a moment later, from the thread pool).
        var left = ProjectList.Items.OfType<ProjectRow>().Where(r => !ReferenceEquals(r.Entry, entry)).ToList();
        ProjectList.ItemsSource = left;
        ProjectList.SelectedItem = null;
        RefreshList();
        MessageText.Text = left.Count == 0 ? "No projects saved yet." : $"Deleted project \"{entry.Name}\".";
    }

    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void Load_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (ProjectList.SelectedItem is not ProjectRow { Entry: var entry })
            return;

        // While something is encoding, the project is not loaded into the window: it goes into the
        // queue as a new job instead, without disturbing what is running.
        ProjectToLoad = entry.FilePath;
        DialogResult = true;
    }
}
