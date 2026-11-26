using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandPegApp.Views;

/// <summary>
/// Timeline pane: track surface (<see cref="Surface"/>), ruler, scroll bars, zoom, tool row and selection inspector. It edits nothing
/// itself: every button raises <see cref="ToolClick"/> with a Tag, handled by the main window, which owns selection and view
/// (MainWindow.UnifiedTimeline.cs). Owns only the row scroll bar sync, the Add Track and Quick FX flyouts and the recycle bin popup.
/// Constructed before Frame 1 in every layout with an empty surface. The tool row is built with it (zero pop-in); parts hidden at
/// Frame 1 (Text Settings, inspector, Add Track and bin flyouts: DetailsTemplate) are deferred until on screen (<see cref="RealizeTools"/>).
/// </summary>
public partial class UnifiedTimelinePane : UserControl
{
    private bool _showingRowScroll;

    // Deferred parts resolved from DetailsTemplate in RealizeTools; untouched before then.
    private bool _toolsMade;
    private HoverFlyout? _addLayerHover;
    private FrameworkElement _addLayerFlyoutPanel = null!;
    private Panel _kindsPanel = null!;
    private Border _inspector = null!;
    private TextBlock _inspectedName = null!;
    private FrameworkElement _pictureGroup = null!, _soundGroup = null!, _backgroundGroup = null!, _soundSpeedHost = null!, _inspectorDelete = null!;
    private FrameworkElement _textSettingsPanel = null!;
    private ComboBox _fontBox = null!;

    // Inspect request received before RealizeTools; applied once built.
    private (string Name, object? Picture, object? Sound, object? Background)? _inspectLater;

    // Quick FX flyout of Creator Hotbar slots (built with DetailsTemplate in RealizeTools); slot definitions from ShowHotbar (may arrive
    // before Frame 1) applied once built; and a flag holding the flyout open while a slot's context menu is open.
    private HoverFlyout? _quickFxHover;
    private FrameworkElement _quickFxPanel = null!;
    private Panel? _slotsPanel;
    private IReadOnlyList<Models.HotbarSlot> _slots = [];
    private bool _slotMenuOpen;

    public UnifiedTimelinePane()
    {
        InitializeComponent();

        // Ruler and horizontal scroll bar start where row headers end; the zoom corner matches the header width (track height lives
        // on the tool row). 1 DIP gap from the tracks.
        var headers = Surface.HeaderWidth;
        ZoomCorner.Width = headers;
        Ruler.Margin = new Thickness(headers, 0, 0, 1);
        TimeScroll.Margin = new Thickness(headers, 1, 0, 0);

        Surface.ExtentChanged += UpdateRowScroll;
        Surface.VerticalOffsetChanged += UpdateRowScroll;
        Surface.SizeChanged += (_, _) => UpdateRowScroll();

        // Cursor-Locked Scrub Pointer (Pass 5.7.1): while a locked ruler drag has the cursor over the tracks, they draw the pointer on
        // their playhead; the ruler hides the OS cursor only while one of the two draws it, and every drag end hides it here.
        Ruler.ScrubPointerElsewhere = screen => Surface.ShowScrubPointerAt(screen);
        Ruler.ScrubSampled += _ =>
        {
            if (!Ruler.PointerLocked)
                Surface.ShowScrubPointerAt(null);
        };
        Ruler.SeekEnded += () => Surface.ShowScrubPointerAt(null);

        // Overflowed tools stay reachable from "more": Add Track as a kinds menu, Quick FX as a slots menu.
        Toolbar.MenuEntryFor = element => ReferenceEquals(element, AddLayerHost) ? ("Add Track", new Action(OpenKindsMenu))
            : ReferenceEquals(element, QuickFxHost) ? ("Quick FX", new Action(OpenHotbarMenu)) : null;
    }

    /// <summary>The Slice-Preview ribbon over the tracks (MainWindow.Scrub.cs gates, places and fills it).</summary>
    public SlicePreviewRibbon SlicePreview => SliceRibbon;

    /// <summary>
    /// Places the Slice-Preview ribbon centred on the playhead at <paramref name="seconds"/>, kept within the tracks' time area: a
    /// translation of its strip, no layout.
    /// </summary>
    public void PlaceSlicePreview(double seconds)
    {
        var centre = Surface.HeaderWidth + seconds * Surface.PixelsPerSecond - Surface.ViewOffsetX;
        SliceRibbon.Place(SlicePreviewPlanner.LeftFor(centre, SliceRibbon.RibbonWidth, Surface.HeaderWidth, Surface.ActualWidth));
    }

    /// <summary>Inspector overflow priority on the tool row: above every tool (Add Track is 100).</summary>
    private const int InspectorPriority = 1000;

    /// <summary>
    /// Builds the deferred DetailsTemplate parts: Text Settings, inspector, and tool-row flyouts (Add Track kinds, Quick FX slots, bin list).
    /// Called once on screen (ContextIdle after ContentRendered) so Frame 1 never waits; parts start hidden and DetailsHost takes no space
    /// until a selection exists, so nothing shifts. Idempotent; early consumers ("more" menu Add Track, bin hover) trigger it on demand.
    /// </summary>
    public void RealizeTools()
    {
        if (_toolsMade)
            return;

        _toolsMade = true;
        var tools = (FrameworkElement)((DataTemplate)Resources["DetailsTemplate"]).LoadContent();
        T Part<T>(string name) where T : class => (T)tools.FindName(name);
        _addLayerFlyoutPanel = Part<FrameworkElement>("AddLayerFlyoutPanel");
        _kindsPanel = Part<Panel>("KindsPanel");
        _inspector = Part<Border>("Inspector");
        _inspectedName = Part<TextBlock>("InspectedName");
        _pictureGroup = Part<FrameworkElement>("PictureGroup");
        _soundGroup = Part<FrameworkElement>("SoundGroup");
        _backgroundGroup = Part<FrameworkElement>("BackgroundGroup");
        _soundSpeedHost = Part<FrameworkElement>("SoundSpeedHost");
        _inspectorDelete = Part<FrameworkElement>("InspectorDelete");
        _textSettingsPanel = Part<FrameworkElement>("TextSettingsPanel");
        _fontBox = Part<ComboBox>("FontBox");

        // Add Track kinds open on hover and close shortly after leave (like Settings). PlacementTargets are set here because the
        // template's name scope cannot reach the pane's elements by ElementName.
        var addLayer = Part<Popup>("AddLayerPopup");
        addLayer.PlacementTarget = AddLayerHost;
        _addLayerHover = new HoverFlyout(AddLayerHost, addLayer, _addLayerFlyoutPanel) { HoldsWhileFocused = true };

        // Bin popup bound two-way to its toggle (a click before the list existed is reset).
        var bin = Part<Popup>("BinPopup");
        bin.PlacementTarget = BinButton;
        BinButton.IsChecked = false;
        bin.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding(nameof(ToggleButton.IsChecked))
        {
            Source = BinButton,
            Mode = System.Windows.Data.BindingMode.TwoWay,
        });
        bin.Opened += BinPopup_Opened;
        bin.Closed += BinPopup_Closed;
        _textSettingsPanel.IsVisibleChanged += TextSettings_IsVisibleChanged;

        // Quick FX slots: hover flyout like Add Track, held open while a slot's context menu is open (HoldQuickFxFor).
        var quickFx = Part<Popup>("QuickFxPopup");
        quickFx.PlacementTarget = QuickFxHost;
        _quickFxPanel = Part<FrameworkElement>("QuickFxFlyoutPanel");
        _slotsPanel = Part<Panel>("HotbarSlotsPanel");
        _quickFxHover = new HoverFlyout(QuickFxHost, quickFx, _quickFxPanel) { HoldsWhileFocused = true, HoldsWhile = () => _slotMenuOpen };
        LabelSlots();

        // Null DataContext until a selection exists (never inherit the window's view model).
        _pictureGroup.DataContext = _soundGroup.DataContext = _backgroundGroup.DataContext = null;

        DetailsHost.Child = tools;

        // The inspector joins the single-row toolbar after the tools and before "more", with the highest priority so tools overflow
        // into "more" first.
        if (_inspector.Parent is Panel holder)
            holder.Children.Remove(_inspector);
        OverflowStrip.SetPriority(_inspector, InspectorPriority);
        Toolbar.Children.Insert(Math.Max(Toolbar.Children.Count - 1, 0), _inspector);

        if (_inspectLater is { } later)
        {
            _inspectLater = null;
            Inspect(later.Name, later.Picture, later.Sound, later.Background);
        }
    }

    // System font list loads on first Text Settings show (never during window construction); the selection binds after ItemsSource
    // so it never transiently reads as none.
    private void TextSettings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_textSettingsPanel.IsVisible)
            return;

        _textSettingsPanel.IsVisibleChanged -= TextSettings_IsVisibleChanged;
        _fontBox.ItemsSource = Services.TextLayout.SystemFonts;
        _fontBox.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding("FontFamily"));
    }

    /// <summary>Raised by any tool (tool row, inspector, corners, bin); the sender's Tag identifies it.</summary>
    public event RoutedEventHandler? ToolClick;

    private void Tool_Click(object sender, RoutedEventArgs e) => ToolClick?.Invoke(sender, e);

    /// <summary>
    /// Shows the selection in the inspector: visual clip (<paramref name="picture"/>, a Layer), audio clip (<paramref name="sound"/>), blur
    /// fill settings (<paramref name="background"/>, the view model). Each group edits its target; all null hides the inspector. Calls
    /// before RealizeTools are deferred.
    /// </summary>
    public void Inspect(string name, object? picture, object? sound, object? background)
    {
        if (!_toolsMade)
        {
            _inspectLater = (name, picture, sound, background);
            return;
        }

        Show(_pictureGroup, picture);
        Show(_soundGroup, sound);
        Show(_backgroundGroup, background);
        _soundSpeedHost.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;
        // Captions are deleted from their own menu (not a clip), so no inspector Delete for them.
        _inspectorDelete.Visibility = picture is not Models.Layer { IsCaptions: true } && (picture is not null || sound is not null) ? Visibility.Visible : Visibility.Collapsed;
        _inspectedName.Text = name;
        _inspector.Visibility = picture is not null || sound is not null || background is not null ? Visibility.Visible : Visibility.Collapsed;

        static void Show(FrameworkElement group, object? target)
        {
            // Rebind only on target change so a box being edited keeps its text.
            if (!ReferenceEquals(group.DataContext, target))
                group.DataContext = target;
            group.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ----- Creator Hotbar (Quick FX flyout) -----

    /// <summary>Slot context-menu request (right-click or menu key): (slot index, anchor); the window opens the assignment menu.</summary>
    public event Action<int, FrameworkElement>? HotbarMenuRequested;

    // Slot buttons after RealizeTools; empty before.
    private IEnumerable<Button> HotbarSlots => _slotsPanel?.Children.OfType<Button>() ?? [];

    /// <summary>
    /// Sets Quick FX visibility and slot labels/tooltips. Cheap before Frame 1 (button only); labels apply when the flyout is built.
    /// </summary>
    public void ShowHotbar(IReadOnlyList<Models.HotbarSlot> slots, bool shown)
    {
        _slots = slots;
        QuickFxHost.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
            _quickFxHover?.Close();
        LabelSlots();
    }

    private void LabelSlots()
    {
        foreach (var (button, slot) in HotbarSlots.Zip(_slots))
        {
            button.Content = slot.Label;
            button.ToolTip = $"{slot.About} Right-click to choose what this slot holds.";
            System.Windows.Automation.AutomationProperties.SetName(button, $"Quick FX: {slot.Label}");
        }
    }

    /// <summary>Shows or hides tool-row Undo/Redo (AppSettings.ShowTimelineUndoRedo, off by default).</summary>
    public void ShowUndoRedo(bool shown) => UndoButton.Visibility = RedoButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Holds the Quick FX flyout open while a slot context menu is open (moving into the menu leaves the flyout); releases on menu close,
    /// after which it closes shortly unless hovered.
    /// </summary>
    public void HoldQuickFxFor(ContextMenu menu)
    {
        _slotMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _slotMenuOpen = false;
            _quickFxHover?.Release();
        };
    }

    // Slot click forwards via ToolClick (by Tag). For pointer clicks, return focus to the Quick FX button first: the slot took focus on
    // press, and the flyout (held open while focused, for Down/Escape) would otherwise linger after the pointer left. Keyboard
    // activation (Space/Enter) keeps focus on the slots.
    private void HotbarSlot_Click(object sender, RoutedEventArgs e)
    {
        if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice && _quickFxPanel.IsKeyboardFocusWithin)
            Keyboard.Focus(QuickFxButton);
        ToolClick?.Invoke(sender, e);
    }

    private void HotbarSlot_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: string tag } button && int.TryParse(tag.AsSpan("Hotbar".Length), out var index))
            HotbarMenuRequested?.Invoke(index, button);
    }

    // Click opens the slots immediately (hover also opens them via HoverFlyout).
    private void QuickFx_Click(object sender, RoutedEventArgs e)
    {
        RealizeTools();
        _quickFxHover?.Open();
    }

    // Keyboard: Down/Alt+Down opens the slots and focuses the first; Escape closes.
    private void QuickFx_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) != Key.Down)
            return;

        e.Handled = true;
        RealizeTools();
        _quickFxHover?.Open();
        Dispatcher.BeginInvoke(() => _quickFxPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Loaded);
    }

    private void QuickFxFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        _quickFxHover?.Close();
        Keyboard.Focus(QuickFxButton);
    }

    /// <summary>Slots as a pointer context menu (Quick FX entry in "more" when overflowed).</summary>
    private void OpenHotbarMenu()
    {
        RealizeTools();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        foreach (var slot in HotbarSlots)
        {
            var item = new MenuItem { Header = slot.Content, ToolTip = slot.ToolTip };
            item.Click += (_, e) => ToolClick?.Invoke(slot, e);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ----- Row scroll bar (two-way sync with the surface) -----

    private void UpdateRowScroll()
    {
        var (extent, height) = (Surface.ExtentHeight, Surface.ActualHeight);
        var room = Math.Max(extent - height, 0);
        _showingRowScroll = true;
        try
        {
            (RowScroll.Minimum, RowScroll.Maximum, RowScroll.ViewportSize) = (0, room, Math.Max(height, 1));
            (RowScroll.LargeChange, RowScroll.SmallChange) = (Math.Max(height * 0.9, 1), 24);
            RowScroll.Value = Math.Clamp(Surface.VerticalOffset, 0, room);
        }
        finally
        {
            _showingRowScroll = false;
        }

        // Hidden (not collapsed) when nothing scrolls, so its width is kept and the ruler never shifts.
        RowScroll.Visibility = room > 0.5 ? Visibility.Visible : Visibility.Hidden;
    }

    private void RowScroll_Scroll(object sender, ScrollEventArgs e)
    {
        if (!_showingRowScroll)
            Surface.VerticalOffset = RowScroll.Value;
    }

    // ----- Add Track: click adds an empty track; hover opens the kinds flyout -----

    // Keyboard: Down/Alt+Down opens the kinds and focuses the first; Escape closes.
    private void AddLayerButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) != Key.Down)
            return;

        e.Handled = true;
        RealizeTools();
        _addLayerHover!.Open();
        Dispatcher.BeginInvoke(() => _addLayerFlyoutPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Loaded);
    }

    private void AddLayerFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        _addLayerHover?.Close();
        Keyboard.Focus(AddLayerButton);
    }

    // Close the flyout before forwarding, since the action may open a dialog.
    private void AddLayerItem_Click(object sender, RoutedEventArgs e)
    {
        _addLayerHover?.Close();
        ToolClick?.Invoke(sender, e);
    }

    /// <summary>Add Track kinds as a pointer context menu ("more" entry when overflowed).</summary>
    private void OpenKindsMenu()
    {
        RealizeTools();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        foreach (var child in _kindsPanel.Children)
        {
            if (child is Separator)
            {
                menu.Items.Add(new Separator());
            }
            else if (child is Button { Content: string header } kind)
            {
                var item = new MenuItem { Header = header, ToolTip = kind.ToolTip };
                item.Click += (_, e) => ToolClick?.Invoke(kind, e);
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    // ----- Recycle bin -----

    // Disable the toggle's hit-testing while open so the closing click does not immediately reopen it.
    private void BinPopup_Opened(object? sender, EventArgs e) => BinButton.IsHitTestVisible = false;

    private void BinPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => BinButton.IsHitTestVisible = true, DispatcherPriority.Input);
}
