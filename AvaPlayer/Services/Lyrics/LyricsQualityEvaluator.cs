using System.Text;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

internal static class LyricsQualityEvaluator
{
    private const double SuspiciousCoverageRatio = 0.72;
    private const double SuspiciousTrailingGapSeconds = 35;
    private const double HardTrailingGapSeconds = 55;
    private const double ExpectedSecondsPerLine = 7.5;

    public static LyricsQualityEvaluation Evaluate(Track track, IReadOnlyList<LyricLine>? lyrics)
    {
        if (lyrics is not { Count: > 0 })
        {
            return LyricsQualityEvaluation.Empty;
        }

        var uniqueLines = new HashSet<string>(StringComparer.Ordinal);
        var nonEmptyLineCount = 0;
        var lastTimestamp = TimeSpan.Zero;

        foreach (var line in lyrics)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            nonEmptyLineCount++;

            var normalizedLine = NormalizeLineText(line.Text);
            if (!string.IsNullOrEmpty(normalizedLine))
            {
                uniqueLines.Add(normalizedLine);
            }

            if (line.Time > lastTimestamp)
            {
                lastTimestamp = line.Time;
            }
        }

        if (nonEmptyLineCount == 0)
        {
            return LyricsQualityEvaluation.Empty;
        }

        var distinctLineCount = uniqueLines.Count;
        var score = nonEmptyLineCount * 2 + distinctLineCount * 3;
        var coverageRatio = 1d;
        var trailingGapSeconds = 0d;
        var expectedLineCount = 0;

        if (track.DurationSeconds > 1)
        {
            coverageRatio = Math.Clamp(lastTimestamp.TotalSeconds / track.DurationSeconds, 0d, 1.15d);
            trailingGapSeconds = Math.Max(0d, track.DurationSeconds - lastTimestamp.TotalSeconds);
            expectedLineCount = Math.Clamp((int)Math.Round(track.DurationSeconds / ExpectedSecondsPerLine), 8, 52);

            score += (int)Math.Round(Math.Min(coverageRatio, 1d) * 36);
            score += Math.Min(nonEmptyLineCount, Math.Max(4, expectedLineCount / 2));
            score += trailingGapSeconds switch
            {
                <= 12 => 10,
                <= 25 => 5,
                >= HardTrailingGapSeconds => -18,
                >= SuspiciousTrailingGapSeconds => -8,
                _ => 0
            };

            if (nonEmptyLineCount >= expectedLineCount)
            {
                score += 10;
            }
            else if (nonEmptyLineCount < expectedLineCount * 0.55d)
            {
                score -= 10;
            }
        }
        else
        {
            score += Math.Min(nonEmptyLineCount, 24);
        }

        var isAccepted = nonEmptyLineCount >= 3;

        // Reject candidates that stop far too early; these are usually truncated sync data or the wrong recording.
        if (track.DurationSeconds >= 90 &&
            trailingGapSeconds > SuspiciousTrailingGapSeconds &&
            coverageRatio < SuspiciousCoverageRatio)
        {
            isAccepted = false;
        }

        if (track.DurationSeconds >= 120 &&
            trailingGapSeconds > HardTrailingGapSeconds &&
            coverageRatio < 0.70)
        {
            isAccepted = false;
        }

        if (expectedLineCount > 0 &&
            nonEmptyLineCount < expectedLineCount * 0.45d &&
            coverageRatio < 0.82)
        {
            isAccepted = false;
        }

        return new LyricsQualityEvaluation(
            isAccepted,
            score,
            nonEmptyLineCount,
            distinctLineCount,
            coverageRatio,
            trailingGapSeconds);
    }

    private static string NormalizeLineText(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}

internal readonly record struct LyricsQualityEvaluation(
    bool IsAccepted,
    int Score,
    int NonEmptyLineCount,
    int DistinctLineCount,
    double CoverageRatio,
    double TrailingGapSeconds)
{
    public static LyricsQualityEvaluation Empty { get; } = new(false, int.MinValue, 0, 0, 0, 0);
}
