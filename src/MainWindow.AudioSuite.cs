using System.Windows;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- Studio Audio Suite window wiring -----
// Master meters (Audio tab horizontal, Master Meter pane vertical) read the audio engine through one AudioMeterFeed that runs only during
// playback; their faders drive the Sequence master gain, one Undo step per gesture. Detected clip beats render as ticks along the block
// (beat grid) and serve as Timeline snap targets (SnapTimes).
public partial class MainWindow
{
    private AudioMeterFeed? _meterFeed;

    // Single engine meter reader, created when the first meter is wired; its timer runs only while playing with a meter visible.
    private AudioMeterFeed MeterFeed => _meterFeed ??= new AudioMeterFeed(() => _gpu?.Audio.Meter) { Playing = _viewModel.IsPlaying };

    private void InitializeAudioSuite()
    {
        WireMeter(MasterMeterPaneMeter);
        EffectsView.AudioFiltersRequested += layer => OpenAudioFilters(layer);
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsPlaying) when _meterFeed is { } feed:
                    feed.Playing = _viewModel.IsPlaying;
                    break;
                case nameof(MainViewModel.ShowBeatGrid):
                    ScheduleClipRedraw();
                    break;
            }
        };
    }

    /// <summary>Wires a meter (pane, or Audio tab on realization) to the shared feed; each fader gesture is one Undo step.</summary>
    private void WireMeter(AudioMeterControl meter)
    {
        meter.Feed = MeterFeed;
        meter.GainChangeStarted += _viewModel.BeginMasterGainChange;
        meter.GainChangeEnded += _viewModel.EndMasterGainChange;
    }

    /// <summary>
    /// Beat-grid tick offsets in pixels from the block start (<paramref name="blockStart"/>, Sequence seconds), in order; null when the clip
    /// has no beats or the grid is off.
    /// </summary>
    private List<double>? BeatTicks(TimelineClip? model, double blockStart, double scale)
    {
        if (model?.BeatTimes is not { Length: > 0 } beats || !_viewModel.ShowBeatGrid || scale <= 0)
            return null;

        var ticks = new List<double>(beats.Length);
        MainViewModel.BeatsOnSequence(model, ticks);
        for (var i = 0; i < ticks.Count; i++)
            ticks[i] = (ticks[i] - blockStart) * scale;
        return ticks;
    }

    /// <summary>
    /// Gain line double-click (and the clip's Auto-Level Speech): measures in the background and sets the clip gain for -14 LUFS (one undo
    /// step), then redraws the rows with the new gain line.
    /// </summary>
    internal async Task AutoLevelSpeechAsync(Layer clip)
    {
        try
        {
            if (await _viewModel.AutoLevelSpeechAsync(clip))
                ScheduleClipRedraw();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.AppLog.Write("Auto-Level Speech", ex);
            _viewModel.StatusText = $"The speech could not be levelled: {ex.Message}";
        }
    }

    /// <summary>Clip menu "Detect &amp; Snap to Audio Beats": detects in the background, then redraws rows with the beats.</summary>
    private async Task DetectBeatsAsync(Layer clip)
    {
        try
        {
            if (await _viewModel.DetectClipBeatsAsync(clip))
                ScheduleClipRedraw();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.AppLog.Write("Detect & Snap to Audio Beats", ex);
            _viewModel.StatusText = $"The beats could not be found: {ex.Message}";
        }
    }
}
