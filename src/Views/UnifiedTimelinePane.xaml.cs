using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandPegApp.Views;

/// <summary>
/// The timeline pane (4.4): the drawn surface of every track (<see cref="Surface"/>), its ruler, scroll bars, zoom, tools and
/// the inspector of what is selected. It edits nothing itself: every button goes out through <see cref="ToolClick"/>, its
/// Tag saying what it is, to the main window, which holds the selection and the view (MainWindow.UnifiedTimeline.cs).
/// What is its own: the rows' scroll bar following the surface, the Add Layer flyout and the recycle bin's popup.
/// Made before the first frame in every layout, so making it does nothing but lay out its parts: the surface is empty
/// until the window gives it rows.
/// </summary>
public partial class UnifiedTimelinePane : UserControl
{
    private readonly HoverFlyout _addLayerHover;
    private bool _showingRowScroll;

    public UnifiedTimelinePane()
    {
        InitializeComponent();

        // Add Layer's kinds open as the Settings button's do: resting on it, and close a moment after the pointer leaves.
        _addLayerHover = new HoverFlyout(AddLayerHost, AddLayerPopup, AddLayerFlyoutPanel) { HoldsWhileFocused = true };

        // Left out of a narrow title row, Add Layer is still in the "more" menu: as a menu of its kinds.
        Toolbar.MenuEntryFor = element => ReferenceEquals(element, AddLayerHost) ? ("Add Layer", new Action(OpenKindsMenu)) : null;

        // The ruler and the time's scroll bar begin where the rows' headers end; the corners beside them are as wide as the headers.
        var headers = Surface.HeaderWidth;
        ZoomCorner.Width = headers;
        HeightCorner.Width = headers;
        Ruler.Margin = new Thickness(headers, 0, 0, 2);
        TimeScroll.Margin = new Thickness(headers, 2, 0, 0);

        Surface.ExtentChanged += UpdateRowScroll;
        Surface.VerticalOffsetChanged += UpdateRowScroll;
        Surface.SizeChanged += (_, _) => UpdateRowScroll();
        TextSettingsPanel.IsVisibleChanged += TextSettings_IsVisibleChanged;

        // The inspector's groups are bound to nothing until something is selected (never to the window's view model, by inheritance).
        PictureGroup.DataContext = SoundGroup.DataContext = BackgroundGroup.DataContext = null;
    }

    // The font list is every font installed on the computer: read the first time Text Settings are shown, never while the
    // window is being made. The choice is bound only once the list is there, so a font never reads as "none" meanwhile.
    private void TextSettings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!TextSettingsPanel.IsVisible)
            return;

        TextSettingsPanel.IsVisibleChanged -= TextSettings_IsVisibleChanged;
        FontBox.ItemsSource = Services.TextLayout.SystemFonts;
        FontBox.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding("FontFamily"));
    }

    /// <summary>A tool was used: a button of the title row, the inspector, the corners or the bin, known by its Tag (sender).</summary>
    public event RoutedEventHandler? ToolClick;

    private void Tool_Click(object sender, RoutedEventArgs e) => ToolClick?.Invoke(sender, e);

    /// <summary>
    /// Shows the values of what is selected in the inspector under the tracks: a picture clip's (<paramref name="picture"/>,
    /// a Layer), a sound's (<paramref name="sound"/>: an audio track of the main file, or a clip that carries sound), the
    /// background's blur (<paramref name="background"/>: what holds it, the view model). Each group edits what it is given;
    /// with none of them the inspector is hidden.
    /// </summary>
    public void Inspect(string name, object? picture, object? sound, object? background)
    {
        Show(PictureGroup, picture);
        Show(SoundGroup, sound);
        Show(BackgroundGroup, background);
        SoundSpeedHost.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;
        InspectorDelete.Visibility = picture is not null || sound is not null ? Visibility.Visible : Visibility.Collapsed;
        InspectedName.Text = name;
        Inspector.Visibility = picture is not null || sound is not null || background is not null ? Visibility.Visible : Visibility.Collapsed;

        static void Show(FrameworkElement group, object? target)
        {
            // What a group is bound to is changed only when it is something else: a box being typed in keeps its value.
            if (!ReferenceEquals(group.DataContext, target))
                group.DataContext = target;
            group.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ----- The rows' scroll bar: it follows the surface, and moves it -----

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

        // Hidden rather than collapsed while every row is in sight: it keeps its room, so the ruler never moves sideways.
        RowScroll.Visibility = room > 0.5 ? Visibility.Visible : Visibility.Hidden;
    }

    private void RowScroll_Scroll(object sender, ScrollEventArgs e)
    {
        if (!_showingRowScroll)
            Surface.VerticalOffset = RowScroll.Value;
    }

    // ----- Add Layer: a click adds an empty track; resting on the button opens the kinds -----

    // From the keyboard: Down (or Alt+Down) on the button opens the kinds, the first one focused; Escape closes them again.
    private void AddLayerButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) != Key.Down)
            return;

        e.Handled = true;
        _addLayerHover.Open();
        Dispatcher.BeginInvoke(() => AddLayerFlyoutPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Loaded);
    }

    private void AddLayerFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        _addLayerHover.Close();
        Keyboard.Focus(AddLayerButton);
    }

    // A kind chosen: the flyout closes first (what it adds may open a window), then the window adds it.
    private void AddLayerItem_Click(object sender, RoutedEventArgs e)
    {
        _addLayerHover.Close();
        ToolClick?.Invoke(sender, e);
    }

    /// <summary>The kinds of layer as a menu at the pointer: Add Layer's entry of the "more" menu, when the title row has no room for it.</summary>
    private void OpenKindsMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        foreach (var child in KindsPanel.Children)
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

    // ----- The recycle bin -----

    // While its list is open the button does not take the click that closes it (it would open it again at once).
    private void BinPopup_Opened(object? sender, EventArgs e) => BinButton.IsHitTestVisible = false;

    private void BinPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => BinButton.IsHitTestVisible = true, DispatcherPriority.Input);
}
