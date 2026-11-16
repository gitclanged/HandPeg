using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Answer to the unsaved-changes prompt on exit.</summary>
public enum ExitChoice
{
    Cancel,
    SaveAndClose,
    CloseWithoutSaving,
}

/// <summary>Prompt when closing the main window with unsaved project changes.</summary>
public partial class SaveOnExitDialog : Window
{
    /// <param name="suggestedName">Default save name (the video's or the project's).</param>
    /// <param name="projectPath">
    /// Project file opened from or last saved to ("" if never saved). When set, Save &amp; Close saves there like Ctrl+S without asking
    /// for a name; the name box appears only for never-saved work.
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

    /// <summary>Whether Save &amp; Close writes to the existing project file (no name prompt).</summary>
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
