namespace HandPegApp.Services;

// ----- The v5.0 sound engine (Pass 5.0-AudioAnd3D, 5.0C): Settings > Timeline & Player > Audio playback -----
//
// Read by WasapiAudioEngine.Create and ApplySettings (Services/Audio). Saved settings need no compatibility in v5.0 (the
// user's call): these are new, and an old file simply has none of them.

public sealed partial class AppSettings
{
    /// <summary>The sound output the editor plays through: a device id as Windows gives it (AudioDevices.List); "" follows the system's default.</summary>
    public string AudioOutputDeviceId { get; set; } = "";

    /// <summary>
    /// How much sound is kept queued for the device, in milliseconds (3 to 40; 10 by default): less answers seeks and edits
    /// sooner, more rides out a busy processor. Never less than one and a half of the device's periods.
    /// </summary>
    public int AudioBufferMs { get; set; } = 10;

    /// <summary>
    /// The sound's offset against the picture, in milliseconds (-250 to 250; 0 by default): positive plays the sound later.
    /// For a speaker or headset whose own delay Windows does not report (Bluetooth), measured by eye and ear.
    /// </summary>
    public double AudioSyncOffsetMs { get; set; }

    // ----- Audio beats (Pass 5.2B): Detect & Snap to Audio Beats (BeatDetector, TimelineClip.BeatTimes) -----

    /// <summary>A clip's detected beats are among the times the Timeline snaps to (on by default; a file without the setting reads it on).</summary>
    public bool SnapToBeats { get; set; } = true;

    /// <summary>A clip with detected beats shows them as faint ticks on its block (on by default).</summary>
    public bool ShowBeatGrid { get; set; } = true;

    /// <summary>The Master Meter pane is open (5.2B: the meter and the master fader upright, a pane of its own; off to start with).</summary>
    public bool ShowMasterMeter { get; set; }
}
