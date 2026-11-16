using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp.Controls;

/// <summary>
/// A failure shown in a dialog's or pane's text line and written to HandPeg.log with its exception in one call: the line is
/// replaced seconds later, the log keeps the time, the operation, the path and the exception type for triage.
/// The view model's status bar uses <c>MainViewModel.Fail</c> for the same purpose.
/// </summary>
public static class UiFault
{
    /// <summary>Shows <paramref name="userText"/> in <paramref name="target"/> and logs <paramref name="ex"/> under <paramref name="logContext"/>.</summary>
    /// <returns>The text shown.</returns>
    public static string Show(TextBlock target, string userText, string logContext, Exception ex)
    {
        AppLog.Write(logContext, ex);
        target.Text = userText;
        return userText;
    }

    /// <summary>
    /// <see cref="Show"/> for a failure that recurs on every refresh (a folder on an unplugged drive): shown each time, logged once per
    /// <paramref name="key"/> for the life of the process.
    /// </summary>
    public static string ShowOnce(TextBlock target, string key, string userText, string logContext, Exception ex)
    {
        AppLog.WriteOnce(key, logContext, ex);
        target.Text = userText;
        return userText;
    }
}
