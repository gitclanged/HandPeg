using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Views;

// ----- Pass 5.6A (lane C): the Speed & Time card (Speed & Pitch for a sound) and when the Opacity card shows -----
// A clip that plays a file has a card above its effects: its speed on a log slider (0.1x to 16x, 1x near the middle) and in a box, five
// usual speeds, the length it should take on the timeline (the speed follows), and whether what follows ripples (a setting). A video adds
// Smooth Slow-Mo and the engine its in-betweens are made with; a sound (or a video that carries one) adds Keep Pitch, Pitch Shift and
// Spectral Audio. It is no effect (no packet, no lane card, nothing copied with Copy Effects): the speed is the clip's own edit
// (MainViewModel.SetClipSpeed: one undo step, its family with it, never a checkpoint before it), the options its look. A Cutout's speed is
// its source clip's (the Cutout runs at it), so the card sets that clip's. A drag shows the speed it would give and is written when let go.
// The Opacity card shows only when it says something: below 100 %, keyed, or added (Add Effect) or shown once this session.
public partial class EffectsPane
{
    /// <summary>Puts the Speed & Time card into SpeedPanel for the target (called by Rebuild, after SpeedPanel was cleared).</summary>
    private partial void AddSpeedCard();

    /// <summary>Whether the pinned Opacity card shows for this picture clip.</summary>
    private partial bool ShowsOpacityCard(Layer layer);

    /// <summary>Whether a pinned definition (Opacity) is offered by Add Effect for the target.</summary>
    private partial bool PinnedFits(EffectDefinition definition);

    /// <summary>Add Effect chose a pinned definition (Opacity): show its card.</summary>
    private partial void AddPinned(EffectDefinition definition);

    /// <summary>A property of the target clip changed (before the pane's own handling of it).</summary>
    private partial void SpeedLayerChanged(string? propertyName);

    // The clips whose Opacity card was added or shown this session (by clip, so a view made again by Undo keeps it): kept while HandPeg runs.
    private static readonly HashSet<Guid> OpacityShownFor = [];

    // The usual speeds, as pills; and the slider's ends, log2 of the slowest and fastest speeds.
    internal static readonly double[] SpeedPills = [0.25, 0.5, 1, 2, 4];
    private static readonly double SliderLow = Math.Log2(Layer.SlowestSpeed), SliderHigh = Math.Log2(Layer.FastestSpeed);

    // Within this of a pill's place on the slider (log2), the slider rests on it: 1x, 2x... are easy to find by hand.
    private const double PillDetent = 0.04;

    // Why the vendor's optical flow is not offered, worked out once (the card's own words: <= 12).
    private static string? _vendorFlowReason;

    // The card now: the clip whose speed it sets (a Cutout's source clip), the clip it was built for, and its controls.
    private Layer? _speedTarget, _speedFor;
    private Slider? _speedSlider, _pitchSlider;
    private TextBox? _speedBox, _durationBox, _pitchBox;
    private readonly List<(ToggleButton Pill, double Speed)> _speedPillButtons = [];
    private CheckBox? _rippleCheck, _smoothCheck, _keepPitchCheck, _spectralCheck;
    private TextBlock? _speedNote;

    // Set while the card writes its own controls (nothing it does then is the user's); a drag's speed not yet written; whether the
    // slider or the box is held; whether the Opacity card is shown now.
    private bool _speedShowing, _speedHeld, _opacityCardShown;
    private double? _speedOwed;

    // The boxes typed in since they last showed the clip: a box left without typing writes nothing (after an Undo it may show what was).
    private readonly HashSet<TextBox> _typedBoxes = [];

    private partial void AddSpeedCard()
    {
        // A drag the card was rebuilt under is written once the rebuild is done (never from inside it: the edit syncs the views).
        if (_speedOwed is { } owed && _speedTarget is { } owedTarget && _vm is { } vm)
            Dispatcher.BeginInvoke(() => vm.SetClipSpeed(owedTarget, owed), DispatcherPriority.Background);
        _writes.Flush();
        (_speedOwed, _speedHeld, _speedTarget, _speedFor) = (null, false, null, null);
        (_speedSlider, _pitchSlider, _speedBox, _durationBox, _pitchBox, _speedNote) = (null, null, null, null, null, null);
        (_rippleCheck, _smoothCheck, _keepPitchCheck, _spectralCheck) = (null, null, null, null);
        _speedPillButtons.Clear();
        _typedBoxes.Clear();
        _opacityCardShown = _layer is { HasStyle: true, IsAdjustment: false } shown && OpacityShows(shown);

        if (_vm is null || _noClip || _layer is not { HasSpeed: true, IsBackground: false } layer)
            return;

        var target = SpeedTargetOf(layer);
        (_speedTarget, _speedFor) = (target, layer);
        var sound = layer.IsAudio;
        var title = sound ? "Speed & Pitch" : "Speed & Time";
        var border = new Border { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6, 3, 4, 5), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        AutomationProperties.SetName(border, title);
        var root = new StackPanel();
        border.Child = root;

        var header = new DockPanel { Margin = new Thickness(0, 1, 0, 2) };
        if (!ReferenceEquals(target, layer))
        {
            // A Cutout: its speed is its source clip's.
            _speedNote = new TextBlock { Text = "Follows source clip", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "A Cutout runs at its source clip's speed." };
            _speedNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            DockPanel.SetDock(_speedNote, Dock.Right);
            header.Children.Add(_speedNote);
        }

        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        root.Children.Add(header);
        var body = new StackPanel { Margin = new Thickness(2, 3, 0, 0) };
        root.Children.Add(body);

        body.Children.Add(SpeedRow());
        body.Children.Add(PillRow());
        body.Children.Add(DurationRow());
        _rippleCheck = SpeedCheck("Ripple Timeline", AppSettings.Current.SpeedRipplesTimeline, "Later clips move with the new length.", on =>
            _vm?.SetViewSetting(s => s.SpeedRipplesTimeline = on));
        body.Children.Add(_rippleCheck);

        // A video: Smooth Slow-Mo and the engine of its in-betweens (a setting of this PC's).
        if (!sound)
        {
            _smoothCheck = SpeedCheck("Smooth Slow-Mo", target.SmoothSlowMo, "Optical-flow in-betweens when slower than 1x.", on =>
                SetOption($"switch Smooth Slow-Mo {(on ? "on" : "off")}", l => l.SmoothSlowMo = on));
            body.Children.Add(_smoothCheck);
            body.Children.Add(EngineRow());
        }

        // A sound, or a video that carries one (its own, or its streams' lanes): how its sound follows the speed.
        if (sound || target.CarriesSound || SpeedFamily(target).Any(l => l.IsAudio))
        {
            _keepPitchCheck = SpeedCheck("Keep Pitch", target.KeepPitch, "Off: the pitch follows the speed, like tape.", on =>
                SetOption($"switch Keep Pitch {(on ? "on" : "off")}", l => l.KeepPitch = on));
            body.Children.Add(_keepPitchCheck);
            body.Children.Add(PitchRow(target));
            _spectralCheck = SpeedCheck("Spectral Audio", target.SpectralAudio, "Phase-vocoder stretch: smooth slow-motion sound.", on =>
                SetOption($"switch Spectral Audio {(on ? "on" : "off")}", l => l.SpectralAudio = on));
            body.Children.Add(_spectralCheck);
        }

        SpeedPanel.Children.Add(border);
        ShowSpeed();
    }

    /// <summary>The clip a clip's card sets the speed of: a Cutout's source clip (the Cutout runs at it), else the clip itself.</summary>
    private Layer SpeedTargetOf(Layer layer)
    {
        if (_vm is { } vm && vm.IsCutout(layer) && vm.ClipOf(layer) is { ParentClipId: { } parent } && vm.Document.FindClip(parent) is { } source && vm.ViewOf(source) is { } view)
            return view;
        return layer;
    }

    /// <summary>The clips that run at a clip's speed with it (its streams' lanes, its Cutouts: MainViewModel.SetClipSpeed's family), as views.</summary>
    private List<Layer> SpeedFamily(Layer target)
    {
        var family = new List<Layer>();
        if (_vm is not { } vm || vm.ClipOf(target) is not { } clip)
            return family;

        foreach (var track in vm.Document.Tracks)
        {
            foreach (var child in track.Clips)
            {
                if (child.ParentClipId == clip.Id && child.PlaysFile && string.Equals(child.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase)
                    && vm.ViewOf(child) is { } view)
                {
                    family.Add(view);
                }
            }
        }

        return family;
    }

    // ----- The rows -----

    private Grid LabelledRow(string label, out int next)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        next = 1;
        return row;
    }

    // Typing in a box (it has the keyboard; not the card writing it, nor a sideways drag) marks it typed.
    private void WatchTyping(TextBox box) => box.TextChanged += (_, _) =>
    {
        if (!_speedShowing && box.IsKeyboardFocusWithin)
            _typedBoxes.Add(box);
    };

    // Whether a box being left writes what it holds: typed in, and still on the card (not one a rebuild has just taken away).
    private bool LeftTyped(TextBox box) => _typedBoxes.Remove(box) && SpeedPanel.IsAncestorOf(box);

    private TextBox ValueBox(string name, string usual, double min, double max, int decimals, double dragStep)
    {
        var box = new TextBox { Width = 56, Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("ValueBox") as Style };
        NumericInput.SetDefault(box, usual);
        NumericInput.SetMinimum(box, min);
        NumericInput.SetMaximum(box, max);
        NumericInput.SetDragStep(box, dragStep);
        NumericInput.SetDecimals(box, decimals);
        AutomationProperties.SetName(box, name);
        return box;
    }

    // The speed: a log slider and its box. A drag (of either) shows what it would give and writes it when let go; a key on the slider
    // or a value typed (Enter, or the box left) is written at once.
    private Grid SpeedRow()
    {
        var row = LabelledRow("Speed (x)", out _);
        row.ToolTip = "Clip speed, 0.1x to 16x (double-click for 1x)";
        var slider = new Slider { Minimum = SliderLow, Maximum = SliderHigh, SmallChange = 0.05, LargeChange = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        NumericInput.SetDefault(slider, "0");
        AutomationProperties.SetName(slider, "Clip Speed Slider");
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        var box = ValueBox("Clip Speed", "1", Layer.SlowestSpeed, Layer.FastestSpeed, 2, 0.01);
        Grid.SetColumn(box, 2);
        row.Children.Add(box);
        (_speedSlider, _speedBox) = (slider, box);

        NumericInput.WatchHold(slider, held => SpeedHeld(held));
        NumericInput.WatchHold(box, held => SpeedHeld(held));
        slider.ValueChanged += (_, e) =>
        {
            if (_speedShowing)
                return;
            var speed = SpeedOfSlider(e.NewValue);
            if (_speedHeld)
                Preview(speed);
            else
                CommitSpeed(speed);
        };
        box.TextChanged += (_, _) =>
        {
            // A box dragged sideways (NumericInput): shown as it goes, written when let go. Typing waits for Enter or for the box to be left.
            if (_speedShowing || !_speedHeld || !TryReadSpeed(box.Text, out var speed))
                return;
            Preview(speed);
        };
        WatchTyping(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TryReadSpeed(box.Text, out var speed))
            {
                _typedBoxes.Remove(box);
                CommitSpeed(speed);
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (LeftTyped(box) && TryReadSpeed(box.Text, out var speed))
                CommitSpeed(speed);
        };

        // A double-click puts the box back to 1x (NumericInput's reset, which has handled the click by then): written at once.
        box.AddHandler(Control.PreviewMouseDoubleClickEvent, new MouseButtonEventHandler((_, _) =>
        {
            _typedBoxes.Remove(box);
            if (!_speedShowing && TryReadSpeed(box.Text, out var speed))
                CommitSpeed(speed);
        }), handledEventsToo: true);
        return row;
    }

    private StackPanel PillRow()
    {
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(100, 1, 0, 3) };
        foreach (var speed in SpeedPills)
        {
            var label = SpeedLabel(speed);
            var pill = new ToggleButton { Content = label, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 3, 0), FontSize = 11, ToolTip = $"Set {label}" };
            AutomationProperties.SetName(pill, $"Speed {label}");
            pill.Click += (_, _) => CommitSpeed(speed);
            _speedPillButtons.Add((pill, speed));
            pills.Children.Add(pill);
        }

        return pills;
    }

    // How long the clip should be on the timeline: the speed follows (its media span over the length), within 0.1x to 16x.
    private Grid DurationRow()
    {
        var row = LabelledRow("Target Duration", out _);
        var box = new TextBox { MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Length on the timeline ({TimeDisplay.FormatHint}); speed follows." };
        AutomationProperties.SetName(box, "Target Duration");
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        row.Children.Add(box);
        _durationBox = box;

        void Take()
        {
            if (_speedShowing || _speedTarget is not { } target || _vm?.ClipOf(target) is not { } clip)
                return;
            if (TryReadDuration(box.Text, out var seconds) && SpeedForDuration(clip.MediaEnd - clip.MediaOffset, seconds) is var speed && double.IsFinite(speed))
                CommitSpeed(speed);
            else
                ShowSpeed();
        }

        WatchTyping(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _typedBoxes.Remove(box);
                Take();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (LeftTyped(box))
                Take();
        };
        return row;
    }

    // Smooth Slow-Mo's engine (AppSettings.FrameInterpolationEngine): Auto is HandPeg's shader today; the vendor's hardware is shown, not offered.
    private Grid EngineRow()
    {
        var row = LabelledRow("Interpolation", out _);
        var combo = new ComboBox { Margin = new Thickness(4, 0, 0, 0), ToolTip = "How in-betweens are made; Auto uses the HandPeg shader here." };
        AutomationProperties.SetName(combo, "Interpolation");
        combo.Items.Add(new ComboBoxItem { Content = "Auto", Tag = "auto" });
        combo.Items.Add(new ComboBoxItem { Content = "HandPeg Shader", Tag = "shader", ToolTip = "GPU optical flow, built into HandPeg." });
        var vendor = new ComboBoxItem { Content = "Vendor Hardware", Tag = "vendor", IsEnabled = false, ToolTip = VendorFlowReason() };
        ToolTipService.SetShowOnDisabled(vendor, true);
        combo.Items.Add(vendor);
        combo.SelectedIndex = AppSettings.Current.FrameInterpolationEngine switch { "shader" => 1, "vendor" => 2, _ => 0 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string engine } && engine != AppSettings.Current.FrameInterpolationEngine)
                _vm?.SetViewSetting(s => s.FrameInterpolationEngine = engine);
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        row.Children.Add(combo);
        return row;
    }

    // Pitch Shift, -12 to +12 semitones: the sound's Pitch and Tempo filter (its tempo left as it is), on the clip and its streams' lanes.
    private Grid PitchRow(Layer target)
    {
        var row = LabelledRow("Pitch Shift (st)", out _);
        row.ToolTip = "Semitones up or down; the timing stays.";
        var filters = target.AudioFilters;
        var value = filters.PitchTempo ? Math.Clamp(filters.PitchSemitones, -12, 12) : 0;
        var slider = new Slider { Minimum = -12, Maximum = 12, Value = value, SmallChange = 1, LargeChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        NumericInput.SetDefault(slider, "0");
        AutomationProperties.SetName(slider, "Pitch Shift");
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        var box = ValueBox("Pitch Shift Value", "0", -12, 12, 0, 0.1);
        box.Text = value.ToString("0", CultureInfo.InvariantCulture);
        Grid.SetColumn(box, 2);
        row.Children.Add(box);
        (_pitchSlider, _pitchBox) = (slider, box);

        slider.ValueChanged += (_, e) =>
        {
            if (_speedShowing)
                return;
            box.Text = e.NewValue.ToString("0", CultureInfo.InvariantCulture);
            PitchChanged(e.NewValue);
        };
        void Typed()
        {
            if (!_speedShowing && double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var semitones) && double.IsFinite(semitones))
                slider.Value = Math.Round(Math.Max(-12, Math.Min(semitones, 12)));
        }

        // (A sideways drag of the box writes as it goes; typing waits for Enter or for the box to be left.)
        box.TextChanged += (_, _) =>
        {
            if (!box.IsKeyboardFocusWithin)
                Typed();
        };
        WatchTyping(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _typedBoxes.Remove(box);
                Typed();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (LeftTyped(box))
                Typed();
        };
        return row;
    }

    private CheckBox SpeedCheck(string label, bool on, string tip, Action<bool> switched)
    {
        var check = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 4, 0, 1), ToolTip = tip };
        AutomationProperties.SetName(check, label);
        check.Click += (_, _) => switched(check.IsChecked == true);
        return check;
    }

    // ----- Writing -----

    private void SpeedHeld(bool held)
    {
        if (held)
        {
            _speedHeld = true;
            return;
        }

        if (!_speedHeld)
            return;
        _speedHeld = false;
        if (_speedOwed is { } owed)
        {
            CommitSpeed(owed);

            // 5.6A review: the thumb (or a track's RepeatButton) still holds the pointer here (PreviewMouseLeftButtonUp), so ShowSpeed left the
            // slider where the drag had it; once it lets go, the slider shows what was written (the detent, or the old speed when refused).
            Dispatcher.BeginInvoke(() =>
            {
                if (!_speedHeld)
                    ShowSpeed();
            }, DispatcherPriority.Input);
        }
    }

    // A drag's speed, shown (the slider, the box, the length it would give) and owed until let go.
    private void Preview(double speed)
    {
        _speedOwed = speed;
        ShowSpeed(speed);
    }

    // The speed set on the clip (its family with it, ripple as the setting says): one undo step, the clip's own edit (no checkpoint first).
    private void CommitSpeed(double speed)
    {
        _speedOwed = null;
        if (_vm is { } vm && _speedTarget is { } target && double.IsFinite(speed))
        {
            speed = Math.Round(Math.Max(Layer.SlowestSpeed, Math.Min(speed, Layer.FastestSpeed)), 3);
            if (Math.Abs(target.Speed - speed) >= 0.0005)
                vm.SetClipSpeed(target, speed);
        }

        ShowSpeed();
    }

    // An option of the clip's look, set on it and on its family (a Cutout and its source clip are one moving picture; a video's streams' lanes
    // are its sound), as one undo step.
    private void SetOption(string label, Action<Layer> set)
    {
        if (_vm is not { } vm || _speedTarget is not { } target)
            return;

        vm.Checkpoint(label);
        using (OwnLooks())
        {
            set(target);
            foreach (var member in SpeedFamily(target))
                set(member);
        }
    }

    // A Pitch Shift: paced with the pane's other writes (each write is a new sound program); a slider drag is one undo step.
    private void PitchChanged(double semitones)
    {
        _drag.Follow();
        _writes.Queue(PitchWriteKey, () => WritePitch(semitones));
    }

    // The semitones put into the Pitch and Tempo filter of the clip and of every sound of its family (each a copy of its own filters),
    // after its undo step. The sound's own cards' copy (EffectsPane.Audio.cs) takes it too, so they never write it back.
    private void WritePitch(double semitones)
    {
        if (_vm is null || _speedTarget is not { } target)
            return;

        Step(target, "pitch", $"shift the pitch of {target.Name}");
        if (_audio is { } shown && ReferenceEquals(_audioLayer, target))
            WithPitch(shown, semitones);
        _writingAudio = true;
        try
        {
            foreach (var sound in SpeedFamily(target).Prepend(target))
            {
                if (!sound.IsAudio && !sound.CarriesSound)
                    continue;
                var filters = sound.AudioFilters.Clone();
                WithPitch(filters, semitones);
                sound.AudioFilters = filters;
            }
        }
        finally
        {
            _writingAudio = false;
        }
    }

    /// <summary>Semitones put into a sound's Pitch and Tempo filter, its tempo left as it is: on while either moves anything.</summary>
    internal static void WithPitch(TrackAudioFilters filters, double semitones)
    {
        filters.PitchSemitones = Math.Round(Math.Max(-12, Math.Min(semitones, 12)));
        filters.PitchTempo = filters.PitchSemitones != 0 || Math.Abs(filters.Tempo - 1) > 1e-9;
    }

    // ----- Showing -----

    // The card's controls from the clip (or from a drag's speed), nothing written while they are.
    private void ShowSpeed(double? dragged = null)
    {
        if (_speedTarget is not { } target || _speedSlider is null)
            return;

        _speedShowing = true;
        try
        {
            // (Not under the pointer dragging the slider itself: its thumb stays where the pointer has it, the detent is taken when let go.)
            var speed = dragged ?? target.Speed;
            if (!_speedSlider.IsMouseCaptureWithin)
                _speedSlider.Value = Math.Log2(Math.Max(Layer.SlowestSpeed, Math.Min(speed, Layer.FastestSpeed)));
            // (A box being typed in keeps what is typed.)
            if (_speedBox is { } box && (dragged is not null || !_typedBoxes.Contains(box)))
                box.Text = speed.ToString("0.00", CultureInfo.InvariantCulture);
            foreach (var (pill, value) in _speedPillButtons)
                pill.IsChecked = Math.Abs(speed - value) < 0.0005;

            // The length it has (or would have at the dragged speed): the media it plays over the speed.
            if (_durationBox is { } duration && !_typedBoxes.Contains(duration))
            {
                var span = _vm?.ClipOf(target) is { } clip ? clip.MediaEnd - clip.MediaOffset : target.Duration * target.Speed;
                duration.Text = span > 0 && speed > 0 ? TimeDisplay.Format(span / speed) : "";
            }

            if (_rippleCheck is { } ripple)
                ripple.IsChecked = AppSettings.Current.SpeedRipplesTimeline;
            if (_smoothCheck is { } smooth)
            {
                smooth.IsChecked = target.SmoothSlowMo;
                smooth.Opacity = speed < 1 || !target.SmoothSlowMo ? 1 : 0.6;
            }

            if (_keepPitchCheck is { } keep)
                keep.IsChecked = target.KeepPitch;
            if (_spectralCheck is { } spectral)
                spectral.IsChecked = target.SpectralAudio;
            if (_pitchSlider is { } pitch && !_writes.IsOwed(PitchWriteKey))
            {
                var filters = target.AudioFilters;
                pitch.Value = filters.PitchTempo ? Math.Max(-12, Math.Min(filters.PitchSemitones, 12)) : 0;
                if (_pitchBox is { } pitchBox && !_typedBoxes.Contains(pitchBox))
                    pitchBox.Text = pitch.Value.ToString("0", CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _speedShowing = false;
        }
    }

    private partial void SpeedLayerChanged(string? propertyName)
    {
        switch (propertyName)
        {
            // Its speed (an edit, Undo), its length, its options: the card shows them again (a drag in progress keeps its own speed).
            case nameof(Layer.Speed) or nameof(Layer.ClipSpeed) or nameof(Layer.Duration) or nameof(Layer.MediaOffset)
                or nameof(Layer.SmoothSlowMo) or nameof(Layer.KeepPitch) or nameof(Layer.SpectralAudio) or nameof(Layer.AudioFilters):
                if (!_speedHeld && ReferenceEquals(_layer, _speedFor))
                    ShowSpeed();
                break;

            // The Opacity card comes when the clip's opacity or its keys first say something (set elsewhere: the timeline, the
            // Keyframes pane): rebuilt only when that answer changes, never for a value moved.
            case nameof(Layer.Opacity) or Layer.KeysChanged:
                if (_layer is { HasStyle: true, IsAdjustment: false } layer && OpacityShows(layer) != _opacityCardShown)
                    _rebuild.Request();

                break;
        }
    }

    // ----- The Opacity card -----

    /// <summary>Whether a picture clip's Opacity card says something: below 100 %, keyed, or added or shown before this session.</summary>
    private static bool OpacityShows(Layer layer) =>
        layer.Opacity < 100 || layer.HasKeys(KeyProperty.Opacity) || (layer.ClipId != Guid.Empty && OpacityShownFor.Contains(layer.ClipId));

    private partial bool ShowsOpacityCard(Layer layer)
    {
        // Shown once, it stays for the session: a drag back to 100 % never takes it away under the pointer.
        var shows = OpacityShows(layer);
        if (shows && layer.ClipId != Guid.Empty)
            OpacityShownFor.Add(layer.ClipId);
        return shows;
    }

    private partial bool PinnedFits(EffectDefinition definition) =>
        definition.Type == EffectRegistry.Opacity && _layer is { HasStyle: true, IsAdjustment: false, ClipId: var id } layer && id != Guid.Empty && !OpacityShows(layer);

    private partial void AddPinned(EffectDefinition definition)
    {
        if (definition.Type != EffectRegistry.Opacity || _layer is not { ClipId: var id } || id == Guid.Empty)
            return;

        OpacityShownFor.Add(id);
        Rebuild();
    }

    // ----- Pure parts (the goldens' too) -----

    /// <summary>The speed a log slider's place stands for: 2 to its power, to the hundredth, resting on a usual speed within a detent of it.</summary>
    internal static double SpeedOfSlider(double place)
    {
        foreach (var pill in SpeedPills)
        {
            if (Math.Abs(place - Math.Log2(pill)) <= PillDetent)
                return pill;
        }

        return Math.Round(Math.Max(Layer.SlowestSpeed, Math.Min(Math.Pow(2, place), Layer.FastestSpeed)), 2);
    }

    /// <summary>A speed typed into the box: a number (an "x" after it allowed), within 0.1x to 16x; false for anything else.</summary>
    internal static bool TryReadSpeed(string text, out double speed)
    {
        var typed = text.Trim().TrimEnd('x', 'X').Trim();
        if (double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out speed) && double.IsFinite(speed) && speed > 0)
        {
            speed = Math.Max(Layer.SlowestSpeed, Math.Min(speed, Layer.FastestSpeed));
            return true;
        }

        speed = 0;
        return false;
    }

    /// <summary>A length typed into Target Duration: a time in the format in use (TimeDisplay), or a number of seconds.</summary>
    internal static bool TryReadDuration(string text, out double seconds)
    {
        if (TimeDisplay.TryParse(text, out seconds, out _) && seconds > 0)
            return true;
        if (double.TryParse(text.Trim().TrimEnd('s', 'S'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds) && seconds > 0)
            return true;

        seconds = 0;
        return false;
    }

    /// <summary>
    /// The speed that makes a clip whose file stretch is <paramref name="mediaSpan"/> seconds last <paramref name="target"/> on the timeline:
    /// their ratio, held to 0.1x to 16x and to the thousandth (as an edit sets it); NaN when either is no length.
    /// </summary>
    internal static double SpeedForDuration(double mediaSpan, double target) =>
        mediaSpan > 0 && target > 0 && double.IsFinite(mediaSpan) && double.IsFinite(target)
            ? Math.Round(Math.Max(Layer.SlowestSpeed, Math.Min(mediaSpan / target, Layer.FastestSpeed)), 3)
            : double.NaN;

    /// <summary>A speed as a pill or a menu shows it: "0.25x", "1x", "16x".</summary>
    internal static string SpeedLabel(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <summary>
    /// Why the vendor's optical-flow hardware is not offered (worked out on the interface's side, no device made): an NVIDIA card with its
    /// optical-flow library is one HandPeg does not reach yet; anything else has none it can use. The cards are not listed for it.
    /// </summary>
    internal static string VendorFlowReason()
    {
        if (_vendorFlowReason is { } known)
            return known;

        var card = GpuAdapters.IsListed ? GpuAdapters.CompositorAdapter : null;
        var reason = card is { IsNvidia: true } && File.Exists(Path.Combine(Environment.SystemDirectory, "nvofapi64.dll")) ? "Not available in this build" : "Not available on this GPU";
        if (GpuAdapters.IsListed)
            _vendorFlowReason = reason;
        return reason;
    }
}
