using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Save Clip to Personal Clip Library (clip context menu): title (defaults to clip name) and tags for the stored span (ClipLibrary),
/// with a summary line above.
/// </summary>
public partial class SaveToClipLibraryDialog : Window
{
    /// <param name="name">Clip name (initial title).</param>
    /// <param name="what">Summary line (duration, focal point).</param>
    public SaveToClipLibraryDialog(string name, string what)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        WhatText.Text = what;
        TitleBox.Text = name;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>Entered title (after save).</summary>
    public string ClipTitle => TitleBox.Text.Trim();

    /// <summary>Entered tags, trimmed and non-empty (after save).</summary>
    public IReadOnlyList<string> Tags => [.. TagsBox.Text.Split(',', ';').Select(t => t.Trim()).Where(t => t.Length > 0)];

    private void TitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => SaveButton.IsEnabled = TitleBox.Text.Trim().Length > 0;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
