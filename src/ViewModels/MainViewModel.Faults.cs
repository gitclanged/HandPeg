using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- User-facing failures: shown and logged in one call -----
//
// Status-bar failures are also written to HandPeg.log with the exception, preserving time, operation, path and exception type for
// triage after the status line is replaced. Expected failures (I/O, access, JSON) log type and message; JsonException messages carry
// line and byte position.

public sealed partial class MainViewModel
{
    /// <summary>Shows <paramref name="userText"/> in the status bar and logs <paramref name="ex"/> under <paramref name="logContext"/>.</summary>
    /// <param name="logContext">Operation and target for the log, e.g. "Save project to C:\x.hproj".</param>
    /// <returns>The status line, for callers that return it.</returns>
    internal string Fail(string userText, string logContext, Exception ex)
    {
        AppLog.Write(logContext, ex);
        return StatusText = userText;
    }

    /// <summary>Successful save: shows the status line and returns it as the outcome.</summary>
    private SaveOutcome SaveSucceeded(string line) => new(SaveResult.Saved, StatusText = line);

    /// <summary>Intentionally skipped save.</summary>
    private SaveOutcome SaveDeclined(string line) => new(SaveResult.Declined, StatusText = line);

    /// <summary>Failed save: shown and logged.</summary>
    private SaveOutcome SaveFailed(string path, Exception ex) =>
        new(SaveResult.Failed, Fail($"Could not save the project: {ex.Message}", $"Save project to {path}", ex));
}
