using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.Views;

// ----- Speed & Time card (Speed & Pitch for audio) and Opacity card visibility -----
// File-backed clips get a card above their effects: log speed slider (0.1x-16x, 1x near centre) plus box, five preset pills, Target
// Duration (speed follows), and Ripple Timeline (a setting). Video adds Smooth Slow-Mo and its interpolation engine; audio (or video with
// audio) adds Keep Pitch, Pitch Shift and Spectral Audio. Not an effect (no packet, lane card, or Copy Effects): speed is a clip edit
// (MainViewModel.SetClipSpeed: one Undo step including its family, no prior checkpoint); options are look properties. A Cutout uses its
// source clip's speed, so the card edits the source. Drags preview and commit on release. The Opacity card appears only when meaningful:
// below 100 %, keyed, or added/shown earlier this session.
public partial class EffectsPane
{
    /// <summary>Adds the Speed & Time card to SpeedPanel for the target (called by Rebuild after clearing it).</summary>
    private partial void AddSpeedCard();

    /// <summary>True when the pinned Opacity card should show for this visual clip.</summary>
    private partial bool ShowsOpacityCard(Layer layer);

    /// <summary>True when Add Effect should offer a pinned definition (Opacity) for the target.</summary>
    private partial bool PinnedFits(EffectDefinition definition);

    /// <summary>Add Effect picked a pinned definition (Opacity): show its card.</summary>
    private partial void AddPinned(EffectDefinition definition);

    /// <summary>Target clip property changed (called before the pane's own handling).</summary>
    private partial void SpeedLayerChanged(string? propertyName);

    // Clip ids whose Opacity card was added/shown this session (keyed by clip so Undo-rebuilt views keep it); process lifetime.
    private static readonly HashSet<Guid> OpacityShownFor = [];

    // Preset speed pills; slider range as log2 of the slowest and fastest speeds.
    internal static readonly double[] SpeedPills = [0.25, 0.5, 1, 2, 4];
    private static readonly double SliderLow = Math.Log2(Layer.SlowestSpeed), SliderHigh = Math.Log2(Layer.FastestSpeed);

    // Slider detent radius (log2) around pill speeds so 1x, 2x... are easy to hit.
    private const double PillDetent = 0.04;

    // Cached reason (<= 12 words) why vendor optical flow is not offered.
    private static string? _vendorFlowReason;

    // Current card: speed target (a Cutout's source), the clip it was built for, and its controls.
    private Layer? _speedTarget, _speedFor;
    private Slider? _speedSlider, _pitchSlider;
    private TextBox? _speedBox, _durationBox, _pitchBox;
    private readonly List<(ToggleButton Pill, double Speed)> _speedPillButtons = [];
    private CheckBox? _rippleCheck, _smoothCheck, _keepPitchCheck, _spectralCheck;
    private TextBlock? _speedNote;

    // Re-entrancy flag while the card updates its own controls; uncommitted drag speed; slider/box held flag; Opacity card shown flag.
    private bool _speedShowing, _speedHeld, _opacityCardShown;
    private double? _speedOwed;

    // Boxes typed in since last refresh; leaving an untyped box writes nothing (it may show pre-Undo text).
    private readonly HashSet<TextBox> _typedBoxes = [];

    private partial void AddSpeedCard()
    {
        // Commit a drag interrupted by the rebuild afterwards (never during it: the edit resyncs views).
        if (_speedOwed is { } owed && _speedTarget is { } owedTarget && _vm is { } vm)
            Dispatcher.BeginInvoke(() => vm.SetClipSpeed(owedTarget, owed), DispatcherPriority.Background);
        _writes.Flush();
        (_speedOwed, _speedHeld, _speedTarget, _speedFor) = (null, false, null, null);
        (_speedSlider, _pitchSlider, _speedBox, _durationBox, _pitchBox, _speedNote) = (null, null, null, null, null, null);
        (_rippleCheck, _smoothCheck, _keepPitchCheck, _spectralCheck) = (null, null, null, null);
        _speedPillButtons.Clear();
        _typedBoxes.Clear();
        (_ramp, _rampNote, _rampStepTaken, _rampLive) = (null, null, false, null);
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
            // Cutout: speed belongs to its source clip.
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
        body.Children.Add(RampRow(target, sound));
        _rippleCheck = SpeedCheck("Ripple Timeline", AppSettings.Current.SpeedRipplesTimeline, "Later clips move with the new length.", on =>
            _vm?.SetViewSetting(s => s.SpeedRipplesTimeline = on));
        body.Children.Add(_rippleCheck);

        // Video: Smooth Slow-Mo and the interpolation engine (a machine setting).
        if (!sound)
        {
            _smoothCheck = SpeedCheck("Smooth Slow-Mo", target.SmoothSlowMo, "Optical-flow in-betweens below 1x; auto-on when a ramp dips.", on =>
                SetOption($"switch Smooth Slow-Mo {(on ? "on" : "off")}", l => l.SmoothSlowMo = on));
            body.Children.Add(_smoothCheck);
            body.Children.Add(EngineRow());
        }

        // Audio, or video with audio (own or stream lanes): how audio follows the speed.
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

    /// <summary>Speed target for a clip: a Cutout's source clip, else the clip itself.</summary>
    private Layer SpeedTargetOf(Layer layer)
    {
        if (_vm is { } vm && vm.IsCutout(layer) && vm.ClipOf(layer) is { ParentClipId: { } parent } && vm.Document.FindClip(parent) is { } source && vm.ViewOf(source) is { } view)
            return view;
        return layer;
    }

    /// <summary>Views of clips sharing the target's speed (stream lanes, Cutouts: MainViewModel.SetClipSpeed's family).</summary>
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

    // ----- Card rows -----

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

    // Marks a box as typed when its text changes with keyboard focus (not card updates or sideways drags).
    private void WatchTyping(TextBox box) => box.TextChanged += (_, _) =>
    {
        if (!_speedShowing && box.IsKeyboardFocusWithin)
            _typedBoxes.Add(box);
    };

    // On focus loss, commit only if typed and still in the card (not removed by a rebuild).
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

    // Speed row: log slider plus box. Drags (either control) preview and commit on release; slider keys and typed values (Enter or focus
    // loss) commit immediately.
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
            // Sideways box drag (NumericInput) previews live and commits on release; typing waits for Enter or focus loss.
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

        // Double-click reset to 1x (NumericInput already handled it, hence handledEventsToo): commit immediately.
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

    // Target Duration: speed = media span / duration, clamped to 0.1x-16x.
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

    // Interpolation engine (AppSettings.FrameInterpolationEngine): Auto currently means the HandPeg shader; vendor hardware is listed but disabled.
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

    // Pitch Shift -12..+12 st via the Pitch and Tempo filter (tempo unchanged), applied to the clip and its stream lanes.
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

        // Sideways drags apply live (no keyboard focus); typing waits for Enter or focus loss.
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

    // ----- Commit -----

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

            // The thumb (or track RepeatButton) still holds capture here (PreviewMouseLeftButtonUp), so ShowSpeed skipped the slider; after
            // release, resync it to the committed value (detent, or the old speed if refused).
            Dispatcher.BeginInvoke(() =>
            {
                if (!_speedHeld)
                    ShowSpeed();
            }, DispatcherPriority.Input);
        }
    }

    // Drag preview: show the speed (slider, box, resulting length) and hold it as owed until release.
    private void Preview(double speed)
    {
        _speedOwed = speed;
        ShowSpeed(speed);
    }

    // Commits speed (with family, ripple per setting) as one Undo step via SetClipSpeed (no prior checkpoint).
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

    // Sets a look option on the target and its family (Cutouts share the source picture; stream lanes are its audio) as one Undo step.
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

    // Pitch Shift: paced with other pane writes (each write rebuilds the audio program); a slider drag is one Undo step.
    private void PitchChanged(double semitones)
    {
        _drag.Follow();
        _writes.Queue(PitchWriteKey, () => WritePitch(semitones));
    }

    // Writes semitones into the Pitch and Tempo filter of the target and each audio family member (cloned filters) after the Undo step.
    // The audio cards' working copy (EffectsPane.Audio.cs) is updated too, so it never writes a stale value back.
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

    /// <summary>Sets PitchSemitones (tempo unchanged); PitchTempo is enabled while pitch or tempo differs from neutral.</summary>
    internal static void WithPitch(TrackAudioFilters filters, double semitones)
    {
        filters.PitchSemitones = Math.Round(Math.Max(-12, Math.Min(semitones, 12)));
        filters.PitchTempo = filters.PitchSemitones != 0 || Math.Abs(filters.Tempo - 1) > 1e-9;
    }

    // ----- Display -----

    // Refreshes controls from the clip (or a drag preview speed) with writes suppressed.
    private void ShowSpeed(double? dragged = null)
    {
        if (_speedTarget is not { } target || _speedSlider is null)
            return;

        _speedShowing = true;
        try
        {
            // Skip the slider while it holds capture: the thumb follows the pointer; the detent applies on release.
            var speed = dragged ?? target.Speed;
            if (!_speedSlider.IsMouseCaptureWithin)
                _speedSlider.Value = Math.Log2(Math.Max(Layer.SlowestSpeed, Math.Min(speed, Layer.FastestSpeed)));
            // Boxes being typed in keep their text.
            if (_speedBox is { } box && (dragged is not null || !_typedBoxes.Contains(box)))
                box.Text = speed.ToString("0.00", CultureInfo.InvariantCulture);
            foreach (var (pill, value) in _speedPillButtons)
                pill.IsChecked = Math.Abs(speed - value) < 0.0005;

            // Timeline length = media span / speed (current or dragged).
            if (_durationBox is { } duration && !_typedBoxes.Contains(duration))
            {
                var model = _vm?.ClipOf(target);
                var span = model is { } clip ? clip.MediaEnd - clip.MediaOffset : target.Duration * target.Speed;
                if (dragged is null && target.HasSpeedRamp && model is not null)
                    duration.Text = TimeDisplay.Format(_rampLive is { Duration: > 0 } live ? live.Duration : model.Duration);
                else
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

            ShowRamp(target);
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
            // Speed, length or option changed (edit or Undo): refresh, unless a drag is in progress.
            case nameof(Layer.Speed) or nameof(Layer.ClipSpeed) or nameof(Layer.Duration) or nameof(Layer.MediaOffset)
                or nameof(Layer.SmoothSlowMo) or nameof(Layer.KeepPitch) or nameof(Layer.SpectralAudio) or nameof(Layer.AudioFilters)
                or nameof(Layer.SpeedKeys) or nameof(Layer.HasSpeedRamp):
                if (!_speedHeld && ReferenceEquals(_layer, _speedFor))
                    ShowSpeed();
                break;

            // Opacity or keys changed elsewhere (timeline, Keyframes pane): rebuild only when OpacityShows flips, not on every value change.
            case nameof(Layer.Opacity) or Layer.KeysChanged:
                if (_layer is { HasStyle: true, IsAdjustment: false } layer && OpacityShows(layer) != _opacityCardShown)
                    _rebuild.Request();

                break;
        }
    }

    // ----- Opacity card -----

    /// <summary>True when the Opacity card is meaningful: below 100 %, keyed, or added/shown earlier this session.</summary>
    private static bool OpacityShows(Layer layer) =>
        layer.Opacity < 100 || layer.HasKeys(KeyProperty.Opacity) || (layer.ClipId != Guid.Empty && OpacityShownFor.Contains(layer.ClipId));

    private partial bool ShowsOpacityCard(Layer layer)
    {
        // Sticky for the session so dragging back to 100 % never removes it under the pointer.
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

    // ----- Pure helpers (also used by goldens) -----

    /// <summary>Slider position to speed: 2^place rounded to 0.01, snapping to a pill speed within <see cref="PillDetent"/>.</summary>
    internal static double SpeedOfSlider(double place)
    {
        foreach (var pill in SpeedPills)
        {
            if (Math.Abs(place - Math.Log2(pill)) <= PillDetent)
                return pill;
        }

        return Math.Round(Math.Max(Layer.SlowestSpeed, Math.Min(Math.Pow(2, place), Layer.FastestSpeed)), 2);
    }

    /// <summary>Parses a typed speed (optional trailing "x"), clamped to 0.1x-16x; false if not a positive number.</summary>
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

    /// <summary>Parses Target Duration: the current TimeDisplay format, or plain seconds (optional "s").</summary>
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
    /// Speed making <paramref name="mediaSpan"/> seconds of media last <paramref name="target"/> seconds: their ratio clamped to 0.1x-16x and
    /// rounded to 0.001 (as edits store it); NaN when either is not a positive length.
    /// </summary>
    internal static double SpeedForDuration(double mediaSpan, double target) =>
        mediaSpan > 0 && target > 0 && double.IsFinite(mediaSpan) && double.IsFinite(target)
            ? Math.Round(Math.Max(Layer.SlowestSpeed, Math.Min(mediaSpan / target, Layer.FastestSpeed)), 3)
            : double.NaN;

    /// <summary>Speed label for pills and menus ("0.25x", "1x", "16x").</summary>
    internal static string SpeedLabel(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <summary>
    /// Reason vendor optical flow is unavailable (UI-side check, no device created): NVIDIA with nvofapi64.dll is not wired up in this build;
    /// other GPUs have no usable path. Cached once adapters are listed.
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

// ----- Speed Ramp (Speed & Time card, Pass 5.6.5) -----
// A keyframed speed curve (Layer.SpeedKeys; SpeedRamp maps sequence to file time): clip time across, log speed up (0.1x-16x, 1x marked,
// the sub-1x band shaded as the optical-flow zone). Click adds a key (the first click seeds start and end at the clip's speed), drag
// shapes, double-click removes; Clear returns to constant speed. A gesture is one Undo step (Checkpoint on its first change). On release,
// a ramp that newly dips below 1x switches Smooth Slow-Mo (optical flow) on for the video and its family; the Smooth Slow-Mo check stays
// the per-clip override, and is never switched back on while the dip stays.
public partial class EffectsPane
{
    private SpeedRampEditor? _ramp;
    private TextBlock? _rampNote;

    // A ramp gesture has taken its Undo step; whether the ramp dipped below 1x before it.
    private bool _rampStepTaken, _rampDippedBefore;

    // Pass 5.7.1 auto-duration: the file seconds the clip played when the gesture began (kept by the fit on release), the live fit while
    // shaping (the length box and note show it), and the last fit that stopped at the next clip (its clip and length: the note warns while
    // the clip still has that length, so an Undo clears it).
    private double _rampSpan;
    private SpeedRampFit? _rampLive;

    // The clip's length when the gesture began: the editor shapes keys over it while live fits resize the clip.
    private double _rampBase;
    private (Guid Clip, double Duration)? _rampClamped;

    private StackPanel RampRow(Layer target, bool sound)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
        var head = new DockPanel();
        var clear = new Button { Content = "Clear", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, ToolTip = "Back to constant speed" };
        AutomationProperties.SetName(clear, "Clear Speed Ramp");
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.Add(clear);
        head.Children.Add(new TextBlock { Text = "Speed Ramp", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Click adds a key, drag shapes, double-click removes" });
        panel.Children.Add(head);

        var editor = new SpeedRampEditor { Height = 84, Margin = new Thickness(0, 3, 0, 0) };
        AutomationProperties.SetName(editor, "Speed Ramp Curve");
        panel.Children.Add(editor);
        _rampNote = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        _rampNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        panel.Children.Add(_rampNote);

        _ramp = editor;
        editor.Shaped += (keys, done) => ShapeRamp(keys, done, sound);
        clear.Click += (_, _) =>
        {
            if (_speedTarget is { HasSpeedRamp: true })
                ShapeRamp([], done: true, sound);
        };
        ShowRamp(target);
        return panel;
    }

    // Applies a ramp edit to the target and its family (Cutouts, stream lanes share its source time).
    private void ShapeRamp(IReadOnlyList<Keyframe> keys, bool done, bool sound)
    {
        if (_vm is not { } vm || _speedTarget is not { } target)
            return;

        if (!_rampStepTaken)
        {
            _rampDippedBefore = target.SpeedKeys.Any(k => k.Value < 1);
            (_rampSpan, _rampClamped, _rampBase) = (vm.RampSourceSpan(target), null, vm.ClipOf(target)?.Duration ?? target.Duration);
            vm.Checkpoint(keys.Count == 0 ? $"clear the speed ramp of {target.Name}" : $"ramp the speed of {target.Name}");
            _rampStepTaken = true;
        }

        // Live fits resize the clip mid-gesture: re-time the editor's keys (shaped over the gesture's first length) to its current length.
        var length = vm.ClipOf(target)?.Duration ?? target.Duration;
        if (_rampBase > 0 && length > 0 && Math.Abs(length - _rampBase) > 1e-9)
            keys = [.. keys.Select(k => k with { Time = k.Time * length / _rampBase })];

        var family = SpeedFamily(target);
        using (OwnLooks())
        {
            target.SetSpeedKeys(keys);
            foreach (var member in family)
                member.SetSpeedKeys(keys);

            // Auto optical flow: only when this gesture made the first dip (an explicit Off stays off while the dip stays).
            if (done && !sound && !_rampDippedBefore && keys.Any(k => k.Value < 1) && !target.SmoothSlowMo)
            {
                target.SmoothSlowMo = true;
                foreach (var member in family)
                    member.SmoothSlowMo = true;
            }
        }

        if (done)
        {
            // Auto-duration (Pass 5.7.1): the clip and its family take the length that plays the same file span, in the gesture's step.
            (_rampStepTaken, _rampLive) = (false, null);
            var outcome = vm.FitClipToRamp(target, target.SpeedKeys, _rampSpan);
            if (outcome.Clamped && vm.ClipOf(target) is { } fitted)
                _rampClamped = (fitted.Id, fitted.Duration);
            ShowSpeed();
        }
        else
        {
            _rampLive = vm.PreviewRampFit(target, target.SpeedKeys, _rampSpan);

            // Preview the fit itself (no past-EOF black tail while shaping): bounds and re-timed keys on the clip and family inside the
            // gesture's step, resized only when the length moves half a frame (else just the re-timed keys); the release commits the same fit.
            if (_rampLive is { Duration: > 0 } live && vm.ClipOf(target) is { } model)
            {
                var rate = vm.Document.FrameRate;
                var halfFrame = rate.IsValid ? rate.Denominator / (2.0 * rate.Numerator) : 1 / 120.0;
                if (Math.Abs(model.Duration - live.Duration) >= halfFrame)
                {
                    vm.FitClipToRamp(target, target.SpeedKeys, _rampSpan, AppSettings.Current.SpeedRipplesTimeline, live: true);
                }
                else
                {
                    using (OwnLooks())
                    {
                        target.SetSpeedKeys(live.Keys);
                        foreach (var member in family)
                            member.SetSpeedKeys(live.Keys);
                    }
                }
            }

            ShowRampNote(target);
            ShowRampLength();
        }
    }

    // While shaping: the length box shows the length the release will give.
    private void ShowRampLength()
    {
        if (_durationBox is { } box && _rampLive is { Duration: > 0 } live && !_typedBoxes.Contains(box))
            box.Text = TimeDisplay.Format(live.Duration);
    }

    // Refreshes the editor and its note from the clip (ShowSpeed).
    private void ShowRamp(Layer target)
    {
        if (_ramp is not { } ramp)
            return;

        var length = _vm?.ClipOf(target) is { } clip ? clip.Duration : target.Duration;
        ramp.Show(target.SpeedKeys, target.Speed, Math.Max(length, 0.05));
        ShowRampNote(target);
    }

    // Optical flow on the dip, or a ramp reading past the file's end.
    private void ShowRampNote(Layer target)
    {
        if (_rampNote is not { } note)
            return;

        string? text = null;
        var model = _vm?.ClipOf(target);
        if (_rampLive is { Clamped: true } || (_rampClamped is { } clamped && model is not null && clamped.Clip == model.Id && Math.Abs(model.Duration - clamped.Duration) < 1e-6))
            text = "Clamped at next clip";
        else if (target.HasSpeedRamp && model is { PlaysFile: true, WholeDuration: > 0 } clip
            && clip.MediaOffset + SpeedRamp.MediaSeconds(target.SpeedKeys, target.Speed, clip.Duration) > clip.WholeDuration + 1e-3)
            text = "Runs past the file's end";
        else if (target.SpeedKeys.Any(k => k.Value < 1))
            text = target.SmoothSlowMo ? "Optical Flow on for the slow-mo" : "Optical Flow off (override)";

        note.Text = text ?? "";
        note.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// Speed ramp curve editor: time 0..clip length across, log2 speed (Layer.SlowestSpeed..FastestSpeed) up. Raises <see cref="Shaped"/> with
/// the whole key list on every change (false) and on release (true); the host applies them.
/// </summary>
internal sealed class SpeedRampEditor : FrameworkElement
{
    private const double Pad = 6, NodeReach = 6;
    private static readonly double Low = Math.Log2(Layer.SlowestSpeed), High = Math.Log2(Layer.FastestSpeed);
    private static readonly double[] Marks = [0.25, 0.5, 1, 2, 4];

    private static readonly Brush Surface = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22)));
    private static readonly Pen Border = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30))), 1));
    private static readonly Brush FlowZone = Frozen(new SolidColorBrush(Color.FromArgb(36, 0x4F, 0xC3, 0xF7)));
    private static readonly Pen Grid = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(50, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen Unity = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xFF, 0xFF))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen Curve = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7))), 1.8));
    private static readonly Brush Node = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Pen NodeEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22))), 1.2));
    private static readonly Brush MarkText = Frozen(new SolidColorBrush(Color.FromArgb(150, 0xFF, 0xFF, 0xFF)));

    private List<Keyframe> _keys = [];
    private double _speed = 1, _length = 1;

    // Key being dragged (-1 for none) and whether the gesture changed anything.
    private int _dragged = -1;
    private bool _changed;

    /// <summary>The keys after a change, and whether the gesture ended.</summary>
    public event Action<IReadOnlyList<Keyframe>, bool>? Shaped;

    public SpeedRampEditor()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    /// <summary>Shows a clip's ramp (ignored mid-drag: the drag owns the keys).</summary>
    public void Show(IReadOnlyList<Keyframe> keys, double speed, double length)
    {
        if (_dragged >= 0)
            return;

        (_keys, _speed, _length) = (keys.ToList(), speed, Math.Max(length, 0.05));
        InvalidateVisual();
    }

    private double Width0 => Math.Max(ActualWidth - 2 * Pad, 1);

    private double Height0 => Math.Max(ActualHeight - 2 * Pad, 1);

    private double XOf(double seconds) => Pad + Math.Clamp(seconds / _length, 0, 1) * Width0;

    private double YOf(double speed) =>
        Pad + (1 - (Math.Log2(Math.Clamp(speed, Layer.SlowestSpeed, Layer.FastestSpeed)) - Low) / (High - Low)) * Height0;

    private double SecondsAt(double x) => Math.Round(Math.Clamp((x - Pad) / Width0, 0, 1) * _length, 3);

    // Pointer height to speed: the speed slider's rule (2^place to 0.01, detents at the pill speeds).
    private double SpeedAt(double y) => EffectsPane.SpeedOfSlider(Low + (1 - Math.Clamp((y - Pad) / Height0, 0, 1)) * (High - Low));

    protected override void OnRender(DrawingContext drawing)
    {
        var box = new Rect(0.5, 0.5, Math.Max(ActualWidth - 1, 0), Math.Max(ActualHeight - 1, 0));
        drawing.DrawRoundedRectangle(Surface, Border, box, 4, 4);
        if (ActualWidth < 2 * Pad + 4 || ActualHeight < 2 * Pad + 4)
            return;

        // Optical-flow zone (below 1x), speed marks, then the curve and its keys.
        var unity = YOf(1);
        drawing.DrawRectangle(FlowZone, null, new Rect(Pad, unity, Width0, Math.Max(Pad + Height0 - unity, 0)));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var mark in Marks)
        {
            var y = YOf(mark);
            drawing.DrawLine(mark == 1 ? Unity : Grid, new Point(Pad, y), new Point(Pad + Width0, y));
            var label = new FormattedText(EffectsPane.SpeedLabel(mark), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, MarkText, pixelsPerDip);
            drawing.DrawText(label, new Point(Pad + 2, y - label.Height));
        }

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            const int Steps = 96;
            for (var i = 0; i <= Steps; i++)
            {
                var seconds = _length * i / Steps;
                var point = new Point(XOf(seconds), YOf(SpeedRamp.SpeedAt(_keys, _speed, seconds)));
                if (i == 0)
                    context.BeginFigure(point, false, false);
                else
                    context.LineTo(point, true, false);
            }
        }

        curve.Freeze();
        drawing.DrawGeometry(null, Curve, curve);
        foreach (var key in _keys)
            drawing.DrawEllipse(Node, NodeEdge, new Point(XOf(key.Time), YOf(key.Value)), 4, 4);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        var at = e.GetPosition(this);
        var hit = _keys.FindIndex(key => (new Point(XOf(key.Time), YOf(key.Value)) - at).Length <= NodeReach);
        if (hit >= 0 && e.ClickCount >= 2)
        {
            // Double-click removes a key; fewer than two left is no ramp at all.
            _keys.RemoveAt(hit);
            if (_keys.Count < 2)
                _keys.Clear();
            InvalidateVisual();
            Shaped?.Invoke(_keys.ToList(), true);
            return;
        }

        if (hit < 0)
        {
            // The first key seeds the clip's ends at its constant speed, so the curve starts where the clip runs now.
            if (_keys.Count == 0)
                _keys = [new Keyframe(0, _speed), new Keyframe(Math.Round(_length, 3), _speed)];
            var added = new Keyframe(SecondsAt(at.X), SpeedAt(at.Y));
            hit = _keys.FindIndex(key => key.Time > added.Time);
            hit = hit < 0 ? _keys.Count : hit;
            _keys.Insert(hit, added);
            _changed = true;
        }

        if (!CaptureMouse())
            return;

        _dragged = hit;
        InvalidateVisual();
        if (_changed)
            Shaped?.Invoke(_keys.ToList(), false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragged < 0 || !IsMouseCaptured)
            return;

        var at = e.GetPosition(this);
        var (low, high) = (_dragged > 0 ? _keys[_dragged - 1].Time + 0.01 : 0, _dragged < _keys.Count - 1 ? _keys[_dragged + 1].Time - 0.01 : _length);
        var moved = _keys[_dragged] with { Time = Math.Round(Math.Clamp(SecondsAt(at.X), Math.Min(low, high), Math.Max(low, high)), 3), Value = SpeedAt(at.Y) };
        if (moved == _keys[_dragged])
            return;

        _keys[_dragged] = moved;
        _changed = true;
        InvalidateVisual();
        Shaped?.Invoke(_keys.ToList(), false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragged >= 0)
            Finish();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragged >= 0)
            Finish();
    }

    // Ends the gesture; a changed ramp is committed.
    private void Finish()
    {
        _dragged = -1;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (!_changed)
            return;

        _changed = false;
        Shaped?.Invoke(_keys.ToList(), true);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
