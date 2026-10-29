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

    /// <summary>
    /// Makes Cancel the button Enter presses: for a warning, where going ahead should take a deliberate click and a
    /// key pressed out of habit should change nothing.
    /// </summary>
    public bool DefaultsToCancel
    {
        get => CancelButton.IsDefault;
        init => (ConfirmButton.IsDefault, CancelButton.IsDefault) = (!value, value);
    }

    /// <summary>
    /// The words of a "Remember my choice" box under the message, for a question that can be answered once for all
    /// (4.4); null, the default, shows none. Whatever button closes the dialog, <see cref="Remember"/> says whether it was ticked.
    /// </summary>
    public string? RememberText
    {
        get => RememberBox.Content as string;
        init => (RememberBox.Content, RememberBox.Visibility) = (value, value is null ? Visibility.Collapsed : Visibility.Visible);
    }

    /// <summary>Whether "Remember my choice" was ticked (see <see cref="RememberText"/>).</summary>
    public bool Remember => RememberBox.IsChecked == true;

    /// <summary>What the button that calls it off says: "Cancel" unless given ("Don't Copy").</summary>
    public string CancelText
    {
        get => CancelButton.Content as string ?? "";
        init => CancelButton.Content = value;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Other_Click(object sender, RoutedEventArgs e)
    {
        ChoseOther = true;
        DialogResult = true;
    }
}
