namespace HandPegApp.Models;

/// <summary>
/// HH:MM:SS:FF timecodes, where FF counts whole frames into the second at the source's frame rate.
/// </summary>
public static class Timecode
{
    /// <summary>Highest value FF can take: frames 0 to this fit in one second.</summary>
    public static int MaxFrame(double frameRate) => Math.Max((int)Math.Ceiling(frameRate) - 1, 0);

    /// <summary>
    /// TotalSeconds = (HH * 3600) + (MM * 60) + SS + (FF / frame rate).
    /// </summary>
    public static bool TryParse(string text, double frameRate, out double seconds, out string error)
    {
        seconds = 0;
        error = "";

        var parts = text.Trim().Split(':');
        var values = new int[4];
        if (parts.Length != 4 || !parts.Select((p, i) => int.TryParse(p, out values[i]) && values[i] >= 0).All(ok => ok))
        {
            error = "Use the form HH:MM:SS:FF, for example 00:01:30:12.";
            return false;
        }

        var (hours, minutes, wholeSeconds, frames) = (values[0], values[1], values[2], values[3]);
        if (minutes > 59 || wholeSeconds > 59)
        {
            error = "Minutes and seconds go up to 59.";
            return false;
        }

        if (frames > MaxFrame(frameRate))
        {
            error = $"The frame number goes up to {MaxFrame(frameRate)} at this frame rate.";
            return false;
        }

        seconds = hours * 3600 + minutes * 60 + wholeSeconds + frames / frameRate;
        return true;
    }

    public static string Format(double seconds, double frameRate)
    {
        seconds = Math.Max(seconds, 0);
        var whole = (long)Math.Floor(seconds);

        // Rounded to the nearest frame: timestamps are rarely exact multiples of the frame duration.
        var frames = (int)Math.Round((seconds - whole) * frameRate);
        if (frames > MaxFrame(frameRate))
        {
            frames = 0;
            whole++;
        }

        return $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}:{frames:00}";
    }
}
