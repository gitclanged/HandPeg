using System.Text.Json;

namespace HandPegApp.Models;

/// <summary>
/// The fields of a project file that this version does not know, kept per object of the file: a newer HandPeg's additions (or
/// fields an older format wrote and this one retired). Read with the project (<c>[JsonExtensionData]</c> on each object), carried
/// with its timeline (<see cref="Timeline.TimelineDocument.CarriedProjectFields"/>) and written back on the next save, so opening
/// and saving a project made by a newer version does not drop what that version stored. The timeline, its tracks, clips and
/// looks carry their own unknown fields on their own objects; these are the objects the editor rebuilds from its own state when
/// it saves (the file header, source, output, sequence, background and export settings).
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
