using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>What was answered when HandPeg was closed with unsaved changes.</summary>
public enum ExitChoice
{
    Cancel,
    SaveAndClose,
    CloseWithoutSaving,
}

/// <summary>Asked when the main window is closed while the open video has changes that were not saved as a project.</summary>
public partial class SaveOnExitDialog : Window
{
    /// <param name="suggestedName">The name the project would be saved under: the video's, or the project's own.</param>
    /// <param name="projectPath">
    /// Pass 5.5A review: the project file the project was opened from or last saved to ("" for work never saved). With one, Save &amp; Close saves
    /// there (as Ctrl+S does) and no name is asked; the name box is only for work never saved.
    /// </param>
    public SaveOnExitDialog(string suggestedName, string projectPath = "")
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        SavesToFile = projectPath.Length > 0;
        NameBox.Text = suggestedName;
        if (SavesToFile)
        {
            NameRow.Visibility = Visibility.Collapsed;
            (SavesToText.Text, SavesToText.Visibility) = ($"Saves to its project file: {Path.GetFileName(projectPath)}", Visibility.Visible);
            SavesToText.ToolTip = projectPath;
            (SaveButton.IsEnabled, NoteText.Text) = (true, "");
        }
        else
        {
            Loaded += (_, _) => NameBox.SelectAll();
        }
    }

    public ExitChoice Choice { get; private set; } = ExitChoice.Cancel;

    public string ProjectName => NameBox.Text.Trim();

    /// <summary>Whether Save &amp; Close saves to the project's own file (Pass 5.5A review): no name is asked then.</summary>
    public bool SavesToFile { get; }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NoteText is null || SavesToFile)
            return;

        SaveButton.IsEnabled = ProjectName.Length > 0;
        var fileName = string.Concat(ProjectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        NoteText.Text = fileName.Length > 0 && File.Exists(ProjectStore.PathOfName(fileName))
            ? "A project with this name exists already and will be replaced."
            : "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.SaveAndClose;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.CloseWithoutSaving;
        DialogResult = true;
    }
}
