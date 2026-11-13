using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    /// Where along its Sequence the pointer scrubs its hover card (5.4.2A: 0 to 1 across the tab, its overview sheet's picture there shown;
    /// 5.4.4: across the card too); NaN while it does not.
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
            {
                CloseFlyouts();
                FinishSlide();
            }
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

    /// <summary>
    /// The tab whose hover card is open now (null for none): a picture that comes late is given only to it. 5.4.4: kept while the pointer
    /// is over the tab or its card (and the moment the gap between them takes), so "the pointer rests on a tab" counts the card too.
    /// </summary>
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

        // A slide still under way ends first (Pass 5.5A): the drag reads where the tabs are drawn.
        FinishSlide();
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
    // 5.4.4 (the user's): the card takes the pointer. The tab under the pointer always owns it; across the card the pointer scrubs that
    // tab's whole Sequence, edge to edge (a narrowed tab is too small to scrub across: along it the pointer moves about its playhead, a span
    // as wide as the card centred on it); a click on the card brings the tab to the front at its own playhead (no seek: its still, frames
    // and pre-roll are all kept for that moment). The card stays open while the pointer is over the tab or the card, and HoverCardGraceMs
    // after it left both (the gap under the tab); every other close (a press, a drag, a menu, the window let go) is at once.

    /// <summary>How long the hover card waits after the pointer left its tab and the card (5.4.4): as long as the header's hover flyouts.</summary>
    public const int HoverCardGraceMs = HoverFlyout.CloseDelayMs;

    private bool _hoverOwnerHooked;

    // The tab the card was opened from (to ask whether the pointer is still on it), the pointer's place over the tab and the card, the
    // timer that closes the card after the grace (made the first time a card opens), and a press on the card that a release there completes.
    private FrameworkElement? _hoverTab;
    private readonly HoverCardHold _hoverHold = new(HoverCardGraceMs);
    private DispatcherTimer? _hoverClose;
    private bool _cardPressed;

    /// <summary>
    /// The pointer across a tab behind whose card is open (5.4.2A): where along it, 0 to 1, and the window shows that picture of its
    /// Sequence. 5.4.4: with the second value true, the first is an offset from the tab's playhead instead, as a fraction of the Sequence
    /// (a narrowed tab: <see cref="TabScrub"/>).
    /// </summary>
    public event Action<ProjectTabItem, double, bool>? HoverScrubbed;

    /// <summary>
    /// What the pointer at <paramref name="x"/> along a tab <paramref name="tabWidth"/> wide scrubs (pure, 5.4.4: the golden tab-hover-scrub):
    /// across a tab with its name, the whole Sequence (0 to 1); across a narrowed tab (<paramref name="compact"/>), an offset from its
    /// playhead, the tab's middle at the playhead, as if the tab were <paramref name="span"/> wide (its card's width), so the pointer moves
    /// the picture as fast as it does across the card.
    /// </summary>
    internal static (double Value, bool AboutPlayhead) TabScrub(double x, double tabWidth, bool compact, double span)
    {
        if (!(tabWidth > 0) || !double.IsFinite(x))
            return (0, compact);
        if (!compact)
            return (Math.Max(0, Math.Min(x / tabWidth, 1)), false);
        var offset = (Math.Max(0, Math.Min(x, tabWidth)) - tabWidth / 2) / (span > 0 ? span : tabWidth);
        return (Math.Max(-0.5, Math.Min(offset, 0.5)), true);
    }

    /// <summary>What the pointer at <paramref name="x"/> across a card <paramref name="width"/> wide scrubs (pure, 5.4.4): the whole Sequence, edge to edge.</summary>
    internal static double CardScrub(double x, double width) => width > 0 && double.IsFinite(x) ? Math.Max(0, Math.Min(x / width, 1)) : 0;

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private void Tab_MouseEnter(object sender, MouseEventArgs e)
    {
        if (IsReordering || e.LeftButton == MouseButtonState.Pressed || ItemOf(sender) is not { IsActive: false } item || sender is not FrameworkElement tab
            || !tab.IsVisible || !Tabs.Contains(item))
        {
            CloseHoverCard();
            return;
        }

        _hoverHold.EnterTab();
        _hoverClose?.Stop();
        OpenHoverCard(item, tab);
    }

    private void Tab_MouseLeave(object sender, MouseEventArgs e)
    {
        // Onto the next tab (its enter comes after this leave), the card moves there; onto the card, it stays (HoverCardHold).
        if (ItemOf(sender) is { } item && ReferenceEquals(item, HoverItem))
            HoldOff(card: false);
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (ItemOf(sender) is not { } item || !ReferenceEquals(item, HoverItem) || sender is not FrameworkElement { ActualWidth: > 1 } tab
            || e.LeftButton == MouseButtonState.Pressed)
            return;

        var (value, aboutPlayhead) = TabScrub(e.GetPosition(tab).X, tab.ActualWidth, Compact, item.CardWidth);
        HoverScrubbed?.Invoke(item, value, aboutPlayhead);
    }

    private void HoverCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (HoverItem is null)
            return;
        _hoverHold.EnterCard();
        _hoverClose?.Stop();
    }

    private void HoverCard_MouseLeave(object sender, MouseEventArgs e)
    {
        _cardPressed = false;
        if (HoverItem is not null)
            HoldOff(card: true);
    }

    // Across the card (5.4.4): the tab's whole Sequence, measured on the card as it is drawn now (its first scrubbed picture can change its shape).
    private void HoverCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (HoverItem is not { } item || e.LeftButton == MouseButtonState.Pressed || HoverBody.ActualWidth <= 1)
            return;

        HoverScrubbed?.Invoke(item, CardScrub(e.GetPosition(HoverBody).X, HoverBody.ActualWidth), false);
    }

    private void HoverCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The card's own (its routed events also pass the strip, through the popup's place in the tree).
        e.Handled = true;
        _cardPressed = HoverItem is not null;
    }

    // A click on the card brings its tab to the front, as a click on the tab does (5.4.4), at the tab's own playhead.
    private void HoverCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_cardPressed || HoverItem is not { } item)
            return;
        _cardPressed = false;
        CloseHoverCard();
        TabChosen?.Invoke(item);
    }

    // The pointer left the tab or the card: the card closes once it has been off both for the grace (the timer made the first time).
    private void HoldOff(bool card)
    {
        var now = NowMs();
        if (card)
            _hoverHold.LeaveCard(now);
        else
            _hoverHold.LeaveTab(now);
        if (_hoverHold.IsOver)
            return;

        if (_hoverClose is null)
        {
            _hoverClose = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
            _hoverClose.Tick += (_, _) => HoverCloseTick();
        }

        _hoverClose.Stop();
        _hoverClose.Interval = TimeSpan.FromMilliseconds(Math.Max(_hoverHold.Remaining(now), 1));
        _hoverClose.Start();
    }

    private void HoverCloseTick()
    {
        var now = NowMs();
        _hoverClose?.Stop();
        if (HoverItem is null)
            return;

        // An enter missed (the pointer came back while a popup or capture had it): the card stays, and the next leave starts the grace again.
        if (_hoverTab?.IsMouseOver == true || HoverSurface.IsMouseOver)
            return;
        if (!_hoverHold.Due(now))
        {
            if (_hoverClose is { } timer && double.IsFinite(_hoverHold.Remaining(now)))
            {
                timer.Interval = TimeSpan.FromMilliseconds(Math.Max(_hoverHold.Remaining(now), 1));
                timer.Start();
            }

            return;
        }

        CloseHoverCard();
    }

    private void OpenHoverCard(ProjectTabItem item, FrameworkElement tab)
    {
        // The window let go (Alt+Tab with the pointer still on the tab): the card goes with it, never left over another program.
        if (!_hoverOwnerHooked && Window.GetWindow(this) is { } owner)
        {
            owner.Deactivated += (_, _) => CloseFlyouts();
            _hoverOwnerHooked = true;
        }

        _hoverTab = tab;
        if (ReferenceEquals(item, HoverItem) && HoverCard.IsOpen)
            return;

        // Another tab's card open: its picture let go, and the card moved.
        if (HoverItem is { } shown)
            (shown.Preview, shown.ScrubFraction) = (null, double.NaN);
        HoverItem = item;
        _cardPressed = false;
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
        _hoverTab = null;
        _cardPressed = false;
        _hoverHold.Reset();
        _hoverClose?.Stop();
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

    // ----- Restored tabs slide out from behind the tab in front (Pass 5.5A) -----
    // The tabs a session left (kept for this start, or left by a crash) come onto the strip at idle after the first frame
    // (MainWindow.RecoverProjectTabs), out from behind the tab in front: each starts at its x, under it (the tab in front drawn on top, its
    // face opaque) and faded out, and slides to its own place as it fades in, together with + and the overflow count (from behind it as
    // well), 180 ms, a cubic ease out. Only a RenderTransform and the opacity move (no layout); the animations are frozen and
    // made on first use (nothing at type load), and at their end (FillBehavior.Stop) every value is its own again: each base value is the
    // end one already. Never with the extra animations off (Settings) or Windows' client-area animations off. A drag along the strip, or the
    // strip hidden, ends a slide at once (ReorderTo reads where the tabs are drawn).

    /// <summary>How long restored tabs take to slide out from behind the tab in front, ms.</summary>
    public const double SlideMs = 180;

    // The easing and the fade, made on first use and frozen (UI thread only).
    private static IEasingFunction? _slideEase;
    private static DoubleAnimation? _slideFade;

    // What a slide under way moves (its transform and opacity animated) and raises (the tab in front, the tabs over + and the count).
    private readonly List<UIElement> _sliding = [];
    private readonly List<UIElement> _raised = [];

    // The clock whose end ends the slide (the first one made: every one runs the same 180 ms).
    private AnimationClock? _slideClock;

    /// <summary>Whether a slide is under way (tests).</summary>
    internal bool IsSliding => _sliding.Count > 0;

    /// <summary>Whether restored tabs slide out (pure: project-tabs-slide-out): some were restored, the strip is shown, and both kinds of animation are on.</summary>
    internal static bool SlideWanted(int restored, bool shown, bool extraAnimations, bool clientAreaAnimation) =>
        restored > 0 && shown && extraAnimations && clientAreaAnimation;

    /// <summary>
    /// The tabs restored at start (<paramref name="ids"/>) slide out from behind the tab in front (Pass 5.5A; called only from
    /// MainWindow.RecoverProjectTabs, right after the strip shows them): nothing when <see cref="SlideWanted"/> says not, or while a tab is dragged.
    /// </summary>
    public void SlideOutFromActive(IReadOnlyCollection<string> ids)
    {
        if (IsReordering || !SlideWanted(ids.Count, Visibility == Visibility.Visible, AppSettings.Current.EnableExtraAnimations, SystemParameters.ClientAreaAnimation))
            return;
        BeginSlide(ids);
    }

    /// <summary>
    /// The slide begun (no gate: <see cref="SlideOutFromActive"/> has it): the strip laid out now (at idle, after the first frame) so the
    /// restored tabs' places are known before they are first drawn, each shown restored tab (and + and the count) started at the tab in front,
    /// the tab in front raised over them. The start offsets by tab id, in DIPs along the strip (empty: nothing slides).
    /// </summary>
    internal IReadOnlyDictionary<string, double> BeginSlide(IReadOnlyCollection<string> ids)
    {
        FinishSlide();
        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        UpdateLayout();
        var active = -1;
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].IsActive)
                active = i;
        }

        if (active < 0 || !Tabs[active].IsShown || Items.ItemContainerGenerator.ContainerFromIndex(active) is not FrameworkElement { ActualWidth: > 0 } front)
            return offsets;

        var frontX = front.TranslatePoint(new Point(0, 0), Items).X;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var item = Tabs[i];
            if (i == active || !item.IsShown || !ids.Contains(item.Id, StringComparer.Ordinal)
                || Items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement { ActualWidth: > 0 } box)
            {
                continue;
            }

            var offset = SlideOffset(frontX, box.TranslatePoint(new Point(0, 0), Items).X);
            Slide(box, offset);
            offsets[item.Id] = offset;
        }

        if (offsets.Count == 0)
            return offsets;

        // The tab in front drawn over the tabs coming out from behind it, and the tabs over + and the count, which come out from behind it too.
        Raise(front);
        Raise(Items);
        foreach (var button in new FrameworkElement[] { OverflowButton, NewTabButton })
        {
            if (button.Visibility == Visibility.Visible && button.ActualWidth > 0)
                Slide(button, SlideOffset(frontX, button.TranslatePoint(new Point(0, 0), Items).X));
        }

        return offsets;
    }

    /// <summary>Where a tab starts its slide (pure): the tab in front's x less its own place's, in DIPs.</summary>
    internal static double SlideOffset(double activeX, double slotX) => activeX - slotX;

    /// <summary>
    /// The slide along the strip from <paramref name="from"/> DIPs to its own place (0), 180 ms, a cubic ease out, frozen, and gone at its
    /// end (FillBehavior.Stop: the base value, 0, is the end one).
    /// </summary>
    internal static DoubleAnimation SlideAnimation(double from)
    {
        var slide = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = SlideEase, FillBehavior = FillBehavior.Stop };
        slide.Freeze();
        return slide;
    }

    /// <summary>The cubic ease out the slide and its fade follow (made on first use, frozen).</summary>
    internal static IEasingFunction SlideEase
    {
        get
        {
            if (_slideEase is null)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                ease.Freeze();
                _slideEase = ease;
            }

            return _slideEase;
        }
    }

    /// <summary>The fade in from nothing over the slide (made on first use, frozen; the base opacity, 1, is the end one).</summary>
    internal static DoubleAnimation SlideFade
    {
        get
        {
            if (_slideFade is null)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = SlideEase, FillBehavior = FillBehavior.Stop };
                fade.Freeze();
                _slideFade = fade;
            }

            return _slideFade;
        }
    }

    /// <summary>One element's slide and fade started, with a transform of its own (animated, so never frozen).</summary>
    private void Slide(UIElement element, double offset)
    {
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        var clock = SlideAnimation(offset).CreateClock();
        shift.ApplyAnimationClock(TranslateTransform.XProperty, clock);
        element.ApplyAnimationClock(OpacityProperty, SlideFade.CreateClock());
        _sliding.Add(element);
        if (_slideClock is null)
        {
            _slideClock = clock;
            clock.Completed += SlideClock_Completed;
        }
    }

    private void Raise(UIElement element)
    {
        Panel.SetZIndex(element, 1);
        _raised.Add(element);
    }

    private void SlideClock_Completed(object? sender, EventArgs e) => FinishSlide();

    /// <summary>The slide ended (at its end, as a drag begins, or as the strip hides): every clock let go, every transform and z-order its own again.</summary>
    internal void FinishSlide()
    {
        if (_slideClock is { } clock)
        {
            clock.Completed -= SlideClock_Completed;
            _slideClock = null;
        }

        foreach (var element in _sliding)
        {
            element.ApplyAnimationClock(OpacityProperty, null);
            if (element.RenderTransform is TranslateTransform { IsFrozen: false } shift)
                shift.ApplyAnimationClock(TranslateTransform.XProperty, null);
            element.ClearValue(RenderTransformProperty);
        }

        foreach (var element in _raised)
            element.ClearValue(Panel.ZIndexProperty);
        _sliding.Clear();
        _raised.Clear();
    }
}
