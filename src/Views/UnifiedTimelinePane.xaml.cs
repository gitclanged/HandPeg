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
/// until the window gives it rows. The row of tools is made with it (5.0, the user's Q5: zero pop-in); what is hidden at
/// the first frame (Text Settings, the inspector, the Add Layer and recycle bin flyouts: DetailsTemplate) is not made at
/// all until the window is on screen (<see cref="RealizeTools"/>).
/// </summary>
public partial class UnifiedTimelinePane : UserControl
{
    private bool _showingRowScroll;

    // What is hidden under the tracks, found in DetailsTemplate as it is made (RealizeTools); nothing touches them before.
    private bool _toolsMade;
    private HoverFlyout? _addLayerHover;
    private FrameworkElement _addLayerFlyoutPanel = null!;
    private Panel _kindsPanel = null!;
    private Border _inspector = null!;
    private TextBlock _inspectedName = null!;
    private FrameworkElement _pictureGroup = null!, _soundGroup = null!, _backgroundGroup = null!, _soundSpeedHost = null!, _inspectorDelete = null!;
    private FrameworkElement _textSettingsPanel = null!;
    private ComboBox _fontBox = null!;

    // What the inspector was asked to show before it was made: shown as it is made.
    private (string Name, object? Picture, object? Sound, object? Background)? _inspectLater;

    public UnifiedTimelinePane()
    {
        InitializeComponent();

        // The ruler and the time's scroll bar begin where the rows' headers end; the zoom's corner beside the ruler is as wide
        // as the headers (the tracks' height is on the row of tools, with the other layer tools).
        var headers = Surface.HeaderWidth;
        ZoomCorner.Width = headers;
        Ruler.Margin = new Thickness(headers, 0, 0, 2);
        TimeScroll.Margin = new Thickness(headers, 2, 0, 0);

        Surface.ExtentChanged += UpdateRowScroll;
        Surface.VerticalOffsetChanged += UpdateRowScroll;
        Surface.SizeChanged += (_, _) => UpdateRowScroll();

        // Left out of a narrow row, Add Layer is still in the "more" menu: as a menu of its kinds.
        Toolbar.MenuEntryFor = element => ReferenceEquals(element, AddLayerHost) ? ("Add Layer", new Action(OpenKindsMenu)) : null;
    }

    /// <summary>
    /// Makes what is under the tracks and hidden at the first frame (5.0, the user's Q5; DetailsTemplate): Text Settings, the
    /// inspector, and the flyouts of the row of tools (Add Layer's kinds, the recycle bin's list). The window calls it once it
    /// is on screen (at ContextIdle after ContentRendered), so the first frame never waits for them; they start hidden and
    /// DetailsHost takes no room until something is selected, so nothing moves when they come. The row of tools itself is
    /// made with the pane. Once: again does nothing. Anything that needs them before (the "more" menu's Add Layer, resting on
    /// the bin) makes them at once.
    /// </summary>
    /// <summary>The inspector's place among the row's priorities: above every tool (Add Layer is 100).</summary>
    private const int InspectorPriority = 1000;

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

        // Add Layer's kinds open as the Settings button's do: resting on it, and close a moment after the pointer leaves. The
        // flyouts' targets are in the pane's own name scope, so they are given here, not by ElementName.
        var addLayer = Part<Popup>("AddLayerPopup");
        addLayer.PlacementTarget = AddLayerHost;
        _addLayerHover = new HoverFlyout(AddLayerHost, addLayer, _addLayerFlyoutPanel) { HoldsWhileFocused = true };

        // The bin's list opens with its button (a click on it before the list was made is let go of).
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

        // The inspector's groups are bound to nothing until something is selected (never to the window's view model, by inheritance).
        _pictureGroup.DataContext = _soundGroup.DataContext = _backgroundGroup.DataContext = null;

        DetailsHost.Child = tools;

        // The inspector is part of the row of tools (the user's call, 5.0-Verify: "a single row bottom toolbar"): after the
        // tools, before "more", and the most wanted thing on it, so a narrow row lets tools go into "more" first.
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

    // The font list is every font installed on the computer: read the first time Text Settings are shown, never while the
    // window is being made. The choice is bound only once the list is there, so a font never reads as "none" meanwhile.
    private void TextSettings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_textSettingsPanel.IsVisible)
            return;

        _textSettingsPanel.IsVisibleChanged -= TextSettings_IsVisibleChanged;
        _fontBox.ItemsSource = Services.TextLayout.SystemFonts;
        _fontBox.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding("FontFamily"));
    }

    /// <summary>A tool was used: a button of the tools' row, the inspector, the corners or the bin, known by its Tag (sender).</summary>
    public event RoutedEventHandler? ToolClick;

    private void Tool_Click(object sender, RoutedEventArgs e) => ToolClick?.Invoke(sender, e);

    /// <summary>
    /// Shows the values of what is selected in the inspector under the tracks: a picture clip's (<paramref name="picture"/>,
    /// a Layer), a sound's (<paramref name="sound"/>: an audio track of the main file, or a clip that carries sound), the
    /// background's blur (<paramref name="background"/>: what holds it, the view model). Each group edits what it is given;
    /// with none of them the inspector is hidden. Asked before the inspector is made (RealizeTools), it is shown as it is.
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
        // The captions are deleted from their own menu (they are words, not a clip): no Delete for their box.
        _inspectorDelete.Visibility = picture is not Models.Layer { IsCaptions: true } && (picture is not null || sound is not null) ? Visibility.Visible : Visibility.Collapsed;
        _inspectedName.Text = name;
        _inspector.Visibility = picture is not null || sound is not null || background is not null ? Visibility.Visible : Visibility.Collapsed;

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

    // A kind chosen: the flyout closes first (what it adds may open a window), then the window adds it.
    private void AddLayerItem_Click(object sender, RoutedEventArgs e)
    {
        _addLayerHover?.Close();
        ToolClick?.Invoke(sender, e);
    }

    /// <summary>The kinds of layer as a menu at the pointer: Add Layer's entry of the "more" menu, when the tools' row has no room for it.</summary>
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

    // ----- The recycle bin -----

    // While its list is open the button does not take the click that closes it (it would open it again at once).
    private void BinPopup_Opened(object? sender, EventArgs e) => BinButton.IsHitTestVisible = false;

    private void BinPopup_Closed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => BinButton.IsHitTestVisible = true, DispatcherPriority.Input);
}
