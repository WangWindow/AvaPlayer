using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics
{
    internal static class LrcLibProviderHelpers
    {

        internal static LyricLine[] BuildEstimatedLyrics(string text, double durationSeconds)
        {
            var lines = text
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length == 0)
            {
                return [];
            }

            var totalDuration = Math.Max(durationSeconds, lines.Length * 4d);
            var step = totalDuration / Math.Max(1, lines.Length);

            return lines
                .Select((line, index) => new LyricLine
                {
                    IsTimed = false,
                    Time = TimeSpan.FromSeconds(index * step),
                    Text = line
                })
                .ToArray();
        }
    }
}
