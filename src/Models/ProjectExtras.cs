using System.Text.Json;

namespace HandPegApp.Models;

/// <summary>
/// Unknown project fields (newer-version additions or retired fields) per rebuilt object (header, source, output, sequence, background,
/// export settings), captured via <c>[JsonExtensionData]</c>, carried on <see cref="Timeline.TimelineDocument.CarriedProjectFields"/>
/// and re-emitted on save so round trips preserve them. Timeline, tracks, clips and looks keep their own unknown fields.
/// </summary>
public sealed record ProjectExtras(
    Dictionary<string, JsonElement>? File,
    Dictionary<string, JsonElement>? Source,
    Dictionary<string, JsonElement>? Output,
    Dictionary<string, JsonElement>? Sequence,
    Dictionary<string, JsonElement>? Background,
    Dictionary<string, JsonElement>? Settings)
{
    /// <summary>The extras of a file, or null when it had none (the usual case).</summary>
    public static ProjectExtras? Of(
        Dictionary<string, JsonElement>? file, Dictionary<string, JsonElement>? source, Dictionary<string, JsonElement>? output,
        Dictionary<string, JsonElement>? sequence, Dictionary<string, JsonElement>? background, Dictionary<string, JsonElement>? settings)
    {
        static Dictionary<string, JsonElement>? Some(Dictionary<string, JsonElement>? fields) => fields is { Count: > 0 } ? fields : null;
        var extras = new ProjectExtras(Some(file), Some(source), Some(output), Some(sequence), Some(background), Some(settings));
        return extras is { File: null, Source: null, Output: null, Sequence: null, Background: null, Settings: null } ? null : extras;
    }
}
