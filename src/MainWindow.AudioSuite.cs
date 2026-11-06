using System.Windows;
using HandPegApp.Models;
using HandPegApp.Models.Timeline;
using HandPegApp.ViewModels;

namespace HandPegApp;

// ----- The Studio Audio Suite in the window (Pass 5.2B) -----
// The master meters (the Audio tab's, across; the Master Meter pane's, upright) read the sound engine through one AudioMeterFeed, which
// runs only while the player plays; their faders are the Sequence's master gain, a gesture of either one step of Undo. The beats found in
// a clip's sound are ticks along its block (the beat grid) and times the timeline snaps to (SnapTimes).
public partial class MainWindow
{
    private AudioMeterFeed? _meterFeed;

    // The one reader of the engine's meter, made as the first meter is wired: a timer that does not run until the player plays with a meter shown.
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

    /// <summary>A meter (the pane's, or the Audio tab's as the tab is made): fed by the one feed, its fader's gestures one step of Undo each.</summary>
    private void WireMeter(AudioMeterControl meter)
    {
        meter.Feed = MeterFeed;
        meter.GainChangeStarted += _viewModel.BeginMasterGainChange;
        meter.GainChangeEnded += _viewModel.EndMasterGainChange;
    }

    /// <summary>
    /// A clip's beats as ticks on its block (the beat grid): pixels from where the block is laid (<paramref name="blockStart"/>, seconds on the
    /// Sequence), in order; null for a clip with none, or with the grid off.
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

    /// <summary>Detect &amp; Snap to Audio Beats on a clip's menu: found in the background, then the rows drawn again with its beats.</summary>
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
