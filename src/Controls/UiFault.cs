using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp.Controls;

/// <summary>
/// Reports a failure in one call: sets a dialog or pane status line and writes the exception to HandPeg.log (timestamp, operation
/// context and exception type for triage). The status line is transient by convention (callers overwrite it).
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
    /// <see cref="Show"/> variant for failures that recur on every refresh (e.g. a folder on an unplugged drive): displayed every time,
    /// logged once per <paramref name="key"/> per process lifetime.
    /// </summary>
    public static string ShowOnce(TextBlock target, string key, string userText, string logContext, Exception ex)
    {
        AppLog.WriteOnce(key, logContext, ex);
        target.Text = userText;
        return userText;
    }
}
