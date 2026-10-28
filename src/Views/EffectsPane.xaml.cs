using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

/// <summary>
/// The Effects pane: one card per effect of the target's stack (the selected clip, or the Sequence Output), in the
/// order they are applied, with zone headings. Cards are built here, not templated: each parameter kind has its own
/// row, and the values are read from the card (or, for the effects whose values are the layer's, from the layer).
/// What the pane changes is the clip's own (Layer.Echo): the track's other clips do not follow it.
/// </summary>
public partial class EffectsPane : UserControl
{
    private MainViewModel? _vm;

    // The clip shown (null for the Sequence Output) and its stack.
    private Layer? _layer;
    private ObservableCollection<EffectInstance>? _stack;

    private readonly List<ParamModel> _params = [];
    private readonly HashSet<Guid> _collapsed = [];

    // Copy Stack's cards, for Paste Effects and Paste Selected Effects...: shared by every target.
    private static List<EffectState>? _clipboard;

    // The stack menu's "Apply Paste to All Clips on Track" (4.1.1-B), kept while HandPeg runs.
    private static bool _pasteToTrack;

    // The window whose clicks outside the Add Effect flyout close it, while it is open.
    private Window? _addOwner;

    // The Add Effect flyout closes a moment after the pointer leaves it and its button (the Settings button's 260 ms).
    private readonly DispatcherTimer _addClose = new() { Interval = TimeSpan.FromMilliseconds(260) };

    // A run of changes to values (a slider dragged) is one undo step: a checkpoint when the first comes after a pause.
    private DateTime _lastWrite;

    private bool _rebuildQueued, _refreshQueued;

    /// <summary>The Mask card's Edit Mask... button: the window opens the layer's mask tools.</summary>
    public event Action<Layer>? EditMaskRequested;

    private static readonly SolidColorBrush GreenCost = Frozen(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush AmberCost = Frozen(Color.FromRgb(0xF0, 0xA8, 0x30));
    private static readonly SolidColorBrush RedCost = Frozen(Color.FromRgb(0xE0, 0x4B, 0x4B));
    private static readonly FontFamily Glyphs = new("Segoe MDL2 Assets");

    public EffectsPane()
    {
        InitializeComponent();
        _addClose.Tick += AddClose_Tick;
        AddPopup.Opened += AddPopup_Opened;
        AddPopup.Closed += AddPopup_Closed;
        ReorderBehavior.Reordered += OnReordered;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Attach(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ActiveKeyLayer):
                    Retarget();
                    break;
                case nameof(MainViewModel.PositionMs):
                    OnPlayheadMoved();
                    break;
                case nameof(MainViewModel.SequenceEffectsBypassed):
                    UpdateHeader();
                    break;
            }
        };
        viewModel.Layers.CollectionChanged += (_, _) => Retarget();
        Retarget();
    }

    private bool IsSequence => SequenceToggle.IsChecked == true;

    /// <summary>Seconds into the target at the playhead: into the clip, or into the sequence.</summary>
    private double ClipTime => (_vm?.PositionMs ?? 0) / 1000 - (_layer is { IsMainVideo: false } layer ? layer.StartTime : 0);

    // ----- The target -----

    private void Retarget()
    {
        if (_vm is null)
            return;

        var layer = IsSequence ? null : _vm.ActiveKeyLayer;
        var stack = layer?.Effects ?? _vm.SequenceEffects;
        if (ReferenceEquals(layer, _layer) && ReferenceEquals(stack, _stack))
            return;

        if (_stack is not null)
            _stack.CollectionChanged -= Stack_CollectionChanged;
        if (_layer is not null)
            _layer.PropertyChanged -= Layer_PropertyChanged;
        (_layer, _stack) = (layer, stack);
        _stack.CollectionChanged += Stack_CollectionChanged;
        if (_layer is not null)
            _layer.PropertyChanged += Layer_PropertyChanged;
        Rebuild();
    }

    private void SequenceToggle_Click(object sender, RoutedEventArgs e) => Retarget();

    private void Stack_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Layer.Name) or nameof(Layer.EffectsBypassed):
                UpdateHeader();
                break;
            case Layer.EffectsChanged or Layer.KeysChanged or nameof(Layer.Opacity) or nameof(Layer.ChromaColor) or nameof(Layer.ChromaSimilarity) or nameof(Layer.ChromaBlend)
                or nameof(Layer.MaskGrow) or nameof(Layer.MaskInvert) or nameof(Layer.MaskOffsetX) or nameof(Layer.MaskOffsetY) or nameof(Layer.CornerRadius) or nameof(Layer.Feather) or nameof(Layer.FeatherRadius)
                or nameof(Layer.ShadowOpacity) or nameof(Layer.ShadowDistance) or nameof(Layer.ShadowAngle) or nameof(Layer.ShadowSoftness) or nameof(Layer.ShadowColor):
                QueueRefresh();
                break;
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
            return;

        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            foreach (var model in _params)
                model.Refresh();
        }, DispatcherPriority.Background);
    }

    private void OnPlayheadMoved()
    {
        foreach (var model in _params)
        {
            if (model.Animated)
                model.Refresh();
        }
    }

    private void UpdateHeader()
    {
        var bypassed = _layer?.EffectsBypassed ?? _vm?.SequenceEffectsBypassed ?? false;
        TargetLabel.Text = _layer is null ? "Sequence Output" : $"Clip: {_layer.Name}";
        BypassButton.IsChecked = bypassed;
        BypassButton.Content = bypassed ? "" : "";
        AddButton.IsEnabled = EffectRegistry.All.Any(d => Fits(d, ignorePresent: true));
    }

    // ----- The cards -----

    private void Rebuild()
    {
        CardsPanel.Children.Clear();
        PinnedPanel.Children.Clear();
        _params.Clear();
        UpdateHeader();
        if (_stack is null)
            return;

        EmptyHint.Text = _stack.Count > 0 ? ""
            : _layer is null ? "No effects on the Sequence Output. Add Effect puts one on the finished picture, over the whole sequence."
            : _layer.IsAdjustment ? "This adjustment layer has no effects yet: Add Effect, and they work on everything under it while it lasts."
            : _layer.HasFilters ? "No effects on this clip yet. Add Effect, or select another clip on the timeline (or Sequence Output)."
            : "This clip takes no effects: select a video, picture or text clip, or Sequence Output.";
        EmptyHint.Visibility = EmptyHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        EffectZone? zone = null;
        foreach (var card in _stack)
        {
            if (card.Definition is { } definition && definition.Zone != zone)
            {
                zone = definition.Zone;
                CardsPanel.Children.Add(ZoneHeading(definition.Zone));
            }

            CardsPanel.Children.Add(BuildCard(card));
        }

        // The opacity: always there for a picture, animated by the layer's own Opacity keyframes.
        if (_layer is { HasStyle: true, IsAdjustment: false } && EffectRegistry.Find(EffectRegistry.Opacity) is { } opacity)
            PinnedPanel.Children.Add(BuildCard(null, opacity));
    }

    private TextBlock ZoneHeading(EffectZone zone)
    {
        var heading = new TextBlock
        {
            Text = zone switch { EffectZone.Look => "LOOK", EffectZone.KeyAndMatte => "KEY & MATTE", EffectZone.Composite => "COMPOSITE", _ => zone.ToString().ToUpperInvariant() },
            FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 0, 3),
            ToolTip = zone switch
            {
                EffectZone.Look => "Colour and detail, applied first, in this order. Drag a card's grip to change the order.",
                EffectZone.KeyAndMatte => "Keys and everything that works on the transparency, after the colour, in this order.",
                _ => "How the layer is laid on the picture under it: a fixed order.",
            },
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return heading;
    }

    /// <param name="pinned">For the pinned opacity row: its definition (there is no card).</param>
    private Border BuildCard(EffectInstance? card, EffectDefinition? pinned = null)
    {
        var definition = card?.Definition ?? pinned;
        var title = card?.Title ?? pinned!.Name;
        var border = new Border { Tag = card, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(4, 3, 4, 5), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var root = new StackPanel();
        border.Child = root;

        var header = new DockPanel();
        root.Children.Add(header);
        var reorderable = card is not null && definition?.Reorderable == true;
        var grip = new TextBlock
        {
            Text = "⋮⋮", Width = 16, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Opacity = reorderable ? 1 : 0.25,
            Cursor = reorderable ? Cursors.SizeNS : Cursors.Arrow,
            ToolTip = reorderable ? "Drag to move this effect up or down within its zone." : "This effect's place is fixed.",
        };
        ReorderBehavior.SetIsGrip(grip, reorderable);
        header.Children.Add(grip);

        if (card is not null)
        {
            var eye = new ToggleButton
            {
                Content = card.Enabled ? "" : "", FontFamily = Glyphs, FontSize = 11, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 6, 0),
                IsChecked = card.Enabled, IsEnabled = !card.IsMissing, ToolTip = "On / off: an effect that is off does nothing, and keeps its values.",
            };
            AutomationProperties.SetName(eye, $"{title} On");
            eye.Click += (_, _) =>
            {
                _vm?.Checkpoint($"switch {title} {(eye.IsChecked == true ? "on" : "off")}");
                using (Layer.Echo())
                    card.Enabled = eye.IsChecked == true;
                eye.Content = card.Enabled ? "" : "";
            };
            header.Children.Add(eye);

            var remove = new Button { Content = "", FontFamily = Glyphs, FontSize = 10, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove this effect." };
            AutomationProperties.SetName(remove, $"Remove {title}");
            remove.Click += (_, _) => Remove(card);
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);
        }

        var collapsed = card is not null && _collapsed.Contains(card.Id);
        var collapse = new ToggleButton { Content = collapsed ? "" : "", FontFamily = Glyphs, FontSize = 10, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(4, 0, 0, 0), IsChecked = !collapsed, ToolTip = "Show or hide this effect's values." };
        AutomationProperties.SetName(collapse, $"Expand {title}");
        DockPanel.SetDock(collapse, Dock.Right);
        header.Children.Add(collapse);

        if (definition is not null)
        {
            var (brush, tip) = definition.Cost switch
            {
                EffectCost.Green => (GreenCost, "Green: Live Preview barely feels it."),
                EffectCost.Amber => (AmberCost, "Amber: felt on large frames in Live Preview."),
                _ => (RedCost, "Red: slows Live Preview; left out while scrubbing in Draft Mode (Settings)."),
            };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = brush, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            DockPanel.SetDock(dot, Dock.Right);
            header.Children.Add(dot);
        }

        header.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = definition?.Description is { Length: > 0 } description ? description : title,
        });

        var body = new StackPanel { Margin = new Thickness(16, 4, 0, 0), Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
        root.Children.Add(body);
        collapse.Click += (_, _) =>
        {
            var open = collapse.IsChecked == true;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            collapse.Content = open ? "" : "";
            if (card is not null && !open)
                _collapsed.Add(card.Id);
            else if (card is not null)
                _collapsed.Remove(card.Id);
        };

        if (definition is null)
        {
            var missing = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This project uses an effect this HandPeg does not have (from a plugin, or a later version). It does nothing here, and is saved back exactly as it was.",
            };
            missing.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            body.Children.Add(missing);
            return border;
        }

        foreach (var param in definition.Params)
            body.Children.Add(BuildRow(card, param, title));
        if (card is not null)
            body.Children.Add(BuildRangeRow(card, title));
        return border;
    }

    /// <summary>
    /// The card's range: on, the effect only works between two moments, in the clip's own seconds (the Sequence
    /// Output's: the sequence's); the buttons beside each box trim it to the playhead. Off, it works throughout.
    /// </summary>
    private FrameworkElement BuildRangeRow(EffectInstance card, string title)
    {
        var model = new RangeModel(this, card);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var on = new CheckBox
        {
            Content = "Range", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Only between two moments of the clip (its own seconds from where it begins); off, all of it. The buttons trim either end to the playhead.",
        };
        on.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(RangeModel.IsOn)) { Source = model, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(on, $"{title} Range");
        row.Children.Add(on);

        void Add(string property, string label, string glyph, string tip)
        {
            var box = new TextBox { Width = 52, Margin = new Thickness(4, 0, 0, 0), Style = TryFindResource("ValueBox") as Style, ToolTip = tip };
            box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = model, Mode = BindingMode.TwoWay, StringFormat = "{0:0.##}" });
            box.SetBinding(IsEnabledProperty, new Binding(nameof(RangeModel.IsOn)) { Source = model, Mode = BindingMode.OneWay });
            NumericInput.SetMinimum(box, 0);
            NumericInput.SetMaximum(box, 86400);
            NumericInput.SetDragStep(box, 0.05);
            NumericInput.SetDecimals(box, 2);
            AutomationProperties.SetName(box, $"{title} Range {label}");
            row.Children.Add(box);

            var trim = new Button { Content = glyph, FontFamily = Glyphs, FontSize = 10, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(2, 0, 0, 0), ToolTip = $"Trim to Playhead: the range {label.ToLowerInvariant()}s at the playhead." };
            trim.Click += (_, _) => model.TrimToPlayhead(property == nameof(RangeModel.End));
            AutomationProperties.SetName(trim, $"{title} Range {label} at Playhead");
            row.Children.Add(trim);
        }

        Add(nameof(RangeModel.Start), "Start", "\uE892", "Where the effect starts working, in seconds from the clip's start.");
        Add(nameof(RangeModel.End), "End", "\uE893", "Where it stops, in seconds from the clip's start.");
        return row;
    }

    /// <summary>Writes a card's range as one undo step (a value box dragged: one step per burst).</summary>
    private void WriteRange(EffectInstance card, (double Start, double End)? range, string what)
    {
        if (card.Range == range)
            return;

        Burst(what);
        using (Layer.Echo())
            card.SetRange(range);
    }

    private FrameworkElement BuildRow(EffectInstance? card, EffectParam param, string title)
    {
        var model = new ParamModel(this, card, param, _layer);
        _params.Add(model);
        var name = $"{title} {param.Label}";

        if (param.Kind == ParamKind.Toggle)
        {
            var check = new CheckBox { Content = param.Label, Margin = new Thickness(0, 2, 0, 2), ToolTip = param.Tip.Length > 0 ? param.Tip : null };
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ParamModel.IsOn)) { Source = model, Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(check, name);
            return check;
        }

        if (param.Kind == ParamKind.Action)
        {
            var action = new Button { Content = param.Label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = param.Tip };
            action.Click += (_, _) =>
            {
                if (_layer is not null)
                    EditMaskRequested?.Invoke(_layer);
            };
            AutomationProperties.SetName(action, name);
            return action;
        }

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = param.Tip.Length > 0 ? param.Tip : null };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = param.Unit.Length > 0 ? $"{param.Label} ({param.Unit})" : param.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        FrameworkElement editor;
        switch (param.Kind)
        {
            case ParamKind.Number:
            {
                var defaultText = param.Default.ToString(CultureInfo.InvariantCulture);
                var slider = new Slider
                {
                    Minimum = param.Min, Maximum = param.Max, SmallChange = param.Step, LargeChange = param.Step * 10, VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = param.Step >= 1, TickFrequency = param.Step, Margin = new Thickness(4, 0, 0, 0),
                };
                slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(ParamModel.Value)) { Source = model, Mode = BindingMode.TwoWay });
                NumericInput.SetDefault(slider, defaultText);
                AutomationProperties.SetName(slider, name);
                Grid.SetColumn(slider, 1);
                row.Children.Add(slider);

                var box = new TextBox { Width = 56, Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("ValueBox") as Style };
                box.SetBinding(TextBox.TextProperty, new Binding(nameof(ParamModel.Value))
                {
                    Source = model, Mode = BindingMode.TwoWay, StringFormat = "{0:0." + new string('#', Math.Max(param.Decimals, 1)) + "}",
                });
                NumericInput.SetDefault(box, defaultText);
                NumericInput.SetMinimum(box, param.Min);
                NumericInput.SetMaximum(box, param.Max);
                NumericInput.SetDragStep(box, param.Step);
                NumericInput.SetDecimals(box, param.Decimals);
                AutomationProperties.SetName(box, name + " Value");
                Grid.SetColumn(box, 2);
                row.Children.Add(box);

                if (param.Animation != ParamAnimation.None)
                    row.Children.Add(BuildKeyButtons(model, name));
                return row;
            }

            case ParamKind.Choice:
            {
                var combo = new ComboBox
                {
                    Margin = new Thickness(4, 0, 0, 0), DisplayMemberPath = "Value", SelectedValuePath = "Key",
                    ItemsSource = param.Choices.Select(c => new KeyValuePair<string, string>(c.Value, c.Label)).ToList(),
                };
                combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(ParamModel.Text)) { Source = model, Mode = BindingMode.TwoWay });
                editor = combo;
                break;
            }

            default:
            {
                var text = new TextBox { Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
                text.SetBinding(TextBox.TextProperty, new Binding(nameof(ParamModel.Text)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                editor = text;
                if (param.Kind == ParamKind.File)
                {
                    var browse = new Button { Content = "Browse...", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
                    browse.Click += (_, _) =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "3D LUT (*.cube)|*.cube|All files|*.*" };
                        if (dialog.ShowDialog(Window.GetWindow(this) ?? Application.Current.MainWindow) == true)
                            model.Text = dialog.FileName;
                    };
                    AutomationProperties.SetName(browse, $"Browse {name}");
                    Grid.SetColumn(browse, 2);
                    row.Children.Add(browse);
                }

                break;
            }
        }

        AutomationProperties.SetName(editor, name);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    /// <summary>The stopwatch (animate this value: keyframes from now on) and the diamond (a keyframe at the playhead).</summary>
    private StackPanel BuildKeyButtons(ParamModel model, string name)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(buttons, 3);
        var watch = new ToggleButton
        {
            Content = "", FontFamily = Glyphs, FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
            ToolTip = "Animate: on, this value gets keyframes (one at the playhead now; changing the value writes another). Off: the keyframes go and the value stays as it is here.",
        };
        watch.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ParamModel.Animated)) { Source = model, Mode = BindingMode.OneWay });
        watch.Click += (_, _) => ToggleAnimated(model);
        AutomationProperties.SetName(watch, $"Animate {name}");

        var diamond = new Button { FontSize = 12, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(2, 0, 0, 0), Foreground = Brushes.Gold, ToolTip = "A keyframe at the playhead: adds one holding the value, or removes the one there." };
        diamond.SetBinding(ContentProperty, new Binding(nameof(ParamModel.Diamond)) { Source = model, Mode = BindingMode.OneWay });
        diamond.SetBinding(VisibilityProperty, new Binding(nameof(ParamModel.DiamondVisibility)) { Source = model, Mode = BindingMode.OneWay });
        diamond.Click += (_, _) => ToggleKeyAtPlayhead(model);
        AutomationProperties.SetName(diamond, $"Keyframe {name}");
        buttons.Children.Add(watch);
        buttons.Children.Add(diamond);
        return buttons;
    }

    // ----- Writing values -----

    private void Burst(string what)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastWrite).TotalMilliseconds > 1000)
            _vm?.Checkpoint(what);
        _lastWrite = now;
    }

    private void WriteValue(ParamModel model, double value)
    {
        var param = model.Param;
        value = Math.Clamp(param.Step >= 1 ? Math.Round(value / param.Step) * param.Step : Math.Round(value, param.Decimals + 1), param.Min, param.Max);
        if (Math.Abs(value - model.Value) < 1e-9)
            return;

        Burst($"change {param.Label}");
        using (Layer.Echo())
        {
            // A value of the layer's own that is animated by commands (the key's) keeps its keyframes on the card.
            if (model.Card is { } keyed && param.Bind != nameof(Layer.Opacity) && keyed.HasKeys(param.Key))
                keyed.SetKeys(param.Key, WithKey(keyed.GetKeys(param.Key), ClipTime, value));
            else if (param.Bind is { } bind && _layer is not null && typeof(Layer).GetProperty(bind) is { } property)
                property.SetValue(_layer, property.PropertyType == typeof(int) ? (int)Math.Round(value) : property.PropertyType == typeof(bool) ? value >= 0.5 : value);
            else if (model.Card is { } card && card.HasKeys(param.Key))
                card.SetKeys(param.Key, WithKey(card.GetKeys(param.Key), ClipTime, value));
            else
                model.Card?.Set(param.Key, value);
        }

        model.Refresh();
    }

    private void WriteText(ParamModel model, string value)
    {
        if (value == model.Text)
            return;

        _vm?.Checkpoint($"change {model.Param.Label}");
        using (Layer.Echo())
        {
            if (model.Param.Bind is { } bind && _layer is not null && typeof(Layer).GetProperty(bind) is { PropertyType: var type } property && type == typeof(string))
                property.SetValue(_layer, value);
            else
                model.Card?.SetText(model.Param.Key, value);
        }

        model.Refresh();
    }

    private static List<Keyframe> WithKey(IReadOnlyList<Keyframe> keys, double time, double value)
    {
        var list = keys.ToList();
        var at = list.FindIndex(k => Math.Abs(k.Time - time) < 0.02);
        if (at >= 0)
            list[at] = list[at] with { Value = value };
        else
            list.Add(new Keyframe(Math.Max(time, 0), value));
        return list;
    }

    private void ToggleAnimated(ParamModel model)
    {
        var (param, time, value) = (model.Param, Math.Max(ClipTime, 0), model.Value);
        var on = !model.Animated;
        _vm?.Checkpoint($"{(on ? "animate" : "stop animating")} {param.Label}");
        using (Layer.Echo())
        {
            if (param.Bind == nameof(Layer.Opacity) && _layer is not null)
            {
                _layer.SetKeys(KeyProperty.Opacity, on ? [new Keyframe(time, _layer.Opacity)] : null);
            }
            else if (model.Card is { } card)
            {
                card.Set(param.Key, value);
                card.SetKeys(param.Key, on ? [new Keyframe(time, value)] : null);

                // A value of the layer's own stays as it was here.
                if (!on && param.Bind is { } bind && _layer is not null && typeof(Layer).GetProperty(bind) is { PropertyType: var type } property && type == typeof(double))
                    property.SetValue(_layer, value);
            }
        }

        model.Refresh();
    }

    private void ToggleKeyAtPlayhead(ParamModel model)
    {
        var (param, time, value) = (model.Param, Math.Max(ClipTime, 0), model.Value);
        var keys = model.Keys.ToList();
        var at = keys.FindIndex(k => Math.Abs(k.Time - time) < 0.02);
        if (at >= 0)
            keys.RemoveAt(at);
        else
            keys.Add(new Keyframe(time, value));

        _vm?.Checkpoint($"{(at >= 0 ? "remove a keyframe of" : "add a keyframe to")} {param.Label}");
        using (Layer.Echo())
        {
            if (param.Bind == nameof(Layer.Opacity) && _layer is not null)
            {
                _layer.SetKeys(KeyProperty.Opacity, keys);
            }
            else if (model.Card is { } card)
            {
                // The last keyframe gone, the value stays what it was there.
                if (keys.Count == 0)
                    card.Set(param.Key, value);
                card.SetKeys(param.Key, keys);
            }
        }

        model.Refresh();
    }

    /// <summary>Keyframes at a moment, eased as the graph eases them.</summary>
    private static double Evaluate(IReadOnlyList<Keyframe> keys, double time) => EasingCurves.ValueAt(keys, time);

    // ----- Adding, removing, reordering -----

    /// <summary>Whether an effect can go on the target: the whole picture takes the Look effects; a clip what its kind allows, and a bound effect once.</summary>
    private bool Fits(EffectDefinition definition, bool ignorePresent = false)
    {
        if (definition.Pinned || _stack is null)
            return false;
        if (_layer is null || _layer.IsAdjustment)
            return definition.ForWholePicture;

        var allowed = definition.Zone switch
        {
            EffectZone.Look => _layer.HasFilters,
            EffectZone.KeyAndMatte => definition.Type == EffectRegistry.ChromaKey ? _layer.HasKeying : _layer.HasMask && !_layer.IsCaptions,
            EffectZone.Composite => _layer.HasStyle && !_layer.IsCaptions,
            _ => false,
        };
        return allowed && (ignorePresent || definition.Feature is null && definition.Type != EffectRegistry.BlendMode || _layer.Find(definition.Type) is null);
    }

    private void Add(EffectDefinition definition)
    {
        if (_vm is null || _stack is null)
            return;

        CloseAdd();
        _vm.Checkpoint($"add {definition.Name}");
        using (Layer.Echo())
        {
            if (_layer is null)
            {
                var card = new EffectInstance { Type = definition.Type };
                _stack.Insert(Layer.InsertionIndex(_stack, card), card);
            }
            else
            {
                // The effects whose values are the layer's are switched on as Layer Properties would, which brings their card.
                switch (definition.Feature)
                {
                    case nameof(Layer.ChromaKey): _layer.ChromaKey = true; break;
                    case nameof(Layer.Shadow): _layer.Shadow = true; break;
                    case "Edges" when _layer.CornerRadius == 0 && !_layer.Feather: _layer.CornerRadius = 8; break;
                }

                if (_layer.Find(definition.Type) is null || definition.Feature is null && definition.Type != EffectRegistry.BlendMode)
                    _layer.AddEffect(new EffectInstance { Type = definition.Type });
            }
        }

        _vm.StatusText = $"{definition.Name} added to {(_layer is null ? "the Sequence Output" : _layer.Name)}. Ctrl+Z takes it off.";
        if (definition.Type == EffectRegistry.Mask && _layer is not null && !_layer.CustomMask)
            EditMaskRequested?.Invoke(_layer);
    }

    private void Remove(EffectInstance card)
    {
        if (_vm is null || _stack is null)
            return;

        _vm.Checkpoint($"remove {card.Title}");
        using (Layer.Echo())
        {
            if (_layer is not null)
                _layer.RemoveEffect(card);
            else
                _stack.Remove(card);
        }
    }

    private void OnReordered(Panel panel)
    {
        if (!ReferenceEquals(panel, CardsPanel) || _stack is null || _vm is null)
            return;

        var order = CardsPanel.Children.OfType<FrameworkElement>().Select(c => c.Tag).OfType<EffectInstance>().ToList();
        var zones = order.Select(c => c.Definition?.Zone).OfType<EffectZone>().ToList();
        var valid = order.Count == _stack.Count && zones.Zip(zones.Skip(1)).All(p => p.First <= p.Second);
        if (!valid || order.SequenceEqual(_stack))
        {
            if (!valid)
                _vm.StatusText = "Effects move within their own zone: keys and masks stay below the colour effects, and the composite below both.";
            Rebuild();
            return;
        }

        _vm.Checkpoint("reorder the effects");
        using (Layer.Echo())
        {
            for (var i = 0; i < order.Count; i++)
            {
                var from = _stack.IndexOf(order[i]);
                if (from != i)
                    _stack.Move(from, i);
            }
        }
    }

    // ----- The title row -----

    private void BypassButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var on = BypassButton.IsChecked == true;
        _vm.Checkpoint(on ? "bypass the effects" : "bring the effects back");
        using (Layer.Echo())
        {
            if (_layer is not null)
                _layer.EffectsBypassed = on;
            else
                _vm.SequenceEffectsBypassed = on;
        }

        UpdateHeader();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        void Item(string header, string tip, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, ToolTip = tip, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        var has = _stack is { Count: > 0 };
        var canPaste = _clipboard is { Count: > 0 } && _stack is not null;
        Item("Copy Stack", "Copies every effect of this stack, with its values, to paste onto another clip or the Sequence Output.", has, CopyStack);
        Item("Paste Effects", "Replaces this clip's stack with the effects copied. Hold Shift while clicking (or tick Apply Paste to All Clips on Track) to give every clip on this track the copied stack.",
            canPaste, () => PasteEffects(_pasteToTrack || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
        Item("Paste Selected Effects...", "Lists the copied effects, each with a box, to choose which are added to this clip (or every clip on its track).", canPaste, PasteSelected);
        var toTrack = new MenuItem
        {
            Header = "Apply Paste to All Clips on Track", IsCheckable = true, IsChecked = _pasteToTrack, StaysOpenOnClick = true, IsEnabled = CanPasteToTrack,
            ToolTip = "While ticked, Paste Effects gives every clip on the selected clip's track the copied stack, not only the clip.",
        };
        toTrack.Click += (_, _) => _pasteToTrack = toTrack.IsChecked;
        menu.Items.Add(toTrack);
        menu.Items.Add(new Separator());
        Item("Save Effect Preset...", $"Saves this stack as a .hpfx file in {AppPaths.EffectPresets}; it is then listed under Presets in Add Effect.", has, SavePreset);
        Item("Reset", "Removes every effect of this stack (Ctrl+Z brings them back).", has, ResetStack);
        menu.IsOpen = true;
    }

    private List<EffectState> CaptureStack() => _stack is null ? [] : [.. _stack.Select(c => _layer?.CaptureCard(c) ?? c.ToState())];

    private void CopyStack()
    {
        _clipboard = CaptureStack();
        if (_vm is not null)
            _vm.StatusText = $"Copied {_clipboard.Count} effect{(_clipboard.Count == 1 ? "" : "s")}: Paste Effects puts them on another clip.";
    }

    /// <summary>Whether the target is a clip on a track (the main video and the Sequence Output are not).</summary>
    private bool CanPasteToTrack => _layer is not null && _vm?.Layers.Contains(_layer) == true;

    /// <summary>Paste Effects (4.1.1-B, Paste Stack and Paste to Track in one): the copied stack replaces the clip's, or every clip's on its track.</summary>
    private void PasteEffects(bool toTrack)
    {
        if (_clipboard is null || _vm is null)
            return;

        if (toTrack && CanPasteToTrack)
        {
            _vm.PasteEffectsToTrack(_layer!, _clipboard);
            return;
        }

        _vm.Checkpoint("paste effects");
        ReplaceWith(_clipboard, append: false);
    }

    /// <summary>Paste Selected Effects... (4.1.1-B): the copied effects ticked in a list are added to the clip, or to every clip on its track.</summary>
    private void PasteSelected()
    {
        if (_clipboard is null || _vm is null || _stack is null)
            return;

        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        var target = _layer is null ? "the Sequence Output" : _layer.Name;
        var dialog = new PasteEffectsDialog(_clipboard, target, CanPasteToTrack, _pasteToTrack) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Chosen is not { Count: > 0 } chosen)
            return;

        if (dialog.ToTrack && CanPasteToTrack)
        {
            _vm.PasteEffectsToTrack(_layer!, chosen, append: true);
            return;
        }

        _vm.Checkpoint($"paste {chosen.Count} effect{(chosen.Count == 1 ? "" : "s")}");
        ReplaceWith(chosen, append: true);
    }

    /// <summary>Puts cards on the target, adopted by it: what it cannot take is left out (a missing effect is kept); a bound effect it already has is not doubled.</summary>
    private void ReplaceWith(IReadOnlyList<EffectState> states, bool append)
    {
        if (_stack is null)
            return;

        using (Layer.Echo())
        {
            var keep = append ? _stack.ToList() : [];
            var cards = new List<EffectInstance>(keep);
            foreach (var state in states)
            {
                var definition = EffectRegistry.Find(state.Type);
                if (definition is not null && !Fits(definition, ignorePresent: true))
                    continue;

                // An effect a clip has once at most: added again, the pasted one takes the place of the one there (4.1.1-B).
                if (definition is { Feature: not null } && cards.FindIndex(c => c.Type == state.Type) is var present and >= 0)
                {
                    if (!append || present >= keep.Count)
                        continue;
                    cards.RemoveAt(present);
                    keep.RemoveAt(present);
                }

                cards.Add(_layer?.AdoptCard(state) ?? EffectInstance.FromState(state));
            }

            // Each in its zone, the order within a zone kept.
            cards = [.. cards.OrderBy(c => (int)(c.Definition?.Zone ?? EffectZone.Look))];
            if (_layer is not null)
            {
                _layer.SetEffects(cards);
            }
            else
            {
                _stack.Clear();
                foreach (var card in cards)
                    _stack.Add(card);
            }
        }
    }

    private void SavePreset()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.EffectPresets);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = AppPaths.EffectPresets, Filter = "HandPeg effect preset (*.hpfx)|*.hpfx", DefaultExt = ".hpfx",
                FileName = _layer is null ? "Sequence Output" : _layer.Name,
            };
            if (dialog.ShowDialog(Window.GetWindow(this) ?? Application.Current.MainWindow) != true)
                return;

            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(CaptureStack(), new JsonSerializerOptions { WriteIndented = true }));
            if (_vm is not null)
                _vm.StatusText = $"Saved the effect preset {System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)}: Add Effect lists it under Presets.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_vm is not null)
                _vm.StatusText = $"The effect preset could not be saved: {ex.Message}";
        }
    }

    private void ApplyPreset(string path)
    {
        CloseAdd();
        try
        {
            var states = JsonSerializer.Deserialize<List<EffectState>>(File.ReadAllText(path)) ?? [];
            _vm?.Checkpoint($"apply the effect preset {System.IO.Path.GetFileNameWithoutExtension(path)}");
            ReplaceWith(states, append: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_vm is not null)
                _vm.StatusText = $"The effect preset could not be read: {ex.Message}";
        }
    }

    private void ResetStack()
    {
        if (_stack is null || _vm is null)
            return;

        _vm.Checkpoint("reset the effects");
        using (Layer.Echo())
        {
            foreach (var card in _stack.ToList())
            {
                if (_layer is not null)
                    _layer.RemoveEffect(card);
                else
                    _stack.Remove(card);
            }

            if (_layer is not null)
                _layer.EffectsBypassed = false;
            else
                _vm.SequenceEffectsBypassed = false;
        }
    }

    // ----- Add Effect: the flyout -----

    private static readonly string[] CategoryOrder = ["Color", "Detail", "Blur", "Look", "Transform", "Key", "Matte", "Composite"];

    private void OpenAdd(bool focusSearch)
    {
        _addClose.Stop();
        SearchBox.Text = "";
        BuildAddList("");
        AddPopup.IsOpen = true;
        if (focusSearch)
            Dispatcher.BeginInvoke(() => Keyboard.Focus(SearchBox), DispatcherPriority.Loaded);
    }

    private void BuildAddList(string search)
    {
        AddList.Children.Clear();
        bool Matches(string text) => search.Length == 0 || text.Contains(search, StringComparison.OrdinalIgnoreCase);
        var available = EffectRegistry.All.Where(d => Fits(d) && (Matches(d.Name) || Matches(d.Category)))
            .GroupBy(d => d.Category).OrderBy(g => Array.IndexOf(CategoryOrder, g.Key) is var i and >= 0 ? i : CategoryOrder.Length);
        foreach (var group in available)
        {
            AddList.Children.Add(MenuHeading(group.Key));
            foreach (var definition in group)
                AddList.Children.Add(MenuButton(definition.Name, $"{definition.Description} ({definition.Cost} cost in Live Preview.)", () => Add(definition)));
        }

        // Presets saved from this pane.
        var presets = Directory.Exists(AppPaths.EffectPresets)
            ? Directory.EnumerateFiles(AppPaths.EffectPresets, "*.hpfx").Where(f => Matches(System.IO.Path.GetFileNameWithoutExtension(f))).Order().ToList()
            : [];
        if (presets.Count > 0 && _stack is not null)
        {
            AddList.Children.Add(MenuHeading("Presets"));
            foreach (var preset in presets)
                AddList.Children.Add(MenuButton(System.IO.Path.GetFileNameWithoutExtension(preset), "Adds this saved stack's effects to the target.", () => ApplyPreset(preset)));
        }

        if (AddList.Children.Count == 0)
            AddList.Children.Add(MenuHeading(_stack is null ? "Nothing selected." : "No effect matches."));
    }

    private TextBlock MenuHeading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 6, 0, 2) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return heading;
    }

    private static Button MenuButton(string text, string tip, Action click)
    {
        var button = new Button
        {
            Content = text, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
        };
        button.SetResourceReference(ForegroundProperty, "TextBrush");
        button.Click += (_, _) => click();
        return button;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildAddList(SearchBox.Text.Trim());

    private void AddButton_Click(object sender, RoutedEventArgs e) => OpenAdd(focusSearch: true);

    private void AddHost_MouseEnter(object sender, MouseEventArgs e)
    {
        _addClose.Stop();
        if (!AddPopup.IsOpen && AddButton.IsEnabled)
            OpenAdd(focusSearch: false);
    }

    private void AddFlyout_MouseLeave(object sender, MouseEventArgs e)
    {
        _addClose.Stop();
        _addClose.Start();
    }

    private void AddClose_Tick(object? sender, EventArgs e)
    {
        _addClose.Stop();
        if (!AddHost.IsMouseOver && !AddFlyoutPanel.IsMouseOver && !AddFlyoutPanel.IsKeyboardFocusWithin)
            CloseAdd();
    }

    /// <summary>
    /// Closes the Add Effect flyout and takes the keyboard out of its search box (4.1.1-B): a focused search box kept
    /// the flyout open (see <see cref="AddClose_Tick"/>), and with it closed the typing would go nowhere.
    /// </summary>
    private void CloseAdd()
    {
        _addClose.Stop();
        var hadFocus = AddFlyoutPanel.IsKeyboardFocusWithin;
        AddPopup.IsOpen = false;
        if (hadFocus)
            Keyboard.Focus(AddButton);
    }

    // While the flyout is open, a press anywhere in the window outside it and its button closes it, and so does the
    // window losing the foreground: a search box with the keyboard otherwise held it open (4.1.1-B). A press inside the
    // flyout never reaches the window (a popup is a window of its own).
    private void AddPopup_Opened(object? sender, EventArgs e)
    {
        _addOwner = Window.GetWindow(this);
        if (_addOwner is null)
            return;

        _addOwner.PreviewMouseDown += AddOwner_PreviewMouseDown;
        _addOwner.Deactivated += AddOwner_Deactivated;
    }

    private void AddPopup_Closed(object? sender, EventArgs e)
    {
        if (_addOwner is not null)
        {
            _addOwner.PreviewMouseDown -= AddOwner_PreviewMouseDown;
            _addOwner.Deactivated -= AddOwner_Deactivated;
            _addOwner = null;
        }
    }

    private void AddOwner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddHost.IsMouseOver)
            CloseAdd();
    }

    private void AddOwner_Deactivated(object? sender, EventArgs e)
    {
        // The flyout's own window taking the keyboard deactivates nothing; another window does.
        if (!AddFlyoutPanel.IsKeyboardFocusWithin)
            CloseAdd();
    }

    private void AddButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) != Key.Down)
            return;

        e.Handled = true;
        OpenAdd(focusSearch: true);
    }

    private void AddFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CloseAdd();
        Keyboard.Focus(AddButton);
    }

    // ----- A parameter's row, as the bindings see it -----

    private sealed class ParamModel(EffectsPane pane, EffectInstance? card, EffectParam param, Layer? layer) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public EffectParam Param => param;

        public EffectInstance? Card => card;

        private System.Reflection.PropertyInfo? Bound => param.Bind is { } name && layer is not null ? typeof(Layer).GetProperty(name) : null;

        public IReadOnlyList<Keyframe> Keys => param.Bind == nameof(Layer.Opacity) && layer is not null ? layer.GetKeys(KeyProperty.Opacity)
            : card?.GetKeys(param.Key) ?? [];

        public bool Animated => param.Animation != ParamAnimation.None && Keys.Count > 0;

        public double Value
        {
            get
            {
                if (card is not null && param.Bind != nameof(Layer.Opacity) && card.HasKeys(param.Key))
                    return Evaluate(card.GetKeys(param.Key), Math.Max(pane.ClipTime, 0));
                if (Bound is { } property)
                    return property.GetValue(layer) switch { int whole => whole, double number => number, bool on => on ? 1 : 0, _ => param.Default };
                if (card is null)
                    return param.Default;
                return card.HasKeys(param.Key) ? Evaluate(card.GetKeys(param.Key), Math.Max(pane.ClipTime, 0)) : card.Get(param.Key);
            }
            set => pane.WriteValue(this, value);
        }

        public bool IsOn
        {
            get => Value >= 0.5;
            set => pane.WriteValue(this, value ? 1 : 0);
        }

        public string Text
        {
            get => Bound is { } property ? property.GetValue(layer) as string ?? "" : card?.GetText(param.Key) ?? "";
            set => pane.WriteText(this, value ?? "");
        }

        public string Diamond => Keys.Any(k => Math.Abs(k.Time - Math.Max(pane.ClipTime, 0)) < 0.02) ? "◆" : "◇";

        public Visibility DiamondVisibility => Animated ? Visibility.Visible : Visibility.Collapsed;

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>A card's range, as its row's boxes see it: off, the boxes show where it would start (0) and end (the clip's end).</summary>
    private sealed class RangeModel(EffectsPane pane, EffectInstance card) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // How long the target is, for a range switched on: the clip's length, or 5 s for one with none.
        private double Length => pane._layer is { Duration: > 0.01 } clip ? clip.Duration : 5;

        public bool IsOn
        {
            get => card.Range is not null;
            set => Write(value ? (0, Length) : null, value ? "limit an effect to a range" : "let an effect work throughout");
        }

        public double Start
        {
            get => card.Range?.Start ?? 0;
            set => Write((Math.Clamp(value, 0, End - 0.01), End), "change an effect's range");
        }

        public double End
        {
            get => card.Range?.End ?? Length;
            set => Write((Start, Math.Max(value, Start + 0.01)), "change an effect's range");
        }

        /// <summary>One end of the range to the playhead (switching the range on).</summary>
        public void TrimToPlayhead(bool end)
        {
            var at = Math.Max(pane.ClipTime, 0);
            var (start, stop) = card.Range ?? (0, Length);
            if (end && at > start + 0.01)
                Write((start, at), "trim an effect's range to the playhead");
            else if (!end && at < stop - 0.01)
                Write((at, stop), "trim an effect's range to the playhead");
            else if (pane._vm is { } vm)
                vm.StatusText = (end ? "The playhead is before the range's start: move it later to end the range there." : "The playhead is after the range's end: move it earlier to start the range there.");
        }

        private void Write((double Start, double End)? range, string what)
        {
            pane.WriteRange(card, range is { } r ? (Math.Round(r.Start, 3), Math.Round(r.End, 3)) : null, what);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}
