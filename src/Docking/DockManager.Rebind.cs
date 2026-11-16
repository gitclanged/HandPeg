using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace HandPegApp.Docking;

// ----- DockManager: in-place layout rebind (Rebind, ShowShares) -----
public sealed partial class DockManager
{
    /// <summary>Tolerance for equal normalized split shares: 0.01 %.</summary>
    internal const double ShareTolerance = 1e-4;

    // Matches split shares in a Signature (compact JSON); stripped to compare layouts ignoring shares.
    private static readonly Regex SizesAttribute = new("\"Sizes\":\\[[^\\]]*\\],?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Lays out a newly loaded layout (Load, Apply, Reset). Identical layouts, or ones differing only in split shares and active tabs
    /// (e.g. a project tab restored with moved borders), rebind in place: windows stay, shares and active tabs update where different.
    /// Anything else, or any change during a pane drag, rebuilds.
    /// </summary>
    private void RebindOrRebuild()
    {
        var signature = Signature();
        if (signature == _builtSignature)
        {
            Rebind();
            return;
        }

        if (!_dragging && _builtSignature is { } built && SameButShares(built, signature))
        {
            Rebind();
            MarkAllBuilt();
            return;
        }

        Rebuild();
    }

    /// <summary>Whether two signatures (or saved layouts) are equal ignoring split shares (pure).</summary>
    internal static bool SameButShares(string a, string b) =>
        string.Equals(SizesAttribute.Replace(a, ""), SizesAttribute.Replace(b, ""), StringComparison.Ordinal);

    /// <summary>
    /// In-place rebind for a structurally identical layout (shares and active tabs may differ); windows stay. Hosts and splits always
    /// rebind to the new tree's nodes (so later border drags write into the saved tree, tab clicks find their host, the joined header
    /// finds its part). A group switches pane only when its shown pane (Render's rule) differs; splits update only differing shares. No
    /// differences means no writes and no layout pass.
    /// </summary>
    private void Rebind()
    {
        for (var i = 0; i < _groupHosts.Count; i++)
        {
            var (host, old, side) = _groupHosts[i];
            if (old.Panes.FirstOrDefault() is not { } first || (side is null ? _state.Main : side.State.Tree)?.GroupOf(first) is not { } fresh)
                continue;

            // Compare with the shown pane, not the old tree's Active, which may name a hidden pane Render skipped.
            var wanted = fresh.Active;
            var front = FrontOf(old, IsPaneShown);
            _groupHosts[i] = (host, fresh, side);
            if (_joined is { } joined && ReferenceEquals(joined.Under, old))
                _joined = joined with { Under = fresh };
            if (host is TabGroupHost tabs)
                tabs.Group = fresh;
            if (wanted is not null && wanted != front && IsPaneShown(wanted))
            {
                fresh.Active = front;
                Activate(wanted);
            }
            else
            {
                fresh.Active = wanted;
            }
        }

        // Rebind splits to the new tree's nodes (same positions) so later border drags write into the saved layout, re-read minimum
        // sizes, and apply the new tree's shares regardless of prior on-screen drags.
        foreach (var view in _splitViews)
        {
            if ((view.Side is null ? _state.Main : view.Side.State.Tree) is { } root && NodeAt(root, view.Path) is { IsGroup: false } fresh)
            {
                view.Split = fresh;
                ShowShares(view);
            }
        }

        RefreshMinSizes();
    }

    private bool IsPaneShown(string pane) => _panes.TryGetValue(pane, out var known) && known.IsShown();

    /// <summary>Shown pane of a group (pure; Render's rule): Active if visible, else first visible; null when none.</summary>
    internal static string? FrontOf(DockNode group, Func<string, bool> shown)
    {
        string? first = null;
        foreach (var pane in group.Panes)
        {
            if (!shown(pane))
                continue;
            if (pane == group.Active)
                return pane;
            first ??= pane;
        }

        return first;
    }

    /// <summary>
    /// Applies tree shares to the grid's columns/rows in place (Rebind); content-sized parts keep Auto. Only when different
    /// (<see cref="SharesMatch"/>), so an unchanged split invalidates nothing. Shares below 1 are kept as RenderSplit does (SetDips
    /// clamps drags to >= 1, which turned 0.5/0.5/1.3 into 1/1/1.3).
    /// </summary>
    private static void ShowShares(SplitView view)
    {
        var (grid, split) = (view.Grid, view.Split);
        var (shown, wanted) = (new List<double>(), new List<double>());
        foreach (var (element, index, fits) in view.Parts)
        {
            if (fits)
                continue;
            var length = split.Vertical ? grid.RowDefinitions[Grid.GetRow(element)].Height : grid.ColumnDefinitions[Grid.GetColumn(element)].Width;
            shown.Add(length.IsStar ? length.Value : double.NaN);
            wanted.Add(ShareOf(split, index));
        }

        if (SharesMatch(shown, wanted))
            return;

        foreach (var (element, index, fits) in view.Parts)
        {
            if (!fits)
                SetShare(grid, element, split.Vertical, ShareOf(split, index));
        }
    }

    /// <summary>Part share as RenderSplit uses it: tree value, or 1 when absent.</summary>
    private static double ShareOf(DockNode split, int index) => index < split.Sizes.Count && split.Sizes[index] > 0 ? split.Sizes[index] : 1;

    /// <summary>
    /// Whether shown and desired shares match after normalizing each list (pure), within <see cref="ShareTolerance"/> (shares are
    /// ratios: 70/30, 1/1.3, 0.5/0.5 or drag DIPs). Non-positive shares (non-star parts) never match.
    /// </summary>
    internal static bool SharesMatch(IReadOnlyList<double> shown, IReadOnlyList<double> wanted)
    {
        if (shown.Count != wanted.Count)
            return false;

        var (shownSum, wantedSum) = (0.0, 0.0);
        for (var i = 0; i < shown.Count; i++)
        {
            if (!(shown[i] > 0) || !double.IsFinite(shown[i]) || !(wanted[i] > 0) || !double.IsFinite(wanted[i]))
                return false;
            (shownSum, wantedSum) = (shownSum + shown[i], wantedSum + wanted[i]);
        }

        for (var i = 0; i < shown.Count; i++)
        {
            if (Math.Abs(shown[i] / shownSum - wanted[i] / wantedSum) > ShareTolerance)
                return false;
        }

        return true;
    }

    /// <summary>Tree share as a grid star length (Rebind only; drags clamp to >= 1 via SetDips).</summary>
    private static void SetShare(Grid grid, FrameworkElement element, bool vertical, double share)
    {
        var length = new GridLength(share > 0 ? share : 1, GridUnitType.Star);
        if (vertical)
            grid.RowDefinitions[Grid.GetRow(element)].Height = length;
        else
            grid.ColumnDefinitions[Grid.GetColumn(element)].Width = length;
    }
}
