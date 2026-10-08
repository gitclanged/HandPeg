using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Saves the current session as a project and lists the saved ones.
/// </summary>
public partial class ProjectManagerWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ProjectManagerWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        if (viewModel.HasSource)
            NameBox.Text = Path.GetFileNameWithoutExtension(viewModel.LocalMediaPath);

        RefreshList();

        // Projects imported while the list is showing appear in it at once.
        ProjectStore.Changed += RefreshList;
        Closed += (_, _) => ProjectStore.Changed -= RefreshList;
    }

    /// <summary>The project the user chose to open, for the main window to load once this one has closed.</summary>
    public string? ProjectToLoad { get; private set; }

    private void RefreshList()
    {
        ProjectList.ItemsSource = ProjectStore.ListRecent();
        if (ProjectList.Items.Count == 0)
            MessageText.Text = "No projects saved yet.";
    }

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

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadButton.IsEnabled = DeleteButton.IsEnabled = ProjectList.SelectedItem is ProjectEntry;

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The folder only comes into being with the first saved project.
            Directory.CreateDirectory(ProjectStore.Folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{ProjectStore.Folder}\"")?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageText.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectEntry entry)
            return;

        var answer = MessageBox.Show(this,
            $"Delete the project \"{entry.Name}\"?\n\nThe project file is removed from disk. The video it refers to is not touched.",
            "Handpeg", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
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

        RefreshList();
        MessageText.Text = ProjectList.Items.Count == 0 ? "No projects saved yet." : $"Deleted project \"{entry.Name}\".";
    }

    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void Load_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (ProjectList.SelectedItem is not ProjectEntry entry)
            return;

        // While something is encoding, the project is not loaded into the window: it goes into the
        // queue as a new job instead, without disturbing what is running.
        ProjectToLoad = entry.FilePath;
        DialogResult = true;
    }
}
