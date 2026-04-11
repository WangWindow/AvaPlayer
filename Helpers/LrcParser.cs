using System.Text.RegularExpressions;
using AvaPlayer.Models;

namespace AvaPlayer.Helpers;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(?<min>\d{1,2}):(?<sec>\d{1,2})(?:[.:](?<frac>\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var lines = new List<LyricLine>();

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var lyricText = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(lyricText))
            {
                continue;
            }

            foreach (Match match in matches)
            {
                lines.Add(new LyricLine
                {
                    Time = ParseTimestamp(match),
                    Text = lyricText
                });
            }
        }

        return lines
            .OrderBy(static line => line.Time)
            .ToArray();
    }

    private static TimeSpan ParseTimestamp(Match match)
    {
        var minutes = int.Parse(match.Groups["min"].Value);
        var seconds = int.Parse(match.Groups["sec"].Value);
        var fractionText = match.Groups["frac"].Value;

        var milliseconds = fractionText.Length switch
        {
            1 => int.Parse(fractionText) * 100,
            2 => int.Parse(fractionText) * 10,
            3 => int.Parse(fractionText),
            _ => 0
        };

        return new TimeSpan(0, 0, minutes, seconds, milliseconds);
    }
}
