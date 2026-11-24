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
/// Color tab: edits the Color Grade card of the current <see cref="MainViewModel.EditTarget"/> (clip, blur fill or Sequence Output) via
/// Lift/Gamma/Gain wheels with masters, curves over the live post-grade YRGB histogram, white balance, exposure, contrast, saturation,
/// vibrance, a LUT with mix, the Color Match bar and, for legacy projects, the old Filters grade with Convert, as deck cards that reflow
/// into one, two or three columns (ColorStudioPane.Deck.cs; the Output fold is retired: Deinterlace lives in Export). The first edit adds a
/// card (one Undo step); a drag is one step written at most 30 times per second; the card is re-resolved whenever its stack changes
/// (Undo inserts new instances). Built lazily on first show; the histogram is captured only while visible and paused (Law 3).
/// </summary>
public partial class ColorStudioPane : UserControl
{
    /// <summary>Wheel cards in display order (Lift, Gamma, Gain), each with its R/G/B parameter keys.</summary>
    internal static readonly (string Title, string R, string G, string B)[] WheelKeys =
    [
        ("Lift", ColorGradeKeys.LiftR, ColorGradeKeys.LiftG, ColorGradeKeys.LiftB),
        ("Gamma", ColorGradeKeys.GammaR, ColorGradeKeys.GammaG, ColorGradeKeys.GammaB),
        ("Gain", ColorGradeKeys.GainR, ColorGradeKeys.GainG, ColorGradeKeys.GainB),
    ];

    /// <summary>Basics card slider keys in display order.</summary>
    internal static readonly string[] BasicKeys =
        [ColorGradeKeys.Temperature, ColorGradeKeys.Tint, ColorGradeKeys.Exposure, ColorGradeKeys.Contrast, ColorGradeKeys.Saturation, ColorGradeKeys.Vibrance];

    private MainViewModel? _vm;
    private Func<Guid, Task<(byte[] Bgra, int Width, int Height)?>>? _capture;
    private Func<Func<ColorGradeTarget>, FrameworkElement?>? _colorMatch;

    private ColorGradeTarget _target;
    private ObservableCollection<EffectInstance>? _stack;
    private EffectInstance? _card;
    private bool _built, _listening, _refreshQueued, _settingControls;

    // A held playhead scrub's PositionMs commits skipped the keyed refresh and the recapture (ScrubGate.IsHeld); its release catches up.
    private bool _playheadAfterScrub;

    // UI parts created by Build.
    private TextBlock _targetLabel = null!, _empty = null!;
    private ToggleButton _sequenceToggle = null!, _compareEye = null!;
    private Button _resetButton = null!;
    private StackPanel _body = null!;
    private readonly ColorWheelsControl[] _wheels = new ColorWheelsControl[3];
    private ColorCurvesControl _curves = null!;
    private readonly Dictionary<string, SliderRow> _rows = new(StringComparer.Ordinal);
    private TextBox _lutBox = null!;

    /// <summary>
    /// One card slider row: parameter, slider, value box, thumb-held flag, and the box text last written by the pane (while the box still
    /// shows it, it holds no user edit: leaving it writes nothing and Undo refreshes it).
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
    /// Connects the pane on tab realization (MainWindow.ColorStudio.cs): the view model (follows <see cref="MainViewModel.EditTarget"/>),
    /// the ungraded input-still capture (GpuPreviewEngine.CaptureInputThumbnailAsync) and the Color Match bar factory. Nothing is built
    /// or subscribed until the tab is shown.
    /// </summary>
    public void Attach(MainViewModel viewModel, Func<Guid, Task<(byte[] Bgra, int Width, int Height)?>> capture,
        Func<Func<ColorGradeTarget>, FrameworkElement?>? colorMatch)
    {
        (_vm, _capture, _colorMatch) = (viewModel, capture, colorMatch);
        viewModel.EditTargetChanged += Retarget;
        Wake();
    }

    /// <summary>Raised when the Compare eye toggles (on: the player shows a Before/After split at a divider).</summary>
    public event Action<bool>? CompareToggled;

    /// <summary>Syncs the Compare eye to the window state (a hotkey may have changed it).</summary>
    public void ShowCompare(bool on)
    {
        if (_built && _compareEye.IsChecked != on)
            _compareEye.IsChecked = on;
    }

    // ----- Golden test hooks -----

    internal bool IsBuilt => _built;

    internal ColorGradeTarget Target => _target;

    internal EffectInstance? Card => _card;

    internal ColorWheelsControl Wheel(int index) => _wheels[index];

    internal ColorCurvesControl Curves => _curves;

    /// <summary>Explicit retarget for goldens (in the app, the pane follows the Effects pane's target).</summary>
    internal void SetTargetForTests(ColorGradeTarget target)
    {
        _vm?.SetEditTarget(target);
        SetTarget(target);
    }

    // ----- Lazy build and visibility-scoped subscriptions -----

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

        // Static event: held only while listening (StopListening removes it), so it never roots an unloaded pane.
        Services.ScrubGate.Released += ScrubGate_Released;
    }

    private void StopListening()
    {
        if (!_listening || _vm is not { } vm)
            return;

        _listening = false;
        vm.PropertyChanged -= ViewModel_PropertyChanged;
        vm.TimelineChanged -= Invalidated;
        vm.PreviewStateInvalidated -= Invalidated;
        Services.ScrubGate.Released -= ScrubGate_Released;
        _playheadAfterScrub = false;
        _throttle?.Stop();
        FlushWrites();
    }

    // The scrub ended (after its last PositionMs commit): one keyed refresh and one recapture for everything the held scrub skipped.
    private void ScrubGate_Released()
    {
        if (!_playheadAfterScrub)
            return;

        _playheadAfterScrub = false;
        if (Dispatcher.CheckAccess())
            CatchUpAfterScrub();
        else
            Dispatcher.InvokeAsync(CatchUpAfterScrub);
    }

    private void CatchUpAfterScrub()
    {
        if (!_listening)
            return;

        ForgetStill();
        if (_card is { Keys.Count: > 0 })
            QueueRefresh();
        Want();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Paused scrub: recapture once it rests; keyed values track the playhead.
            case nameof(MainViewModel.PositionMs):
                // Held scrub (60 Hz commits): drop the stale still once; refresh and recapture once on release (ScrubGate_Released).
                if (Services.ScrubGate.IsHeld)
                {
                    if (_still is not null)
                        ForgetStill();
                    _playheadAfterScrub = true;
                    break;
                }

                ForgetStill();
                if (_card is { Keys.Count: > 0 })
                    QueueRefresh();
                if (_vm is { IsPlaying: false })
                    Want(afterRest: true);
                break;
            case nameof(MainViewModel.IsPlaying) or nameof(MainViewModel.HasSource):
                ForgetStill();
                Want();
                break;
            case nameof(MainViewModel.ColorContrast) or nameof(MainViewModel.ColorBrightness) or nameof(MainViewModel.ColorSaturation) or nameof(MainViewModel.ColorGamma)
                or nameof(MainViewModel.ColorHue) or nameof(MainViewModel.ColorRed) or nameof(MainViewModel.ColorGreen) or nameof(MainViewModel.ColorBlue)
                or nameof(MainViewModel.SharpenStrength) or nameof(MainViewModel.LutPath):
                UpdateLegacy();
                break;
        }
    }

    // ----- Target -----

    /// <summary>Adopts the view model's edit target (on change or when the tab is shown).</summary>
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
        if (target != _target)
            ForgetStill();
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
    /// Re-resolves the target's card (new target, or a stack change since Undo inserts new instances) and refreshes the controls.
    /// Non-gradable targets (e.g. an audio clip selected in the Effects pane) never get a card here, so nothing writes to them.
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

    // External value change (Effects pane card, Color Match, keyframes): refresh once per burst.
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

    /// <summary>Playhead in target-local seconds (clip keys are relative to clip start; Sequence Output keys to 0).</summary>
    private double ClipTime => (_vm?.PositionMs ?? 0) / 1000 - (_target.Layer?.StartTime ?? 0);

    /// <summary>Current card value (keyframed value at the playhead if keyed); the registry default when there is no card.</summary>
    private double ValueOf(string key)
    {
        if (_card is not { } card)
            return EffectRegistry.Find(EffectRegistry.ColorStudio)?.Param(key)?.Default ?? 0;
        return card.HasKeys(key) ? EasingCurves.ValueAt(card.GetKeys(key), Math.Max(ClipTime, 0)) : card.Get(key);
    }

    /// <summary>Refreshes all controls from the card; controls mid-gesture keep their gesture value.</summary>
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

                // Boxes with pending typing keep their text; focused but unedited boxes follow (Undo, retarget).
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
    /// True when a box should display the current value: always when unfocused; when focused, only while its text equals the last shown
    /// text (e.g. Undo after Enter), so in-progress typing is never overwritten. Pure; golden-tested.
    /// </summary>
    internal static bool BoxFollows(bool focused, string text, string shown) => !focused || text == shown;

    private static string Format(EffectParam param, double value)
    {
        var decimals = param.Step >= 1 ? 0 : param.Step >= 0.1 ? 1 : param.Step >= 0.01 ? 2 : 3;
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture) + param.Unit;
    }

    // ----- Build (first show) -----

    private void Build()
    {
        _built = true;
        var definition = EffectRegistry.Find(EffectRegistry.ColorStudio);

        // Header: target label, Sequence Output toggle, Compare eye, Reset.
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

        // Wheels in a three-column row (Wheels & Color Match column).
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

        DeckCard(WheelsCard, wheels);

        // Curves over the live post-grade YRGB histogram (its mode shared with Video Scopes).
        _curves = new ColorCurvesControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        _curves.EditStarted += BeginGesture;
        _curves.EditEnded += EndGesture;
        _curves.CurveChanged += (channel, text) => QueueText(ColorGradeKeys.Curves[channel], text);
        WireHistogramMode();
        DeckCard(CurvesCard, _curves);

        // Basics & LUT: white balance, exposure, contrast, saturation, vibrance, then the LUT file and its mix.
        var basics = new StackPanel();
        foreach (var key in BasicKeys)
        {
            if (definition?.Param(key) is { } param)
                basics.Children.Add(Row(param));
        }

        var lutHeading = new TextBlock { Text = "LUT", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        lutHeading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        basics.Children.Add(lutHeading);
        basics.Children.Add(LutRows(definition));
        DeckCard(BasicsCard, basics);

        // Color Match bar, when the host provides one (under the wheels).
        if (_colorMatch?.Invoke(() => _target) is { } match)
            DeckCard(MatchCard, match);

        _cards[LegacyCard] = LegacyFold();
        _body.Children.Add(BuildDeck());
    }

    /// <summary>Inspector card: title above content on the InspectorCard surface and border brushes.</summary>
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

    /// <summary>Numeric parameter row: label (double-click resets to default), slider and value box.</summary>
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
    /// Commits a value box on Enter or blur, only if its text was edited (an untouched box adds no card, key or step, and never reapplies a
    /// value Undo reverted). Strips the parameter's unit suffix when it has one, then clamps to range.
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
