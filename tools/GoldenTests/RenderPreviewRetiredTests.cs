using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HandPegApp;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4.1A (Subagent A): render-preview-retired. The legacy temp-file Render Preview is gone (CLAUDE.md 8: the Live Encoded
/// Preview is the single encode preview): no command, event, encode or settings of it in the view model, the exporter or AppSettings;
/// a settings file saved with its two values still loads and the next save drops them; a playback bar saved with its button loads
/// with the rest; no button, binding or Settings section names it; and no source file under src names any of its members. Every
/// line says what it proved.
/// </summary>
public static class RenderPreviewRetiredTests
{
    private const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static IEnumerable<GraphResult> All()
    {
        yield return Retired();
    }

    private static GraphResult Retired()
    {
        var text = new StringBuilder("# Render Preview retired (5.4.1A): the Live Encoded Preview is the single encode preview; nothing of the old one is left.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            // The members, by reflection.
            var vm = typeof(MainViewModel);
            Expect(vm.GetProperty("RenderPreviewCommand", Every) is null && vm.GetMethod("RenderPreviewAsync", Every) is null
                   && vm.GetEvent("PreviewRendered", Every) is null && vm.GetProperty("PreviewFolder", Every) is null,
                "MainViewModel has no RenderPreviewCommand, RenderPreviewAsync, PreviewRendered event or PreviewFolder");
            Expect(typeof(GpuExport).GetMethod("RunPreviewAsync", Every) is null && typeof(GpuExport).GetField("SoundRate", Every) is null
                   && typeof(GpuExport).GetMethod("WriteSound", Every) is null,
                "GpuExport has no RunPreviewAsync, SoundRate or WriteSound");
            var prepare = typeof(GpuExport).GetMethod("Prepare", Every);
            Expect(prepare is not null && prepare.GetParameters().All(p => p.Name != "captionsShift"),
                "GpuExport.Prepare takes no captionsShift (the preview's stretch start): an export's captions are on the sequence's clock");
            Expect(typeof(EncoderCommand).GetMethod("BuildPreview", Every) is null && typeof(GpuExportTiming).GetMethod("Window", Every) is null
                   && typeof(GpuExportTiming).GetProperty("FirstCanvasFrame", Every) is null,
                "EncoderCommand has no BuildPreview, and GpuExportTiming no Window or FirstCanvasFrame (an export starts at the canvas's frame 0)");
            Expect(typeof(AppSettings).GetProperty("PreviewResolutionPercent", Every) is null && typeof(AppSettings).GetProperty("PreviewDurationSeconds", Every) is null,
                "AppSettings has no PreviewResolutionPercent or PreviewDurationSeconds");
            Expect(typeof(SessionPaths).GetProperty("Previews", Every) is not null,
                "SessionPaths.Previews stays: the session's scratch files (mask stills, export sound renders, GIF frames) use it");

            // A settings file saved before 5.4.1A: its two values are skipped on load and gone from the next save.
            const string old = "{\"PreviewResolutionPercent\":25,\"PreviewDurationSeconds\":8,\"FirstRunComplete\":true}";
            var loaded = JsonSerializer.Deserialize(old, AppSettingsJsonContext.Default.AppSettings);
            var saved = loaded is null ? "" : JsonSerializer.Serialize(loaded, AppSettingsJsonContext.Default.AppSettings);
            Expect(loaded is { FirstRunComplete: true } && saved.Length > 0 && !saved.Contains("PreviewResolutionPercent", StringComparison.Ordinal)
                   && !saved.Contains("PreviewDurationSeconds", StringComparison.Ordinal),
                "an old settings file with PreviewResolutionPercent and PreviewDurationSeconds loads (its other values kept) and saves without them");

            // A playback bar saved with the old button: the rest is shown, and the next save drops its name.
            var bar = new PlaybackControlSet();
            Expect(bar.Options.All(o => o.Id != "RenderPreview") && !bar["RenderPreview"], "the playback bar offers no RenderPreview control");
            bar.Load("RenderPreview,Volume,@3");
            var resaved = bar.Save();
            text.Append("   RenderPreview,Volume,@3 saves as ").Append(resaved).Append('\n');
            Expect(resaved == "Volume,@3" && bar["Volume"], "a bar saved as RenderPreview,Volume,@3 loads with Volume shown and saves as Volume,@3");
            bar.Load(null);
            Expect(!bar.Save().Contains("RenderPreview", StringComparison.Ordinal), "the bar's defaults do not name RenderPreview");

            // The XAML: no button, binding or Settings section of it.
            var main = Read("MainWindow.xaml");
            Expect(!main.Contains("AutomationProperties.Name=\"Render Preview\"", StringComparison.Ordinal) && !main.Contains("RenderPreviewCommand", StringComparison.Ordinal)
                   && !main.Contains("PlaybackControls[RenderPreview]", StringComparison.Ordinal),
                "MainWindow.xaml: neither playback bar has a Render Preview button, binding or visibility switch");
            var settings = Read("Dialogs", "SettingsWindow.xaml");
            Expect(!settings.Contains("Render Preview", StringComparison.OrdinalIgnoreCase) && !settings.Contains("PreviewResolutionPercent", StringComparison.Ordinal)
                   && !settings.Contains("PreviewDurationSeconds", StringComparison.Ordinal),
                "SettingsWindow.xaml: no Render Preview section, text or binding (Preview & Cache starts at Caches)");
            Expect(!Read("Dialogs", "SettingsWindow.Stage.cs").Contains("Render Preview", StringComparison.Ordinal),
                "SettingsWindow.Stage.cs: the Preview & Cache intro and the stage map name no Render Preview section");

            // Nothing under src names a removed member.
            var removed = new Regex("RenderPreview|BuildPreview|RunPreviewAsync|PreviewRendered|PreviewResolutionPercent|PreviewDurationSeconds", RegexOptions.CultureInvariant);
            var hits = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Where(f => removed.IsMatch(File.ReadAllText(f)))
                .Select(f => Path.GetRelativePath(Root(), f))
                .ToList();
            Expect(hits.Count == 0, $"no .cs or .xaml file under src names RenderPreview, BuildPreview, RunPreviewAsync, PreviewRendered, PreviewResolutionPercent or PreviewDurationSeconds{(hits.Count == 0 ? "" : ": " + string.Join(", ", hits))}");
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("render-preview-retired", text.ToString(), problems, 0);
    }

    private static string Root()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "src", .. parts]));
}
