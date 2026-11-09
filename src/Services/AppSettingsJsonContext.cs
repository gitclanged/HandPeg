using System.Text.Json.Serialization;
using HandPegApp.Docking;
using HandPegApp.Models;

namespace HandPegApp.Services;

// ----- Start-up's JSON, compiled ahead (Pass 5.2D) -----
//
// What HandPeg reads before its first frame (the settings, the export presets, the encoders found before, the panes'
// arrangement) is read through code the compiler made for these types, not through reflection: no type is looked over
// at run time and no accessor is made then, so the reads cost what parsing costs. The generated metadata is the
// reflection's own (the same names, order, ignore rules, enums as numbers and escaping), so every file reads and writes
// to the byte as before (golden test startup-json-source-generation).

/// <summary>The generated metadata for the start-up reads (default options: what each read used before).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<EncodingPreset>))]
[JsonSerializable(typeof(IEnumerable<EncodingPreset>))]
[JsonSerializable(typeof(EncoderProber.SavedResult))]
[JsonSerializable(typeof(DockLayoutState))]
[JsonSerializable(typeof(DockNode))]
// The side windows (5.4C: AppSettings.SideWindows, read with the arrangement before the first frame).
[JsonSerializable(typeof(List<SideWindowState>))]
[JsonSerializable(typeof(SideWindowState))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
