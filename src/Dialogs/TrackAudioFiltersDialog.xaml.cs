using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Audio filter editor: compressor, gate, de-noise, Studio Voice, De-Esser, tone, and under "Show Advanced Filters" limiter, 10-band EQ
/// (parametric curve over the sliders), pitch/tempo and stereo width. Edits a copy the caller takes on OK.
/// </summary>
public partial class TrackAudioFiltersDialog : Window
{
    // Add Mix Key time in seconds from the owner's start (clip start); null when no playhead applies.
    private readonly double? _keyTime;

    /// <param name="heading">Audio source label.</param>
    /// <param name="filters">Current filters; a copy is edited.</param>
    /// <param name="keyTime">Playhead in seconds from the owner's start (clip Start on the Sequence) for Studio Voice mix keys; null for none.</param>
    public TrackAudioFiltersDialog(string heading, TrackAudioFilters filters, double? keyTime = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);

        // Local Ctrl+Z/Ctrl+Y over whole filter snapshots, one per press; Reset and mix keys (unbound list) are steps like any slider.
        History = new SnapshotUndo<TrackAudioFilters>(this, () => Filters!.Clone(), ShowFilters, state => JsonSerializer.Serialize(state));
        TrackText.Text = heading;
        _keyTime = keyTime is { } at && double.IsFinite(at) ? Math.Max(at, 0) : null;
        ShowFilters(filters);

        // Redraw the EQ curve on the next render after any source value changes (sliders, high-pass, shelves, switches, undo), once
        // bindings have propagated.
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => EqCurve.Refresh()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => EqCurve.Refresh()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => EqCurve.Refresh()));
        EqCurve.BandChanged += CurveBandChanged;
    }

    /// <summary>Edited filters; valid only after OK.</summary>
    public TrackAudioFilters Filters { get; private set; } = null!;

    /// <summary>Dialog-local undo over the filters.</summary>
    internal SnapshotUndo<TrackAudioFilters> History { get; }

    // Binds a filter copy: rebinds all controls and the curve, and refreshes the mix key line.
    private void ShowFilters(TrackAudioFilters filters)
    {
        DataContext = Filters = filters.Clone();
        EqCurve.Filters = Filters;
        UpdateMixKeys();
        EqCurve.Refresh();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetFilters();

    /// <summary>Resets to defaults, preserving advanced-section visibility.</summary>
    internal void ResetFilters() => ShowFilters(new TrackAudioFilters { ShowAdvanced = Filters.ShowAdvanced });

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    // Curve band drag: syncs its slider gain (binding round-trips the same value) and enables the EQ so the change is audible.
    private void CurveBandChanged(int band)
    {
        if (FindName($"EqSlider{band.ToString(CultureInfo.InvariantCulture)}") is Slider slider && band < Filters.EqualizerGains.Length)
            slider.Value = Filters.EqualizerGains[band];
        if (!Filters.Equalizer)
            EqualizerBox.IsChecked = true;
    }

    // ----- Studio Voice mix keys -----

    private void AddMixKey_Click(object sender, RoutedEventArgs e) => AddMixKey();

    /// <summary>Adds a mix key at the playhead with the current mix (one key per time).</summary>
    internal void AddMixKey()
    {
        if (_keyTime is not { } at)
            return;

        // One key per time: an existing key there is updated to the current mix.
        Filters.PutMixKey(at);
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
