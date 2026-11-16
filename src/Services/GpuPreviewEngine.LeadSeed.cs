namespace HandPegApp.Services;

// ----- The saved display lead seeding a player's first play (5.6.0) -----
// A player engine's swap chain knows no lead until five of its playing frames were seen on the screen: its first play drew those for one
// refresh where the 500 Hz Desktop shows them ~6 ms after their turn (frames 5-6 ms late after a cold play's stall). The Playback & A/V Sync
// Test measured this monitor's lead (AppSettings.LastPlaybackSyncTest): a player's first play starts from it when the refresh matches.
public sealed partial class GpuPreviewEngine
{
    // The player's engine (not the Playback & A/V Sync Test's own, nor a strip's or an export's): only it is seeded.
    private readonly bool _player;
    private bool _leadSeedTried;

    /// <summary>Seeds the viewport's lead once per engine, at its first play (the pump's thread, the monitor's refresh known).</summary>
    private void SeedLeadOnce(double monitorPeriod)
    {
        if (!_player || _leadSeedTried)
            return;

        _leadSeedTried = true;
        var test = AppSettings.Current.LastPlaybackSyncTest;
        var seed = GpuViewport.SeedLeadFor(test?.DisplayLeadMs, test?.FrameStatistics == true, test?.RefreshHz ?? 0, monitorPeriod);
        if (double.IsFinite(seed))
        {
            _viewport.SeedLead(seed);
            AppLog.Write(FormattableString.Invariant($"GPU preview: the first play starts from the saved display lead ({seed * 1000:0.###} ms at {test!.RefreshHz:0.##} Hz)"));
        }
    }
}
