using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>
/// A section of the source that is kept in the output.
/// </summary>
public sealed partial class CutSegment(TimeSpan start, TimeSpan end) : ObservableObject
{
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;

    public TimeSpan Duration => End - Start;

    public string Display =>
        $"{FormatTime(Start)}  →  {FormatTime(End)}    ({Duration.TotalSeconds:0.###} s)";

    /// <summary>Briefly true after the segment's times were adjusted automatically, so the list can highlight it.</summary>
    [ObservableProperty] private bool _isFlashing;

    // Also what screen readers announce for the segment's row in the cut list.
    public override string ToString() => Display;

    public static string FormatTime(TimeSpan time) => TimeDisplay.Format(time);

    /// <summary>Call after the time format or the frame rate changed, so the row is rewritten.</summary>
    public void RefreshDisplay() => OnPropertyChanged(nameof(Display));
}
