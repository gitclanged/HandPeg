using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- User-facing failures: shown and logged in one call -----
//
// A failure the user sees in the status bar is also written to HandPeg.log with its exception, so report-share triage has the
// time, the operation, the path and the exception type after the status line has been replaced. Expected failures (I/O, access,
// JSON) are logged with type and message; JsonException's message carries the line and byte position.

public sealed partial class MainViewModel
{
    /// <summary>Shows <paramref name="userText"/> in the status bar and logs <paramref name="ex"/> under <paramref name="logContext"/>.</summary>
    /// <param name="logContext">The operation and its target for the log, for example "Save project to C:\x.hproj".</param>
    /// <returns>The status line, for methods that return it.</returns>
    internal string Fail(string userText, string logContext, Exception ex)
    {
        AppLog.Write(logContext, ex);
        return StatusText = userText;
    }

    /// <summary>A save that wrote the project: the status line shown and returned as the outcome.</summary>
    private SaveOutcome SaveSucceeded(string line) => new(SaveResult.Saved, StatusText = line);

    /// <summary>A save that wrote nothing by choice.</summary>
    private SaveOutcome SaveDeclined(string line) => new(SaveResult.Declined, StatusText = line);

    /// <summary>A save that failed: shown and logged.</summary>
    private SaveOutcome SaveFailed(string path, Exception ex) =>
        new(SaveResult.Failed, Fail($"Could not save the project: {ex.Message}", $"Save project to {path}", ex));
}
