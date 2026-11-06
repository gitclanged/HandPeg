using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// The processing of one sound: compressor, gate, de-noise, Studio Voice and the De-Esser (5.2B), tone, and behind "Show Advanced Filters"
/// the limiter, the equalizer (its parametric curve over its ten sliders, 5.2B), pitch/tempo and stereo width. Works on a copy, which the
/// caller takes on OK.
/// </summary>
public partial class TrackAudioFiltersDialog : Window
{
    // Where Add Mix Key puts a key: seconds from the start of what the filters belong to (the clip's start); null for no playhead to key at.
    private readonly double? _keyTime;

    /// <param name="heading">Which sound the filters are for.</param>
    /// <param name="filters">Its filters as they are; a copy is edited.</param>
    /// <param name="keyTime">The playhead, in seconds from the start of what the filters belong to (a clip's: its Start on the Sequence), for Studio Voice's mix keys; null for none.</param>
    public TrackAudioFiltersDialog(string heading, TrackAudioFilters filters, double? keyTime = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);

        // Ctrl+Z and Ctrl+Y take back and redo what is changed here, a press at a time (4.3E).
        _ = new BoundUndo(this);
        TrackText.Text = heading;
        _keyTime = keyTime is { } at && double.IsFinite(at) ? Math.Max(at, 0) : null;
        DataContext = Filters = filters.Clone();
        EqCurve.Filters = Filters;
        UpdateMixKeys();

        // The curve follows every value it is drawn from (the sliders, the high-pass and the shelves, their switches, Ctrl+Z): it is made again
        // at the next render, once the bindings have handed the values on.
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => EqCurve.Refresh()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => EqCurve.Refresh()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => EqCurve.Refresh()));
        EqCurve.BandChanged += CurveBandChanged;
    }

    /// <summary>The filters as edited. Only meaningful once the dialog has been confirmed.</summary>
    public TrackAudioFilters Filters { get; private set; }

    // A fresh set, keeping only whether the advanced part is showing.
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        DataContext = Filters = new TrackAudioFilters { ShowAdvanced = Filters.ShowAdvanced };
        EqCurve.Filters = Filters;
        UpdateMixKeys();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    // A band moved on the curve: its slider shows its gain (the binding hands it back, the same value), and the equalizer is on (a band moved
    // is meant to be heard).
    private void CurveBandChanged(int band)
    {
        if (FindName($"EqSlider{band.ToString(CultureInfo.InvariantCulture)}") is Slider slider && band < Filters.EqualizerGains.Length)
            slider.Value = Filters.EqualizerGains[band];
        if (!Filters.Equalizer)
            EqualizerBox.IsChecked = true;
    }

    // ----- Studio Voice's mix keys (5.2B) -----

    private void AddMixKey_Click(object sender, RoutedEventArgs e)
    {
        if (_keyTime is not { } at)
            return;

        // One key a moment: a key already there takes the mix as it is set now.
        var keys = Filters.StudioVoiceMixKeys ??= [];
        keys.RemoveAll(k => Math.Abs(k.Time - at) < 0.001);
        keys.Add(new Keyframe(at, Math.Clamp(Filters.StudioVoiceMix, 0, 100), EasingType.Linear));
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        UpdateMixKeys();
    }

    private void ClearMixKeys_Click(object sender, RoutedEventArgs e)
    {
        Filters.StudioVoiceMixKeys = null;
        UpdateMixKeys();
    }

    private void UpdateMixKeys()
    {
        var count = Filters.StudioVoiceMixKeys?.Count ?? 0;
        AddMixKeyButton.IsEnabled = _keyTime is not null;
        ClearMixKeysButton.IsEnabled = count > 0;
        MixKeysText.Text = count == 0
            ? "No mix keys: the mix above holds all along."
            : string.Create(CultureInfo.InvariantCulture, $"{count} mix key{(count == 1 ? "" : "s")}: the mix follows {(count == 1 ? "it" : "them")} along the clip.");
    }
}
