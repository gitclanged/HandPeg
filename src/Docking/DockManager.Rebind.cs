using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace HandPegApp.Docking;

// ----- DockManager: an arrangement taken up in place (Rebind and ShowShares, their own file since 5.4.4) -----
public sealed partial class DockManager
{
    /// <summary>How far apart two shares of a split may be, each as a part of its split's whole, and still be the same (5.4.4): 0.01 %.</summary>
    internal const double ShareTolerance = 1e-4;

    // The splits' shares in a signature (Signature, compact JSON): taken out to see whether two arrangements differ in nothing else.
    private static readonly Regex SizesAttribute = new("\"Sizes\":\\[[^\\]]*\\],?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The arrangement just taken up (Load, Apply, Reset) laid out. The one laid out already is taken up in place (Rebind), and so is one
    /// that differs from it only in its splits' shares and its groups' front tabs (5.4.4, the user's: a project tab brought back with the
    /// same layout, its borders elsewhere, took the whole layout again): the windows stay, the shares are set and a front tab shown where
    /// they differ. Anything else, and anything while a pane is being dragged, is laid out again (Rebuild).
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

    /// <summary>Whether two signatures (or two arrangements as saved) are the same but for their splits' shares (pure, 5.4.4).</summary>
    internal static bool SameButShares(string a, string b) =>
        string.Equals(SizesAttribute.Replace(a, ""), SizesAttribute.Replace(b, ""), StringComparison.Ordinal);

    /// <summary>
    /// The arrangement taken up again is the one laid out (a copy of it, read back from the settings), or one that differs from it only in
    /// its shares and front tabs (5.4.4): the windows stay as they are. The hosts and the splits are always told which nodes of the new
    /// tree they show (never skipped: a border dragged next writes its sizes into the tree that is saved, a tab clicked finds its group's
    /// host, the joined header's bar its part). A group shows another pane only when the one shown now (Render's rule) is not the one the
    /// new tree wants; a split takes the new shares only where they differ. Nothing different, nothing is written: no layout follows.
    /// </summary>
    private void Rebind()
    {
        for (var i = 0; i < _groupHosts.Count; i++)
        {
            var (host, old, side) = _groupHosts[i];
            if (old.Panes.FirstOrDefault() is not { } first || (side is null ? _state.Main : side.State.Tree)?.GroupOf(first) is not { } fresh)
                continue;

            // 5.4.4: compared with the pane shown, not with the old tree's Active, which can name a hidden pane Render passed over.
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

        // 5.4D: the splits laid out are the new tree's too (the same places in it: the tree is the same), so a border dragged next writes
        // its sizes into the arrangement that is saved, not into the one laid out before; and their least sizes are taken again. Each
        // shows the new tree's shares too, so a tree taken up in place always shows its own, whatever was dragged on screen before.
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

    /// <summary>The pane a group shows (pure, 5.4.4): its Active when that is shown, else its first pane shown (Render's rule); null with none shown.</summary>
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
    /// A split's shares as its tree has them, given to its grid's columns or rows in place (5.4D, Rebind): those as wide or as tall as their
    /// content keep their Auto size. 5.4.4: only when they differ from the grid's (<see cref="SharesMatch"/>), so a split taken up as it is
    /// invalidates nothing; and as RenderSplit lays them out, a share under 1 kept (SetDips, for a drag, keeps each at least 1: a split of
    /// 0.5, 0.5 and 1.3 came out 1, 1 and 1.3).
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

    /// <summary>A part's share of its split as RenderSplit lays it out: the tree's, 1 for none.</summary>
    private static double ShareOf(DockNode split, int index) => index < split.Sizes.Count && split.Sizes[index] > 0 ? split.Sizes[index] : 1;

    /// <summary>
    /// Whether a split's shares shown and wanted are the same (pure, 5.4.4): each as a part of its own list's whole, within
    /// <see cref="ShareTolerance"/> (shares are relative: 70/30, 1/1.3, 0.5/0.5 and a drag's DIPs all mean their ratio). A share that is not
    /// a positive number (a part not laid out as a share) never matches.
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

    /// <summary>A part's share as the tree has it, a star of its grid (5.4.4, Rebind only: the drags keep theirs at least 1, SetDips).</summary>
    private static void SetShare(Grid grid, FrameworkElement element, bool vertical, double share)
    {
        var length = new GridLength(share > 0 ? share : 1, GridUnitType.Star);
        if (vertical)
            grid.RowDefinitions[Grid.GetRow(element)].Height = length;
        else
            grid.ColumnDefinitions[Grid.GetColumn(element)].Width = length;
    }
}
