using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// What HandPeg trusts from outside: the FFmpeg program a command may run, a shared bundle's command and strings, free text that only
/// looks like a path, command-line quoting that FFmpeg reads back exactly, a project's own command run only once confirmed, a settings
/// backup that cannot change which programs run, downloads that fail closed without a checksum, and HTTP timeouts reported as failures.
/// </summary>
public static class TrustBoundaryTests
{
    public static IEnumerable<GraphResult> All(string scratch)
    {
        yield return Case("ffmpeg-executable-trusted", "# FfmpegRunner.ResolveExecutable: a bare ffmpeg or the FFmpeg HandPeg uses; never a path a project chose.", Executable);
        yield return Case("bundle-strings-untrusted", "# A bundle's strings: free text and effect text never mapped as paths; unpack drops the command and maps only manifest entries.", BundleStrings);
        yield return Case("quote-argument-inverse", "# EncoderCommand.QuoteArgument is the exact inverse of CommandLineToArgvW (FfmpegRunner.SplitArguments).", Quoting);
        yield return Case("project-command-trust", "# CommandTrust: a project's own command or Extra options run only once confirmed; the user's own edits never ask; confirmed or saved text is trusted on this PC.", CommandTrustRules);
        yield return Case("settings-import-protected", "# Import Settings keeps this PC's tool paths, sources, folders and devices; a newer backup is refused; export leaves them out.", e => ImportProtected(e, scratch));
        yield return Case("download-checksums-fail-closed", "# Downloads fail closed without a checksum; every model's SHA-256 is pinned; release addresses are https or the default.", Checksums);
        yield return Case("http-timeout-is-failure", "# A server that does not answer in time fails the request (HttpRequestException), never reads as the user cancelling.", Timeouts);
    }

    // ----- ffmpeg-executable-trusted -----

    private static void Executable(Action<bool, string> expect)
    {
        static bool Refused(string token)
        {
            try
            {
                FfmpegRunner.ResolveExecutable(token);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        foreach (var token in new[] { @"\\host\share\ffmpeg.exe", @"C:\elsewhere\ffmpeg.exe", @"media\ffmpeg.bat", "ffmpeg.bat", "ffmpeg2", "media/ffmpeg.exe" })
            expect(Refused(token), $"refused: {token}");
        expect(!Refused("ffmpeg") && !Refused("\"ffmpeg.exe\"") && !Refused(DependencyUpdater.FfmpegPath),
            "accepted: ffmpeg, \"ffmpeg.exe\" and the FFmpeg HandPeg uses written in full (as generated commands name it)");
        expect(SourceProbe.Member(SourceProbe.Src("Services", "GpuExport.cs"), "private async Task RunFfmpegAsync(").Contains("FfmpegRunner.ResolveExecutable(Command.Executable)", StringComparison.Ordinal),
            "the GPU export resolves its executable the same way");
    }

    // ----- bundle-strings-untrusted -----

    private static void BundleStrings(Action<bool, string> expect)
    {
        var withText = EffectRegistry.All.FirstOrDefault(d => d.Params.Any(p => p.Kind == ParamKind.Text) && d.Params.Any(p => p.Kind == ParamKind.File));
        var textKey = withText?.Params.First(p => p.Kind == ParamKind.Text).Key ?? "";
        var fileKey = withText?.Params.First(p => p.Kind == ParamKind.File).Key ?? "";
        var project = new JsonObject
        {
            ["textContent"] = @"C:\Users\me\Documents\notes.txt",
            ["name"] = @"D:\Footage\Track name",
            ["imagePath"] = @"C:\Pictures\logo.png",
            ["markers"] = new JsonArray(new JsonObject { ["seconds"] = 1, ["name"] = @"C:\a marker.txt" }),
            ["effects"] = new JsonArray(new JsonObject
            {
                ["type"] = withText?.Type, ["values"] = new JsonObject { [textKey] = @"C:\words.txt", [fileKey] = @"C:\look.cube" },
            }),
        };
        ProjectBundle.Rewrite(project, value => ProjectBundle.LooksLikeFilePath(value) ? "media/mapped" : null);
        expect(withText is not null, $"an effect with both a text and a file parameter exists to test with ({withText?.Type})");
        expect((string?)project["textContent"] == @"C:\Users\me\Documents\notes.txt" && (string?)project["name"] == @"D:\Footage\Track name"
               && (string?)project["markers"]![0]!["name"] == @"C:\a marker.txt",
            "a title card's text, a name and a marker's name that read like paths are left as they are (never shipped as files)");
        expect((string?)project["imagePath"] == "media/mapped", "a real path field is still mapped");
        var values = project["effects"]![0]!["values"]!;
        expect((string?)values[textKey] == @"C:\words.txt" && (string?)values[fileKey] == "media/mapped",
            "an effect's text parameter is left as it is; its file parameter (a LUT) is still mapped");

        var unpack = SourceProbe.Member(SourceProbe.Src("Services", "ProjectBundle.cs"), "public static string Unpack(");
        expect(SourceProbe.InOrder(unpack, "Remove(\"manualCommand\")", "manifest.Media.Select(m => m.Entry)", "entries.Contains(value) ? InsideOf(folder, value) : null")
               && !unpack.Contains("StartsWith(MediaFolder", StringComparison.Ordinal),
            "unpacking drops a hand-edited command and points only the manifest's own entries at the unpacked files (not every string starting with media/)");
    }

    // ----- quote-argument-inverse -----

    private static void Quoting(Action<bool, string> expect)
    {
        string[][] cases =
        [
            ["title=My Mic\\"], ["a \"b\" \\"], ["x\\\"y"], [@"\\server\share\"], [""], ["plain"], ["two words"], ["tab\there"],
            ["-metadata:s:a:0", "title=x\\", "-metadata:s:a:1", "title=. -f mpegts http://evil/ -metadata a=\\", "out.mp4"],
        ];
        foreach (var arguments in cases)
        {
            var line = string.Join(" ", arguments.Select(EncoderCommand.QuoteArgument));
            var back = FfmpegRunner.SplitArguments(line);
            expect(back.SequenceEqual(arguments, StringComparer.Ordinal), $"{line}  ->  {back.Count} argument(s), as given");
        }

        var copy = SourceProbe.Member(SourceProbe.Src("Services", "EncoderCommand.cs"), "public static ExportCommand BuildCopy(");
        expect(copy.Contains("args.Select(QuoteArgument)", StringComparison.Ordinal) && !copy.Contains("title=\\\"", StringComparison.Ordinal),
            "the Copy export builds one item per argument and writes the line with QuoteArgument (a title is one argument, whatever it holds)");
    }

    // ----- project-command-trust -----

    private static void CommandTrustRules(Action<bool, string> expect)
    {
        IReadOnlyCollection<string> none = [];
        var trust = new CommandTrust();
        trust.TookFromFile("ffmpeg -i a.mp4 -f mpegts http://host/", "");
        expect(trust.NeedsConfirmation("ffmpeg -i a.mp4 -f mpegts http://host/", "", none), "a command the opened file carried needs confirming before it runs");
        expect(!trust.NeedsConfirmation("ffmpeg -i a.mp4 out.mp4", "", none), "a command the user has changed since is theirs: no question");
        trust.Confirmed();
        expect(!trust.NeedsConfirmation("ffmpeg -i a.mp4 -f mpegts http://host/", "", none), "once confirmed, it is not asked again");

        trust.TookFromFile(null, "-f mpegts http://host/");
        expect(trust.NeedsConfirmation("ffmpeg -i a.mp4 out.mp4", "-f mpegts http://host/", none) && trust.AreFileExtras("-f mpegts http://host/", none),
            "a project's own Extra options (appended to every generated command) need confirming too");
        trust.Clear();
        expect(!trust.NeedsConfirmation("anything", "-f mpegts http://host/", none), "this session's own queued job edited again asks nothing");

        // Trusted on this PC (confirmed, or written here and saved): the same text from a file runs at once; any other text still asks.
        const string Own = "ffmpeg -i a.mp4 -c:v libx264 -crf 18 out.mp4";
        IReadOnlyCollection<string> trusted = [CommandTrust.FingerprintOf(extras: false, Own), CommandTrust.FingerprintOf(extras: true, "-tune film")];
        trust.TookFromFile(Own, "-tune film");
        expect(!trust.NeedsConfirmation(Own, "-tune film", trusted) && trust.NeedsConfirmation(Own, "-tune film", none),
            "a command and Extra options trusted on this PC run without asking (the same file on another PC asks)");
        trust.TookFromFile("ffmpeg -i a.mp4 -f mpegts http://host/", "");
        expect(trust.NeedsConfirmation("ffmpeg -i a.mp4 -f mpegts http://host/", "", trusted), "a foreign command never seen here still asks");
        expect(CommandTrust.FingerprintOf(extras: false, Own) != CommandTrust.FingerprintOf(extras: true, Own) && CommandTrust.FingerprintOf(false, Own).Length == 66,
            "fingerprints: the kind and the SHA-256 of the text (the text itself is never kept)");
        var wiring = SourceProbe.Src("ViewModels", "MainViewModel.CommandTrust.cs");
        expect(SourceProbe.InOrder(SourceProbe.Member(wiring, "private bool ConfirmFileCommand("), "Confirm?.Invoke(", "TrustOnThisPc(", "_commandTrust.Confirmed();")
               && SourceProbe.MemberOfType("MainViewModel", "private ProjectState CaptureStateForSave(string name, Func<string, string>? moved, bool waitForThumbnail)").Contains("TrustOwnCommandOnSave();", StringComparison.Ordinal)
               && SourceProbe.Member(wiring, "private void TrustOwnCommandOnSave(").Contains("!_commandTrust.IsFileCommand(command, TrustedCommands)", StringComparison.Ordinal),
            "a confirmation and a save (of text that is not a file's unconfirmed) remember it in AppSettings.TrustedProjectCommands");

        var project = SourceProbe.Src("ViewModels", "MainViewModel.Project.cs");
        var queue = SourceProbe.Src("ViewModels", "MainViewModel.Queue.cs");
        expect(SourceProbe.Member(project, "private Task RestoreStateAsync(").Contains("_commandTrust.TookFromFile(state.ManualCommand, extras);", StringComparison.Ordinal)
               && SourceProbe.Member(SourceProbe.Src("ViewModels", "MainViewModel.EncodeAndLoad.cs"), "private Task RunEncodeAsync(").Contains("if (!ConfirmFileCommand(command))", StringComparison.Ordinal)
               && SourceProbe.Member(queue, "private async Task AddToQueueAsync(").Contains("if (!ConfirmFileCommand(CommandPreview))", StringComparison.Ordinal)
               && SourceProbe.InOrder(SourceProbe.Member(queue, "private async Task QueueProjectInBackgroundAsync("), "await worker.RestoreStateAsync(state, \"\");", "worker.DropFileCommand();", "worker.CreateQueueJob()"),
            "wired: an open marks the file's command, Export and Add to Queue confirm it, and a background job (no one to ask) drops it");
    }

    // ----- settings-import-protected -----

    private static void ImportProtected(Action<bool, string> expect, string scratch)
    {
        var settingsFile = AppSettings.FilePath;
        var presetsFile = PresetStore.FilePath;
        var kept = new[] { settingsFile, presetsFile, settingsFile + ".bak", presetsFile + ".bak" }.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        Directory.CreateDirectory(scratch);
        var backup = Path.Combine(scratch, "settings-backup.json");
        try
        {
            var here = new JsonObject
            {
                ["FirstRunComplete"] = true, ["FfmpegPath"] = "", ["CrashReportEndpoint"] = "", ["ShowTimesAsFrames"] = false, ["DownloadFolder"] = @"D:\Here",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.WriteAllText(settingsFile, here.ToJsonString());
            File.WriteAllText(presetsFile, "[]");

            JsonObject Backup(string schema) => new()
            {
                [BackupExporter.SchemaVersionKey] = schema,
                ["AppSettings"] = new JsonObject
                {
                    ["FirstRunComplete"] = true, ["FfmpegPath"] = @"\\host\share\ffmpeg.exe", ["FfmpegReleaseUrl"] = "http://host/releases",
                    ["AllowUnpinnedDependencyUpdates"] = true, ["CrashReportEndpoint"] = "https://elsewhere.example/crash", ["ShowTimesAsFrames"] = true,
                    ["DownloadFolder"] = @"E:\Theirs",
                },
                ["Presets"] = new JsonArray(new JsonObject { ["Name"] = "Shared Preset" }),
            };

            File.WriteAllText(backup, Backup("1.0").ToJsonString());
            var count = BackupImporter.ImportSettingsAndPresets(backup);
            var imported = JsonNode.Parse(File.ReadAllText(settingsFile))!.AsObject();
            expect(count == 1 && (bool?)imported["ShowTimesAsFrames"] == true, "the backup's ordinary settings and its preset are imported");
            expect((string?)imported["FfmpegPath"] == "" && imported["FfmpegReleaseUrl"] is null && imported["AllowUnpinnedDependencyUpdates"] is null
                   && (string?)imported["CrashReportEndpoint"] == "" && (string?)imported["DownloadFolder"] == @"D:\Here",
                "its FFmpeg path, release address, unpinned switch, crash endpoint and folders are not: this PC's values stay (or stay unset)");
            expect(File.Exists(settingsFile + ".bak"), "the file it replaced is kept as appsettings.json.bak");

            var before = File.ReadAllBytes(settingsFile);
            File.WriteAllText(backup, Backup("99.0").ToJsonString());
            var refused = false;
            try
            {
                BackupImporter.ImportSettingsAndPresets(backup);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            expect(refused && File.ReadAllBytes(settingsFile).SequenceEqual(before), "a backup of a newer major format is refused and changes nothing");

            File.WriteAllText(settingsFile, new JsonObject { ["FirstRunComplete"] = true, ["FfmpegPath"] = @"C:\Tools\ffmpeg.exe", ["YtDlpCookiesFilePath"] = @"C:\Users\me\cookies.txt" }.ToJsonString());
            BackupExporter.ExportSettingsAndPresets(backup);
            var exported = JsonNode.Parse(File.ReadAllText(backup))!["AppSettings"]!.AsObject();
            expect(BackupImporter.ProtectedKeys.All(k => exported[k] is null) && (bool?)exported["FirstRunComplete"] == true,
                "an export leaves this PC's tool paths, cookies file and the other protected keys out");
        }
        finally
        {
            foreach (var (path, bytes) in kept)
            {
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            }

            AppSettings.Reload();
            PresetStore.Load();
        }
    }

    // ----- download-checksums-fail-closed -----

    private static void Checksums(Action<bool, string> expect)
    {
        static bool Throws(Action act)
        {
            try
            {
                act();
                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }

        expect(Throws(() => DependencyUpdater.Verify("a tool", "ABC", null)), "a download with no known checksum is refused (fail closed)");
        expect(Throws(() => DependencyUpdater.Verify("a tool", "ABC", "DEF")) && !Throws(() => DependencyUpdater.Verify("a tool", "abc", "ABC")),
            "a mismatch is refused; a match passes (case does not matter)");
        var models = DependencyUpdater.WhisperModels.Append(DependencyUpdater.VadModel).ToList();
        var pinned = models.Where(m => DependencyUpdater.ModelSha256.TryGetValue(m, out var hash) && hash.Length == 64 && hash.All(Uri.IsHexDigit)).ToList();
        expect(pinned.Count == models.Count, $"every model HandPeg offers has a pinned SHA-256 ({pinned.Count} of {models.Count})");
        expect(DependencyUpdater.HttpsOrDefault("http://host/releases", "D") == "D" && DependencyUpdater.HttpsOrDefault("  ", "D") == "D"
               && DependencyUpdater.HttpsOrDefault("https://host/releases", "D") == "https://host/releases",
            "a release address of one's own is used only when it is an absolute https address");
        var install = SourceProbe.Src("Services", "DependencyUpdater.cs");
        expect(!install.Contains("no checksum published\" :", StringComparison.Ordinal) && install.Contains("?? throw new InvalidDataException(asset.Pin == PinState.StandIn", StringComparison.Ordinal),
            "a tool whose release publishes no checksum is not installed, whatever its pin");
    }

    // ----- http-timeout-is-failure -----

    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(5000, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static void Timeouts(Action<bool, string> expect)
    {
        using var client = new HttpClient(new DependencyUpdater.AnswerDeadline(TimeSpan.FromMilliseconds(100)) { InnerHandler = new Silent() });
        Exception? timedOut = null;
        try
        {
            client.GetAsync("https://example.invalid/").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            timedOut = ex;
        }

        expect(timedOut is HttpRequestException { InnerException: TimeoutException } && DependencyUpdater.IsInstallFailure(timedOut),
            $"no answer within the limit: an HttpRequestException (an install failure), not a cancellation ({timedOut?.GetType().Name})");

        using var cancel = new CancellationTokenSource(100);
        using var patient = new HttpClient(new DependencyUpdater.AnswerDeadline(TimeSpan.FromSeconds(30)) { InnerHandler = new Silent() });
        Exception? cancelled = null;
        try
        {
            patient.GetAsync("https://example.invalid/", cancel.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            cancelled = ex;
        }

        expect(cancelled is OperationCanceledException, $"the caller's own cancel stays a cancellation ({cancelled?.GetType().Name})");
    }
}
