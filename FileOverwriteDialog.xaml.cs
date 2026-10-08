using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Asked when the file an encode would write already exists: replace it, pick another name, or back out.
/// </summary>
public partial class FileOverwriteDialog : Window
{
    private readonly string _folder;

    public FileOverwriteDialog(string existingPath)
    {
        InitializeComponent();
        _folder = Path.GetDirectoryName(existingPath) ?? "";

        ExistingPathText.Text = existingPath;
        NewNameBox.Text = SuggestName(existingPath);

        // Ready to type over the name, leaving the extension alone.
        NewNameBox.Focus();
        NewNameBox.Select(0, Path.GetFileNameWithoutExtension(NewNameBox.Text).Length);
    }

    /// <summary>Cancel unless one of the two action buttons was pressed.</summary>
    public OverwriteDecision Decision { get; private set; } = new(OverwriteChoice.Cancel);

    /// <summary>"clip.mp4" becomes "clip (1).mp4", or the first number after that which is free.</summary>
    private static string SuggestName(string existingPath)
    {
        var folder = Path.GetDirectoryName(existingPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(existingPath);
        var extension = Path.GetExtension(existingPath);

        // A name that already carries a number counts on from it: "clip (1)" leads to "clip (2)", not "clip (1) (1)".
        var first = 1;
        var numbered = System.Text.RegularExpressions.Regex.Match(name, @"^(?<stem>.*) \((?<n>\d{1,6})\)$");
        if (numbered.Success)
        {
            name = numbered.Groups["stem"].Value;
            first = int.Parse(numbered.Groups["n"].Value) + 1;
        }

        for (var number = first; ; number++)
        {
            var candidate = $"{name} ({number}){extension}";
            if (!File.Exists(Path.Combine(folder, candidate)))
                return candidate;
        }
    }

    private void NewNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorText is not null)
            ErrorText.Text = "";
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        Decision = new OverwriteDecision(OverwriteChoice.Overwrite);
        DialogResult = true;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorText.Text = "That is not a valid file name. Leave out \\ / : * ? \" < > |";
            return;
        }

        // An extension is what tells FFmpeg which format to write.
        if (Path.GetExtension(name).Length == 0)
            name += Path.GetExtension(ExistingPathText.Text);

        var path = Path.Combine(_folder, name);
        if (File.Exists(path))
        {
            ErrorText.Text = "A file with that name exists too. Choose another, or use Overwrite.";
            return;
        }

        Decision = new OverwriteDecision(OverwriteChoice.Rename, path);
        DialogResult = true;
    }
}
