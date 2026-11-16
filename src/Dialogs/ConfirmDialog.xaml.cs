using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Themed confirmation dialog (instead of a Windows message box) shown before a possibly unintended action.</summary>
public partial class ConfirmDialog : Window
{
    /// <param name="confirm">Confirm button label.</param>
    /// <param name="other">Optional second confirm option label; null for a plain yes/no.</param>
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

    /// <summary>Whether the second confirm option was chosen.</summary>
    public bool ChoseOther { get; private set; }

    /// <summary>Makes Cancel the default (Enter) button for destructive warnings, requiring a deliberate click to proceed.</summary>
    public bool DefaultsToCancel
    {
        get => CancelButton.IsDefault;
        init => (ConfirmButton.IsDefault, CancelButton.IsDefault) = (!value, value);
    }

    /// <summary>
    /// Optional "Remember my choice" checkbox label (null = hidden). <see cref="Remember"/> reports its state for any closing button.
    /// </summary>
    public string? RememberText
    {
        get => RememberBox.Content as string;
        init => (RememberBox.Content, RememberBox.Visibility) = (value, value is null ? Visibility.Collapsed : Visibility.Visible);
    }

    /// <summary>Whether "Remember my choice" was checked (<see cref="RememberText"/>).</summary>
    public bool Remember => RememberBox.IsChecked == true;

    /// <summary>Cancel button label ("Cancel" by default, e.g. "Don't Copy").</summary>
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
