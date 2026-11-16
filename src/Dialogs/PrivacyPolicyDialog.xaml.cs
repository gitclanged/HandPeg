using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Plain-language crash report contents and exclusions, with the "Automatically save and send anonymous crash reports" switch
/// (persisted on change) and a link to the reports folder. Opened from Privacy Policy links in first run and Settings (General,
/// About HandPeg, Diagnostics).
/// </summary>
public partial class PrivacyPolicyDialog : Window
{
    private readonly Action<bool>? _changed;
    private readonly bool _ready;

    private PrivacyPolicyDialog(bool sendCrashReports, Action<bool>? changed)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        FolderText.Text = "Crash reports folder: " + AppPaths.Crashes;
        FolderText.ToolTip = AppPaths.Crashes;
        SendBox.IsChecked = sendCrashReports;
        (_changed, _ready) = (changed, true);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>
    /// Shows the policy modally over the caller until Close/Escape. The switch starts at <paramref name="sendCrashReports"/> (caller's
    /// unsaved state); changes are saved immediately and passed to <paramref name="changed"/> so the caller's checkbox syncs.
    /// </summary>
    public static void ShowFor(Window owner, bool sendCrashReports, Action<bool>? changed = null) =>
        new PrivacyPolicyDialog(sendCrashReports, changed) { Owner = owner }.ShowDialog();

    private void SendBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        var on = SendBox.IsChecked == true;
        AppSettings.Update(settings => settings.SendCrashReports = on);

        CrashReporter.Note("settings", on ? "crash reports on (privacy policy)" : "crash reports off (privacy policy)");
        CrashReporter.FollowSetting();
        _changed?.Invoke(on);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenCrashReportsFolder())
            FolderText.Text = "The crash reports folder could not be opened: " + AppPaths.Crashes;
    }

    /// <summary>Opens the crash reports folder in Explorer, creating it if needed; false on failure.</summary>
    public static bool OpenCrashReportsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Crashes);
            ShellOpen.OpenFolder(AppPaths.Crashes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("The crash reports folder could not be opened", ex);
            return false;
        }
    }
}
