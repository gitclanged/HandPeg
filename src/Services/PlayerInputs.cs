namespace HandPegApp.Services;

/// <summary>
/// What the window tells the player engine, as one value: the sound's volume, mute and rate, the player's zoom and pan, and the grade's
/// Before/After. An engine that starts (the first, Reload Player, a restart after a fault) is given all of it at once
/// (<see cref="GpuPreviewEngine.Apply"/>), so no input is left at the engine's default; live changes still go to the matching setter.
/// </summary>
/// <param name="Volume">Master volume, 0 to 4 (1 = as recorded).</param>
/// <param name="Muted">The player's sound muted.</param>
/// <param name="Rate">J/K/L rate (0.25 to 4, negative backward).</param>
/// <param name="ZoomLog2">The player's zoom as a power of two (0 = fit).</param>
/// <param name="PanX">Horizontal pan, as a fraction of the picture.</param>
/// <param name="PanY">Vertical pan, as a fraction of the picture.</param>
/// <param name="GradeCompare">The grade's Before/After mode.</param>
/// <param name="GradeWipe">Where the Before/After split stands across the picture, 0 to 1.</param>
public readonly record struct PlayerInputs(float Volume, bool Muted, double Rate, double ZoomLog2, double PanX, double PanY, GradeCompareMode GradeCompare, double GradeWipe);
