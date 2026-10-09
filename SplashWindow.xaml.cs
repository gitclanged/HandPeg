using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Services;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>What the launch window was asked to open.</summary>
public enum LaunchKind
{
    Video,
    Project,
}

/// <param name="Path">The video, or the project file.</param>
/// <param name="StylePath">The style preset to give the video, or null to open it as it is.</param>
public sealed record LaunchRequest(LaunchKind Kind, string Path, string? StylePath = null);

/// <summary>A style preset as the launch window and the settings list it: a file in the Styles folder.</summary>
public sealed record StyleFile(string Path)
{
    public const string Extension = ".hpstyle";

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The style presets in the Styles folder, most recently saved first.</summary>
    public static List<StyleFile> All()
    {
        try
        {
            return Directory.Exists(AppPaths.Styles)
                ? new DirectoryInfo(AppPaths.Styles).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTime).Select(f => new StyleFile(f.FullName)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The ones the launch window shows: those picked in the settings, or failing that the most recent, up to the number set.</summary>
    public static List<StyleFile> ForLaunch()
    {
        var (all, settings) = (All(), AppSettings.Current);
        var picked = all.Where(s => settings.SplashStylePresets.Contains(s.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
        return (picked.Count > 0 ? picked : all).Take(Math.Clamp(settings.SplashPresetCount, 0, 5)).ToList();
    }
}

/// <summary>
/// The launch window, shown over the main window as it starts in Editor Mode, when it is switched on in the
/// settings: a place to drop a video, a way back into a recent project, and style presets as drop targets.
/// It only records what was asked for; the main window, which owns it, does the opening once it has closed.
/// </summary>
public partial class SplashWindow : Window
{
    private const string VideoFilter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.mp3;*.m4a;*.flac;*.wav|All files|*.*";

    public SplashWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var styles = StyleFile.ForLaunch();
        StyleList.ItemsSource = styles;
        (StylePanel.Visibility, NoStylesText.Visibility) = styles.Count > 0 ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Collapsed, Visibility.Visible);

        var recent = ProjectStore.ListRecent().Take(12).ToList();
        RecentBox.ItemsSource = recent;
        RecentBox.SelectedIndex = recent.Count > 0 ? 0 : -1;
        RecentButton.IsEnabled = RecentBox.IsEnabled = recent.Count > 0;
        if (recent.Count == 0)
            RecentButton.ToolTip = "There are no saved projects yet. Save one with the Project button in the main window.";
    }

    /// <summary>What to open once this window has closed; null for nothing (a blank project, or the window was simply closed).</summary>
    public LaunchRequest? Request { get; private set; }

    private void Finish(LaunchRequest? request)
    {
        Request = request;
        Close();
    }

    private void NewBlank_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void LoadRecent_Click(object sender, RoutedEventArgs e)
    {
        if (RecentBox.SelectedItem is ProjectEntry project)
            Finish(new LaunchRequest(LaunchKind.Project, project.FilePath));
    }

    // ----- Dropping -----

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]) ? files[0] : null;

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // The target under the pointer lights up, so it is clear which style a drop would use.
    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is UIElement target && e.Data.GetDataPresent(DataFormats.FileDrop))
            target.Opacity = 0.65;
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.Opacity = 1;
        e.Handled = true;
        if (DroppedFile(e) is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file));
    }

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Browse() is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file));
    }

    private void Style_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: StyleFile style } button)
            return;

        button.Opacity = 1;
        if (DroppedFile(e) is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file, style.Path));
    }

    private void Style_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StyleFile style } && Browse() is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file, style.Path));
    }

    private string? Browse()
    {
        var dialog = new OpenFileDialog { Title = "Select a video", Filter = VideoFilter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
