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
    /// <param name="other">A second way of going ahead, for a question with two; null for a plain yes or no.</param>
    public ConfirmDialog(string title, string message, string confirm, string? other = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirm;
        if (other is not null)
            (OtherButton.Content, OtherButton.Visibility, Width) = (other, Visibility.Visible, Math.Max(Width, 560));
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Whether the second way of going ahead was the one chosen.</summary>
    public bool ChoseOther { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Other_Click(object sender, RoutedEventArgs e)
    {
        ChoseOther = true;
        DialogResult = true;
    }
}
