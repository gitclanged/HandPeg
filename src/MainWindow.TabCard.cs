using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Project tabs: hover-card scrubbing of background tabs -----
public partial class MainWindow
{
    /// <summary>
    /// Hover scrub over a background tab with its card open: shows the in-memory overview sheet cell at that point (frozen cell, no decode,
    /// no engine request). <paramref name="value"/> is the 0..1 position across the tab or card, or with <paramref name="aboutPlayhead"/>
    /// an offset from the tab's playhead (narrowed tab).
    /// </summary>
    private void ScrubTabCard(ProjectTabItem item, double value, bool aboutPlayhead)
    {
        if (!_hydrated.TryGetValue(item.Id, out var live) || live.ProgramSheet is not { Count: > 0 } sheet)
            return;
        if (!ReferenceEquals(item, ProjectTabStrip.HoverItem))
            return;

        var playheadCell = aboutPlayhead && _tabs.Find(item.Id) is { } tab ? sheet.IndexAt(tab.PlayheadMs / 1000) : 0;
        var fraction = ScrubFraction(sheet.Count, playheadCell, value, aboutPlayhead);
        var index = ScrubCell(sheet.Count, fraction);
        if (item.IsScrubbing && ScrubCell(sheet.Count, item.ScrubFraction) == index)
        {
            item.ScrubFraction = fraction;
            return;
        }

        item.Preview = sheet.Cell(index);
        item.ScrubFraction = fraction;
    }

    /// <summary>
    /// Scrub position along the tab's Sequence, 0..1 (pure; tab-hover-scrub golden): <paramref name="value"/> clamped, or with
    /// <paramref name="aboutPlayhead"/> offset from the centre of the playhead cell (<paramref name="playheadCell"/> of
    /// <paramref name="count"/>), so a narrowed tab's centre shows its paused frame.
    /// </summary>
    internal static double ScrubFraction(int count, int playheadCell, double value, bool aboutPlayhead)
    {
        if (!double.IsFinite(value))
            return 0;
        if (!aboutPlayhead || count <= 0)
            return Math.Max(0, Math.Min(value, 1));
        var centre = (Math.Max(0, Math.Min(playheadCell, count - 1)) + 0.5) / count;
        return Math.Max(0, Math.Min(centre + value, 1));
    }

    /// <summary>Sheet cell index for a scrub at <paramref name="fraction"/> (pure); 1.0 maps to the last cell.</summary>
    internal static int ScrubCell(int count, double fraction) =>
        count <= 0 || !double.IsFinite(fraction) ? 0 : Math.Max(0, Math.Min((int)(fraction * count), count - 1));
}
