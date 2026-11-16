using System.IO;
using System.Text.RegularExpressions;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// How failures reach the log and the user: exceptions logged with their stack (once, then one line), every user-facing failure
/// logged where it is shown, no <c>async void</c> outside the one guard, save outcomes typed instead of matched by wording, settings
/// changed only through <see cref="AppSettings.Update"/>, and the operation boundary telling a timeout from the user's cancel.
/// </summary>
public static partial class FaultBoundaryTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("app-log-stack-traces", "# AppLog writes an exception's type, message, stack and inner exceptions once; the same fault again is one line.", StackTraces);
        yield return Case("log-exceptions-routed", "# No log line hand-formats an exception's message; no user-facing failure goes unlogged (the census, with its reviewed exceptions).", Routed);
        yield return Case("async-void-guarded", "# No async void outside Faults.cs; Faults.Forget logs a fault and drops a cancellation.", AsyncVoid);
        yield return Case("save-outcome-typed", "# Project saves return SaveOutcome; nothing branches on the words of a status line.", SaveOutcomes);
        yield return Case("settings-writes-routed", "# The settings in effect change only through AppSettings.Update/Set; SaveAsCurrent only where the user saves on purpose.", SettingsWrites);
        yield return Case("operation-cancel-labels", "# RunOperationAsync: the user's cancel says Cancelled; an internal timeout says timed out and is logged with its stack.", CancelLabels);
    }

    private static IEnumerable<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}ShellThumbnails{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(SourceProbe.Root, f).Replace('\\', '/'), File.ReadAllText(f).ReplaceLineEndings("\n")));

    // ----- app-log-stack-traces -----

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message, new IOException("the inner cause"));
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    // A second throw site: its stack differs from Thrown's, so it is written in full the first time.
    private static Exception ThrownElsewhere(string message)
    {
        try
        {
            throw new InvalidDataException(message);
        }
        catch (InvalidDataException ex)
        {
            return ex;
        }
    }

    private static void StackTraces(Action<bool, string> expect)
    {
        var fault = Thrown("golden fault");
        var first = AppLog.Describe("Golden", fault);
        expect(first.Contains("InvalidOperationException: golden fault", StringComparison.Ordinal) && first.Contains("   at ", StringComparison.Ordinal)
               && first.Contains("IOException: the inner cause", StringComparison.Ordinal),
            "the first time: type, message, the stack's frames and the inner exception");
        var again = AppLog.Describe("Golden", fault);
        expect(again == "Golden: InvalidOperationException: golden fault (same stack as earlier)", "the same fault again: one line, not the stack a second time");
        var deep = AppLog.Describe("Golden", ThrownElsewhere(new string('x', 10_000)));
        expect(deep.Length <= AppLog.MostEntryChars + 16 && deep.EndsWith("(cut)", StringComparison.Ordinal), $"an entry is capped at {AppLog.MostEntryChars} characters");
        var constructed = AppLog.Describe("Golden", new TimeoutException("never thrown"));
        expect(constructed == "Golden: TimeoutException: never thrown", "an exception with no stack (never thrown) is one line");

        var before = File.Exists(AppLog.FilePath) ? File.ReadAllLines(AppLog.FilePath).Count(l => l.Contains("golden write-once", StringComparison.Ordinal)) : 0;
        var key = "golden|" + Guid.NewGuid();
        AppLog.WriteOnce(key, "golden write-once", Thrown("once"));
        AppLog.WriteOnce(key, "golden write-once", Thrown("once"));
        var after = File.ReadAllLines(AppLog.FilePath).Count(l => l.Contains("golden write-once", StringComparison.Ordinal));
        expect(after == before + 1, "WriteOnce writes a recurring failure once per key");
        expect(SourceProbe.Src("Services", "AppLog.cs").Contains("new Mutex(initiallyOwned: false, MutexName(FilePath))", StringComparison.Ordinal),
            "appends and the rotation are serialized across the UI process and the compositor worker by a named mutex");
        var worker = SourceProbe.Src("Compositor", "CompositorWorker.cs");
        expect(worker.Contains("AppDomain.CurrentDomain.UnhandledException +=", StringComparison.Ordinal) && worker.Contains("ExceptionDispatchInfo.Throw(fault)", StringComparison.Ordinal)
               && worker.Contains("AppLog.Write(\"Compositor worker failed\", ex);", StringComparison.Ordinal),
            "the compositor worker logs an unhandled exception (a decode lane's) and its own failure with the stack, and rethrows a lane's fault with the lane's stack");
    }

    // ----- log-exceptions-routed -----

    [GeneratedRegex(@"AppLog\.Write\([^;\n]*(\{(ex|e|inner|cause|error|exception|fault|failure)\.Message\}|\+\s*(ex|e)\.Message\b)")]
    private static partial Regex HandFormatted();

    [GeneratedRegex(@"catch\s*\(\s*[A-Za-z_][\w.<>]*\s+([a-z_]\w*)\s*\)")]
    private static partial Regex CatchClause();

    // Reviewed catch blocks that show or pass on an exception's message without logging it themselves: each passes it to a caller that
    // logs or shows it, or is not a failure (a refusal by design, a choice the user makes).
    private static readonly (string File, string Snippet)[] Reviewed =
    [
        ("src/Compositor/FfmpegVideoDecoder.cs", "would not start ({ex.Message})"),
        ("src/Compositor/FfmpegVideoDecoder.cs", "The file cannot be read ({ex.Message})"),
        ("src/Compositor/HardwareBenchmark.Compositor.cs", "the compositor could not be timed ({ex.Message})"),
        ("src/Compositor/HardwareBenchmark.cs", "adapter.Note = ex.Message;"),
        ("src/Compositor/HardwareBenchmark.cs", "The two-card test stopped short: {ex.Message}"),
        ("src/Compositor/HardwareBenchmark.Profile.cs", "adapter.Note = ex.Message;"),
        ("src/Services/MaskStore.cs", "could not be unpacked: {ex.Message}"),
        ("src/Services/Audio/AudioDecoders.cs", "Media Foundation could not read it ({ex.Message})"),
        ("src/ViewModels/MainViewModel.Document.cs", "DevFlightRecorder.Record(\"refused\""),
        ("src/ViewModels/MainViewModel.Project.cs", "{newer.Message}"),
        ("src/Services/DependencyUpdater.cs", "new TimeoutException(ex.Message, ex)"),
    ];

    private static void Routed(Action<bool, string> expect)
    {
        var hand = Sources().SelectMany(s => s.Text.Split('\n').Select((line, i) => (s.Path, Line: i + 1, Text: line)))
            .Where(l => HandFormatted().IsMatch(l.Text)).Select(l => $"{l.Path}:{l.Line}").ToList();
        expect(hand.Count == 0, $"no AppLog.Write formats an exception's message by hand: the (what, ex) overload carries the stack{(hand.Count == 0 ? "" : ": " + string.Join(", ", hand))}");

        var silent = new List<string>();
        foreach (var (path, text) in Sources())
        {
            foreach (Match match in CatchClause().Matches(text))
            {
                var open = text.IndexOf('{', match.Index + match.Length);
                if (open < 0)
                    continue;
                var (depth, end) = (0, open);
                for (; end < text.Length; end++)
                {
                    if (text[end] == '{')
                        depth++;
                    else if (text[end] == '}' && --depth == 0)
                        break;
                }

                var body = text[open..Math.Min(end + 1, text.Length)];
                var variable = match.Groups[1].Value;
                if (!body.Contains(variable + ".Message", StringComparison.Ordinal)
                    || new[] { "AppLog.", "Fail(", "UiFault.", "NoteTabFault", "TimelineFault", "SaveFailed(" }.Any(body.Contains))
                {
                    continue;
                }

                if (!Reviewed.Any(r => r.File == path && body.Contains(r.Snippet, StringComparison.Ordinal)))
                    silent.Add($"{path}:{text[..match.Index].Count(c => c == '\n') + 1}");
            }
        }

        expect(silent.Count == 0, $"every catch block that shows or returns an exception's message logs it too (Fail, UiFault.Show, AppLog), or is a reviewed exception{(silent.Count == 0 ? "" : ": " + string.Join(", ", silent))}");
    }

    // ----- async-void-guarded -----

    private static void AsyncVoid(Action<bool, string> expect)
    {
        var found = Sources().Where(s => !s.Path.EndsWith("/Faults.cs", StringComparison.Ordinal) && (s.Text.Contains("async void", StringComparison.Ordinal) || s.Text.Contains("async partial void", StringComparison.Ordinal))).Select(s => s.Path).ToList();
        expect(found.Count == 0, $"no async void outside Faults.cs: an event handler hands its task to Faults.Forget{(found.Count == 0 ? "" : ": " + string.Join(", ", found))}");

        var marker = "golden-forget-" + Guid.NewGuid().ToString("N")[..8];
        Faults.Forget(Task.FromException(new InvalidOperationException("forgotten")), marker);
        Faults.Forget(Task.FromCanceled(new CancellationToken(true)), marker + "-cancelled");
        var logged = false;
        for (var i = 0; i < 50 && !logged; i++)
        {
            logged = File.Exists(AppLog.FilePath) && File.ReadAllText(AppLog.FilePath).Contains($"{marker} failed: System.InvalidOperationException: forgotten", StringComparison.Ordinal);
            if (!logged)
                Thread.Sleep(20);
        }

        expect(logged, "a fault in forgotten work is logged with its stack, not rethrown on the dispatcher");
        expect(!File.ReadAllText(AppLog.FilePath).Contains(marker + "-cancelled failed", StringComparison.Ordinal), "a cancellation is not a fault");
        expect(SourceProbe.Src("MainWindow.GpuPreview.cs").Contains("private void GpuSaveFrame(GpuPreviewEngine engine, string path) => Faults.Forget(GpuSaveFrameAsync(engine, path), nameof(GpuSaveFrame));", StringComparison.Ordinal),
            "Export Frame (the review's first unguarded async void) goes through Faults.Forget");
    }

    // ----- save-outcome-typed -----

    private static void SaveOutcomes(Action<bool, string> expect)
    {
        var matched = Sources().Where(s => s.Text.Contains("StartsWith(\"Project saved\"", StringComparison.Ordinal)).Select(s => s.Path).ToList();
        expect(matched.Count == 0, $"nothing branches on a status line starting \"Project saved\"{(matched.Count == 0 ? "" : ": " + string.Join(", ", matched))}");
        var vm = typeof(MainViewModel);
        expect(vm.GetMethod(nameof(MainViewModel.SaveProject))?.ReturnType == typeof(SaveOutcome) && vm.GetMethod(nameof(MainViewModel.SaveProjectTo))?.ReturnType == typeof(SaveOutcome),
            "SaveProject and SaveProjectTo return a SaveOutcome (Saved, Declined or Failed, with the status line)");
        expect(new SaveOutcome(SaveResult.Declined, "Not saved: x").IsSaved == false && new SaveOutcome(SaveResult.Saved, "anything").IsSaved,
            "IsSaved reads the result, never the words");
        expect(SourceProbe.Src("ViewModels", "MainViewModel.Faults.cs").Contains("Fail($\"Could not save the project: {ex.Message}\", $\"Save project to {path}\", ex)", StringComparison.Ordinal),
            "a failed save is logged with its path and exception as it is shown");
    }

    // ----- settings-writes-routed -----

    [GeneratedRegex(@"AppSettings\.Current\.[A-Z]\w*\s*(=|\+=|-=)(?!=)")]
    private static partial Regex DirectWrite();

    private static void SettingsWrites(Action<bool, string> expect)
    {
        // The first-run window previews a theme in memory and puts the old one back on Cancel: never saved, so not an Update.
        string[] previews = ["src/Dialogs/FirstRunWindow.Screens.cs", "src/Dialogs/FirstRunWindow.xaml.cs"];
        var direct = Sources().Where(s => !s.Path.Contains("/AppSettings", StringComparison.Ordinal) && !previews.Contains(s.Path))
            .SelectMany(s => s.Text.Split('\n').Select((line, i) => (s.Path, Line: i + 1, Text: line))).Where(l => DirectWrite().IsMatch(l.Text))
            .Select(l => $"{l.Path}:{l.Line}").ToList();
        expect(direct.Count == 0, $"no code assigns AppSettings.Current's properties directly (AppSettings.Update / Set / UpdateIf instead){(direct.Count == 0 ? "" : ": " + string.Join(", ", direct))}");

        string[] explicitSaves = ["src/Dialogs/FirstRunWindow.xaml.cs", "src/Services/AppSettings.Persistence.cs"];
        var saves = Sources().Where(s => s.Text.Contains("SaveAsCurrent()", StringComparison.Ordinal) && !explicitSaves.Contains(s.Path)).Select(s => s.Path).ToList();
        expect(saves.Count == 0, $"SaveAsCurrent only where a whole edited copy is saved on purpose (the first-run window){(saves.Count == 0 ? "" : ": " + string.Join(", ", saves))}");
        expect(SourceProbe.Member(SourceProbe.Src("Dialogs", "SettingsWindow.xaml.cs"), "private void Save_Click(").Contains("AppSettings.Update(current => AppSettings.CopyEdited(_opened, _settings, current), saveNow: true);", StringComparison.Ordinal),
            "the Settings window writes back only what it changed");
    }

    // ----- operation-cancel-labels -----

    private static void CancelLabels(Action<bool, string> expect)
    {
        var run = SourceProbe.Member(SourceProbe.Src("ViewModels", "MainViewModel.EncodeAndLoad.cs"), "private async Task RunOperationAsync(");
        expect(SourceProbe.InOrder(run, "outcome = await run;", "catch (OperationCanceledException) when (run is { IsCanceled: true } || cancellation.IsCancellationRequested)",
                   "outcome = \"Cancelled.\";", "catch (OperationCanceledException ex)", "AppLog.Write($\"{title} failed\", ex);", "timed out.", "catch (Exception ex)", "AppLog.Write($\"{title} failed\", ex);"),
            "the user's cancel (the Cancel command or the work flyout's) says Cancelled; any other cancellation is a fault, logged, and says timed out when it was a timeout");
        expect(SourceProbe.Member(SourceProbe.Src("Services", "WorkScheduler.cs"), "public static Task<T> Run<T>(").Contains("AppLog.WriteOnce($\"work|{ex.GetType().FullName}|{ex.StackTrace}\"", StringComparison.Ordinal),
            "background work that fails is logged once per kind of failure");
    }
}
