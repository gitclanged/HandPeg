using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>What to do with the video that is open when another one is about to be loaded over it.</summary>
public enum ReplaceVideoChoice
{
    Cancel,
    SaveAndLoad,
    Discard,
}

/// <summary>
/// Asked when a video arrives (dropped on the window, or sent from the Video Combinator) while another is
/// open: save the open one as a project first, load without saving, or leave things as they are.
/// </summary>
public partial class ReplaceVideoDialog : Window
{
    /// <param name="currentSource">The video that is open, which gives the project its suggested name.</param>
    /// <param name="newVideo">The video about to be loaded.</param>
    public ReplaceVideoDialog(string currentSource, string newVideo)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        IntroText.Text = $"Loading {Path.GetFileName(newVideo)} replaces it, with its cuts and settings. "
                         + "Save them as a project first to come back to them later, or load the new video and discard them.";
        NameBox.Text = SuggestName(currentSource);
        Loaded += (_, _) => NameBox.SelectAll();
    }

    public ReplaceVideoChoice Choice { get; private set; } = ReplaceVideoChoice.Cancel;

    /// <summary>The name to save the project under, when that was chosen.</summary>
    public string ProjectName => NameBox.Text.Trim();

    private static string SuggestName(string source)
    {
        try
        {
            // A web address has no file name worth using.
            var name = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? "" : Path.GetFileNameWithoutExtension(source);
            return name.Length > 0 ? name : $"Project {DateTime.Now:yyyy-MM-dd HH.mm}";
        }
        catch (ArgumentException)
        {
            return $"Project {DateTime.Now:yyyy-MM-dd HH.mm}";
        }
    }

    // A project saved under a name that is taken replaces the one that has it; better said before than found out after.
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NoteText is null)
            return;

        var name = ProjectName;
        SaveButton.IsEnabled = name.Length > 0;
        var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        NoteText.Text = fileName.Length > 0 && File.Exists(Path.Combine(ProjectStore.Folder, fileName + ".txt"))
            ? "A project with this name exists already and will be replaced."
            : "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Choice = ReplaceVideoChoice.SaveAndLoad;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Choice = ReplaceVideoChoice.Discard;
        DialogResult = true;
    }
}
