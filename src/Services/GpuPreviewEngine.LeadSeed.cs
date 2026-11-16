namespace HandPegApp.Services;

// ----- Display lead seeding for first play -----
// A new swap chain has no measured lead until five playing frames are displayed, so the first play assumed one refresh while high-refresh
// monitors may show frames several ms later (late frames after a cold start). If the Playback & A/V Sync Test measured this monitor's
// lead (AppSettings.LastPlaybackSyncTest) at the same refresh, the first play is seeded with it.
public sealed partial class GpuPreviewEngine
{
    // Only the main player engine is seeded (not the sync test, strip or export engines).
    private readonly bool _player;
    private bool _leadSeedTried;

    /// <summary>Seeds the viewport lead once per engine at first play (pump thread, refresh period known).</summary>
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
