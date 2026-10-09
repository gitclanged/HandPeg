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
    public SaveOnExitDialog(string suggestedName)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        NameBox.Text = suggestedName;
        Loaded += (_, _) => NameBox.SelectAll();
    }

    public ExitChoice Choice { get; private set; } = ExitChoice.Cancel;

    public string ProjectName => NameBox.Text.Trim();

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NoteText is null)
            return;

        SaveButton.IsEnabled = ProjectName.Length > 0;
        var fileName = string.Concat(ProjectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        NoteText.Text = fileName.Length > 0 && File.Exists(Path.Combine(ProjectStore.Folder, fileName + ".txt"))
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
