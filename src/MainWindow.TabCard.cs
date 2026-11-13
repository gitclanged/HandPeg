using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;

namespace HandPegApp;

// ----- Project tabs: a tab behind scrubbed from its hover card (Pass 5.4.2A; its own file since 5.4.4) -----
public partial class MainWindow
{
    /// <summary>
    /// The pointer across a tab behind with its card open: its overview sheet's picture there, when it keeps one in memory (a frozen
    /// cell of the sheet already held: nothing decoded, nothing asked of the engine). 5.4.4: <paramref name="value"/> is where across the
    /// tab or the card (0 to 1), or with <paramref name="aboutPlayhead"/> an offset from the tab's playhead (a narrowed tab).
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
    /// Where along a tab's Sequence a hover card scrub is (pure, 5.4.4: the golden tab-hover-scrub), 0 to 1: <paramref name="value"/> itself,
    /// or with <paramref name="aboutPlayhead"/> that offset from the middle of the playhead's cell (<paramref name="playheadCell"/> of
    /// <paramref name="count"/>), so the narrowed tab's middle shows the picture the tab is paused on.
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

    /// <summary>The sheet's cell a scrub at <paramref name="fraction"/> shows (pure): the last for the very end.</summary>
    internal static int ScrubCell(int count, double fraction) =>
        count <= 0 || !double.IsFinite(fraction) ? 0 : Math.Max(0, Math.Min((int)(fraction * count), count - 1));
}
