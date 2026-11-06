using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Save Clip to Personal Clip Library (a clip's menu, 5.2C): a title (the clip's name to start with) and tags for a clip's span kept in the
/// library (ClipLibrary), with what is kept said above them.
/// </summary>
public partial class SaveToClipLibraryDialog : Window
{
    /// <param name="name">The clip's name: the title to start with.</param>
    /// <param name="what">What is kept, said above the boxes (its length, its focal point).</param>
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

    /// <summary>The title given, once saved.</summary>
    public string ClipTitle => TitleBox.Text.Trim();

    /// <summary>The tags given (each trimmed, none empty), once saved.</summary>
    public IReadOnlyList<string> Tags => [.. TagsBox.Text.Split(',', ';').Select(t => t.Trim()).Where(t => t.Length > 0)];

    private void TitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => SaveButton.IsEnabled = TitleBox.Text.Trim().Length > 0;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
