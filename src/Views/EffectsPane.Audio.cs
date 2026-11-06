using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp.Views;

// ----- A sound's own cards (Pass 5.2B, the Studio Audio Suite) -----
// A sound selected (an audio clip, a clip of a lane of a file's streams): its filters as cards of their own under its effects: Studio Voice
// (its wet/dry mix and its mix keys), the De-Esser, the Equalizer with its parametric curve, and the Compressor and Gate. They edit a copy of
// the sound's TrackAudioFilters and put it on the sound as the Audio Filters dialog's OK does (a checkpoint, then the filters set), a run of
// changes one step of Undo (a checkpoint when the first comes after a pause, or as a gesture of the curve begins), and written at most
// thirty times a second while a slider is dragged (each write is a new sound program for the engine).
public partial class EffectsPane
{
    // The copy being edited and the sound it is put on; whether a write is owed (a drag's), and when the last one was made.
    private TrackAudioFilters? _audio;
    private Layer? _audioLayer;
    private bool _audioOwed, _writingAudio, _buildingAudio;
    private DateTime _lastAudioWrite;
    private DispatcherTimer? _audioWrite;

    /// <summary>The sound's cards' "All Audio Filters..." button: the window opens the sound's Audio Filters dialog.</summary>
    public event Action<Layer>? AudioFiltersRequested;

    /// <summary>The cards of a sound's filters, after its effects (in the pinned panel: they are in the chain's own order, never reordered).</summary>
    private void AddAudioCards(Layer sound)
    {
        FlushAudioWrite();
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

            // Studio Voice: on or off, its wet/dry mix, and its mix keys at the playhead (seconds into the clip, as the dialog keys them).
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
                var at = Math.Max(ClipTime, 0);
                var keys = filters.StudioVoiceMixKeys ??= [];
                keys.RemoveAll(k => Math.Abs(k.Time - at) < 0.001);
                keys.Add(new Keyframe(at, Math.Clamp(filters.StudioVoiceMix, 0, 100), EasingType.Linear));
                keys.Sort((a, b) => a.Time.CompareTo(b.Time));
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

            // The De-Esser.
            var esser = AudioCard("De-Esser", filters.DeEsser, on => filters.DeEsser = on,
                "Tames harsh S, SH and T sounds: their band (5.5 to 9 kHz) is turned down only while they are loud.", out var esserBody);
            esserBody.Children.Add(AudioSlider("Amount", "%", 0, 100, filters.DeEsserAmount, 50, 0, value => filters.DeEsserAmount = value));
            PinnedPanel.Children.Add(esser);

            // The Equalizer: its curve, each band dragged, widened with the wheel, reset with a double-click.
            var curve = new ParametricEqControl { Height = 130, Margin = new Thickness(0, 2, 0, 2), Filters = filters };
            var equalizer = AudioCard("Equalizer", filters.Equalizer, on =>
            {
                filters.Equalizer = on;
                curve.Refresh();
            }, "Ten bands: drag a node across for its frequency, up or down for its gain (-12 to +12 dB); the wheel over it widens or narrows it; a double-click puts it back.", out var equalizerBody, out var equalizerSwitch);
            curve.EditStarted += () => _lastAudioWrite = DateTime.MinValue;
            curve.BandChanged += _ =>
            {
                // A band moved is meant to be heard: the equalizer switched on with it.
                if (!filters.Equalizer)
                {
                    filters.Equalizer = true;
                    equalizerSwitch.IsChecked = true;
                }

                AudioChanged(newStep: false);
            };
            curve.EditEnded += FlushAudioWrite;
            equalizerBody.Children.Add(curve);
            PinnedPanel.Children.Add(equalizer);

            // The dynamics: the compressor (threshold, ratio) and the gate (threshold), in one card.
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
                FlushAudioWrite();
                AudioFiltersRequested?.Invoke(sound);
            };
            PinnedPanel.Children.Add(all);
        }
        finally
        {
            _buildingAudio = false;
        }
    }

    /// <summary>A card of the sound's: its title as its On switch (a click one step of Undo), and the body its values go in.</summary>
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

    /// <summary>A value of the sound's: its name, a slider and a number box (double-click resets to <paramref name="usual"/>).</summary>
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
    /// A value of the sound's changed here: written now, or (written less than a thirtieth of a second ago: a drag) when that time is up.
    /// <paramref name="newStep"/>: a step of its own (a switch, a key), never the end of a run of changes.
    /// </summary>
    private void AudioChanged(bool newStep)
    {
        if (_buildingAudio || _audio is null || _audioLayer is null)
            return;

        if (newStep)
        {
            _lastAudioWrite = DateTime.MinValue;
            WriteAudio();
            return;
        }

        if (_audioWrite is { IsEnabled: true })
        {
            _audioOwed = true;
            return;
        }

        WriteAudio();
        if (_audioWrite is null)
        {
            _audioWrite = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / DragWritesPerSecond) };
            _audioWrite.Tick += (_, _) =>
            {
                if (_audioOwed)
                    WriteAudio();
                else
                    _audioWrite?.Stop();
            };
        }

        _audioWrite.Start();
    }

    /// <summary>A write owed (a drag's last value) made now.</summary>
    private void FlushAudioWrite()
    {
        _audioWrite?.Stop();
        if (_audioOwed)
            WriteAudio();
    }

    // The copy put on the sound, as the Audio Filters dialog's OK puts it: a checkpoint for the first change of a run, then the filters.
    private void WriteAudio()
    {
        _audioOwed = false;
        if (_vm is null || _audio is null || _audioLayer is not { } sound)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastAudioWrite).TotalMilliseconds > 1000)
            _vm.Checkpoint($"change the audio filters of {sound.Name}");
        _lastAudioWrite = now;

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

    /// <summary>The sound's filters changed elsewhere (its dialog, Undo): its cards are made again from them.</summary>
    private void AudioFiltersChangedElsewhere()
    {
        if (_writingAudio || _rebuildQueued)
            return;

        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }
}
