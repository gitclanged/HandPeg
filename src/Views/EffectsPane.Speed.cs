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
        (_ramp, _rampNote, _rampStepTaken) = (null, null, false);
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
