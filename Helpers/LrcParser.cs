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
        var continuationIndexes = new List<int>();

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var lineText = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(lineText))
            {
                continuationIndexes.Clear();
                continue;
            }

            var matches = TimestampRegex().Matches(lineText);
            if (matches.Count == 0)
            {
                if (continuationIndexes.Count > 0 && ShouldAppendContinuation(lineText))
                {
                    AppendContinuation(lines, continuationIndexes, lineText);
                    continue;
                }

                continuationIndexes.Clear();
                continue;
            }

            var lyricText = TimestampRegex().Replace(lineText, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(lyricText))
            {
                continuationIndexes.Clear();
                continue;
            }

            continuationIndexes.Clear();
            foreach (Match match in matches)
            {
                continuationIndexes.Add(lines.Count);
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

    private static bool ShouldAppendContinuation(string lineText)
    {
        if (lineText[0] == '[' && lineText[^1] == ']')
        {
            return false;
        }

        var colonIndex = lineText.IndexOf(':');
        if (colonIndex < 0)
        {
            colonIndex = lineText.IndexOf('：');
        }

        return colonIndex is < 0 or > 4;
    }

    private static void AppendContinuation(List<LyricLine> lines, List<int> continuationIndexes, string continuationText)
    {
        for (var i = 0; i < continuationIndexes.Count; i++)
        {
            var index = continuationIndexes[i];
            var previous = lines[index];
            lines[index] = new LyricLine
            {
                Time = previous.Time,
                Text = $"{previous.Text}{Environment.NewLine}{continuationText}"
            };
        }
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
