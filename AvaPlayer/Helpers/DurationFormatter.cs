namespace AvaPlayer.Helpers;

/// <summary>
/// Formats time durations (in seconds) into a human-readable string
/// in the form M:SS or H:MM:SS.
/// </summary>
public static class DurationFormatter
{
    /// <summary>
    /// Formats a duration in seconds to a display string.
    /// Returns "0:00" for NaN, Infinity, or negative values.
    /// </summary>
    /// <param name="seconds">The duration in seconds.</param>
    /// <returns>Formatted duration string (e.g., "3:45" or "1:02:30").</returns>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "0:00";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.Hours > 0
            ? $"{duration.Hours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }
}
