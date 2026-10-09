using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// A question with two answers, in HandPeg's own window rather than a Windows message box: something is about
/// to happen that the user may not have meant, and this says what, once, before it does.
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <param name="confirm">What the button that goes ahead says.</param>
    public ConfirmDialog(string title, string message, string confirm)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirm;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
