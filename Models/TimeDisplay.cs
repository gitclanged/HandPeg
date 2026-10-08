using System.Globalization;

namespace HandPegApp.Models;

/// <summary>
/// The one place that decides how a time is written and read: with a frame number (HH:MM:SS:FF)
/// or with milliseconds (HH:MM:SS.mmm). The timeline, the cut list and the manual cut dialog all use it.
/// </summary>
public static class TimeDisplay
{
    /// <summary>Set from the settings.</summary>
    public static bool UseFrames { get; set; }

    /// <summary>Frame rate of the loaded source; frame numbers are meaningless without it.</summary>
    public static double FrameRate { get; set; } = 30;

    public static string FormatHint => UseFrames ? "HH:MM:SS:FF" : "HH:MM:SS.mmm";

    public static string Format(TimeSpan time) => Format(time.TotalSeconds);

    public static string Format(double seconds) =>
        UseFrames
            ? Timecode.Format(seconds, FrameRate)
            : TimeSpan.FromSeconds(Math.Max(seconds, 0)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    /// <summary>Reads a time typed in the format currently in use.</summary>
    public static bool TryParse(string text, out double seconds, out string error)
    {
        if (UseFrames)
            return Timecode.TryParse(text, FrameRate, out seconds, out error);

        seconds = 0;
        error = "";

        // HH:MM:SS with optional decimals. TimeSpan's own parser would read "1:30" as hours and minutes.
        var parts = text.Trim().Split(':');
        if (parts.Length == 3
            && int.TryParse(parts[0], out var hours) && hours >= 0
            && int.TryParse(parts[1], out var minutes) && minutes is >= 0 and <= 59
            && double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secondsPart)
            && secondsPart is >= 0 and < 60)
        {
            seconds = hours * 3600 + minutes * 60 + secondsPart;
            return true;
        }

        error = "Use the form HH:MM:SS.mmm, for example 00:01:30.500.";
        return false;
    }
}
