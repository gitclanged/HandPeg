using System.Windows.Threading;

namespace HandPegApp.Shell;

/// <summary>
/// MainWindow's playhead-scrub UI state (MainWindow.Scrub.cs), kept off the shell class (shell-budget golden). UI thread only.
/// </summary>
internal sealed class ScrubState
{
    /// <summary>OnScrubFrame is hooked on CompositionTarget.Rendering.</summary>
    public bool FrameHooked;

    /// <summary>FlushScrub is committing PositionMs (the player already seeked there).</summary>
    public bool Flushing;

    /// <summary>A Player/Canvas box rebuild was skipped during a held scrub (MainWindow.PlayerLayout.cs); run once on release.</summary>
    public bool RedrawOwed;

    /// <summary>Pointer rest during a held play-scrub (Pass 5.6.4A): plays a segment from the scrubbed moment until the next move.</summary>
    public DispatcherTimer? GhostDwell;
}
