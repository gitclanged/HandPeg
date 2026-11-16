namespace HandPegApp.Services;

// ----- Audio engine (Settings > Timeline & Player > Audio playback) -----
//
// Read by WasapiAudioEngine.Create and ApplySettings (Services/Audio); absent in older files, which use the defaults.

public sealed partial class AppSettings
{
    /// <summary>Output device id (AudioDevices.List); "" follows the system default.</summary>
    public string AudioOutputDeviceId { get; set; } = "";

    /// <summary>
    /// Device buffer in ms (3-40, default 10): lower responds faster, higher tolerates CPU load. Floor of 1.5 device periods.
    /// </summary>
    public int AudioBufferMs { get; set; } = 10;

    /// <summary>
    /// Audio offset in ms (-250 to 250, default 0; positive delays audio), for outputs with unreported latency (e.g. Bluetooth).
    /// </summary>
    public double AudioSyncOffsetMs { get; set; }

    /// <summary>
    /// Last Playback &amp; A/V Sync Test result (PlaybackSyncTest); null if never run. Informational only; playback never reads it.
    /// </summary>
    public PlaybackSyncResult? LastPlaybackSyncTest { get; set; }

    // ----- Audio beats: Detect & Snap to Audio Beats (BeatDetector, TimelineClip.BeatTimes) -----

    /// <summary>Include detected beats as Timeline snap targets (default on, including when absent).</summary>
    public bool SnapToBeats { get; set; } = true;

    /// <summary>A clip with detected beats shows them as faint ticks on its block (on by default).</summary>
    public bool ShowBeatGrid { get; set; } = true;

    /// <summary>Master Meter pane visibility (vertical meter and master fader; default off).</summary>
    public bool ShowMasterMeter { get; set; }
}
