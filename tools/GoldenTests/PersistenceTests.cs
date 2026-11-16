using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// What is kept on disk and how it survives: whole-file writes (AtomicFile), the settings' migration chain run in memory from every
/// version, settings and presets files that are damaged or locked at start (never reset silently, never written over), changes made
/// in place from any thread and saved by one debounced write, the Settings window's save of only what it changed, and the project
/// format: version 7 only for projects that use 5.6 data, and the unknown fields of a newer version carried through a load, a look
/// edit, a tab round trip and a save.
/// </summary>
public static class PersistenceTests
{
    public static IEnumerable<GraphResult> All(string scratch)
    {
        yield return Case("atomic-file", "# AtomicFile: a failed write leaves the old file and no temporary file; keepPrevious keeps the replaced copy.", e => AtomicWrites(e, scratch));
        yield return Case("settings-migrations", "# SettingsMigrations.Migrate in memory: every start version ends at Latest, a second run changes nothing, and each step.", Migrations);
        yield return Case("settings-load-recovery", "# appsettings.json damaged, NUL-filled, locked or from a newer version: set aside, recovered from .prev, never written over.", SettingsRecovery);
        yield return Case("settings-update", "# AppSettings.Update from many threads at once, one debounced write, and UpdateIf that changes nothing.", Updates);
        yield return Case("settings-diff-save", "# The Settings window's save writes back only what it changed: a change made meanwhile survives.", DiffSave);
        yield return Case("presets-load-recovery", "# presets.json damaged or locked: set aside and recovered from .prev, or never written over.", PresetsRecovery);
        yield return Case("project-format-7", "# Format 7 is written only for a project that uses 5.6 data; 6 is still read; a 5.5-era refusal of 7 holds.", Format7);
        yield return Case("project-unknown-fields", "# A newer version's fields at every level of a project survive a load, a look edit, a tab round trip and a save.", UnknownFields);
    }

    // ----- atomic-file -----

    private static void AtomicWrites(Action<bool, string> expect, string scratch)
    {
        var folder = Path.Combine(scratch, "atomic-file");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "data.json");
        File.WriteAllText(path, "old");

        var threw = false;
        try
        {
            AtomicFile.Write(path, stream =>
            {
                stream.Write("partial"u8);
                throw new IOException("the disk filled");
            }, durable: true);
        }
        catch (IOException)
        {
            threw = true;
        }

        expect(threw && File.ReadAllText(path) == "old", "a write that throws part of the way: the failure is passed on and the file is still the old one");
        expect(Directory.GetFiles(folder, "*.tmp").Length == 0, "and its temporary file is deleted");

        AtomicFile.WriteAllText(path, "new", durable: true, keepPrevious: true);
        expect(File.ReadAllText(path) == "new" && File.ReadAllText(path + AtomicFile.PreviousSuffix) == "old",
            "a durable write keeping the previous copy: the new content in place, the replaced one as data.json.prev");

        AtomicFile.WriteAllText(path, "newer", durable: false);
        expect(File.ReadAllText(path) == "newer" && File.ReadAllText(path + AtomicFile.PreviousSuffix) == "old", "a write without keepPrevious leaves .prev as it was");

        var bytes = File.ReadAllBytes(path);
        expect(bytes.Length == 5 && bytes[0] == (byte)'n', "the text is UTF-8 with no byte order mark (as File.WriteAllText writes it)");

        File.WriteAllText(Path.Combine(folder, "broken.json"), "{");
        var aside = AtomicFile.SetAside(Path.Combine(folder, "broken.json"));
        expect(aside is not null && File.Exists(aside) && !File.Exists(Path.Combine(folder, "broken.json"))
               && Path.GetFileName(aside).StartsWith("broken.damaged-", StringComparison.Ordinal) && aside.EndsWith(".json", StringComparison.Ordinal),
            "SetAside moves a damaged file to <name>.damaged-<stamp>.json beside it");
    }

    // ----- settings-migrations -----

    private static string Json(AppSettings settings) => JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

    private static AppSettings At(int version, Action<AppSettings>? set = null)
    {
        var settings = new AppSettings { ViewDefaultsVersion = version, FirstRunComplete = true };
        set?.Invoke(settings);
        return settings;
    }

    private static void Migrations(Action<bool, string> expect)
    {
        expect(SettingsMigrations.Steps.Select(s => s.Version).SequenceEqual(Enumerable.Range(1, SettingsMigrations.Latest)),
            $"the steps are numbered 1 to {SettingsMigrations.Latest}, in order, one each");

        var allEnd = true;
        var idempotent = true;
        for (var version = 0; version <= SettingsMigrations.Latest; version++)
        {
            var settings = SettingsMigrations.Migrate(At(version));
            allEnd &= settings.ViewDefaultsVersion == SettingsMigrations.Latest;
            var once = Json(settings);
            idempotent &= Json(SettingsMigrations.Migrate(settings)) == once;
        }

        expect(allEnd, $"from every start version (0 to {SettingsMigrations.Latest}) the settings end at version {SettingsMigrations.Latest}");
        expect(idempotent, "and migrating them a second time changes nothing (the same JSON)");

        var one = SettingsMigrations.Migrate(At(0, s => (s.ShowKeyframesPane, s.ShowClipKeyframes, s.TimelineHeight) = (true, false, 3)));
        expect(one is { ShowKeyframesPane: false, ShowClipKeyframes: true, MasterTimelineHeight: 11 },
            "step 1: a file from before the panes gets the Keyframes pane off, clip keyframes on and the scrubber from the old height (4 x 3 - 3, then step 5's + 2)");

        expect(SettingsMigrations.Migrate(At(1, s => s.ShowAdvancedFiltersTab = false)).ShowAdvancedFiltersTab, "step 2: the advanced filters tab is shown once");

        var untouched = SettingsMigrations.Migrate(At(3, s => s.SquisherPresets = [new SquishPreset { Name = "Discord (Non-Nitro)", TargetSizeMb = 25, AudioKbps = 96 }]));
        var edited = SettingsMigrations.Migrate(At(3, s => s.SquisherPresets = [new SquishPreset { Name = "Mine", TargetSizeMb = 7, AudioKbps = 64 }]));
        expect(untouched.SquisherPresets.Count == SquishPreset.Defaults().Count && edited.SquisherPresets is [{ Name: "Mine" }],
            "step 4: a Squisher list that is still the one untouched preset gets the full set; an edited list is kept");

        expect(SettingsMigrations.Migrate(At(4, s => s.MasterTimelineHeight = 10)).MasterTimelineHeight == 12
               && SettingsMigrations.Migrate(At(4, s => (s.MasterTimelineHeight, s.TimelineHeight) = (0, 3))).MasterTimelineHeight == 0,
            "step 5: a saved scrubber height keeps its pixels (two steps up); 0 (from before the slider) stays 0");

        expect(SettingsMigrations.Migrate(At(5)).ShowTimesAsFrames && !SettingsMigrations.Migrate(At(0, s => s.ShowTimesAsFrames = false)).ShowTimesAsFrames,
            "step 6: times in frames once for a file from before; a version-0 file's own choice is kept");

        var cards = SettingsMigrations.Migrate(At(10, s => (s.BackgroundGpu, s.DecodeGpuAdapter) = (1, 2)));
        var processor = SettingsMigrations.Migrate(At(10, s => (s.BackgroundGpu, s.DecodeGpuAdapter) = (-2, -2)));
        expect(cards is { BackgroundGpu: -1, DecodeGpuAdapter: -1 } && processor is { BackgroundGpu: -2, DecodeGpuAdapter: -2 },
            "step 11 (0x116 rule): a saved background or decode card goes back to Auto (-1); Processor Only (-2) is kept");

        expect(SettingsMigrations.Migrate(At(12, s => s.HiddenTabs = ["Tab:Filters", "Tab:Audio"])).HiddenTabs is ["Tab:Color", "Tab:Audio"],
            "step 13: a hidden Filters tab stays hidden as the Color tab");

        // A first start: the object Load makes with no file, the first-run window's own choices, a save and the next start's load.
        var fresh = new AppSettings { ShowTimelineThumbnails = false, Migrated411Defaults = true, ViewDefaultsVersion = SettingsMigrations.Latest };
        fresh.ApplyEditorDefaults();
        (fresh.ShowKeyframesPane, fresh.DecodeGpuAdapter, fresh.BackgroundGpu, fresh.FirstRunComplete) = (true, 1, 1, true);
        var nextStart = SettingsMigrations.Migrate(JsonSerializer.Deserialize(Json(fresh), AppSettingsJsonContext.Default.AppSettings)!);
        expect(nextStart is { ShowKeyframesPane: true, DecodeGpuAdapter: 1, BackgroundGpu: 1 },
            "a first start's own choices (Keyframes pane on, cards picked for decode and background) survive its next start: no step runs on them");

        var newer = SettingsMigrations.Migrate(JsonSerializer.Deserialize("{\"ViewDefaultsVersion\":99,\"ShowStatusBar\":true,\"zzFutureKey\":42}", AppSettingsJsonContext.Default.AppSettings)!);
        expect(newer is { ViewDefaultsVersion: 99, ShowStatusBar: true } && newer.Extra?.ContainsKey("zzFutureKey") == true && Json(newer).Contains("\"zzFutureKey\":42", StringComparison.Ordinal),
            "a newer version's file (version 99): no step runs, and its unknown key is kept and written back");

        var nulls = SettingsMigrations.Migrate(JsonSerializer.Deserialize("{\"SmartRules\":[null,{\"Path\":null,\"Preset\":\"P\"}],\"SavedLayouts\":[null]}", AppSettingsJsonContext.Default.AppSettings)!);
        expect(nulls.SmartRules is [{ Path: "", Type: SmartRule.Folder }] && nulls.SavedLayouts.Count == 0,
            "null Smart Rules and layouts (a hand-edited file) are dropped, and a rule's null path reads as empty");
    }

    // ----- settings-load-recovery -----

    private static void SettingsRecovery(Action<bool, string> expect)
    {
        var file = AppSettings.FilePath;
        var previous = file + AtomicFile.PreviousSuffix;
        var folder = Path.GetDirectoryName(file)!;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var originalPrevious = File.Exists(previous) ? File.ReadAllBytes(previous) : null;
        string[] Damaged() => Directory.GetFiles(folder, "appsettings.damaged-*.json");
        void ClearDamaged()
        {
            foreach (var damaged in Damaged())
                File.Delete(damaged);
        }

        try
        {
            Directory.CreateDirectory(folder);
            ClearDamaged();
            var good = Json(At(SettingsMigrations.Latest, s => s.FfmpegPath = "C:/previous/ffmpeg.exe"));

            File.WriteAllText(previous, good);
            File.WriteAllText(file, "{\"FirstRunComplete\": tr");
            var read = AppSettings.Load();
            expect(read is { FirstRunComplete: true, FfmpegPath: "C:/previous/ffmpeg.exe" } && !AppSettings.ReadFailed,
                "a truncated appsettings.json: the previous copy (appsettings.json.prev) is used, not first-run defaults");
            expect(Damaged().Length == 1 && File.ReadAllText(Damaged()[0]) == "{\"FirstRunComplete\": tr" && !File.Exists(file),
                "and the damaged file is kept, set aside as appsettings.damaged-<stamp>.json");

            ClearDamaged();
            File.WriteAllBytes(file, new byte[256]);
            expect(AppSettings.Load().FfmpegPath == "C:/previous/ffmpeg.exe" && Damaged().Length == 1, "a NUL-filled file (a power cut before the flush): the same");

            ClearDamaged();
            File.Delete(previous);
            File.WriteAllText(file, "not json");
            var defaults = AppSettings.Load();
            expect(!defaults.FirstRunComplete && defaults.ViewDefaultsVersion == SettingsMigrations.Latest && Damaged().Length == 1,
                "damaged with no previous copy: a first start (stamped with the latest version), the damaged file still kept");

            ClearDamaged();
            File.WriteAllText(file, good);
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = AppSettings.Load();
                expect(AppSettings.ReadFailed && locked.FirstRunComplete && Damaged().Length == 0,
                    "a file another process holds open: read fails (after retries), defaults for this session, no first-run window, nothing set aside");
            }

            At(SettingsMigrations.Latest, s => s.FfmpegPath = "C:/elsewhere/ffmpeg.exe").SaveAsCurrent();
            expect(File.ReadAllText(file) == good, "and a save in that session (the lock gone) does not write over the file");

            File.WriteAllText(file, good);
            var loaded = AppSettings.Load();
            loaded.FfmpegPath = "C:/new/ffmpeg.exe";
            loaded.SaveAsCurrent();
            expect(File.ReadAllText(previous) == good && AppSettings.Load().FfmpegPath == "C:/new/ffmpeg.exe",
                "a save keeps the file it replaced as appsettings.json.prev and is read back");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            expect(false, Threw(ex));
        }
        finally
        {
            ClearDamaged();
            if (original is null) File.Delete(file); else File.WriteAllBytes(file, original);
            if (originalPrevious is null) File.Delete(previous); else File.WriteAllBytes(previous, originalPrevious);
            AppSettings.Reload();
        }
    }

    // ----- settings-update -----

    private static void Updates(Action<bool, string> expect)
    {
        var file = AppSettings.FilePath;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var height = AppSettings.Current.LayerHeight;
        try
        {
            AppSettings.Update(s => s.LayerHeight = 0, saveNow: true);
            const int Threads = 8, Each = 250;
            var workers = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
            {
                for (var i = 0; i < Each; i++)
                    AppSettings.Update(s => s.LayerHeight++);
            })).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
            expect(AppSettings.Current.LayerHeight == Threads * Each, $"{Threads} threads x {Each} in-place increments through Update: none lost ({AppSettings.Current.LayerHeight})");

            AppSettings.FlushPendingSave();
            var written = JsonSerializer.Deserialize(File.ReadAllText(file), AppSettingsJsonContext.Default.AppSettings)!;
            expect(written.LayerHeight == Threads * Each, "the debounced write, flushed, holds the final value");

            var changed = 0;
            void Count() => changed++;
            AppSettings.Changed += Count;
            var noChange = AppSettings.UpdateIf(_ => false);
            var set = AppSettings.Set(s => s.LayerHeight, (s, v) => s.LayerHeight = v, Threads * Each);
            var setOther = AppSettings.Set(s => s.LayerHeight, (s, v) => s.LayerHeight = v, 7);
            AppSettings.Changed -= Count;
            expect(!noChange && !set && setOther && changed == 1, "UpdateIf and Set that change nothing raise no Changed and schedule no write; a real change raises one");
        }
        finally
        {
            AppSettings.Update(s => s.LayerHeight = height);
            AppSettings.FlushPendingSave();
            if (original is not null)
                File.WriteAllBytes(file, original);
            AppSettings.Reload();
        }
    }

    // ----- settings-diff-save -----

    private static void DiffSave(Action<bool, string> expect)
    {
        var file = AppSettings.FilePath;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        try
        {
            var opened = AppSettings.Current.Clone();
            var screen = AppSettings.Current.Clone();
            screen.ShowTimesAsFrames = !screen.ShowTimesAsFrames;

            // While the window is open, a caption run marks the GPU as failed (CaptionGenerator.NoteGpuFellOver) on another thread.
            var marker = "golden-gpu-stamp";
            var background = new Thread(() => AppSettings.Update(s => s.WhisperGpuFailedOn = marker));
            background.Start();
            background.Join();

            IReadOnlyList<string> copied = [];
            AppSettings.Update(current => copied = AppSettings.CopyEdited(opened, screen, current), saveNow: true);
            expect(copied is [nameof(AppSettings.ShowTimesAsFrames)], $"only the property the window changed is copied back ({string.Join(", ", copied)})");
            expect(AppSettings.Current.WhisperGpuFailedOn == marker && AppSettings.Current.ShowTimesAsFrames == screen.ShowTimesAsFrames,
                "the change made meanwhile survives the window's save, and the window's own change is in effect");
            var written = JsonSerializer.Deserialize(File.ReadAllText(file), AppSettingsJsonContext.Default.AppSettings)!;
            expect(written.WhisperGpuFailedOn == marker && written.ShowTimesAsFrames == screen.ShowTimesAsFrames, "both are on disk");
        }
        finally
        {
            if (original is not null)
                File.WriteAllBytes(file, original);
            AppSettings.Reload();
        }
    }

    // ----- presets-load-recovery -----

    private static void PresetsRecovery(Action<bool, string> expect)
    {
        var file = PresetStore.FilePath;
        var previous = file + AtomicFile.PreviousSuffix;
        var folder = Path.GetDirectoryName(file)!;
        var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var originalPrevious = File.Exists(previous) ? File.ReadAllBytes(previous) : null;
        string[] Damaged() => Directory.GetFiles(folder, "presets.damaged-*.json");
        void ClearDamaged()
        {
            foreach (var damaged in Damaged())
                File.Delete(damaged);
        }

        try
        {
            Directory.CreateDirectory(folder);
            ClearDamaged();
            File.WriteAllText(previous, "[{\"Name\":\"Kept From Before\"}]");
            File.WriteAllText(file, "[{\"Name\":\"Half");
            var read = PresetStore.Load();
            expect(read is [{ Name: "Kept From Before" }] && Damaged().Length == 1, "a damaged presets.json: set aside, and the previous copy's presets are used");

            PresetStore.Save([new EncodingPreset { Name = "Saved After" }]);
            expect(Damaged().Length == 1 && File.ReadAllText(Damaged()[0]) == "[{\"Name\":\"Half", "a save afterwards never writes over the damaged copy");

            File.WriteAllText(file, "[null,{\"Name\":\"Only\"}]");
            expect(PresetStore.Load() is [{ Name: "Only" }], "a null entry (a hand-edited file) is dropped");

            var before = File.ReadAllBytes(file);
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                expect(PresetStore.Load().Count == 0 && PresetStore.ReadFailed, "a file another process holds open: the built-ins for this session");

            var refused = false;
            try
            {
                PresetStore.Save([new EncodingPreset { Name = "Over It" }]);
            }
            catch (IOException)
            {
                refused = true;
            }

            expect(refused && File.ReadAllBytes(file).SequenceEqual(before), "and a save in that session is refused (IOException) and the file left as it was");
        }
        finally
        {
            ClearDamaged();
            if (original is null) File.Delete(file); else File.WriteAllBytes(file, original);
            if (originalPrevious is null) File.Delete(previous); else File.WriteAllBytes(previous, originalPrevious);
            PresetStore.Load();
        }
    }

    // ----- project-format-7 and project-unknown-fields -----

    private static ProjectState Project(Action<TimelineClip>? change = null)
    {
        var doc = new TimelineDocument { FrameRate = FrameRate.Fps30 };
        using (var setup = doc.BeginTransaction("Set up"))
        {
            var clip = new TimelineClip { ContentKind = LayerKind.Text, End = 4, State = new LayerState { Kind = LayerKind.Text, TextContent = "Title" } };
            change?.Invoke(clip);
            setup.PlaceClip(setup.InsertTrack(0, TimelineTrackKind.Video), clip);
            setup.Commit();
        }

        return new ProjectState { Name = "Format", Timeline = doc };
    }

    private static int VersionOf(string json) => JsonNode.Parse(json)!["version"]!.GetValue<int>();

    private static void Format7(Action<bool, string> expect)
    {
        expect(VersionOf(ProjectStore.Serialize(Project())) == ProjectFile.BaseVersion, "a project with nothing of 5.6 is still written as format 6 (a 5.5 release opens it)");
        expect(VersionOf(ProjectStore.Serialize(Project(c => (c.Speed, c.End) = (16, 1)))) == 7, "a clip at 16x (5.5 plays at most 10x): format 7");
        expect(VersionOf(ProjectStore.Serialize(Project(c => c.State = new LayerState { Kind = LayerKind.Text, KeepPitch = false }))) == 7, "a look with Keep Pitch off: format 7");
        expect(VersionOf(ProjectStore.Serialize(Project(c => c.State = new LayerState { Kind = LayerKind.Text, SmoothSlowMo = true }))) == 7
               && VersionOf(ProjectStore.Serialize(Project(c => c.State = new LayerState { Kind = LayerKind.Text, SpectralAudio = true }))) == 7,
            "Smooth Slow-Mo or Spectral Audio: format 7");

        var seven = ProjectStore.Serialize(Project(c => (c.Speed, c.End) = (16, 1)));
        var six = ProjectStore.Serialize(Project());
        expect(ProjectStore.Deserialize(seven).Timeline.Tracks[0].Clips[0].Speed == 16 && ProjectStore.Deserialize(six).Timeline.Tracks.Count == 1,
            "this version opens both formats");

        // What 5.5 does with a format 7 file: its newest is 6, so 7 reads as newer and is refused unless asked.
        var asFive = JsonNode.Parse(seven)!;
        asFive["version"] = ProjectFile.CurrentVersion + 1;
        var refused = false;
        try
        {
            ProjectStore.Deserialize(asFive.ToJsonString());
        }
        catch (NewerProjectFormatException)
        {
            refused = true;
        }

        expect(refused && ProjectStore.Deserialize(asFive.ToJsonString(), allowNewer: true).Timeline.Tracks.Count == 1,
            "a format newer than this version's newest is refused unless the user asks to open it (as 5.5 now refuses format 7)");

        var five = JsonNode.Parse(six)!;
        five["version"] = 5;
        var old = false;
        try
        {
            ProjectStore.Deserialize(five.ToJsonString());
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("format 6 to 7", StringComparison.Ordinal))
        {
            old = true;
        }

        expect(old, "a format 5 project is refused, naming the formats this version opens (6 to 7)");
    }

    private static readonly string[] Levels = ["(file)", "source", "output", "sequence", "sequence.background", "settings", "timeline", "track", "clip", "clip look"];

    private static JsonObject At(JsonObject root, string level) => level switch
    {
        "(file)" => root,
        "sequence.background" => (JsonObject)(root["sequence"]!["background"] ??= new JsonObject()),
        "timeline" => root["timeline"]!.AsObject(),
        "track" => root["timeline"]!["tracks"]![0]!.AsObject(),
        "clip" => root["timeline"]!["tracks"]![0]!["clips"]![0]!.AsObject(),
        "clip look" => (JsonObject)(root["timeline"]!["tracks"]![0]!["clips"]![0]!["state"] ??= new JsonObject()),
        _ => (JsonObject)(root[level] ??= new JsonObject()),
    };

    private static string[] Missing(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        return [.. Levels.Where(level => At(root, level)["zzFuture"]?.GetValue<string>() != "from " + level)];
    }

    private static void UnknownFields(Action<bool, string> expect)
    {
        var root = JsonNode.Parse(ProjectStore.Serialize(Project()))!.AsObject();
        foreach (var level in Levels)
            At(root, level)["zzFuture"] = "from " + level;
        var newer = root.ToJsonString();

        var opened = ProjectStore.Deserialize(newer);
        var resaved = ProjectStore.Serialize(opened);
        expect(Missing(resaved) is [], $"opened and saved again: the unknown field kept at every level (lost at: {string.Join(", ", Missing(resaved))})");

        // A look edit rebuilds the clip's look from the layer (FlushLooks: view.ToState()).
        var clip = opened.Timeline.Tracks[0].Clips[0];
        var layer = Layer.FromState(clip.State, 1920, 1080, 1920, 1080);
        layer.TextContent = "Edited";
        clip.State = layer.ToState();
        expect(clip.State.Extra?.ContainsKey("zzFuture") == true && Missing(ProjectStore.Serialize(opened)) is [],
            "a look edit (the layer's ToState) keeps the look's unknown field");

        // A tab put behind and brought back: its state written as a project and read again; an undo step copies the timeline.
        var copy = opened.Timeline.Clone(deep: true);
        var tab = ProjectStore.Deserialize(ProjectStore.Serialize(new ProjectState { Name = opened.Name, Timeline = copy, Settings = opened.Settings }));
        expect(copy.CarriedProjectFields is not null && Missing(ProjectStore.Serialize(tab)) is [],
            "a timeline copy (an undo step, a tab) and a tab round trip keep them too");
    }
}
