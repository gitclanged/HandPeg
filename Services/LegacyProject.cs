using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HandPegApp.Models;

namespace HandPegApp.Services;

/// <summary>
/// HandPeg 1.x's projects (format 1): a .txt in the projects folder holding the window's state as it then was, in
/// PascalCase JSON, with no format marker. Recognised by its shape, never by its extension alone, and brought over as
/// far as it still maps: the source, the output, the cuts, and every setting whose name and kind of value are still
/// the same (the layers and the caption box among them). Whatever no longer fits is left at its default, a value at a
/// time, so that one setting that changed never costs the rest.
/// </summary>
public static class LegacyProject
{
    // A format 1 project is a few kilobytes; a text file much larger than this is something else, and is not read to find out.
    private const long LargestFile = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Whether a file is a format 1 project: small enough, and of its shape.</summary>
    public static bool IsLegacyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info is { Exists: true, Length: > 2 and <= LargestFile } && Parse(File.ReadAllText(path)) is { } root && IsLegacy(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The shape of a format 1 project: no format marker, the source as text, the settings as an object, and the cuts as a
    /// list, with the downloaded file or the output beside them. Text files that merely hold JSON do not have all of that.
    /// </summary>
    public static bool IsLegacy(JsonObject root) =>
        !root.Any(p => p.Key.Equals("format", StringComparison.OrdinalIgnoreCase))
        && Get(root, "SourcePath") is JsonValue source && source.GetValueKind() == JsonValueKind.String
        && Get(root, "Settings") is JsonObject
        && Get(root, "Segments") is JsonArray
        && (Get(root, "LocalMediaPath") is JsonValue { } local && local.GetValueKind() == JsonValueKind.String
            || Get(root, "DestinationPath") is JsonValue { } output && output.GetValueKind() == JsonValueKind.String);

    /// <summary>The project a format 1 file holds, or null when the text is not one.</summary>
    public static ProjectState? TryRead(string json) => Parse(json) is { } root && IsLegacy(root) ? Read(root) : null;

    private static ProjectState Read(JsonObject root)
    {
        var state = new ProjectState();
        Fill(state, root, skip: ["Segments", "Settings"]);
        state.Settings = Get(root, "Settings") is JsonObject settings && Convert(settings, typeof(EncodingPreset)) is EncodingPreset preset ? preset : state.Settings;

        // Renamed since: the cuts snapped to "keyframes", which HandPeg now calls I-frames.
        if (Get(root, "SnapToKeyframes") is JsonValue snap && snap.TryGetValue<bool>(out var snapping))
            state.SnapToIFrames = snapping;

        // A 1.x shadow was moved as far right as down: brought over as the distance, angle and softness it is now.
        foreach (var layer in state.Settings.Layers.Append(state.Settings.MainLayer).Append(state.Settings.CaptionLayer))
            layer?.FoldEarlierShadow();

        state.Segments = ReadSegments(Get(root, "Segments") as JsonArray);
        if (state.LocalMediaPath.Length == 0)
            state.LocalMediaPath = state.SourcePath;
        if (state.Settings.Name.Length == 0)
            state.Settings.Name = state.Name;
        return state;
    }

    /// <summary>
    /// The cuts, as milliseconds ({ StartMs, EndMs }, as HandPeg writes them now) or as times ({ Start, End } in hh:mm:ss.fff);
    /// a cut that does not end after it starts is dropped.
    /// </summary>
    private static List<SegmentState> ReadSegments(JsonArray? cuts)
    {
        var segments = new List<SegmentState>();
        foreach (var cut in (cuts ?? []).OfType<JsonObject>())
        {
            var start = Milliseconds(Get(cut, "StartMs")) ?? Time(Get(cut, "Start"));
            var end = Milliseconds(Get(cut, "EndMs")) ?? Time(Get(cut, "End"));
            var skipped = (Get(cut, "Skipped") ?? Get(cut, "IsSkipped")) is JsonValue flag && flag.TryGetValue<bool>(out var isSkipped) && isSkipped;
            if (start is { } from && end is { } to && to > from && from >= 0)
                segments.Add(new SegmentState(from, to, skipped));
        }

        return segments.OrderBy(s => s.StartMs).ToList();
    }

    private static double? Milliseconds(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) ? number : null;

    private static double? Time(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time)
            ? time.TotalMilliseconds
            : null;

    // ----- Taking over what still fits -----

    /// <summary>
    /// Sets each writable property of <paramref name="target"/> that the JSON names (in any case) to its value there, when
    /// that converts to the property's type; an object or a list of objects that does not convert whole is taken a value at a time.
    /// </summary>
    private static void Fill(object target, JsonObject source, IReadOnlyCollection<string>? skip = null)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0 || skip?.Contains(property.Name, StringComparer.OrdinalIgnoreCase) == true)
                continue;
            if (Get(source, property.Name) is not { } node)
                continue;
            if (Convert(node, property.PropertyType) is { } value)
                property.SetValue(target, value);
        }
    }

    /// <summary>A JSON value as an object of the type, whole if it converts, else piece by piece; null when nothing of it does.</summary>
    private static object? Convert(JsonNode node, Type type)
    {
        try
        {
            return node.Deserialize(type, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Something in it no longer fits: what does is taken below.
        }

        if (node is JsonObject inner && Create(type) is { } made)
        {
            Fill(made, inner);
            return made;
        }

        if (node is JsonArray items && ListElement(type) is { } element && Activator.CreateInstance(type) is IList list)
        {
            foreach (var item in items)
            {
                if (item is not null && Convert(item, element) is { } converted)
                    list.Add(converted);
            }

            return list;
        }

        return null;
    }

    /// <summary>A new object of a class that can be filled a property at a time; null for anything else.</summary>
    private static object? Create(Type type)
    {
        if (!type.IsClass || type == typeof(string) || typeof(IEnumerable).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
            return null;

        try
        {
            return Activator.CreateInstance(type);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>The element type of a List&lt;T&gt;, or null for any other type.</summary>
    private static Type? ListElement(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;

    /// <summary>A property of a JSON object by its name in any case: format 1 wrote PascalCase.</summary>
    private static JsonNode? Get(JsonObject source, string name)
    {
        if (source.TryGetPropertyValue(name, out var exact))
            return exact;
        foreach (var (key, value) in source)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
