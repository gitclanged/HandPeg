using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp.Views;

// ----- Audio filter cards (Studio Audio Suite) -----
// For a selected audio source (audio clip, or a clip on a file-stream lane), its filters appear as cards below its effects: Studio Voice
// (wet/dry mix and mix keys), De-Esser, Equalizer with parametric curve, and Compressor plus Gate. Cards edit a copy of the source's
// TrackAudioFilters and apply it like the Audio Filters dialog's OK (checkpoint, then set). A run of changes is one Undo step (checkpoint
// on the first change after a pause, or when a curve gesture begins); during slider drags writes are capped at 30 per second, since each
// write rebuilds the engine's audio program.
public partial class EffectsPane
{
    // Working filter copy and its target layer; flags for an in-progress write (ignores self-notification) and card construction.
    private TrackAudioFilters? _audio;
    private Layer? _audioLayer;
    private bool _writingAudio, _buildingAudio;

    /// <summary>Raised by "All Audio Filters..." so the window opens that source's Audio Filters dialog.</summary>
    public event Action<Layer>? AudioFiltersRequested;

    /// <summary>Adds the audio filter cards after the effects, in the pinned panel (fixed chain order, not reorderable).</summary>
    private void AddAudioCards(Layer sound)
    {
        _writes.Flush();
        var filters = sound.AudioFilters.Clone();
        (_audioLayer, _audio) = (sound, filters);
        _buildingAudio = true;
        try
        {
            var heading = new TextBlock
            {
                Text = "SOUND", FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 0, 3),
                ToolTip = "The sound's own processing, in the order it runs: Studio Voice after the de-noise, then the compressor and gate, the De-Esser, the tone and the equalizer. The rest of its filters are in All Audio Filters...",
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            PinnedPanel.Children.Add(heading);

            // Studio Voice: toggle, wet/dry mix, and mix keys at the playhead (clip-local seconds, as the dialog keys them).
            var voice = AudioCard("Studio Voice", filters.StudioVoice, on => filters.StudioVoice = on,
                "A voice made to sound recorded in a studio: its band isolated, the room and the noise between words gated away, its presence lifted, its level held even. Blended with the dry sound by the mix.",
                out var voiceBody);
            voiceBody.Children.Add(AudioSlider("Wet/Dry Mix", "%", 0, 100, filters.StudioVoiceMix, 85, 0, value => filters.StudioVoiceMix = value));
            var keysRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 1) };
            var keysText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            keysText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var addKey = new Button { Content = "Add Mix Key", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Keeps the mix as it is now at the playhead: with keys, the mix follows them along the clip." };
            var clearKeys = new Button { Content = "Clear", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Takes every mix key away." };
            void ShowKeys()
            {
                var count = filters.StudioVoiceMixKeys?.Count ?? 0;
                keysText.Text = count == 0 ? "no keys" : string.Create(CultureInfo.InvariantCulture, $"{count} key{(count == 1 ? "" : "s")}");
                clearKeys.IsEnabled = count > 0;
            }

            addKey.Click += (_, _) =>
            {
                filters.PutMixKey(Math.Max(ClipTime, 0));
                ShowKeys();
                AudioChanged(newStep: true);
            };
            clearKeys.Click += (_, _) =>
            {
                filters.StudioVoiceMixKeys = null;
                ShowKeys();
                AudioChanged(newStep: true);
            };
            AutomationProperties.SetName(addKey, "Studio Voice Add Mix Key at Playhead");
            AutomationProperties.SetName(clearKeys, "Studio Voice Clear Mix Keys");
            keysRow.Children.Add(addKey);
            keysRow.Children.Add(clearKeys);
            keysRow.Children.Add(keysText);
            ShowKeys();
            voiceBody.Children.Add(keysRow);
            PinnedPanel.Children.Add(voice);

            // De-Esser.
            var esser = AudioCard("De-Esser", filters.DeEsser, on => filters.DeEsser = on,
                "Tames harsh S, SH and T sounds: their band (5.5 to 9 kHz) is turned down only while they are loud.", out var esserBody);
            esserBody.Children.Add(AudioSlider("Amount", "%", 0, 100, filters.DeEsserAmount, 50, 0, value => filters.DeEsserAmount = value));
            PinnedPanel.Children.Add(esser);

            // Equalizer: parametric curve (drag bands, wheel for width, double-click to reset).
            var curve = new ParametricEqControl { Height = 130, Margin = new Thickness(0, 2, 0, 2), Filters = filters };
            var equalizer = AudioCard("Equalizer", filters.Equalizer, on =>
            {
                filters.Equalizer = on;
                curve.Refresh();
            }, "Ten bands: drag a node across for its frequency, up or down for its gain (-12 to +12 dB); the wheel over it widens or narrows it; a double-click puts it back.", out var equalizerBody, out var equalizerSwitch);
            curve.EditStarted += _writes.Begin;
            curve.BandChanged += _ =>
            {
                // Moving a band implies intent to hear it: auto-enable the equalizer.
                if (!filters.Equalizer)
                {
                    filters.Equalizer = true;
                    equalizerSwitch.IsChecked = true;
                }

                AudioChanged(newStep: false);
            };
            curve.EditEnded += _writes.End;
            equalizerBody.Children.Add(curve);
            PinnedPanel.Children.Add(equalizer);

            // Dynamics card: compressor (threshold, ratio) and noise gate (threshold).
            var dynamics = AudioCard("Compressor", filters.Compressor, on => filters.Compressor = on,
                "Evens out loud and quiet: everything above the threshold is turned down by the ratio.", out var dynamicsBody);
            dynamicsBody.Children.Add(AudioSlider("Threshold", "dB", -60, 0, filters.CompressorThreshold, -18, 0, value => filters.CompressorThreshold = value));
            dynamicsBody.Children.Add(AudioSlider("Ratio", ":1", 1, 20, filters.CompressorRatio, 4, 1, value => filters.CompressorRatio = value));
            var gate = new CheckBox { Content = "Noise Gate", IsChecked = filters.NoiseGate, Margin = new Thickness(0, 6, 0, 2), ToolTip = "Silences the sound whenever it is quieter than the threshold: breathing and room noise between words." };
            gate.Click += (_, _) =>
            {
                filters.NoiseGate = gate.IsChecked == true;
                AudioChanged(newStep: true);
            };
            AutomationProperties.SetName(gate, "Noise Gate On");
            dynamicsBody.Children.Add(gate);
            dynamicsBody.Children.Add(AudioSlider("Gate Threshold", "dB", -80, 0, filters.GateThreshold, -40, 0, value => filters.GateThreshold = value));
            PinnedPanel.Children.Add(dynamics);

            var all = new Button { Content = "All Audio Filters...", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Every filter of this sound in one window: the high-pass, de-noise, bass and treble, the limiter, pitch and tempo, the stereo width, and these." };
            all.Click += (_, _) =>
            {
                _writes.Flush();
                AudioFiltersRequested?.Invoke(sound);
            };
            PinnedPanel.Children.Add(all);
        }
        finally
        {
            _buildingAudio = false;
        }
    }

    /// <summary>Builds an audio card: a title checkbox as its On switch (each click one Undo step) and a body panel for its values.</summary>
    private Border AudioCard(string title, bool on, Action<bool> switched, string about, out StackPanel body) =>
        AudioCard(title, on, switched, about, out body, out _);

    private Border AudioCard(string title, bool on, Action<bool> switched, string about, out StackPanel body, out CheckBox toggle)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6, 3, 4, 5), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), ToolTip = about };
        border.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var root = new StackPanel();
        border.Child = root;

        var check = new CheckBox { Content = title, IsChecked = on, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 1) };
        check.Click += (_, _) =>
        {
            switched(check.IsChecked == true);
            AudioChanged(newStep: true);
        };
        AutomationProperties.SetName(check, $"{title} On");
        root.Children.Add(check);
        toggle = check;

        body = new StackPanel { Margin = new Thickness(18, 3, 0, 0) };
        root.Children.Add(body);
        return border;
    }

    /// <summary>Builds a labelled slider plus number box row (double-click resets to <paramref name="usual"/>).</summary>
    private FrameworkElement AudioSlider(string label, string unit, double min, double max, double value, double usual, int decimals, Action<double> set)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = $"{label} ({unit})", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var usualText = usual.ToString(CultureInfo.InvariantCulture);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = 1, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        NumericInput.SetDefault(slider, usualText);
        AutomationProperties.SetName(slider, label);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        var box = new TextBox { Width = 56, Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("ValueBox") as Style };
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Slider.Value)) { Source = slider, Mode = BindingMode.TwoWay, StringFormat = "{0:0." + new string('#', Math.Max(decimals, 1)) + "}" });
        NumericInput.SetDefault(box, usualText);
        NumericInput.SetMinimum(box, min);
        NumericInput.SetMaximum(box, max);
        NumericInput.SetDragStep(box, (max - min) / 200);
        NumericInput.SetDecimals(box, decimals);
        AutomationProperties.SetName(box, label + " Value");
        Grid.SetColumn(box, 2);
        row.Children.Add(box);

        slider.ValueChanged += (_, e) =>
        {
            set(Math.Round(e.NewValue, Math.Max(decimals, 1)));
            AudioChanged(newStep: false);
        };
        return row;
    }

    /// <summary>
    /// Handles a local audio value change, paced with the pane's other writes (<see cref="GestureWriter{TKey}"/>; one Undo step per slider
    /// drag). <paramref name="newStep"/>: a discrete action (switch, key) written immediately as its own step, never merged into a run.
    /// </summary>
    private void AudioChanged(bool newStep)
    {
        if (_buildingAudio || _audio is null || _audioLayer is null)
            return;

        if (newStep)
        {
            _writes.Flush();
            using (OwnLooks())
                WriteAudio(startRun: true);
            return;
        }

        _drag.Follow();
        _writes.Queue(AudioWriteKey, () => WriteAudio(startRun: false));
    }

    // Applies the working copy like the dialog's OK: undo step first (one per drag or run), then the filters.
    private void WriteAudio(bool startRun)
    {
        if (_vm is null || _audio is null || _audioLayer is not { } sound)
            return;

        Step(sound, nameof(Layer.AudioFilters), $"change the audio filters of {sound.Name}", startRun);
        _writingAudio = true;
        try
        {
            sound.AudioFilters = _audio.Clone();
        }
        finally
        {
            _writingAudio = false;
        }
    }

    /// <summary>External filter change (dialog, Undo): schedules a card rebuild unless this pane is writing.</summary>
    private void AudioFiltersChangedElsewhere()
    {
        if (!_writingAudio)
            _rebuild.Request();
    }
}
