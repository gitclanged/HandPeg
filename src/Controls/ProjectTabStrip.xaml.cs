using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>One tab of the title bar's strip, as it is drawn (its header lives in Services/ProjectTabs.cs).</summary>
public sealed partial class ProjectTabItem : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(ToolTipText), nameof(FrontToolTip), nameof(Initial))]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText), nameof(FrontToolTip), nameof(SavedText), nameof(HasFile))]
    private string _projectPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText), nameof(FrontToolTip), nameof(SavedText))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrontToolTip))]
    private bool _isActive;

    /// <summary>Whether it is being dragged along the strip now (Pass 5.4B): drawn faded.</summary>
    [ObservableProperty] private bool _isDragged;

    /// <summary>What its hover card shows of it (Pass 5.4B): its paused still or saved thumbnail, given by the window as the card opens and let go as it closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardWidth))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(ScrubWidth))]
    private ImageSource? _preview;

    /// <summary>
    /// Where along its Sequence the pointer scrubs its hover card (5.4.2A: 0 to 1 across the tab, its overview sheet's picture there shown);
    /// NaN while it does not.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScrubWidth))]
    [NotifyPropertyChangedFor(nameof(IsScrubbing))]
    private double _scrubFraction = double.NaN;

    /// <summary>Whether the hover card is being scrubbed (its bar shown).</summary>
    public bool IsScrubbing => !double.IsNaN(ScrubFraction);

    /// <summary>How long the hover card's scrub bar is, DIPs: the card's width as far as the scrub has come.</summary>
    public double ScrubWidth => IsScrubbing ? Math.Clamp(ScrubFraction, 0, 1) * CardWidth : 0;

    /// <summary>The widest and the tallest a hover card's picture is drawn, in DIPs (5.4C): 16:9 comes out 318 x 179, 9:16 191 x 340.</summary>
    public const double CardMaxWidth = 318, CardMaxHeight = 340;

    /// <summary>How wide its hover card is: its picture's own shape fitted in <see cref="CardMaxWidth"/> x <see cref="CardMaxHeight"/>
    /// (5.4C, the user's: a vertical project's card is tall, never its picture cropped to 16:9); <see cref="CardMaxWidth"/> with none.</summary>
    public double CardWidth => CardFit(Preview?.Width ?? 0, Preview?.Height ?? 0).Width;

    /// <summary>How tall its hover card's picture is (0 with none).</summary>
    public double PictureHeight => CardFit(Preview?.Width ?? 0, Preview?.Height ?? 0).Height;

    /// <summary>A picture of <paramref name="width"/> x <paramref name="height"/> fitted whole in the card's largest box, its shape kept (pure).</summary>
    public static Size CardFit(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return new Size(CardMaxWidth, 0);
        var scale = Math.Min(CardMaxWidth / width, CardMaxHeight / height);
        return new Size(Math.Round(width * scale), Math.Round(height * scale));
    }

    /// <summary>Its frame and frame rate in a line for its hover card ("1920 x 1080 · 60 fps"; "" while not known).</summary>
    [ObservableProperty] private string _details = "";

    /// <summary>Whether the strip has room to show it (<see cref="ProjectTabStrip.Fit"/>); one it has not is in the overflow list.</summary>
    [ObservableProperty] private bool _isShown = true;

    /// <summary>What a tab narrowed below its name shows: the name's first letter or digit (a dot for a name with none).</summary>
    public string Initial
    {
        get
        {
            foreach (var c in Title)
            {
                if (char.IsLetterOrDigit(c))
                    return char.ToUpper(c, CultureInfo.CurrentCulture).ToString();
            }

            return "\u00b7";
        }
    }

    /// <summary>What UI Automation calls the tab: "Project Tab: My Project".</summary>
    public string AutomationName => $"Project Tab: {Title}";

    /// <summary>The tab's tooltip: its name, its file, whether it has unsaved changes, and what a middle click does.</summary>
    public string ToolTipText =>
        (ProjectPath.Length > 0 ? $"{Title}\n{ProjectPath}" : $"{Title}\nNot saved as a project yet")
        + (IsDirty ? "\nUnsaved changes" : "")
        + "\nMiddle-click to close";

    /// <summary>The tooltip the tab carries: the tab in front its text (Pass 5.4B); a tab behind none, its hover card shows it instead.</summary>
    public string? FrontToolTip => IsActive ? ToolTipText : null;

    /// <summary>Its hover card's last line: saved or not, and its file.</summary>
    public string SavedText => ProjectPath.Length == 0 ? "Not saved as a project yet" : (IsDirty ? "Unsaved changes\n" : "Saved\n") + ProjectPath;

    /// <summary>Whether it has a project file (Open Containing Folder).</summary>
    public bool HasFile => ProjectPath.Length > 0;
}

/// <summary>What a tab's right-click menu asks the window for (Pass 5.4B).</summary>
public enum ProjectTabCommand
{
    Save,
    SaveAs,
    Rename,
    Duplicate,
    OpenFolder,
    Close,
    CloseOthers,
}

/// <summary>One line of a tab's right-click menu (<see cref="ProjectTabStrip.MenuFor"/>): its label (with its key), what it asks for, whether it may be chosen; a null command is a separator.</summary>
internal readonly record struct ProjectTabMenuLine(string Header, ProjectTabCommand? Command, bool Enabled);

/// <summary>
/// How the strip lays its tabs out in the room it is given (<see cref="ProjectTabStrip.LayoutFor"/>): how wide each is drawn at most,
/// whether they are narrowed to their dot and initial, and the run of tabs shown (from <c>First</c>, <c>Shown</c> of them).
/// </summary>
internal readonly record struct ProjectTabLayout(double TabWidth, bool Compact, int First, int Shown);

/// <summary>
/// The title bar's browser-style project tabs (Pass 5.4A): a button per open project and a + after them. It only draws and says what
/// was clicked; the window switches, opens and closes tabs (MainWindow.ProjectTabs.cs). It starts collapsed and empty (Pass 5.4B: the main
/// window shows it once a project is started or opened). It never grows past the room it is given (the caption to drag the window by stays
/// free right of +): tabs share it down to <see cref="MinTabWidth"/> with their names, then narrow to their dot and initial down to
/// <see cref="CompactTabWidth"/>, and past that the strip shows a run of tabs round the one in front and a count of the rest, which opens
/// a list of them (Ctrl+Tab goes round them all). A tab can be dragged along the strip, has a right-click menu (save, rename, duplicate,
/// its folder, close), and a tab behind shows a hover card with its picture, frame and saved state.
/// </summary>
public partial class ProjectTabStrip : UserControl
{
    /// <summary>The narrowest a tab is drawn with its name and close button, the widest, and the narrowest with its dot and initial alone, in DIPs.</summary>
    public const double MinTabWidth = 72, MaxTabWidth = 200, CompactTabWidth = 28;

    /// <summary>The gap after each tab (its right margin), the + button's width and the overflow count's, in DIPs (as ProjectTabStrip.xaml draws them).</summary>
    public const double TabGap = 2, NewTabWidth = 24, OverflowWidth = 28;

    public static readonly DependencyProperty TabMaxWidthProperty =
        DependencyProperty.Register(nameof(TabMaxWidth), typeof(double), typeof(ProjectTabStrip), new FrameworkPropertyMetadata(MaxTabWidth));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(ProjectTabStrip), new FrameworkPropertyMetadata(false));

    // The room last given (NaN before the title bar's first layout): the strip is laid out again in it as tabs come, go or come to the front.
    private double _available = double.NaN;

    public ProjectTabStrip()
    {
        InitializeComponent();
        Items.ItemsSource = Tabs;

        // A tab dragged along the strip: the strip holds the mouse while it is (Pass 5.4B).
        PreviewMouseMove += Strip_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Strip_PreviewMouseLeftButtonUp;
        LostMouseCapture += (_, e) =>
        {
            // The strip's own capture lost (a tab's, bubbled up as the strip takes it from the tab's button, is not the end of a drag).
            if (ReferenceEquals(e.OriginalSource, this))
                EndReorder();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                CloseFlyouts();
        };
    }

    /// <summary>The tabs as they are drawn, in their order.</summary>
    public ObservableCollection<ProjectTabItem> Tabs { get; } = [];

    /// <summary>How wide each tab may be drawn now (<see cref="Fit"/>).</summary>
    public double TabMaxWidth
    {
        get => (double)GetValue(TabMaxWidthProperty);
        set => SetValue(TabMaxWidthProperty, value);
    }

    /// <summary>Whether the tabs are narrowed to their dot and initial now (<see cref="Fit"/>): their names are in their tooltips, and a middle click still closes one.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>A tab was clicked (to bring it to the front).</summary>
    public event Action<ProjectTabItem>? TabChosen;

    /// <summary>A tab's × was clicked, or the tab was middle-clicked (to close it).</summary>
    public event Action<ProjectTabItem>? TabClosing;

    /// <summary>+ was clicked (a new project tab).</summary>
    public event Action? NewTabAsked;

    /// <summary>A tab dragged into another place along the strip (Pass 5.4B): the tab, and its index in <see cref="Tabs"/> now (already moved there).</summary>
    public event Action<ProjectTabItem, int>? TabMoved;

    /// <summary>A drag along the strip let go (the window writes the order down once, here).</summary>
    public event Action? ReorderEnded;

    /// <summary>A line of a tab's right-click menu chosen (but Rename Project..., which is <see cref="TabRenamed"/>).</summary>
    public event Action<ProjectTabItem, ProjectTabCommand>? TabCommand;

    /// <summary>A tab's new name typed in its right-click menu and taken with Enter (never as each key is typed).</summary>
    public event Action<ProjectTabItem, string>? TabRenamed;

    /// <summary>A tab behind's hover card about to open: the window gives it its picture and frame (<see cref="ProjectTabItem.Preview"/>, <see cref="ProjectTabItem.Details"/>).</summary>
    public event Action<ProjectTabItem>? PreviewAsked;

    /// <summary>Whether a tab is being dragged along the strip now: the window leaves the strip's order and layout alone until it is let go.</summary>
    public bool IsReordering { get; private set; }

    /// <summary>The tab whose hover card is open now (null for none): a picture that comes late is given only to it.</summary>
    public ProjectTabItem? HoverItem { get; private set; }

    /// <summary>The tabs share <paramref name="available"/> DIPs (what the title bar leaves between its name and its buttons, less a caption to drag by), never more.</summary>
    public void Fit(double available)
    {
        _available = available;
        var active = 0;
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].IsActive)
                active = i;
        }

        var layout = LayoutFor(available, Tabs.Count, active);
        if (Math.Abs(layout.TabWidth - TabMaxWidth) > 0.5)
            TabMaxWidth = layout.TabWidth;
        if (Compact != layout.Compact)
            Compact = layout.Compact;
        for (var i = 0; i < Tabs.Count; i++)
            Tabs[i].IsShown = i >= layout.First && i < layout.First + layout.Shown;

        var hidden = Tabs.Count - layout.Shown;
        var count = hidden.ToString(CultureInfo.InvariantCulture);
        OverflowButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverflowButton.Content = "+" + count;
        OverflowButton.ToolTip = hidden == 1 ? "1 more project tab (Ctrl+Tab goes round them all)" : $"{count} more project tabs (Ctrl+Tab goes round them all)";
        if (hidden == 0)
            OverflowPopup.IsOpen = false;
    }

    /// <summary>Laid out again in the room last given, as tabs come, go or come to the front (no room given yet: nothing to do).</summary>
    public void Refit()
    {
        if (double.IsFinite(_available))
            Fit(_available);
    }

    /// <summary>
    /// How <paramref name="count"/> tabs, the one at <paramref name="active"/> in front, are laid out in <paramref name="available"/> DIPs
    /// (pure: titlebar-tab-overflow): an equal share each with their names, from <see cref="MinTabWidth"/> to <see cref="MaxTabWidth"/>;
    /// below that, narrowed to their dot and initial down to <see cref="CompactTabWidth"/>; below that, as many as fit after the overflow
    /// count, a run round the one in front. The tabs shown with their gaps, the count and + never take more than <paramref name="available"/>
    /// (once it holds + and the count at all).
    /// </summary>
    internal static ProjectTabLayout LayoutFor(double available, int count, int active)
    {
        if (count <= 0 || !double.IsFinite(available))
            return new ProjectTabLayout(MaxTabWidth, false, 0, Math.Max(count, 0));

        var room = Math.Max(0, available - NewTabWidth);
        var share = Math.Floor(room / count) - TabGap;
        if (share >= MinTabWidth)
            return new ProjectTabLayout(Math.Min(share, MaxTabWidth), false, 0, count);
        if (share >= CompactTabWidth)
            return new ProjectTabLayout(share, true, 0, count);

        // A run of tabs round the one in front, the rest behind the overflow count.
        var forTabs = Math.Max(0, room - OverflowWidth);
        var shown = (int)Math.Min(count - 1, Math.Floor(forTabs / (CompactTabWidth + TabGap)));
        if (shown <= 0)
            return new ProjectTabLayout(CompactTabWidth, true, 0, 0);
        var width = Math.Clamp(Math.Floor(forTabs / shown) - TabGap, CompactTabWidth, MinTabWidth - 1);
        var first = Math.Clamp(Math.Clamp(active, 0, count - 1) - shown / 2, 0, count - shown);
        return new ProjectTabLayout(width, true, first, shown);
    }

    /// <summary>The tooltip of +, with its key (given after the first frame: the key map is not read before it).</summary>
    public void SetGestures(string newTab) => NewTabButton.ToolTip = newTab;

    private static ProjectTabItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ProjectTabItem;

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            TabChosen?.Invoke(item);
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || ItemOf(sender) is not { } item)
            return;
        e.Handled = true;
        CloseHoverCard();
        TabClosing?.Invoke(item);
    }

    /// <summary>The hover card and the overflow list closed (the strip collapsed, a drag begun, a menu opened).</summary>
    public void CloseFlyouts()
    {
        CloseHoverCard();
        OverflowPopup.IsOpen = false;
    }

    // ----- Dragging a tab along the strip (Pass 5.4B) -----
    // A press on a tab is its button's (a click brings it to the front). Moved further than the system's drag distance with the button
    // held, the strip takes the mouse from the tab's button (so no click follows) and the tab follows the pointer from place to place among
    // the tabs shown: each change of place is made at once in Tabs and told to the window (TabMoved), which writes the order down once the
    // tab is let go (ReorderEnded). The strip holds the mouse, never the tab: a tab moved in the ItemsControl is taken out of the tree and
    // put back, and would lose it. Nothing gets a background: the gaps between the tabs stay the window's caption.

    private ProjectTabItem? _pressed;
    private Point _pressedAt;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseHoverCard();
        if (!IsReordering && ItemOf(sender) is { } item)
            (_pressed, _pressedAt) = (item, e.GetPosition(Items));
    }

    private void Strip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } item)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Let go where the strip did not hear it (another window took the mouse): the drag ends there.
            _pressed = null;
            EndReorder();
            return;
        }

        var at = e.GetPosition(Items);
        if (!IsReordering)
        {
            if (Math.Abs(at.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance)
                return;
            if (!BeginReorder(item))
            {
                _pressed = null;
                return;
            }

            if (!CaptureMouse())
            {
                EndReorder();
                return;
            }
        }

        ReorderTo(item, at.X);
    }

    /// <summary>A drag of <paramref name="item"/> begun (titlebar-tab-drag drives it from here): drawn faded, the flyouts closed. False for a tab not on the strip.</summary>
    internal bool BeginReorder(ProjectTabItem item)
    {
        if (IsReordering || !Tabs.Contains(item))
            return false;
        IsReordering = true;
        item.IsDragged = true;
        CloseFlyouts();
        return true;
    }

    private void Strip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsReordering)
        {
            _pressed = null;
            return;
        }

        e.Handled = true;
        EndReorder();
    }

    /// <summary>The dragged tab put where the pointer is (<paramref name="x"/>, along the tabs) among the tabs shown (the middles of the others, as they are drawn now).</summary>
    internal void ReorderTo(ProjectTabItem item, double x)
    {
        if (!IsReordering)
            return;
        var from = Tabs.IndexOf(item);
        if (from < 0)
        {
            EndReorder();
            return;
        }

        var shown = new List<(int Index, double Middle)>();
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].IsShown && Items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement { ActualWidth: > 0 } box)
                shown.Add((i, box.TranslatePoint(new Point(box.ActualWidth / 2, 0), Items).X));
        }

        var to = ReorderIndex(from, shown, x);
        if (to == from)
            return;
        Tabs.Move(from, to);
        TabMoved?.Invoke(item, to);
    }

    /// <summary>
    /// Where a tab dragged from <paramref name="from"/> goes with the pointer at <paramref name="x"/> (pure: project-tabs-reorder): before
    /// the first of the other tabs shown whose middle is right of the pointer, else after the last of them. <paramref name="shown"/> is the
    /// tabs shown, in order, each its index in the strip and its middle along it; the tabs in the overflow list are never passed over.
    /// </summary>
    internal static int ReorderIndex(int from, IReadOnlyList<(int Index, double Middle)> shown, double x)
    {
        var others = shown.Where(s => s.Index != from).ToList();
        if (others.Count == 0)
            return from;

        var before = others.Count(s => s.Middle < x);
        if (before < others.Count)
        {
            var next = others[before].Index;
            return from < next ? next - 1 : next;
        }

        var last = others[^1].Index;
        return from < last ? last : last + 1;
    }

    /// <summary>The drag let go (or the strip's hold on the mouse lost): the tab drawn as the others, the strip laid out again, the order written down.</summary>
    internal void EndReorder()
    {
        _pressed = null;
        if (!IsReordering)
            return;

        IsReordering = false;
        foreach (var tab in Tabs)
            tab.IsDragged = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Refit();
        ReorderEnded?.Invoke();
    }

    // ----- A tab's right-click menu (Pass 5.4B) -----

    private void Tab_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsReordering || ItemOf(sender) is not { } item || sender is not FrameworkElement tab)
            return;
        e.Handled = true;
        CloseFlyouts();
        OpenTabMenu(tab, item);
    }

    /// <summary>
    /// The lines of a tab's right-click menu (pure: project-tabs-menu), in order; a null command is a separator. The keys are shown
    /// beside the lines they do for the tab in front only (Ctrl+S and Ctrl+W act on the tab in front), and read as the menu opens, so a
    /// key rebound since shows as it is now.
    /// </summary>
    internal static IReadOnlyList<ProjectTabMenuLine> MenuFor(ProjectTabItem item, int count)
    {
        string Keyed(string label, string action) => item.IsActive ? KeybindManager.WithGesture(label, action) : label;
        return
        [
            new(Keyed("Save Project", "file.save"), ProjectTabCommand.Save, true),
            new(Keyed("Save Project As...", "file.saveAs"), ProjectTabCommand.SaveAs, true),
            new("Rename Project...", ProjectTabCommand.Rename, true),
            new("Duplicate Project in New Tab", ProjectTabCommand.Duplicate, true),
            new("Open Containing Folder", ProjectTabCommand.OpenFolder, item.HasFile),
            new("", null, false),
            new(Keyed("Close Tab", "tab.close"), ProjectTabCommand.Close, true),
            new("Close Other Tabs", ProjectTabCommand.CloseOthers, count > 1),
        ];
    }

    /// <summary>A tab's right-click menu, at the mouse as every right-click menu of HandPeg's is, made as it opens (none is kept).</summary>
    private void OpenTabMenu(FrameworkElement tab, ProjectTabItem item)
    {
        var menu = new ContextMenu { PlacementTarget = tab, Placement = PlacementMode.MousePoint };
        foreach (var line in MenuFor(item, Tabs.Count))
        {
            if (line.Command is not { } command)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var entry = new MenuItem { Header = line.Header, IsEnabled = line.Enabled };
            if (command == ProjectTabCommand.Rename)
            {
                entry.StaysOpenOnClick = true;
                entry.Click += (_, _) => BeginRename(menu, entry, item);
            }
            else
            {
                entry.Click += (_, _) => TabCommand?.Invoke(item, command);
            }

            menu.Items.Add(entry);
        }

        menu.IsOpen = true;
    }

    /// <summary>Rename Project... chosen: the line becomes a name box; Enter renames (once, never as each key is typed), Escape leaves it.</summary>
    private void BeginRename(ContextMenu menu, MenuItem entry, ProjectTabItem item)
    {
        if (entry.Header is not string)
            return;

        var box = new TextBox { Text = item.Title, MinWidth = 190, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var name = box.Text.Trim();
                menu.IsOpen = false;
                if (name.Length > 0 && !string.Equals(name, item.Title, StringComparison.Ordinal))
                    TabRenamed?.Invoke(item, name);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                menu.IsOpen = false;
            }
        };
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(box);
        entry.Header = line;
        entry.ToolTip = "Enter renames the project (its file too, once it is saved); Escape leaves it as it is.";
    }

    // ----- A tab behind's hover card (Pass 5.4B) -----
    // The pointer on a tab behind the one in front opens a card under it at once (5.4.2A; 500 ms before): its paused still or the thumbnail
    // its file carries, its frame and frame rate, and whether it is saved. Gliding along the strip moves the one card from tab to tab, and
    // across a tab scrubs its overview sheet (HoverScrubbed: pictures already in memory, nothing decoded). The window gives the picture
    // (PreviewAsked); the card lets it go as it closes, so at most the shown one is held. The tab in front keeps its text tooltip instead.

    private bool _hoverOwnerHooked;

    // Counted up at every tab the pointer enters: a leave closes the card only when no other tab was entered meanwhile.
    private int _hoverEntered;

    /// <summary>The pointer across a tab behind whose card is open (5.4.2A): where along it, 0 to 1. The window shows that picture of its Sequence.</summary>
    public event Action<ProjectTabItem, double>? HoverScrubbed;

    private void Tab_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverEntered++;
        if (IsReordering || e.LeftButton == MouseButtonState.Pressed || ItemOf(sender) is not { IsActive: false } item || sender is not FrameworkElement tab
            || !tab.IsVisible || !Tabs.Contains(item))
        {
            CloseHoverCard();
            return;
        }

        OpenHoverCard(item, tab);
    }

    private void Tab_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ItemOf(sender) is not { } item || !ReferenceEquals(item, HoverItem))
            return;

        // Onto the next tab (its enter comes after this leave): the card moves there instead of closing and opening again.
        var entered = _hoverEntered;
        Dispatcher.BeginInvoke(() =>
        {
            if (entered == _hoverEntered && ReferenceEquals(item, HoverItem))
                CloseHoverCard();
        }, DispatcherPriority.Input);
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (ItemOf(sender) is not { } item || !ReferenceEquals(item, HoverItem) || sender is not FrameworkElement { ActualWidth: > 1 } tab
            || e.LeftButton == MouseButtonState.Pressed)
            return;

        HoverScrubbed?.Invoke(item, Math.Clamp(e.GetPosition(tab).X / tab.ActualWidth, 0, 1));
    }

    private void OpenHoverCard(ProjectTabItem item, FrameworkElement tab)
    {
        // The window let go (Alt+Tab with the pointer still on the tab): the card goes with it, never left over another program.
        if (!_hoverOwnerHooked && Window.GetWindow(this) is { } owner)
        {
            owner.Deactivated += (_, _) => CloseFlyouts();
            _hoverOwnerHooked = true;
        }

        if (ReferenceEquals(item, HoverItem) && HoverCard.IsOpen)
            return;

        // Another tab's card open: its picture let go, and the card moved.
        if (HoverItem is { } shown)
            (shown.Preview, shown.ScrubFraction) = (null, double.NaN);
        HoverItem = item;
        PreviewAsked?.Invoke(item);
        HoverCard.DataContext = item;
        UiScale.PlaceAgainst(HoverCard, tab, UiScale.FlyoutSide.BelowStart, 4);
        HoverCard.IsOpen = true;
    }

    /// <summary>The hover card closed (or kept from opening), its picture let go.</summary>
    public void CloseHoverCard()
    {
        if (HoverItem is { } shown)
            (shown.Preview, shown.ScrubFraction) = (null, double.NaN);
        HoverItem = null;
        HoverCard.IsOpen = false;
        HoverCard.DataContext = null;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // The × lies inside its tab: its click is not also the tab's.
        e.Handled = true;
        if (ItemOf(sender) is { } item)
            TabClosing?.Invoke(item);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTabAsked?.Invoke();

    /// <summary>The overflow count: the tabs the strip has no room for, listed under it (placed by UiScale.PlaceAgainst, never mirrored).</summary>
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        if (OverflowPopup.IsOpen)
        {
            OverflowPopup.IsOpen = false;
            return;
        }

        OverflowItems.ItemsSource = Tabs.Where(t => !t.IsShown).ToList();
        UiScale.PlaceAgainst(OverflowPopup, OverflowButton, UiScale.FlyoutSide.BelowStart, 4);
        OverflowPopup.IsOpen = true;
    }

    private void OverflowTab_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        if (ItemOf(sender) is { } item)
            TabChosen?.Invoke(item);
    }
}
