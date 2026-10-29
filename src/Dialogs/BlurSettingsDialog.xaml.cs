using System.Windows;

namespace HandPegApp;

/// <summary>
/// The blur and dimming of the Frame &amp; Layer Engine's background. Its DataContext is the main view model.
/// </summary>
public partial class BlurSettingsDialog : Window
{
    public BlurSettingsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);

        // Ctrl+Z and Ctrl+Y take back and redo what is changed here, a press at a time (4.3E).
        _ = new BoundUndo(this);
    }
}
