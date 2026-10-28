using System.IO;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// What a crash report holds and what it never does, said plainly, with the switch itself ("Automatically save and send
/// anonymous crash reports", saved as soon as it is changed) and the way to the folder the reports are kept in.
/// Opened from the Privacy Policy links in the first-run window and in Settings (General, About HandPeg and Diagnostics).
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
    /// Shows the policy over the window that asked for it, until it is closed (Close or Escape). Its box starts as
    /// <paramref name="sendCrashReports"/> (the asking window's own, unsaved, state); a change is saved at once and
    /// handed to <paramref name="changed"/>, so that window's box shows it too.
    /// </summary>
    public static void ShowFor(Window owner, bool sendCrashReports, Action<bool>? changed = null) =>
        new PrivacyPolicyDialog(sendCrashReports, changed) { Owner = owner }.ShowDialog();

    private void SendBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        var on = SendBox.IsChecked == true;
        AppSettings.Current.SendCrashReports = on;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The crash report setting could not be saved", ex);
        }

        CrashReporter.Note("settings", on ? "crash reports on (privacy policy)" : "crash reports off (privacy policy)");
        CrashReporter.FollowSetting();
        _changed?.Invoke(on);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenCrashReportsFolder())
            FolderText.Text = "The crash reports folder could not be opened: " + AppPaths.Crashes;
    }

    /// <summary>Opens the crash reports folder in Explorer, making it first if no report was ever written. False when it could not be.</summary>
    public static bool OpenCrashReportsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Crashes);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.Crashes}\"")?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("The crash reports folder could not be opened", ex);
            return false;
        }
    }
}
