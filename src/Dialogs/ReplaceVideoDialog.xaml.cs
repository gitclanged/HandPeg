using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Action for the open video when another is about to replace it.</summary>
public enum ReplaceVideoChoice
{
    Cancel,
    SaveAndLoad,
    Discard,
}

/// <summary>
/// Prompt when a video arrives (window drop or media browser) while another is open: save as project first, load without saving, or
/// cancel.
/// </summary>
public partial class ReplaceVideoDialog : Window
{
    /// <param name="currentSource">Open video (source of the suggested project name).</param>
    /// <param name="newVideo">Incoming video.</param>
    public ReplaceVideoDialog(string currentSource, string newVideo)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var name = Uri.TryCreate(newVideo, UriKind.Absolute, out var link) && link.Scheme is "http" or "https" ? $"the video at {link.Host}" : Path.GetFileName(newVideo);
        IntroText.Text = $"Loading {name} replaces it, with its cuts and settings. "
                         + "Save them as a project first to come back to them later, or load the new video and discard them.";
        NameBox.Text = SuggestName(currentSource);
        Loaded += (_, _) => NameBox.SelectAll();
    }

    public ReplaceVideoChoice Choice { get; private set; } = ReplaceVideoChoice.Cancel;

    /// <summary>Project name when saving was chosen.</summary>
    public string ProjectName => NameBox.Text.Trim();

    private static string SuggestName(string source)
    {
        try
        {
            // URLs yield no usable file name.
            var name = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? "" : Path.GetFileNameWithoutExtension(source);
            return name.Length > 0 ? name : $"Project {DateTime.Now:yyyy-MM-dd HH.mm}";
        }
        catch (ArgumentException)
        {
            return $"Project {DateTime.Now:yyyy-MM-dd HH.mm}";
        }
    }

    // Warn up front when the name is taken, since saving would overwrite that project.
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NoteText is null)
            return;

        var name = ProjectName;
        SaveButton.IsEnabled = name.Length > 0;
        var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        NoteText.Text = fileName.Length > 0 && File.Exists(ProjectStore.PathOfName(fileName))
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
