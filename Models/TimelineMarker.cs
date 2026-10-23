namespace HandPegApp.Models;

/// <summary>A marker: a named moment of the sequence, in seconds.</summary>
public sealed record TimelineMarker(double Seconds, string Name);
