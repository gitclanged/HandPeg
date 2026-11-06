using System.Text.Json.Serialization;

namespace HandPegApp.Models;

/// <summary>A marker: a named moment of the sequence, in seconds.</summary>
public sealed record TimelineMarker(double Seconds, string Name)
{
    /// <summary>
    /// A Ping-to-Mark highlight (5.2C: Ctrl+J, or Alt + a click on the player's picture): drawn in the highlight colour. Left out of the
    /// file while false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Highlight { get; init; }

    /// <summary>
    /// Where on the picture the highlight was pinged (5.2C: Alt + a click), as fractions of the frame (0 to 1 across and down); null for a
    /// marker with none, and so left out of the file. What Pass 5.3B's Zoom-Hold macro aims at.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FocalX { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FocalY { get; init; }
}
