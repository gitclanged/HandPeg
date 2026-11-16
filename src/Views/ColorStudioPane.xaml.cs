using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

/// <summary>
/// The Color tab: the Color Grade card of what the editing panes work on (<see cref="MainViewModel.EditTarget"/>: a clip, the
/// blur fill or the Sequence Output), as wheels (Lift, Gamma, Gain, each with its master), curves over the picture's histogram before the
/// grade, the white balance, exposure, contrast, saturation and vibrance, a LUT and its mix, Color Match (lane B's bar), the whole picture's
/// Output switches and, while the project still has it, the old Filters grade with Convert. The first edit of a target without a card puts
/// one on (one step of Undo); a drag is one step, written at most 30 times a second; the card is found again whenever its stack changes
/// (Undo puts new cards in). Light to make: everything is built the first time the tab is shown, and the histogram is captured only while
/// it is in sight and the player paused (Law 3).
/// </summary>
public partial class ColorStudioPane : UserControl
{
    /// <summary>The wheels' cards, in their order: Lift, Gamma, Gain (each its red, green and blue keys).</summary>
    internal static readonly (string Title, string R, string G, string B)[] WheelKeys =
    [
        ("Lift", ColorGradeKeys.LiftR, ColorGradeKeys.LiftG, ColorGradeKeys.LiftB),
        ("Gamma", ColorGradeKeys.GammaR, ColorGradeKeys.GammaG, ColorGradeKeys.GammaB),
        ("Gain", ColorGradeKeys.GainR, ColorGradeKeys.GainG, ColorGradeKeys.GainB),
    ];

    /// <summary>The sliders of the Basics card, in their order.</summary>
    internal static readonly string[] BasicKeys =
        [ColorGradeKeys.Temperature, ColorGradeKeys.Tint, ColorGradeKeys.Exposure, ColorGradeKeys.Contrast, ColorGradeKeys.Saturation, ColorGradeKeys.Vibrance];

    private MainViewModel? _vm;
    private Func<Guid, Task<(byte[] Bgra, int Width, int Height)?>>? _capture;
    private Func<Func<ColorGradeTarget>, FrameworkElement?>? _colorMatch;

    private ColorGradeTarget _target;
    private ObservableCollection<EffectInstance>? _stack;
    private EffectInstance? _card;
    private bool _built, _listening, _refreshQueued, _settingControls;

    // The parts, made by Build.
    private TextBlock _targetLabel = null!, _empty = null!;
    private ToggleButton _sequenceToggle = null!, _compareEye = null!;
    private Button _resetButton = null!;
    private StackPanel _body = null!;
    private readonly ColorWheelsControl[] _wheels = new ColorWheelsControl[3];
    private ColorCurvesControl _curves = null!;
    private readonly Dictionary<string, SliderRow> _rows = new(StringComparer.Ordinal);
    private TextBox _lutBox = null!;

    /// <summary>
    /// A slider of the card's: its parameter, the slider, its value box, whether the slider's thumb is held, and the box's text as the pane
    /// last put it (5.6A: a box whose text is still that holds no edit of the user's: leaving it writes nothing, and an Undo refreshes it).
    /// </summary>
    private sealed class SliderRow(EffectParam param, Slider slider, TextBox box)
    {
        public EffectParam Param { get; } = param;

        public Slider Slider { get; } = slider;

        public TextBox Box { get; } = box;

        public bool Dragging { get; set; }

        public string Shown { get; set; } = box.Text;
    }

    public ColorStudioPane()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Wake();
        Loaded += (_, _) => Wake();
        Unloaded += (_, _) => StopListening();
    }

    /// <summary>
    /// Wires the pane to the window (MainWindow.ColorStudio.cs, as the tab is realized): the view model (whose edit target it follows,
    /// <see cref="MainViewModel.EditTarget"/>), the capture of the input still (the frame without a card:
    /// GpuPreviewEngine.CaptureInputThumbnailAsync) and Color Match's bar. Nothing is built or listened to until the tab is shown.
    /// </summary>
    public void Attach(MainViewModel viewModel, Func<Guid, Task<(byte[] Bgra, int Width, int Height)?>> capture,
        Func<Func<ColorGradeTarget>, FrameworkElement?>? colorMatch)
    {
        (_vm, _capture, _colorMatch) = (viewModel, capture, colorMatch);
        viewModel.EditTargetChanged += Retarget;
        Wake();
    }

    /// <summary>The Compare eye was switched (on: the player splits Before and After at a divider).</summary>
    public event Action<bool>? CompareToggled;

    /// <summary>The Compare eye shown on or off (the window's state: a hotkey may have changed it).</summary>
    public void ShowCompare(bool on)
    {
        if (_built && _compareEye.IsChecked != on)
            _compareEye.IsChecked = on;
    }

    // ----- For the goldens -----

    internal bool IsBuilt => _built;

    internal ColorGradeTarget Target => _target;

    internal EffectInstance? Card => _card;

    internal ColorWheelsControl Wheel(int index) => _wheels[index];

    internal ColorCurvesControl Curves => _curves;

    /// <summary>Retargets the pane by hand (the goldens; the window's pane follows the Effects pane).</summary>
    internal void SetTargetForTests(ColorGradeTarget target)
    {
        _vm?.SetEditTarget(target);
        SetTarget(target);
    }

    // ----- Built and listening only while shown -----

    private void Wake()
    {
        if (_vm is null || !IsLoaded || !IsVisible)
        {
            if (!IsVisible)
                StopListening();
            return;
        }

        if (!_built)
            Build();
        StartListening();
        Retarget();
        Want();
    }

    private void StartListening()
    {
        if (_listening || _vm is not { } vm)
            return;

        _listening = true;
        vm.PropertyChanged += ViewModel_PropertyChanged;
        vm.TimelineChanged += Invalidated;
        vm.PreviewStateInvalidated += Invalidated;
    }

    private void StopListening()
    {
        if (!_listening || _vm is not { } vm)
            return;

        _listening = false;
        vm.PropertyChanged -= ViewModel_PropertyChanged;
        vm.TimelineChanged -= Invalidated;
        vm.PreviewStateInvalidated -= Invalidated;
        _throttle?.Stop();
        FlushWrites();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // A paused scrub: captured once it rests; keyed values follow the playhead.
            case nameof(MainViewModel.PositionMs):
                if (_card is { Keys.Count: > 0 })
                    QueueRefresh();
                if (_vm is { IsPlaying: false })
                    Want(afterRest: true);
                break;
            case nameof(MainViewModel.IsPlaying) or nameof(MainViewModel.HasSource):
                Want();
                break;
            case nameof(MainViewModel.ColorContrast) or nameof(MainViewModel.ColorBrightness) or nameof(MainViewModel.ColorSaturation) or nameof(MainViewModel.ColorGamma)
                or nameof(MainViewModel.ColorHue) or nameof(MainViewModel.ColorRed) or nameof(MainViewModel.ColorGreen) or nameof(MainViewModel.ColorBlue)
                or nameof(MainViewModel.SharpenStrength) or nameof(MainViewModel.LutPath):
                UpdateLegacy();
                break;
        }
    }

    // ----- The target -----

    /// <summary>The view model's edit target taken (it changed, or the tab was shown).</summary>
    private void Retarget()
    {
        if (!_built)
            return;
        SetTarget(_vm?.EditTarget ?? _target);
    }

    private void SetTarget(ColorGradeTarget target)
    {
        if (_vm is not { } vm)
            return;

        var stack = vm.ColorGradeStack(target);
        if (target == _target && ReferenceEquals(stack, _stack))
        {
            Resolve();
            return;
        }

        FlushWrites();
        if (_stack is not null)
            _stack.CollectionChanged -= Stack_CollectionChanged;
        (_target, _stack) = (target, stack);
        if (_stack is not null)
            _stack.CollectionChanged += Stack_CollectionChanged;
        Adopt(null);
        Resolve();
        Want();
    }

    private void Stack_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resolve();

    /// <summary>
    /// The target's card found again (a new target, a stack changed: Undo puts new cards in), and the controls shown from it. A target that
    /// cannot be graded (5.6A: a sound clip can be the Effects pane's) has no card here, whatever its stack holds: nothing writes to it.
    /// </summary>
    private void Resolve()
    {
        Adopt(_vm is { } vm && vm.CanColorGrade(_target) ? vm.FindColorGrade(_target) : null);
        RefreshControls();
    }

    private void Adopt(EffectInstance? card)
    {
        if (ReferenceEquals(card, _card))
            return;
        if (_card is not null)
            _card.ValueChanged -= Card_ValueChanged;
        _card = card;
        if (_card is not null)
            _card.ValueChanged += Card_ValueChanged;
    }

    // A value changed elsewhere (the Effects pane's card, Color Match, keyframes): shown once the burst is over.
    private void Card_ValueChanged(EffectInstance card, string key)
    {
        if (!_writing)
            QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !_built)
            return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            RefreshControls();
        }, DispatcherPriority.DataBind);
    }

    /// <summary>Where the playhead is in the target's own time (a clip's keys count from its start; the Sequence Output's from 0).</summary>
    private double ClipTime => (_vm?.PositionMs ?? 0) / 1000 - (_target.Layer?.StartTime ?? 0);

    /// <summary>A value as the card has it now (its keys at the playhead when it has some); a new card's when there is no card.</summary>
    private double ValueOf(string key)
    {
        if (_card is not { } card)
            return EffectRegistry.Find(EffectRegistry.ColorStudio)?.Param(key)?.Default ?? 0;
        return card.HasKeys(key) ? EasingCurves.ValueAt(card.GetKeys(key), Math.Max(ClipTime, 0)) : card.Get(key);
    }

    /// <summary>Every control shown from the card (a control under a gesture keeps what the gesture made).</summary>
    private void RefreshControls()
    {
        if (!_built || _vm is not { } vm)
            return;

        _settingControls = true;
        try
        {
            var gradable = !_target.IsNone && vm.CanColorGrade(_target);
            _targetLabel.Text = _target.Sequence ? "Sequence Output" : _target.Layer is { } layer ? layer.Name : "No clip";
            _sequenceToggle.IsChecked = _target.Sequence;
            _body.Visibility = gradable ? Visibility.Visible : Visibility.Collapsed;
            _empty.Visibility = gradable ? Visibility.Collapsed : Visibility.Visible;
            _empty.Text = _target.IsNone ? "Select a clip" : "Nothing to grade";
            _resetButton.IsEnabled = gradable && _card is not null;

            for (var i = 0; i < 3; i++)
            {
                if (!_wheels[i].IsDragging)
                    _wheels[i].Value = (ValueOf(WheelKeys[i].R), ValueOf(WheelKeys[i].G), ValueOf(WheelKeys[i].B));
            }

            _curves.SetCurves(Text(ColorGradeKeys.CurveMaster), Text(ColorGradeKeys.CurveRed), Text(ColorGradeKeys.CurveGreen), Text(ColorGradeKeys.CurveBlue));
            foreach (var row in _rows.Values)
            {
                if (row.Dragging)
                    continue;
                var value = ValueOf(row.Param.Key);
                row.Slider.Value = value;

                // A box being typed into keeps its text; a focused one the user has not edited follows (an Undo, a retarget: 5.6A).
                if (BoxFollows(row.Box.IsKeyboardFocusWithin, row.Box.Text, row.Shown))
                    row.Box.Text = row.Shown = Format(row.Param, value);
            }

            if (BoxFollows(_lutBox.IsKeyboardFocusWithin, _lutBox.Text, _lutShown))
                _lutBox.Text = _lutShown = Text(ColorGradeKeys.Lut);
        }
        finally
        {
            _settingControls = false;
        }

        UpdateLegacy();
        string Text(string key) => _card?.GetText(key) ?? "";
    }

    /// <summary>
    /// Whether a box shows the value as it is now (5.6A): an unfocused one always; a focused one only while its text is still what was last
    /// shown (an Undo with the box still focused after Enter), so text being typed is never overwritten. Pure (the goldens).
    /// </summary>
    internal static bool BoxFollows(bool focused, string text, string shown) => !focused || text == shown;

    private static string Format(EffectParam param, double value)
    {
        var decimals = param.Step >= 1 ? 0 : param.Step >= 0.1 ? 1 : param.Step >= 0.01 ? 2 : 3;
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture) + param.Unit;
    }

    // ----- Building (the first time the tab is shown) -----

    private void Build()
    {
        _built = true;
        var definition = EffectRegistry.Find(EffectRegistry.ColorStudio);

        // The header: what is graded, the Sequence Output, the Compare eye, Reset.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        _resetButton = new Button { Content = "Reset", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Reset the whole grade" };
        AutomationProperties.SetName(_resetButton, "Reset Grade");
        _resetButton.Click += (_, _) => ResetGrade();
        DockPanel.SetDock(_resetButton, Dock.Right);
        header.Children.Add(_resetButton);

        _compareEye = new ToggleButton
        {
            Content = new TextBlock { Text = Glyphs.View, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 },
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Split Before and After on the player",
        };
        AutomationProperties.SetName(_compareEye, "Compare");
        _compareEye.Click += (_, _) => CompareToggled?.Invoke(_compareEye.IsChecked == true);
        DockPanel.SetDock(_compareEye, Dock.Right);
        header.Children.Add(_compareEye);

        _sequenceToggle = new ToggleButton { Content = "Sequence Output", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Grade the whole sequence" };
        AutomationProperties.SetName(_sequenceToggle, "Sequence Output");
        _sequenceToggle.Click += (_, _) => ToggleSequence(_sequenceToggle.IsChecked == true);
        DockPanel.SetDock(_sequenceToggle, Dock.Right);
        header.Children.Add(_sequenceToggle);

        _targetLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetName(_targetLabel, "Color Target");
        header.Children.Add(_targetLabel);
        Root.Children.Add(header);

        _empty = new TextBlock { Margin = new Thickness(2, 8, 2, 8), Visibility = Visibility.Collapsed };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(_empty, Dock.Top);
        Root.Children.Add(_empty);

        _body = new StackPanel();
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = _body };
        Root.Children.Add(scroller);

        // The wheels, side by side.
        var wheels = new UniformGrid { Columns = 3 };
        for (var i = 0; i < 3; i++)
        {
            var wheel = _wheels[i] = new ColorWheelsControl { Title = WheelKeys[i].Title, Margin = new Thickness(4, 0, 4, 0) };
            var keys = WheelKeys[i];
            wheel.EditStarted += _ => BeginGesture();
            wheel.EditEnded += _ => EndGesture();
            wheel.ValueChanged += w =>
            {
                QueueNumber(keys.R, w.Value.R);
                QueueNumber(keys.G, w.Value.G);
                QueueNumber(keys.B, w.Value.B);
            };
            wheels.Children.Add(wheel);
        }

        _body.Children.Add(InspectorCard("Wheels", wheels));

        // The curves, over the input's histogram.
        _curves = new ColorCurvesControl { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        _curves.EditStarted += BeginGesture;
        _curves.EditEnded += EndGesture;
        _curves.CurveChanged += (channel, text) => QueueText(ColorGradeKeys.Curves[channel], text);
        _body.Children.Add(InspectorCard("Curves", _curves));

        // The white balance, exposure and the rest.
        var basics = new StackPanel();
        foreach (var key in BasicKeys)
        {
            if (definition?.Param(key) is { } param)
                basics.Children.Add(Row(param));
        }

        _body.Children.Add(InspectorCard("Basics", basics));
        _body.Children.Add(InspectorCard("LUT", LutRows(definition)));

        // Color Match (lane B's bar), when there is one.
        if (_colorMatch?.Invoke(() => _target) is { } match)
            _body.Children.Add(InspectorCard("Color Match", match));

        _body.Children.Add(OutputFold());
        _body.Children.Add(LegacyFold());
    }

    /// <summary>An inspector card (CLAUDE.md 10.3): its title over its content, on the cards' surface and edge.</summary>
    private static Border InspectorCard(string title, UIElement content)
    {
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        stack.Children.Add(heading);
        stack.Children.Add(content);
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "InspectorCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "InspectorCardBorderBrush");
        AutomationProperties.SetName(card, title);
        return card;
    }

    /// <summary>A number of the card as a row: its name (double-click: back to its default), a slider and a value box.</summary>
    private FrameworkElement Row(EffectParam param)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var label = new TextBlock { Text = param.Label, VerticalAlignment = VerticalAlignment.Center, ToolTip = param.Tip ?? $"{param.Label} (double-click to reset)" };
        var slider = new Slider
        {
            Minimum = param.Min, Maximum = param.Max, SmallChange = param.Step, LargeChange = param.Step * 10, IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), Value = param.Default,
        };
        AutomationProperties.SetName(slider, param.Label);
        var box = new TextBox { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Text = Format(param, param.Default) };
        AutomationProperties.SetName(box, param.Label + " Value");
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(box);

        var row = _rows[param.Key] = new SliderRow(param, slider, box);
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            row.Dragging = true;
            BeginGesture();
        }));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            row.Dragging = false;
            EndGesture();
        }));
        slider.ValueChanged += (_, e) =>
        {
            if (_settingControls)
                return;
            box.Text = row.Shown = Format(param, e.NewValue);
            QueueNumber(param.Key, e.NewValue);
        };
        label.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                QueueNumber(param.Key, param.Default);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                CommitBox(row);
        };
        box.LostKeyboardFocus += (_, _) => CommitBox(row);
        return grid;
    }

    /// <summary>
    /// A value box's text written (Enter, or the box left): only text the user changed (5.6A: leaving an untouched box adds no card, key or
    /// step, and never writes a value an Undo took back); its unit stripped when the parameter has one (Temperature and the rest have none).
    /// </summary>
    private void CommitBox(SliderRow row)
    {
        if (row.Box.Text == row.Shown)
            return;

        var unit = row.Param.Unit.Trim();
        var text = (unit.Length == 0 ? row.Box.Text : row.Box.Text.Replace(unit, "", StringComparison.Ordinal)).Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        {
            var held = Math.Max(row.Param.Min, Math.Min(value, row.Param.Max));
            row.Box.Text = row.Shown = Format(row.Param, held);
            QueueNumber(row.Param.Key, held);
        }
        else
        {
            row.Box.Text = row.Shown = Format(row.Param, ValueOf(row.Param.Key));
        }
    }

    private void ToggleSequence(bool on)
    {
        if (_vm is not null)
            _vm.EditSequenceOutput = on;
        else
            SetTarget(on ? ColorGradeTarget.ForSequence : ColorGradeTarget.None);
    }
}
