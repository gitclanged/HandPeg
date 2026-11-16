using System.Text.Json.Serialization;
using HandPegApp.Docking;
using HandPegApp.Models;

namespace HandPegApp.Services;

// ----- Source-generated startup JSON -----
//
// Pre-Frame-1 reads (settings, export presets, cached encoder probe, dock layout) use System.Text.Json source generation instead of
// reflection, so the cost is parsing only. Metadata matches the reflection serializer exactly (names, order, ignore rules, numeric
// enums, escaping) for byte-identical round trips (golden startup-json-source-generation).

/// <summary>The generated metadata for the start-up reads (default options: what each read used before).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<EncodingPreset>))]
[JsonSerializable(typeof(IEnumerable<EncodingPreset>))]
[JsonSerializable(typeof(EncoderProber.SavedResult))]
[JsonSerializable(typeof(DockLayoutState))]
[JsonSerializable(typeof(DockNode))]
// Side windows (AppSettings.SideWindows, read with the dock layout before Frame 1).
[JsonSerializable(typeof(List<SideWindowState>))]
[JsonSerializable(typeof(SideWindowState))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
