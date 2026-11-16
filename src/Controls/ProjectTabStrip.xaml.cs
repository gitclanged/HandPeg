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

/// <summary>View model of one title-bar tab (its persisted header lives in Services/ProjectTabs.cs).</summary>
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

    /// <summary>True while being dragged along the strip (drawn faded).</summary>
    [ObservableProperty] private bool _isDragged;

    /// <summary>Hover card image (paused still or saved thumbnail), set by the window on open and cleared on close.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardWidth))]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    [NotifyPropertyChangedFor(nameof(ScrubWidth))]
    private ImageSource? _preview;

    /// <summary>
    /// Hover-card scrub position along the Sequence (0..1, from the pointer over the tab or card; overview sheet frame shown); NaN when idle.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScrubWidth))]
    [NotifyPropertyChangedFor(nameof(IsScrubbing))]
    private double _scrubFraction = double.NaN;

    /// <summary>True while scrubbing (scrub bar visible).</summary>
    public bool IsScrubbing => !double.IsNaN(ScrubFraction);

    /// <summary>Scrub bar width in DIPs (card width * scrub fraction).</summary>
    public double ScrubWidth => IsScrubbing ? Math.Clamp(ScrubFraction, 0, 1) * CardWidth : 0;

    /// <summary>Max hover-card image size in DIPs (16:9 = 318 x 179, 9:16 = 191 x 340).</summary>
    public const double CardMaxWidth = 318, CardMaxHeight = 340;

    /// <summary>Card width: image aspect fitted in <see cref="CardMaxWidth"/> x <see cref="CardMaxHeight"/> (vertical projects get tall
    /// cards, never cropped); <see cref="CardMaxWidth"/> without an image.</summary>
    public double CardWidth => CardFit(Preview?.Width ?? 0, Preview?.Height ?? 0).Width;

    /// <summary>Card image height (0 without an image).</summary>
    public double PictureHeight => CardFit(Preview?.Width ?? 0, Preview?.Height ?? 0).Height;

    /// <summary>Pure: aspect-preserving fit of <paramref name="width"/> x <paramref name="height"/> into the max card box.</summary>
    public static Size CardFit(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return new Size(CardMaxWidth, 0);
        var scale = Math.Min(CardMaxWidth / width, CardMaxHeight / height);
        return new Size(Math.Round(width * scale), Math.Round(height * scale));
    }

    /// <summary>Frame size and rate line for the card ("1920 x 1080 · 60 fps"; "" when unknown).</summary>
    [ObservableProperty] private string _details = "";

    /// <summary>True when shown on the strip (<see cref="ProjectTabStrip.Fit"/>); otherwise it is in the overflow list.</summary>
    [ObservableProperty] private bool _isShown = true;

    /// <summary>Compact label: the name's first letter or digit (a dot if none).</summary>
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

    /// <summary>Automation name ("Project Tab: My Project").</summary>
    public string AutomationName => $"Project Tab: {Title}";

    /// <summary>Tooltip text: name, file, unsaved state, middle-click hint.</summary>
    public string ToolTipText =>
        (ProjectPath.Length > 0 ? $"{Title}\n{ProjectPath}" : $"{Title}\nNot saved as a project yet")
        + (IsDirty ? "\nUnsaved changes" : "")
        + "\nMiddle-click to close";

    /// <summary>Effective tooltip: text for the active tab; null for inactive tabs (their hover card shows it).</summary>
    public string? FrontToolTip => IsActive ? ToolTipText : null;

    /// <summary>Card footer: saved state and file.</summary>
    public string SavedText => ProjectPath.Length == 0 ? "Not saved as a project yet" : (IsDirty ? "Unsaved changes\n" : "Saved\n") + ProjectPath;

    /// <summary>Has a project file (enables Open Containing Folder).</summary>
    public bool HasFile => ProjectPath.Length > 0;
}

/// <summary>Tab context-menu commands handled by the window.</summary>
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

/// <summary>Tab context-menu line (<see cref="ProjectTabStrip.MenuFor"/>): label with shortcut, command, enabled; null command = separator.</summary>
internal readonly record struct ProjectTabMenuLine(string Header, ProjectTabCommand? Command, bool Enabled);

/// <summary>
/// Strip layout result (<see cref="ProjectTabStrip.LayoutFor"/>): max tab width, compact flag, and the visible run (<c>First</c>,
/// <c>Shown</c>).
/// </summary>
internal readonly record struct ProjectTabLayout(double TabWidth, bool Compact, int First, int Shown);

/// <summary>
/// Browser-style title-bar project tabs plus "+". Pure view: raises events; the window switches, opens and closes tabs
/// (MainWindow.ProjectTabs.cs). Starts collapsed and empty. Never exceeds its given width (caption stays free right of +): tabs shrink to
/// <see cref="MinTabWidth"/>, then compact (dot + initial) to <see cref="CompactTabWidth"/>, then a run around the active tab plus an
/// overflow count/list. Supports drag reorder, a context menu, and hover cards for inactive tabs.
/// </summary>
public partial class ProjectTabStrip : UserControl
{
    /// <summary>Tab widths in DIPs: min with name and close, max, and min compact (dot + initial).</summary>
    public const double MinTabWidth = 72, MaxTabWidth = 200, CompactTabWidth = 28;

    /// <summary>Tab right margin, + width and overflow count width in DIPs (matching ProjectTabStrip.xaml).</summary>
    public const double TabGap = 2, NewTabWidth = 24, OverflowWidth = 28;

    public static readonly DependencyProperty TabMaxWidthProperty =
        DependencyProperty.Register(nameof(TabMaxWidth), typeof(double), typeof(ProjectTabStrip), new FrameworkPropertyMetadata(MaxTabWidth));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(ProjectTabStrip), new FrameworkPropertyMetadata(false));

    // Last available width (NaN before the title bar's first layout); reused on tab add/remove/activate.
    private double _available = double.NaN;

    public ProjectTabStrip()
    {
        InitializeComponent();
        Items.ItemsSource = Tabs;

        // Drag reorder: the strip holds mouse capture.
        PreviewMouseMove += Strip_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Strip_PreviewMouseLeftButtonUp;
        LostMouseCapture += (_, e) =>
        {
            // Only the strip's own capture loss ends a drag (a tab button's, bubbled when the strip takes capture, does not).
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

    /// <summary>Tabs in display order.</summary>
    public ObservableCollection<ProjectTabItem> Tabs { get; } = [];

    /// <summary>Current max tab width (<see cref="Fit"/>).</summary>
    public double TabMaxWidth
    {
        get => (double)GetValue(TabMaxWidthProperty);
        set => SetValue(TabMaxWidthProperty, value);
    }

    /// <summary>True when tabs are compact (<see cref="Fit"/>): names in tooltips; middle-click still closes.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>Tab clicked (activate).</summary>
    public event Action<ProjectTabItem>? TabChosen;

    /// <summary>Tab × or middle click (close).</summary>
    public event Action<ProjectTabItem>? TabClosing;

    /// <summary>+ clicked (new project tab).</summary>
    public event Action? NewTabAsked;

    /// <summary>Tab moved during a drag: the tab and its new index in <see cref="Tabs"/> (already applied).</summary>
    public event Action<ProjectTabItem, int>? TabMoved;

    /// <summary>Drag ended (the window persists the order once, here).</summary>
    public event Action? ReorderEnded;

    /// <summary>Context-menu command chosen (except Rename Project..., see <see cref="TabRenamed"/>).</summary>
    public event Action<ProjectTabItem, ProjectTabCommand>? TabCommand;

    /// <summary>New name committed with Enter in the context-menu rename box (not per keystroke).</summary>
    public event Action<ProjectTabItem, string>? TabRenamed;

    /// <summary>Inactive tab card opening: the window supplies <see cref="ProjectTabItem.Preview"/> and <see cref="ProjectTabItem.Details"/>.</summary>
    public event Action<ProjectTabItem>? PreviewAsked;

    /// <summary>True during a drag; the window must not touch order or layout until it ends.</summary>
    public bool IsReordering { get; private set; }

    /// <summary>
    /// Tab whose card is open (null for none); late images apply only to it. Held while the pointer is over the tab, its card, or crossing
    /// the gap, so hovering the card counts as hovering the tab.
    /// </summary>
    public ProjectTabItem? HoverItem { get; private set; }

    /// <summary>Lays out tabs within <paramref name="available"/> DIPs (title-bar room minus the drag caption), never exceeding it.</summary>
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

    /// <summary>Re-fits in the last width after tab changes (no-op before the first width).</summary>
    public void Refit()
    {
        if (double.IsFinite(_available))
            Fit(_available);
    }

    /// <summary>
    /// Pure (golden titlebar-tab-overflow): layout for <paramref name="count"/> tabs with <paramref name="active"/> in
    /// <paramref name="available"/> DIPs: equal named widths in [<see cref="MinTabWidth"/>, <see cref="MaxTabWidth"/>]; then compact down to
    /// <see cref="CompactTabWidth"/>; then a run around the active tab plus overflow count. Tabs, gaps, count and + never exceed
    /// <paramref name="available"/> (once it fits + and the count).
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

        // Visible run around the active tab; the rest go into the overflow list.
        var forTabs = Math.Max(0, room - OverflowWidth);
        var shown = (int)Math.Min(count - 1, Math.Floor(forTabs / (CompactTabWidth + TabGap)));
        if (shown <= 0)
            return new ProjectTabLayout(CompactTabWidth, true, 0, 0);
        var width = Math.Clamp(Math.Floor(forTabs / shown) - TabGap, CompactTabWidth, MinTabWidth - 1);
        var first = Math.Clamp(Math.Clamp(active, 0, count - 1) - shown / 2, 0, count - shown);
        return new ProjectTabLayout(width, true, first, shown);
    }

    /// <summary>Sets the + tooltip with its shortcut (after Frame 1; the key map is not read earlier).</summary>
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

    /// <summary>Closes the hover card and overflow list (strip collapsed, drag started, menu opened).</summary>
    public void CloseFlyouts()
    {
        CloseHoverCard();
        OverflowPopup.IsOpen = false;
    }

    // ----- Drag reorder -----
    // A press belongs to the tab button (click = activate). Past the system drag distance the strip captures the mouse (cancelling the
    // click) and the tab follows the pointer among visible tabs; each move updates Tabs and raises TabMoved; release raises ReorderEnded
    // (order persisted once). Capture is on the strip, not the tab (moving an ItemsControl item re-parents it and would drop capture).
    // No background is added, so gaps between tabs remain window caption.

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
            // Release missed (another window took the mouse): end the drag.
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

    /// <summary>Starts dragging <paramref name="item"/> (golden titlebar-tab-drag entry): fades it and closes flyouts; false if not on the strip.</summary>
    internal bool BeginReorder(ProjectTabItem item)
    {
        if (IsReordering || !Tabs.Contains(item))
            return false;

        // Finish any slide-out first: the drag reads drawn tab positions.
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

    /// <summary>Moves the dragged tab to the slot for pointer <paramref name="x"/>, using the current drawn midpoints of the other visible tabs.</summary>
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
    /// Pure (golden project-tabs-reorder): target index for a tab dragged from <paramref name="from"/> with the pointer at
    /// <paramref name="x"/>: before the first other visible tab whose midpoint is right of x, else after the last. <paramref name="shown"/>
    /// lists visible tabs (strip index, midpoint); overflow tabs are never crossed.
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

    /// <summary>Ends a drag (release or capture loss): restores the tab's look, re-fits, and raises ReorderEnded.</summary>
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

    // ----- Tab context menu -----

    private void Tab_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsReordering || ItemOf(sender) is not { } item || sender is not FrameworkElement tab)
            return;
        e.Handled = true;
        CloseFlyouts();
        OpenTabMenu(tab, item);
    }

    /// <summary>
    /// Pure (golden project-tabs-menu): ordered menu lines (null command = separator). Shortcuts are shown only for the active tab (Ctrl+S,
    /// Ctrl+W act on it) and read at open time, so rebinds are reflected.
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

    /// <summary>Builds and opens the context menu at the mouse (created per open, not cached).</summary>
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

    /// <summary>Rename Project...: turns the line into a text box; Enter commits once, Escape cancels.</summary>
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

    // ----- Inactive tab hover card -----
    // Hovering an inactive tab opens a card immediately: paused still or file thumbnail, frame size/rate, saved state. One card moves
    // between tabs; moving across a tab scrubs its overview sheet (HoverScrubbed: cached images, no decode). The window supplies the image
    // (PreviewAsked), released on close so at most one is held. The active tab keeps a text tooltip. The card is hit-testable: the tab under
    // the pointer owns it; across the card the pointer scrubs the whole Sequence (compact tabs scrub a card-width span about the playhead);
    // clicking the card activates the tab at its own playhead (no seek). Open while over tab or card, plus HoverCardGraceMs after leaving
    // both; any press, drag, menu or deactivation closes it at once.

    /// <summary>Close grace after leaving both tab and card (same as header hover flyouts).</summary>
    public const int HoverCardGraceMs = HoverFlyout.CloseDelayMs;

    private bool _hoverOwnerHooked;

    // Card source tab, pointer-over flags for tab and card, grace timer (created on first open), and a pending card press awaiting release.
    private FrameworkElement? _hoverTab;
    private readonly HoverCardHold _hoverHold = new(HoverCardGraceMs);
    private DispatcherTimer? _hoverClose;
    private bool _cardPressed;

    /// <summary>
    /// Hover scrub: position 0..1 along the Sequence; when the second value is true, the first is instead a Sequence-fraction offset from
    /// the playhead (compact tabs, <see cref="TabScrub"/>).
    /// </summary>
    public event Action<ProjectTabItem, double, bool>? HoverScrubbed;

    /// <summary>
    /// Pure (golden tab-hover-scrub): scrub value for pointer <paramref name="x"/> on a <paramref name="tabWidth"/> tab. Named tab: whole
    /// Sequence 0..1. <paramref name="compact"/> tab: playhead-relative offset with the tab centre at the playhead, scaled as if
    /// <paramref name="span"/> (card width) wide, matching card scrub speed.
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

    /// <summary>Pure: card scrub value for <paramref name="x"/> over <paramref name="width"/>, whole Sequence edge to edge.</summary>
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
        // Moving to the next tab (its Enter follows this Leave) moves the card; moving onto the card keeps it (HoverCardHold).
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

    // Card scrub over the whole Sequence, measured on the current card width (the first scrubbed image can change its aspect).
    private void HoverCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (HoverItem is not { } item || e.LeftButton == MouseButtonState.Pressed || HoverBody.ActualWidth <= 1)
            return;

        HoverScrubbed?.Invoke(item, CardScrub(e.GetPosition(HoverBody).X, HoverBody.ActualWidth), false);
    }

    private void HoverCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only the card's own events (its routed events also bubble through the strip via the popup's logical parent).
        e.Handled = true;
        _cardPressed = HoverItem is not null;
    }

    // Card click activates its tab at the tab's own playhead, like a tab click.
    private void HoverCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_cardPressed || HoverItem is not { } item)
            return;
        _cardPressed = false;
        CloseHoverCard();
        TabChosen?.Invoke(item);
    }

    // Left tab or card: close after the grace once off both (timer created lazily).
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

        // Missed Enter (pointer returned under a popup or capture): keep the card; the next Leave restarts the grace.
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
        // Window deactivated (Alt+Tab while hovering): close so it never floats over another app.
        if (!_hoverOwnerHooked && Window.GetWindow(this) is { } owner)
        {
            owner.Deactivated += (_, _) => CloseFlyouts();
            _hoverOwnerHooked = true;
        }

        _hoverTab = tab;
        if (ReferenceEquals(item, HoverItem) && HoverCard.IsOpen)
            return;

        // Card open for another tab: release its image and move the card.
        if (HoverItem is { } shown)
            (shown.Preview, shown.ScrubFraction) = (null, double.NaN);
        HoverItem = item;
        _cardPressed = false;
        PreviewAsked?.Invoke(item);
        HoverCard.DataContext = item;
        UiScale.PlaceAgainst(HoverCard, tab, UiScale.FlyoutSide.BelowStart, 4);
        HoverCard.IsOpen = true;
    }

    /// <summary>Closes (or cancels) the hover card and releases its image.</summary>
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
        // × is inside the tab: mark handled so the tab does not also receive the click.
        e.Handled = true;
        if (ItemOf(sender) is { } item)
            TabClosing?.Invoke(item);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTabAsked?.Invoke();

    /// <summary>Overflow count click: lists hidden tabs below it (UiScale.PlaceAgainst, never mirrored).</summary>
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

    // ----- Restored-tab slide-out -----
    // Restored tabs (kept or crash leftovers) are added at idle after Frame 1 (MainWindow.RecoverProjectTabs) and slide out from under the
    // active tab (raised, opaque) to their slots while fading in, along with + and the overflow count: 180 ms cubic ease-out. Only
    // RenderTransform and Opacity animate (no layout); animations are frozen, created on first use, and FillBehavior.Stop restores base
    // values (which already equal the end values). Disabled when Extra Animations or Windows client-area animations are off. A drag or
    // hiding the strip ends the slide immediately (ReorderTo reads drawn positions).

    /// <summary>Slide-out duration in ms.</summary>
    public const double SlideMs = 180;

    // Easing and fade, created on first use and frozen (UI thread only).
    private static IEasingFunction? _slideEase;
    private static DoubleAnimation? _slideFade;

    // Elements animated by the active slide, and elements raised in z-order (active tab; tabs above + and the count).
    private readonly List<UIElement> _sliding = [];
    private readonly List<UIElement> _raised = [];

    // Clock whose Completed ends the slide (the first created; all share the 180 ms duration).
    private AnimationClock? _slideClock;

    /// <summary>True while a slide is running (tests).</summary>
    internal bool IsSliding => _sliding.Count > 0;

    /// <summary>Pure (golden project-tabs-slide-out): tabs were restored, the strip is visible, and both animation settings are on.</summary>
    internal static bool SlideWanted(int restored, bool shown, bool extraAnimations, bool clientAreaAnimation) =>
        restored > 0 && shown && extraAnimations && clientAreaAnimation;

    /// <summary>
    /// Slides out restored tabs <paramref name="ids"/> from under the active tab (called only by MainWindow.RecoverProjectTabs right after
    /// they are shown). No-op when <see cref="SlideWanted"/> is false or a drag is active.
    /// </summary>
    public void SlideOutFromActive(IReadOnlyCollection<string> ids)
    {
        if (IsReordering || !SlideWanted(ids.Count, Visibility == Visibility.Visible, AppSettings.Current.EnableExtraAnimations, SystemParameters.ClientAreaAnimation))
            return;
        BeginSlide(ids);
    }

    /// <summary>
    /// Ungated slide start (<see cref="SlideOutFromActive"/> gates): forces layout so restored slots are known before first draw, offsets
    /// each visible restored tab (and +, count) to the active tab's x, and raises the active tab. Returns start offsets by tab id in DIPs
    /// (empty = nothing slides).
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

        // Z-order: active tab above sliding tabs, and tabs above + and the count (which also slide from under it).
        Raise(front);
        Raise(Items);
        foreach (var button in new FrameworkElement[] { OverflowButton, NewTabButton })
        {
            if (button.Visibility == Visibility.Visible && button.ActualWidth > 0)
                Slide(button, SlideOffset(frontX, button.TranslatePoint(new Point(0, 0), Items).X));
        }

        return offsets;
    }

    /// <summary>Pure: slide start offset = active tab x minus the tab's slot x (DIPs).</summary>
    internal static double SlideOffset(double activeX, double slotX) => activeX - slotX;

    /// <summary>
    /// Frozen 180 ms cubic ease-out translation from <paramref name="from"/> DIPs to 0, FillBehavior.Stop (base value 0 equals the end).
    /// </summary>
    internal static DoubleAnimation SlideAnimation(double from)
    {
        var slide = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = SlideEase, FillBehavior = FillBehavior.Stop };
        slide.Freeze();
        return slide;
    }

    /// <summary>Shared frozen cubic ease-out (created on first use).</summary>
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

    /// <summary>Frozen fade-in 0 to 1 over the slide (created on first use; base opacity 1 equals the end).</summary>
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

    /// <summary>Starts slide and fade on one element with its own (animated, unfrozen) TranslateTransform.</summary>
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

    /// <summary>Ends the slide (completion, drag start, or strip hide): removes clocks and restores transforms and z-order.</summary>
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
