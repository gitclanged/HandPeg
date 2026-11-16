using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>A marker: a named moment of the sequence, in seconds.</summary>
public sealed record TimelineMarker(double Seconds, string Name)
{
    /// <summary>
    /// Ping-to-Mark highlight (Ctrl+J, or Alt+click on the player), drawn in the highlight colour; omitted when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Highlight { get; init; }

    /// <summary>
    /// Pinged focal point (Alt+click) in frame fractions 0-1; null (omitted) when none. Target of the Zoom-Hold macro.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FocalX { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FocalY { get; init; }
}
