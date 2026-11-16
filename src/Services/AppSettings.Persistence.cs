using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandPegApp.Services;

// ----- Reading and writing appsettings.json -----
//
// Load never resets the settings silently. A file that cannot be opened (locked by another process, access denied) gives
// this session defaults and the file is never written while it runs (ReadFailed), so an intact file is not replaced because
// one read failed. A file that opens but does not parse is set aside as appsettings.damaged-<stamp>.json and the copy the
// last save left beside it (appsettings.json.prev, AtomicFile) is used instead; only when neither reads does HandPeg start
// as a first run. Every write is whole and flushed (AtomicFile, durable).
//
// Two ways to change the settings in effect:
// - Update(change): an in-place change to Current, under Gate, from any thread; saved by one debounced background write.
//  For in-session toggles and background writers (captions, the hardware test, preset renames at start-up).
// - SaveAsCurrent: writes this object now and makes it Current. For the explicit saves the user waits on (the Settings
//  window, the first-run window, window close).
// Writes are ordered by WriteGate and always serialize the object in effect at write time, so a later write never carries
// older settings than an earlier one.

public sealed partial class AppSettings
{
    /// <summary>
    /// Keys this version does not know: a newer version's (a rollback, or two versions sharing one profile) or retired ones. Kept
    /// so that saving does not drop them.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// True when appsettings.json existed at start but could not be opened (another process held it, access denied): this session
    /// runs on defaults and never writes the file.
    /// </summary>
    internal static bool ReadFailed { get; private set; }

    /// <summary>Raised on the calling thread after <see cref="Update"/> changed the settings in effect.</summary>
    public static event Action? Changed;

    // Gate guards in-place changes to Current and its serialization; WriteGate orders whole-file writes and is always taken first.
    private static readonly Lock Gate = new();
    private static readonly Lock WriteGate = new();

    // Debounce for Update's saves: changes within this window share one write.
    private const int SaveDelayMs = 250;
    private static Timer? _saveTimer;
    private static int _savePending;
    private static int _skippedWriteLogged;

    private enum ReadOutcome { Read, Missing, Unreadable, Damaged }

    /// <summary>
    /// Changes the settings in effect in place and saves them shortly after on a background thread (several changes within
    /// 250 ms share one write). Safe from any thread: changes are serialized by one lock, and the write serializes the object
    /// as it is at write time.
    /// </summary>
    /// <param name="saveNow">Write before returning (and rethrow a write failure) instead of debouncing.</param>
    public static void Update(Action<AppSettings> change, bool saveNow = false)
    {
        lock (Gate)
            change(Current);
        Changed?.Invoke();

        if (saveNow)
        {
            Interlocked.Exchange(ref _savePending, 1);
            WriteCurrent(rethrow: true);
        }
        else
        {
            ScheduleSave();
        }
    }

    /// <summary>
    /// <see cref="Update"/> for a change that may find nothing to do: <paramref name="change"/> returns whether it changed anything,
    /// and only then is <see cref="Changed"/> raised and a save scheduled.
    /// </summary>
    /// <returns>Whether the settings changed.</returns>
    public static bool UpdateIf(Func<AppSettings, bool> change)
    {
        bool changed;
        lock (Gate)
            changed = change(Current);
        if (!changed)
            return false;

        Changed?.Invoke();
        ScheduleSave();
        return true;
    }

    /// <summary>Sets one value through <see cref="UpdateIf"/>: no event and no save when the settings already hold it.</summary>
    public static bool Set<T>(Func<AppSettings, T> get, Action<AppSettings, T> set, T value) =>
        UpdateIf(settings =>
        {
            if (EqualityComparer<T>.Default.Equals(get(settings), value))
                return false;
            set(settings, value);
            return true;
        });

    /// <summary>
    /// Writes an <see cref="Update"/> that is still waiting for its debounce: at exit, and before an import replaces the file
    /// (a late debounced write would otherwise put the old settings back over it).
    /// </summary>
    public static void FlushPendingSave() => WriteCurrent(rethrow: false);

    /// <summary>
    /// Copies into <paramref name="target"/> every property whose value differs between <paramref name="original"/> and
    /// <paramref name="edited"/>, compared as JSON. A screen that edits a copy saves with it (inside <see cref="Update"/>), so a
    /// change made to the settings in effect while the screen was open (a background writer, another pane) survives unless the
    /// screen changed the same property.
    /// </summary>
    /// <returns>The JSON names of the properties copied.</returns>
    internal static IReadOnlyList<string> CopyEdited(AppSettings original, AppSettings edited, AppSettings target)
    {
        var info = AppSettingsJsonContext.Default.AppSettings;
        var before = JsonSerializer.SerializeToNode(original, info)!.AsObject();
        var after = JsonSerializer.SerializeToNode(edited, info)!.AsObject();

        var copied = new List<string>();
        foreach (var property in info.Properties)
        {
            if (property.IsExtensionData || property.Get is not { } get || property.Set is not { } set)
                continue;
            before.TryGetPropertyValue(property.Name, out var was);
            after.TryGetPropertyValue(property.Name, out var now);
            if (System.Text.Json.Nodes.JsonNode.DeepEquals(was, now))
                continue;

            set(target, get(edited));
            copied.Add(property.Name);
        }

        return copied;
    }

    /// <summary>The clean-up every save applies: pasted quotes around paths, whitespace around addresses, incomplete Smart Rules.</summary>
    internal void TidyForSave()
    {
        YtDlpPath = Clean(YtDlpPath);
        FfmpegPath = Clean(FfmpegPath);
        FfprobePath = Clean(FfprobePath);
        WhisperPath = Clean(WhisperPath);
        YtDlpCookiesFilePath = Clean(YtDlpCookiesFilePath);
        YtDlpReleaseUrl = YtDlpReleaseUrl.Trim();
        FfmpegReleaseUrl = FfmpegReleaseUrl.Trim();
        WhisperReleaseUrl = WhisperReleaseUrl.Trim();
        SmartRules = SmartRules.Where(r => !string.IsNullOrWhiteSpace(r.Path) && !string.IsNullOrWhiteSpace(r.Preset)).ToList();
    }

    /// <summary>Writes the settings to disk (whole and flushed) and puts them into effect.</summary>
    public void SaveAsCurrent()
    {
        lock (WriteGate)
        {
            string json;
            lock (Gate)
            {
                TidyForSave();
                json = JsonSerializer.Serialize(this, JsonOptions);
                Current = this;

                // This write carries everything an earlier Update changed in Current when this is Current; a later Update
                // schedules its own write.
                Interlocked.Exchange(ref _savePending, 0);
            }

            WriteFile(json);
        }
    }

    /// <summary>Reads the file again and puts it into effect: after an import replaced it.</summary>
    public static void Reload()
    {
        // Any debounced write still waiting would serialize the settings that are about to be replaced.
        Interlocked.Exchange(ref _savePending, 0);
        Current = Load();
    }

    internal static AppSettings Load()
    {
        ReadFailed = false;
        var path = FilePath;

        switch (TryRead(path, out var settings, out var error))
        {
            case ReadOutcome.Read:
                return settings!;

            case ReadOutcome.Unreadable:
                AppLog.Write($"Settings: {path} could not be read; this session uses defaults and does not write the file", error!);
                ReadFailed = true;
                return new AppSettings
                {
                    ShowTimelineThumbnails = false, Migrated411Defaults = true, ViewDefaultsVersion = SettingsMigrations.Latest, FirstRunComplete = true,
                };

            case ReadOutcome.Damaged:
                var aside = AtomicFile.SetAside(path);
                AppLog.Write($"Settings: {path} is damaged and was set aside as {Path.GetFileName(aside) ?? "(could not be moved)"}", error!);
                break;
        }

        // No file, or a damaged one set aside: the copy the last save left beside it.
        var previous = path + AtomicFile.PreviousSuffix;
        if (File.Exists(previous))
        {
            var outcome = TryRead(previous, out var earlier, out var earlierError);
            if (outcome == ReadOutcome.Read)
            {
                AppLog.Write($"Settings: restored from {Path.GetFileName(previous)}");
                return earlier!;
            }

            if (earlierError is not null)
                AppLog.Write($"Settings: {Path.GetFileName(previous)} could not be used either", earlierError);
        }

        // A first start. Stamped with the latest migration version, so the first session's own choices are never migrated as an
        // old file's would be on the next start (SettingsMigrations).
        NoteFirstRun();
        return new AppSettings { ShowTimelineThumbnails = false, Migrated411Defaults = true, ViewDefaultsVersion = SettingsMigrations.Latest };
    }

    /// <summary>Reads and migrates one settings file. Open failures are retried briefly; anything wrong with the content is Damaged.</summary>
    private static ReadOutcome TryRead(string path, out AppSettings? settings, out Exception? error)
    {
        (settings, error) = (null, null);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();

                var read = JsonSerializer.Deserialize(text, AppSettingsJsonContext.Default.AppSettings)
                           ?? throw new JsonException("The settings file holds null.");
                settings = SettingsMigrations.Migrate(read);
                return ReadOutcome.Read;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return ReadOutcome.Missing;
            }
            catch (IOException) when (attempt < 4)
            {
                // A sharing violation from an antivirus scan, a backup or sync client, or another HandPeg mid-write: brief, so retried.
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
                return ReadOutcome.Unreadable;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // JSON that does not parse (truncated, NUL-filled, a hand-editing typo) or content a migration cannot take.
                error = ex;
                return ReadOutcome.Damaged;
            }
        }
    }

    private static void ScheduleSave()
    {
        Interlocked.Exchange(ref _savePending, 1);
        lock (Gate)
        {
            _saveTimer ??= new Timer(static _ => WriteCurrent(rethrow: false), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>Writes Current if a write is pending. Serializes under Gate, writes under WriteGate (taken first).</summary>
    private static void WriteCurrent(bool rethrow)
    {
        lock (WriteGate)
        {
            if (Interlocked.Exchange(ref _savePending, 0) == 0)
                return;

            try
            {
                string json;
                lock (Gate)
                    json = JsonSerializer.Serialize(Current, JsonOptions);
                WriteFile(json);
            }
            catch (InvalidOperationException ex) when (!rethrow)
            {
                // A list in Current was changed in place outside Update while it was serialized: written on the next turn instead.
                AppLog.Write("Settings: a background save met a list being changed; it is tried again", ex);
                ScheduleSave();
            }
            catch (Exception ex) when (!rethrow && ex is not OutOfMemoryException)
            {
                AppLog.Write("Settings: a background save failed", ex);
            }
        }
    }

    private static void WriteFile(string json)
    {
        if (ReadFailed)
        {
            if (Interlocked.Exchange(ref _skippedWriteLogged, 1) == 0)
                AppLog.Write("Settings: not saved, because the file could not be read at start (it is left as it was)");
            return;
        }

        AtomicFile.WriteAllText(FilePath, json, durable: true, keepPrevious: true);
    }

    // Paths are often pasted with the quotes Explorer's "Copy as path" adds.
    private static string Clean(string path) => path.Trim().Trim('"');
}
